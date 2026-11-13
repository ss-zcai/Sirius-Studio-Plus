using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Sirius.Core.Graph;
using Sirius.Core.Runtime;

namespace Sirius.Plc.Workflows;

public enum ControllerState
{
    Stopped,
    Running,
    Stopping,
}

public enum WorkflowLogLevel
{
    /// <summary>Each read, write and skipped node: shown when asked for.</summary>
    Trace,

    Info,
    Warning,
    Error,
}

/// <summary>A line of a controller's log.</summary>
public sealed record WorkflowLogEntry(DateTime Time, WorkflowLogLevel Level, string? Node, string Message)
{
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Time:HH:mm:ss.fff}  {Level,-7}  {(Node is null ? string.Empty : $"[{Node}] ")}{Message}");
}

/// <summary>How a pass of a workflow went.</summary>
/// <param name="Ran">The nodes that ran and finished, in the order they ran.</param>
/// <param name="Failed">The nodes that failed.</param>
public sealed record WorkflowPassResult(
    int Number,
    string Start,
    string What,
    IReadOnlyList<string> Ran,
    IReadOnlyList<string> Failed,
    TimeSpan Duration);

/// <summary>
/// Runs a controller: connects its PLC and keeps it connected, has its
/// event nodes watch, and runs a pass of the workflow each time one of
/// them raises one.
/// </summary>
/// <remarks>
/// <para>
/// <b>A pass</b> starts at the event node that raised it and runs what is
/// downstream of it, in wiring order, each node once:
/// </para>
/// <list type="bullet">
/// <item>a node with a trigger wired in runs when one of its triggers was
/// fired in this pass - so a branch not taken does not run;</item>
/// <item>a node with no trigger wired in - one that only gathers values -
/// runs when every node in this pass feeding it has run and finished;</item>
/// <item>a node fails by throwing or by taking longer than its timeout: it
/// gives nothing, its Failed trigger fires, and the log says why. What
/// needed a value from it does not run - the failure is said once, where
/// it happened.</item>
/// </list>
/// <para>
/// <b>One pass at a time</b>, in the order raised: a second edge while a
/// pass runs waits for it, so a sequence never meets itself halfway.
/// </para>
/// <para>
/// <b>Connection</b>: the PLC is connected when the controller starts and
/// again whenever it is lost, every <see cref="ReconnectInterval"/>, the
/// log saying so once rather than at every try.
/// </para>
/// <para>
/// Nodes run on a worker, never the screen's thread, and the events here
/// are raised there too.
/// </para>
/// </remarks>
public sealed class WorkflowRunner : IAsyncDisposable
{
    private const int LogKept = 1000;

    private readonly Dictionary<OutputPort, object?> _last = [];
    private readonly List<WorkflowLogEntry> _log = [];
    private readonly object _logGate = new();
    private readonly List<Task> _tasks = [];

    private IReadOnlyList<WorkflowNode> _order = [];
    private Channel<PassRequest>? _queue;
    private CancellationTokenSource? _stop;
    private ControllerState _state;
    private int _passes;
    private string? _lastEvent;
    private string? _lastError;

    public WorkflowRunner(VisionGraph workflow, IPlcDriver driver, IStationBus? stations = null, string name = "Controller")
    {
        Workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        Driver = driver ?? throw new ArgumentNullException(nameof(driver));
        Stations = stations;
        Name = name;
    }

    public string Name { get; }

    public VisionGraph Workflow { get; }

    public IPlcDriver Driver { get; }

    public IStationBus? Stations { get; }

    /// <summary>How long a step may take when its node says nothing.</summary>
    public TimeSpan DefaultStepTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a lost PLC is tried again.</summary>
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(2);

    public ControllerState State => _state;

    public bool IsConnected => Driver.IsConnected;

    /// <summary>Passes run since the controller started.</summary>
    public int Passes => Volatile.Read(ref _passes);

    /// <summary>What last started a pass: "Monitor Ready: rising edge on DB34.20.0".</summary>
    public string? LastEvent => Volatile.Read(ref _lastEvent);

    /// <summary>What last went wrong, or null.</summary>
    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>The latest lines of the log, oldest first.</summary>
    public IReadOnlyList<WorkflowLogEntry> Log
    {
        get
        {
            lock (_logGate)
                return _log.ToArray();
        }
    }

    /// <summary>A line written to the log.</summary>
    public event EventHandler<WorkflowLogEntry>? Logged;

    /// <summary>A pass has finished.</summary>
    public event EventHandler<WorkflowPassResult>? PassFinished;

    /// <summary>The controller started, stopped, connected or lost its connection.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>
    /// What is wrong with the workflow, in sentences: nodes that cannot run
    /// in one, settings the PLC or the project cannot have, nothing to
    /// start it. None stops it starting, except a node that is not a
    /// workflow's.
    /// </summary>
    public IReadOnlyList<string> Check()
    {
        var problems = new List<string>();
        var check = new WorkflowCheck(Driver, Stations?.Stations);

        foreach (var node in Workflow.Nodes)
        {
            if (node is not WorkflowNode step)
            {
                problems.Add($"'{node.Name}' is a recipe's node: a workflow cannot run it.");
                continue;
            }

            if (!step.Enabled)
                continue;

            foreach (var problem in step.Check(check))
                problems.Add($"{node.Name}: {problem}");
        }

        if (!Workflow.Nodes.OfType<WorkflowEventNode>().Any(node => node.Enabled))
            problems.Add("Nothing starts this workflow: it needs a Monitor or a Station Result.");

        return problems;
    }

    /// <summary>Starts connecting, watching and running.</summary>
    /// <exception cref="InvalidOperationException">It is running already, or the workflow holds a node it cannot run.</exception>
    public void Start()
    {
        if (_state != ControllerState.Stopped)
            throw new InvalidOperationException($"{Name} is running already.");

        if (Workflow.Nodes.FirstOrDefault(node => node is not WorkflowNode) is { } stranger)
            throw new InvalidOperationException($"'{stranger.Name}' is a recipe's node: a workflow cannot run it.");

        if (!Workflow.TryTopologicalSort(out var order, out var error))
            throw new InvalidOperationException(error);

        _order = order.Cast<WorkflowNode>().ToArray();

        foreach (var problem in Check())
            Write(WorkflowLogLevel.Warning, null, problem);

        var stop = new CancellationTokenSource();
        _stop = stop;
        _queue = Channel.CreateUnbounded<PassRequest>(new UnboundedChannelOptions { SingleReader = true });
        _passes = 0;
        _state = ControllerState.Running;

        var watching = Workflow.Nodes.OfType<WorkflowEventNode>().Where(node => node.Enabled).ToArray();

        Write(
            WorkflowLogLevel.Info,
            null,
            $"Started: {_order.Count} nodes, {watching.Length} watching, on {Driver.Description}.");

        _tasks.Add(Task.Run(() => KeepConnectedAsync(stop.Token)));
        _tasks.Add(Task.Run(() => RunPassesAsync(_queue.Reader, stop.Token)));

        // Begun here rather than on a worker, so each watcher has hold of
        // what it watches - a station's results - before Start returns, and
        // nothing that happens just after is missed. Their first awaits hand
        // them on to workers.
        foreach (var node in watching)
            _tasks.Add(WatchAsync(node, stop.Token));

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Stops: the pass in hand is cancelled, passes waiting are dropped, and
    /// the PLC disconnected.
    /// </summary>
    public async Task StopAsync()
    {
        if (_state != ControllerState.Running || _stop is not { } stop)
            return;

        _state = ControllerState.Stopping;
        StatusChanged?.Invoke(this, EventArgs.Empty);

        stop.Cancel();
        _queue?.Writer.TryComplete();

        try
        {
            await Task.WhenAll(_tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _tasks.Clear();

        // Anything still waiting is let go, so a watcher awaiting it is not left hanging.
        while (_queue?.Reader.TryRead(out var request) == true)
            request.Done.TrySetResult();

        await Driver.DisconnectAsync().ConfigureAwait(false);

        stop.Dispose();
        _stop = null;
        _state = ControllerState.Stopped;
        Write(WorkflowLogLevel.Info, null, "Stopped.");
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await Driver.DisposeAsync().ConfigureAwait(false);
    }

    // ---- passes -------------------------------------------------------------------------

    private sealed record PassRequest(WorkflowEventNode Start, Action<WorkflowStep> Outputs, string What, TaskCompletionSource Done);

    internal Task Enqueue(WorkflowEventNode start, Action<WorkflowStep> outputs, string what)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        var request = new PassRequest(start, outputs, what, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        if (_state != ControllerState.Running || _queue is not { } queue || !queue.Writer.TryWrite(request))
            return Task.CompletedTask;

        return request.Done.Task;
    }

    private async Task RunPassesAsync(ChannelReader<PassRequest> queue, CancellationToken stop)
    {
        try
        {
            await foreach (var request in queue.ReadAllAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    await RunPassAsync(request, stop).ConfigureAwait(false);
                }
                finally
                {
                    request.Done.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private async Task RunPassAsync(PassRequest request, CancellationToken stop)
    {
        var number = Interlocked.Increment(ref _passes);
        var clock = Stopwatch.StartNew();
        var pass = new WorkflowPass(number, request.Start, Workflow.GetSelfAndDownstream(request.Start));
        var ran = new List<string>();

        Volatile.Write(ref _lastEvent, $"{request.Start.Name}: {request.What}");
        Write(WorkflowLogLevel.Info, request.Start, $"{request.What} (pass {number})");

        foreach (var node in _order)
        {
            if (!pass.Reached.Contains(node) || !node.Enabled)
                continue;

            if (ReferenceEquals(node, request.Start))
            {
                var first = new WorkflowStep(this, pass, node, stop);

                try
                {
                    request.Outputs(first);
                    first.Commit();
                    pass.Ran.Add(node);
                    ran.Add(node.Name);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Fail(node, pass, ex.Message);
                }

                continue;
            }

            // Another event node downstream starts passes of its own; it is not run by this one.
            if (node is WorkflowEventNode || !ShouldRun(node, pass))
                continue;

            if (await StepAsync(node, pass, stop).ConfigureAwait(false))
                ran.Add(node.Name);
        }

        foreach (var (output, value) in pass.Values)
            _last[output] = value;

        var result = new WorkflowPassResult(
            number,
            request.Start.Name,
            request.What,
            ran,
            pass.Failed.Select(node => node.Name).ToArray(),
            clock.Elapsed);

        Write(
            pass.Failed.Count > 0 ? WorkflowLogLevel.Warning : WorkflowLogLevel.Info,
            request.Start,
            pass.Failed.Count > 0
                ? $"Pass {number} finished in {clock.ElapsedMilliseconds} ms, with {pass.Failed.Count} failed."
                : $"Pass {number} finished in {clock.ElapsedMilliseconds} ms.");

        PassFinished?.Invoke(this, result);
    }

    /// <summary>Whether a node downstream of the pass's start runs in it - see the remarks on the class.</summary>
    private static bool ShouldRun(WorkflowNode node, WorkflowPass pass)
    {
        var triggers = node.Inputs
            .Where(input => input.DataType == typeof(Signal) && input.Connection is not null)
            .ToArray();

        // What feeds it values in this pass; a trigger wired into a value counts as a trigger.
        var feeding = node.Inputs
            .Where(input => input.DataType != typeof(Signal))
            .Select(input => input.Connection?.Source)
            .Where(source => source is not null && source.DataType != typeof(Signal))
            .Select(source => source!.Node)
            .Where(pass.Reached.Contains)
            .Distinct()
            .ToArray();

        // A value that failed to come is that node's failure, said once
        // there: what needed it does not run on without it.
        if (feeding.Any(pass.Failed.Contains))
            return false;

        if (triggers.Length > 0)
            return triggers.Any(trigger => pass.Fired.Contains(trigger.Connection!.Source));

        return feeding.Length > 0 && feeding.All(pass.Ran.Contains);
    }

    private async Task<bool> StepAsync(WorkflowNode node, WorkflowPass pass, CancellationToken stop)
    {
        var timeout = node.StepTimeout ?? DefaultStepTimeout;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
        limit.CancelAfter(timeout);

        var step = new WorkflowStep(this, pass, node, limit.Token);

        try
        {
            await node.RunAsync(step).ConfigureAwait(false);
            step.Commit();
            pass.Ran.Add(node);
            return true;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Fail(node, pass, $"No answer within {timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(node, pass, ex.Message);
        }

        return false;
    }

    private void Fail(WorkflowNode node, WorkflowPass pass, string message)
    {
        pass.Failed.Add(node);
        Volatile.Write(ref _lastError, $"{node.Name}: {message}");
        Write(WorkflowLogLevel.Error, node, message);

        if (node.FailedOutput is { } failed)
            pass.Fired.Add(failed);
    }

    /// <summary>The last value an output gave in any pass.</summary>
    internal bool LastValue(OutputPort output, out object? value) => _last.TryGetValue(output, out value);

    // ---- watching and connecting --------------------------------------------------------------

    private async Task WatchAsync(WorkflowEventNode node, CancellationToken stop)
    {
        var watch = new WorkflowWatch(this, node);

        while (!stop.IsCancellationRequested)
        {
            try
            {
                await node.WatchAsync(watch, stop).ConfigureAwait(false);

                // Returned without being asked to: not a watcher's way, but not a reason to spin.
                await Task.Delay(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Volatile.Write(ref _lastError, $"{node.Name}: {ex.Message}");
                Write(WorkflowLogLevel.Error, node, $"Stopped watching: {ex.Message} Watching again in a moment.");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task KeepConnectedAsync(CancellationToken stop)
    {
        var wasConnected = false;
        string? lastFailure = null;

        while (!stop.IsCancellationRequested)
        {
            if (!Driver.IsConnected)
            {
                if (wasConnected)
                {
                    wasConnected = false;
                    Volatile.Write(ref _lastError, $"Lost the connection to {Driver.Description}.");
                    Write(WorkflowLogLevel.Warning, null, $"Lost the connection to {Driver.Description}.");
                    StatusChanged?.Invoke(this, EventArgs.Empty);
                }

                try
                {
                    await Driver.ConnectAsync(stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Said once, not at every try.
                    if (ex.Message != lastFailure)
                    {
                        lastFailure = ex.Message;
                        Volatile.Write(ref _lastError, $"Cannot connect to {Driver.Description}: {ex.Message}");
                        Write(WorkflowLogLevel.Warning, null, $"Cannot connect to {Driver.Description}: {ex.Message} Trying again.");
                    }
                }
            }

            if (Driver.IsConnected && !wasConnected)
            {
                wasConnected = true;
                lastFailure = null;
                Write(WorkflowLogLevel.Info, null, $"Connected to {Driver.Description}.");
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }

            try
            {
                await Task.Delay(Driver.IsConnected ? TimeSpan.FromMilliseconds(200) : ReconnectInterval, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // ---- the log ----------------------------------------------------------------------

    internal void Write(WorkflowLogLevel level, VisionNode? node, string message)
    {
        var entry = new WorkflowLogEntry(DateTime.Now, level, node?.Name, message);

        lock (_logGate)
        {
            _log.Add(entry);

            if (_log.Count > LogKept)
                _log.RemoveRange(0, _log.Count - LogKept);
        }

        Logged?.Invoke(this, entry);
    }
}
