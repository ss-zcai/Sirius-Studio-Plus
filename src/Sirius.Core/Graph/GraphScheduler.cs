using System.Diagnostics;

namespace Sirius.Core.Graph;

/// <summary>Outcome of one evaluation pass.</summary>
public sealed class GraphRunResult
{
    public GraphRunResult(int evaluated, int skipped, int failed, TimeSpan duration, bool cancelled, string? error)
    {
        Evaluated = evaluated;
        Skipped = skipped;
        Failed = failed;
        Duration = duration;
        Cancelled = cancelled;
        Error = error;
    }

    /// <summary>Nodes that actually ran.</summary>
    public int Evaluated { get; }

    /// <summary>Nodes served from cache.</summary>
    public int Skipped { get; }

    public int Failed { get; }

    public TimeSpan Duration { get; }

    public bool Cancelled { get; }

    /// <summary>Set when the pass could not start at all (e.g. a cycle).</summary>
    public string? Error { get; }

    public bool Success => Error is null && Failed == 0 && !Cancelled;
}

/// <summary>
/// Evaluates a <see cref="VisionGraph"/>, caching each output port's value and
/// re-running only what a change actually affected.
/// </summary>
/// <remarks>
/// <para>
/// Three problems in the old <c>MainForm.Process()</c> drove this design:
/// it re-ran every tool in the rack on every parameter keystroke; it ran on the
/// UI thread, so a large image froze the window; and it never disposed a single
/// <c>Mat</c>, leaking native memory on every pass.
/// </para>
/// <para>
/// Here, a parameter change marks only that node and its downstream dirty;
/// evaluation happens off the UI thread and is cancellable, so dragging a
/// slider abandons the in-flight pass instead of queueing dozens; and the cache
/// disposes values it supersedes, following the ownership rule in
/// <see cref="PortValue"/>.
/// </para>
/// <para>
/// Not thread-safe: call <see cref="RunAsync"/> from one thread (the UI thread)
/// and await it. Concurrent passes over one graph are not supported.
/// </para>
/// </remarks>
public sealed class GraphScheduler : IDisposable
{
    private readonly VisionGraph _graph;
    private readonly Dictionary<OutputPort, PortValue> _cache = new();
    private readonly HashSet<VisionNode> _dirty = new();
    private readonly SynchronizationContext? _uiContext;
    private bool _disposed;
    private int _running;

    /// <summary>
    /// The sub-graphs in this graph, as of the last structural change.
    /// </summary>
    /// <remarks>
    /// An array swapped whole rather than the node collection walked, because
    /// it is read off the UI thread - by panels preparing their pictures -
    /// while the editor may be adding a node to that collection.
    /// </remarks>
    private ISubGraphHost[] _hosts = [];

    /// <summary>The order a nested pass runs in, worked out on the UI thread by <see cref="Plan"/>.</summary>
    private VisionNode[]? _plan;
    private string? _planError;

    public GraphScheduler(VisionGraph graph)
        : this(graph, SynchronizationContext.Current)
    {
    }

    /// <param name="uiContext">
    /// Where node status is reported. A sub-graph's scheduler is given its
    /// parent's, so its nodes report where the parent's do.
    /// </param>
    internal GraphScheduler(VisionGraph graph, SynchronizationContext? uiContext)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));

        // Captured so node Status/StatusMessage updates are raised on the thread
        // that created the scheduler, keeping Core free of any WPF reference.
        _uiContext = uiContext;

        _graph.NodeInvalidated += OnNodeInvalidated;
        _graph.StructureChanged += OnStructureChanged;

        foreach (var node in _graph.Nodes)
            _dirty.Add(node);

        _hosts = _graph.Nodes.OfType<ISubGraphHost>().ToArray();
    }

    /// <summary>Nodes awaiting re-evaluation.</summary>
    public int DirtyCount => _dirty.Count;

    /// <summary>
    /// Reads a node's last computed output. The value is owned by the cache -
    /// consumers must not dispose it, and must not retain it across a
    /// subsequent <see cref="RunAsync"/>.
    /// </summary>
    /// <remarks>
    /// A port inside a sub-graph is answered too, from that sub-graph's own
    /// cache, so a viewer, a preview or a readout works the same on a node
    /// inside one as on a node outside. A sub-graph that is bypassed
    /// answers nothing: what is cached inside it may be from before its
    /// inputs last changed.
    /// </remarks>
    public object? GetOutput(OutputPort port)
    {
        if (_cache.TryGetValue(port, out var value))
            return value.Value;

        return TryReadNested(port, out var nested) ? nested : null;
    }

    /// <summary>Looks for <paramref name="port"/> in the sub-graphs of this graph, at any depth.</summary>
    internal bool TryReadNested(OutputPort port, out object? value)
    {
        foreach (var host in _hosts)
        {
            if (host is VisionNode { Enabled: true } && host.TryReadNested(port, out value))
                return true;
        }

        value = null;
        return false;
    }

    public T? GetOutput<T>(OutputPort port) where T : class => GetOutput(port) as T;

    /// <summary>
    /// Reads the value arriving at an input, by following its connection back
    /// to the producing output.
    /// </summary>
    /// <remarks>
    /// Needed by terminal nodes - a viewer has no output of its own, so its
    /// panel has to read what was fed into it.
    /// </remarks>
    public object? GetInput(InputPort port) =>
        port.Connection is { } connection ? GetOutput(connection.Source) : null;

    public T? GetInput<T>(InputPort port) where T : class => GetInput(port) as T;

    /// <summary>Marks a node and everything downstream of it stale.</summary>
    public void Invalidate(VisionNode node)
    {
        foreach (var affected in _graph.GetSelfAndDownstream(node))
            _dirty.Add(affected);
    }

    /// <summary>Discards all cached results, disposing owned buffers.</summary>
    public void InvalidateAll()
    {
        ClearCache();
        foreach (var node in _graph.Nodes)
            _dirty.Add(node);
    }

    /// <summary>
    /// Evaluates every dirty node in dependency order on a background thread.
    /// </summary>
    public Task<GraphRunResult> RunAsync(CancellationToken cancellationToken = default) =>
        Start(inspection: false, cancellationToken);

    /// <summary>
    /// Runs as an inspection of a part: what <see cref="RunAsync"/> does,
    /// and every <see cref="IInspectionNode"/> evaluated - counted, logged -
    /// whether or not anything upstream of it changed.
    /// </summary>
    public Task<GraphRunResult> InspectAsync(CancellationToken cancellationToken = default) =>
        Start(inspection: true, cancellationToken);

    private Task<GraphRunResult> Start(bool inspection, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Two passes over one cache will free a buffer the other is still
        // using, which surfaces as an access violation inside OpenCV rather
        // than a managed exception. Cancelling a pass does not stop it
        // immediately - cancellation is only observed between nodes - so a
        // caller must await the previous pass before starting the next.
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            throw new InvalidOperationException(
                "A graph evaluation is already in progress. Cancel and await the previous " +
                "RunAsync before starting another; concurrent passes share one result cache.");
        }

        if (!_graph.TryTopologicalSort(out var ordered, out var sortError))
        {
            Interlocked.Exchange(ref _running, 0);
            return Task.FromResult(new GraphRunResult(0, 0, 0, TimeSpan.Zero, false, sortError));
        }

        // Snapshot the order so the background pass is unaffected by edits made
        // while it runs; any such edit invalidates and triggers another pass.
        var plan = ordered.ToArray();

        // The same for every graph nested in this one, while still on the
        // thread the editor changes them on.
        foreach (var host in plan.OfType<ISubGraphHost>())
            host.Plan(_uiContext);

        if (inspection)
            MarkInspectionNodes();

        return Task.Run(() => Run(plan, cancellationToken, inspection), cancellationToken)
            .ContinueWith(
                task =>
                {
                    Interlocked.Exchange(ref _running, 0);
                    return task.GetAwaiter().GetResult();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    /// <summary>
    /// Works out the order the next <see cref="RunSynchronously"/> will use,
    /// here and in every graph nested in this one.
    /// </summary>
    /// <remarks>
    /// Called by the parent scheduler on the UI thread just before its own
    /// pass, for the reason <see cref="RunAsync"/> snapshots its order there:
    /// sorting walks the node collection, which the editor changes on that
    /// thread.
    /// </remarks>
    internal void Plan()
    {
        if (_graph.TryTopologicalSort(out var ordered, out var error))
        {
            _plan = ordered.ToArray();
            _planError = null;
        }
        else
        {
            _plan = null;
            _planError = error;
        }

        foreach (var host in _plan?.OfType<ISubGraphHost>() ?? [])
            host.Plan(_uiContext);
    }

    /// <summary>
    /// Evaluates every dirty node on the calling thread.
    /// </summary>
    /// <remarks>
    /// For a sub-graph, which is evaluated as one node of its parent - on the
    /// parent's worker thread, inside the parent's pass. A second thread
    /// would add nothing but a wait.
    /// </remarks>
    internal GraphRunResult RunSynchronously(CancellationToken cancellationToken, bool inspection = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.Exchange(ref _running, 1) == 1)
            throw new InvalidOperationException("This sub-graph is already being evaluated.");

        try
        {
            if (_plan is null && _planError is null)
                Plan();

            if (inspection)
                MarkInspectionNodes();

            return _planError is not null
                ? new GraphRunResult(0, 0, 0, TimeSpan.Zero, false, _planError)
                : Run(_plan!, cancellationToken, inspection);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    /// Every node that acts on an inspection made stale, with what depends
    /// on it - and every sub-graph holding one, so it runs its inside.
    /// </summary>
    private void MarkInspectionNodes()
    {
        foreach (var node in _graph.Nodes.Where(ActsOnInspection).ToArray())
            Invalidate(node);
    }

    private static bool ActsOnInspection(VisionNode node) =>
        node is IInspectionNode ||
        (node is ISubGraphHost host && host.Inner.Nodes.Any(ActsOnInspection));

    private GraphRunResult Run(IReadOnlyList<VisionNode> ordered, CancellationToken cancellationToken, bool inspection)
    {
        var stopwatch = Stopwatch.StartNew();
        var evaluated = 0;
        var skipped = 0;
        var failed = 0;
        var cancelled = false;

        foreach (var node in ordered)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            if (!_dirty.Contains(node))
            {
                skipped++;
                continue;
            }

            var status = EvaluateNode(node, cancellationToken, inspection);

            if (status == EvaluationOutcome.Cancelled)
            {
                cancelled = true;
                break;
            }

            // Cleared only on completion, so a cancelled pass leaves the
            // remaining nodes dirty for the next one.
            _dirty.Remove(node);
            evaluated++;

            if (status == EvaluationOutcome.Failed)
                failed++;
        }

        stopwatch.Stop();
        return new GraphRunResult(evaluated, skipped, failed, stopwatch.Elapsed, cancelled, null);
    }

    private enum EvaluationOutcome
    {
        Succeeded,
        Failed,
        Cancelled,
    }

    private EvaluationOutcome EvaluateNode(VisionNode node, CancellationToken cancellationToken, bool inspection)
    {
        var inputs = new Dictionary<InputPort, object?>(node.Inputs.Count);
        InputPort? missingRequired = null;

        foreach (var input in node.Inputs)
        {
            var source = input.Connection?.Source;
            var value = source is not null && _cache.TryGetValue(source, out var cached) ? cached.Value : null;

            if (value is null && input.IsRequired)
                missingRequired ??= input;

            inputs[input] = value;
        }

        if (missingRequired is not null)
        {
            // Produce nothing: downstream nodes then see a missing required
            // input too, so one unwired port surfaces at its actual origin
            // instead of as a confusing failure deep in the graph.
            EvictOutputs(node);
            SetStatus(node, NodeStatus.Error, DescribeMissingInput(missingRequired));
            return EvaluationOutcome.Failed;
        }

        if (!node.Enabled)
        {
            Bypass(node, inputs);
            SetStatus(node, NodeStatus.Idle, "Bypassed.");
            return EvaluationOutcome.Succeeded;
        }

        var context = new EvalContext(node, inputs, cancellationToken, inspection);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            node.Evaluate(context);
            stopwatch.Stop();
        }
        catch (OperationCanceledException)
        {
            DisposeUncommitted(context);
            return EvaluationOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            DisposeUncommitted(context);

            // Echo the input through so the rest of the graph still produces
            // something - the old tools' behaviour, but marked unowned so the
            // cache never frees a buffer belonging to an upstream node.
            Bypass(node, inputs);
            SetEvaluationTime(node, stopwatch.Elapsed);
            SetStatus(node, NodeStatus.Error, ex.Message);
            return EvaluationOutcome.Failed;
        }

        Commit(node, context);
        SetEvaluationTime(node, stopwatch.Elapsed);

        SetStatus(
            node,
            context.Warning is null ? NodeStatus.Ready : NodeStatus.Warning,
            context.Warning);

        return EvaluationOutcome.Succeeded;
    }

    /// <summary>
    /// Explains why a required input had no value.
    /// </summary>
    /// <remarks>
    /// A connected port whose producer failed is a different problem from an
    /// unwired one, and saying "not connected" for both sends the operator
    /// looking at the wrong node. The upstream node is named so the real
    /// origin of the failure is one glance away.
    /// </remarks>
    private static string DescribeMissingInput(InputPort input)
    {
        if (input.Connection is not { } connection)
            return $"Input '{input.DisplayName}' is not connected.";

        var producer = connection.Source.Node;
        return $"Input '{input.DisplayName}' has no data: '{producer.Name}' produced no result.";
    }

    /// <summary>Forwards the primary input to the primary output, unowned.</summary>
    private void Bypass(VisionNode node, Dictionary<InputPort, object?> inputs)
    {
        EvictOutputs(node);

        if (node.PrimaryInput is not { } input || node.PrimaryOutput is not { } output)
            return;

        if (inputs.TryGetValue(input, out var value) && value is not null)
            _cache[output] = new PortValue(value, isOwned: false);
    }

    private void Commit(VisionNode node, EvalContext context)
    {
        foreach (var output in node.Outputs)
        {
            var hasNew = context.Outputs.TryGetValue(output, out var fresh);

            if (_cache.TryGetValue(output, out var previous))
            {
                // Guard against a node that returned the same instance again -
                // disposing here would free the value we are about to store.
                var sameInstance = hasNew && ReferenceEquals(previous.Value, fresh.Value);
                if (!sameInstance)
                    previous.DisposeIfOwned();
            }

            if (hasNew)
                _cache[output] = fresh;
            else
                _cache.Remove(output);
        }
    }

    /// <summary>
    /// Disposes values a failed or cancelled node had already published. They
    /// never reached the cache, so nothing else can be referencing them.
    /// </summary>
    private static void DisposeUncommitted(EvalContext context)
    {
        foreach (var value in context.Outputs.Values)
            value.DisposeIfOwned();
    }

    private void EvictOutputs(VisionNode node)
    {
        foreach (var output in node.Outputs)
        {
            if (_cache.Remove(output, out var value))
                value.DisposeIfOwned();
        }
    }

    private void ClearCache()
    {
        foreach (var value in _cache.Values)
            value.DisposeIfOwned();

        _cache.Clear();
    }

    private void SetStatus(VisionNode node, NodeStatus status, string? message) =>
        OnUiThread(() =>
        {
            node.Status = status;
            node.StatusMessage = message;
        });

    private void SetEvaluationTime(VisionNode node, TimeSpan elapsed) =>
        OnUiThread(() => node.LastEvaluationTime = elapsed);

    private void OnUiThread(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
            action();
        else
            _uiContext.Post(static state => ((Action)state!)(), action);
    }

    private void OnNodeInvalidated(object? sender, VisionNode node) => Invalidate(node);

    private void OnStructureChanged(object? sender, EventArgs e)
    {
        // A node added after construction has no cached results yet.
        foreach (var node in _graph.Nodes)
        {
            if (!node.Outputs.Any(_cache.ContainsKey))
                _dirty.Add(node);
        }

        _hosts = _graph.Nodes.OfType<ISubGraphHost>().ToArray();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _graph.NodeInvalidated -= OnNodeInvalidated;
        _graph.StructureChanged -= OnStructureChanged;
        ClearCache();
        _dirty.Clear();

        // A recipe being closed takes its sub-graphs' results with it.
        foreach (var host in _hosts)
            host.ReleaseResults();
    }
}
