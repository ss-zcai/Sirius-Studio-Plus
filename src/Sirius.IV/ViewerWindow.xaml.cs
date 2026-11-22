using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Layout;
using Microsoft.Win32;
using Sirius.Studio.App;
using Sirius.Studio.App.Layout;
using Sirius.Studio.App.ViewModels;

namespace Sirius.IV;

/// <summary>Sirius IV's window.</summary>
public partial class ViewerWindow : Window
{
    public static readonly RoutedUICommand OpenFolderCommand = new("Open Folder", nameof(OpenFolderCommand), typeof(ViewerWindow));
    public static readonly RoutedUICommand SearchCommand = new("Search", nameof(SearchCommand), typeof(ViewerWindow));
    public static readonly RoutedUICommand MatchCommand = new("Match Pictures", nameof(MatchCommand), typeof(ViewerWindow));
    public static readonly RoutedUICommand CopyNameCommand = new("Copy Name", nameof(CopyNameCommand), typeof(ViewerWindow));
    public static readonly RoutedUICommand CopyPathCommand = new("Copy Full Path", nameof(CopyPathCommand), typeof(ViewerWindow));
    public static readonly RoutedUICommand ShowInExplorerCommand = new("Show in Explorer", nameof(ShowInExplorerCommand), typeof(ViewerWindow));
    public static readonly RoutedUICommand CropAllCommand = new("ROIs from All Pictures", nameof(CropAllCommand), typeof(ViewerWindow));
    public static readonly RoutedUICommand ExportCommand = new("Export Annotations as CSV", nameof(ExportCommand), typeof(ViewerWindow));
    public static readonly RoutedUICommand ExportDrawnCommand = new("Export Pictures with Annotations Drawn", nameof(ExportDrawnCommand), typeof(ViewerWindow));

    private readonly BrowserViewModel _model;
    private bool _choosing;

    public ViewerWindow(BrowserViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _model = model;
        DataContext = model;

        // A heading for each sub-folder - a station's days - when sorted by name.
        GroupByFolder();
        Sync.IsOn = model.SyncViews;

        // A tab for each viewer, in the layout's grid.
        ArrangeViewerTabs();
        model.TilesArranged += (_, _) => ArrangeViewerTabs();

        // The Annotations panel follows the viewer whose tab is made active.
        Docking.ActiveContentChanged += (_, _) =>
        {
            if (Docking.ActiveContent is ContentControl { Content: TileViewModel tile })
                _model.CurrentTile = tile;
        };

        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BrowserViewModel.IsGroupedByFolder))
                GroupByFolder();
            else if (e.PropertyName == nameof(BrowserViewModel.SyncViews))
                Sync.IsOn = model.SyncViews;
        };

        if (TryFindResource("Icon.SiriusImageViewer") is ImageSource icon)
            Icon = icon;

        // The list's selection is the window's to set; the model says what.
        model.ChooseRequested += (_, chosen) =>
        {
            _choosing = true;

            try
            {
                FileList.SelectedItems.Clear();

                foreach (var item in chosen)
                    FileList.SelectedItems.Add(item);

                if (chosen.Count > 0)
                    FileList.ScrollIntoView(chosen[0]);
            }
            finally
            {
                _choosing = false;
            }
        };
    }

    private void GroupByFolder()
    {
        var groups = CollectionViewSource.GetDefaultView(_model.Files).GroupDescriptions;
        groups.Clear();

        if (_model.IsGroupedByFolder)
            groups.Add(new PropertyGroupDescription(nameof(ImageFileItem.Subfolder)));
    }

    /// <summary>A folder or picture to open as the window appears - from the command line.</summary>
    public string? StartWith { get; init; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var start = StartWith ?? _model.LastFolder;

        if (start is not null && (Directory.Exists(start) || File.Exists(start)))
            await _model.OpenAsync(start);

        FileList.Focus();
    }

    private void OnFilesSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_choosing)
            _model.Choose(FileList.SelectedItems.OfType<ImageFileItem>());
    }

    private async void OnOpenFolder(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Open a Folder of Pictures",
            InitialDirectory = _model.Folder ?? _model.LastFolder ?? string.Empty,
        };

        if (dialog.ShowDialog(this) == true)
            await _model.OpenAsync(dialog.FolderName);
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } dropped)
            await _model.OpenAsync(dropped[0]);
    }

    private void OnShowFiles(object sender, RoutedEventArgs e)
    {
        FilesPanel.Show();
        FilesPanel.IsActive = true;
    }

    private void OnShowAnnotations(object sender, RoutedEventArgs e)
    {
        AnnotationsPanel.Show();
        AnnotationsPanel.IsActive = true;
    }

    // ---- the Annotations panel (F20b) ---------------------------------------------------

    /// <summary>Each viewer on screen, by the model it shows - to zoom one to a shape.</summary>
    private readonly Dictionary<TileViewModel, Sirius.Studio.App.Controls.ImageViewer> _viewers = [];

    /// <summary>A viewer clicked: the panel shows its picture.</summary>
    private void OnViewerPressed(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TileViewModel tile })
            _model.CurrentTile = tile;
    }

    /// <summary>A shape's row double-clicked: its viewer zooms to it - and, in step, the others.</summary>
    private void OnShapeActivated(object? sender, int index) => ZoomToShape(_model.CurrentTile, index);

    internal void ZoomToShape(TileViewModel? tile, int index)
    {
        if (tile?.Shapes is not { } shapes || index < 0 || index >= shapes.Shapes.Count)
            return;

        if (!_viewers.TryGetValue(tile, out var viewer))
            return;

        var bounds = shapes.Shapes[index].Bounds;
        tile.SelectedShapeIndex = index;
        viewer.ZoomTo(new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }

    /// <summary>
    /// The pictures chosen, or all listed, written as PNGs with their
    /// annotations drawn on - for showing them to someone without Sirius IV.
    /// </summary>
    private async void OnExportDrawn(object sender, ExecutedRoutedEventArgs e)
    {
        const string title = "Export Pictures with Annotations Drawn";

        var chosen = _model.ChosenPictures();
        var listed = _model.ListedPictures();

        if (ExportWindow.AskDrawn(this, chosen.Count, listed.Count) is not { } chosenOnly)
            return;

        var pictures = chosenOnly ? chosen : listed;
        if (pictures.Count == 0)
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder for the drawn pictures",
            InitialDirectory = _model.Folder ?? string.Empty,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var targets = BrowserViewModel.DrawnTargets(pictures, dialog.FolderName);

        // Never over a picture itself: a PNG drawn into its own folder would be.
        if (targets.FirstOrDefault(target => string.Equals(target.Path, Path.GetFullPath(target.Picture.Path), StringComparison.OrdinalIgnoreCase)) is { } over)
        {
            Dialog.Warn(
                $"{over.Picture.Name} would be drawn over itself. Choose another folder - one of its own, such as a new \"drawn\" folder.",
                title,
                this);
            return;
        }

        var existing = targets.Count(target => File.Exists(target.Path));
        var ask = $"Draw {pictures.Count:N0} picture{(pictures.Count == 1 ? string.Empty : "s")} with {(pictures.Count == 1 ? "its" : "their")} annotations, " +
                  $"as PNG, into {dialog.FolderName}?" +
                  (existing > 0 ? $"\n\n{existing:N0} of the files are there already, and will be replaced." : string.Empty);

        if (!Dialog.Confirm(ask, title, this))
            return;

        // A viewer of its own, never shown, draws each as Save Image with Overlays does.
        var drawer = new Sirius.Studio.App.Controls.ImageViewer();
        drawer.Measure(new Size(800, 600));
        drawer.Arrange(new Rect(0, 0, 800, 600));

        BrowserViewModel.DrawnResult result;

        try
        {
            result = await _model.ExportDrawnAsync(targets, drawer.RenderPicture);
        }
        finally
        {
            CommandManager.InvalidateRequerySuggested();
        }

        var summary = $"Wrote {result.Written:N0} drawn picture{(result.Written == 1 ? string.Empty : "s")} to {dialog.FolderName}.";

        if (result.Problems.Count == 0)
        {
            Dialog.Inform(summary, title, this);
            return;
        }

        var listedProblems = string.Join("\n", result.Problems.Take(8));
        var more = result.Problems.Count > 8 ? $"\n…and {result.Problems.Count - 8} more." : string.Empty;
        Dialog.Warn($"{summary}\n\n{result.Problems.Count} could not be drawn:\n{listedProblems}{more}", title, this);
    }

    private void OnCanExport(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = _model is { HasFolder: true, IsExporting: false, IsCropping: false } && _model.Files.Count > 0;

    /// <summary>
    /// The pictures' annotations as one CSV: the ones chosen or all listed,
    /// a row per picture or per shape, then where to save it.
    /// </summary>
    private async void OnExport(object sender, ExecutedRoutedEventArgs e)
    {
        const string title = "Export Annotations as CSV";

        var chosen = _model.ChosenPictures();
        var listed = _model.ListedPictures();

        if (ExportWindow.Ask(this, chosen.Count, listed.Count) is not { } answer)
            return;

        var pictures = answer.ChosenOnly ? chosen : listed;
        if (pictures.Count == 0)
            return;

        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(_model.Folder ?? "pictures"));
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = "CSV (comma-separated) (*.csv)|*.csv",
            FileName = $"{folderName}-{(answer.PerShape ? "shapes" : "results")}.csv",
            InitialDirectory = _model.Folder ?? string.Empty,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        (int Rows, int Without) result;

        try
        {
            result = await _model.ExportAsync(pictures, answer.PerShape, dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialog.Error($"{Path.GetFileName(dialog.FileName)} could not be written: {ex.Message}", title, this);
            return;
        }
        finally
        {
            CommandManager.InvalidateRequerySuggested();
        }

        var rows = answer.PerShape
            ? $"{result.Rows:N0} shape{(result.Rows == 1 ? string.Empty : "s")} from {pictures.Count:N0} picture{(pictures.Count == 1 ? string.Empty : "s")}"
            : $"{result.Rows:N0} picture{(result.Rows == 1 ? string.Empty : "s")}";
        var without = result.Without > 0
            ? $"\n\n{result.Without:N0} had no annotation file{(answer.PerShape ? " and add no rows" : ": their rows have only the file and its verdict")}."
            : string.Empty;

        Dialog.Inform($"Wrote {rows} to {dialog.FileName}.{without}", title, this);
    }

    // ---- searching the names ------------------------------------------------------------

    private void OnSearch(object sender, ExecutedRoutedEventArgs e)
    {
        FilesPanel.Show();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            // Esc clears; a second Esc, with nothing to clear, goes back to the list.
            case Key.Escape:
                if (SearchBox.Text.Length > 0)
                    ClearSearch();
                else
                    FocusList();

                e.Handled = true;
                break;

            // Down, or Enter: on to the pictures found.
            case Key.Down or Key.Enter:
                _model.SearchText = SearchBox.Text;
                FocusList();
                e.Handled = true;
                break;
        }
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        ClearSearch();
        SearchBox.Focus();
    }

    private void ClearSearch()
    {
        SearchBox.Clear();
        _model.SearchText = string.Empty;
    }

    /// <summary>To the list, on the picture chosen - so the arrow keys step on from it.</summary>
    private void FocusList()
    {
        var item = FileList.SelectedItem ?? (FileList.Items.Count > 0 ? FileList.Items[0] : null);

        if (item is null)
        {
            FileList.Focus();
            return;
        }

        FileList.ScrollIntoView(item);
        FileList.UpdateLayout();

        if (FileList.ItemContainerGenerator.ContainerFromItem(item) is UIElement container)
            container.Focus();
        else
            FileList.Focus();
    }

    // ---- matching, and the viewers in step -------------------------------------------------

    /// <summary>The viewers on screen, kept in step while Sync View is on.</summary>
    internal ViewSync Sync { get; } = new();

    private void OnMatch(object sender, ExecutedRoutedEventArgs e)
    {
        if (MatchingWindow.Ask(this, _model) is { } rule)
            _model.Match = rule;
    }

    // Asked while the window is still being built, before it has a model.
    private void OnCanCropAll(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = _model is { HasFolder: true, IsCropping: false };

    /// <summary>
    /// ROIs cut from the pictures in the list into a folder chosen for them:
    /// each picture's own rectangles, or those on the picture shown cut from
    /// every picture.
    /// </summary>
    private async void OnCropAll(object sender, ExecutedRoutedEventArgs e)
    {
        const string title = "ROIs from All Pictures";

        var pictures = _model.ListedPictures();
        if (pictures.Count == 0)
            return;

        var own = (await _model.OwnRoisAsync(pictures)).Where(cut => cut.Rois.Count > 0).ToArray();
        var source = _model.RoiSource();

        if (own.Length == 0 && source is null)
        {
            Dialog.Inform(
                "Draw a rectangle round each ROI first, with the viewer's Rectangle tool - on each picture you want " +
                "cut, or on one picture to cut the same places from all.\n\n" +
                "Each crop is saved as picture-1.png, picture-2.png… in the folder you choose.",
                title,
                this);
            return;
        }

        if (RoisFromAllWindow.Ask(this, own, source, pictures.Count, _model.Folder) is not { } answer)
            return;

        var folder = answer.Folder;
        var targets = answer.SameForAll
            ? RoiBatch.Targets(pictures, source!.Value.Rois, folder)
            : RoiBatch.Targets(own, folder);

        // Never over a picture itself: a crop named as one, in the pictures' own folder.
        var originals = pictures.Select(picture => Path.GetFullPath(picture.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targets.FirstOrDefault(target => originals.Contains(Path.GetFullPath(target.Path))) is { } over)
        {
            Dialog.Warn($"{Path.GetFileName(over.Path)} would be cut over a picture of that name. Choose another folder.", title, this);
            return;
        }

        var existing = targets.Count(t => File.Exists(t.Path));

        if (existing > 0 &&
            !Dialog.Confirm(
                $"{existing:N0} of the {targets.Count:N0} ROI images are in {folder} already, and will be replaced. Go on?",
                title,
                this))
        {
            return;
        }

        var result = await _model.CropAllAsync(targets);
        CommandManager.InvalidateRequerySuggested();

        var summary = $"Saved {result.Saved:N0} ROI image{(result.Saved == 1 ? string.Empty : "s")} " +
                      $"from {result.Pictures:N0} picture{(result.Pictures == 1 ? string.Empty : "s")} into {folder}.";

        if (result.Problems.Count == 0)
        {
            Dialog.Inform(summary, title, this);
            return;
        }

        var listed = string.Join("\n", result.Problems.Take(8));
        var more = result.Problems.Count > 8 ? $"\n…and {result.Problems.Count - 8} more." : string.Empty;

        Dialog.Warn($"{summary}\n\n{result.Problems.Count} could not be cut:\n{listed}{more}", title, this);
    }

    private void OnViewerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Sirius.Studio.App.Controls.ImageViewer viewer)
            return;

        Sync.Add(viewer);

        if (viewer.DataContext is TileViewModel tile)
            _viewers[tile] = viewer;
    }

    private void OnViewerUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Sirius.Studio.App.Controls.ImageViewer viewer)
            return;

        Sync.Remove(viewer);

        foreach (var (tile, shown) in _viewers.ToArray())
        {
            if (ReferenceEquals(shown, viewer))
                _viewers.Remove(tile);
        }
    }

    // ---- the viewers' tabs -----------------------------------------------------------------

    private readonly Dictionary<TileViewModel, LayoutDocument> _viewerTabs = [];

    /// <summary>Each viewer's tab, in the order of the layout's places.</summary>
    internal IReadOnlyList<LayoutDocument> ViewerTabs => _model.Tiles.Select(tile => _viewerTabs[tile]).ToArray();

    /// <summary>
    /// Puts the viewers' tabs in the layout's grid - a pane each, in rows -
    /// where the viewers were, wherever they had been dragged to.
    /// </summary>
    internal void ArrangeViewerTabs()
    {
        foreach (var (tile, tab) in _viewerTabs.ToArray())
        {
            if (!_model.Tiles.Contains(tile))
            {
                _viewerTabs.Remove(tile);
                tab.Parent?.RemoveChild(tab);
                CloseCharts(tile);
            }
        }

        foreach (var tile in _model.Tiles)
        {
            if (!_viewerTabs.ContainsKey(tile))
                _viewerTabs[tile] = ViewerTab(tile);
        }

        // Where the viewers are - before their tabs are taken out of it.
        var area = DocumentArea();

        foreach (var tab in _viewerTabs.Values)
            tab.Parent?.RemoveChild(tab);

        var grid = new LayoutDocumentPaneGroup { Orientation = Orientation.Vertical };
        var tabs = ViewerTabs;

        for (var row = 0; row < _model.Rows; row++)
        {
            var line = new LayoutDocumentPaneGroup { Orientation = Orientation.Horizontal };

            for (var column = 0; column < _model.Columns; column++)
            {
                var place = row * _model.Columns + column;

                if (place < tabs.Count)
                    line.Children.Add(new LayoutDocumentPane(tabs[place]));
            }

            grid.Children.Add(line);
        }

        if (area?.Parent is ILayoutContainer parent)
            parent.ReplaceChild(area, grid);
        else
            Docking.Layout.RootPanel.Children.Add(grid);

        // Panes emptied - a tab dragged out on its own, a floating window - go.
        Docking.Layout.CollectGarbage();
    }

    /// <summary>
    /// The panes holding the viewers, at the top: the one in the main
    /// window, not inside another group, not floating.
    /// </summary>
    private ILayoutElement? DocumentArea() =>
        Docking.Layout.Descendents()
            .OfType<ILayoutElement>()
            .FirstOrDefault(element => element is LayoutDocumentPane or LayoutDocumentPaneGroup && element.Parent is LayoutPanel);

    private LayoutDocument ViewerTab(TileViewModel tile)
    {
        var tab = new LayoutDocument
        {
            Title = tile.Title,
            ContentId = $"viewer{tile.Number}",
            CanClose = false,
            Content = new ContentControl
            {
                Content = tile,
                ContentTemplate = (DataTemplate)FindResource("TileTemplate"),
                Focusable = false,
            },
        };

        // The title - for the window a tab is floated out in, and the tab list - follows the picture.
        tile.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(TileViewModel.Title))
                return;

            tab.Title = tile.Title;

            foreach (var (chart, window) in _charts)
            {
                if (tile.ProfileLines.Contains(chart))
                    window.Title = ChartTitle(chart, tile);
            }
        };

        // A chart for each profile line drawn on it.
        tile.ProfileLines.CollectionChanged += (_, e) =>
        {
            foreach (var chart in e.OldItems?.OfType<ProfileLinePanelViewModel>() ?? [])
                CloseChart(chart);

            foreach (var chart in e.NewItems?.OfType<ProfileLinePanelViewModel>() ?? [])
                OpenChart(chart, tile);
        };

        return tab;
    }

    // ---- profile charts --------------------------------------------------------------------

    /// <summary>Each profile line's chart window, by its chart.</summary>
    private readonly Dictionary<ProfileLinePanelViewModel, LayoutAnchorable> _charts = [];

    /// <summary>Charts floated and not yet shown by AvalonDock, which must not be closed until they are.</summary>
    private readonly HashSet<LayoutAnchorable> _floatingPending = [];

    /// <summary>How many charts have been opened, for the cascade.</summary>
    private int _chartsOpened;

    /// <summary>The profile charts open, for tests.</summary>
    internal IReadOnlyCollection<LayoutAnchorable> ChartWindows => _charts.Values;

    /// <summary>
    /// Floats a chart for a profile line, as Studio does: docked, it would
    /// land as a tab over the pictures the line was drawn on.
    /// </summary>
    private void OpenChart(ProfileLinePanelViewModel chart, TileViewModel tile)
    {
        if (_charts.ContainsKey(chart))
            return;

        var window = new LayoutAnchorable
        {
            Title = ChartTitle(chart, tile),
            ContentId = chart.ContentId,
            Content = chart,
            CanClose = true,
            CanHide = false,
        };

        chart.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileLinePanelViewModel.Title))
                window.Title = ChartTitle(chart, tile);
        };

        // Closing the chart takes its line off the picture, as in Studio:
        // drawing the line again is how a chart is had back. Not from inside
        // AvalonDock's close, which the list changing would run into.
        window.Closed += (_, _) =>
        {
            if (!_charts.Remove(chart))
                return;

            Dispatcher.BeginInvoke(() => tile.ProfileLines.Remove(chart), System.Windows.Threading.DispatcherPriority.Background);
        };

        _charts[chart] = window;

        var origin = IsLoaded
            ? PointToScreenIndependent(new Point(
                Math.Max(0, (ActualWidth - PanelLayout.DefaultFloatingSize.Width) / 2),
                Math.Max(0, (ActualHeight - PanelLayout.DefaultFloatingSize.Height) / 2)))
            : new Point(Left + 80, Top + 80);
        var position = PanelLayout.CascadeFrom(origin, _chartsOpened++);

        window.FloatingLeft = position.X;
        window.FloatingTop = position.Y;
        window.FloatingWidth = PanelLayout.DefaultFloatingSize.Width;
        window.FloatingHeight = PanelLayout.DefaultFloatingSize.Height;

        window.AddToLayout(Docking, AnchorableShowStrategy.Most);
        window.Float();

        // AvalonDock shows the window on a later pass; closed before then, it throws.
        _floatingPending.Add(window);
        Dispatcher.BeginInvoke(() => _floatingPending.Remove(window), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Closes a chart whose line has gone.</summary>
    private void CloseChart(ProfileLinePanelViewModel chart)
    {
        if (!_charts.Remove(chart, out var window))
            return;

        CloseWindow(window);
    }

    private void CloseWindow(LayoutAnchorable window)
    {
        if (_floatingPending.Contains(window))
        {
            Dispatcher.BeginInvoke(() => CloseWindow(window), System.Windows.Threading.DispatcherPriority.Background);
            return;
        }

        try
        {
            window.Close();
        }
        catch (Exception)
        {
            // Already detached by AvalonDock; it is gone either way.
        }
    }

    /// <summary>Closes the charts of a viewer that has gone from the layout.</summary>
    private void CloseCharts(TileViewModel tile)
    {
        foreach (var chart in tile.ProfileLines.ToArray())
            CloseChart(chart);
    }

    /// <summary>
    /// The line's name and the picture it is on: two viewers can each have
    /// a Profile 1.
    /// </summary>
    private static string ChartTitle(ProfileLinePanelViewModel chart, TileViewModel tile) =>
        tile.IsEmpty ? chart.Title : $"{chart.Title} — {tile.Title}";

    /// <summary>
    /// A point in this window on the screen, in device-independent units as
    /// a window's Left and Top are - not the device pixels PointToScreen gives.
    /// </summary>
    private Point PointToScreenIndependent(Point point)
    {
        var screen = PointToScreen(point);
        var toIndependent = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;

        return toIndependent.Transform(screen);
    }

    // ---- sorting ---------------------------------------------------------------------------

    private void OnSortByClick(object sender, RoutedEventArgs e)
    {
        var menu = SortByMenu();
        menu.PlacementTarget = SortByButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>
    /// What to sort by, the one in use ticked; then the two ways round, in
    /// words for that sort - "Oldest first", "Newest first".
    /// </summary>
    internal ContextMenu SortByMenu()
    {
        var menu = new ContextMenu();

        foreach (var choice in BrowserViewModel.Sorts)
        {
            var sort = (FileSort)choice.Value;
            var item = new MenuItem { Header = choice.Label, IsCheckable = true, IsChecked = _model.Sort == sort };
            item.Click += (_, _) => _model.Sort = sort;
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        foreach (var descending in new[] { false, true })
        {
            var item = new MenuItem
            {
                Header = BrowserViewModel.DirectionText(_model.Sort, descending),
                IsCheckable = true,
                IsChecked = _model.SortDescending == descending,
            };

            item.Click += (_, _) => _model.SortDescending = descending;
            menu.Items.Add(item);
        }

        return menu;
    }

    // ---- the pictures chosen, as files ---------------------------------------------------

    /// <summary>The pictures chosen in the list, in list order.</summary>
    private IReadOnlyList<ImageFileItem> ChosenFiles => _model.Chosen;

    private void OnCanUseChosen(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = ChosenFiles.Count > 0;
        e.Handled = true;
    }

    private void OnCopyName(object sender, ExecutedRoutedEventArgs e) =>
        Copy(string.Join(Environment.NewLine, ChosenFiles.Select(file => file.Name)));

    private void OnCopyPath(object sender, ExecutedRoutedEventArgs e) =>
        Copy(string.Join(Environment.NewLine, ChosenFiles.Select(file => file.Path)));

    private static void Copy(string text)
    {
        // The clipboard can be held a moment by another program; not worth an error.
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }
    }

    /// <summary>Opens Explorer on the first picture chosen's folder, with it selected.</summary>
    private void OnShowInExplorer(object sender, ExecutedRoutedEventArgs e)
    {
        if (ChosenFiles.FirstOrDefault() is not { } file)
            return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Dialog.Error($"Explorer could not be opened: {ex.Message}", BrowserViewModel.AppTitle, this);
        }
    }

    // ---- copied or moved to a folder (F20f) ---------------------------------------------

    /// <summary>The toolbar's To Folder: Copy to and Move to, each with the folders last used.</summary>
    private void OnToFolderClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = ToFolderButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        menu.Items.Add(new MenuItem { Header = "Copy _to Folder", Tag = "copy" });
        menu.Items.Add(new MenuItem { Header = "_Move to Folder", Tag = "move" });

        foreach (var item in menu.Items.OfType<MenuItem>())
            FillToFolder(item);

        menu.IsOpen = true;
    }

    /// <summary>The list's right-click menu: its Copy to and Move to filled as it opens.</summary>
    private void OnFileMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            foreach (var item in menu.Items.OfType<MenuItem>().Where(item => item.Tag is "copy" or "move"))
                FillToFolder(item);
        }
    }

    /// <summary>A Copy to or Move to: the folders last used, then Choose Folder; none chosen, nothing to do.</summary>
    internal void FillToFolder(MenuItem item)
    {
        var move = item.Tag is "move";
        var count = ChosenFiles.Count == 0 ? 0 : _model.ChosenPictures().Count;

        item.Items.Clear();
        item.IsEnabled = count > 0 && !_model.IsTransferring;

        foreach (var folder in _model.RecentTargets)
        {
            var recent = new MenuItem { Header = folder.Replace("_", "__", StringComparison.Ordinal), ToolTip = folder };
            recent.Click += async (_, _) => await ToFolderAsync(move, folder);
            item.Items.Add(recent);
        }

        if (_model.RecentTargets.Count > 0)
            item.Items.Add(new Separator());

        var choose = new MenuItem { Header = "_Choose Folder…" };
        choose.Click += async (_, _) => await ToFolderAsync(move, null);
        item.Items.Add(choose);
    }

    /// <summary>Asks Yes / No / Cancel about names there already - a test answers for the user.</summary>
    internal Func<string, string, MessageBoxResult>? AskTransfer { get; set; }

    /// <summary>Asks OK or Cancel before a move, or a save first - a test answers for the user.</summary>
    internal Func<string, string, bool>? ConfirmTransfer { get; set; }

    /// <summary>
    /// Copies or moves the pictures chosen - a matched set whole - with
    /// their .sann, into a folder. A move is confirmed; names there already
    /// are asked about; measurements drawn and not saved are saved first,
    /// so they go with their pictures.
    /// </summary>
    internal async Task ToFolderAsync(bool move, string? folder)
    {
        var title = move ? "Move to Folder" : "Copy to Folder";
        var pictures = _model.ChosenPictures();

        if (pictures.Count == 0)
            return;

        if (folder is null)
        {
            var dialog = new OpenFolderDialog
            {
                Title = $"{(move ? "Move" : "Copy")} {Count(pictures.Count)} to…",
                InitialDirectory = _model.RecentTargets.FirstOrDefault(Directory.Exists) ?? _model.Folder ?? string.Empty,
            };

            if (dialog.ShowDialog(this) != true)
                return;

            folder = dialog.FolderName;
        }

        var plan = BrowserViewModel.TransferPlan(pictures, folder);
        var here = plan.Count(transfer => string.Equals(Path.GetFullPath(transfer.Picture.Path), transfer.Files[0].To, StringComparison.OrdinalIgnoreCase));

        if (here == plan.Count)
        {
            Dialog.Inform($"{(pictures.Count == 1 ? "The picture is" : "The pictures are")} in {folder} already.", title, this);
            return;
        }

        var existing = plan.Count(transfer => File.Exists(transfer.Files[0].To)) - here;
        var unsaved = _model.UnsavedAmong(pictures);

        var ask = new List<string>();

        if (move)
            ask.Add($"Move {Count(pictures.Count)}, with their annotation files, to {folder}?");

        if (unsaved > 0)
            ask.Add($"Measurements drawn on {unsaved} of them are not saved; they are saved into their annotation files first, so they go with them.");

        if (existing > 0)
            ask.Add($"{existing} of the same name {(existing == 1 ? "is" : "are")} there already. Yes replaces {(existing == 1 ? "it" : "them")}; No leaves {(existing == 1 ? "it" : "them")} and {(move ? "moves" : "copies")} the rest.");

        var replace = true;

        if (existing > 0)
        {
            // Yes replaces them, No leaves them and goes on with the rest, Cancel stops.
            var answer = (AskTransfer ?? ((message, caption) => Dialog.Ask(message, caption, this)))(string.Join("\n\n", ask), title);

            if (answer is MessageBoxResult.Cancel or MessageBoxResult.None)
                return;

            replace = answer == MessageBoxResult.Yes;
        }
        else if (ask.Count > 0 && !(ConfirmTransfer ?? ((message, caption) => Dialog.Confirm(message, caption, this)))(string.Join("\n\n", ask), title))
        {
            return;
        }

        if (unsaved > 0 && !_model.SaveFor(pictures))
        {
            Dialog.Warn($"Nothing was {(move ? "moved" : "copied")}: {_model.Problem}", title, this);
            return;
        }

        BrowserViewModel.TransferResult result;

        try
        {
            result = await _model.TransferAsync(plan, folder, move, replace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Dialog.Error($"{folder} could not be used: {ex.Message}", title, this);
            return;
        }

        var summary = $"{(move ? "Moved" : "Copied")} {Count(result.Done)} to {folder}." +
                      (result.Skipped > 0 ? $" {result.Skipped} left as {(result.Skipped == 1 ? "it was" : "they were")}." : string.Empty);

        if (result.Problems.Count == 0)
        {
            Dialog.Inform(summary, title, this);
            return;
        }

        var listed = string.Join("\n", result.Problems.Take(8));
        var more = result.Problems.Count > 8 ? $"\n…and {result.Problems.Count - 8} more." : string.Empty;
        Dialog.Warn($"{summary}\n\n{result.Problems.Count} could not be:\n{listed}{more}", title, this);
    }

    private static string Count(int pictures) => pictures == 1 ? "1 picture" : $"{pictures:N0} pictures";

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    /// <summary>Measurements not saved are asked about before they are lost.</summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_model.ConfirmLeaving())
            e.Cancel = true;
    }

    private void OnAboutClick(object sender, RoutedEventArgs e) =>
        new AboutWindow(
            BrowserViewModel.AppTitle,
            TryFindResource("Icon.SiriusImageViewer") as ImageSource,
            "Sirius Image Viewer: the pictures a station saved, with what was found on each, one at a time or side by side.",
            typeof(ViewerWindow).Assembly)
        {
            Owner = this,
        }.ShowDialog();
}
