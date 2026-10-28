using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Sirius.Core.Nodes;

namespace Sirius.Core.Graph;

/// <summary>
/// Base class for every node in a vision graph.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the old <c>IVisionTool</c> interface. The important change is
/// shape: <c>IVisionTool</c> had exactly one <c>InputImage</c> and one
/// <c>OutputImage</c> (plus an untyped <c>object</c> escape hatch), which is
/// why the old pipeline could only ever be a straight line. Nodes here declare
/// any number of typed ports, so branching and merging are expressible.
/// </para>
/// <para>
/// It is also a base class rather than an interface so the ~9 members of
/// plumbing each old tool re-declared - with <c>[Browsable(false)]</c> on every
/// one - exist exactly once.
/// </para>
/// <para>
/// Derived nodes declare ports in their constructor via <see cref="AddInput{T}"/>
/// and <see cref="AddOutput{T}"/>, expose parameters as properties marked with
/// <see cref="ParamAttribute"/>, and override <see cref="OnEvaluate"/>.
/// </para>
/// </remarks>
public abstract class VisionNode : ObservableObject
{
    private readonly List<InputPort> _inputs = new();
    private readonly List<OutputPort> _outputs = new();

    /// <summary>Ports created from connectable parameters, and their metadata.</summary>
    private readonly Dictionary<InputPort, ParamDescriptor> _parameterPorts = new();

    private bool _parameterPortsBuilt;

    private string _name;
    private bool _enabled = true;
    private NodeStatus _status = NodeStatus.Idle;
    private string? _statusMessage;
    private double _x;
    private double _y;
    private TimeSpan _lastEvaluationTime;

    protected VisionNode()
        : this(null)
    {
    }

    /// <summary>
    /// For a node whose metadata is not its own class's - one wrapper type
    /// standing in for many, each with its own descriptor.
    /// </summary>
    protected VisionNode(NodeDescriptor? descriptor)
    {
        Descriptor = descriptor ?? NodeCatalog.Describe(GetType());
        _name = Descriptor.DisplayName;
    }

    /// <summary>Stable instance identifier, persisted in the saved graph.</summary>
    public string Id { get; internal set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The graph this node is in, or null when it is in none - set by
    /// <see cref="VisionGraph.Add"/> and <see cref="VisionGraph.Remove"/>.
    /// </summary>
    /// <remarks>
    /// A node rarely needs to know. A sub-graph does: deleting a boundary
    /// inside one takes a socket off the outside, and the wires on that
    /// socket belong to the graph the sub-graph sits in.
    /// </remarks>
    public VisionGraph? Graph { get; internal set; }

    /// <summary>
    /// Raised when this node added, removed, renamed or retyped a port of
    /// its own accord, so an editor showing it knows to read it again.
    /// </summary>
    /// <remarks>
    /// Most nodes' ports never change. A growing node's change in response
    /// to its own wiring, which the editor already rebuilds after; this is
    /// for a change caused somewhere the editor cannot see - inside a
    /// sub-graph, open in another tab.
    /// </remarks>
    public event EventHandler? PortsChanged;

    protected void OnPortsChanged() => PortsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>For a node whose ports another node changed - a sub-graph retyping a boundary inside it.</summary>
    internal void RaisePortsChanged() => OnPortsChanged();

    /// <summary>Static metadata for this node type: category, icon, parameters.</summary>
    public NodeDescriptor Descriptor { get; }

    /// <summary>User-editable label. Renaming does not invalidate results.</summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    private IReadOnlyList<MeasurementRecord> _measurements = [];
    private NodeAccent _accent = NodeAccent.Default;

    /// <summary>
    /// When false the node is skipped and forwards its primary input to its
    /// primary output, so an operator can bypass a step without unwiring it.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
                Invalidate();
        }
    }

    /// <summary>
    /// Measurements drawn by hand on this node's viewer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept on the node so they are saved with the recipe and counted as a
    /// change to it. They were held only by the control before, which meant
    /// an afternoon of measuring vanished on File - New without so much as
    /// a prompt, because nothing in the recipe had altered.
    /// </para>
    /// <para>
    /// Set without invalidating: drawing a line on an image says nothing
    /// about what the graph computes, and re-running a recipe because
    /// somebody measured something would be absurd on a large frame.
    /// </para>
    /// </remarks>
    public IReadOnlyList<MeasurementRecord> Measurements
    {
        get => _measurements;
        set => SetProperty(ref _measurements, value ?? []);
    }

    /// <summary>
    /// How this node is coloured on the canvas.
    /// </summary>
    /// <remarks>
    /// Marking up a graph is work, so it is saved with the recipe and counts
    /// as a change to it - and, like a position, it changes nothing about
    /// what the recipe computes, so it does not invalidate.
    /// </remarks>
    public NodeAccent Accent
    {
        get => _accent;
        set => SetProperty(ref _accent, value);
    }

    public NodeStatus Status
    {
        get => _status;
        internal set => SetProperty(ref _status, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        internal set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Wall-clock duration of the most recent evaluation.</summary>
    public TimeSpan LastEvaluationTime
    {
        get => _lastEvaluationTime;
        internal set => SetProperty(ref _lastEvaluationTime, value);
    }

    /// <summary>Canvas X position. Presentation state, persisted with the graph.</summary>
    public double X
    {
        get => _x;
        set => SetProperty(ref _x, value);
    }

    /// <summary>Canvas Y position. Presentation state, persisted with the graph.</summary>
    public double Y
    {
        get => _y;
        set => SetProperty(ref _y, value);
    }

    public IReadOnlyList<InputPort> Inputs
    {
        get
        {
            EnsureParameterPorts();
            return _inputs;
        }
    }

    public IReadOnlyList<OutputPort> Outputs => _outputs;

    /// <summary>True if <paramref name="port"/> was created from a parameter.</summary>
    public bool IsParameterPort(InputPort port)
    {
        EnsureParameterPorts();
        return _parameterPorts.ContainsKey(port);
    }

    /// <summary>
    /// Appends an optional input for every parameter marked
    /// <see cref="ParamAttribute.Connectable"/>.
    /// </summary>
    /// <remarks>
    /// Built on first access rather than in the constructor: the base
    /// constructor runs before the derived one declares its data ports, so
    /// creating these eagerly would put parameter sockets above In/Out instead
    /// of below them.
    /// </remarks>
    private void EnsureParameterPorts()
    {
        if (_parameterPortsBuilt)
            return;

        _parameterPortsBuilt = true;

        foreach (var parameter in Descriptor.Parameters)
        {
            if (!parameter.Attribute.Connectable)
                continue;

            var port = new InputPort(parameter.Name, parameter.DisplayName, parameter.ValueType, isRequired: false)
            {
                Node = this,
            };

            _inputs.Add(port);
            _parameterPorts[port] = parameter;
        }
    }

    /// <summary>
    /// Raised when cached results for this node are no longer valid - a
    /// parameter changed, or it was enabled/disabled. The scheduler listens and
    /// marks this node plus everything downstream dirty.
    /// </summary>
    public event EventHandler? Invalidated;

    protected InputPort AddInput<T>(string name, string? displayName = null, bool isRequired = true) =>
        AddInput(typeof(T), name, displayName, isRequired);

    protected OutputPort AddOutput<T>(string name, string? displayName = null) =>
        AddOutput(typeof(T), name, displayName);

    /// <summary>
    /// Adds an input whose type is only known at run time - a node that
    /// declares its ports from a description rather than in code.
    /// </summary>
    protected InputPort AddInput(Type dataType, string name, string? displayName = null, bool isRequired = true)
    {
        ArgumentNullException.ThrowIfNull(dataType);

        var port = new InputPort(name, displayName ?? name, dataType, isRequired) { Node = this };
        _inputs.Add(port);
        return port;
    }

    /// <summary>The output counterpart of <see cref="AddInput(Type, string, string?, bool)"/>.</summary>
    protected OutputPort AddOutput(Type dataType, string name, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(dataType);

        var port = new OutputPort(name, displayName ?? name, dataType) { Node = this };
        _outputs.Add(port);
        return port;
    }

    /// <summary>
    /// Adds an input in a chosen position rather than at the end.
    /// </summary>
    /// <remarks>
    /// A node that grows its inputs has to put the new one beside its
    /// siblings. Appending would place it below the parameter sockets
    /// <see cref="EnsureParameterPorts"/> adds, so the node would sprout a
    /// second Shapes socket underneath its parameters.
    /// </remarks>
    protected InputPort InsertInput<T>(
        int index, string name, string? displayName = null, bool isRequired = true)
    {
        // Forces the parameter sockets into the list first, so the index
        // this is given means the same thing before and after they appear.
        _ = Inputs;

        var port = new InputPort(name, displayName ?? name, typeof(T), isRequired) { Node = this };
        _inputs.Insert(Math.Clamp(index, 0, _inputs.Count), port);
        return port;
    }

    /// <summary>
    /// Takes an input away again.
    /// </summary>
    /// <remarks>
    /// Refuses a connected port. Removing one would leave a connection in
    /// the graph pointing at a socket no node admits to having, which the
    /// editor draws as a wire to nowhere and the serialiser writes out as a
    /// connection that cannot be read back.
    /// </remarks>
    protected bool RemoveInput(InputPort port)
    {
        if (port is null || port.IsConnected || IsParameterPort(port))
            return false;

        return _inputs.Remove(port);
    }

    /// <summary>
    /// Puts an existing input back on the node, at a position.
    /// </summary>
    /// <remarks>
    /// For a socket that comes and goes with something else. Reattaching
    /// the same instance, rather than making a new one, keeps every
    /// reference to it - an undo history's, a wire's - meaning the same
    /// socket.
    /// </remarks>
    protected void AttachInput(InputPort port, int index)
    {
        ArgumentNullException.ThrowIfNull(port);

        if (!ReferenceEquals(port.Node, this))
            throw new ArgumentException("The port belongs to another node.", nameof(port));

        // Forces the parameter sockets into the list first, as InsertInput does.
        _ = Inputs;

        if (!_inputs.Contains(port))
            _inputs.Insert(Math.Clamp(index, 0, _inputs.Count), port);
    }

    /// <summary>The output counterpart of <see cref="AttachInput"/>.</summary>
    protected void AttachOutput(OutputPort port, int index)
    {
        ArgumentNullException.ThrowIfNull(port);

        if (!ReferenceEquals(port.Node, this))
            throw new ArgumentException("The port belongs to another node.", nameof(port));

        if (!_outputs.Contains(port))
            _outputs.Insert(Math.Clamp(index, 0, _outputs.Count), port);
    }

    /// <summary>
    /// Takes an output away. Refuses a connected one, for the reason
    /// <see cref="RemoveInput"/> does.
    /// </summary>
    protected bool RemoveOutput(OutputPort port)
    {
        if (port is null || port.IsConnected)
            return false;

        return _outputs.Remove(port);
    }

    public InputPort? FindInput(string name) =>
        Inputs.FirstOrDefault(p => p.Name == name);

    public OutputPort? FindOutput(string name) =>
        _outputs.FirstOrDefault(p => p.Name == name);

    /// <summary>
    /// Sets a parameter backing field and invalidates cached results on change.
    /// Use this - not <c>SetProperty</c> - for anything that affects output.
    /// </summary>
    protected bool SetParam<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
            return false;

        Invalidate();
        return true;
    }

    /// <summary>Marks this node's cached results stale.</summary>
    public void Invalidate() => Invalidated?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Produce outputs from inputs. Implementations should not dispose inputs,
    /// should honour <see cref="EvalContext.CancellationToken"/> on long work,
    /// and may throw - the scheduler converts exceptions into
    /// <see cref="NodeStatus.Error"/> rather than letting them escape.
    /// </summary>
    protected abstract void OnEvaluate(EvalContext context);

    internal void Evaluate(EvalContext context) => OnEvaluate(context);

    /// <summary>
    /// The input forwarded to <see cref="PrimaryOutput"/> when the node is
    /// disabled. Defaults to the first input; override if that is wrong.
    /// </summary>
    public virtual InputPort? PrimaryInput =>
        Inputs.FirstOrDefault(port => !IsParameterPort(port));

    /// <summary>The output that receives <see cref="PrimaryInput"/> on bypass.</summary>
    public virtual OutputPort? PrimaryOutput => _outputs.FirstOrDefault();

    public override string ToString() => $"{Descriptor.DisplayName} '{Name}' ({Id[..Math.Min(8, Id.Length)]})";
}
