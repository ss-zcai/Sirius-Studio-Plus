using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using Sirius.Core.Nodes;
using Sirius.UI;
using Sirius.UI.Hosting;

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

        // A page or popup in a tab of its own, beside the start screen.
        model.Designers.CollectionChanged += (_, _) => DropClosedScreens();

        // The document in front decides what the toolbar, tools and Properties act on.
        Docking.ActiveContentChanged += (_, _) => FollowFront();
        model.ScreenShown += (_, designer) => ShowScreen(designer);
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BuilderViewModel.MainDesigner))
                FollowMainTitle();
        };
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BuilderViewModel.ToolsTitle))
                ToolsPanel.Title = model.ToolsTitle;
        };
    }

    /// <summary>For tests: the screen's tab, to bring to the front.</summary>
    internal LayoutDocument? ScreenTabForTests => ScreenTab();

    /// <summary>For tests: the tab of a page or popup, if it is open.</summary>
    internal LayoutDocument? TabForTests(ScreenDesignerViewModel designer) =>
        designer.IsStart ? ScreenTab() : _screenTabs.GetValueOrDefault(designer);

    /// <summary>The screen's own tab.</summary>
    private LayoutDocument? ScreenTab() =>
        Docking.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(document => document.ContentId == "screen");

    /// <summary>
    /// A document coming to the front makes it the one the toolbar, tools
    /// and Properties act on: the screen's designer, or a workflow's editor.
    /// A panel - Properties, Project - coming to the front changes nothing:
    /// the document behind it is still the one being worked on.
    /// </summary>
    /// <remarks>
    /// Read from what the docking manager has in front, not from each tab:
    /// restoring the saved layout at start makes the tabs anew, and a tab
    /// listened to before that was no longer the one on screen - so coming
    /// back to the screen went unnoticed, and Properties stayed on a
    /// workflow's node.
    /// </remarks>
    internal void FollowFront()
    {
        switch (Docking.ActiveContent)
        {
            case WorkflowEditorView { DataContext: WorkflowDocumentViewModel workflow }:
                _model.ActiveWorkflow = workflow;
                break;

            case ScreenDesignerView { DataContext: ScreenDesignerViewModel designer }:
                _model.ShowingDesigner(designer);
                break;
        }
    }

    /// <summary>A screen's tab to the front: the start screen's, or a page's or popup's - made the first time.</summary>
    internal void ShowScreen(ScreenDesignerViewModel designer)
    {
        if (designer.IsStart)
        {
            if (ScreenTab() is { } screen)
                screen.IsActive = true;
        }
        else
        {
            ScreenTabFor(designer).IsActive = true;
        }

        _model.ShowingDesigner(designer);
    }

    // ---- pages and popups, each in a tab ---------------------------------------------

    private readonly Dictionary<ScreenDesignerViewModel, LayoutDocument> _screenTabs = [];
    private System.ComponentModel.PropertyChangedEventHandler? _mainTitle;
    private ScreenDesignerViewModel? _mainTitled;

    /// <summary>The start screen's tab carries its name and a star while it has changes, as the others do.</summary>
    private void FollowMainTitle()
    {
        if (_mainTitled is not null && _mainTitle is not null)
            _mainTitled.PropertyChanged -= _mainTitle;

        _mainTitled = _model.MainDesigner;

        if (_mainTitled is not { } main)
            return;

        _mainTitle = (_, e) =>
        {
            if (e.PropertyName == nameof(ScreenDesignerViewModel.TabTitle) && ScreenTab() is { } tab)
                tab.Title = main.TabTitle;
        };

        main.PropertyChanged += _mainTitle;

        if (ScreenTab() is { } tab)
            tab.Title = main.TabTitle;
    }

    private LayoutDocument ScreenTabFor(ScreenDesignerViewModel designer)
    {
        if (!_screenTabs.TryGetValue(designer, out var tab))
        {
            tab = new LayoutDocument
            {
                Title = designer.TabTitle,
                ContentId = "screen:" + designer.Name,
                CanClose = true,
                Content = new ScreenDesignerView { DataContext = designer },
            };

            designer.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ScreenDesignerViewModel.TabTitle))
                    tab.Title = designer.TabTitle;
                else if (e.PropertyName == nameof(ScreenDesignerViewModel.Name))
                    tab.ContentId = "screen:" + designer.Name;
            };

            // Closing the tab asks to save first, and may be called off.
            tab.Closing += (_, e) =>
            {
                if (_screenTabs.ContainsKey(designer) && !_model.CloseScreen(designer))
                    e.Cancel = true;
            };

            tab.Closed += (_, _) => _screenTabs.Remove(designer);

            _screenTabs[designer] = tab;
        }

        // Made before, but left out of a layout restored since - Reset Layout.
        if (tab.Parent is null)
            DocumentPane().Children.Add(tab);

        return tab;
    }

    /// <summary>Tabs whose screen was closed elsewhere - the project closed, the screen removed.</summary>
    private void DropClosedScreens()
    {
        foreach (var (designer, tab) in _screenTabs.ToArray())
        {
            if (_model.Designers.Contains(designer))
                continue;

            // Forgotten first, so the close below is not taken for the operator closing it.
            _screenTabs.Remove(designer);
            tab.Parent?.RemoveChild(tab);
        }
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

        // A layout saved before there were several screens calls this tab "Screen".
        FollowMainTitle();
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

    /// <summary>Zoom In, Zoom Out and Fit, on the workflow in front.</summary>
    private void OnZoomWorkflowClick(object sender, RoutedEventArgs e)
    {
        if (ActiveWorkflowView is not { } view || sender is not FrameworkElement { Tag: string how })
            return;

        switch (how)
        {
            case "in":
                view.ZoomIn();
                break;

            case "out":
                view.ZoomOut();
                break;

            default:
                view.Fit();
                break;
        }
    }

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

    /// <summary>For tests: the layout written out and read back, as restoring the saved one at start does.</summary>
    internal void ReloadLayoutForTests()
    {
        _panels.Clear();

        foreach (var content in Docking.Layout.Descendents().OfType<LayoutContent>())
        {
            if (!string.IsNullOrEmpty(content.ContentId) && content.Content is { } panel)
                _panels[content.ContentId] = panel;
        }

        var layout = Serialize();
        Restore(serializer => serializer.Deserialize(new StringReader(layout)));
    }

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
        // Workflow and screen tabs are opened from the project, not the saved layout.
        if (args.Model.ContentId is { } id && _panels.TryGetValue(id, out var content))
            args.Content = content;
        else
            args.Cancel = true;
    }

    // ---- stations ----------------------------------------------------------

    /// <summary>A station shows the start screen; a controller, its workflow; a screen, its design.</summary>
    private void OnProjectItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var current = e.OriginalSource as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: StationViewModel or CameraUnitViewModel or ControllerViewModel or ScreenItemViewModel } item)
            {
                _model.Open(item.DataContext);
                return;
            }
        }
    }

    /// <summary>
    /// A station's Camera submenu, as it opens: its recipe's own Camera node,
    /// then each of the project's cameras - the one it uses ticked - and a
    /// way to add one.
    /// </summary>
    private void OnStationCameraMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: StationViewModel station } menu || !ReferenceEquals(e.OriginalSource, menu))
            return;

        menu.Items.Clear();

        foreach (var item in StationCameraItems(station))
            menu.Items.Add(item);
    }

    /// <summary>The station's Camera submenu, made - for the window, and for tests.</summary>
    internal IReadOnlyList<object> StationCameraItems(StationViewModel station)
    {
        var items = new List<object>();

        var own = new MenuItem
        {
            Header = "_The Recipe's Own",
            IsCheckable = true,
            IsChecked = !station.HasCamera,
            ToolTip = "The Camera node in its recipe, as Studio set it",
        };

        own.Click += (_, _) => _model.SetStationCamera(station, null);
        items.Add(own);

        if (_model.Cameras.Count > 0)
            items.Add(new Separator());

        foreach (var camera in _model.Cameras)
        {
            var unit = camera.Camera;
            var item = new MenuItem
            {
                Header = camera.Name.Replace("_", "__", StringComparison.Ordinal),
                IsCheckable = true,
                IsChecked = string.Equals(station.CameraName, unit.Name, StringComparison.OrdinalIgnoreCase),
                ToolTip = camera.DeviceText,
            };

            item.Click += (_, _) => _model.SetStationCamera(station, unit);
            items.Add(item);
        }

        items.Add(new Separator());

        var add = new MenuItem { Header = "_Add Camera…", Command = _model.AddCameraCommand };
        items.Add(add);

        return items;
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

            _workflowTabs[workflow] = tab;
        }

        // Made before, but left out of a layout restored since - Reset Layout.
        if (tab.Parent is null)
            DocumentPane().Children.Add(tab);

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
