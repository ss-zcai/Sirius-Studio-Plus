using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
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

/// <summary>Where a node of a pass is: started, finished or failed.</summary>
public enum WorkflowNodeState
{
    Running,
    Done,
    Failed,
}

/// <summary>
/// A node of a pass starting, finishing or failing - for an editor to light
/// the node as a Test Run goes through it.
/// </summary>
/// <param name="Pass">The pass's number: a new number is a new pass.</param>
/// <param name="NodeId">The node's ID, as its workflow file keeps it.</param>
/// <param name="Took">How long it ran, once done or failed.</param>
/// <param name="Message">Why it failed.</param>
public sealed record WorkflowNodeActivity(int Pass, string NodeId, string Node, WorkflowNodeState State, TimeSpan Took, string? Message = null);

/// <summary>How a pass of a workflow went.</summary>
/// <param name="Ran">The nodes that ran and finished, in the order they ran.</param>
/// <param name="Failed">The nodes that failed.</param>
/// <param name="Part">The part it judged; null for a pass that inspected nothing.</param>
public sealed record WorkflowPassResult(
    int Number,
    string Start,
    string What,
    IReadOnlyList<string> Ran,
    IReadOnlyList<string> Failed,
    TimeSpan Duration,
    PartRecord? Part = null);

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

    private int _busy;

    private readonly object _partGate = new();
    private PartCounts _partCounts = PartCounts.Zero;
    private ResultTally _partResults = ResultTally.Empty;
    private long _nextPart = 1;
    private PartRecord? _lastPart;
    private string? _countsPath;

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

    /// <summary>The parts judged, kept over a restart when <see cref="KeepCountsIn"/> was given a file.</summary>
    public PartCounts PartCounts
    {
        get
        {
            lock (_partGate)
                return _partCounts;
        }
    }

    /// <summary>How many parts had each result (F33), by name - kept and reset with <see cref="PartCounts"/>.</summary>
    public ResultTally PartResultCounts
    {
        get
        {
            lock (_partGate)
                return _partResults;
        }
    }

    /// <summary>The last part judged, or null.</summary>
    public PartRecord? LastPart
    {
        get
        {
            lock (_partGate)
                return _lastPart;
        }
    }

    /// <summary>
    /// Keeps the part counts, and the number the next part takes, in
    /// <paramref name="path"/> - carrying on from what it holds.
    /// </summary>
    public void KeepCountsIn(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var (counts, next) = PartCountsFile.Load(path);
        var results = PartCountsFile.LoadResults(path);

        lock (_partGate)
        {
            _countsPath = path;
            _partCounts = counts;
            _partResults = results;
            _nextPart = next;
        }
    }

    /// <summary>
    /// Starts the part counts again from zero. Part numbers carry on, so a
    /// number is never given to two parts.
    /// </summary>
    public void ResetPartCounts()
    {
        lock (_partGate)
        {
            _partCounts = PartCounts.Zero;
            _partResults = ResultTally.Empty;
        }

        SaveCounts();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Whether a part is in hand: from the start of a pass that can inspect
    /// to its end, its answer written.
    /// </summary>
    public bool IsBusy => Volatile.Read(ref _busy) > 0;

    /// <summary>The stations its workflow has inspect, by name.</summary>
    public IReadOnlyList<string> StationsInspected =>
        Workflow.Nodes.OfType<Nodes.InspectNode>()
            .Where(node => node.Enabled && node.Station.Length > 0)
            .Select(node => node.Station)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>A part has been judged: after its pass, and before <see cref="PassFinished"/>.</summary>
    public event EventHandler<PartRecord>? PartFinished;

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

    /// <summary>A node of a pass started, finished or failed - on the pass's thread.</summary>
    public event EventHandler<WorkflowNodeActivity>? NodeActivity;

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

        // A pass that can inspect holds a part from its start: Busy to the PLC.
        var holdsPart = pass.Reached.OfType<WorkflowNode>().Any(node => node.Inspects && node.Enabled) ||
                        request.Start.StartsPart;

        if (holdsPart)
            Interlocked.Increment(ref _busy);

        try
        {
            await RunReachedAsync(request, pass, ran, clock, number, stop).ConfigureAwait(false);
        }
        finally
        {
            if (holdsPart)
                Interlocked.Decrement(ref _busy);
        }
    }

    private async Task RunReachedAsync(PassRequest request, WorkflowPass pass, List<string> ran, Stopwatch clock, int number, CancellationToken stop)
    {
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
                    Report(pass, node, WorkflowNodeState.Done, TimeSpan.Zero);
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

        var part = FinishPart(pass, clock.Elapsed);

        var result = new WorkflowPassResult(
            number,
            request.Start.Name,
            request.What,
            ran,
            pass.Failed.Select(node => node.Name).ToArray(),
            clock.Elapsed,
            part);

        Write(
            pass.Failed.Count > 0 ? WorkflowLogLevel.Warning : WorkflowLogLevel.Info,
            request.Start,
            pass.Failed.Count > 0
                ? $"Pass {number} finished in {clock.ElapsedMilliseconds} ms, with {pass.Failed.Count} failed."
                : $"Pass {number} finished in {clock.ElapsedMilliseconds} ms.");

        if (part is not null)
            PartFinished?.Invoke(this, part);

        PassFinished?.Invoke(this, result);
    }

    // ---- parts ---------------------------------------------------------------------------

    /// <summary>
    /// Makes the pass a part, when it is not one yet: the next number, and
    /// its ID from <paramref name="given"/> - Part ID from the PLC - or else
    /// the number.
    /// </summary>
    /// <remarks>
    /// A part whose set is under way (F26) is carried on: by its ID, or -
    /// the PLC giving none - at <paramref name="station"/>, one set at a
    /// time there, unless the PLC says this is shot 1. Without an ID the
    /// shots are counted into sets in order, as Studio does at the desk:
    /// a trigger lost puts every set after it out, so a line says the ID.
    /// </remarks>
    internal void BeginPart(WorkflowPass pass, PartValues? given, string? station = null, object? shot = null)
    {
        if (pass.PartNumber is not null)
            return;

        var id = PartRecord.IdIn(given);

        lock (_partGate)
        {
            // A part whose earlier shots came in passes before this one (F26).
            var open = id is not null
                ? _openParts.GetValueOrDefault(id)
                : station is not null && !IsFirstShot(shot)
                    ? _openParts.Values.FirstOrDefault(part => part.Numbered && string.Equals(part.Station, station, StringComparison.OrdinalIgnoreCase))
                    : null;

            if (open is not null)
            {
                _openParts.Remove(open.Id);
                pass.PartNumber = open.Number;
                pass.PartId = open.Id;
                pass.Numbered = open.Numbered;
                pass.Shots.InsertRange(0, open.Shots);
                return;
            }

            pass.PartNumber = _nextPart++;
        }

        pass.Numbered = id is null;
        pass.PartId = id ?? pass.PartNumber.Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The PLC says this is a set's first picture.</summary>
    private static bool IsFirstShot(object? shot)
    {
        try
        {
            return shot is not null && Convert.ToInt64(PartValues.Plain(shot), CultureInfo.InvariantCulture) <= 1;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>A part with shots taken and its set's last still to come (F26); numbered when the PLC gave no ID.</summary>
    private sealed record OpenPart(long Number, string Id, IReadOnlyList<Nodes.InspectionResult> Shots, DateTime Opened, string Station, bool Numbered);

    private readonly Dictionary<string, OpenPart> _openParts = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a part's set may take between its shots before it is given
    /// up on - not inspected - when the next shot comes, or any pass
    /// finishes. The PLC's sequence times out sooner; this is the backstop.
    /// </summary>
    public TimeSpan SetTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Parts whose sets are under way, by ID - for a test.</summary>
    internal IReadOnlyList<string> OpenParts
    {
        get
        {
            lock (_partGate)
                return _openParts.Keys.ToArray();
        }
    }

    /// <summary>
    /// Sets given up on: at <paramref name="station"/>, any other part's -
    /// one set at a time there - and anywhere, any past the timeout.
    /// </summary>
    private List<PartRecord> GiveUpOpenParts(string? station, string? keep)
    {
        var given = new List<OpenPart>();
        var now = DateTime.Now;

        lock (_partGate)
        {
            foreach (var open in _openParts.Values.ToArray())
            {
                if (string.Equals(open.Id, keep, StringComparison.Ordinal))
                    continue;

                if ((station is not null && string.Equals(open.Station, station, StringComparison.OrdinalIgnoreCase)) || now - open.Opened > SetTimeout)
                {
                    _openParts.Remove(open.Id);
                    given.Add(open);
                }
            }
        }

        return given.Select(open =>
        {
            var taken = open.Shots.Count;
            var part = new PartRecord(
                open.Number,
                open.Id,
                open.Shots,
                PartVerdict.NotInspected,
                open.Opened,
                now - open.Opened,
                $"its set was not completed: {taken} shot{(taken == 1 ? string.Empty : "s")} taken")
            {
                Result = Stations?.ResultsOf(open.Station).NotInspectedResult,
            };

            Count(part);
            return part;
        }).ToList();
    }

    private void Count(PartRecord part)
    {
        lock (_partGate)
        {
            _partCounts = _partCounts.Add(part.Verdict, part.Result);
            _partResults = _partResults.Add(part.Result);
            _lastPart = part;
        }

        SaveCounts();

        Write(
            part.Verdict == PartVerdict.Passed ? WorkflowLogLevel.Info : WorkflowLogLevel.Warning,
            null,
            $"Part {part.Id}: {part.Word}{(part.Problem is null ? string.Empty : $" - {part.Problem}")}.");
    }

    /// <summary>The part a pass judged, counted and kept; null when it inspected nothing.</summary>
    private PartRecord? FinishPart(WorkflowPass pass, TimeSpan duration)
    {
        if (pass.PartNumber is null && pass.Missed is null)
        {
            Announce(GiveUpOpenParts(null, null));
            return null;
        }

        BeginPart(pass, null);

        // Its set's last still to come: kept open for the next pass, not a part yet (F26).
        if (pass.Missed is null && pass.Shots.Count > 0 && pass.Shots[^1].Received)
        {
            Announce(GiveUpOpenParts(pass.Shots[^1].Station, pass.PartId));

            lock (_partGate)
                _openParts[pass.PartId!] = new OpenPart(pass.PartNumber!.Value, pass.PartId!, pass.Shots.ToArray(), DateTime.Now, pass.Shots[^1].Station, pass.Numbered);

            return null;
        }

        Announce(GiveUpOpenParts(null, pass.PartId));

        var verdict = PartRecord.Judge(pass.Shots, pass.Missed);
        var part = new PartRecord(
            pass.PartNumber!.Value,
            pass.PartId!,
            pass.Shots.ToArray(),
            verdict,
            DateTime.Now - duration,
            duration,
            pass.MissedWhy)
        {
            Result = PartRecord.ResultOf(pass.Shots, pass.Missed, pass.MissedResult),
        };

        Count(part);
        return part;
    }

    /// <summary>Parts given up on, told to whoever listens, as a part judged is.</summary>
    private void Announce(IEnumerable<PartRecord> parts)
    {
        foreach (var part in parts)
            PartFinished?.Invoke(this, part);
    }

    private void SaveCounts()
    {
        string? path;
        PartCounts counts;
        ResultTally results;
        long next;

        lock (_partGate)
        {
            path = _countsPath;
            counts = _partCounts;
            results = _partResults;
            next = _nextPart;
        }

        if (path is not null && PartCountsFile.Save(path, counts, next, results) is { } problem)
            Write(WorkflowLogLevel.Warning, null, problem);
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
        // there: what needed it does not run on without it. A node that
        // failed may still have given some - Inspect's result and code for a
        // part it missed (F33) - and what reads only those runs.
        var failedFeeding = node.Inputs
            .Where(input => input.DataType != typeof(Signal))
            .Select(input => input.Connection?.Source)
            .Any(source => source is not null && source.DataType != typeof(Signal)
                           && pass.Reached.Contains(source.Node) && pass.Failed.Contains(source.Node)
                           && !pass.Values.ContainsKey(source));

        if (failedFeeding)
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
        var took = Stopwatch.StartNew();
        Report(pass, node, WorkflowNodeState.Running, TimeSpan.Zero);

        try
        {
            await node.RunAsync(step).ConfigureAwait(false);
            step.Commit();
            pass.Ran.Add(node);
            Report(pass, node, WorkflowNodeState.Done, took.Elapsed);
            return true;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            var why = $"No answer within {timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s.";

            // A station past its deadline: the part is late, a reject.
            if (node.Inspects)
                Missed(pass, PartVerdict.Late, $"{node.Name}: no result within {timeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms", MissedOutputs(node, step, late: true));

            Fail(node, pass, why);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (node.Inspects)
                Missed(pass, PartVerdict.NotInspected, $"{node.Name}: {ex.Message.TrimEnd('.')}", MissedOutputs(node, step, late: false));

            Fail(node, pass, ex.Message);
        }

        return false;
    }

    /// <summary>
    /// What a node gives for a part it missed - the result and its code (F33) -
    /// kept for what follows Failed; a node that throws doing it gives nothing.
    /// </summary>
    private static ResultCode? MissedOutputs(WorkflowNode node, WorkflowStep step, bool late)
    {
        try
        {
            var result = node.Missed(step, late);
            step.CommitValues();
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>A station gave no result for the part; late wins over not inspected, the first reason is kept.</summary>
    private static void Missed(WorkflowPass pass, PartVerdict how, string why, ResultCode? result)
    {
        if (pass.Missed == PartVerdict.Late)
            return;

        pass.Missed = how;
        pass.MissedWhy ??= why;
        pass.MissedResult = ResultCodeWorst(pass.MissedResult, result);

        if (how == PartVerdict.Late)
        {
            pass.MissedWhy = why;
            pass.MissedResult = result ?? pass.MissedResult;
        }
    }

    private static ResultCode? ResultCodeWorst(ResultCode? a, ResultCode? b) =>
        ResultCode.Worst([a, b]);

    /// <summary>Tells whoever watches - an editor lighting its nodes - and never lets them stop the pass.</summary>
    private void Report(WorkflowPass pass, VisionNode node, WorkflowNodeState state, TimeSpan took, string? message = null)
    {
        if (NodeActivity is not { } watchers)
            return;

        try
        {
            watchers(this, new WorkflowNodeActivity(pass.Number, node.Id, node.Name, state, took, message));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Lighting a node is not worth a pass.
        }
    }

    private void Fail(WorkflowNode node, WorkflowPass pass, string message)
    {
        pass.Failed.Add(node);
        Report(pass, node, WorkflowNodeState.Failed, TimeSpan.Zero, message);
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
