using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Sirius.Core.Graph;
using Sirius.Core.Graph.Undo;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Core.Serialization;
using Sirius.Studio.App.Imaging;
using Sirius.Vision;
using Sirius.Vision.Nodes;

namespace Sirius.Studio.App.ViewModels;

/// <summary>Shell view-model: owns the graph, the scheduler and the run loop.</summary>
public sealed class MainViewModel : ObservableObject
{
    /// <summary>
    /// Window over which rapid changes are coalesced. A slider drag raises many
    /// changes; without this each one would start a pass that the next cancels.
    /// </summary>
    private static readonly TimeSpan RunDebounce = TimeSpan.FromMilliseconds(40);

    private VisionGraph _graph = null!;
    private GraphScheduler _scheduler = null!;
    private GraphEditorViewModel _editor = null!;
    private UndoStack _undo = null!;
    private UndoRecorder _recorder = null!;
    private CancellationTokenSource? _runCancellation;

    /// <summary>
    /// The pass currently in flight. Cancellation is cooperative, so a new
    /// request must wait for this before touching the scheduler.
    /// </summary>
    private Task _runInFlight = Task.CompletedTask;

    private HistogramData? _histogram;
    private ProfileData? _profile;
    private string _statusText = "Ready";
    private string? _currentFilePath;

    /// <summary>The recipe as it was when last saved or opened.</summary>
    private string _savedSnapshot = string.Empty;
    private bool _isRunning;
    private bool _canPaste;
    private ParameterViewModel? _activeRegionParameter;
    private int _addedNodeCount;

    public MainViewModel()
    {
        VisionNodeLibrary.Register();

        Palette = new ObservableCollection<PaletteGroupViewModel>(
            NodeCatalog.All
                .GroupBy(descriptor => descriptor.Category)
                .OrderBy(group => group.Key)
                .Select(group => new PaletteGroupViewModel(
                    group.Key,
                    group.Select(descriptor => new PaletteItemViewModel(descriptor)))));

        // Every category starts open, and they expand independently - closing
        // one does not reopen another.
        foreach (var group in Palette)
            group.IsExpanded = true;

        NewGraphCommand = new RelayCommand(NewGraph);
        OpenGraphCommand = new RelayCommand(OpenGraph);
        SaveGraphCommand = new RelayCommand(() => SaveGraph());
        SaveGraphAsCommand = new RelayCommand(() => SaveGraphAs());
        RunCommand = new RelayCommand(RunEverything);
        PreviewPortCommand = new RelayCommand<PreviewOption>(option =>
        {
            if (option is not null && Editor.SelectedNode is { } node)
                OpenPreview(node.Node, option.Port);
        });

        DrawRegionCommand = new RelayCommand<ParameterViewModel>(parameter =>
        {
            if (parameter is null || Editor.SelectedNode is not { } node)
                return;

            var input = node.Node.Inputs.FirstOrDefault(
                port => port.DataType == typeof(ImageData));

            if (input is not null)
                OpenPreview(node.Node, input);
        });
        DeleteSelectionCommand = new RelayCommand(() => Editor.DeleteSelectionCommand.Execute(null));

        UndoCommand = new RelayCommand(() => _undo.Undo(), () => _undo.CanUndo);
        RedoCommand = new RelayCommand(() => _undo.Redo(), () => _undo.CanRedo);

        CutCommand = new RelayCommand(Cut, HasSelection);
        CopyCommand = new RelayCommand(Copy, HasSelection);
        PasteCommand = new RelayCommand(Paste, CanPaste);
        DuplicateCommand = new RelayCommand(Duplicate, HasSelection);
        SelectAllCommand = new RelayCommand(SelectAll, () => Editor.Nodes.Count > 0);

        SetAccentCommand = new RelayCommand<NodeAccent?>(
            accent =>
            {
                if (accent is { } chosen)
                    ApplyAccent(chosen);
            },
            _ => HasSelection());

        LoadGraph(CreateEmptyGraph());
    }

    public ObservableCollection<PaletteGroupViewModel> Palette { get; }

    /// <summary>
    /// One panel per panel-producing node - viewers, histograms and line
    /// profiles. The window mirrors this into dockable panels, so adding such
    /// a node opens a window and deleting it closes that window.
    /// </summary>
    public ObservableCollection<PanelViewModel> NodePanels { get; } = new();

    public GraphEditorViewModel Editor
    {
        get => _editor;
        private set => SetProperty(ref _editor, value);
    }

    public IRelayCommand NewGraphCommand { get; }

    public IRelayCommand OpenGraphCommand { get; }

    public IRelayCommand SaveGraphCommand { get; }

    public IRelayCommand SaveGraphAsCommand { get; }

    public IRelayCommand UndoCommand { get; }

    public IRelayCommand RedoCommand { get; }

    public IRelayCommand CutCommand { get; }

    public IRelayCommand CopyCommand { get; }

    public IRelayCommand PasteCommand { get; }

    public IRelayCommand DuplicateCommand { get; }

    public IRelayCommand SelectAllCommand { get; }

    /// <summary>
    /// Colours every selected node. Takes the accent as its parameter so one
    /// command serves the whole swatch row and the whole menu.
    /// </summary>
    public IRelayCommand<NodeAccent?> SetAccentCommand { get; }

    /// <summary>
    /// What Undo would undo, for the menu: "Undo Move Threshold". Plain
    /// "Undo" when there is nothing, rather than a disabled item with a
    /// stale name on it.
    /// </summary>
    public string UndoText => _undo?.UndoLabel is { } label ? $"Undo {label}" : "Undo";

    public string RedoText => _undo?.RedoLabel is { } label ? $"Redo {label}" : "Redo";

    /// <summary>The colours offered by the toolbar's style button.</summary>
    public IReadOnlyList<NodeAccentOption> AccentOptions => NodeAccents.All;

    /// <summary>
    /// The colour the toolbar's dropdown shows, and setting it colours the
    /// selection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads the first selected node, so the dropdown says what the
    /// selection is rather than what was last picked. With several selected
    /// and disagreeing it shows the first, which is the ordinary compromise
    /// - the alternative is a blank that cannot be told apart from Default.
    /// </para>
    /// <para>
    /// The setter refuses a no-op. Without that, re-reading the property
    /// after a selection change pushes the value back through the binding
    /// and recolours the selection to what it already was - which would be
    /// harmless to look at and would put a step on the undo stack every
    /// time somebody clicked a node.
    /// </para>
    /// </remarks>
    public NodeAccentOption SelectedAccentOption
    {
        get => NodeAccents.Of(Selection.FirstOrDefault()?.Accent ?? NodeAccent.Default);
        set
        {
            if (value is null || value.Accent == SelectedAccentOption.Accent)
                return;

            ApplyAccent(value.Accent);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Whether anything is selected on the canvas.
    /// </summary>
    /// <remarks>
    /// A property as well as the commands' CanExecute, because the style
    /// button opens a menu rather than running a command - and a button that
    /// drops down eight greyed-out colours is worse than one that is greyed
    /// out itself.
    /// </remarks>
    public bool HasNodeSelection => HasSelection();

    /// <summary>
    /// Raised with the path a recipe's panel arrangement should be written to
    /// or read from.
    /// </summary>
    /// <remarks>
    /// The arrangement of viewers belongs to the recipe - which images you
    /// chose to watch, and where - but only the window knows how to
    /// serialise a docking layout. The view-model says when and where; the
    /// window says how.
    /// </remarks>
    public event EventHandler<string>? LayoutSaveRequested;

    public event EventHandler<string>? LayoutRestoreRequested;

    /// <summary>
    /// Raised with the content id of a panel that should be brought to the
    /// front. Only the window can do it; the view-model knows when.
    /// </summary>
    public event EventHandler<string>? PanelFocusRequested;

    /// <summary>
    /// Raised once a different recipe is in the editor.
    /// </summary>
    /// <remarks>
    /// Node positions are saved in the recipe's own coordinates, which for a
    /// graph built by dragging nodes around can be a long way from the
    /// origin - this project's nodes sit past x=3000. The editor opens at the
    /// origin, so the recipe loads correctly and the canvas looks empty until
    /// you scroll to find it. The window frames the nodes when this fires;
    /// only it knows the viewport.
    /// </remarks>
    public event EventHandler? GraphLoaded;

    /// <summary>The arrangement is kept beside the recipe it belongs to.</summary>
    private static string LayoutPathFor(string graphPath) =>
        Path.ChangeExtension(graphPath, ".layout");

    public IRelayCommand RunCommand { get; }

    public IRelayCommand DeleteSelectionCommand { get; }

    public HistogramData? Histogram
    {
        get => _histogram;
        private set => SetProperty(ref _histogram, value);
    }

    public ProfileData? Profile
    {
        get => _profile;
        private set => SetProperty(ref _profile, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    /// <summary>
    /// The selected node's region parameter, if it has one. The image viewer
    /// binds its ROI to this, which is what lets a rectangle dragged on the
    /// image drive a node like Crop directly.
    /// </summary>
    public ParameterViewModel? ActiveRegionParameter
    {
        get => _activeRegionParameter;
        private set
        {
            if (SetProperty(ref _activeRegionParameter, value))
                OnPropertyChanged(nameof(HasActiveRegion));
        }
    }

    public bool HasActiveRegion => _activeRegionParameter is not null;

    public string Title =>
        (IsModified ? "*" : string.Empty) +
        (_currentFilePath is null
            ? "Sirius Studio Plus"
            : $"Sirius Studio Plus - {Path.GetFileName(_currentFilePath)}");

    /// <summary>
    /// A small working graph, so the editor is not empty on launch: a file
    /// source, grayscale conversion, a threshold, a histogram tap and a viewer.
    /// </summary>
    /// <summary>
    /// The graph the application starts with, and the one File - New returns
    /// to.
    /// </summary>
    /// <remarks>
    /// Empty. An earlier version opened on a worked example - file, convert,
    /// threshold, viewer and histogram - which was useful while there was
    /// nothing else to look at, but it is the wrong thing to meet on every
    /// launch: the first act of real work is deleting it.
    /// </remarks>
    private static VisionGraph CreateEmptyGraph() => new();

    /// <summary>
    /// Whether anything has changed since the recipe was last saved.
    /// </summary>
    /// <remarks>
    /// Answered by re-serialising and comparing, rather than by a flag set
    /// from change events. A flag has to be wired to every kind of edit, and
    /// the one that gets forgotten - dragging a node to a new position, say -
    /// is a silent loss of work. Serialising a graph costs a fraction of the
    /// evaluation it triggers, and it cannot miss anything that gets saved.
    /// </remarks>
    internal bool IsModified =>
        !string.Equals(Snapshot(), _savedSnapshot, StringComparison.Ordinal);

    private string Snapshot()
    {
        try
        {
            return GraphSerializer.Serialize(_graph);
        }
        catch (Exception)
        {
            // A graph that cannot be serialised cannot be compared, and
            // claiming it is unchanged would discard it without asking.
            return Guid.NewGuid().ToString();
        }
    }

    private void MarkSaved() => _savedSnapshot = Snapshot();

    /// <summary>
    /// Offers to save before something discards the current recipe.
    /// </summary>
    /// <returns>False when the operation should be abandoned.</returns>
    internal bool ConfirmDiscard()
    {
        if (!IsModified)
            return true;

        var answer = Dialog.Ask(
            _currentFilePath is null
                ? "Save this project before closing it?"
                : $"Save changes to {Path.GetFileName(_currentFilePath)}?",
            "Unsaved changes");

        return answer switch
        {
            MessageBoxResult.Yes => SaveGraph(),
            MessageBoxResult.No => true,

            // Cancel, or the dialog dismissed: do nothing at all.
            _ => false,
        };
    }

    private void LoadGraph(VisionGraph graph)
    {
        _scheduler?.Dispose();
        _recorder?.Dispose();

        if (_graph is not null)
            _graph.NodeInvalidated -= OnNodeInvalidated;

        _graph = graph;
        _graph.NodeInvalidated += OnNodeInvalidated;

        _scheduler = new GraphScheduler(_graph);

        // A fresh history per recipe. A stack of edits to the graph that was
        // open is not a history of this one, and undoing into it would apply
        // a change to nodes that are no longer anywhere.
        _undo = new UndoStack(_graph);
        _recorder = new UndoRecorder(_graph, _undo);

        _undo.Changed += (_, _) => RefreshEditCommands();
        _undo.Applied += (_, e) =>
        {
            // The canvas collections were built as the operator worked, so
            // after an undo they describe a graph that no longer exists.
            Editor.Resync();
            Editor.FitPorts();
            SyncNodePanels();
            RefreshEditCommands();

            StatusText = $"Undid {e.Edit.Label}.";

            if (e.AffectsResults)
                RequestRun();
        };

        Editor = new GraphEditorViewModel(_graph, _undo);

        // Every path that changes the selection already re-asks the editor's
        // own delete command, so this is the one hook that catches all of
        // them - a container's IsSelected binding, the SelectedItems
        // collection, and a deletion that empties both.
        Editor.DeleteSelectionCommand.CanExecuteChanged += (_, _) => RefreshEditCommands();

        Editor.GraphChanged += (_, _) =>
        {
            SyncNodePanels();
            RequestRun();
        };
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(GraphEditorViewModel.SelectedNode))
                return;

            // Both kinds of region are drawable: the plain rectangle, and
            // the oriented one, whose angle the dragging leaves alone.
            ActiveRegionParameter = Editor.SelectedNode?.Parameters
                .FirstOrDefault(parameter =>
                    parameter.Kind is ParamEditor.Region or ParamEditor.Rect);

            SelectedNodePreviews = Editor.SelectedNode is { } selected
                ? PreviewablePorts(selected.Node).Select(PreviewOption.For).ToArray()
                : [];

            // Previews as well as viewers: the Draw button opens a preview
            // of the node's input, and a region cannot be dragged on a
            // panel that was never told there is one.
            foreach (var panel in NodePanels.OfType<ImagePanelViewModel>())
                panel.RegionParameter = ActiveRegionParameter;
        };

        NodePanels.Clear();
        SyncNodePanels();

        RefreshEditCommands();
        MarkSaved();

        OnPropertyChanged(nameof(Title));
        GraphLoaded?.Invoke(this, EventArgs.Empty);
        RequestRun(immediate: true);
    }

    /// <summary>Selected nodes, in canvas order.</summary>
    private IReadOnlyList<NodeViewModel> Selection =>
        Editor.Nodes.Where(node => node.IsSelected).ToArray();

    private bool HasSelection() => Editor is not null && Editor.Nodes.Any(node => node.IsSelected);

    /// <summary>
    /// Whether there is a graph on the clipboard, as last looked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Remembered rather than asked, and this is not an optimisation. WPF
    /// reaches the clipboard through OLE, which belongs to whichever process
    /// last opened it: the call blocks and retries when somebody else holds
    /// it, and it can take seconds. <c>CanExecute</c> is re-asked on every
    /// <see cref="System.Windows.Input.CommandManager.RequerySuggested"/> -
    /// which is to say constantly - so asking there makes the whole window
    /// stutter for something that changes only occasionally.
    /// </para>
    /// <para>
    /// Refreshed when it can have changed: after this application copies,
    /// and when the window is activated, which is the only way a copy made
    /// in another application can arrive.
    /// </para>
    /// </remarks>
    private bool CanPaste() => _canPaste;

    /// <summary>
    /// Re-reads whether the clipboard holds a graph.
    /// </summary>
    /// <remarks>
    /// Called by the shell when the window is activated, as well as after
    /// our own copies.
    /// </remarks>
    public void RefreshClipboardState()
    {
        bool available;

        try
        {
            available = Clipboard.ContainsData(GraphClipboard.Format);
        }
        catch (Exception)
        {
            // Held by another process. Reporting "no" greys out Paste for a
            // moment, which is the harmless answer.
            available = false;
        }

        if (available == _canPaste)
            return;

        _canPaste = available;
        PasteCommand.NotifyCanExecuteChanged();
    }

    private void Copy()
    {
        var nodes = Selection;

        if (nodes.Count == 0)
            return;

        var json = GraphClipboard.Copy(_graph, nodes.Select(node => node.Node));

        try
        {
            // Both formats. The private one is what Paste reads; the text is
            // so the clipboard is inspectable, and pastes into an editor as
            // something a person can actually read.
            var data = new DataObject();
            data.SetData(GraphClipboard.Format, json);
            data.SetData(DataFormats.UnicodeText, json);

            Clipboard.SetDataObject(data, copy: true);

            StatusText = nodes.Count == 1
                ? $"Copied {nodes[0].Title}."
                : $"Copied {nodes.Count} nodes.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not copy: {ex.Message}";
        }

        RefreshClipboardState();
        RefreshEditCommands();
    }

    private void Cut()
    {
        var count = Selection.Count;

        if (count == 0)
            return;

        // Copy first: if the clipboard refuses, nothing has been deleted yet.
        Copy();

        using (_undo.Begin(count == 1 ? $"Cut {Selection[0].Title}" : $"Cut {count} nodes"))
            Editor.DeleteSelectionCommand.Execute(null);

        StatusText = count == 1 ? "Cut 1 node." : $"Cut {count} nodes.";
    }

    private void Paste()
    {
        string json;

        try
        {
            if (Clipboard.GetData(GraphClipboard.Format) is not string stored)
                return;

            json = stored;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not read the clipboard: {ex.Message}";
            return;
        }

        PasteGraph(json, "Paste");
    }

    /// <summary>
    /// Copies the selection and pastes it in one step, without touching the
    /// clipboard.
    /// </summary>
    /// <remarks>
    /// Not Copy-then-Paste internally: duplicating a node should not throw
    /// away whatever the operator had on the clipboard for something else.
    /// </remarks>
    private void Duplicate()
    {
        var nodes = Selection;

        if (nodes.Count == 0)
            return;

        PasteGraph(
            GraphClipboard.Copy(_graph, nodes.Select(node => node.Node)),
            nodes.Count == 1 ? $"Duplicate {nodes[0].Title}" : $"Duplicate {nodes.Count} nodes");
    }

    private void PasteGraph(string json, string label)
    {
        PasteResult result;

        try
        {
            // Recording is paused for the paste itself: the nodes are built
            // by the loader, which sets every property on every one of them,
            // and a paste is one step back rather than a hundred and forty.
            using (_recorder.Pause())
                result = GraphClipboard.Paste(_graph, json);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not paste: {ex.Message}";
            return;
        }

        if (result.Nodes.Count == 0)
        {
            StatusText = "There was nothing to paste.";
            return;
        }

        // Described after the fact rather than applied by the stack: the
        // paste has already happened, and this is what it would take to
        // undo it.
        _undo.Record(new CompositeEdit(
            label,
            result.Nodes.Select(node => (IGraphEdit)new AddNodeEdit(node))
                .Concat(result.Wires.Select(
                    wire => (IGraphEdit)new ConnectEdit(wire.Source, wire.Target, null)))));

        Editor.FitPorts();
        Editor.Resync();

        // Selected, so the pasted nodes can be dragged into place at once -
        // which is what anyone does next.
        foreach (var node in Editor.Nodes)
            node.IsSelected = result.Nodes.Any(pasted => ReferenceEquals(pasted, node.Node));

        Editor.SelectedNode = Editor.Nodes.FirstOrDefault(node => node.IsSelected);

        SyncNodePanels();
        RefreshEditCommands();
        RequestRun();

        StatusText = result.Warnings.Count > 0
            ? $"Pasted {result.Nodes.Count} node(s) with {result.Warnings.Count} warning(s)."
            : $"Pasted {result.Nodes.Count} node(s).";
    }

    private void SelectAll()
    {
        foreach (var node in Editor.Nodes)
            node.IsSelected = true;

        Editor.SelectedNode = Editor.Nodes.FirstOrDefault();
        RefreshEditCommands();
    }

    /// <summary>
    /// Colours every selected node, as one undoable step.
    /// </summary>
    /// <remarks>
    /// The whole selection, which is the point of having it on the toolbar:
    /// the swatches on the properties panel and the canvas menu colour the
    /// one node they belong to, and marking up a branch means marking
    /// several at once.
    /// </remarks>
    private void ApplyAccent(NodeAccent accent)
    {
        var nodes = Selection;

        if (nodes.Count == 0)
            return;

        var name = NodeAccents.Of(accent).Name;

        using (_undo.Begin(nodes.Count == 1 ? $"Colour {nodes[0].Title}" : $"Colour {nodes.Count} nodes"))
        {
            foreach (var node in nodes)
                node.Accent = accent;
        }

        StatusText = nodes.Count == 1
            ? $"{nodes[0].Title} is {name}."
            : $"{nodes.Count} nodes are {name}.";
    }

    /// <summary>
    /// Re-asks every edit command whether it can run, and re-reads the undo
    /// labels.
    /// </summary>
    /// <remarks>
    /// Called from the places that change what is selected or what is on the
    /// stack. A command that is never re-asked stays greyed out for the rest
    /// of the session, which reads as the feature being broken.
    /// </remarks>
    private void RefreshEditCommands()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        SetAccentCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
        OnPropertyChanged(nameof(HasNodeSelection));
        OnPropertyChanged(nameof(SelectedAccentOption));
    }

    private void NewGraph()
    {
        if (!ConfirmDiscard())
            return;

        _currentFilePath = null;
        LoadGraph(CreateEmptyGraph());
        StatusText = "New graph created.";
    }

    private void OpenGraph()
    {
        if (!ConfirmDiscard())
            return;

        var dialog = new OpenFileDialog
        {
            Filter = "Sirius Graph (*.sgraph)|*.sgraph|JSON (*.json)|*.json|All files (*.*)|*.*",
            Title = "Open Graph",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var result = GraphSerializer.Load(dialog.FileName);
            _currentFilePath = dialog.FileName;
            LoadGraph(result.Graph);

            // After the graph, because the layout places panels by id and
            // they do not exist until the graph that owns them is loaded.
            LayoutRestoreRequested?.Invoke(this, LayoutPathFor(dialog.FileName));

            StatusText = result.HasWarnings
                ? $"Loaded with {result.Warnings.Count} warning(s)."
                : $"Loaded {Path.GetFileName(dialog.FileName)}.";

            if (result.HasWarnings)
            {
                Dialog.Warn(
                    string.Join(Environment.NewLine, result.Warnings),
                    "Graph loaded with warnings");
            }
        }
        catch (Exception ex)
        {
            Dialog.Error(ex.Message, "Could not open graph");
        }
    }

    /// <summary>
    /// Saves over the open file, asking for a name only when there is not one
    /// yet.
    /// </summary>
    /// <remarks>
    /// It used to prompt every time, which is Save As wearing Save's name -
    /// and meant the keyboard shortcut could not be used without a dialog.
    /// </remarks>
    private bool SaveGraph() =>
        _currentFilePath is null ? SaveGraphAs() : Write(_currentFilePath);

    private bool SaveGraphAs()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Sirius Graph (*.sgraph)|*.sgraph",
            DefaultExt = ".sgraph",
            Title = "Save Project As",
            FileName = _currentFilePath is null ? "recipe.sgraph" : Path.GetFileName(_currentFilePath),
        };

        if (dialog.ShowDialog() != true)
            return false;

        return Write(dialog.FileName);
    }

    /// <summary>Writes the recipe and its panel arrangement to a path.</summary>
    internal bool Write(string path)
    {
        try
        {
            GraphSerializer.Save(_graph, path);

            // The panel arrangement is written with it, so reopening gets the
            // workspace back rather than every viewer in one tab stack.
            LayoutSaveRequested?.Invoke(this, LayoutPathFor(path));

            _currentFilePath = path;
            MarkSaved();
            OnPropertyChanged(nameof(Title));
            StatusText = $"Saved {Path.GetFileName(path)}.";
            return true;
        }
        catch (Exception ex)
        {
            Dialog.Error(ex.Message, "Could not save graph");
            return false;
        }
    }

    /// <summary>
    /// Discards every cached result and evaluates the whole graph.
    /// </summary>
    /// <remarks>
    /// A plain run only evaluates what is stale, so on a graph nobody has
    /// touched it evaluates nothing and reports "cached 12 in 0 ms" - true,
    /// and useless as a measurement. The button exists to re-run and time the
    /// recipe, so it clears the cache first and the status bar then reports
    /// what the whole thing actually costs.
    /// </remarks>
    private void RunEverything()
    {
        _scheduler?.InvalidateAll();
        RequestRun(immediate: true);
    }

    /// <summary>
    /// Adds a node at a point in graph coordinates, cascading successive
    /// additions so they do not stack on one spot.
    /// </summary>
    /// <remarks>
    /// The caller supplies the position because only the view knows where the
    /// graph is currently scrolled to. Placing at fixed coordinates put new
    /// nodes outside the visible area as soon as the canvas had been panned.
    /// </remarks>
    public NodeViewModel? AddNodeAt(PaletteItemViewModel? item, Point location)
    {
        if (item is null)
            return null;

        var step = _addedNodeCount++ % 6 * 26;
        return Editor.AddNode(item.Descriptor, new Point(location.X + step, location.Y + step));
    }

    private void OnNodeInvalidated(object? sender, VisionNode node)
    {
        RequestRun();

        // Editing a parameter is unsaved work, and the title is where that is
        // said. A node dragged on the canvas raises nothing, so the marker can
        // lag - the prompt before discarding does not, because it re-reads the
        // whole graph.
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>
    /// Schedules an evaluation, cancelling and then waiting out any pass
    /// already in flight.
    /// </summary>
    /// <remarks>
    /// Waiting is not optional. Cancelling a pass only sets a flag the
    /// scheduler checks between nodes, so the old pass keeps running for a
    /// while. Starting the next one immediately puts two passes on the same
    /// result cache, where one disposes a buffer the other is still reading -
    /// which shows up as an access violation inside OpenCV, not a managed
    /// exception.
    /// </remarks>
    private async void RequestRun(bool immediate = false)
    {
        _runCancellation?.Cancel();

        var previous = _runInFlight;
        var cancellation = new CancellationTokenSource();
        _runCancellation = cancellation;

        // Published before the first await, so the next request waits on this
        // pass rather than on the one it supersedes.
        var completion = new TaskCompletionSource();
        _runInFlight = completion.Task;

        try
        {
            await previous.ConfigureAwait(true);

            if (!immediate)
                await Task.Delay(RunDebounce, cancellation.Token).ConfigureAwait(true);

            if (cancellation.IsCancellationRequested)
                return;

            IsRunning = true;
            var result = await _scheduler.RunAsync(cancellation.Token).ConfigureAwait(true);

            if (cancellation.IsCancellationRequested)
                return;

            await UpdateOutputsAsync(cancellation.Token).ConfigureAwait(true);
            StatusText = Describe(result);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request; the next pass reports status.
        }
        catch (Exception ex)
        {
            StatusText = $"Run failed: {ex.Message}";
        }
        finally
        {
            // Always released, or every later request would wait forever.
            completion.SetResult();

            if (ReferenceEquals(_runCancellation, cancellation))
                IsRunning = false;
        }
    }

    private static string Describe(GraphRunResult result)
    {
        if (result.Error is not null)
            return result.Error;

        if (result.Cancelled)
            return "Run cancelled.";

        var summary = $"Evaluated {result.Evaluated}, cached {result.Skipped} in {result.Duration.TotalMilliseconds:0} ms";
        return result.Failed > 0 ? $"{summary} - {result.Failed} node(s) failed" : summary;
    }

    /// <summary>
    /// Refreshes every node panel from the evaluated graph, doing the costly
    /// part off the UI thread.
    /// </summary>
    /// <remarks>
    /// Converting a native image to a bitmap is the expensive step, and doing
    /// it on the UI thread would undo the point of evaluating off it - a large
    /// frame would stutter the window on every slider movement.
    ///
    /// Reading cached buffers off-thread is safe only while no evaluation pass
    /// is running. The run loop guarantees that: it does not mark the pass
    /// complete until this returns, so the next pass waits rather than
    /// disposing buffers mid-read.
    /// </remarks>
    private async Task UpdateOutputsAsync(CancellationToken cancellationToken)
    {
        var panels = NodePanels.ToArray();

        if (panels.Length == 0)
            return;

        await Task.Run(
            () =>
            {
                foreach (var panel in panels)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    panel.Prepare(_scheduler);
                }
            },
            cancellationToken).ConfigureAwait(true);

        foreach (var panel in panels)
            panel.Commit();
    }

    /// <summary>
    /// Keeps <see cref="NodePanels"/> in step with the nodes that produce
    /// panels, and gives every node a unique default name.
    /// </summary>
    private void SyncNodePanels()
    {
        EnsureDefaultNames();

        var stalePanels = NodePanels
            .OfType<NodePanelViewModel>()
            .Where(panel => !_graph.Nodes.Contains(panel.Node))
            .ToArray();

        foreach (var stale in stalePanels)
        {
            NodePanels.Remove(stale);

            // Deleting a node closes the profile windows that belonged to
            // its panel; nothing else would ever remove them.
            if (stale is ImagePanelViewModel image)
            {
                foreach (var line in image.ProfileLines.ToArray())
                    NodePanels.Remove(line);
            }

            // It may also be holding a native buffer for interactive profiling.
            stale.Dispose();
        }

        foreach (var node in _graph.Nodes)
        {
            // Previews are deliberately excluded: they are opened by hand,
            // not implied by the graph, so one open on a Viewer node must not
            // be mistaken for that node's own panel.
            if (NodePanels.OfType<NodePanelViewModel>()
                .Any(panel => panel.Node == node && panel is not PreviewPanelViewModel))
            {
                continue;
            }

            PanelViewModel? panel = node switch
            {
                ViewerNode viewer => CreateViewerPanel(viewer),
                HistogramNode histogram => new HistogramPanelViewModel(histogram),
                LineProfileNode profile => new ProfilePanelViewModel(profile),
                ShapeTableNode table => new ShapeTablePanelViewModel(table, ShapeSelection),
                _ => null,
            };

            if (panel is not null)
                NodePanels.Add(panel);
        }
    }

    /// <summary>
    /// Shared by every panel showing the same shapes, so selecting a blob in
    /// a table highlights it on the image.
    /// </summary>
    public ShapeSelectionService ShapeSelection { get; } = new();

    private IReadOnlyList<PreviewOption> _selectedNodePreviews = [];

    /// <summary>
    /// What the selected node offers to show, as buttons in the properties
    /// panel.
    /// </summary>
    /// <remarks>
    /// The canvas has a right-click menu for this as well. This is the
    /// discoverable route: a menu on a node that also pans with the right
    /// button is not somewhere an operator looks.
    /// </remarks>
    public IReadOnlyList<PreviewOption> SelectedNodePreviews
    {
        get => _selectedNodePreviews;
        private set
        {
            if (SetProperty(ref _selectedNodePreviews, value))
                OnPropertyChanged(nameof(HasPreviews));
        }
    }

    /// <summary>Whether the selected node has anything worth showing.</summary>
    public bool HasPreviews => SelectedNodePreviews.Count > 0;

    /// <summary>Opens a preview of one of the selected node's ports.</summary>
    public IRelayCommand<PreviewOption> PreviewPortCommand { get; }

    /// <summary>
    /// Opens the image a region parameter is measured in, so it can be
    /// dragged there.
    /// </summary>
    /// <remarks>
    /// Always the owning node's image <b>input</b>, whatever the operator
    /// last looked at. A region is expressed in the coordinates of the
    /// image the node is given, and offering any other one is how a region
    /// comes to be drawn in a space it does not belong to.
    /// </remarks>
    public IRelayCommand<ParameterViewModel> DrawRegionCommand { get; }

    /// <summary>
    /// The ports of <paramref name="node"/> that something can be shown for,
    /// in the order they should be offered.
    /// </summary>
    /// <remarks>
    /// Outputs first, because "what did this do" is asked more often than
    /// "what did it get". A port carrying anything but an image or a shape
    /// set has nothing to draw and is left out rather than offered and then
    /// refused.
    /// </remarks>
    public static IReadOnlyList<Port> PreviewablePorts(VisionNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node.Outputs.Cast<Port>()
            .Concat(node.Inputs)
            .Where(CanPreview)
            .ToArray();
    }

    private static bool CanPreview(Port port) =>
        port.DataType == typeof(ImageData) || port.DataType == typeof(ShapeSet);

    /// <summary>
    /// The port a plain "show me this node" should open, or null when the
    /// node has nothing to show.
    /// </summary>
    public static Port? DefaultPreviewPort(VisionNode node) =>
        PreviewablePorts(node).FirstOrDefault();

    /// <summary>
    /// Opens a preview of one port, or returns the one already open for it.
    /// </summary>
    /// <remarks>
    /// Keyed by node and port, so asking twice focuses what is there instead
    /// of stacking a second window on it.
    /// </remarks>
    public PreviewPanelViewModel? OpenPreview(VisionNode node, Port port)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(port);

        if (!CanPreview(port))
            return null;

        var existing = NodePanels
            .OfType<PreviewPanelViewModel>()
            .FirstOrDefault(panel => panel.Node == node && panel.Port == port);

        if (existing is not null)
        {
            PanelFocusRequested?.Invoke(this, existing.ContentId);
            return existing;
        }

        // A shape set has to be drawn over something, and the only image this
        // node can speak for is the one it was given.
        var backdrop = port.DataType == typeof(ShapeSet)
            ? node.Inputs.FirstOrDefault(input => input.DataType == typeof(ImageData))
            : null;

        var preview = new PreviewPanelViewModel(node, port, backdrop, ShapeSelection)
        {
            // So a preview opened by the Draw button can be drawn on at once.
            RegionParameter = ActiveRegionParameter,
        };

        AttachProfiling(preview);

        NodePanels.Add(preview);

        // Previews are opened against a graph that has already run, so there
        // is a frame waiting in the cache. Without a pass the window would sit
        // empty until something unrelated made the graph dirty.
        RequestRun(immediate: true);

        PanelFocusRequested?.Invoke(this, preview.ContentId);
        return preview;
    }

    /// <summary>
    /// One port offered for previewing, labelled for a button.
    /// </summary>
    /// <remarks>
    /// A small type rather than converters on the port: which side a port is
    /// on changes what the preview means - what this node was given, or what
    /// it made of it - and that is worth saying in words rather than leaving
    /// to the operator to infer from a port name.
    /// </remarks>
    public sealed record PreviewOption(Port Port, string Label, string Description)
    {
        public static PreviewOption For(Port port)
        {
            var side = port.Direction == PortDirection.Input ? "input" : "output";

            return new PreviewOption(
                port,
                port.DisplayName,
                $"Show the {side} \"{port.DisplayName}\" in a window of its own");
        }
    }

    private ViewerPanelViewModel CreateViewerPanel(ViewerNode viewer)
    {
        var panel = new ViewerPanelViewModel(viewer, ShapeSelection)
        {
            RegionParameter = ActiveRegionParameter,
        };

        AttachProfiling(panel);

        return panel;
    }

    /// <summary>
    /// Wires a panel's interactive profiling into the shell.
    /// </summary>
    /// <remarks>
    /// Two things only the shell can do. A frame to sample can be captured
    /// only inside an evaluation pass, because the graph reclaims its buffers
    /// when one ends - so the request becomes a run. And a profile line drawn
    /// on the image becomes a panel: the viewer owns the line, the shell
    /// mirrors it into the docking layout.
    /// </remarks>
    private void AttachProfiling(ImagePanelViewModel panel)
    {
        panel.ProfileDataRequested += (_, _) => RequestRun();

        panel.ProfileLines.CollectionChanged += (_, args) =>
        {
            foreach (var removed in args.OldItems?.OfType<PanelViewModel>() ?? [])
            {
                NodePanels.Remove(removed);
                removed.Dispose();
            }

            foreach (var added in args.NewItems?.OfType<PanelViewModel>() ?? [])
                NodePanels.Add(added);
        };
    }

    /// <summary>
    /// Numbers nodes that still carry their type's bare name - "Threshold"
    /// becomes "Threshold 1", then "Threshold 2".
    /// </summary>
    /// <remarks>
    /// Only untouched names are replaced, so anything the operator renamed, or
    /// a name loaded from a saved project, is left alone. Numbers are reused
    /// from the lowest free slot rather than always incrementing, so deleting
    /// and re-adding does not leave the count climbing.
    /// </remarks>
    private void EnsureDefaultNames()
    {
        // Not recorded. "Threshold" becoming "Threshold 1" is part of adding
        // the node, not something the operator did, and recording it put a
        // separate "Rename" step on the stack after every single add - so
        // undoing an add left the node in place with its number taken off.
        using var quiet = _recorder.Pause();

        foreach (var node in _graph.Nodes)
        {
            if (node.Name != node.Descriptor.DisplayName)
                continue;

            var taken = _graph.Nodes
                .Where(other => !ReferenceEquals(other, node))
                .Select(other => other.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (var index = 1; ; index++)
            {
                var candidate = $"{node.Descriptor.DisplayName} {index}";
                if (taken.Add(candidate))
                {
                    node.Name = candidate;
                    break;
                }
            }
        }
    }
}
