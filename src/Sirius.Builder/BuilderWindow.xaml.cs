using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using Sirius.Core.Nodes;
using Sirius.Studio.App;
using Sirius.Studio.App.Hosting;

namespace Sirius.Builder;

/// <summary>Sirius Builder's window.</summary>
/// <remarks>
/// Laid out as Studio is - menu, toolbar, docking panels - and the panels
/// are put back where they were left, from a layout file of Builder's own.
/// </remarks>
public partial class BuilderWindow : Window
{
    private readonly BuilderViewModel _model;
    private readonly Dictionary<string, object> _panels = [];
    private string? _defaultLayout;

    private Point _toolDownAt;
    private WidgetTool? _pressedTool;

    public BuilderWindow(BuilderViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _model = model;
        DataContext = model;

        // A workflow in a tab beside the screen.
        model.WorkflowShown += (_, workflow) => ShowWorkflow(workflow);
        model.Workflows.CollectionChanged += (_, _) => DropClosedWorkflows();

        // The document in front decides what the toolbar, tools and Properties act on.
        Follow(ScreenTab());
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BuilderViewModel.ToolsTitle))
                ToolsPanel.Title = model.ToolsTitle;
        };
    }

    /// <summary>For tests: the screen's tab, to bring to the front.</summary>
    internal LayoutDocument? ScreenTabForTests => ScreenTab();

    /// <summary>The screen's own tab.</summary>
    private LayoutDocument? ScreenTab() =>
        Docking.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(document => document.ContentId == "screen");

    /// <summary>
    /// A document tab coming to the front makes its document the one the
    /// toolbar acts on. A panel - Properties, Project - coming to the front
    /// changes nothing: the document behind it is still the one being
    /// worked on.
    /// </summary>
    private void Follow(LayoutDocument? tab)
    {
        if (tab is null)
            return;

        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LayoutDocument.IsActive) && tab.IsActive)
                _model.ActiveWorkflow = (tab.Content as WorkflowEditorView)?.DataContext as WorkflowDocumentViewModel;
        };
    }

    internal BuilderViewModel Model => _model;

    /// <summary>Where the panel arrangement is kept between sessions.</summary>
    internal static string LayoutPath => Path.Combine(SiriusHost.SettingsFolder, "builder-layout.xml");

    /// <summary>For tests: the layout is neither read nor written.</summary>
    internal bool KeepsLayout { get; set; } = true;

    // ---- the window ----------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        foreach (var content in Docking.Layout.Descendents().OfType<LayoutContent>())
        {
            if (!string.IsNullOrEmpty(content.ContentId) && content.Content is { } panel)
                _panels[content.ContentId] = panel;
        }

        _defaultLayout = Serialize();

        if (KeepsLayout && File.Exists(LayoutPath))
            Restore(serializer => serializer.Deserialize(LayoutPath));
    }

    /// <summary>Back in front: a recipe may have been saved in Studio meanwhile.</summary>
    private void OnActivated(object? sender, EventArgs e) => _model.Refresh();

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_model.ConfirmLeaving("Sirius Builder"))
        {
            e.Cancel = true;
            return;
        }

        if (!KeepsLayout)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutPath)!);
            new XmlLayoutSerializer(Docking).Serialize(LayoutPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Only a convenience.
        }
    }

    // ---- panels ------------------------------------------------------------

    private void OnShowPanelClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string contentId })
            ShowPanel(contentId);
    }

    /// <summary>Brings a panel back, hidden or not, and to the front.</summary>
    internal void ShowPanel(string contentId)
    {
        var anchorable = Docking.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == contentId);

        if (anchorable is null)
            return;

        if (anchorable.IsHidden)
            anchorable.Show();

        anchorable.IsActive = true;
    }

    /// <summary>The panels as they were first laid out.</summary>
    private void OnResetLayoutClick(object sender, RoutedEventArgs e)
    {
        if (_defaultLayout is { } layout)
            Restore(serializer => serializer.Deserialize(new StringReader(layout)));
    }

    private void OnAboutClick(object sender, RoutedEventArgs e) =>
        About().ShowDialog();

    /// <summary>Sirius Builder's About: Studio's window, with Builder's name, logo and version.</summary>
    internal AboutWindow About() =>
        new(
            "Sirius Builder",
            TryFindResource("Icon.SiriusBuilder") as ImageSource,
            "Builds Sirius projects: stations, line screens and the settings the line may change.",
            typeof(BuilderWindow).Assembly)
        {
            Owner = IsLoaded ? this : null,
        };

    private string Serialize()
    {
        using var writer = new StringWriter();
        new XmlLayoutSerializer(Docking).Serialize(writer);
        return writer.ToString();
    }

    /// <summary>
    /// Lays the panels out from a saved arrangement, putting each panel's
    /// content back by its id. One from another build is left out; a broken
    /// arrangement, or one missing a panel, gives way to the first layout.
    /// </summary>
    private void Restore(Action<XmlLayoutSerializer> load)
    {
        try
        {
            Load(load);

            if (Docking.Layout.Descendents().OfType<LayoutContent>().Count(content => _panels.ContainsKey(content.ContentId ?? string.Empty)) == _panels.Count)
                return;
        }
        catch (Exception)
        {
            // Falls back below.
        }

        if (_defaultLayout is { } layout)
            Load(serializer => serializer.Deserialize(new StringReader(layout)));
    }

    private void Load(Action<XmlLayoutSerializer> load)
    {
        var serializer = new XmlLayoutSerializer(Docking);
        serializer.LayoutSerializationCallback += OnRestoring;
        load(serializer);
    }

    private void OnRestoring(object? sender, LayoutSerializationCallbackEventArgs args)
    {
        // Workflow tabs are opened from the project, not the saved layout.
        if (args.Model.ContentId is { } id && _panels.TryGetValue(id, out var content))
            args.Content = content;
        else
            args.Cancel = true;
    }

    // ---- stations ----------------------------------------------------------

    /// <summary>A station opens in Studio; a controller, its workflow here.</summary>
    private void OnProjectItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var current = e.OriginalSource as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: StationViewModel or ControllerViewModel } item)
            {
                _model.Open(item.DataContext);
                return;
            }
        }
    }

    /// <summary>The + button: a menu of what can be added, under it.</summary>
    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        AddMenu.PlacementTarget = AddButton;
        AddMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        AddMenu.DataContext = DataContext;
        AddMenu.IsOpen = true;
    }

    // ---- controllers and their workflows -------------------------------------------

    private readonly Dictionary<WorkflowDocumentViewModel, LayoutDocument> _workflowTabs = [];

    /// <summary>The workflow's tab, beside the screen's - made the first time, brought forward after.</summary>
    internal LayoutDocument ShowWorkflow(WorkflowDocumentViewModel workflow)
    {
        if (!_workflowTabs.TryGetValue(workflow, out var tab))
        {
            tab = new LayoutDocument
            {
                Title = workflow.Title,
                ContentId = workflow.ContentId,
                CanClose = true,
                Content = new WorkflowEditorView { DataContext = workflow },
            };

            workflow.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(WorkflowDocumentViewModel.Title))
                    tab.Title = workflow.Title;
            };

            // Closing the tab asks to save first, and may be called off.
            tab.Closing += (_, e) =>
            {
                if (_workflowTabs.ContainsKey(workflow) && !_model.CloseWorkflow(workflow))
                    e.Cancel = true;
            };

            tab.Closed += (_, _) => _workflowTabs.Remove(workflow);

            Follow(tab);
            _workflowTabs[workflow] = tab;
            DocumentPane().Children.Add(tab);
        }

        tab.IsActive = true;
        _model.ActiveWorkflow = workflow;
        return tab;
    }

    /// <summary>Tabs whose workflow was closed elsewhere - the project closed, the controller removed.</summary>
    private void DropClosedWorkflows()
    {
        foreach (var (workflow, tab) in _workflowTabs.ToArray())
        {
            if (_model.Workflows.Contains(workflow))
                continue;

            // Forgotten first, so the close below is not taken for the operator closing it.
            _workflowTabs.Remove(workflow);
            tab.Parent?.RemoveChild(tab);
        }
    }

    /// <summary>The pane the screen's tab is in, where workflows open beside it.</summary>
    private LayoutDocumentPane DocumentPane() =>
        Docking.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(document => document.ContentId == "screen")?.Parent as LayoutDocumentPane
        ?? Docking.Layout.Descendents().OfType<LayoutDocumentPane>().First();

    // ---- the tools panel: a workflow's nodes ---------------------------------------------

    private NodeDescriptor? _pressedNode;

    /// <summary>The editor of the workflow in front, if its tab has one.</summary>
    private WorkflowEditorView? ActiveWorkflowView =>
        _model.ActiveWorkflow is { } workflow && _workflowTabs.TryGetValue(workflow, out var tab) ? tab.Content as WorkflowEditorView : null;

    /// <summary>Double-click adds the node where the canvas is looking; a press is otherwise the start of a drag.</summary>
    private void OnNodeToolMouseDown(object sender, MouseButtonEventArgs e)
    {
        _toolDownAt = e.GetPosition(this);
        _pressedNode = (sender as FrameworkElement)?.DataContext as NodeDescriptor;

        if (e.ClickCount == 2 && _pressedNode is { } descriptor && ActiveWorkflowView is { } view)
        {
            view.AddInView(descriptor);
            _pressedNode = null;
            e.Handled = true;
        }
    }

    private void OnNodeToolMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressedNode is not { } descriptor)
            return;

        var moved = e.GetPosition(this) - _toolDownAt;

        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressedNode = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(WorkflowEditorView.DragFormat, descriptor), DragDropEffects.Copy);
    }

    // ---- the Widgets panel ---------------------------------------------------

    /// <summary>
    /// Double-click adds the widget; a press is otherwise only the start of a
    /// possible drag. Nothing is added on a single click, so a drag that
    /// ends on the screen adds exactly one.
    /// </summary>
    private void OnWidgetToolMouseDown(object sender, MouseButtonEventArgs e)
    {
        _toolDownAt = e.GetPosition(this);
        _pressedTool = (sender as FrameworkElement)?.DataContext as WidgetTool;

        if (e.ClickCount == 2 && _pressedTool is { } tool && _model.Designer is { } designer)
        {
            designer.Add(tool.Kind);
            _pressedTool = null;
            e.Handled = true;
        }
    }

    private void OnWidgetToolMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressedTool is not { } tool)
            return;

        var moved = e.GetPosition(this) - _toolDownAt;

        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressedTool = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(ScreenDesignerView.DragFormat, tool.Kind), DragDropEffects.Copy);
    }
}
