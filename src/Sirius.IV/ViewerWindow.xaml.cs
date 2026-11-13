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

    private void OnViewerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Sirius.Studio.App.Controls.ImageViewer viewer)
            Sync.Add(viewer);
    }

    private void OnViewerUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Sirius.Studio.App.Controls.ImageViewer viewer)
            Sync.Remove(viewer);
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
            if (e.PropertyName == nameof(TileViewModel.Title))
                tab.Title = tile.Title;
        };

        return tab;
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
