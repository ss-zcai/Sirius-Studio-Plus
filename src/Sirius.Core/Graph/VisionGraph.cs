using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sirius.Core.Graph;

/// <summary>Why a proposed connection was refused.</summary>
public enum ConnectionRejection
{
    None = 0,
    SamePort,
    SameNode,
    NotInGraph,
    TypeMismatch,
    WouldCreateCycle,
}

public readonly record struct ConnectionResult(bool Success, ConnectionRejection Reason, string? Message, Connection? Connection)
{
    public static ConnectionResult Ok(Connection connection) => new(true, ConnectionRejection.None, null, connection);

    public static ConnectionResult Fail(ConnectionRejection reason, string message) => new(false, reason, message, null);
}

/// <summary>
/// A directed acyclic graph of vision nodes. Owns structure only - evaluation
/// and caching live in <see cref="GraphScheduler"/>.
/// </summary>
public sealed class VisionGraph : ObservableObject
{
    private readonly ObservableCollection<VisionNode> _nodes = new();
    private readonly ObservableCollection<Connection> _connections = new();

    public VisionGraph()
    {
        Nodes = new ReadOnlyObservableCollection<VisionNode>(_nodes);
        Connections = new ReadOnlyObservableCollection<Connection>(_connections);
    }

    public ReadOnlyObservableCollection<VisionNode> Nodes { get; }

    public ReadOnlyObservableCollection<Connection> Connections { get; }

    /// <summary>Raised when a node signals that its cached results are stale.</summary>
    public event EventHandler<VisionNode>? NodeInvalidated;

    /// <summary>Raised when nodes or connections are added or removed.</summary>
    public event EventHandler? StructureChanged;

    public void Add(VisionNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (_nodes.Contains(node))
            return;

        _nodes.Add(node);
        node.Graph = this;
        node.Invalidated += OnNodeInvalidated;
        StructureChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a node along with every connection touching it.</summary>
    public void Remove(VisionNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!_nodes.Contains(node))
            return;

        foreach (var connection in _connections
                     .Where(c => ReferenceEquals(c.Source.Node, node) || ReferenceEquals(c.Target.Node, node))
                     .ToArray())
        {
            DisconnectCore(connection);
        }

        node.Invalidated -= OnNodeInvalidated;
        _nodes.Remove(node);

        if (ReferenceEquals(node.Graph, this))
            node.Graph = null;

        StructureChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Checks a proposed connection without mutating the graph - used by the
    /// editor to show whether a drag would land.
    /// </summary>
    public ConnectionResult CanConnect(OutputPort source, InputPort target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        if (ReferenceEquals(source, target))
            return ConnectionResult.Fail(ConnectionRejection.SamePort, "A port cannot connect to itself.");

        if (ReferenceEquals(source.Node, target.Node))
            return ConnectionResult.Fail(ConnectionRejection.SameNode, "A node cannot connect to itself.");

        if (!_nodes.Contains(source.Node) || !_nodes.Contains(target.Node))
            return ConnectionResult.Fail(ConnectionRejection.NotInGraph, "Both nodes must belong to this graph.");

        // A sub-graph's socket can be taken off with the boundary inside it,
        // while an undo history still holds the port. Wiring it then would
        // connect to a socket the node no longer shows.
        if (!source.Node.Outputs.Contains(source) || !target.Node.Inputs.Contains(target))
            return ConnectionResult.Fail(ConnectionRejection.NotInGraph, "That socket is no longer on its node.");

        if (!IsCompatible(source.DataType, target.DataType))
        {
            return ConnectionResult.Fail(
                ConnectionRejection.TypeMismatch,
                $"Cannot connect {source.DataType.Name} to {target.DataType.Name}.");
        }

        // Adding source.Node -> target.Node closes a loop exactly when
        // source.Node is already reachable downstream of target.Node.
        if (CanReach(target.Node, source.Node))
        {
            return ConnectionResult.Fail(
                ConnectionRejection.WouldCreateCycle,
                $"Connecting '{source.Node.Name}' to '{target.Node.Name}' would create a cycle.");
        }

        return new ConnectionResult(true, ConnectionRejection.None, null, null);
    }

    /// <summary>
    /// Connects two ports. An input accepts a single connection, so any existing
    /// one on <paramref name="target"/> is replaced.
    /// </summary>
    public ConnectionResult TryConnect(OutputPort source, InputPort target)
    {
        var check = CanConnect(source, target);
        if (!check.Success)
            return check;

        if (target.Connection is { } existing)
            DisconnectCore(existing);

        var connection = new Connection(source, target);
        source.Add(connection);
        target.Connection = connection;
        _connections.Add(connection);

        // Downstream results depended on the old wiring.
        OnNodeInvalidated(target.Node, EventArgs.Empty);
        StructureChanged?.Invoke(this, EventArgs.Empty);

        return ConnectionResult.Ok(connection);
    }

    public void Disconnect(Connection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!_connections.Contains(connection))
            return;

        var target = connection.Target.Node;
        DisconnectCore(connection);
        OnNodeInvalidated(target, EventArgs.Empty);
        StructureChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DisconnectCore(Connection connection)
    {
        connection.Source.Remove(connection);
        if (ReferenceEquals(connection.Target.Connection, connection))
            connection.Target.Connection = null;

        _connections.Remove(connection);
    }

    /// <summary>Nodes feeding <paramref name="node"/> directly.</summary>
    public IEnumerable<VisionNode> GetDirectUpstream(VisionNode node) =>
        node.Inputs
            .Select(input => input.Connection?.Source.Node)
            .Where(n => n is not null)
            .Distinct()!;

    /// <summary>Nodes consuming <paramref name="node"/>'s outputs directly.</summary>
    public IEnumerable<VisionNode> GetDirectDownstream(VisionNode node) =>
        node.Outputs
            .SelectMany(output => output.Connections)
            .Select(connection => connection.Target.Node)
            .Distinct();

    /// <summary>
    /// <paramref name="node"/> plus everything transitively downstream of it -
    /// exactly the set whose cached results a change to it invalidates.
    /// </summary>
    public IReadOnlyCollection<VisionNode> GetSelfAndDownstream(VisionNode node)
    {
        var visited = new HashSet<VisionNode>();
        var pending = new Stack<VisionNode>();
        pending.Push(node);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
                continue;

            foreach (var next in GetDirectDownstream(current))
                pending.Push(next);
        }

        return visited;
    }

    /// <summary>
    /// Whether a value of <paramref name="source"/> can drive an input of
    /// <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// Numbers are interchangeable: an int output may drive a double parameter
    /// and vice versa, with the conversion done at evaluation time. Requiring
    /// an exact match would force adapter nodes into graphs for no benefit.
    ///
    /// <see cref="object"/> is "any" at either end: a sub-graph socket nothing
    /// has been wired to yet, which takes its type from the first wire.
    /// </remarks>
    private static bool IsCompatible(Type source, Type target) =>
        target.IsAssignableFrom(source) ||
        source == typeof(object) ||
        (IsNumeric(source) && IsNumeric(target)) ||
        IsVectorForPoint(source, target);

    /// <summary>
    /// A matrix may drive a point, since a point is a two-value vector.
    /// </summary>
    /// <remarks>
    /// Checked by type here and by length when it is read: a matrix knows
    /// how many values it holds only once it has one, so refusing the wire
    /// would mean refusing every matrix on the chance it is the wrong
    /// shape. The node says so at evaluate time instead, the way a filter
    /// does with a kernel that cannot convolve.
    /// </remarks>
    private static bool IsVectorForPoint(Type source, Type target) =>
        source == typeof(Nodes.NumberMatrix) && target == typeof(Shapes.Point2);

    private static bool IsNumeric(Type type) =>
        Type.GetTypeCode(Nullable.GetUnderlyingType(type) ?? type) is
            TypeCode.Byte or TypeCode.SByte or
            TypeCode.Int16 or TypeCode.UInt16 or
            TypeCode.Int32 or TypeCode.UInt32 or
            TypeCode.Int64 or TypeCode.UInt64 or
            TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

    private bool CanReach(VisionNode from, VisionNode target)
    {
        var visited = new HashSet<VisionNode>();
        var pending = new Stack<VisionNode>();
        pending.Push(from);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (ReferenceEquals(current, target))
                return true;

            if (!visited.Add(current))
                continue;

            foreach (var next in GetDirectDownstream(current))
                pending.Push(next);
        }

        return false;
    }

    /// <summary>
    /// Orders nodes so every node follows its dependencies (Kahn's algorithm).
    /// Returns false if a cycle exists, which <see cref="TryConnect"/> should
    /// already have prevented - this is the backstop for graphs loaded from disk.
    /// </summary>
    public bool TryTopologicalSort(out IReadOnlyList<VisionNode> ordered, out string? error)
    {
        var indegree = new Dictionary<VisionNode, int>(_nodes.Count);
        foreach (var node in _nodes)
            indegree[node] = GetDirectUpstream(node).Count();

        var ready = new Queue<VisionNode>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var result = new List<VisionNode>(_nodes.Count);

        while (ready.Count > 0)
        {
            var node = ready.Dequeue();
            result.Add(node);

            foreach (var consumer in GetDirectDownstream(node))
            {
                if (--indegree[consumer] == 0)
                    ready.Enqueue(consumer);
            }
        }

        if (result.Count != _nodes.Count)
        {
            var offenders = indegree.Where(kv => kv.Value > 0).Select(kv => kv.Key.Name);
            ordered = Array.Empty<VisionNode>();
            error = $"Graph contains a cycle involving: {string.Join(", ", offenders)}.";
            return false;
        }

        ordered = result;
        error = null;
        return true;
    }

    private void OnNodeInvalidated(object? sender, EventArgs e)
    {
        if (sender is VisionNode node)
            NodeInvalidated?.Invoke(this, node);
    }
}
