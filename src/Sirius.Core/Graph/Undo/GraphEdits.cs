using System.Reflection;
using Sirius.Core.Nodes;

namespace Sirius.Core.Graph.Undo;

/// <summary>
/// A property of a node that undo can read and put back.
/// </summary>
/// <remarks>
/// Usually a CLR property of the node class. A custom node's settings are
/// not - they live on the plugin object the node wraps - so this reaches a
/// parameter through its descriptor instead, and undo stays indifferent to
/// where a value is kept.
/// </remarks>
public sealed class NodeProperty
{
    private readonly Func<VisionNode, object?> _get;
    private readonly Action<VisionNode, object?> _set;

    private NodeProperty(string name, Func<VisionNode, object?> get, Action<VisionNode, object?> set)
    {
        Name = name;
        _get = get;
        _set = set;
    }

    public string Name { get; }

    public object? GetValue(VisionNode node) => _get(node);

    public void SetValue(VisionNode node, object? value) => _set(node, value);

    public static NodeProperty Of(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return new NodeProperty(property.Name, property.GetValue, property.SetValue);
    }

    public static NodeProperty Of(ParamDescriptor parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return new NodeProperty(parameter.Name, parameter.GetValue, parameter.SetValue);
    }
}

/// <summary>Adding one node.</summary>
/// <remarks>
/// The node instance is held, not described, so redo puts back the same
/// object rather than an equal one - and anything that was looking at it
/// while it existed is looking at it again.
/// </remarks>
public sealed class AddNodeEdit : IGraphEdit
{
    private readonly VisionNode _node;

    public AddNodeEdit(VisionNode node) => _node = node;

    public string Label => $"Add {_node.Name}";

    public bool AffectsResults => true;

    public void Apply(VisionGraph graph) => graph.Add(_node);

    public void Revert(VisionGraph graph) => graph.Remove(_node);

    public bool TryAbsorb(IGraphEdit next) => false;
}

/// <summary>
/// Removing nodes, and restoring the wires that were attached to them.
/// </summary>
/// <remarks>
/// <see cref="VisionGraph.Remove"/> drops every connection touching a node
/// and reports none of them, so the edit has to record the wiring before it
/// goes. Without that, undoing a delete would put the node back unconnected,
/// which looks like undo half-working - the worst kind of half.
/// </remarks>
public sealed class RemoveNodesEdit : IGraphEdit
{
    private readonly VisionNode[] _nodes;
    private readonly (OutputPort Source, InputPort Target)[] _wires;

    /// <param name="graph">Read for the wiring, not modified here.</param>
    /// <param name="nodes">What is about to be removed.</param>
    public RemoveNodesEdit(VisionGraph graph, IEnumerable<VisionNode> nodes)
    {
        _nodes = nodes.ToArray();

        var removed = new HashSet<VisionNode>(_nodes);

        _wires = graph.Connections
            .Where(c => removed.Contains(c.Source.Node) || removed.Contains(c.Target.Node))
            .Select(c => (c.Source, c.Target))
            .ToArray();
    }

    public string Label => _nodes.Length == 1
        ? $"Delete {_nodes[0].Name}"
        : $"Delete {_nodes.Length} nodes";

    public bool AffectsResults => true;

    public void Apply(VisionGraph graph)
    {
        foreach (var node in _nodes)
            graph.Remove(node);
    }

    public void Revert(VisionGraph graph)
    {
        foreach (var node in _nodes)
            graph.Add(node);

        // After every node is back, or a wire between two of them would be
        // refused for having an endpoint outside the graph.
        foreach (var (source, target) in _wires)
            graph.TryConnect(source, target);
    }

    public bool TryAbsorb(IGraphEdit next) => false;
}

/// <summary>
/// Connecting two ports, including whatever wire that displaced.
/// </summary>
/// <remarks>
/// An input holds one connection, so connecting to a taken input silently
/// replaces what was there. Undo has to put the old wire back, which means
/// recording it at the time - afterwards there is nothing left to read.
/// </remarks>
public sealed class ConnectEdit : IGraphEdit
{
    private readonly OutputPort _source;
    private readonly InputPort _target;
    private readonly OutputPort? _replaced;

    /// <param name="replaced">
    /// What was feeding <paramref name="target"/> before, if anything.
    /// </param>
    public ConnectEdit(OutputPort source, InputPort target, OutputPort? replaced)
    {
        _source = source;
        _target = target;
        _replaced = replaced;
    }

    public string Label => $"Connect {_source.Node.Name} to {_target.Node.Name}";

    public bool AffectsResults => true;

    public void Apply(VisionGraph graph) => graph.TryConnect(_source, _target);

    public void Revert(VisionGraph graph)
    {
        if (_target.Connection is { } current)
            graph.Disconnect(current);

        if (_replaced is not null)
            graph.TryConnect(_replaced, _target);
    }

    public bool TryAbsorb(IGraphEdit next) => false;
}

/// <summary>Removing wires.</summary>
public sealed class DisconnectEdit : IGraphEdit
{
    private readonly (OutputPort Source, InputPort Target)[] _wires;

    public DisconnectEdit(IEnumerable<Connection> connections) =>
        _wires = connections.Select(c => (c.Source, c.Target)).ToArray();

    public string Label => _wires.Length == 1 ? "Disconnect" : $"Disconnect {_wires.Length} wires";

    public bool AffectsResults => true;

    public void Apply(VisionGraph graph)
    {
        foreach (var (source, target) in _wires)
        {
            // Matched by endpoint rather than held as a Connection: redo runs
            // against the object the previous revert created, not the one
            // that existed when this edit was first made.
            var connection = graph.Connections.FirstOrDefault(
                c => ReferenceEquals(c.Source, source) && ReferenceEquals(c.Target, target));

            if (connection is not null)
                graph.Disconnect(connection);
        }
    }

    public void Revert(VisionGraph graph)
    {
        foreach (var (source, target) in _wires)
            graph.TryConnect(source, target);
    }

    public bool TryAbsorb(IGraphEdit next) => false;
}

/// <summary>
/// One property of one node, before and after.
/// </summary>
/// <remarks>
/// <para>
/// The generic edit, and the reason the stack does not need a class per
/// parameter. A rename, a bypass, a threshold, a colour, a drag and a
/// measurement are all this.
/// </para>
/// <para>
/// Recorded by watching the node rather than by being told, which is what
/// keeps it honest: a parameter added to a node tomorrow is undoable the
/// day it ships, with nothing written here about it. See
/// <see cref="UndoRecorder"/>.
/// </para>
/// </remarks>
public sealed class PropertyEdit : IGraphEdit
{
    private readonly VisionNode _node;
    private readonly NodeProperty _property;
    private readonly object? _before;
    private object? _after;

    public PropertyEdit(
        VisionNode node,
        PropertyInfo property,
        object? before,
        object? after,
        string? label = null,
        bool affectsResults = true)
        : this(node, NodeProperty.Of(property), before, after, label, affectsResults)
    {
    }

    public PropertyEdit(
        VisionNode node,
        NodeProperty property,
        object? before,
        object? after,
        string? label = null,
        bool affectsResults = true)
    {
        _node = node;
        _property = property;
        _before = before;
        _after = after;
        Label = label ?? $"Change {property.Name}";
        AffectsResults = affectsResults;
        At = DateTime.UtcNow;
    }

    public string Label { get; }

    public bool AffectsResults { get; }

    /// <summary>When this was recorded, for the merge window.</summary>
    public DateTime At { get; private set; }

    public VisionNode Node => _node;

    /// <summary>Which property this changes, by name.</summary>
    public string PropertyName => _property.Name;

    public void Apply(VisionGraph graph) => _property.SetValue(_node, _after);

    public void Revert(VisionGraph graph) => _property.SetValue(_node, _before);

    /// <summary>
    /// Keeps the original "before" and takes the newest "after", so a whole
    /// gesture undoes in one step.
    /// </summary>
    /// <remarks>
    /// The window is deliberately short. Half a second is longer than the gap
    /// between two frames of a drag and shorter than the gap between two
    /// decisions, so a drag coalesces and typing 100 then changing your mind
    /// to 200 does not.
    /// </remarks>
    public bool TryAbsorb(IGraphEdit next)
    {
        if (next is not PropertyEdit other ||
            !ReferenceEquals(other._node, _node) ||
            other.PropertyName != PropertyName ||
            other.At - At > MergeWindow)
        {
            return false;
        }

        _after = other._after;
        At = other.At;
        return true;
    }

    public static TimeSpan MergeWindow { get; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Several edits that undo as one.
/// </summary>
/// <remarks>
/// A paste is nodes and wires; a node drag is an X and a Y; deleting a
/// selection is nodes and the connections between them. Each is one action
/// to the person who did it, so it is one step back.
/// </remarks>
public sealed class CompositeEdit : IGraphEdit
{
    private readonly IGraphEdit[] _edits;

    public CompositeEdit(string label, IEnumerable<IGraphEdit> edits)
    {
        Label = label;
        _edits = edits.ToArray();
    }

    public string Label { get; }

    public bool AffectsResults => _edits.Any(edit => edit.AffectsResults);

    public int Count => _edits.Length;

    public void Apply(VisionGraph graph)
    {
        foreach (var edit in _edits)
            edit.Apply(graph);
    }

    /// <summary>
    /// Reverses in reverse order, which is what makes a composite safe to
    /// build out of edits that depend on each other - a wire added after the
    /// node it lands on comes off before the node does.
    /// </summary>
    public void Revert(VisionGraph graph)
    {
        for (var i = _edits.Length - 1; i >= 0; i--)
            _edits[i].Revert(graph);
    }

    /// <summary>
    /// Absorbs another composite when they line up edit for edit - which is
    /// what consecutive frames of a multi-node drag look like.
    /// </summary>
    public bool TryAbsorb(IGraphEdit next)
    {
        if (next is not CompositeEdit other || other._edits.Length != _edits.Length)
            return false;

        // All or nothing: a half-absorbed composite would leave the stack
        // describing a gesture that never happened. Checked against copies so
        // a refusal part-way through changes nothing.
        if (!_edits.Zip(other._edits).All(pair => CanAbsorb(pair.First, pair.Second)))
            return false;

        for (var i = 0; i < _edits.Length; i++)
            _edits[i].TryAbsorb(other._edits[i]);

        return true;
    }

    private static bool CanAbsorb(IGraphEdit current, IGraphEdit next) =>
        current is PropertyEdit first &&
        next is PropertyEdit second &&
        ReferenceEquals(first.Node, second.Node) &&
        first.PropertyName == second.PropertyName &&
        second.At - first.At <= PropertyEdit.MergeWindow;
}
