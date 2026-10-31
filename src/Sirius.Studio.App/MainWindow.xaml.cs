using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Collections.Specialized;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock;
using Sirius.Core.Graph;
using Sirius.Studio.App.Diagnostics;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using Sirius.Studio.App.Layout;
using Nodify;
using Sirius.Studio.App.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace Sirius.Studio.App;

public partial class MainWindow : Window
{
    /// <summary>
    /// Panel content captured at startup, keyed by ContentId.
    /// </summary>
    /// <remarks>
    /// Restoring a saved layout rebuilds the pane tree from XML, which carries
    /// only identifiers - not the controls. Without this map every panel would
    /// come back empty, so the deserializer is given back the live content for
    /// each id it encounters.
    /// </remarks>
    private readonly Dictionary<string, object> _panelContent = new(StringComparer.Ordinal);

    /// <summary>
    /// Canonical panel titles from the XAML, keyed by ContentId.
    /// </summary>
    /// <remarks>
    /// A saved layout stores each panel's title, so restoring one puts the old
    /// title back and a rename in the XAML appears to do nothing. These are
    /// re-applied after every restore so the code stays the source of truth.
    /// </remarks>
    private readonly Dictionary<string, string> _panelTitles = new(StringComparer.Ordinal);

    private readonly string _layoutPath;

    private readonly string _placementPath;

    private readonly ObservableCollection<PanelViewModel>? _nodePanels;

    /// <summary>Dockable panels created for panel-producing nodes, by ContentId.</summary>
    private readonly Dictionary<string, LayoutAnchorable> _panelAnchorables = new(StringComparer.Ordinal);

    /// <summary>Panels whose title is already being followed.</summary>
    private readonly HashSet<PanelViewModel> _titleBound = new();

    /// <summary>
    /// Every graph editor the template has made - one per tab - found by
    /// the document it is showing.
    /// </summary>
    /// <remarks>
    /// Looked up by data context rather than kept in a map, because the
    /// recipe's editor is reused when a new recipe is opened: its document
    /// changes underneath it.
    /// </remarks>
    private readonly HashSet<NodifyEditor> _graphEditors = new();

    /// <summary>Sub-graph tabs, by the document each shows.</summary>
    private readonly Dictionary<GraphDocumentViewModel, LayoutDocument> _documentTabs = new();

    /// <summary>Documents whose tab has been framed on the nodes once, as the recipe's is on opening.</summary>
    private readonly HashSet<GraphDocumentViewModel> _framed = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();

        _layoutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SiriusStudioPlus",
            "layout.xml");

        _placementPath = Path.Combine(
            Path.GetDirectoryName(_layoutPath)!,
            "window.json");

        // Before the window is shown, so it opens where it was rather than
        // appearing centred and then jumping.
        WindowPlacement.Read(_placementPath)?.ApplyTo(this, WindowPlacement.Desktop);

        // The title bar is drawn by Windows, not WPF, so it has to be told
        // separately to use the dark variant.
        DarkTitleBar.Apply(this);

        CapturePanelContent();

        if (DataContext is MainViewModel viewModel)
        {
            _nodePanels = viewModel.NodePanels;
            _nodePanels.CollectionChanged += OnNodePanelsChanged;

            // The arrangement of viewers belongs to the recipe; only the
            // window knows how to write a docking layout down.
            viewModel.LayoutSaveRequested += (_, path) => SaveLayout(path);
            viewModel.LayoutRestoreRequested += (_, path) => RestoreLayout(path);
            viewModel.GraphLoaded += (_, _) => FrameGraph();
            viewModel.PanelFocusRequested += (_, contentId) => ShowPanel(contentId);

            viewModel.OpenSubGraphs.CollectionChanged += (_, _) => SyncDocumentTabs();
            viewModel.DocumentFocusRequested += (_, document) => FocusDocument(document);

            // A new recipe is a new document in the same tab.
            MainGraphHost.Content = viewModel.MainDocument;
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.MainDocument))
                    MainGraphHost.Content = viewModel.MainDocument;
            };
        }

        // Which tab is being worked in decides what the properties panel,
        // the Edit menu and the toolbox act on.
        Docking.ActiveContentChanged += OnActiveContentChanged;
        Docking.DocumentClosed += OnDocumentClosed;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void CapturePanelContent()
    {
        foreach (var content in Docking.Layout.Descendents().OfType<LayoutContent>())
        {
            if (string.IsNullOrEmpty(content.ContentId))
                continue;

            _panelContent[content.ContentId] = content.Content;
            _panelTitles[content.ContentId] = content.Title;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Register panel content, restore the saved layout, then sync again.
        //
        // The first pass is what lets a saved layout reattach panels by
        // ContentId. The second is essential: deserializing rebuilds the whole
        // pane tree, so any panel the saved layout did not mention - a node
        // added since it was written - is left detached and has to be put back.
        SyncNodeAnchorables();
        RestoreLayout();
        SyncNodeAnchorables();

        // The recipe loaded before the window had a size, so whatever framing
        // was attempted then had nothing to work with.
        FrameGraph();

        CheckNativeLibraries();
    }

    /// <summary>
    /// Says at once, and with the fix, when OpenCV cannot load on this PC -
    /// rather than leaving every image node to fail with a message that
    /// names the symptom.
    /// </summary>
    /// <remarks>
    /// Off the UI thread: loading the native library is the slow part of
    /// the first run either way, and the window should not wait for it.
    /// </remarks>
    private async void CheckNativeLibraries()
    {
        // Camera drivers too, off the UI thread: the first search for GigE
        // cameras waits for them to answer.
        _ = Task.Run(() => Sirius.Vision.Cameras.CameraDrivers.LoadInstalled());

        var problem = await Task.Run(Sirius.Vision.NativeLibraries.Problem).ConfigureAwait(true);

        if (problem is not null)
            Dialog.Warn(problem, "Image processing unavailable", this);
    }

    /// <summary>
    /// Points the editor at the nodes.
    /// </summary>
    /// <remarks>
    /// Deferred to a later dispatcher pass because the editor reports an
    /// extent only once it has realised an item container for each node, and
    /// a graph assigned a moment ago has none. Running at Loaded priority
    /// puts this after the layout pass that creates them.
    /// </remarks>
    private void FrameGraph()
    {
        if (DataContext is MainViewModel model)
            FrameGraph(model.MainDocument);
    }

    private void FrameGraph(GraphDocumentViewModel document) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (EditorFor(document) is not { } editor)
                return;

            var extent = editor.ItemsExtent;
            var screen = new Size(editor.ActualWidth, editor.ActualHeight);

            var (location, zoom) = GraphView.Frame(
                extent,
                screen,
                editor.MinViewportZoom,
                Math.Min(GraphView.MaxZoom, editor.MaxViewportZoom));

            editor.ViewportZoom = zoom;
            editor.ViewportLocation = location;
        });

    /// <summary>The editor showing <paramref name="document"/>, once its tab has been shown.</summary>
    private NodifyEditor? EditorFor(GraphDocumentViewModel? document) =>
        document is null
            ? null
            : _graphEditors.FirstOrDefault(editor => ReferenceEquals(editor.DataContext, document));

    /// <summary>The editor of the tab being worked in.</summary>
    private NodifyEditor? ActiveEditor => EditorFor((DataContext as MainViewModel)?.ActiveDocument);

    /// <summary>
    /// Registers each editor the template makes, and gives it the
    /// right-button handlers.
    /// </summary>
    /// <remarks>
    /// The handlers are added in code rather than in the markup so they run
    /// even when the editor has already marked the event handled - which it
    /// does, because it claims the right button for panning. An attribute in
    /// XAML cannot ask for that. Loaded fires again each time a tab is shown,
    /// so the set keeps them from being added twice.
    /// </remarks>
    private void OnGraphEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not NodifyEditor editor || !_graphEditors.Add(editor))
            return;

        editor.AddHandler(
            MouseRightButtonDownEvent,
            new MouseButtonEventHandler(OnGraphRightButtonDown),
            handledEventsToo: true);

        editor.AddHandler(
            MouseRightButtonUpEvent,
            new MouseButtonEventHandler(OnGraphRightButtonUp),
            handledEventsToo: true);
    }

    // ---- sub-graph tabs ----------------------------------------------------

    /// <summary>
    /// Keeps a document tab for every open sub-graph, beside the recipe's.
    /// </summary>
    private void SyncDocumentTabs()
    {
        if (DataContext is not MainViewModel model)
            return;

        var open = model.OpenSubGraphs.ToHashSet();

        foreach (var (document, tab) in _documentTabs.ToArray())
        {
            if (open.Contains(document))
                continue;

            // Forgotten first, so the close below is not mistaken for the
            // operator closing it.
            _documentTabs.Remove(document);

            if (tab.Root is not null)
                tab.Close();
        }

        foreach (var document in model.OpenSubGraphs)
        {
            if (!_documentTabs.ContainsKey(document))
                _documentTabs[document] = CreateDocumentTab(document);
        }
    }

    private LayoutDocument CreateDocumentTab(GraphDocumentViewModel document)
    {
        var tab = new LayoutDocument
        {
            Title = document.Title,
            ContentId = document.ContentId,
            CanClose = true,
            Content = new ContentControl
            {
                Content = document,
                ContentTemplate = (DataTemplate)FindResource("GraphEditorTemplate"),
                Focusable = false,
            },
        };

        // The caption follows the sub-graph node's name.
        document.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(GraphDocumentViewModel.Title))
                tab.Title = document.Title;
        };

        DocumentPane().Children.Add(tab);
        return tab;
    }

    /// <summary>The pane the recipe's own tab is in, where sub-graph tabs open beside it.</summary>
    private LayoutDocumentPane DocumentPane()
    {
        var documents = Docking.Layout.Descendents().OfType<LayoutDocument>().ToArray();

        return documents.FirstOrDefault(document => document.ContentId == "graph")?.Parent as LayoutDocumentPane
               ?? Docking.Layout.Descendents().OfType<LayoutDocumentPane>().First();
    }

    private void FocusDocument(GraphDocumentViewModel document)
    {
        SyncDocumentTabs();

        if (!_documentTabs.TryGetValue(document, out var tab))
            return;

        tab.IsSelected = true;
        tab.IsActive = true;

        // A sub-graph opens looking at its nodes, as a recipe does.
        if (_framed.Add(document))
            FrameGraph(document);
    }

    private void OnActiveContentChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel model &&
            Docking.ActiveContent is ContentControl { Content: GraphDocumentViewModel document })
        {
            model.ActivateDocument(document);
        }
    }

    /// <summary>The operator closed a sub-graph's tab.</summary>
    private void OnDocumentClosed(object? sender, DocumentClosedEventArgs e)
    {
        var closed = _documentTabs.FirstOrDefault(pair => ReferenceEquals(pair.Value, e.Document));

        if (closed.Key is null || DataContext is not MainViewModel model)
            return;

        _documentTabs.Remove(closed.Key);
        _framed.Remove(closed.Key);
        model.CloseSubGraph(closed.Key);
    }

    private void OnNodePanelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Trace($"panels changed: {e.Action}, now {_nodePanels?.Count ?? 0} panel(s)");
        SyncNodeAnchorables();
    }

    /// <summary>
    /// Appends a line to the diagnostics log beside the saved layout.
    /// </summary>
    /// <remarks>
    /// Panel creation spans the view-model, the collection notification and the
    /// docking layout, and a failure anywhere in that chain shows up only as a
    /// panel that never appears. This records the chain so the break can be
    /// located without guessing.
    /// </remarks>
    private static bool VerbosePanelLogging =>
        Environment.GetEnvironmentVariable("SIRIUS_PANEL_LOG") == "1";

    private void Trace(string message, bool always = false)
    {
        // Verbose tracing is opt-in: panel creation is working, and writing a
        // log on every launch is noise. Problems are always recorded.
        if (!always && !VerbosePanelLogging)
            return;

        try
        {
            var path = Path.Combine(Path.GetDirectoryName(_layoutPath)!, "panels.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Diagnostics must never break the feature they are observing.
        }
    }

    /// <summary>
    /// Creates a dockable panel for every viewer view-model and removes those
    /// whose node has gone.
    /// </summary>
    private void SyncNodeAnchorables()
    {
        if (_nodePanels is null)
            return;

        var live = _nodePanels.ToDictionary(panel => panel.ContentId, StringComparer.Ordinal);
        Trace($"sync: {live.Count} panel(s) wanted, {_panelAnchorables.Count} tracked");

        // Anything no longer part of the layout tree - discarded by a restore,
        // or closed by the operator - is forgotten, so the pass below rebuilds it.
        var attached = Docking.Layout.Descendents().OfType<LayoutAnchorable>().ToHashSet();

        foreach (var (contentId, anchorable) in _panelAnchorables.ToArray())
        {
            if (!attached.Contains(anchorable))
            {
                _panelAnchorables.Remove(contentId);

                // Closing a preview means closing it. Every other panel
                // belongs to a node and is rebuilt below, which is right for
                // them and wrong here: a preview exists because somebody
                // opened it, so leaving the view-model behind made every
                // closed preview reappear the next time anything opened a
                // window.
                if (_nodePanels.FirstOrDefault(panel => panel.ContentId == contentId)
                    is PreviewPanelViewModel closed)
                {
                    _nodePanels.Remove(closed);
                    closed.Dispose();
                    _panelContent.Remove(contentId);
                    _panelTitles.Remove(contentId);
                }

                continue;
            }

            if (live.ContainsKey(contentId))
                continue;

            // Closing can fail if AvalonDock already detached it; the panel is
            // gone either way, and failing here must not stop the pass that
            // creates the new panels below.
            try
            {
                anchorable.Close();
            }
            catch (Exception)
            {
                // Nothing useful to do - it is being forgotten regardless.
            }

            _panelAnchorables.Remove(contentId);
            _panelContent.Remove(contentId);
            _panelTitles.Remove(contentId);
        }

        foreach (var panel in _nodePanels)
        {
            if (_panelAnchorables.ContainsKey(panel.ContentId))
                continue;

            var anchorable = new LayoutAnchorable
            {
                Title = panel.Title,
                ContentId = panel.ContentId,
                Content = panel,
                CanClose = true,
            };

            // Title follows the node name, so renaming a viewer renames its tab.
            BindTitle(panel);

            // Transient windows float; the panels that belong to a recipe
            // dock. A profile chart docked would arrive as a tab in the pane
            // the viewers share, covering the image the line was drawn on,
            // and a preview opened to glance at a node has no business
            // rearranging a layout somebody set up.
            if (panel is ProfileLinePanelViewModel or PreviewPanelViewModel)
                FloatPanel(anchorable);
            else
                PanelLayout.Attach(Docking.Layout, anchorable, message => Trace(message));

            _panelAnchorables[panel.ContentId] = anchorable;
            _panelContent[panel.ContentId] = panel;
            _panelTitles[panel.ContentId] = panel.Title;

            var reachable = Docking.Layout.Descendents().OfType<LayoutAnchorable>().Contains(anchorable);

            Trace(
                $"  created '{panel.Title}' ({panel.ContentId}) reachable={reachable} " +
                $"parent={anchorable.Parent?.GetType().Name ?? "<none>"}",
                always: !reachable);
        }
    }

    /// <summary>
    /// Opens a panel in a window of its own.
    /// </summary>
    /// <remarks>
    /// It has to go through the docking manager. Adding a
    /// <c>LayoutAnchorableFloatingWindow</c> to the layout model directly
    /// describes a floating window without creating one: the model is
    /// consistent, nothing throws, and no window appears. <c>AddToLayout</c>
    /// gives the anchorable a root, which is what <c>Float</c> needs to build
    /// the window control - the same path a panel dragged out by hand takes.
    /// </remarks>
    private void FloatPanel(LayoutAnchorable anchorable)
    {
        var position = NextFloatingPosition();

        anchorable.FloatingLeft = position.X;
        anchorable.FloatingTop = position.Y;
        anchorable.FloatingWidth = PanelLayout.DefaultFloatingSize.Width;
        anchorable.FloatingHeight = PanelLayout.DefaultFloatingSize.Height;

        anchorable.AddToLayout(Docking, AnchorableShowStrategy.Most);
        anchorable.Float();
    }

    /// <summary>
    /// Where the next floating panel opens: inside this window, stepped so a
    /// second one does not land exactly on the first.
    /// </summary>
    private Point NextFloatingPosition()
    {
        var inset = new Point(
            Math.Max(0, (ActualWidth - PanelLayout.DefaultFloatingSize.Width) / 2),
            Math.Max(0, (ActualHeight - PanelLayout.DefaultFloatingSize.Height) / 2));

        // PointToScreen needs a connected visual, and a panel could in
        // principle be wanted before the window is up.
        if (!IsLoaded)
            return PanelLayout.CascadeFrom(new Point(Left + 80, Top + 80), _floatingOpened++);

        // PointToScreen answers in device pixels; a window's Left and Top are
        // device-independent. On a display scaled above 100% the difference
        // would put the chart well off to one side, or off the screen.
        var screen = PointToScreen(inset);
        var toIndependent = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;

        return PanelLayout.CascadeFrom(toIndependent.Transform(screen), _floatingOpened++);
    }

    /// <summary>How many panels have been floated, for the cascade.</summary>
    private int _floatingOpened;

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Asked before the layout is written, so a cancelled close leaves
        // everything exactly as it was.
        if (DataContext is MainViewModel model && !model.ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }

        SaveLayout();
        WindowPlacement.Of(this).Write(_placementPath);
    }

    private void RestoreLayout() => RestoreLayout(_layoutPath);

    /// <summary>
    /// Puts the panels back where a saved layout says they belong.
    /// </summary>
    /// <remarks>
    /// Panels are matched by content id, so this only places the ones that
    /// already exist. Restoring a recipe's layout therefore has to happen
    /// after its graph is loaded - before that, there is nothing to place and
    /// every entry would be discarded.
    /// </remarks>
    private void RestoreLayout(string path)
    {
        if (!File.Exists(path))
            return;

        try
        {
            var serializer = new XmlLayoutSerializer(Docking);

            serializer.LayoutSerializationCallback += (_, args) =>
            {
                if (args.Model.ContentId is { } id && _panelContent.TryGetValue(MapLegacyContentId(id), out var content))
                    args.Content = content;
                else
                    // A panel from an older build that no longer exists: drop it
                    // rather than restoring an empty tab.
                    args.Cancel = true;
            };

            serializer.Deserialize(path);

            // A profile chart is floated under a guid no later session can
            // match, so its content is cancelled above and the window it was
            // in would otherwise be restored empty.
            PanelLayout.PruneEmptyFloatingWindows(Docking.Layout);

            ApplyCanonicalIdentity();
            TrackRestoredAnchorables();

            // Anything the restore dropped - a panel the layout did not
            // mention - still needs a home.
            SyncNodeAnchorables();
        }
        catch (Exception)
        {
            // A corrupt or incompatible layout file must not stop the app
            // opening; the XAML default layout is already in place.
            if (string.Equals(path, _layoutPath, StringComparison.OrdinalIgnoreCase))
                TryDeleteLayout();

            Trace($"could not restore layout from {path}", always: true);
        }
    }

    /// <summary>
    /// Adopts the anchorables a restore created.
    /// </summary>
    /// <remarks>
    /// Deserialising replaces every anchorable with a new object, so the ones
    /// tracked before the restore are now detached orphans. Without this, the
    /// panels on screen belong to nobody: New cannot close them, and opening
    /// a project again builds a second set beside them.
    /// </remarks>
    private void TrackRestoredAnchorables()
    {
        if (_nodePanels is null)
            return;

        var panels = _nodePanels.ToDictionary(panel => panel.ContentId, StringComparer.Ordinal);

        foreach (var anchorable in PanelLayout.NodePanels(Docking.Layout))
        {
            if (anchorable.ContentId is not { } id || !panels.TryGetValue(id, out var panel))
                continue;

            _panelAnchorables[id] = anchorable;
            BindTitle(panel);
        }
    }

    /// <summary>
    /// Keeps a panel's tab title following its node's name.
    /// </summary>
    /// <remarks>
    /// Bound to the panel once, not to each anchorable, and it looks the
    /// anchorable up when it fires. A restore swaps the anchorable underneath,
    /// and a subscription captured at creation would go on retitling the one
    /// that was thrown away.
    /// </remarks>
    private void BindTitle(PanelViewModel panel)
    {
        if (!_titleBound.Add(panel))
            return;

        panel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(PanelViewModel.Title))
                return;

            _panelTitles[panel.ContentId] = panel.Title;

            if (_panelAnchorables.TryGetValue(panel.ContentId, out var anchorable))
                anchorable.Title = panel.Title;
        };
    }

    /// <summary>
    /// Restores each panel's current id and title after a layout is loaded,
    /// overriding whatever the saved file recorded.
    /// </summary>
    private void ApplyCanonicalIdentity()
    {
        foreach (var content in Docking.Layout.Descendents().OfType<LayoutContent>())
        {
            if (content.ContentId is not { } id)
                continue;

            var current = MapLegacyContentId(id);

            // Normalise the id too, or a renamed panel stays unreachable from
            // the View menu, which looks it up by its current id.
            content.ContentId = current;

            if (_panelTitles.TryGetValue(current, out var title))
                content.Title = title;
        }
    }

    /// <summary>
    /// Maps content ids from earlier builds onto their current names, so a
    /// saved layout does not quietly lose a panel that was only renamed.
    /// </summary>
    private static string MapLegacyContentId(string contentId) => contentId switch
    {
        "nodes" => "toolbox",
        _ => contentId,
    };

    private void SaveLayout() => SaveLayout(_layoutPath);

    private void SaveLayout(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            new XmlLayoutSerializer(Docking).Serialize(path);
        }
        catch (Exception)
        {
            // Layout is a convenience; failing to persist it is not worth
            // interrupting shutdown.
        }
    }

    private void TryDeleteLayout()
    {
        try
        {
            if (File.Exists(_layoutPath))
                File.Delete(_layoutPath);
        }
        catch (Exception)
        {
            // Nothing useful to do if it cannot be removed.
        }
    }

    /// <summary>Re-opens a panel, or focuses it when already visible.</summary>
    private void OnShowPanelClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string contentId })
            return;

        var anchorable = Docking.Layout
            .Descendents()
            .OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == contentId);

        if (anchorable is null)
            return;

        if (anchorable.IsHidden)
            anchorable.Show();

        anchorable.IsActive = true;
    }

    /// <summary>
    /// Discards the saved layout and reloads the window, which is simpler and
    /// more predictable than trying to rebuild the pane tree in place.
    /// </summary>
    private void OnResetLayoutClick(object sender, RoutedEventArgs e)
    {
        if (!Dialog.Confirm(
                "Reset all panels to the default layout? The window will reopen.",
                "Reset Layout",
                this))
        {
            return;
        }

        TryDeleteLayout();

        var replacement = new MainWindow();
        Application.Current.MainWindow = replacement;

        // Drop the handler first so closing this window does not write the
        // layout straight back out again.
        Closing -= OnClosing;
        replacement.Show();
        Close();
    }

    // ---- toolbox drag and drop -----------------------------------------

    private const string ToolboxDragFormat = "SiriusStudioPlus.ToolboxItem";

    private Point _toolboxDragOrigin;

    /// <summary>
    /// Fallback payload. A WPF DataObject round-trips arbitrary objects only
    /// within the process, and can refuse a non-serialisable one outright, so
    /// the dragged item is also held here.
    /// </summary>
    private PaletteItemViewModel? _draggedItem;

    private void OnToolboxItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        _toolboxDragOrigin = e.GetPosition(null);
        _draggedItem = (sender as FrameworkElement)?.DataContext as PaletteItemViewModel;

        // Border is a FrameworkElement and has no MouseDoubleClick event, so
        // the second click is recognised here instead.
        if (e.ClickCount == 2)
            AddToolboxItem(_draggedItem, e);
    }

    private void OnToolboxItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedItem is null)
            return;

        // Only start once the pointer has moved past the system drag threshold,
        // otherwise an ordinary click would begin a drag.
        var delta = _toolboxDragOrigin - e.GetPosition(null);
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(ToolboxDragFormat, _draggedItem);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }

    private void OnGraphDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ResolveDraggedItem(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnGraphDrop(object sender, DragEventArgs e)
    {
        if (sender is not NodifyEditor editor ||
            DataContext is not MainViewModel viewModel ||
            editor.DataContext is not GraphDocumentViewModel document ||
            ResolveDraggedItem(e) is not { } item)
        {
            return;
        }

        // Screen point -> graph space. The editor renders the graph offset by
        // ViewportLocation and scaled by ViewportZoom, so the drop point has to
        // be run back through both or the node lands somewhere else entirely.
        var position = e.GetPosition(editor);
        var location = new Point(
            editor.ViewportLocation.X + (position.X / editor.ViewportZoom),
            editor.ViewportLocation.Y + (position.Y / editor.ViewportZoom));

        // Into the tab it was dropped on, whichever was active before.
        viewModel.AddNodeAt(document, item, location);

        _draggedItem = null;
        e.Handled = true;
    }

    // ---- toolbox search ----------------------------------------------------

    /// <summary>
    /// Enter adds the first tool listed, which with a few letters typed is
    /// the one wanted - so a tool can be found and placed without leaving
    /// the keyboard. Esc empties the search.
    /// </summary>
    private void OnToolboxSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel model)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                if (model.FirstToolboxMatch is { } first &&
                    model.AddNodeAt(first, ViewportCentre()) is { } added)
                {
                    ActiveEditor?.BringIntoView(added.Location);

                    // Selected rather than cleared, so Enter again adds
                    // another and typing starts the next search.
                    ToolboxSearchBox.SelectAll();
                }

                e.Handled = true;
                break;

            case Key.Escape:
                model.ToolboxSearch = string.Empty;
                e.Handled = true;
                break;
        }
    }

    private void OnToolboxSearchClearClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model)
            model.ToolboxSearch = string.Empty;

        ToolboxSearchBox.Focus();
    }

    /// <summary>Ctrl+F: brings the toolbox forward and puts the cursor in its search box.</summary>
    private void OnFindCommand(object sender, ExecutedRoutedEventArgs e)
    {
        ShowPanel("toolbox");

        // After the toolbox has been shown, or a hidden one is not yet
        // focusable.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            ToolboxSearchBox.Focus();
            Keyboard.Focus(ToolboxSearchBox);
            ToolboxSearchBox.SelectAll();
        });

        e.Handled = true;
    }

    private PaletteItemViewModel? ResolveDraggedItem(DragEventArgs e) =>
        e.Data.GetDataPresent(ToolboxDragFormat)
            ? e.Data.GetData(ToolboxDragFormat) as PaletteItemViewModel ?? _draggedItem
            : null;

    // ---- menu commands ---------------------------------------------------

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Re-reads the clipboard when the window comes forward.
    /// </summary>
    /// <remarks>
    /// The only way a copy made in another application can arrive. Asking in
    /// the command's CanExecute instead would mean an OLE call on every
    /// requery, which is constant - and the clipboard belongs to whichever
    /// process last opened it, so that call can block for seconds.
    /// </remarks>
    private void OnWindowActivated(object? sender, EventArgs e) =>
        (DataContext as MainViewModel)?.RefreshClipboardState();

    /// <summary>
    /// The routed edit commands, forwarded to the view-model.
    /// </summary>
    /// <remarks>
    /// These exist so that Ctrl+C in a text box copies text and Ctrl+C on
    /// the canvas copies nodes. WPF routes a command from whatever has the
    /// focus upwards, so a TextBox handles its own and marks it handled;
    /// only what nothing closer wanted arrives here. A KeyBinding on the
    /// window would have fired either way.
    /// </remarks>
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
        if (DataContext is not MainViewModel model)
            return null;

        return command switch
        {
            _ when command == ApplicationCommands.Undo => model.UndoCommand,
            _ when command == ApplicationCommands.Redo => model.RedoCommand,
            _ when command == ApplicationCommands.Cut => model.CutCommand,
            _ when command == ApplicationCommands.Copy => model.CopyCommand,
            _ when command == ApplicationCommands.Paste => model.PasteCommand,
            _ when command == ApplicationCommands.SelectAll => model.SelectAllCommand,
            _ => null,
        };
    }

    private void OnAboutClick(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    private void OnCustomNodesClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model)
            new CustomNodesWindow(model.CustomNodes) { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// Opens the generated node reference, which lists every node's ports and
    /// parameters.
    /// </summary>
    private void OnNodeReferenceClick(object sender, RoutedEventArgs e)
    {
        // Walk up from the binaries to the repository root, where docs/ lives.
        var directory = AppContext.BaseDirectory;
        string? found = null;

        for (var depth = 0; depth < 8 && directory is not null; depth++)
        {
            var candidate = Path.Combine(directory, "docs", "node-reference.md");
            if (File.Exists(candidate))
            {
                found = candidate;
                break;
            }

            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
        }

        if (found is null)
        {
            Dialog.Inform(
                "The node reference was not found. Generate it with:" + Environment.NewLine + Environment.NewLine +
                "dotnet run --project tools/NodeDocGen -- docs/node-reference.md",
                "Node Reference",
                this);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(found) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Dialog.Warn(ex.Message, "Node Reference", this);
        }
    }

    /// <summary>
    /// Double-clicking a node opens and focuses its panel, if it has one.
    /// </summary>
    /// <remarks>
    /// Handled here rather than bound in the node template because the graph
    /// can be floated into its own window, where a RelativeSource lookup for
    /// the main window would not find this view-model.
    /// </remarks>
    private void OnGraphDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_nodePanels is null || e.OriginalSource is not DependencyObject source)
            return;

        // A port answers for itself. Double clicking "In" shows what this node
        // was given and "Out" what it made of it, which is the same choice the
        // menu and the properties buttons offer - at the place the operator is
        // already looking when they wonder what is on a wire.
        //
        // Handled either way: a port that carries a number has nothing to
        // show, and falling through to the node's own preview would answer a
        // question nobody asked.
        if (FindDataContext<ConnectorViewModel>(source) is { } connector)
        {
            ShowPreview(connector.Node.Node, connector.Port);
            e.Handled = true;
            return;
        }

        var node = FindNodeViewModel(source);
        if (node is null)
            return;

        // The caption starts an in-place rename instead of opening a panel.
        if (IsWithinNodeCaption(source))
        {
            node.IsEditingName = true;
            e.Handled = true;
            return;
        }

        // A sub-graph opens in a tab of its own.
        if (node.Node is SubGraphNode subGraph && DataContext is MainViewModel model)
        {
            model.OpenSubGraph(subGraph);
            e.Handled = true;
            return;
        }

        // Only node-backed panels answer to a double-click on a node; a
        // profile window belongs to a line, not to anything in the graph.
        //
        // A node's own panel wins over a preview of it: double clicking a
        // Viewer node should raise its viewer, not a preview somebody left
        // open on one of its ports.
        var panel = _nodePanels
            .OfType<NodePanelViewModel>()
            .FirstOrDefault(p => p.Node == node.Node && p is not PreviewPanelViewModel);

        panel ??= _nodePanels
            .OfType<PreviewPanelViewModel>()
            .FirstOrDefault(p => p.Node == node.Node);

        if (panel is not null)
        {
            ShowPanel(panel.ContentId);
            e.Handled = true;
            return;
        }

        // Nothing of its own, so show what it produced. Most nodes are in
        // this case, and before previews existed a double click on them did
        // nothing at all.
        if (MainViewModel.DefaultPreviewPort(node.Node) is { } port)
        {
            ShowPreview(node.Node, port);
            e.Handled = true;
        }
    }

    /// <summary>Where the right button went down, for telling a click from a drag.</summary>
    private Point _rightButtonDown;

    private void OnGraphRightButtonDown(object sender, MouseButtonEventArgs e) =>
        _rightButtonDown = e.GetPosition((IInputElement)sender);

    /// <summary>
    /// Offers a preview of each port of the node under the cursor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opened by hand rather than through <c>ContextMenu</c>, because the
    /// editor claims the right button for panning and a menu declared on the
    /// node never got the chance to open.
    /// </para>
    /// <para>
    /// Only when the button did not travel: right-drag still pans, and a pan
    /// that happens to end over a node must not throw a menu up. The event is
    /// left unhandled so the editor still finishes whatever it started.
    /// </para>
    /// <para>
    /// On the bubbling pass, and asking for events the editor has already
    /// handled, so the editor has finished whatever it does with the click -
    /// including taking the focus, which a menu set not to stay open reads
    /// as a reason to close itself. Opening from the tunnelling pass ran
    /// before that and the menu closed again immediately, which looked like
    /// needing two right clicks.
    /// </para>
    /// </remarks>
    private void OnGraphRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        const double clickSlop = 4;

        if (sender is not NodifyEditor graphEditor)
            return;

        var position = e.GetPosition(graphEditor);

        if ((position - _rightButtonDown).Length > clickSlop)
            return;

        // Asked of the geometry, not of the event. The editor captures the
        // mouse on right-button-down for panning, so from then on every
        // right-button event reports the editor as its source however far
        // into a node the pointer is - which is why walking up from
        // OriginalSource found nothing at all.
        var hit = graphEditor.InputHitTest(position) as DependencyObject;

        if (hit is null || FindNodeViewModel(hit) is not { } node)
        {
            Log.Write($"right click at {position:0.#} hit nothing");
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = graphEditor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        };
        var ports = MainViewModel.PreviewablePorts(node.Node);

        if (ports.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Nothing to preview", IsEnabled = false });
        }

        foreach (var port in ports)
        {
            var item = new MenuItem
            {
                Header = $"Preview _{port.DisplayName}",

                // Which side the port is on decides what the preview means -
                // what this node was given, or what it made of it.
                InputGestureText = port.Direction == PortDirection.Input ? "input" : "output",
            };

            var target = port;
            item.Click += (_, _) => ShowPreview(node.Node, target);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(BuildAccentMenu(node));

        menu.IsOpen = true;

        Log.Write($"right click on '{node.Node.Name}': {ports.Count} port(s) offered");
    }

    /// <summary>
    /// The Style submenu: one item per accent, the current one checked.
    /// </summary>
    /// <remarks>
    /// Offered here as well as on the properties panel because this is
    /// where a graph gets marked up - working across a canvas, not having
    /// selected a node to edit it. Each item carries its own swatch, so the
    /// menu shows the colours rather than naming them.
    ///
    /// A swatch and a name, matching the dropdown on the toolbar. The
    /// descriptions were also shown here, which made the menu twice as
    /// wide as the node it belongs to for the sake of explaining colours
    /// that the swatch beside each one already shows.
    /// </remarks>
    private static MenuItem BuildAccentMenu(NodeViewModel node)
    {
        var parent = new MenuItem { Header = "_Style" };

        foreach (var choice in node.AccentChoices)
        {
            var item = new MenuItem
            {
                Header = choice.Name,
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

            var accent = choice.Accent;
            item.Click += (_, _) => node.Accent = accent;
            parent.Items.Add(item);
        }

        return parent;
    }

    /// <summary>
    /// Opens a preview of one port. The view-model asks for the focus, so
    /// this behaves the same whichever route reached it.
    /// </summary>
    private void ShowPreview(VisionNode node, Port port) =>
        (DataContext as MainViewModel)?.OpenPreview(node, port);

    private static NodeViewModel? FindNodeViewModel(DependencyObject source) =>
        FindDataContext<NodeViewModel>(source);

    /// <summary>
    /// The nearest ancestor presenting a <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// Walking the visual tree rather than binding, because these events are
    /// handled on the editor: the graph can be floated into a window of its
    /// own, where a RelativeSource lookup for the main window finds nothing.
    /// </remarks>
    private static T? FindDataContext<T>(DependencyObject source) where T : class
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: T found })
                return found;
        }

        return null;
    }

    /// <summary>Re-opens a panel if it was closed, then brings it to the front.</summary>
    private void ShowPanel(string contentId)
    {
        // Rebuild it first: a closed panel is no longer in the layout tree.
        SyncNodeAnchorables();

        var anchorable = Docking.Layout
            .Descendents()
            .OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == contentId);

        if (anchorable is null)
            return;

        if (anchorable.IsHidden)
            anchorable.Show();

        anchorable.IsActive = true;
    }

    /// <summary>
    /// Whether a click landed on a node's caption, which is tagged for the
    /// purpose rather than identified by position or element type.
    /// </summary>
    private static bool IsWithinNodeCaption(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Tag: "NodeTitle" })
                return true;

            // Stop at the node itself: a tag further up would not be a caption.
            if (current is Nodify.Node)
                return false;
        }

        return false;
    }

    /// <summary>Selects the text as soon as the editor appears.</summary>
    private void OnNodeNameEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox editor || !editor.IsVisible)
            return;

        // Deferred: the element is not yet focusable at the moment its
        // visibility changes.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            editor.Focus();
            editor.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnNodeNameEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor || editor.DataContext is not NodeViewModel node)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                // Push the edit through before closing, since the binding
                // commits on lost focus.
                editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                node.IsEditingName = false;
                e.Handled = true;
                break;

            case Key.Escape:
                editor.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
                node.IsEditingName = false;
                e.Handled = true;
                break;
        }
    }

    private void OnNodeNameEditorLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: NodeViewModel node })
            node.IsEditingName = false;
    }

    // ---- window chrome ---------------------------------------------------

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>
    /// Adds a toolbox item at the middle of what is currently on screen.
    /// </summary>
    /// <remarks>
    /// Only the view knows where the graph is scrolled to. Placing at fixed
    /// graph coordinates dropped new nodes outside the visible area as soon as
    /// the canvas had been panned, leaving nothing to show for the click.
    /// </remarks>
    private void AddToolboxItem(PaletteItemViewModel? item, MouseButtonEventArgs e)
    {
        if (item is null || DataContext is not MainViewModel viewModel)
            return;

        var added = viewModel.AddNodeAt(item, ViewportCentre());

        // Put it on screen even if the cascade has walked it towards the edge.
        if (added is not null)
            ActiveEditor?.BringIntoView(added.Location);

        // Cancel the drag the first click armed.
        _draggedItem = null;
        e.Handled = true;
    }

    /// <summary>
    /// Middle of the visible canvas, in graph coordinates.
    /// </summary>
    /// <remarks>
    /// The editor draws the graph offset by ViewportLocation and scaled by
    /// ViewportZoom, so a screen-space midpoint has to be run back through
    /// both. Offset by roughly half a node so the node is centred rather than
    /// starting at the middle.
    /// </remarks>
    private Point ViewportCentre()
    {
        if (ActiveEditor is not { } editor)
            return new Point(60, 60);

        // ViewportSize is already in graph units, so no zoom arithmetic is
        // needed - unlike ActualWidth, which is in screen pixels.
        var size = editor.ViewportSize;
        var origin = editor.ViewportLocation;

        if (size.Width <= 0 || size.Height <= 0)
            return new Point(origin.X + 60, origin.Y + 60);

        // Offset by roughly half a node, so it lands centred rather than
        // starting at the middle.
        const double halfNodeWidth = 90;
        const double halfNodeHeight = 30;

        return new Point(
            origin.X + (size.Width / 2) - halfNodeWidth,
            origin.Y + (size.Height / 2) - halfNodeHeight);
    }

    /// <summary>
    /// Opens the help for the selected node.
    /// </summary>
    /// <remarks>
    /// Everything it shows is read off the node, so it cannot disagree with
    /// the tool it describes - and a node added later, including one from a
    /// plugin, is documented the day it appears.
    /// </remarks>
    private void OnNodeHelpClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel { Editor.SelectedNode: { } selected })
            new NodeHelpWindow(selected.Node) { Owner = this }.Show();
    }

    /// <summary>Opens the control gallery, for reviewing the theme.</summary>
    private void OnGalleryClick(object sender, RoutedEventArgs e) =>
        new GalleryWindow { Owner = this }.Show();
}
