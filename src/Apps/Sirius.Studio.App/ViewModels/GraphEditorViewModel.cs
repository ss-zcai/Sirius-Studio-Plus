using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sirius.Core.Graph;
using Sirius.Core.Graph.Undo;
using Sirius.Core.Nodes;

namespace Sirius.Studio.App.ViewModels;

/// <summary>
/// Presentation layer over a <see cref="VisionGraph"/>: keeps canvas view-models
/// in step with the model and turns editor gestures into graph mutations.
/// </summary>
public sealed class GraphEditorViewModel : ObservableObject
{
    private readonly Dictionary<VisionNode, NodeViewModel> _nodeLookup = new();
    private readonly Dictionary<Port, ConnectorViewModel> _connectorLookup = new();

    private readonly UndoStack _undo;

    private NodeViewModel? _selectedNode;

    /// <summary>How many gestures are under way; see <see cref="Gesture"/>.</summary>
    private int _gestures;

    /// <summary>A node's ports changed during a gesture, so the canvas is rebuilt when it ends.</summary>
    private bool _portsStale;

    /// <param name="undo">
    /// Where structural edits are recorded. The editor is where a node is
    /// added, deleted, connected and disconnected, so it is where those four
    /// get their undo - property changes are recorded by watching the nodes
    /// instead, in <see cref="UndoRecorder"/>.
    /// </param>
    public GraphEditorViewModel(VisionGraph graph, UndoStack undo)
    {
        Graph = graph;
        _undo = undo ?? throw new ArgumentNullException(nameof(undo));

        Nodes = new ObservableCollection<NodeViewModel>();
        Connections = new ObservableCollection<ConnectionViewModel>();
        PendingConnection = new PendingConnectionViewModel();
        SelectedItems = new ObservableCollection<object>();
        SelectedConnections = new ObservableCollection<object>();

        // Typed as object: Nodify supplies the endpoints as an untyped
        // (object source, object target) tuple, which will not bind to a
        // RelayCommand with a more specific generic argument.
        StartConnectionCommand = new RelayCommand<object>(StartConnection);
        CompleteConnectionCommand = new RelayCommand<object>(CompleteConnection);
        DisconnectCommand = new RelayCommand<object>(Disconnect);
        RemoveConnectionCommand = new RelayCommand<object>(RemoveConnection);
        DeleteSelectionCommand = new RelayCommand(DeleteSelection, CanDeleteSelection);

        // Subscribed after the commands exist, since these handlers raise
        // CanExecuteChanged on DeleteSelectionCommand.
        //
        // Two independent paths keep SelectedNode current, because which one
        // fires depends on how the editor reports a click: the container's
        // IsSelected binding, or its SelectedItems collection. Both funnel into
        // the same setter, and setting it twice is a no-op.
        SelectedItems.CollectionChanged += (_, _) => SyncSelectionFromList();
        SelectedConnections.CollectionChanged += (_, _) => DeleteSelectionCommand.NotifyCanExecuteChanged();

        Rebuild();
    }

    public VisionGraph Graph { get; }

    public ObservableCollection<NodeViewModel> Nodes { get; }

    public ObservableCollection<ConnectionViewModel> Connections { get; }

    /// <summary>
    /// Bound to the editor's selection. Exposed as a collection because the
    /// canvas supports marquee-selecting several nodes; the inspector follows
    /// the first of them.
    /// </summary>
    public ObservableCollection<object> SelectedItems { get; }

    /// <summary>
    /// Connections currently selected on the canvas. Bound so a wire can be
    /// clicked and removed with Delete, like any other object in the editor.
    /// </summary>
    public ObservableCollection<object> SelectedConnections { get; }

    public PendingConnectionViewModel PendingConnection { get; }

    public IRelayCommand<object> StartConnectionCommand { get; }

    public IRelayCommand<object> CompleteConnectionCommand { get; }

    public IRelayCommand<object> DisconnectCommand { get; }

    public IRelayCommand<object> RemoveConnectionCommand { get; }

    public IRelayCommand DeleteSelectionCommand { get; }

    /// <summary>Drives the inspector panel.</summary>
    public NodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set => SetProperty(ref _selectedNode, value);
    }

    /// <summary>Raised when the graph structure changed in a way that needs re-running.</summary>
    public event EventHandler? GraphChanged;

    public NodeViewModel AddNode(NodeDescriptor descriptor, Point location)
    {
        using var gesture = Gesture();

        var node = descriptor.CreateInstance();
        node.X = location.X;
        node.Y = location.Y;

        Graph.Add(node);
        _undo.Record(new AddNodeEdit(node));

        var viewModel = Track(node);
        Nodes.Add(viewModel);

        // Select the new node on the canvas too, so the inspector and the
        // editor agree about what is current.
        foreach (var other in Nodes)
            other.IsSelected = ReferenceEquals(other, viewModel);

        SelectedNode = viewModel;
        GraphChanged?.Invoke(this, EventArgs.Empty);
        return viewModel;
    }

    public void RemoveNode(NodeViewModel viewModel)
    {
        using var gesture = Gesture();

        // Recorded before the removal, because Graph.Remove drops every wire
        // touching the node and reports none of them - afterwards there is
        // nothing left to read, and the node would come back unconnected.
        var edit = new RemoveNodesEdit(Graph, [viewModel.Node]);

        // Drop wires from the canvas first; Graph.Remove detaches them on the
        // model side but raises no per-connection notification.
        foreach (var connection in Connections
                     .Where(c => c.Source.Node == viewModel || c.Target.Node == viewModel)
                     .ToArray())
        {
            connection.PropertyChanged -= OnConnectionViewModelPropertyChanged;
            SelectedConnections.Remove(connection);
            Connections.Remove(connection);
        }

        Graph.Remove(viewModel.Node);
        _undo.Record(edit);

        Detach(viewModel);
        SelectedItems.Remove(viewModel);
        Nodes.Remove(viewModel);

        foreach (var port in viewModel.Node.Inputs.Cast<Port>().Concat(viewModel.Node.Outputs))
            _connectorLookup.Remove(port);

        _nodeLookup.Remove(viewModel.Node);

        if (SelectedNode == viewModel)
            SelectedNode = null;

        FitPorts();
        GraphChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool CanDeleteSelection() =>
        Nodes.Any(n => n.IsSelected) ||
        Connections.Any(c => c.IsSelected) ||
        SelectedConnections.Count > 0;

    /// <summary>
    /// Deletes whatever is selected. Connections go first: removing a node
    /// already drops the wires attached to it, so deleting them afterwards
    /// would be operating on connections that no longer exist.
    /// </summary>
    private void DeleteSelection()
    {
        using var gesture = Gesture();

        // One step back, however much was selected. The individual removals
        // record themselves; the batch is what makes them one action, which
        // is what deleting a selection is to whoever did it.
        using var batch = _undo.Begin(DeleteLabel());

        var connections = Connections
            .Where(c => c.IsSelected)
            .Concat(SelectedConnections.OfType<ConnectionViewModel>())
            .Distinct()
            .ToArray();

        foreach (var connection in connections)
            RemoveConnectionCore(connection.Connection);

        SelectedConnections.Clear();

        foreach (var selected in Nodes.Where(n => n.IsSelected).ToArray())
            RemoveNode(selected);

        if (connections.Length > 0)
            GraphChanged?.Invoke(this, EventArgs.Empty);

        DeleteSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>What the Edit menu will offer to undo.</summary>
    private string DeleteLabel()
    {
        var nodes = Nodes.Count(n => n.IsSelected);

        if (nodes == 1)
            return $"Delete {Nodes.First(n => n.IsSelected).Title}";

        return nodes > 1 ? $"Delete {nodes} nodes" : "Delete";
    }

    private void StartConnection(object? parameter)
    {
        if (parameter is not ConnectorViewModel source)
            return;

        PendingConnection.Source = source;
        PendingConnection.IsValid = true;
        PendingConnection.ValidationMessage = null;
    }

    private void CompleteConnection(object? parameter)
    {
        using var gesture = Gesture();

        PendingConnection.Source = null;

        if (!TryUnpackEndpoints(parameter, out var first, out var second))
            return;

        // Nodify reports the drag in gesture order, so a wire dragged from an
        // input to an output arrives reversed.
        var (output, input) = first.IsInput
            ? (second.Port as OutputPort, first.Port as InputPort)
            : (first.Port as OutputPort, second.Port as InputPort);

        if (output is null || input is null)
        {
            PendingConnection.ValidationMessage = "Connect an output to an input.";
            return;
        }

        // An input holds one wire; replacing it must also remove the old line.
        var replaced = input.Connection;

        var result = Graph.TryConnect(output, input);
        if (!result.Success)
        {
            PendingConnection.IsValid = false;
            PendingConnection.ValidationMessage = result.Message;
            return;
        }

        // Carries what this displaced, if anything: an input holds one
        // wire, so landing on a taken input silently replaces what was
        // there, and undo has to put that back rather than just removing
        // the new one.
        _undo.Record(new ConnectEdit(output, input, replaced?.Source));

        if (replaced is not null)
        {
            var stale = Connections.FirstOrDefault(c => c.Connection == replaced);
            if (stale is not null)
                Connections.Remove(stale);
        }

        TrackConnection(new ConnectionViewModel(
            result.Connection!,
            ResolveConnector(output),
            ResolveConnector(input)));

        FitPorts();
        GraphChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reads Nodify's <c>(object source, object target)</c> payload. Written
    /// reflectively over <see cref="ITuple"/> so it does not depend on the
    /// exact generic arguments Nodify constructs the tuple with.
    /// </summary>
    private static bool TryUnpackEndpoints(
        object? parameter,
        [NotNullWhen(true)] out ConnectorViewModel? source,
        [NotNullWhen(true)] out ConnectorViewModel? target)
    {
        source = null;
        target = null;

        if (parameter is not ITuple tuple || tuple.Length < 2)
            return false;

        source = tuple[0] as ConnectorViewModel;
        target = tuple[1] as ConnectorViewModel;
        return source is not null && target is not null;
    }

    private void Disconnect(object? parameter)
    {
        using var gesture = Gesture();

        if (parameter is not ConnectorViewModel connector)
            return;

        var affected = connector.Port switch
        {
            InputPort input when input.Connection is not null => new[] { input.Connection },
            OutputPort output => output.Connections.ToArray(),
            _ => Array.Empty<Connection>(),
        };

        // Pulling every wire off an output is one gesture, so it is one
        // step back even when it took six wires with it.
        using (_undo.Begin(affected.Length == 1 ? "Disconnect" : $"Disconnect {affected.Length} wires"))
        {
            foreach (var connection in affected)
                RemoveConnectionCore(connection);
        }

        if (affected.Length > 0)
        {
            FitPorts();
            GraphChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RemoveConnection(object? parameter)
    {
        using var gesture = Gesture();

        if (parameter is not ConnectionViewModel viewModel)
            return;

        RemoveConnectionCore(viewModel.Connection);
        FitPorts();
        GraphChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveConnectionCore(Connection connection)
    {
        var edit = new DisconnectEdit([connection]);

        Graph.Disconnect(connection);
        _undo.Record(edit);

        var viewModel = Connections.FirstOrDefault(c => c.Connection == connection);
        if (viewModel is null)
            return;

        viewModel.PropertyChanged -= OnConnectionViewModelPropertyChanged;
        SelectedConnections.Remove(viewModel);
        Connections.Remove(viewModel);
    }

    /// <summary>Registers a connection view-model and starts tracking selection.</summary>
    private ConnectionViewModel TrackConnection(ConnectionViewModel viewModel)
    {
        viewModel.PropertyChanged += OnConnectionViewModelPropertyChanged;
        Connections.Add(viewModel);
        return viewModel;
    }

    private void OnConnectionViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionViewModel.IsSelected))
            DeleteSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Lets every growing node resize itself, and rebuilds the canvas if any
    /// of them did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called after anything that changes the wiring. A node with growing
    /// inputs keeps one spare socket, so connecting to the last one has to
    /// produce another - and the canvas only learns a node has new ports by
    /// reading it again.
    /// </para>
    /// <para>
    /// Asked of every node rather than only the one just wired, because
    /// removing a wire shrinks a node at the other end of the graph from
    /// where the change happened.
    /// </para>
    /// </remarks>
    public void FitPorts()
    {
        var changed = false;

        foreach (var node in Graph.Nodes.OfType<IGrowingPorts>().ToArray())
            changed |= node.Fit();

        if (changed)
            Resync();
    }

    /// <summary>Rebuilds all view-models from the model, e.g. after a file load.</summary>
    public void Rebuild()
    {
        foreach (var node in Graph.Nodes.OfType<IGrowingPorts>().ToArray())
            node.Fit();

        RebuildCore();
        SelectedNode = Nodes.FirstOrDefault();
    }

    /// <summary>
    /// Rebuilds after an undo or redo, keeping the selection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The canvas collections are built incrementally as the operator works,
    /// so after an undo they describe a graph that no longer exists. Replaying
    /// each edit's effect on them in reverse is possible and is the version
    /// that goes wrong quietly; reading the graph back is the version that
    /// cannot disagree with it.
    /// </para>
    /// <para>
    /// Selection is restored by node id rather than by instance, because the
    /// view-models are new ones. Without it, undoing a nudge would empty the
    /// properties panel - the panel would be right about the graph and wrong
    /// about what the operator was doing.
    /// </para>
    /// </remarks>
    public void Resync()
    {
        var selected = Nodes
            .Where(node => node.IsSelected)
            .Select(node => node.Node.Id)
            .ToHashSet(StringComparer.Ordinal);

        var current = SelectedNode?.Node.Id;

        RebuildCore();

        foreach (var node in Nodes.Where(node => selected.Contains(node.Node.Id)))
            node.IsSelected = true;

        SelectedNode =
            Nodes.FirstOrDefault(node => node.Node.Id == current) ??
            Nodes.FirstOrDefault(node => node.IsSelected);

        DeleteSelectionCommand.NotifyCanExecuteChanged();
    }

    private void RebuildCore()
    {
        foreach (var existing in Nodes)
            Detach(existing);

        foreach (var existing in Connections)
            existing.PropertyChanged -= OnConnectionViewModelPropertyChanged;

        SelectedItems.Clear();
        SelectedConnections.Clear();
        Nodes.Clear();
        Connections.Clear();
        _nodeLookup.Clear();
        _connectorLookup.Clear();

        foreach (var node in Graph.Nodes)
            Nodes.Add(Track(node));

        foreach (var connection in Graph.Connections)
        {
            TrackConnection(new ConnectionViewModel(
                connection,
                ResolveConnector(connection.Source),
                ResolveConnector(connection.Target)));
        }
    }

    private NodeViewModel Track(VisionNode node)
    {
        var viewModel = new NodeViewModel(node);
        _nodeLookup[node] = viewModel;

        foreach (var connector in viewModel.Inputs.Concat(viewModel.Outputs))
            _connectorLookup[connector.Port] = connector;

        viewModel.PropertyChanged += OnNodeViewModelPropertyChanged;
        node.PortsChanged += OnNodePortsChanged;
        return viewModel;
    }

    private void Detach(NodeViewModel viewModel)
    {
        viewModel.PropertyChanged -= OnNodeViewModelPropertyChanged;
        viewModel.Node.PortsChanged -= OnNodePortsChanged;
    }

    /// <summary>
    /// A node changed its own sockets - a sub-graph whose inside was edited
    /// in another tab - so the canvas reads it again.
    /// </summary>
    /// <remarks>
    /// Not mid-gesture. Wiring a boundary inside a sub-graph retypes it,
    /// which arrives here while <see cref="CompleteConnection"/> is still
    /// adding the wire's line; rebuilding then would draw the wire twice.
    /// The gesture rebuilds once it has finished instead.
    /// </remarks>
    private void OnNodePortsChanged(object? sender, EventArgs e)
    {
        if (_gestures > 0)
        {
            _portsStale = true;
            return;
        }

        Resync();
    }

    /// <summary>
    /// Marks a change made from the canvas, for the length of a
    /// <c>using</c>.
    /// </summary>
    private IDisposable Gesture()
    {
        _gestures++;
        return new GestureScope(this);
    }

    private sealed class GestureScope(GraphEditorViewModel editor) : IDisposable
    {
        private bool _ended;

        public void Dispose()
        {
            if (_ended)
                return;

            _ended = true;

            if (--editor._gestures > 0 || !editor._portsStale)
                return;

            editor._portsStale = false;
            editor.Resync();
        }
    }

    /// <summary>
    /// Mirrors a container's selection state onto <see cref="SelectedNode"/>.
    /// This is what makes clicking a node populate the inspector.
    /// </summary>
    private void OnNodeViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NodeViewModel.IsSelected) || sender is not NodeViewModel viewModel)
            return;

        if (viewModel.IsSelected)
        {
            SelectedNode = viewModel;
        }
        else if (ReferenceEquals(SelectedNode, viewModel))
        {
            // Deselected: fall back to whatever else is still selected rather
            // than blanking the inspector during a multi-select change.
            SelectedNode = Nodes.FirstOrDefault(n => n.IsSelected);
        }

        DeleteSelectionCommand.NotifyCanExecuteChanged();
    }

    private void SyncSelectionFromList()
    {
        var first = SelectedItems.OfType<NodeViewModel>().FirstOrDefault();

        if (first is not null)
            SelectedNode = first;
        else if (SelectedItems.Count == 0 && Nodes.All(n => !n.IsSelected))
            SelectedNode = null;

        DeleteSelectionCommand.NotifyCanExecuteChanged();
    }

    private ConnectorViewModel ResolveConnector(Port port) =>
        _connectorLookup.TryGetValue(port, out var connector)
            ? connector
            : throw new InvalidOperationException($"No connector view-model for port '{port}'.");
}
