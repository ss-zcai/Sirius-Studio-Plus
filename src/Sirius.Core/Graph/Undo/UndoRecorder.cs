using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;

namespace Sirius.Core.Graph.Undo;

/// <summary>
/// Watches a graph and records every undoable property change on its nodes.
/// </summary>
/// <remarks>
/// <para>
/// The alternative was a command class per parameter, pushed from wherever
/// that parameter is edited. The roadmap called this the mechanism's
/// weakness before it was built, and it is: the one edit somebody forgets to
/// wire up is not an error anywhere, it is a step undo silently skips, and
/// it will be found by a user rather than by a test.
/// </para>
/// <para>
/// So nothing is wired up. A node announces a change before and after making
/// it - <see cref="INotifyPropertyChanging"/> as well as
/// <see cref="INotifyPropertyChanged"/>, both of which every node already
/// raises - and the before and after are read off the property either side.
/// A parameter added to a node tomorrow is undoable the day it ships.
/// </para>
/// <para>
/// What is undoable is an allow-list rather than a deny-list, which is the
/// one deliberate asymmetry here. The engine writes Status, StatusMessage
/// and timings on every pass; a deny-list that forgot a new one of those
/// would fill the stack with evaluation noise, and the operator would find
/// Undo stepping backwards through things they never did. An allow-list that
/// forgets a parameter loses one step and
/// <c>UndoRecorderTests.Everything_saved_is_undoable</c> fails.
/// </para>
/// </remarks>
public sealed class UndoRecorder : IDisposable
{
    /// <summary>
    /// The node's own properties that are the operator's work rather than
    /// the engine's. Parameters come from the descriptor and are not listed.
    /// </summary>
    private static readonly string[] OwnProperties =
    [
        nameof(VisionNode.Name),
        nameof(VisionNode.Enabled),
        nameof(VisionNode.X),
        nameof(VisionNode.Y),
        nameof(VisionNode.Accent),
        nameof(VisionNode.Measurements),
    ];

    /// <summary>
    /// The ones that change only how the graph looks, so undoing them must
    /// not re-run the recipe.
    /// </summary>
    private static readonly HashSet<string> Cosmetic = new(StringComparer.Ordinal)
    {
        nameof(VisionNode.Name),
        nameof(VisionNode.X),
        nameof(VisionNode.Y),
        nameof(VisionNode.Accent),
        nameof(VisionNode.Measurements),
    };

    private readonly VisionGraph _graph;
    private readonly UndoStack _stack;
    private readonly Dictionary<VisionNode, Dictionary<string, NodeProperty>> _watched = [];

    private VisionNode? _pendingNode;
    private NodeProperty? _pendingProperty;
    private object? _pendingBefore;

    public UndoRecorder(VisionGraph graph, UndoStack stack)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _stack = stack ?? throw new ArgumentNullException(nameof(stack));

        ((INotifyCollectionChanged)_graph.Nodes).CollectionChanged += OnNodesChanged;

        foreach (var node in _graph.Nodes)
            Attach(node);
    }

    /// <summary>
    /// Stops recording, for the span of the returned scope.
    /// </summary>
    /// <remarks>
    /// For loading a recipe, which sets every property on every node and is
    /// not a hundred and forty edits.
    /// </remarks>
    public IDisposable Pause()
    {
        IsPaused = true;
        return new Resume(this);
    }

    public bool IsPaused { get; private set; }

    /// <summary>
    /// Which properties of a node this records, by name.
    /// </summary>
    /// <remarks>
    /// Public so a test can assert it against what the serialiser writes:
    /// anything saved with the recipe is work, and work that cannot be
    /// undone is the gap this is here to close.
    /// </remarks>
    public static IReadOnlyCollection<string> UndoableProperties(VisionNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return OwnProperties
            .Concat(node.Descriptor.Parameters.Select(parameter => parameter.Name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public void Dispose()
    {
        ((INotifyCollectionChanged)_graph.Nodes).CollectionChanged -= OnNodesChanged;

        foreach (var node in _watched.Keys.ToArray())
            Detach(node);
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var node in e.OldItems?.OfType<VisionNode>() ?? [])
            Detach(node);

        foreach (var node in e.NewItems?.OfType<VisionNode>() ?? [])
            Attach(node);

        // A reset reports neither, so the watch list is rebuilt from the graph.
        if (e.Action != NotifyCollectionChangedAction.Reset)
            return;

        foreach (var node in _watched.Keys.Except(_graph.Nodes).ToArray())
            Detach(node);

        foreach (var node in _graph.Nodes)
            Attach(node);
    }

    private void Attach(VisionNode node)
    {
        if (_watched.ContainsKey(node))
            return;

        var type = node.GetType();
        var properties = new Dictionary<string, NodeProperty>(StringComparer.Ordinal);

        foreach (var name in OwnProperties)
        {
            if (type.GetProperty(name) is { CanRead: true, CanWrite: true } property)
                properties[name] = NodeProperty.Of(property);
        }

        // Through the descriptor rather than by reflecting on the class: a
        // custom node's settings live on the plugin it wraps, and the
        // descriptor is the one thing that knows where. Only writable
        // properties become parameters, so each can be put back.
        foreach (var parameter in node.Descriptor.Parameters)
            properties.TryAdd(parameter.Name, NodeProperty.Of(parameter));

        _watched[node] = properties;

        node.PropertyChanging += OnPropertyChanging;
        node.PropertyChanged += OnPropertyChanged;
    }

    private void Detach(VisionNode node)
    {
        if (!_watched.Remove(node))
            return;

        node.PropertyChanging -= OnPropertyChanging;
        node.PropertyChanged -= OnPropertyChanged;

        if (ReferenceEquals(_pendingNode, node))
            ClearPending();
    }

    private void OnPropertyChanging(object? sender, PropertyChangingEventArgs e)
    {
        ClearPending();

        if (!TryResolve(sender, e.PropertyName, out var node, out var property))
            return;

        _pendingNode = node;
        _pendingProperty = property;
        _pendingBefore = property.GetValue(node);
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_pendingNode is null || _pendingProperty is null)
            return;

        if (!ReferenceEquals(sender, _pendingNode) || _pendingProperty.Name != e.PropertyName)
        {
            // A property raised Changed without a matching Changing, or two
            // changes interleaved. Either way the pair cannot be trusted.
            ClearPending();
            return;
        }

        var node = _pendingNode;
        var property = _pendingProperty;
        var before = _pendingBefore;

        ClearPending();

        var after = property.GetValue(node);

        if (Equals(before, after))
            return;

        _stack.Record(new PropertyEdit(
            node,
            property,
            before,
            after,
            LabelFor(node, property.Name),
            affectsResults: !Cosmetic.Contains(property.Name)));
    }

    private bool TryResolve(
        object? sender,
        string? propertyName,
        out VisionNode node,
        out NodeProperty property)
    {
        node = null!;
        property = null!;

        if (IsPaused || _stack.IsApplying || propertyName is null)
            return false;

        if (sender is not VisionNode candidate ||
            !_watched.TryGetValue(candidate, out var properties) ||
            !properties.TryGetValue(propertyName, out var found))
        {
            return false;
        }

        node = candidate;
        property = found;
        return true;
    }

    /// <summary>
    /// What the Edit menu says. Named for what the operator sees - the
    /// parameter's display name, and the node's own name - rather than for
    /// the property.
    /// </summary>
    private static string LabelFor(VisionNode node, string propertyName)
    {
        if (propertyName == nameof(VisionNode.Name))
            return "Rename";

        if (propertyName is nameof(VisionNode.X) or nameof(VisionNode.Y))
            return $"Move {node.Name}";

        if (propertyName == nameof(VisionNode.Enabled))
            return node.Enabled ? $"Enable {node.Name}" : $"Bypass {node.Name}";

        if (propertyName == nameof(VisionNode.Accent))
            return $"Colour {node.Name}";

        if (propertyName == nameof(VisionNode.Measurements))
            return "Measure";

        var parameter = node.Descriptor.Parameters
            .FirstOrDefault(candidate => candidate.Name == propertyName);

        return $"Change {parameter?.DisplayName ?? propertyName}";
    }

    private void ClearPending()
    {
        _pendingNode = null;
        _pendingProperty = null;
        _pendingBefore = null;
    }

    private sealed class Resume : IDisposable
    {
        private readonly UndoRecorder _recorder;
        private bool _closed;

        public Resume(UndoRecorder recorder) => _recorder = recorder;

        public void Dispose()
        {
            if (_closed)
                return;

            _closed = true;
            _recorder.IsPaused = false;
            _recorder.ClearPending();
        }
    }
}
