using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sirius.Core.Graph;
using Sirius.Core.Graph.Undo;
using Sirius.Core.Nodes;
using Sirius.Core.Projects;
using Sirius.Core.Serialization;
using Sirius.Plc;
using Sirius.Plc.Nodes;
using Sirius.Plc.Workflows;
using Sirius.Studio.App.ViewModels;

namespace Sirius.Builder;

/// <summary>A group of the workflow editor's palette: the PLC's nodes, the stations'.</summary>
public sealed record WorkflowPaletteGroup(string Name, IReadOnlyList<NodeDescriptor> Items);

/// <summary>
/// A controller's workflow open in a tab: the graph on Studio's canvas, a
/// palette of workflow nodes beside it, and the chosen node's settings.
/// </summary>
/// <remarks>
/// <para>
/// The graph, its editor, selection and undo are Studio's own
/// (<see cref="GraphDocumentViewModel"/>); only the palette and the
/// settings are Builder's. Saved by hand, as a recipe is in Studio - a
/// test run uses the saved file.
/// </para>
/// <para>
/// The settings are the grid Builder shows a widget's in: a word for an
/// address, a list for a type or an edge, the project's stations for a
/// station.
/// </para>
/// </remarks>
public sealed class WorkflowDocumentViewModel : ObservableObject, IDisposable
{
    private readonly Project _project;
    private readonly List<VisionNode> _watched = [];
    private IReadOnlyList<PropertyCategory> _properties = [];
    private bool _isModified;
    private string _problemText = string.Empty;
    private string _statusText = string.Empty;

    public WorkflowDocumentViewModel(Project project, Controller controller)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        Controller = controller ?? throw new ArgumentNullException(nameof(controller));
        Path = project.WorkflowPath(controller);

        var loaded = File.Exists(Path)
            ? WorkflowFile.Load(Path)
            : new GraphLoadResult(new VisionGraph(), [$"{System.IO.Path.GetFileName(Path)} was missing; a new workflow is started."]);

        LoadWarnings = loaded.Warnings;
        Document = new GraphDocumentViewModel(loaded.Graph);

        Palette = NodeCatalog.All
            .Where(descriptor => descriptor.Category.IsWorkflow() && !descriptor.IsRetired)
            .GroupBy(descriptor => descriptor.Category)
            .OrderBy(group => group.Key)
            .Select(group => new WorkflowPaletteGroup(group.Key == NodeCategory.Plc ? "PLC" : "Stations", group.ToArray()))
            .ToArray();

        SaveCommand = new RelayCommand(() => Save());
        UndoCommand = new RelayCommand(Undo, () => Document.Undo.CanUndo);
        RedoCommand = new RelayCommand(Redo, () => Document.Undo.CanRedo);
        DeleteCommand = new RelayCommand(() => Editor.DeleteSelectionCommand.Execute(null), () => Editor.DeleteSelectionCommand.CanExecute(null));
        CopyCommand = new RelayCommand(Copy, HasSelection);
        CutCommand = new RelayCommand(Cut, HasSelection);
        PasteCommand = new RelayCommand(Paste);
        DuplicateCommand = new RelayCommand(Duplicate, HasSelection);
        Editor.SelectedItems.CollectionChanged += (_, _) => SelectionChanged();

        Document.Undo.Changed += OnUndoChanged;
        Graph.StructureChanged += OnStructureChanged;
        Editor.PropertyChanged += OnEditorPropertyChanged;
        Editor.DeleteSelectionCommand.CanExecuteChanged += (_, _) => DeleteCommand.NotifyCanExecuteChanged();

        WatchNodes();
        Check();
    }

    public Controller Controller { get; }

    /// <summary>The workflow file, in full.</summary>
    public string Path { get; }

    public GraphDocumentViewModel Document { get; }

    public GraphEditorViewModel Editor => Document.Editor;

    public VisionGraph Graph => Document.Graph;

    /// <summary>What reading the file found - unknown nodes, a missing file.</summary>
    public IReadOnlyList<string> LoadWarnings { get; }

    /// <summary>The tab's caption: the controller's name, with a * while changed.</summary>
    public string Title => $"{Controller.Name}{(IsModified ? " *" : string.Empty)}";

    /// <summary>The docking layout's identity for the tab.</summary>
    public string ContentId => "workflow:" + Controller.Workflow;

    public IReadOnlyList<WorkflowPaletteGroup> Palette { get; }

    public bool IsModified
    {
        get => _isModified;
        private set
        {
            if (SetProperty(ref _isModified, value))
                OnPropertyChanged(nameof(Title));
        }
    }

    /// <summary>What the workflow's check finds - an address missing, a station not in the project - or "Ready to run."</summary>
    public string ProblemText
    {
        get => _problemText;
        private set => SetProperty(ref _problemText, value);
    }

    public bool HasProblems { get; private set; }

    public IRelayCommand SaveCommand { get; }

    public IRelayCommand UndoCommand { get; }

    public IRelayCommand RedoCommand { get; }

    public IRelayCommand DeleteCommand { get; }

    public IRelayCommand CopyCommand { get; }

    public IRelayCommand CutCommand { get; }

    public IRelayCommand PasteCommand { get; }

    public IRelayCommand DuplicateCommand { get; }

    /// <summary>What the last edit did - "Copied 2 nodes." - for Builder's status line.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    // ---- the clipboard: Studio's own, so a workflow's nodes copy and paste as a recipe's do ----

    private IReadOnlyList<NodeViewModel> Selection => Editor.Nodes.Where(node => node.IsSelected).ToArray();

    private bool HasSelection() => Editor.Nodes.Any(node => node.IsSelected);

    private void SelectionChanged()
    {
        CopyCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private void Copy()
    {
        var nodes = Selection;

        if (nodes.Count == 0)
            return;

        var json = GraphClipboard.Copy(Graph, nodes.Select(node => node.Node));

        try
        {
            var data = new DataObject();
            data.SetData(GraphClipboard.Format, json);
            data.SetData(DataFormats.UnicodeText, json);
            Clipboard.SetDataObject(data, copy: true);

            StatusText = nodes.Count == 1 ? $"Copied {nodes[0].Title}." : $"Copied {nodes.Count} nodes.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            StatusText = $"Could not copy: {ex.Message}";
        }
    }

    private void Cut()
    {
        var count = Selection.Count;

        if (count == 0)
            return;

        // Copied first: if the clipboard refuses, nothing has been deleted yet.
        Copy();

        using (Document.Undo.Begin(count == 1 ? $"Cut {Selection[0].Title}" : $"Cut {count} nodes"))
            Editor.DeleteSelectionCommand.Execute(null);

        StatusText = count == 1 ? "Cut 1 node." : $"Cut {count} nodes.";
    }

    private void Paste()
    {
        string? json;

        try
        {
            json = Clipboard.ContainsData(GraphClipboard.Format) ? Clipboard.GetData(GraphClipboard.Format) as string : null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            StatusText = $"Could not read the clipboard: {ex.Message}";
            return;
        }

        if (json is null)
        {
            StatusText = "There are no nodes on the clipboard to paste.";
            return;
        }

        PasteGraph(json, "Paste");
    }

    /// <summary>Copies the selection and pastes it in one step, leaving the clipboard as it was.</summary>
    private void Duplicate()
    {
        var nodes = Selection;

        if (nodes.Count > 0)
            PasteGraph(GraphClipboard.Copy(Graph, nodes.Select(node => node.Node)), nodes.Count == 1 ? $"Duplicate {nodes[0].Title}" : $"Duplicate {nodes.Count} nodes");
    }

    private void PasteGraph(string json, string label)
    {
        PasteResult result;

        try
        {
            // One step back for the whole paste, not one per property the loader sets.
            using (Document.Recorder.Pause())
                result = GraphClipboard.Paste(Graph, json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            StatusText = $"Could not paste: {ex.Message}";
            return;
        }

        // A recipe's nodes pasted here would never run: taken out again.
        foreach (var stranger in result.Nodes.Where(node => node is not WorkflowNode).ToArray())
            Graph.Remove(stranger);

        var pasted = result.Nodes.Where(node => node is WorkflowNode).ToArray();

        if (pasted.Length == 0)
        {
            StatusText = result.Nodes.Count > 0 ? "A recipe's nodes cannot go in a workflow." : "There was nothing to paste.";
            Editor.Resync();
            return;
        }

        Document.Undo.Record(new CompositeEdit(
            label,
            pasted.Select(node => (IGraphEdit)new AddNodeEdit(node))
                .Concat(result.Wires
                    .Where(wire => pasted.Contains(wire.Source.Node) && pasted.Contains(wire.Target.Node))
                    .Select(wire => (IGraphEdit)new ConnectEdit(wire.Source, wire.Target, null)))));

        Editor.FitPorts();
        Editor.Resync();

        // Selected, so they can be dragged into place at once.
        foreach (var node in Editor.Nodes)
            node.IsSelected = pasted.Any(added => ReferenceEquals(added, node.Node));

        Editor.SelectedNode = Editor.Nodes.FirstOrDefault(node => node.IsSelected);
        Changed();
        StatusText = pasted.Length == 1 ? $"Pasted {pasted[0].Name}." : $"Pasted {pasted.Length} nodes.";
    }

    // ---- editing ---------------------------------------------------------------------

    /// <summary>Adds a node of <paramref name="descriptor"/>'s kind at a point on the canvas.</summary>
    public NodeViewModel AddNode(NodeDescriptor descriptor, Point location)
    {
        var added = Editor.AddNode(descriptor, location);

        // A station node starts on the project's first station.
        if (_project.Stations.Count > 0)
        {
            switch (added.Node)
            {
                case InspectNode { Station.Length: 0 } inspect:
                    inspect.Station = _project.Stations[0].Name;
                    break;

                case StationResultNode { Station.Length: 0 } result:
                    result.Station = _project.Stations[0].Name;
                    break;
            }
        }

        return added;
    }

    private void Undo()
    {
        Document.Undo.Undo();
        Editor.Resync();
        Changed();
    }

    private void Redo()
    {
        Document.Undo.Redo();
        Editor.Resync();
        Changed();
    }

    /// <summary>Writes the workflow to its file.</summary>
    /// <returns>Whether it was written.</returns>
    public bool Save()
    {
        try
        {
            WorkflowFile.Save(Graph, Path, Controller.Name);
            IsModified = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Sirius.Studio.App.Dialog.Error($"{System.IO.Path.GetFileName(Path)} could not be saved: {ex.Message}", "Save Workflow");
            return false;
        }
    }

    // ---- the chosen node's settings ---------------------------------------------------

    public IReadOnlyList<PropertyCategory> Properties
    {
        get => _properties;
        private set => SetProperty(ref _properties, value);
    }

    public string PropertyTitle => Editor.SelectedNode?.Node.Name ?? Controller.Name;

    public string PropertySubtitle => Editor.SelectedNode?.Node.Descriptor.Description
                                      ?? "Choose a node to set it up. Drag nodes in from the left; wire a trigger (round, filled) to a trigger.";

    private void ShowProperties()
    {
        Properties = Editor.SelectedNode is { } selected ? Rows(selected.Node) : [];
        OnPropertyChanged(nameof(PropertyTitle));
        OnPropertyChanged(nameof(PropertySubtitle));
    }

    private IReadOnlyList<PropertyCategory> Rows(VisionNode node)
    {
        var rows = new List<PropertyRow>
        {
            new("Node", "Name", PropertyEditor.Text, () => node.Name, value =>
            {
                if (value is string text && text.Trim().Length > 0)
                    node.Name = text.Trim();
            }),
            new("Node", "Kind", PropertyEditor.ReadOnly, () => node.Descriptor.DisplayName),
        };

        foreach (var parameter in node.Descriptor.Parameters.Where(parameter => !parameter.Attribute.Hidden))
            rows.Add(Row(node, parameter));

        return PropertyCategory.From(rows);
    }

    private PropertyRow Row(VisionNode node, ParamDescriptor parameter)
    {
        var category = parameter.Attribute.Group ?? "Settings";
        var description = parameter.Attribute.Description;
        var type = parameter.ValueType;

        if (type == typeof(bool))
            return new PropertyRow(category, parameter.DisplayName, PropertyEditor.Check, () => parameter.GetValue(node), value => Set(node, parameter, value), description);

        if (type.IsEnum)
        {
            var options = Enum.GetValues(type).Cast<object>().Select(value => new PropertyOption(Label(value), value)).ToArray();
            return new PropertyRow(category, parameter.DisplayName, PropertyEditor.Choice, () => parameter.GetValue(node), value => Set(node, parameter, value), description, () => options);
        }

        // A station: one of the project's, by name.
        if (parameter.Name == nameof(InspectNode.Station) && type == typeof(string))
        {
            return new PropertyRow(
                category,
                parameter.DisplayName,
                PropertyEditor.Choice,
                () => parameter.GetValue(node),
                value => Set(node, parameter, value),
                description,
                () => StationOptions(parameter.GetValue(node) as string));
        }

        return new PropertyRow(
            category,
            parameter.DisplayName,
            PropertyEditor.Text,
            () => Convert.ToString(parameter.GetValue(node), CultureInfo.InvariantCulture),
            value =>
            {
                if (Parse(value as string, type) is { } parsed)
                    Set(node, parameter, parsed);
            },
            description);
    }

    private IEnumerable<PropertyOption> StationOptions(string? current)
    {
        var options = _project.Stations.Select(station => new PropertyOption(station.Name, station.Name)).ToList();

        if (!string.IsNullOrEmpty(current) && options.All(option => !string.Equals((string?)option.Value, current, StringComparison.OrdinalIgnoreCase)))
            options.Insert(0, new PropertyOption($"{current} (not in the project)", current));

        return options;
    }

    private static void Set(VisionNode node, ParamDescriptor parameter, object? value)
    {
        if (value is not null)
            parameter.SetValue(node, value);
    }

    private static object? Parse(string? text, Type type)
    {
        text ??= string.Empty;

        if (type == typeof(string))
            return text;

        if (type == typeof(int))
            return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) ? whole : null;

        if (type == typeof(double))
            return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

        return null;
    }

    /// <summary>A choice as a person reads it: "Int32 (DINT)" for a PLC type, the enum's name otherwise.</summary>
    internal static string Label(object value) => value switch
    {
        PlcType type => PlcTypeLabel(type),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>A PLC type with the IEC 61131-3 name a PLC programmer knows.</summary>
    public static string PlcTypeLabel(PlcType type) => type switch
    {
        PlcType.Bool => "Bool (bit)",
        PlcType.Byte => "Byte",
        PlcType.Int16 => "Int16 (INT)",
        PlcType.UInt16 => "UInt16 (WORD)",
        PlcType.Int32 => "Int32 (DINT)",
        PlcType.UInt32 => "UInt32 (DWORD)",
        PlcType.Real => "Real (32-bit)",
        PlcType.LReal => "LReal (64-bit)",
        PlcType.String => "String",
        _ => type.ToString(),
    };

    // ---- keeping up ------------------------------------------------------------------

    /// <summary>The controller was renamed or reconnected: the title, and the check against its driver.</summary>
    public void ControllerChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PropertyTitle));
        Check();
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphEditorViewModel.SelectedNode))
            ShowProperties();
    }

    private void OnStructureChanged(object? sender, EventArgs e)
    {
        WatchNodes();
        Changed();
    }

    private void OnUndoChanged(object? sender, EventArgs e)
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Engine state and the canvas's own bookkeeping are not changes to the workflow.
        if (e.PropertyName is nameof(VisionNode.Status) or nameof(VisionNode.StatusMessage) or nameof(VisionNode.LastEvaluationTime))
            return;

        Changed();

        if (e.PropertyName == nameof(VisionNode.Name))
            OnPropertyChanged(nameof(PropertyTitle));

        foreach (var category in Properties)
            category.Refresh();
    }

    private void Changed()
    {
        IsModified = true;
        Check();
    }

    /// <summary>The workflow checked as it would be when run - addresses against the driver, stations against the project.</summary>
    private void Check()
    {
        var connection = new PlcConnection(Controller.Driver, Controller.Address, Controller.Options);
        IPlcDriver? driver = null;

        try
        {
            driver = PlcDrivers.Create(connection);
        }
        catch (PlcException)
        {
            // Checked without addresses; the PLC window says the driver is missing.
        }

        var problems = new WorkflowRunner(Graph, driver ?? new SimulatedPlc(), new ProjectStations(_project)).Check();

        HasProblems = problems.Count > 0;
        OnPropertyChanged(nameof(HasProblems));
        ProblemText = problems.Count == 0 ? "Ready to run." : string.Join("   ·   ", problems);
    }

    private void WatchNodes()
    {
        foreach (var node in _watched.Except(Graph.Nodes).ToArray())
        {
            node.PropertyChanged -= OnNodeChanged;
            _watched.Remove(node);
        }

        foreach (var node in Graph.Nodes.Except(_watched).ToArray())
        {
            node.PropertyChanged += OnNodeChanged;
            _watched.Add(node);
        }
    }

    public void Dispose()
    {
        foreach (var node in _watched)
            node.PropertyChanged -= OnNodeChanged;

        Graph.StructureChanged -= OnStructureChanged;
        Editor.PropertyChanged -= OnEditorPropertyChanged;
        Document.Undo.Changed -= OnUndoChanged;
        Document.Dispose();
    }

    /// <summary>The project's stations by name, for a check - nothing is asked of them.</summary>
    private sealed class ProjectStations(Project project) : Sirius.Core.Runtime.IStationBus
    {
        public IReadOnlyList<string> Stations => project.Stations.Select(station => station.Name).ToArray();

        public event EventHandler<Sirius.Core.Runtime.InspectionSnapshot>? Inspected
        {
            add { }
            remove { }
        }

        public Task<Sirius.Core.Runtime.InspectionSnapshot?> InspectAsync(string station, Sirius.Core.Runtime.PartValues? part, CancellationToken cancellationToken) =>
            Task.FromResult<Sirius.Core.Runtime.InspectionSnapshot?>(null);
    }
}
