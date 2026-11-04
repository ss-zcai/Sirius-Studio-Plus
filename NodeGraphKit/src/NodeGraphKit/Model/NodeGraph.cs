namespace NodeGraphKit.Model;

/// <summary>Whether a wire was made, and why not.</summary>
public sealed record ConnectResult(bool Success, string? Message, GraphConnection? Connection, GraphConnection? Replaced)
{
    internal static ConnectResult Refused(string message) => new(false, message, null, null);
}

/// <summary>
/// Nodes and the wires between them. The rules for wiring live here, so
/// every way of making a wire - the canvas, a file, a paste - obeys them.
/// </summary>
public sealed class NodeGraph
{
    private readonly List<GraphNode> _nodes = [];
    private readonly List<GraphConnection> _connections = [];

    public IReadOnlyList<GraphNode> Nodes => _nodes;

    public IReadOnlyList<GraphConnection> Connections => _connections;

    /// <summary>Off by default: a loop has no order to run in.</summary>
    public bool AllowCycles { get; set; }

    /// <summary>Raised after any node or wire is added or removed.</summary>
    public event EventHandler? Changed;

    public GraphNode? Find(string id) => _nodes.FirstOrDefault(node => node.Id == id);

    public void Add(GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (_nodes.Contains(node))
            return;

        if (Find(node.Id) is not null)
            throw new ArgumentException($"A node with id '{node.Id}' is already in the graph.", nameof(node));

        _nodes.Add(node);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a node and every wire touching it.</summary>
    public void Remove(GraphNode node)
    {
        foreach (var connection in _connections.Where(c => c.Source.Node == node || c.Target.Node == node).ToArray())
            Detach(connection);

        if (_nodes.Remove(node))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        foreach (var connection in _connections.ToArray())
            Detach(connection);

        _nodes.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether a wire may be made, without making it.</summary>
    public string? Check(GraphPort output, GraphPort input)
    {
        if (output.Direction != PortDirection.Output || input.Direction != PortDirection.Input)
            return "Connect an output to an input.";

        if (!_nodes.Contains(output.Node) || !_nodes.Contains(input.Node))
            return "Both nodes must be in the graph.";

        if (output.Node == input.Node)
            return "A node cannot feed itself.";

        if (!PortTypes.CanConnect(output.Type, input.Type))
            return $"{output.Type.Name} cannot go into {input.Type.Name}.";

        if (!AllowCycles && Reaches(input.Node, output.Node))
            return "That wire would make a loop.";

        return null;
    }

    /// <summary>
    /// Wires an output to an input. An input holds one wire, so one already
    /// there is replaced and reported in <see cref="ConnectResult.Replaced"/>.
    /// </summary>
    public ConnectResult Connect(GraphPort output, GraphPort input)
    {
        if (Check(output, input) is { } problem)
            return ConnectResult.Refused(problem);

        var replaced = ConnectionInto(input);

        if (replaced is not null && replaced.Source == output)
            return new ConnectResult(true, null, replaced, null);

        if (replaced is not null)
            Detach(replaced);

        var connection = new GraphConnection(output, input);
        _connections.Add(connection);
        output.IsConnected = true;
        input.IsConnected = true;

        Changed?.Invoke(this, EventArgs.Empty);
        return new ConnectResult(true, null, connection, replaced);
    }

    public void Disconnect(GraphConnection connection)
    {
        if (Detach(connection))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The wire into an input, if any.</summary>
    public GraphConnection? ConnectionInto(GraphPort input) =>
        _connections.FirstOrDefault(c => c.Target == input);

    /// <summary>The wires out of an output, or the one into an input.</summary>
    public IEnumerable<GraphConnection> ConnectionsOf(GraphPort port) =>
        _connections.Where(c => c.Source == port || c.Target == port);

    /// <summary>
    /// The nodes in an order where each comes after everything feeding it -
    /// the order to run them in.
    /// </summary>
    public IReadOnlyList<GraphNode> TopologicalOrder()
    {
        var waiting = _nodes.ToDictionary(node => node, node => _connections.Count(c => c.Target.Node == node));
        var ready = new Queue<GraphNode>(_nodes.Where(node => waiting[node] == 0));
        var order = new List<GraphNode>(_nodes.Count);

        while (ready.Count > 0)
        {
            var node = ready.Dequeue();
            order.Add(node);

            foreach (var connection in _connections.Where(c => c.Source.Node == node))
            {
                if (--waiting[connection.Target.Node] == 0)
                    ready.Enqueue(connection.Target.Node);
            }
        }

        // A loop, when allowed, is left in the order the nodes were added.
        order.AddRange(_nodes.Except(order));
        return order;
    }

    private bool Detach(GraphConnection connection)
    {
        if (!_connections.Remove(connection))
            return false;

        connection.Source.IsConnected = _connections.Any(c => c.Source == connection.Source);
        connection.Target.IsConnected = _connections.Any(c => c.Target == connection.Target);
        return true;
    }

    /// <summary>Whether wires lead from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private bool Reaches(GraphNode from, GraphNode to)
    {
        var seen = new HashSet<GraphNode>();
        var pending = new Stack<GraphNode>([from]);

        while (pending.Count > 0)
        {
            var node = pending.Pop();

            if (node == to)
                return true;

            if (!seen.Add(node))
                continue;

            foreach (var connection in _connections.Where(c => c.Source.Node == node))
                pending.Push(connection.Target.Node);
        }

        return false;
    }
}
