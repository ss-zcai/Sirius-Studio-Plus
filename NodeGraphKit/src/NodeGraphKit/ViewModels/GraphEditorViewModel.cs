using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NodeGraphKit.Model;
using NodeGraphKit.Serialization;

namespace NodeGraphKit.ViewModels;

/// <summary>
/// Everything the canvas edits: the graph, its view models, the selection,
/// the clipboard and the undo history. One per open graph.
/// </summary>
/// <remarks>
/// <para>
/// Bind <see cref="Controls.GraphEditorView"/> and
/// <see cref="Controls.PropertiesView"/> to one of these. The host adds
/// file handling and whatever "run" means to it; see the demo.
/// </para>
/// <para>
/// Undo restores a snapshot, which rebuilds the graph's nodes as new
/// objects with the same ids. Hold on to a node by <see cref="GraphNode.Id"/>,
/// not by reference, across an undo.
/// </para>
/// </remarks>
public sealed class GraphEditorViewModel : ObservableObject
{
    /// <summary>The clipboard format copied nodes travel in.</summary>
    public const string ClipboardFormat = "NodeGraphKit.Nodes";

    private const double PasteStep = 30;

    private readonly Dictionary<GraphNode, NodeViewModel> _nodeLookup = [];
    private readonly Dictionary<GraphPort, ConnectorViewModel> _connectorLookup = [];

    private NodeViewModel? _selectedNode;
    private bool _restoring;
    private int _suspended;
    private bool _movePending;
    private bool _clipboardHasNodes;
    private string? _lastCopy;
    private int _pasteCount;

    public GraphEditorViewModel(NodeCatalog catalog)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

        StartConnectionCommand = new RelayCommand<object>(StartConnection);
        CompleteConnectionCommand = new RelayCommand<object>(CompleteConnection);
        DisconnectCommand = new RelayCommand<object>(Disconnect);
        RemoveConnectionCommand = new RelayCommand<object>(RemoveConnection);

        DeleteSelectionCommand = new RelayCommand(DeleteSelection, () => HasSelection);
        SelectAllCommand = new RelayCommand(SelectAll, () => Nodes.Count > 0);
        CopyCommand = new RelayCommand(Copy, () => HasNodeSelection);
        CutCommand = new RelayCommand(Cut, () => HasNodeSelection);
        PasteCommand = new RelayCommand(Paste, () => _clipboardHasNodes || _lastCopy is not null);
        DuplicateCommand = new RelayCommand(Duplicate, () => HasNodeSelection);
        UndoCommand = new RelayCommand(Undo, () => History.CanUndo);
        RedoCommand = new RelayCommand(Redo, () => History.CanRedo);
        SetAccentCommand = new RelayCommand<NodeAccent>(SetAccent);

        SelectedItems.CollectionChanged += (_, _) => SyncSelectionFromList();
        SelectedConnections.CollectionChanged += (_, _) => SelectionChanged();
        History.Changed += (_, _) => OnHistoryChanged();

        History.Reset(GraphSerializer.Save(Graph));
    }

    public NodeCatalog Catalog { get; }

    public NodeGraph Graph { get; } = new();

    public UndoHistory History { get; } = new();

    public ObservableCollection<NodeViewModel> Nodes { get; } = [];

    public ObservableCollection<ConnectionViewModel> Connections { get; } = [];

    /// <summary>Bound to the canvas's selection.</summary>
    public ObservableCollection<object> SelectedItems { get; } = [];

    public ObservableCollection<object> SelectedConnections { get; } = [];

    public PendingConnectionViewModel PendingConnection { get; } = new();

    public IRelayCommand<object> StartConnectionCommand { get; }

    public IRelayCommand<object> CompleteConnectionCommand { get; }

    public IRelayCommand<object> DisconnectCommand { get; }

    public IRelayCommand<object> RemoveConnectionCommand { get; }

    public IRelayCommand DeleteSelectionCommand { get; }

    public IRelayCommand SelectAllCommand { get; }

    public IRelayCommand CopyCommand { get; }

    public IRelayCommand CutCommand { get; }

    public IRelayCommand PasteCommand { get; }

    public IRelayCommand DuplicateCommand { get; }

    public IRelayCommand UndoCommand { get; }

    public IRelayCommand RedoCommand { get; }

    /// <summary>Colours every selected node.</summary>
    public IRelayCommand<NodeAccent> SetAccentCommand { get; }

    /// <summary>The node the properties panel shows: the last one selected.</summary>
    public NodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set => SetProperty(ref _selectedNode, value);
    }

    public bool HasNodeSelection => Nodes.Any(node => node.IsSelected);

    public bool HasSelection => HasNodeSelection || Connections.Any(c => c.IsSelected) || SelectedConnections.Count > 0;

    /// <summary>"Undo Delete Add" - for the Edit menu and the toolbar tooltip.</summary>
    public string UndoText => History.UndoText;

    public string RedoText => History.RedoText;

    public bool IsDirty => History.IsDirty;

    /// <summary>
    /// Raised after every edit, undo and load: the moment for a host to run
    /// the graph again or refresh anything it shows about it.
    /// </summary>
    public event EventHandler? GraphChanged;

    /// <summary>
    /// Raised after <see cref="New"/> and <see cref="Load"/>: a different
    /// graph, which the canvas frames so it is not hunted for by scrolling.
    /// </summary>
    public event EventHandler? GraphReplaced;

    // ---- whole graph -------------------------------------------------------

    /// <summary>Empties the canvas and the history.</summary>
    public void New()
    {
        Graph.Clear();
        RebuildCore();
        History.Reset(GraphSerializer.Save(Graph));
        GraphReplaced?.Invoke(this, EventArgs.Empty);
        GraphChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the graph with a saved one, and starts its history afresh.</summary>
    /// <returns>Whatever had to be left out; see <see cref="GraphSerializer.Load"/>.</returns>
    public IReadOnlyList<string> Load(string json)
    {
        var replacement = new NodeGraph { AllowCycles = Graph.AllowCycles };
        var result = GraphSerializer.Load(json, Catalog, replacement);

        // Read into a spare graph first, so a file that is not a graph
        // throws before the one on screen is cleared.
        Graph.Clear();
        GraphSerializer.Load(GraphSerializer.Save(replacement), Catalog, Graph);

        RebuildCore();
        History.Reset(GraphSerializer.Save(Graph));
        GraphReplaced?.Invoke(this, EventArgs.Empty);
        GraphChanged?.Invoke(this, EventArgs.Empty);
        return result.Warnings;
    }

    /// <summary>The graph as JSON. Call <see cref="UndoHistory.MarkSaved"/> once it is written.</summary>
    public string Save() => GraphSerializer.Save(Graph);

    /// <summary>Adds a node of this type, selected, at a point in graph space.</summary>
    public NodeViewModel AddNode(NodeDefinition definition, Point location)
    {
        var node = new GraphNode(definition) { X = location.X, Y = location.Y };

        return Edit($"Add {definition.DisplayName}", () =>
        {
            Graph.Add(node);
            var viewModel = Track(node);
            Nodes.Add(viewModel);
            SelectOnly([viewModel]);
            return viewModel;
        });
    }

    public NodeViewModel? Find(GraphNode node) => _nodeLookup.GetValueOrDefault(node);

    /// <summary>Selects exactly these nodes, and shows the first on the properties panel.</summary>
    public void SelectOnly(IReadOnlyCollection<NodeViewModel> nodes)
    {
        foreach (var connection in Connections)
            connection.IsSelected = false;

        SelectedConnections.Clear();

        foreach (var node in Nodes)
            node.IsSelected = nodes.Contains(node);

        SelectedNode = nodes.FirstOrDefault();
    }

    // ---- wiring ------------------------------------------------------------

    private void StartConnection(object? parameter)
    {
        if (parameter is ConnectorViewModel source)
            PendingConnection.Source = source;
    }

    private void CompleteConnection(object? parameter)
    {
        PendingConnection.Source = null;

        // Nodify passes (source, target) in the order of the gesture, so a
        // wire dragged from an input to an output arrives reversed.
        if (parameter is not ITuple { Length: >= 2 } tuple ||
            tuple[0] is not ConnectorViewModel first ||
            tuple[1] is not ConnectorViewModel second)
        {
            return;
        }

        var (output, input) = first.IsInput ? (second, first) : (first, second);

        if (Graph.Check(output.Port, input.Port) is not null)
            return;

        Edit($"Connect {output.Node.Title}", () =>
        {
            var result = Graph.Connect(output.Port, input.Port);

            if (result.Replaced is { } replaced)
                Untrack(replaced);

            if (result.Connection is { } connection && Connections.All(c => c.Connection != connection))
                Connections.Add(Track(connection));
        });
    }

    /// <summary>Alt+click on a socket: pulls every wire off it.</summary>
    private void Disconnect(object? parameter)
    {
        if (parameter is not ConnectorViewModel connector)
            return;

        var wires = Graph.ConnectionsOf(connector.Port).ToArray();
        if (wires.Length == 0)
            return;

        Edit("Disconnect", () =>
        {
            foreach (var wire in wires)
            {
                Graph.Disconnect(wire);
                Untrack(wire);
            }
        });
    }

    private void RemoveConnection(object? parameter)
    {
        if (parameter is not ConnectionViewModel viewModel)
            return;

        Edit("Delete Connection", () =>
        {
            Graph.Disconnect(viewModel.Connection);
            Untrack(viewModel.Connection);
        });
    }

    // ---- selection commands ------------------------------------------------

    private void DeleteSelection()
    {
        var nodes = Nodes.Where(n => n.IsSelected).ToArray();
        var wires = Connections
            .Where(c => c.IsSelected)
            .Concat(SelectedConnections.OfType<ConnectionViewModel>())
            .Distinct()
            .ToArray();

        var label = nodes.Length switch
        {
            1 => $"Delete {nodes[0].Title}",
            > 1 => $"Delete {nodes.Length} nodes",
            _ => "Delete Connection",
        };

        Edit(label, () =>
        {
            foreach (var wire in wires)
            {
                Graph.Disconnect(wire.Connection);
                Untrack(wire.Connection);
            }

            foreach (var node in nodes)
            {
                foreach (var wire in Connections.Where(c => c.Source.Node == node || c.Target.Node == node).ToArray())
                    Untrack(wire.Connection);

                Graph.Remove(node.Node);
                Untrack(node);
            }
        });
    }

    private void SelectAll()
    {
        foreach (var node in Nodes)
            node.IsSelected = true;
    }

    private void Copy()
    {
        var json = GraphSerializer.Save(Graph, Nodes.Where(n => n.IsSelected).Select(n => n.Node));
        _lastCopy = json;
        _pasteCount = 0;

        try
        {
            Clipboard.SetData(ClipboardFormat, json);
            _clipboardHasNodes = true;
        }
        catch (COMException)
        {
            // Another process has the clipboard open. The copy is still
            // kept here, so pasting into this window works.
        }

        PasteCommand.NotifyCanExecuteChanged();
    }

    private void Cut()
    {
        Copy();

        var nodes = Nodes.Where(n => n.IsSelected).ToArray();
        Edit(nodes.Length == 1 ? $"Cut {nodes[0].Title}" : $"Cut {nodes.Length} nodes", DeleteSelection);
    }

    private void Paste()
    {
        string? json = null;

        try
        {
            if (Clipboard.ContainsData(ClipboardFormat))
                json = Clipboard.GetData(ClipboardFormat) as string;
        }
        catch (COMException)
        {
        }

        json ??= _lastCopy;
        if (json is null)
            return;

        _pasteCount++;
        Insert(json, "Paste", new Vector(PasteStep * _pasteCount, PasteStep * _pasteCount));
    }

    private void Duplicate()
    {
        var json = GraphSerializer.Save(Graph, Nodes.Where(n => n.IsSelected).Select(n => n.Node));
        Insert(json, "Duplicate", new Vector(PasteStep, PasteStep));
    }

    /// <summary>Adds the nodes in <paramref name="json"/> with new ids, and selects them.</summary>
    private void Insert(string json, string label, Vector offset)
    {
        Edit(label, () =>
        {
            var added = GraphSerializer.Load(json, Catalog, Graph, newIds: true, offset).Nodes;
            var viewModels = added.Select(Track).ToArray();

            foreach (var viewModel in viewModels)
                Nodes.Add(viewModel);

            foreach (var connection in Graph.Connections.Where(c => added.Contains(c.Target.Node)))
                Connections.Add(Track(connection));

            SelectOnly(viewModels);
        });
    }

    private void SetAccent(NodeAccent accent)
    {
        var nodes = Nodes.Where(n => n.IsSelected).ToArray();
        if (nodes.Length == 0)
            return;

        Edit($"Style {NodeAccents.Of(accent).Name}", () =>
        {
            foreach (var node in nodes)
                node.Accent = accent;
        });
    }

    /// <summary>
    /// Re-reads whether the system clipboard holds nodes. Call it when the
    /// window is activated: that is the only way a copy made in another
    /// window arrives, and asking on every command requery would be an OLE
    /// call each time.
    /// </summary>
    public void RefreshClipboardState()
    {
        try
        {
            _clipboardHasNodes = Clipboard.ContainsData(ClipboardFormat);
        }
        catch (COMException)
        {
        }

        PasteCommand.NotifyCanExecuteChanged();
    }

    // ---- undo --------------------------------------------------------------

    private void Undo() => Restore(History.Undo());

    private void Redo() => Restore(History.Redo());

    /// <summary>
    /// Puts back a snapshot. Nodes the step brought back - an undone delete,
    /// a redone add - come back selected, so it is plain what changed;
    /// otherwise the selection is kept, by id, since the view models are new.
    /// </summary>
    private void Restore(string state)
    {
        var before = Graph.Nodes.Select(n => n.Id).ToHashSet();
        var selected = Nodes.Where(n => n.IsSelected).Select(n => n.Node.Id).ToHashSet();
        var current = SelectedNode?.Node.Id;

        _restoring = true;

        try
        {
            Graph.Clear();
            GraphSerializer.Load(state, Catalog, Graph);
            RebuildCore();

            var returned = Nodes.Where(n => !before.Contains(n.Node.Id)).ToArray();

            if (returned.Length > 0)
            {
                SelectOnly(returned);
            }
            else
            {
                foreach (var node in Nodes.Where(n => selected.Contains(n.Node.Id)))
                    node.IsSelected = true;

                SelectedNode = Nodes.FirstOrDefault(n => n.Node.Id == current) ?? Nodes.FirstOrDefault(n => n.IsSelected);
            }
        }
        finally
        {
            _restoring = false;
        }

        GraphChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Runs an edit as one undo step, however many changes it makes.
    /// </summary>
    private T Edit<T>(string label, Func<T> change, string? mergeKey = null)
    {
        _suspended++;

        try
        {
            return change();
        }
        finally
        {
            _suspended--;
            Record(label, mergeKey);
            SelectionChanged();
        }
    }

    private void Edit(string label, Action change, string? mergeKey = null) =>
        Edit<object?>(label, () =>
        {
            change();
            return null;
        }, mergeKey);

    private void Record(string label, string? mergeKey = null)
    {
        if (_restoring || _suspended > 0)
            return;

        History.Record(label, GraphSerializer.Save(Graph), mergeKey);
        GraphChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnHistoryChanged()
    {
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
        OnPropertyChanged(nameof(IsDirty));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    // ---- model changes made outside the editor's own commands ---------------

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not GraphNode node)
            return;

        switch (e.PropertyName)
        {
            case nameof(GraphNode.Name):
                Record($"Rename {node.Name}", $"name:{node.Id}");
                break;

            case nameof(GraphNode.Accent):
                Record($"Style {NodeAccents.Of(node.Accent).Name}");
                break;

            case nameof(GraphNode.Enabled):
                Record(node.Enabled ? $"Enable {node.Name}" : $"Bypass {node.Name}");
                break;

            // A drag moves every selected node, X then Y: one step for the
            // lot, recorded once the burst of changes is over.
            case nameof(GraphNode.X):
            case nameof(GraphNode.Y):
                if (_restoring || _suspended > 0 || _movePending)
                    return;

                _movePending = true;
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    _movePending = false;
                    Record("Move");
                });
                break;
        }
    }

    private void OnParameterChanged(object? sender, string name)
    {
        if (sender is GraphNode node)
            Record($"Change {name}", $"parameter:{node.Id}:{name}");
    }

    // ---- view models -------------------------------------------------------

    private void RebuildCore()
    {
        foreach (var node in Nodes.ToArray())
            Untrack(node);

        SelectedItems.Clear();
        SelectedConnections.Clear();
        Nodes.Clear();
        Connections.Clear();
        _nodeLookup.Clear();
        _connectorLookup.Clear();

        foreach (var node in Graph.Nodes)
            Nodes.Add(Track(node));

        foreach (var connection in Graph.Connections)
            Connections.Add(Track(connection));

        SelectedNode = null;
        SelectionChanged();
    }

    private NodeViewModel Track(GraphNode node)
    {
        var viewModel = new NodeViewModel(node);
        _nodeLookup[node] = viewModel;

        foreach (var connector in viewModel.Inputs.Concat(viewModel.Outputs))
            _connectorLookup[connector.Port] = connector;

        node.PropertyChanged += OnNodeChanged;
        node.ParameterChanged += OnParameterChanged;
        viewModel.PropertyChanged += OnNodeViewModelChanged;
        return viewModel;
    }

    private void Untrack(NodeViewModel viewModel)
    {
        viewModel.Node.PropertyChanged -= OnNodeChanged;
        viewModel.Node.ParameterChanged -= OnParameterChanged;
        viewModel.PropertyChanged -= OnNodeViewModelChanged;

        SelectedItems.Remove(viewModel);
        Nodes.Remove(viewModel);
        _nodeLookup.Remove(viewModel.Node);

        foreach (var connector in viewModel.Inputs.Concat(viewModel.Outputs))
            _connectorLookup.Remove(connector.Port);

        if (SelectedNode == viewModel)
            SelectedNode = Nodes.FirstOrDefault(n => n.IsSelected);
    }

    private ConnectionViewModel Track(GraphConnection connection)
    {
        var viewModel = new ConnectionViewModel(
            connection,
            _connectorLookup[connection.Source],
            _connectorLookup[connection.Target]);

        viewModel.PropertyChanged += OnConnectionViewModelChanged;
        return viewModel;
    }

    private void Untrack(GraphConnection connection)
    {
        if (Connections.FirstOrDefault(c => c.Connection == connection) is not { } viewModel)
            return;

        viewModel.PropertyChanged -= OnConnectionViewModelChanged;
        SelectedConnections.Remove(viewModel);
        Connections.Remove(viewModel);
    }

    private void OnNodeViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NodeViewModel.IsSelected) || sender is not NodeViewModel viewModel)
            return;

        if (viewModel.IsSelected)
            SelectedNode = viewModel;
        else if (ReferenceEquals(SelectedNode, viewModel))
            SelectedNode = Nodes.FirstOrDefault(n => n.IsSelected);

        SelectionChanged();
    }

    private void OnConnectionViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionViewModel.IsSelected))
            SelectionChanged();
    }

    private void SyncSelectionFromList()
    {
        if (SelectedItems.OfType<NodeViewModel>().FirstOrDefault() is { } first)
            SelectedNode = first;
        else if (SelectedItems.Count == 0 && Nodes.All(n => !n.IsSelected))
            SelectedNode = null;

        SelectionChanged();
    }

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(HasNodeSelection));
        OnPropertyChanged(nameof(HasSelection));
        DeleteSelectionCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
    }
}
