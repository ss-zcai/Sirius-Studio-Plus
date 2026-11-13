using Sirius.Core.Graph;
using Sirius.Core.Runtime;

namespace Sirius.Plc.Workflows;

/// <summary>One pass of a workflow: what has run, fired and been given so far.</summary>
internal sealed class WorkflowPass
{
    public WorkflowPass(int number, WorkflowEventNode start, IReadOnlyCollection<VisionNode> reached)
    {
        Number = number;
        Start = start;
        Reached = reached;
    }

    public int Number { get; }

    public WorkflowEventNode Start { get; }

    /// <summary>The nodes downstream of the one that started it, which may run in it.</summary>
    public IReadOnlyCollection<VisionNode> Reached { get; }

    public HashSet<VisionNode> Ran { get; } = [];

    public HashSet<VisionNode> Failed { get; } = [];

    public HashSet<OutputPort> Fired { get; } = [];

    public Dictionary<OutputPort, object?> Values { get; } = [];
}

/// <summary>
/// What a node is given when it runs: its inputs, its outputs, the PLC and
/// the stations.
/// </summary>
/// <remarks>
/// What a node sets and fires counts only if it finishes: one that fails
/// part way has given nothing, and only its Failed output fires.
/// </remarks>
public sealed class WorkflowStep
{
    private readonly WorkflowRunner _runner;
    private readonly WorkflowPass _pass;
    private readonly Dictionary<OutputPort, object?> _set = [];
    private readonly HashSet<OutputPort> _fired = [];

    internal WorkflowStep(WorkflowRunner runner, WorkflowPass pass, WorkflowNode node, CancellationToken cancellationToken)
    {
        _runner = runner;
        _pass = pass;
        Node = node;
        CancellationToken = cancellationToken;
    }

    public WorkflowNode Node { get; }

    /// <summary>The controller's PLC.</summary>
    public IPlcDriver Driver => _runner.Driver;

    /// <summary>The project's stations; null when the controller runs without them.</summary>
    public IStationBus? Stations => _runner.Stations;

    /// <summary>Cancelled when the step has taken too long, or the controller stops.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Which pass of the workflow this is, from 1.</summary>
    public int Pass => _pass.Number;

    /// <summary>Whether a trigger reached this node through <paramref name="trigger"/> in this pass.</summary>
    public bool WasFired(InputPort trigger) => trigger.Connection is { } wire && _pass.Fired.Contains(wire.Source);

    public bool IsWired(InputPort input) => input.Connection is not null;

    /// <summary>
    /// The value wired into <paramref name="input"/>: what its source gave
    /// in this pass, or - for a source this pass does not reach - the last
    /// it gave. Null when nothing is wired.
    /// </summary>
    /// <exception cref="WorkflowException">
    /// The source is in this pass and gave nothing - it failed, or its branch
    /// was not taken - or it has never given anything.
    /// </exception>
    public object? Get(InputPort input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Connection is not { } wire)
            return null;

        var source = wire.Source;

        // A trigger wired into a value: whether it fired.
        if (source.DataType == typeof(Signal))
            return _pass.Fired.Contains(source);

        if (_pass.Values.TryGetValue(source, out var value))
            return value;

        if (!_pass.Reached.Contains(source.Node) && _runner.LastValue(source, out var last))
            return last;

        throw new WorkflowException(_pass.Reached.Contains(source.Node)
            ? $"'{input.DisplayName}' has no value: '{source.Node.Name}' gave none in this pass."
            : $"'{input.DisplayName}' has no value: '{source.Node.Name}' has not run yet.");
    }

    /// <summary>Gives an output its value for this pass.</summary>
    public void Set(OutputPort output, object? value)
    {
        Own(output);
        _set[output] = value;
    }

    /// <summary>Fires a trigger output: what is wired to it runs next.</summary>
    public void Fire(OutputPort output)
    {
        Own(output);

        if (output.DataType != typeof(Signal))
            throw new ArgumentException($"'{output.DisplayName}' is a value, not a trigger.", nameof(output));

        _fired.Add(output);
    }

    /// <summary>A line in the controller's log, under this node's name.</summary>
    public void Log(string message) => _runner.Write(WorkflowLogLevel.Info, Node, message);

    /// <summary>A detail in the controller's log - each read and write - shown when asked for.</summary>
    public void Trace(string message) => _runner.Write(WorkflowLogLevel.Trace, Node, message);

    internal void Commit()
    {
        foreach (var (output, value) in _set)
            _pass.Values[output] = value;

        _pass.Fired.UnionWith(_fired);
    }

    private void Own(OutputPort output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (!ReferenceEquals(output.Node, Node))
            throw new ArgumentException($"'{output.DisplayName}' is not an output of '{Node.Name}'.", nameof(output));
    }
}

/// <summary>What an event node is given to watch with: the PLC, the stations, and a way to start a pass.</summary>
public sealed class WorkflowWatch
{
    private readonly WorkflowRunner _runner;

    internal WorkflowWatch(WorkflowRunner runner, WorkflowEventNode node)
    {
        _runner = runner;
        Node = node;
    }

    public WorkflowEventNode Node { get; }

    public IPlcDriver Driver => _runner.Driver;

    public IStationBus? Stations => _runner.Stations;

    /// <summary>
    /// Starts a pass of the workflow from this node - after any already
    /// waiting, since a controller's passes run one at a time, in order.
    /// </summary>
    /// <param name="outputs">Gives this node's outputs their values, and fires its triggers, for the pass.</param>
    /// <param name="what">What happened, for the log: "rising edge on DB34.20.0".</param>
    /// <returns>Finished when the pass has - or at once, when the controller is stopping.</returns>
    public Task RaiseAsync(Action<WorkflowStep> outputs, string what) => _runner.Enqueue(Node, outputs, what);

    public void Log(string message) => _runner.Write(WorkflowLogLevel.Info, Node, message);

    public void Warn(string message) => _runner.Write(WorkflowLogLevel.Warning, Node, message);
}
