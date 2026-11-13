using Sirius.Core.Graph;

namespace Sirius.Plc.Workflows;

/// <summary>
/// What a trigger wire carries: not a value, but "now". The type of every
/// trigger socket, so the editor only joins a trigger to a trigger.
/// </summary>
public sealed class Signal
{
    private Signal()
    {
    }
}

/// <summary>A step of a workflow could not do its work. The message is shown as it is.</summary>
public class WorkflowException : Exception
{
    public WorkflowException(string message)
        : base(message)
    {
    }

    public WorkflowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A node of a controller's workflow. Unlike a recipe's node, which runs
/// once per picture from start to end, it runs when a trigger reaches it -
/// see <see cref="WorkflowRunner"/>.
/// </summary>
/// <remarks>
/// <para>
/// A node declares trigger sockets with <see cref="AddTrigger"/> and
/// <see cref="AddFired"/>, value sockets as a recipe's node does, and does
/// its work in <see cref="RunAsync"/>: read its inputs from the step, set
/// its outputs, fire the triggers that should carry on.
/// </para>
/// <para>
/// Throwing - a <see cref="PlcException"/>, a <see cref="WorkflowException"/>,
/// taking longer than <see cref="StepTimeout"/> - is how a node fails. The
/// runner logs it and fires <see cref="FailedOutput"/>, so a workflow can
/// say what happens then.
/// </para>
/// </remarks>
public abstract class WorkflowNode : VisionNode
{
    /// <summary>A trigger input - what makes this node run.</summary>
    protected InputPort AddTrigger(string name = "Trigger") => AddInput<Signal>(name, isRequired: false);

    /// <summary>A trigger output - fired to carry on.</summary>
    protected OutputPort AddFired(string name) => AddOutput<Signal>(name);

    /// <summary>
    /// The trigger fired when this node fails, or null for none. A node with
    /// a "Failed" output names it here.
    /// </summary>
    public virtual OutputPort? FailedOutput => null;

    /// <summary>
    /// How long one run may take before it fails; null for the runner's
    /// default. A node that waits on purpose - for a bit, for a station -
    /// says how long.
    /// </summary>
    public virtual TimeSpan? StepTimeout => null;

    /// <summary>Does this node's work, for one pass of the workflow.</summary>
    protected internal abstract Task RunAsync(WorkflowStep step);

    /// <summary>
    /// What is wrong with this node's settings, in sentences - an address the
    /// PLC cannot have, a station the project has not got. Asked when the
    /// workflow is checked and when it starts.
    /// </summary>
    protected internal virtual IEnumerable<string> Check(WorkflowCheck check) => [];

    /// <summary>A workflow's node never runs in a recipe.</summary>
    protected sealed override void OnEvaluate(EvalContext context) =>
        throw new NotSupportedException($"'{Name}' is a controller's node: it runs in a PLC workflow, not a recipe.");
}

/// <summary>
/// A node that starts a pass of the workflow when something happens - a
/// bit rising in the PLC, a station finishing - rather than when a trigger
/// reaches it.
/// </summary>
/// <remarks>
/// It watches in <see cref="WatchAsync"/> for as long as the controller
/// runs, and calls <see cref="WorkflowWatch.RaiseAsync"/> each time, giving
/// its outputs' values for that pass.
/// </remarks>
public abstract class WorkflowEventNode : WorkflowNode
{
    /// <summary>
    /// Watches until <paramref name="cancellationToken"/> is cancelled -
    /// the controller stopping. Exceptions other than that are logged, and
    /// it is started watching again a moment later.
    /// </summary>
    protected internal abstract Task WatchAsync(WorkflowWatch watch, CancellationToken cancellationToken);

    /// <summary>Its outputs are set when it raises a pass; it has nothing else to do in one.</summary>
    protected internal sealed override Task RunAsync(WorkflowStep step) => Task.CompletedTask;
}

/// <summary>What a node's settings are checked against: the PLC's driver, and the project's stations.</summary>
public sealed class WorkflowCheck
{
    public WorkflowCheck(IPlcDriver? driver, IReadOnlyList<string>? stations)
    {
        Driver = driver;
        Stations = stations;
    }

    /// <summary>The PLC's driver, to ask about addresses; null when none is chosen yet.</summary>
    public IPlcDriver? Driver { get; }

    /// <summary>The project's stations; null when not known, so not checked.</summary>
    public IReadOnlyList<string>? Stations { get; }

    /// <summary>What is wrong with an address, or null - nothing when no driver is known.</summary>
    public string? AddressProblem(PlcVariable variable) => Driver?.AddressProblem(variable);

    /// <summary>Whether the project has a station of this name; true when stations are not known.</summary>
    public bool HasStation(string name) =>
        Stations is null || Stations.Any(station => string.Equals(station, name, StringComparison.OrdinalIgnoreCase));
}
