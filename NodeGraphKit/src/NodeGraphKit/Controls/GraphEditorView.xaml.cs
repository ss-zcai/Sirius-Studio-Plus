using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Nodify;
using NodeGraphKit.Model;
using NodeGraphKit.ViewModels;

namespace NodeGraphKit.Controls;

/// <summary>Raised before a node's right-click menu opens, so the host can add its own items.</summary>
public sealed class NodeMenuEventArgs(NodeViewModel node, ContextMenu menu) : EventArgs
{
    public NodeViewModel Node { get; } = node;

    public ContextMenu Menu { get; } = menu;
}

/// <summary>
/// The node canvas, styled as Sirius Studio Plus. Bind its DataContext to a
/// <see cref="GraphEditorViewModel"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Drag from an output to an input to wire; drag a wire's end off to remove it; Alt+click a socket to clear it.</item>
/// <item>Left-drag on the canvas selects; right-drag pans; the wheel zooms.</item>
/// <item>Double-click a caption, or press F2, to rename. Double-click elsewhere on a node raises <see cref="NodeDoubleClick"/>.</item>
/// <item>Right-click a node for Rename, Duplicate, Delete and Style; right-click the canvas to add a node there.</item>
/// <item>Drop a toolbox entry to add that node where it lands.</item>
/// </list>
/// </remarks>
public partial class GraphEditorView : UserControl
{
    /// <summary>The drag-and-drop format a toolbox entry travels in: a <see cref="NodeDefinition"/>.</summary>
    public const string DragFormat = "NodeGraphKit.NodeDefinition";

    private Point _rightButtonDown;

    public GraphEditorView()
    {
        InitializeComponent();

        // Added in code so they run even though the editor has already
        // handled the right button - it claims it for panning, and XAML
        // cannot ask for handled events.
        Editor.AddHandler(MouseRightButtonDownEvent, new MouseButtonEventHandler(OnRightButtonDown), handledEventsToo: true);
        Editor.AddHandler(MouseRightButtonUpEvent, new MouseButtonEventHandler(OnRightButtonUp), handledEventsToo: true);

        PreviewKeyDown += OnPreviewKeyDown;
        DataContextChanged += OnDataContextChanged;
        Editor.SizeChanged += (_, _) => FitIfPending();
    }

    /// <summary>
    /// Fit the graph to the window whenever one is opened - when the view
    /// first shows, and after <see cref="GraphEditorViewModel.Load"/> or
    /// <see cref="GraphEditorViewModel.New"/>. On by default.
    /// </summary>
    public bool FitOnOpen { get; set; } = true;

    private bool _fitPending;

    /// <summary>The Nodify editor inside, for anything the view does not expose.</summary>
    public NodifyEditor Canvas => Editor;

    private GraphEditorViewModel? ViewModel => DataContext as GraphEditorViewModel;

    /// <summary>A double click on a node anywhere but its caption.</summary>
    public event EventHandler<NodeViewModel>? NodeDoubleClick;

    /// <summary>A node's right-click menu is about to open; add to <see cref="NodeMenuEventArgs.Menu"/>.</summary>
    public event EventHandler<NodeMenuEventArgs>? NodeMenuOpening;

    /// <summary>Adds a node in the middle of what is on screen, and scrolls to it.</summary>
    public NodeViewModel? AddAtCentre(NodeDefinition definition)
    {
        if (ViewModel is not { } model)
            return null;

        var added = model.AddNode(definition, ViewportCentre());
        Editor.BringIntoView(added.Location);
        return added;
    }

    /// <summary>
    /// The middle of the visible canvas in graph space, less half a node so a
    /// node placed there is centred rather than starting at the middle.
    /// </summary>
    public Point ViewportCentre()
    {
        var size = Editor.ViewportSize;
        var origin = Editor.ViewportLocation;

        if (size.Width <= 0 || size.Height <= 0)
            return new Point(origin.X + 60, origin.Y + 60);

        return new Point(origin.X + (size.Width / 2) - 90, origin.Y + (size.Height / 2) - 30);
    }

    /// <summary>
    /// Frames every node, with a margin, and never closer than 1:1 - so a big
    /// graph shrinks to fit and a small one sits in the middle at full size.
    /// </summary>
    public void FitToScreen()
    {
        var (location, zoom) = ViewportFrame.Frame(
            Editor.ItemsExtent,
            new Size(Editor.ActualWidth, Editor.ActualHeight),
            Editor.MinViewportZoom,
            Math.Min(ViewportFrame.MaxZoom, Editor.MaxViewportZoom));

        Editor.ViewportZoom = zoom;
        Editor.ViewportLocation = location;
    }

    /// <summary>
    /// Fits once the canvas can say where its nodes are.
    /// </summary>
    /// <remarks>
    /// The canvas knows its nodes' extent only after it has laid them out,
    /// and its own size only once it is on screen - which, inside a docking
    /// panel, is later than the window's Loaded event. Fitting before either
    /// frames nothing in the wrong-sized box and leaves the graph shrunk in a
    /// corner. So this waits for a size, then for the layout pass after it:
    /// Loaded priority runs after layout.
    /// </remarks>
    public void FitWhenReady()
    {
        _fitPending = true;
        FitIfPending();
    }

    private void FitIfPending()
    {
        if (!_fitPending || Editor.ActualWidth <= 0 || Editor.ActualHeight <= 0)
            return;

        _fitPending = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FitToScreen);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is GraphEditorViewModel old)
            old.GraphReplaced -= OnGraphReplaced;

        if (e.NewValue is GraphEditorViewModel model)
        {
            model.GraphReplaced += OnGraphReplaced;

            if (FitOnOpen)
                FitWhenReady();
        }
    }

    private void OnGraphReplaced(object? sender, EventArgs e)
    {
        if (FitOnOpen)
            FitWhenReady();
    }

    public void ZoomIn() => Editor.ZoomIn();

    public void ZoomOut() => Editor.ZoomOut();

    /// <summary>A point on this view, in graph space.</summary>
    private Point ToGraph(Point position) => new(
        Editor.ViewportLocation.X + (position.X / Editor.ViewportZoom),
        Editor.ViewportLocation.Y + (position.Y / Editor.ViewportZoom));

    // ---- toolbox drops -----------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (ViewModel is not { } model || e.Data.GetData(DragFormat) is not NodeDefinition definition)
            return;

        model.AddNode(definition, ToGraph(e.GetPosition(Editor)));
        e.Handled = true;
    }

    // ---- double click and rename ---------------------------------------------

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        if (VisualTreeSearch.DataContextOf<ConnectorViewModel>(source) is not null)
            return;

        if (VisualTreeSearch.DataContextOf<NodeViewModel>(source) is not { } node)
            return;

        if (VisualTreeSearch.HasTaggedAncestor(source, "NodeTitle", typeof(Node)))
            node.IsEditingName = true;
        else
            NodeDoubleClick?.Invoke(this, node);

        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && ViewModel?.SelectedNode is { } node && e.OriginalSource is not TextBox)
        {
            node.IsEditingName = true;
            e.Handled = true;
        }
    }

    private void OnNameEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsVisible)
            return;

        // Deferred: the box is not focusable yet at the moment it appears.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    private void OnNameEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: NodeViewModel node } box)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                node.IsEditingName = false;
                Editor.Focus();
                e.Handled = true;
                break;

            case Key.Escape:
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
                node.IsEditingName = false;
                Editor.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnNameEditorLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: NodeViewModel node })
            node.IsEditingName = false;
    }

    // ---- edit commands -----------------------------------------------------

    private void OnEditCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (Resolve(e.Command) is { } command && command.CanExecute(null))
            command.Execute(null);

        e.Handled = true;
    }

    private void OnEditCommandCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = Resolve(e.Command)?.CanExecute(null) == true;
        e.Handled = true;
    }

    private IRelayCommand? Resolve(ICommand command)
    {
        if (ViewModel is not { } model)
            return null;

        return command switch
        {
            _ when command == ApplicationCommands.Undo => model.UndoCommand,
            _ when command == ApplicationCommands.Redo => model.RedoCommand,
            _ when command == ApplicationCommands.Cut => model.CutCommand,
            _ when command == ApplicationCommands.Copy => model.CopyCommand,
            _ when command == ApplicationCommands.Paste => model.PasteCommand,
            _ when command == ApplicationCommands.SelectAll => model.SelectAllCommand,
            _ when command == ApplicationCommands.Delete => model.DeleteSelectionCommand,
            _ => null,
        };
    }

    // ---- right-click menus ---------------------------------------------------

    private void OnRightButtonDown(object sender, MouseButtonEventArgs e) =>
        _rightButtonDown = e.GetPosition(Editor);

    /// <summary>
    /// Opens a menu for the node, or the canvas, under the pointer - only when
    /// the button did not travel, since a right-drag pans.
    /// </summary>
    /// <remarks>
    /// Asked of the geometry rather than the event: the editor captures the
    /// mouse for panning, so every right-button event reports the editor as
    /// its source. Opened on the bubbling pass, after the editor has taken
    /// the focus, or the menu would close again at once.
    /// </remarks>
    private void OnRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } model)
            return;

        var position = e.GetPosition(Editor);
        if ((position - _rightButtonDown).Length > 4)
            return;

        var hit = Editor.InputHitTest(position) as DependencyObject;

        // A wire has a menu of its own.
        if (VisualTreeSearch.DataContextOf<ConnectionViewModel>(hit) is not null)
            return;

        var menu = new ContextMenu { PlacementTarget = Editor, Placement = PlacementMode.MousePoint };

        if (VisualTreeSearch.DataContextOf<NodeViewModel>(hit) is { } node)
        {
            if (!node.IsSelected)
                model.SelectOnly([node]);

            BuildNodeMenu(menu, model, node);
            NodeMenuOpening?.Invoke(this, new NodeMenuEventArgs(node, menu));
        }
        else
        {
            BuildCanvasMenu(menu, model, ToGraph(position));
        }

        menu.IsOpen = true;
    }

    private static void BuildNodeMenu(ContextMenu menu, GraphEditorViewModel model, NodeViewModel node)
    {
        menu.Items.Add(Item("_Rename", "F2", () => node.IsEditingName = true));
        menu.Items.Add(Item("D_uplicate", "Ctrl+D", model.DuplicateCommand.Execute, "ph_copy"));
        menu.Items.Add(Item("_Delete", "Del", model.DeleteSelectionCommand.Execute, "ph_delete"));
        menu.Items.Add(new Separator());

        // A swatch per colour, the current one framed. Applies to the whole
        // selection: marking up a branch means marking several at once.
        var style = new MenuItem { Header = "_Style", Icon = Icon("ph_style") };

        foreach (var choice in node.AccentChoices)
        {
            var accent = choice.Accent;
            var item = new MenuItem
            {
                Header = choice.Name,
                ToolTip = choice.Description,
                IsCheckable = true,
                IsChecked = choice.IsCurrent,
                Icon = new Border
                {
                    Width = 14,
                    Height = 12,
                    CornerRadius = new CornerRadius(2),
                    Background = choice.Background,
                    BorderBrush = choice.Border,
                    BorderThickness = new Thickness(2),
                },
            };

            item.Click += (_, _) => model.SetAccentCommand.Execute(accent);
            style.Items.Add(item);
        }

        menu.Items.Add(style);
    }

    private void BuildCanvasMenu(ContextMenu menu, GraphEditorViewModel model, Point location)
    {
        var add = new MenuItem { Header = "_Add Node", Icon = Icon("ph_new") };

        foreach (var group in model.Catalog.Definitions.GroupBy(d => d.Category))
        {
            var category = new MenuItem { Header = group.Key };

            foreach (var definition in group)
            {
                var item = Item(definition.DisplayName, null, () => model.AddNode(definition, location), definition.Icon);
                item.ToolTip = definition.Description;
                category.Items.Add(item);
            }

            add.Items.Add(category);
        }

        menu.Items.Add(add);
        menu.Items.Add(new Separator());

        var paste = Item("_Paste", "Ctrl+V", model.PasteCommand.Execute, "ph_paste");
        paste.IsEnabled = model.PasteCommand.CanExecute(null);
        menu.Items.Add(paste);

        var selectAll = Item("Select _All", "Ctrl+A", model.SelectAllCommand.Execute, "ph_select_all");
        selectAll.IsEnabled = model.SelectAllCommand.CanExecute(null);
        menu.Items.Add(selectAll);

        menu.Items.Add(Item("_Fit to Screen", null, FitToScreen, "ph_zoom_fit"));
    }

    private static MenuItem Item(string header, string? gesture, Action action, string? icon = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? string.Empty, Icon = Icon(icon) };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Item(string header, string? gesture, Action<object?> action, string? icon = null) =>
        Item(header, gesture, () => action(null), icon);

    private static Image? Icon(string? key) =>
        key is null
            ? null
            : new Image
            {
                Width = 14,
                Height = 14,
                Source = new IconKeyToImageConverter().Convert(key, typeof(object), null!, System.Globalization.CultureInfo.InvariantCulture) as System.Windows.Media.ImageSource,
            };
}
