using System.ComponentModel;
using Sirius.Core.Nodes;

namespace Sirius.Core.Graph;

/// <summary>
/// A node holding a graph of its own.
/// </summary>
/// <remarks>
/// What the scheduler, the serialiser and the clipboard need of a sub-graph,
/// so none of them has to know the node class.
/// </remarks>
public interface ISubGraphHost
{
    /// <summary>The graph inside.</summary>
    VisionGraph Inner { get; }

    /// <summary>Swaps in a graph read from a file, replacing whatever was inside.</summary>
    void Replace(VisionGraph graph);

    /// <summary>
    /// Gives every node inside a new id, at any depth, keeping the sockets
    /// wired - for a pasted copy, which would otherwise share its ids with
    /// the original.
    /// </summary>
    void RenewIds();

    /// <summary>
    /// Works out the order of the next nested pass. Called on the UI thread
    /// by the parent scheduler; see <see cref="GraphScheduler.Plan"/>.
    /// </summary>
    void Plan(SynchronizationContext? uiContext);

    /// <summary>
    /// The value on a port somewhere inside, at any depth. False when the
    /// port is not inside this one.
    /// </summary>
    bool TryReadNested(OutputPort port, out object? value);

    /// <summary>Frees what the inner graph has cached.</summary>
    void ReleaseResults();
}

/// <summary>Walking a graph and the graphs nested inside it.</summary>
public static class GraphTree
{
    /// <summary>
    /// Every node in <paramref name="graph"/> and in the sub-graphs inside
    /// it, at any depth, parents before what they contain.
    /// </summary>
    /// <param name="enabledOnly">
    /// Leave out bypassed nodes, and everything inside a bypassed sub-graph:
    /// what such a node last produced is not what the recipe now means.
    /// </param>
    public static IEnumerable<VisionNode> AllNodes(VisionGraph graph, bool enabledOnly = false)
    {
        ArgumentNullException.ThrowIfNull(graph);

        foreach (var node in graph.Nodes.ToArray())
        {
            if (enabledOnly && !node.Enabled)
                continue;

            yield return node;

            if (node is ISubGraphHost host)
            {
                foreach (var inner in AllNodes(host.Inner, enabledOnly))
                    yield return inner;
            }
        }
    }

    /// <summary>
    /// The sub-graph node whose inner graph is <paramref name="graph"/>,
    /// searching from <paramref name="root"/>, or null.
    /// </summary>
    public static VisionNode? OwnerOf(VisionGraph root, VisionGraph graph) =>
        AllNodes(root).FirstOrDefault(node => node is ISubGraphHost host && ReferenceEquals(host.Inner, graph));
}

/// <summary>
/// Where a value enters a sub-graph.
/// </summary>
/// <remarks>
/// Each one inside a sub-graph is an input socket on the sub-graph's node
/// outside, named after it. Its type is whatever is first wired to it, on
/// either side.
/// </remarks>
[VisionNode(
    "Graph Input",
    Category = NodeCategory.SubGraph,
    Icon = "ph_graph_input",
    TypeKey = "subgraph.input",
    Description = "A value coming into this sub-graph. It appears as an input socket on the sub-graph " +
                  "outside, named after this node, and takes the type of the first thing wired to it.")]
public sealed class GraphInputNode : VisionNode
{
    public GraphInputNode()
    {
        ValueOut = AddOutput(typeof(object), "Value");
    }

    /// <summary>What arrived on the matching socket outside, handed on unchanged.</summary>
    public OutputPort ValueOut { get; }

    /// <summary>Set by the sub-graph before each run.</summary>
    internal object? Value { get; set; }

    /// <summary>True while this is inside a sub-graph, which is the only place it does anything.</summary>
    internal bool IsHosted { get; set; }

    protected override void OnEvaluate(EvalContext context)
    {
        if (!IsHosted)
        {
            context.Warn("A Graph Input only does something inside a sub-graph.");
            return;
        }

        // Borrowed: the value belongs to the graph outside, which keeps it
        // until this sub-graph runs again and replaces it.
        if (Value is not null)
            context.Forward(ValueOut, Value);
    }
}

/// <summary>
/// Where a value leaves a sub-graph.
/// </summary>
[VisionNode(
    "Graph Output",
    Category = NodeCategory.SubGraph,
    Icon = "ph_graph_output",
    TypeKey = "subgraph.output",
    Description = "A value leaving this sub-graph. It appears as an output socket on the sub-graph " +
                  "outside, named after this node, and takes the type of the first thing wired to it.")]
public sealed class GraphOutputNode : VisionNode
{
    public GraphOutputNode()
    {
        ValueIn = AddInput(typeof(object), "Value", isRequired: false);
    }

    /// <summary>What leaves the sub-graph through the matching socket outside.</summary>
    public InputPort ValueIn { get; }

    /// <summary>True while this is inside a sub-graph.</summary>
    internal bool IsHosted { get; set; }

    protected override void OnEvaluate(EvalContext context)
    {
        if (!IsHosted)
            context.Warn("A Graph Output only does something inside a sub-graph.");
    }
}

/// <summary>
/// A graph used as one node.
/// </summary>
/// <remarks>
/// <para>
/// Its sockets are the <see cref="GraphInputNode"/>s and
/// <see cref="GraphOutputNode"/>s inside it: add one and the node outside
/// gains a socket, delete one and the socket goes, rename one and the socket
/// is renamed. Each starts typed as "any" and takes its type from the first
/// wire on either side, keeping it until both sides are unwired again - so
/// a second wire of a different type is refused the way any mismatched wire
/// is, rather than quietly changing what the first one was wired into.
/// </para>
/// <para>
/// The inner graph has a scheduler and a cache of its own, so changing a
/// parameter inside re-runs what that parameter affects and nothing else,
/// and a sub-graph whose inputs have not changed costs nothing to re-run.
/// </para>
/// </remarks>
[VisionNode(
    "Sub Graph",
    Category = NodeCategory.SubGraph,
    Icon = "ph_sub_graph",
    TypeKey = "subgraph.graph",
    Description = "A graph of its own, used as one node. Double-click it to open it in a tab. " +
                  "Graph Input and Graph Output nodes inside become its sockets.")]
public sealed class SubGraphNode : VisionNode, ISubGraphHost, IGrowingPorts
{
    private VisionGraph _inner = null!;
    private GraphScheduler? _scheduler;

    /// <summary>Each boundary inside, and the socket it is outside.</summary>
    private readonly Dictionary<VisionNode, Port> _sockets = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Sockets whose boundary has gone, kept by its id so an undo that puts
    /// the boundary back puts back the same socket.
    /// </summary>
    private readonly Dictionary<string, Port> _retired = new(StringComparer.Ordinal);

    /// <summary>The far ends of the wires taken off with a retired socket.</summary>
    private readonly Dictionary<string, List<Port>> _parked = new(StringComparer.Ordinal);

    /// <summary>The boundaries, as read by a run on the worker thread.</summary>
    private (VisionNode Boundary, Port Socket)[] _snapshot = [];

    public SubGraphNode()
    {
        Attach(new VisionGraph());

        // One way in and one way out, which is what most sub-graphs need.
        // More are added from the toolbox, inside.
        _inner.Add(new GraphInputNode { Name = "In", X = 0, Y = 60 });
        _inner.Add(new GraphOutputNode { Name = "Out", X = 420, Y = 60 });
    }

    public VisionGraph Inner => _inner;

    /// <summary>
    /// A bypassed sub-graph passes its first input to the first output of
    /// the same type, if there is one - forwarding an image into a number
    /// socket would be worse than producing nothing.
    /// </summary>
    public override OutputPort? PrimaryOutput =>
        PrimaryInput is { } input
            ? Outputs.FirstOrDefault(output => output.DataType == input.DataType)
            : null;

    public void Replace(VisionGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        foreach (var boundary in _sockets.Keys.ToArray())
            Retire(boundary);

        _retired.Clear();
        _parked.Clear();

        _scheduler?.Dispose();
        _scheduler = null;

        _inner.StructureChanged -= OnInnerStructureChanged;
        _inner.NodeInvalidated -= OnInnerInvalidated;

        Attach(graph);
        Invalidate();
    }

    public void RenewIds()
    {
        foreach (var node in _inner.Nodes)
        {
            node.Id = Guid.NewGuid().ToString("N");

            if (_sockets.TryGetValue(node, out var socket))
                socket.Rename(node.Id);

            if (node is ISubGraphHost nested)
                nested.RenewIds();
        }

        _retired.Clear();
        _parked.Clear();
    }

    public void Plan(SynchronizationContext? uiContext)
    {
        _scheduler ??= new GraphScheduler(_inner, uiContext);
        _scheduler.Plan();
    }

    public bool TryReadNested(OutputPort port, out object? value)
    {
        value = null;

        if (_scheduler is null)
            return false;

        if (ReferenceEquals(port.Node.Graph, _inner))
        {
            value = _scheduler.GetOutput(port);
            return true;
        }

        return _scheduler.TryReadNested(port, out value);
    }

    public void ReleaseResults()
    {
        _scheduler?.Dispose();
        _scheduler = null;
    }

    /// <summary>
    /// Matches the sockets to the boundaries inside: their number, names and
    /// types. Called by the editor after it changes the wiring outside.
    /// </summary>
    public bool Fit() => Sync(raise: false);

    public InputPort? EnsureInput(string name) => FindInput(name);

    protected override void OnEvaluate(EvalContext context)
    {
        var scheduler = _scheduler ??= new GraphScheduler(_inner, uiContext: null);
        var boundaries = _snapshot;

        foreach (var (boundary, socket) in boundaries)
        {
            if (boundary is not GraphInputNode input || socket is not InputPort outside)
                continue;

            var value = context.Get<object>(outside);

            // A new value re-runs what depends on it inside. The same one -
            // because only something inside changed - re-runs nothing, so a
            // parameter dragged inside costs only what it affects.
            if (!ReferenceEquals(input.Value, value))
            {
                input.Value = value;
                scheduler.Invalidate(input);
            }
        }

        var result = scheduler.RunSynchronously(context.CancellationToken, context.IsInspection);

        if (result.Error is not null)
            throw new InvalidOperationException(result.Error);

        if (result.Cancelled)
            throw new OperationCanceledException(context.CancellationToken);

        foreach (var (boundary, socket) in boundaries)
        {
            if (boundary is GraphOutputNode output && socket is OutputPort outside)
                context.Forward(outside, scheduler.GetInput(output.ValueIn));
        }

        if (result.Failed > 0)
        {
            context.Warn(
                $"{result.Failed} node{(result.Failed == 1 ? "" : "s")} inside failed. " +
                "Open the sub-graph to see which.");
        }
    }

    // ---- keeping the sockets in step with the inside -------------------------

    private void Attach(VisionGraph graph)
    {
        _inner = graph;
        _inner.StructureChanged += OnInnerStructureChanged;
        _inner.NodeInvalidated += OnInnerInvalidated;
        Sync(raise: true);
    }

    private void OnInnerStructureChanged(object? sender, EventArgs e)
    {
        Sync(raise: true);

        // Something was added, removed or rewired inside, so what this
        // produces may have changed.
        Invalidate();
    }

    private void OnInnerInvalidated(object? sender, VisionNode node) => Invalidate();

    private void OnBoundaryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Name) && sender is VisionNode boundary &&
            _sockets.TryGetValue(boundary, out var socket) && socket.DisplayName != boundary.Name)
        {
            socket.Retitle(boundary.Name);
            OnPortsChanged();
        }
    }

    /// <returns>True when a socket was added, removed, renamed or retyped.</returns>
    private bool Sync(bool raise)
    {
        var changed = false;

        foreach (var boundary in _sockets.Keys.ToArray())
        {
            if (!ReferenceEquals(boundary.Graph, _inner))
            {
                Retire(boundary);
                changed = true;
            }
        }

        foreach (var boundary in _inner.Nodes.Where(IsBoundary).ToArray())
        {
            if (!_sockets.ContainsKey(boundary))
            {
                Open(boundary);
                changed = true;
            }
        }

        foreach (var (boundary, socket) in _sockets)
        {
            if (socket.DisplayName != boundary.Name)
            {
                socket.Retitle(boundary.Name);
                changed = true;
            }

            changed |= Retype(boundary, socket);
        }

        _snapshot = _sockets.Select(pair => (pair.Key, pair.Value)).ToArray();

        if (changed && raise)
            OnPortsChanged();

        return changed;
    }

    private static bool IsBoundary(VisionNode node) => node is GraphInputNode or GraphOutputNode;

    /// <summary>Gives a boundary its socket - the one it had before, if it had one.</summary>
    private void Open(VisionNode boundary)
    {
        Port socket;

        if (_retired.Remove(boundary.Id, out var previous))
        {
            socket = previous;
        }
        else
        {
            socket = boundary is GraphInputNode
                ? new InputPort(boundary.Id, boundary.Name, typeof(object), isRequired: false) { Node = this }
                : new OutputPort(boundary.Id, boundary.Name, typeof(object)) { Node = this };
        }

        if (socket is InputPort input)
            AttachInput(input, int.MaxValue);
        else
            AttachOutput((OutputPort)socket, int.MaxValue);

        _sockets[boundary] = socket;
        SetHosted(boundary, true);
        boundary.PropertyChanged += OnBoundaryPropertyChanged;

        Reconnect(boundary.Id, socket);
    }

    /// <summary>
    /// Takes a boundary's socket off, with its wires - a socket cannot stay
    /// on the node with nothing behind it - and remembers both, so an undo
    /// inside puts back the same socket wired the same way.
    /// </summary>
    private void Retire(VisionNode boundary)
    {
        var socket = _sockets[boundary];

        var connections = socket switch
        {
            InputPort { Connection: { } connection } => [connection],
            OutputPort output => output.Connections.ToArray(),
            _ => Array.Empty<Connection>(),
        };

        _parked[boundary.Id] = connections
            .Select(connection => socket is InputPort ? (Port)connection.Source : connection.Target)
            .ToList();

        foreach (var connection in connections)
            Graph?.Disconnect(connection);

        if (socket is InputPort input)
            RemoveInput(input);
        else
            RemoveOutput((OutputPort)socket);

        _retired[boundary.Id] = socket;
        _sockets.Remove(boundary);
        SetHosted(boundary, false);
        boundary.PropertyChanged -= OnBoundaryPropertyChanged;
    }

    /// <summary>Puts back the wires a retired socket took with it, where nothing has taken their place.</summary>
    private void Reconnect(string boundaryId, Port socket)
    {
        if (!_parked.Remove(boundaryId, out var farEnds) || Graph is not { } graph)
            return;

        foreach (var far in farEnds)
        {
            switch (socket, far)
            {
                case (InputPort input, OutputPort source) when !input.IsConnected:
                    graph.TryConnect(source, input);
                    break;

                // An input elsewhere that has been wired to something else
                // since keeps that: putting this back would take it away.
                case (OutputPort output, InputPort target) when !target.IsConnected:
                    graph.TryConnect(output, target);
                    break;
            }
        }
    }

    /// <summary>
    /// Settles a boundary's type: "any" while nothing is wired to it on
    /// either side, otherwise the type of the first wire, kept until both
    /// sides are clear again.
    /// </summary>
    private static bool Retype(VisionNode boundary, Port socket)
    {
        var inside = InsidePort(boundary);
        var current = socket.DataType;

        Type wanted;

        if (!socket.IsConnected && !inside.IsConnected)
            wanted = typeof(object);
        else if (current != typeof(object))
            wanted = current;
        else
            wanted = FarType(inside) ?? FarType(socket) ?? typeof(object);

        if (wanted == current && inside.DataType == current)
            return false;

        socket.Retype(wanted);

        if (inside.DataType != wanted)
        {
            inside.Retype(wanted);

            // The boundary's own socket changed, which the canvas showing the
            // inside has to redraw.
            boundary.RaisePortsChanged();
        }

        return true;
    }

    private static Port InsidePort(VisionNode boundary) => boundary switch
    {
        GraphInputNode input => input.ValueOut,
        GraphOutputNode output => output.ValueIn,
        _ => throw new ArgumentException("Not a boundary.", nameof(boundary)),
    };

    /// <summary>The type at the other end of a port's first wire, unless that is "any" too.</summary>
    private static Type? FarType(Port port)
    {
        var far = port switch
        {
            InputPort { Connection: { } connection } => connection.Source.DataType,
            OutputPort { Connections: [var first, ..] } => first.Target.DataType,
            _ => null,
        };

        return far == typeof(object) ? null : far;
    }

    private static void SetHosted(VisionNode boundary, bool hosted)
    {
        switch (boundary)
        {
            case GraphInputNode input:
                input.IsHosted = hosted;
                if (!hosted)
                    input.Value = null;
                break;

            case GraphOutputNode output:
                output.IsHosted = hosted;
                break;
        }
    }
}
