using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sirius.Core.Annotations;
using Sirius.Core.Graph;
using Sirius.Core.Shapes;
using System.Windows.Media.Imaging;

namespace Sirius.IV;

/// <summary>Which pictures the file list shows.</summary>
public enum FileFilter
{
    All,

    /// <summary>Failed, or a node went wrong: what someone is looking for.</summary>
    [Description("Failed")]
    Failed,

    [Description("Passed")]
    Passed,
}

/// <summary>What the file list is sorted by.</summary>
public enum FileSort
{
    /// <summary>Folder by folder, then by name: for a station's pictures, the order taken.</summary>
    Name,

    [Description("Date modified")]
    Modified,

    Size,

    /// <summary>Width × height: the number of pixels, then the width.</summary>
    [Description("Dimensions")]
    Dimensions,

    Type,
}

/// <summary>How many pictures are shown at once, and how they are laid out.</summary>
public enum ViewerLayout
{
    [Description("One")]
    Single = 1,

    [Description("Two side by side")]
    Two = 2,

    [Description("Three side by side")]
    Three = 3,

    [Description("2 × 2")]
    Four = 4,

    [Description("3 × 3")]
    Nine = 9,
}

/// <summary>
/// Sirius IV's window: a folder, the pictures in it, and the viewers showing
/// the ones chosen.
/// </summary>
/// <remarks>
/// <para>
/// A folder is listed with its sub-folders by default, since a station saves
/// a folder for each day; sorted by path, a station's pictures are in the
/// order they were taken.
/// </para>
/// <para>
/// <b>What is shown:</b> with several pictures chosen in the list, those,
/// one to a viewer; with one chosen, it and the ones after it, as many as
/// there are viewers - so stepping down the list with two or four viewers
/// steps a strip of pictures along.
/// </para>
/// </remarks>
public sealed class BrowserViewModel : ObservableObject
{
    public const string AppTitle = "Sirius IV";

    /// <summary>The picture files listed: those OpenCV reads.</summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff",
    };

    private readonly string _settingsPath;
    private readonly Dictionary<string, Unsaved> _unsaved = new(StringComparer.OrdinalIgnoreCase);
    private List<ImageFileItem> _all = [];
    private IReadOnlyList<ImageFileItem> _chosen = [];
    private string? _folder;
    private FileFilter _filter;
    private string _search = string.Empty;
    private FileSort _sort;
    private bool _descending;
    private string? _sortProgress;
    private MatchRule _match = MatchRule.Off;
    private bool _syncViews = true;
    private int _setCount;
    private int _largestSet;
    private ViewerLayout _layout = ViewerLayout.Single;
    private bool _includeSubfolders = true;
    private bool _busy;
    private string? _problem;
    private int _listing;

    /// <param name="settingsPath">Where the last folder and layout are kept; null keeps nothing.</param>
    public BrowserViewModel(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? string.Empty;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => HasFolder);
        ReverseSortCommand = new RelayCommand(() => SortDescending = !SortDescending);
        ToggleSyncViewsCommand = new RelayCommand(() => SyncViews = !SyncViews);
        SetLayoutCommand = new RelayCommand<ViewerLayout>(layout =>
        {
            // Chosen again: the viewers put back in their places, after dragging them about.
            if (layout == _layout)
                TilesArranged?.Invoke(this, EventArgs.Empty);
            else
                Layout = layout;
        });
        SaveCommand = new RelayCommand(() => Save(), () => HasUnsavedChanges);
        ReadSettings();
        ArrangeTiles();
        Annotations = new AnnotationsViewModel(this);
    }

    // ---- the folder ----------------------------------------------------------

    /// <summary>The folder open, or null.</summary>
    public string? Folder
    {
        get => _folder;
        private set
        {
            if (SetProperty(ref _folder, value))
            {
                OnPropertyChanged(nameof(HasFolder));
                OnPropertyChanged(nameof(Title));
                RefreshCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasFolder => _folder is not null;

    public string Title =>
        (HasUnsavedChanges ? "*" : string.Empty) +
        (_folder is null ? AppTitle : $"{Path.GetFileName(_folder.TrimEnd(Path.DirectorySeparatorChar))} - {AppTitle}");

    /// <summary>Lists the pictures in sub-folders too: a station saves a folder for each day.</summary>
    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set
        {
            if (SetProperty(ref _includeSubfolders, value))
            {
                SaveSettings();
                _ = RefreshAsync();
            }
        }
    }

    /// <summary>Reading a large folder.</summary>
    public bool IsBusy
    {
        get => _busy;
        private set => SetProperty(ref _busy, value);
    }

    /// <summary>Why the folder could not be read, or null.</summary>
    public string? Problem
    {
        get => _problem;
        private set => SetProperty(ref _problem, value);
    }

    public IAsyncRelayCommand RefreshCommand { get; }

    /// <summary>The folder opened last time, if it is still there.</summary>
    public string? LastFolder { get; private set; }

    /// <summary>
    /// Opens a folder - or a picture, opening its folder with it chosen -
    /// once any measurements not yet saved have been saved or let go.
    /// </summary>
    /// <returns>False when the operator chose to stay where they were.</returns>
    public async Task<bool> OpenAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!ConfirmLeaving("opening another folder"))
            return false;

        var full = Path.GetFullPath(path);
        var picture = File.Exists(full) ? full : null;
        var folder = picture is null ? full : Path.GetDirectoryName(full)!;

        if (!Directory.Exists(folder))
        {
            Problem = $"{folder} does not exist.";
            return true;
        }

        Forget();
        Folder = folder;
        LastFolder = folder;
        SaveSettings();

        await ListAsync(picture is null ? null : [picture]).ConfigureAwait(true);
        return true;
    }

    /// <summary>Reads the folder again - pictures a station has saved since - keeping what was chosen.</summary>
    public Task RefreshAsync() =>
        _folder is null ? Task.CompletedTask : ListAsync(_chosen.Select(item => item.Path).ToArray());

    private async Task ListAsync(IReadOnlyCollection<string>? keep)
    {
        if (_folder is not { } folder)
            return;

        var listing = ++_listing;
        IsBusy = true;
        Problem = null;

        List<ImageFileItem> found;

        try
        {
            var subfolders = _includeSubfolders;
            found = await Task.Run(() => List(folder, subfolders)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            found = [];
            Problem = $"{folder} could not be read: {ex.Message}";
        }
        finally
        {
            if (listing == _listing)
                IsBusy = false;
        }

        // A later listing - another folder opened meanwhile - wins.
        if (listing != _listing)
            return;

        // Sizes still to read for the old list's pictures are not wanted now.
        ImageFileItem.ForgetPendingReads();

        // Measured and not saved: still measured, though the list is new.
        foreach (var item in found)
            item.IsModified = _unsaved.ContainsKey(item.Path);

        _all = found;
        ApplyFilter(keep);
        FitLayoutToSets();

        // Sorted by dimensions: shown at once, in order once every size is read.
        if (_sort == FileSort.Dimensions)
            await SortByDimensionsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// The pictures under a folder: sub-folder by sub-folder, then the
    /// folder's own, each by name - for a station's, its days in the order
    /// they were taken. A sub-folder's pictures are kept together, as the
    /// list groups them under its name.
    /// </summary>
    internal static List<ImageFileItem> List(string folder, bool subfolders)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = subfolders,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        // The listing carries each file's size and time: no file is opened here.
        return new DirectoryInfo(folder).EnumerateFiles("*", options)
            .Where(file => Extensions.Contains(file.Extension) && !file.Name.Contains(".saving.", StringComparison.OrdinalIgnoreCase))
            .Select(file => new ImageFileItem(file.FullName, folder, file.Length, file.LastWriteTime))
            .OrderBy(item => item.Subfolder is null)
            .ThenBy(item => item.Subfolder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- the list --------------------------------------------------------------

    /// <summary>The pictures the filter and search let through, in order.</summary>
    public FileCollection Files { get; } = [];

    public FileFilter Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                SaveSettings();
                ApplyFilter(_chosen.Select(item => item.Path).ToArray());
            }
        }
    }

    /// <summary>The filter's choices, as the toolbar lists them.</summary>
    public static IReadOnlyList<Choice> Filters { get; } = Choice.Of<FileFilter>();

    /// <summary>
    /// Only the pictures whose name - or sub-folder - holds this, ignoring
    /// case: "000042", "_FAIL", "2026-11-11", "cam2". Blank lists them all.
    /// </summary>
    public string SearchText
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsSearching));
                ApplyFilter(_chosen.Select(item => item.Path).ToArray());
            }
        }
    }

    public bool IsSearching => _search.Length > 0;

    // ---- sorting --------------------------------------------------------------------

    /// <summary>
    /// What the list is sorted by. The viewers follow it: "the one chosen
    /// and those after it" are after it in this order.
    /// </summary>
    public FileSort Sort
    {
        get => _sort;
        set
        {
            if (!Enum.IsDefined(value) || !SetProperty(ref _sort, value))
                return;

            OnPropertyChanged(nameof(IsGroupedByFolder));
            OnPropertyChanged(nameof(SortDirectionText));
            OnPropertyChanged(nameof(SortDirectionTip));
            OnPropertyChanged(nameof(SortByText));
            SaveSettings();
            ApplyFilter(_chosen.Select(item => item.Path).ToArray());

            if (value == FileSort.Dimensions)
                _ = SortByDimensionsAsync();
        }
    }

    /// <summary>The other way round: Z to A, newest, largest first.</summary>
    public bool SortDescending
    {
        get => _descending;
        set
        {
            if (!SetProperty(ref _descending, value))
                return;

            OnPropertyChanged(nameof(SortDirectionText));
            OnPropertyChanged(nameof(SortDirectionTip));
            OnPropertyChanged(nameof(SortByText));
            SaveSettings();
            ApplyFilter(_chosen.Select(item => item.Path).ToArray());
        }
    }

    public IRelayCommand ReverseSortCommand { get; }

    /// <summary>The sort's choices, as the Sort By menu lists them.</summary>
    public static IReadOnlyList<Choice> Sorts { get; } = Choice.Of<FileSort>();

    /// <summary>"Sort by: Date modified, newest first" - the Sort By button's tooltip.</summary>
    public string SortByText =>
        $"Sort by: {Sorts.First(choice => (FileSort)choice.Value == _sort).Label}, {SortDirectionText.ToLowerInvariant()}";

    /// <summary>The direction: "A to Z", "Newest first".</summary>
    public string SortDirectionText => DirectionText(_sort, _descending);

    /// <summary>"Newest first - click for oldest first": the direction button's tooltip.</summary>
    public string SortDirectionTip =>
        $"{SortDirectionText} - click for {DirectionText(_sort, !_descending).ToLowerInvariant()}";

    /// <summary>A direction in words, for a sort: what comes first.</summary>
    public static string DirectionText(FileSort sort, bool descending) => (sort, descending) switch
    {
        (FileSort.Name or FileSort.Type, false) => "A to Z",
        (FileSort.Name or FileSort.Type, true) => "Z to A",
        (FileSort.Modified, false) => "Oldest first",
        (FileSort.Modified, true) => "Newest first",
        (_, false) => "Smallest first",
        (_, true) => "Largest first",
    };

    /// <summary>
    /// Headed by sub-folder - a station's days - only when sorted by name:
    /// sorted any other way, a folder's pictures are spread through the list.
    /// </summary>
    public bool IsGroupedByFolder => _sort == FileSort.Name;

    /// <summary>"Reading picture sizes… 1,200 of 30,000" while sorting by dimensions; null otherwise.</summary>
    public string? SortProgress
    {
        get => _sortProgress;
        private set => SetProperty(ref _sortProgress, value);
    }

    /// <summary>
    /// The pictures in the order chosen. Ties keep the folder-and-name
    /// order, so pictures of one size or one second stay in the order taken.
    /// </summary>
    private IEnumerable<ImageFileItem> Sorted(IEnumerable<ImageFileItem> items)
    {
        // The listing's own order is the name order, and every tie's.
        var listed = items.ToList();

        if (_sort == FileSort.Name)
        {
            if (_descending)
                listed.Reverse();

            return listed;
        }

        IOrderedEnumerable<ImageFileItem> ordered = (_sort, _descending) switch
        {
            (FileSort.Modified, false) => listed.OrderBy(item => item.Modified),
            (FileSort.Modified, true) => listed.OrderByDescending(item => item.Modified),
            (FileSort.Size, false) => listed.OrderBy(item => item.Size),
            (FileSort.Size, true) => listed.OrderByDescending(item => item.Size),
            (FileSort.Dimensions, false) => listed.OrderBy(item => item.PixelCount).ThenBy(item => item.KnownWidth),
            (FileSort.Dimensions, true) => listed.OrderByDescending(item => item.PixelCount).ThenByDescending(item => item.KnownWidth),
            (FileSort.Type, false) => listed.OrderBy(item => item.Kind, StringComparer.Ordinal),
            _ => listed.OrderByDescending(item => item.Kind, StringComparer.Ordinal),
        };

        // OrderBy is stable: ties stay in the listing's order.
        return ordered;
    }

    /// <summary>
    /// Sorting by dimensions needs every picture's, not only those on
    /// screen: each header is read - a few bytes of each file - and the list
    /// sorted again once they all are.
    /// </summary>
    internal async Task SortByDimensionsAsync()
    {
        var listing = _listing;
        var missing = _all.Where(item => !item.HasDimensions).ToArray();

        if (missing.Length > 0)
        {
            var progress = new Progress<int>(done =>
            {
                if (listing == _listing && _sort == FileSort.Dimensions)
                    SortProgress = $"Reading picture sizes… {done:N0} of {missing.Length:N0}";
            });

            SortProgress = $"Reading picture sizes… 0 of {missing.Length:N0}";
            var sizes = await Task.Run(() => ReadSizes(missing, progress)).ConfigureAwait(true);

            for (var i = 0; i < missing.Length; i++)
                missing[i].SetDimensions(sizes[i]);
        }

        SortProgress = null;

        // Another folder, or another sort, meanwhile: that has been shown.
        if (listing == _listing && _sort == FileSort.Dimensions)
            ApplyFilter(_chosen.Select(item => item.Path).ToArray());
    }

    private static (int Width, int Height)?[] ReadSizes(IReadOnlyList<ImageFileItem> items, IProgress<int> progress)
    {
        var sizes = new (int Width, int Height)?[items.Count];

        for (var i = 0; i < items.Count; i++)
        {
            sizes[i] = ImageHeader.TryRead(items[i].Path);

            if (i % 200 == 199)
                progress.Report(i + 1);
        }

        return sizes;
    }

    /// <summary>"312 pictures, 12 failed · 1.4 GB".</summary>
    public string CountText
    {
        get
        {
            if (!HasFolder)
                return string.Empty;

            var failed = _all.Count(item => item.Failed);
            var size = _all.Count == 0 ? string.Empty : " · " + ImageFileItem.FormatSize(_all.Sum(item => item.Size));
            var pictures = $"{_all.Count} picture{(_all.Count == 1 ? string.Empty : "s")}";

            // Matched: the list has a line for each set.
            var count = _match.IsOn
                ? (Files.Count == _setCount ? string.Empty : $"{Files.Count} shown of ") + $"{_setCount} set{(_setCount == 1 ? string.Empty : "s")} of {pictures}"
                : (Files.Count == _all.Count ? string.Empty : $"{Files.Count} shown of ") + pictures;

            return count + (failed > 0 ? $", {failed} failed" : string.Empty) + size;
        }
    }

    /// <summary>
    /// After the list is made again: the pictures to choose now, by path -
    /// raised for the window, which owns the list's selection.
    /// </summary>
    public event EventHandler<IReadOnlyList<ImageFileItem>>? ChooseRequested;

    /// <summary>
    /// Makes the list again: sorted, matched into sets, then filtered. Sets
    /// are made from every picture, before the filter - so "Failed" shows
    /// each set with a failure in it whole, rather than pairing failures
    /// that do not belong together - and a set is listed when any picture
    /// in it passes the filter and search, by its first picture.
    /// </summary>
    private void ApplyFilter(IReadOnlyCollection<string>? keep)
    {
        var search = _search.Trim();

        bool Passes(ImageFileItem item) => _filter switch
        {
            FileFilter.Failed => item.Failed,
            FileFilter.Passed => item.Passed,
            _ => true,
        } && (search.Length == 0 || item.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase));

        var sets = _match.Sets(Sorted(_all).ToList(), _all);

        foreach (var set in sets)
        {
            foreach (var item in set)
                item.Set = set;
        }

        _setCount = sets.Count;
        _largestSet = sets.Count == 0 ? 0 : sets.Max(set => set.Count);

        // In one go: a station's folder can hold tens of thousands.
        Files.ReplaceAll(sets.Where(set => set.Any(Passes)).Select(set => set[0]));

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));

        // What was chosen, or the set it is now in.
        var keeping = keep is { Count: > 0 } ? new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase) : null;

        var kept = keeping is not null
            ? Files.Where(item => item.Set.Any(member => keeping.Contains(member.Path))).ToArray()
            : [];

        IReadOnlyList<ImageFileItem> chosen = kept.Length > 0 ? kept : Files.Take(1).ToArray();
        Choose(chosen);
        ChooseRequested?.Invoke(this, chosen);
    }

    /// <summary>A folder open with no pictures to show.</summary>
    public bool IsEmpty => HasFolder && Files.Count == 0 && !IsBusy;

    /// <summary>What an empty list says: nothing there, or nothing that matches.</summary>
    public string EmptyText =>
        _all.Count > 0 ? "No pictures match." + (IsSearching ? " Esc clears the search." : string.Empty) : "No pictures here to show.";

    // ---- matching pictures into sets ----------------------------------------------------

    /// <summary>
    /// How pictures that belong together are found: off, every N down the
    /// list, or names alike. Matched, the list has a line for each set - its
    /// first picture - and choosing it shows the set, a picture to a viewer.
    /// </summary>
    public MatchRule Match
    {
        get => _match;
        set
        {
            value ??= MatchRule.Off;

            if (value.Problem() is not null || !SetProperty(ref _match, value))
                return;

            OnPropertyChanged(nameof(IsMatching));
            OnPropertyChanged(nameof(MatchText));
            SaveSettings();
            ApplyFilter(_chosen.SelectMany(item => item.Set).Select(item => item.Path).ToArray());
            FitLayoutToSets();
        }
    }

    public bool IsMatching => _match.IsOn;

    /// <summary>The Match button's tooltip: the rule in use.</summary>
    public string MatchText => _match.IsOn ? $"Matching: {_match.Summary}" : "Match pictures that belong together, and show them side by side";

    /// <summary>
    /// The sets a rule would make of the pictures now listed, as the list is
    /// sorted - for the Match dialog to show before it is used.
    /// </summary>
    public List<IReadOnlyList<ImageFileItem>> SetsFor(MatchRule rule) => rule.Sets(Sorted(_all).ToList(), _all);

    /// <summary>Matched, enough viewers for the largest set - up to 3 × 3.</summary>
    private void FitLayoutToSets()
    {
        if (_match.IsOn && _largestSet > (int)_layout)
            Layout = MatchRule.LayoutFor(Math.Min(_largestSet, MatchRule.MaxGroupSize));
    }

    /// <summary>
    /// The viewers in step: zoomed and scrolled as one, and the pointer's
    /// place on one marked on the others.
    /// </summary>
    public bool SyncViews
    {
        get => _syncViews;
        set
        {
            if (SetProperty(ref _syncViews, value))
                SaveSettings();
        }
    }

    /// <summary>Ctrl+L.</summary>
    public IRelayCommand ToggleSyncViewsCommand { get; }

    // ---- the viewers ------------------------------------------------------------

    public ViewerLayout Layout
    {
        get => _layout;
        set
        {
            if (!Enum.IsDefined(value) || !SetProperty(ref _layout, value))
                return;

            SaveSettings();
            ArrangeTiles();
            Show();
        }
    }

    /// <summary>The layouts, as the toolbar lists them.</summary>
    public static IReadOnlyList<Choice> Layouts { get; } = Choice.Of<ViewerLayout>();

    public IRelayCommand<ViewerLayout> SetLayoutCommand { get; }

    /// <summary>The viewers, one per place in the layout.</summary>
    public ObservableCollection<TileViewModel> Tiles { get; } = [];

    public int Columns => _layout switch
    {
        ViewerLayout.Two or ViewerLayout.Four => 2,
        ViewerLayout.Three or ViewerLayout.Nine => 3,
        _ => 1,
    };

    public int Rows => _layout switch
    {
        ViewerLayout.Four => 2,
        ViewerLayout.Nine => 3,
        _ => 1,
    };

    /// <summary>The pictures chosen in the list, in list order.</summary>
    public IReadOnlyList<ImageFileItem> Chosen => _chosen;

    /// <summary>The list's selection changed.</summary>
    public void Choose(IEnumerable<ImageFileItem> chosen)
    {
        _chosen = chosen.OrderBy(item => Files.IndexOf(item)).ToArray();
        OnPropertyChanged(nameof(Chosen));
        Show();
    }

    /// <summary>
    /// What each viewer shows: the pictures chosen, or - with one chosen - it
    /// and those after it in the list.
    /// </summary>
    internal IReadOnlyList<ImageFileItem> Shown()
    {
        var places = (int)_layout;

        if (_chosen.Count == 0)
            return [];

        // Matched: the set chosen - or the sets - a picture to a viewer.
        if (_match.IsOn)
            return _chosen.SelectMany(item => item.Set).Take(places).ToArray();

        if (_chosen.Count > 1)
            return _chosen.Take(places).ToArray();

        var start = Files.IndexOf(_chosen[0]);
        return start < 0 ? _chosen : Files.Skip(start).Take(places).ToArray();
    }

    private void Show()
    {
        var shown = Shown();

        // Two cameras saving into folders of their own give pictures of one
        // name: their tabs then say the folder too.
        var alike = shown.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < Tiles.Count; i++)
        {
            var file = i < shown.Count ? shown[i] : null;
            Tiles[i].ShowsFolder = file is not null && alike.Contains(file.Name);
            Tiles[i].Show(file);
        }
    }

    /// <summary>
    /// The viewers laid out afresh - the layout changed, or was chosen again:
    /// the window puts each viewer's tab in its place in the grid.
    /// </summary>
    public event EventHandler? TilesArranged;

    private void ArrangeTiles()
    {
        var places = (int)_layout;

        while (Tiles.Count > places)
        {
            Tiles[^1].Show(null);
            Tiles.RemoveAt(Tiles.Count - 1);
        }

        while (Tiles.Count < places)
            Tiles.Add(new TileViewModel(this) { Number = Tiles.Count + 1 });

        // The panel's viewer gone with the layout: it follows the first.
        if (_current is null || !Tiles.Contains(_current))
            CurrentTile = Tiles.FirstOrDefault();

        OnPropertyChanged(nameof(Columns));
        OnPropertyChanged(nameof(Rows));
        TilesArranged?.Invoke(this, EventArgs.Empty);
    }

    // ---- measurements drawn by hand ----------------------------------------------------

    /// <summary>
    /// Measurements drawn on pictures and not saved yet, by picture - each
    /// picture keeps its own, whichever viewer shows it and however often
    /// the list moves on and back.
    /// </summary>
    public bool HasUnsavedChanges => _unsaved.Count > 0;

    /// <summary>How many pictures have measurements not saved.</summary>
    public int UnsavedCount => _unsaved.Count;

    /// <summary>Ctrl+S: writes every picture's measurements into its annotation file.</summary>
    public IRelayCommand SaveCommand { get; }

    /// <summary>Asks a Yes / No / Cancel question; replaced in tests.</summary>
    internal Func<string, string, MessageBoxResult> Ask { get; set; } =
        (message, title) => Sirius.Studio.App.Dialog.Ask(message, title, Application.Current?.MainWindow);

    /// <summary>The measurements a picture has now, saved or not; null for what its file says.</summary>
    internal IReadOnlyList<MeasurementRecord>? UnsavedFor(ImageFileItem file) =>
        _unsaved.TryGetValue(file.Path, out var unsaved) ? unsaved.Measurements : null;

    /// <summary>A viewer's measurements changed, by hand, on the picture it shows.</summary>
    internal void Measured(ImageFileItem file, IReadOnlyList<MeasurementRecord> measurements, IReadOnlyList<MeasurementRecord> saved, int width, int height)
    {
        if (measurements.SequenceEqual(saved, MeasurementComparer.Instance))
            _unsaved.Remove(file.Path);
        else
            _unsaved[file.Path] = new Unsaved(file, measurements, width, height);

        // The same picture is a different item after a refresh: mark both.
        file.IsModified = _unsaved.ContainsKey(file.Path);

        foreach (var item in _all.Where(item => string.Equals(item.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
            item.IsModified = file.IsModified;

        Announce();
    }

    /// <summary>
    /// Writes each picture's measurements into its annotation file - adding
    /// them to what a station wrote there, or making one for a picture that
    /// has none.
    /// </summary>
    /// <returns>Whether everything was saved; <see cref="Problem"/> says what was not.</returns>
    public bool Save()
    {
        var problems = new List<string>();

        foreach (var unsaved in _unsaved.Values.ToArray())
        {
            if (Write(unsaved) is { } problem)
            {
                problems.Add(problem);
                continue;
            }

            _unsaved.Remove(unsaved.File.Path);

            foreach (var item in _all.Where(item => string.Equals(item.Path, unsaved.File.Path, StringComparison.OrdinalIgnoreCase)))
                item.IsModified = false;

            unsaved.File.IsModified = false;
        }

        // What a viewer shows is now what its file says.
        foreach (var tile in Tiles)
            tile.Saved();

        Problem = problems.Count == 0 ? null : string.Join(" ", problems);
        Announce();
        return problems.Count == 0;
    }

    private static string? Write(Unsaved unsaved)
    {
        var path = unsaved.File.AnnotationPath;
        AnnotationFile annotations;

        if (File.Exists(path))
        {
            // A file that cannot be read is left as it is, not replaced.
            if (AnnotationFile.TryLoad(path, out var problem) is not { } existing)
                return $"The measurements on {unsaved.File.Name} were not saved: {problem}";

            annotations = existing;
        }
        else
        {
            annotations = new AnnotationFile { Image = unsaved.File.Name, Width = unsaved.Width, Height = unsaved.Height };
        }

        try
        {
            new AnnotationFile
            {
                Image = annotations.Image ?? unsaved.File.Name,
                Width = annotations.Width > 0 ? annotations.Width : unsaved.Width,
                Height = annotations.Height > 0 ? annotations.Height : unsaved.Height,
                Bits = annotations.Bits,
                Station = annotations.Station,
                Recipe = annotations.Recipe,
                Serial = annotations.Serial,
                Time = annotations.Time,
                Passed = annotations.Passed,
                Grade = annotations.Grade,
                NodeErrors = annotations.NodeErrors,
                Results = annotations.Results,
                Part = annotations.Part,
                Layers = annotations.Layers,
                Measurements = unsaved.Measurements,
            }.Save(path);

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The measurements on {unsaved.File.Name} could not be saved: {ex.Message}";
        }
    }

    /// <summary>
    /// Before leaving the pictures - closing, opening another folder - asks
    /// whether to save measurements not yet saved.
    /// </summary>
    /// <returns>True to go on; false to stay.</returns>
    public bool ConfirmLeaving(string doing = "closing")
    {
        if (!HasUnsavedChanges)
            return true;

        var count = _unsaved.Count;
        var answer = Ask(
            $"Measurements on {count} picture{(count == 1 ? " are" : "s are")} not saved. Save them in the pictures' " +
            $"annotation files before {doing}?",
            AppTitle);

        return answer switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    /// <summary>Lets every unsaved measurement go - after the operator said not to save them.</summary>
    private void Forget()
    {
        _unsaved.Clear();
        Announce();
    }

    // ---- the Annotations panel, and layers on and off (F20b) ----------------------------

    private readonly HashSet<string> _hiddenLayers = new(StringComparer.OrdinalIgnoreCase);
    private TileViewModel? _current;

    /// <summary>What the viewer last clicked shows: its picture's results and shapes, as tables.</summary>
    public AnnotationsViewModel Annotations { get; }

    /// <summary>The viewer last clicked, or made active; the first when none has been.</summary>
    public TileViewModel? CurrentTile
    {
        get => _current;
        set
        {
            if (value is not null && !Tiles.Contains(value))
                return;

            SetProperty(ref _current, value);
        }
    }

    /// <summary>
    /// Whether a layer is drawn. Switched by name, for every viewer and
    /// every picture after - the same viewer's shapes compared across
    /// pictures - until switched on again. Not kept between sessions.
    /// </summary>
    public bool IsLayerShown(string layer) => !_hiddenLayers.Contains(layer);

    public void SetLayerShown(string layer, bool shown)
    {
        if (!(shown ? _hiddenLayers.Remove(layer) : _hiddenLayers.Add(layer)))
            return;

        foreach (var tile in Tiles)
            tile.LayersChanged();

        LayersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A layer was switched on or off.</summary>
    public event EventHandler? LayersChanged;

    // ---- annotations exported as CSV ----------------------------------------------------

    private bool _exporting;

    /// <summary>Annotations are being written out; another export waits.</summary>
    public bool IsExporting
    {
        get => _exporting;
        private set => SetProperty(ref _exporting, value);
    }

    /// <summary>
    /// The pictures chosen in the list - with the rest of each matched set
    /// chosen - in list order.
    /// </summary>
    internal IReadOnlyList<ImageFileItem> ChosenPictures() =>
        _match.IsOn ? _chosen.SelectMany(item => item.Set).Distinct().ToArray() : _chosen;

    /// <summary>
    /// Reads each picture's annotation file and writes them all to one CSV,
    /// a row per picture or per shape, on a worker.
    /// </summary>
    /// <returns>The rows written, and how many pictures had no annotations.</returns>
    internal async Task<(int Rows, int Without)> ExportAsync(IReadOnlyList<ImageFileItem> pictures, bool perShape, string path)
    {
        IsExporting = true;
        Activity = $"Reading annotations: 0 of {pictures.Count} pictures";

        var progress = new Progress<int>(done =>
        {
            if (IsExporting)
                Activity = $"Reading annotations: {done} of {pictures.Count} pictures";
        });

        try
        {
            return await Task.Run(() => Export(pictures, perShape, path, progress));
        }
        finally
        {
            IsExporting = false;
            Activity = null;
        }
    }

    internal static (int Rows, int Without) Export(IReadOnlyList<ImageFileItem> pictures, bool perShape, string path, IProgress<int>? progress = null)
    {
        var read = new List<AnnotationPicture>(pictures.Count);
        var without = 0;

        foreach (var picture in pictures)
        {
            var annotations = System.IO.File.Exists(picture.AnnotationPath)
                ? AnnotationFile.TryLoad(picture.AnnotationPath, out _)
                : null;

            if (annotations is null)
                without++;

            read.Add(new AnnotationPicture(picture.RelativePath, annotations, picture.Verdict));

            if (read.Count % 50 == 0)
                progress?.Report(read.Count);
        }

        // Written beside, then put in place: a failure part way leaves no half a file.
        var temporary = path + ".writing";

        try
        {
            int rows;

            using (var writer = new StreamWriter(temporary, append: false, AnnotationCsv.Encoding))
                rows = perShape ? AnnotationCsv.WriteShapes(writer, read) : AnnotationCsv.WritePictures(writer, read);

            System.IO.File.Move(temporary, path, overwrite: true);
            return (rows, without);
        }
        finally
        {
            if (System.IO.File.Exists(temporary))
                System.IO.File.Delete(temporary);
        }
    }

    // ---- pictures with their annotations drawn on -------------------------------------

    /// <summary>A picture to draw, and the PNG it is written to.</summary>
    internal sealed record DrawnTarget(ImageFileItem Picture, string Path);

    /// <summary>How an export of drawn pictures went.</summary>
    internal sealed record DrawnResult(int Written, IReadOnlyList<string> Problems);

    /// <summary>
    /// Where each picture's drawn copy goes: its own name as a PNG, in the
    /// sub-folder it was in - a station's day - under <paramref name="folder"/>.
    /// Two pictures that would share a name - part.tif and part.bmp - keep
    /// their own kind in it: part_tif.png.
    /// </summary>
    internal static IReadOnlyList<DrawnTarget> DrawnTargets(IReadOnlyList<ImageFileItem> pictures, string folder)
    {
        var stems = pictures
            .GroupBy(picture => System.IO.Path.ChangeExtension(picture.RelativePath, null), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return pictures.Select(picture =>
        {
            var stem = System.IO.Path.ChangeExtension(picture.RelativePath, null);
            var name = stems.Contains(stem) ? $"{stem}_{System.IO.Path.GetExtension(picture.Path).TrimStart('.').ToLowerInvariant()}" : stem;
            return new DrawnTarget(picture, System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, name + ".png")));
        }).ToArray();
    }

    /// <summary>
    /// The shapes to draw on a picture: its annotation file's layers that
    /// are switched on, as the viewers show them.
    /// </summary>
    internal ShapeSet? ShapesToDraw(AnnotationFile? annotations, int width, int height) =>
        annotations is null
            ? null
            : TileViewModel.Frame(annotations, annotations.Layers.Where(layer => IsLayerShown(layer.Name)).ToArray(), width, height);

    /// <summary>
    /// Each picture with its annotations drawn on - the layers switched on,
    /// and the measurements drawn by hand, saved or not - written as a PNG.
    /// Read on a worker, drawn by <paramref name="draw"/> here, written on a
    /// worker again.
    /// </summary>
    /// <param name="draw">Draws a picture as Save Image with Overlays does; frozen.</param>
    internal async Task<DrawnResult> ExportDrawnAsync(
        IReadOnlyList<DrawnTarget> targets,
        Func<BitmapSource, ShapeSet?, IReadOnlyList<MeasurementRecord>, BitmapSource> draw)
    {
        IsExporting = true;
        var problems = new List<string>();
        var written = 0;

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                Activity = $"Drawing pictures: {i} of {targets.Count}";
                var (picture, path) = targets[i];

                try
                {
                    var loaded = await Task.Run(() => TileViewModel.Load(picture));

                    if (loaded.Picture is not { } image)
                    {
                        problems.Add(loaded.Problem ?? $"{picture.RelativePath} could not be read.");
                        continue;
                    }

                    var measurements = UnsavedFor(picture) ?? loaded.Annotations?.Measurements ?? [];
                    var drawn = draw(image, ShapesToDraw(loaded.Annotations, image.PixelWidth, image.PixelHeight), measurements);

                    await Task.Run(() =>
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                        Sirius.Studio.App.Imaging.ImageFileWriter.Save(drawn, path);
                    });

                    written++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                               or InvalidOperationException or ArgumentException or Emgu.CV.Util.CvException)
                {
                    problems.Add($"{picture.RelativePath}: {ex.Message}");
                }
            }

            return new DrawnResult(written, problems);
        }
        finally
        {
            IsExporting = false;
            Activity = null;
        }
    }

    // ---- ROIs from every picture ------------------------------------------------------

    private string? _activity;
    private bool _cropping;

    /// <summary>What is being done in the background - ROIs being cut - or null.</summary>
    public string? Activity
    {
        get => _activity;
        private set => SetProperty(ref _activity, value);
    }

    /// <summary>ROIs are being cut from the pictures; another run waits.</summary>
    public bool IsCropping
    {
        get => _cropping;
        private set => SetProperty(ref _cropping, value);
    }

    /// <summary>
    /// The rectangles to cut from every picture: those on the first viewer
    /// that has any, drawn on its picture whether saved or not - and the
    /// picture they were drawn on.
    /// </summary>
    internal (ImageFileItem Picture, IReadOnlyList<RoiBatch.Roi> Rois)? RoiSource()
    {
        foreach (var tile in Tiles)
        {
            if (tile.File is not { } file)
                continue;

            var rois = RoiBatch.From(tile.Measurements);
            if (rois.Count > 0)
                return (file, rois);
        }

        return null;
    }

    /// <summary>
    /// Every picture in the list as it is filtered - with each matched
    /// set's other pictures, which the list shows as one line.
    /// </summary>
    internal IReadOnlyList<ImageFileItem> ListedPictures() =>
        _match.IsOn ? Files.SelectMany(item => item.Set).Distinct().ToArray() : Files.ToArray();

    /// <summary>
    /// The rectangles drawn on each picture: those not yet saved, else those
    /// in its <c>.sann</c> - read on a worker. A picture with none has none.
    /// </summary>
    internal async Task<IReadOnlyList<RoiBatch.Cut>> OwnRoisAsync(IReadOnlyList<ImageFileItem> pictures)
    {
        // What is drawn and not saved is here, on this thread; the rest is on disk.
        var unsaved = pictures.Select(picture => UnsavedFor(picture)).ToArray();

        Activity = $"Finding the ROIs drawn on {pictures.Count} pictures";

        try
        {
            return await Task.Run(() => pictures.Select((picture, i) =>
            {
                var measurements = unsaved[i]
                    ?? (System.IO.File.Exists(picture.AnnotationPath)
                        ? AnnotationFile.TryLoad(picture.AnnotationPath, out _)?.Measurements
                        : null)
                    ?? [];

                return new RoiBatch.Cut(picture, RoiBatch.From(measurements));
            }).ToArray());
        }
        finally
        {
            Activity = null;
        }
    }

    /// <summary>Cuts the ROIs from each picture into their files, on a worker.</summary>
    internal async Task<RoiBatch.Result> CropAllAsync(IReadOnlyList<RoiBatch.Target> targets)
    {
        var pictures = targets.Select(t => t.Picture).Distinct().Count();

        IsCropping = true;
        Activity = $"Cutting ROIs: 0 of {pictures} pictures";

        // A report posted before the end and run after it is let go.
        var progress = new Progress<int>(done =>
        {
            if (IsCropping)
                Activity = $"Cutting ROIs: {done} of {pictures} pictures";
        });

        try
        {
            return await Task.Run(() => RoiBatch.Run(targets, progress));
        }
        finally
        {
            IsCropping = false;
            Activity = null;
        }
    }

    private void Announce()
    {
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(UnsavedCount));
        OnPropertyChanged(nameof(Title));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private sealed record Unsaved(ImageFileItem File, IReadOnlyList<MeasurementRecord> Measurements, int Width, int Height);

    /// <summary>Two measurements are the same when drawn the same: tool, points, colour and name.</summary>
    internal sealed class MeasurementComparer : IEqualityComparer<MeasurementRecord>
    {
        public static readonly MeasurementComparer Instance = new();

        public bool Equals(MeasurementRecord? a, MeasurementRecord? b) =>
            ReferenceEquals(a, b) ||
            (a is not null && b is not null &&
             a.Tool == b.Tool &&
             string.Equals(a.Colour, b.Colour, StringComparison.OrdinalIgnoreCase) &&
             (a.Name ?? string.Empty) == (b.Name ?? string.Empty) &&
             a.Points.Count == b.Points.Count &&
             a.Points.Zip(b.Points).All(pair => Math.Abs(pair.First.X - pair.Second.X) < 1e-3 && Math.Abs(pair.First.Y - pair.Second.Y) < 1e-3));

        public int GetHashCode(MeasurementRecord record) => record.Tool.GetHashCode(StringComparison.Ordinal);
    }

    // ---- remembered between sessions -----------------------------------------------

    private void ReadSettings()
    {
        if (_settingsPath.Length == 0 || !File.Exists(_settingsPath))
            return;

        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath));

            if (settings is null)
                return;

            _layout = Enum.IsDefined(settings.Layout) ? settings.Layout : ViewerLayout.Single;
            _filter = Enum.IsDefined(settings.Filter) ? settings.Filter : FileFilter.All;
            _sort = Enum.IsDefined(settings.Sort) ? settings.Sort : FileSort.Name;
            _descending = settings.Descending;
            _match = settings.Match is { } match && Enum.IsDefined(match.Mode) && match.Problem() is null ? match : MatchRule.Off;
            _syncViews = settings.SyncViews;
            _includeSubfolders = settings.IncludeSubfolders;
            LastFolder = settings.Folder is { } folder && Directory.Exists(folder) ? folder : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Settings that cannot be read are the defaults.
        }
    }

    private void SaveSettings()
    {
        if (_settingsPath.Length == 0)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new Settings
            {
                Folder = LastFolder,
                Layout = _layout,
                Filter = _filter,
                Sort = _sort,
                Match = _match.IsOn ? _match : null,
                SyncViews = _syncViews,
                Descending = _descending,
                IncludeSubfolders = _includeSubfolders,
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not remembered next time; nothing else is lost.
        }
    }

    private sealed class Settings
    {
        [JsonPropertyName("folder")]
        public string? Folder { get; set; }

        [JsonPropertyName("layout")]
        public ViewerLayout Layout { get; set; } = ViewerLayout.Single;

        [JsonPropertyName("filter")]
        public FileFilter Filter { get; set; }

        [JsonPropertyName("sort")]
        public FileSort Sort { get; set; }

        [JsonPropertyName("descending")]
        public bool Descending { get; set; }

        [JsonPropertyName("match")]
        public MatchRule? Match { get; set; }

        [JsonPropertyName("syncViews")]
        public bool SyncViews { get; set; } = true;

        [JsonPropertyName("includeSubfolders")]
        public bool IncludeSubfolders { get; set; } = true;
    }
}

/// <summary>An entry of a toolbar list: what is shown, and the value behind it.</summary>
public sealed record Choice(string Label, object Value)
{
    public static IReadOnlyList<Choice> Of<T>()
        where T : struct, Enum =>
        Enum.GetValues<T>().Select(value => new Choice(Sirius.Core.Nodes.TypeNames.OptionName(value), value)).ToArray();

    public override string ToString() => Label;
}

/// <summary>
/// The file list's pictures: replaced all at once, with one notice, rather
/// than one by one - tens of thousands of pictures, grouped by folder, would
/// otherwise be re-laid out as each arrived.
/// </summary>
public sealed class FileCollection : ObservableCollection<ImageFileItem>
{
    public void ReplaceAll(IEnumerable<ImageFileItem> items)
    {
        CheckReentrancy();
        Items.Clear();

        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>A picture in the list.</summary>
/// <remarks>
/// Its size on disk and when it was last written come with the folder's
/// listing. Its width and height mean reading the file's header, so they
/// are read only when asked for - when the list shows the picture - on a
/// thread of their own, the most recently asked first: what is on screen
/// fills in, however long the folder.
/// </remarks>
public sealed class ImageFileItem : ObservableObject
{
    private bool _modified;
    private int _width;
    private int _height;
    private int _dimensions; // 0 not asked, 1 being read, 2 read

    public ImageFileItem(string path, string folder, long size = 0, DateTime modified = default)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        RelativePath = System.IO.Path.GetRelativePath(folder, path);
        Size = size;
        Modified = modified;

        Kind = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant() switch
        {
            "JPEG" => "JPG",
            "TIFF" => "TIF",
            var other => other,
        };

        var sub = System.IO.Path.GetDirectoryName(RelativePath);
        Subfolder = string.IsNullOrEmpty(sub) ? null : sub;

        // A station's pictures say how they went in their names.
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        Verdict = new[] { "FAIL", "ERROR", "PASS", "NONE" }
            .FirstOrDefault(word => stem.EndsWith("_" + word, StringComparison.OrdinalIgnoreCase) ||
                                    stem.Contains("_" + word + "_", StringComparison.OrdinalIgnoreCase));
    }

    public string Path { get; }

    /// <summary>The file's name: "20261111-142305-123_000042_FAIL.png".</summary>
    public string Name { get; }

    /// <summary>Its path from the folder open: "2026-11-11\…png".</summary>
    public string RelativePath { get; }

    /// <summary>The sub-folder it is in - a station's day folder - or null.</summary>
    public string? Subfolder { get; }

    /// <summary>FAIL, ERROR, PASS or NONE from a station's file name; null for any other picture.</summary>
    public string? Verdict { get; }

    /// <summary>Has measurements drawn on it that are not saved yet.</summary>
    public bool IsModified
    {
        get => _modified;
        set => SetProperty(ref _modified, value);
    }

    public bool Failed => Verdict is "FAIL" or "ERROR";

    public bool Passed => Verdict is "PASS";

    /// <summary>The annotation file beside it, by name.</summary>
    public string AnnotationPath => Sirius.Core.Annotations.AnnotationFile.PathFor(Path);

    // ---- the file ----------------------------------------------------------------------

    /// <summary>PNG, JPG, BMP or TIF - what its icon says.</summary>
    public string Kind { get; }

    /// <summary>Its size on disk, in bytes.</summary>
    public long Size { get; }

    /// <summary>When it was last written, local time.</summary>
    public DateTime Modified { get; }

    /// <summary>"1.24 MB".</summary>
    public string SizeText => FormatSize(Size);

    /// <summary>"2026-11-11 14:23:05".</summary>
    public string ModifiedText => Modified == default ? string.Empty : Modified.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Its width in pixels; 0 until read, or when it cannot be.</summary>
    public int Width
    {
        get
        {
            RequestDimensions();
            return _width;
        }
    }

    /// <summary>Its height in pixels; 0 until read, or when it cannot be.</summary>
    public int Height
    {
        get
        {
            RequestDimensions();
            return _height;
        }
    }

    /// <summary>"2448 × 2048", once read; empty before.</summary>
    public string DimensionsText
    {
        get
        {
            RequestDimensions();
            return _width > 0 ? $"{_width} × {_height}" : string.Empty;
        }
    }

    /// <summary>The list's second line: "2026-11-11 14:23:05 · 1.24 MB · 2448 × 2048".</summary>
    public string DetailsText =>
        string.Join("  ·  ", new[] { ModifiedText, SizeText, DimensionsText }.Where(part => part.Length > 0));

    /// <summary>Everything, for the list's tooltip.</summary>
    public string Description =>
        string.Join(
            Environment.NewLine,
            new[]
            {
                Path,
                $"{Kind} picture" + (DimensionsText.Length > 0 ? $", {DimensionsText} pixels" : string.Empty),
                $"{SizeText} ({Size:N0} bytes)",
                $"Modified {ModifiedText}",
            }.Concat(Set.Count > 1
                ? ["", $"One of a set of {Set.Count}:", .. Set.Select(item => "  " + item.RelativePath)]
                : []));

    /// <summary>"812 bytes", "12.4 KB", "1.24 MB", "3.1 GB" - as Explorer counts, in 1024s.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return bytes == 1 ? "1 byte" : $"{bytes} bytes";

        string[] units = ["KB", "MB", "GB", "TB"];
        var value = bytes / 1024.0;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = value < 10 ? "0.##" : value < 100 ? "0.#" : "0";
        return value.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }

    // ---- the set it is in ----------------------------------------------------------------

    private IReadOnlyList<ImageFileItem>? _set;

    /// <summary>
    /// The pictures it is matched with, itself among them, in the order the
    /// viewers show them; only itself when nothing is matched.
    /// </summary>
    public IReadOnlyList<ImageFileItem> Set
    {
        get => _set ?? [this];
        internal set
        {
            if (_set is not null && _set.SequenceEqual(value))
                return;

            _set = value;
            OnPropertyChanged(nameof(Set));
            OnPropertyChanged(nameof(SetCountText));
            OnPropertyChanged(nameof(SetVerdict));
            OnPropertyChanged(nameof(Description));
        }
    }

    /// <summary>"+2" beside a set's first picture in the list; null for a picture on its own.</summary>
    public string? SetCountText => Set.Count > 1 ? $"+{Set.Count - 1}" : null;

    /// <summary>How its set went, the worst of them: ERROR, then FAIL, PASS, NONE.</summary>
    public string? SetVerdict
    {
        get
        {
            if (Set.Count == 1)
                return Verdict;

            string[] worstFirst = ["ERROR", "FAIL", "PASS", "NONE"];
            return worstFirst.FirstOrDefault(word => Set.Any(item => item.Verdict == word));
        }
    }

    /// <summary>Its width and height have been read - or tried, and it had none to read.</summary>
    internal bool HasDimensions => _dimensions == 2;

    /// <summary>Width × height, as known now; 0 until read. Sorting by it asks for nothing.</summary>
    internal long PixelCount => (long)_width * _height;

    /// <summary>The width as known now; 0 until read.</summary>
    internal int KnownWidth => _width;

    /// <summary>The width and height have been read: shown from now on.</summary>
    internal void SetDimensions((int Width, int Height)? size)
    {
        _dimensions = 2;

        if (size is not { } known)
            return;

        _width = known.Width;
        _height = known.Height;
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
        OnPropertyChanged(nameof(DimensionsText));
        OnPropertyChanged(nameof(DetailsText));
        OnPropertyChanged(nameof(Description));
    }

    private void RequestDimensions()
    {
        if (_dimensions != 0)
            return;

        _dimensions = 1;
        HeaderReader.Request(this);
    }

    /// <summary>Widths and heights asked for and not read yet are let go - another folder is open.</summary>
    internal static void ForgetPendingReads() => HeaderReader.Forget();

    /// <summary>
    /// Reads pictures' headers one at a time, the most recently asked first,
    /// and hands each size back on the thread that asked - the window's.
    /// </summary>
    private static class HeaderReader
    {
        private static readonly object Gate = new();
        private static readonly Stack<(ImageFileItem Item, SynchronizationContext? Context)> Waiting = new();
        private static bool _reading;

        public static void Request(ImageFileItem item)
        {
            lock (Gate)
            {
                Waiting.Push((item, SynchronizationContext.Current));

                if (_reading)
                    return;

                _reading = true;
            }

            _ = Task.Run(Read);
        }

        public static void Forget()
        {
            lock (Gate)
            {
                foreach (var (item, _) in Waiting)
                    item._dimensions = 0;

                Waiting.Clear();
            }
        }

        private static void Read()
        {
            while (true)
            {
                (ImageFileItem Item, SynchronizationContext? Context) next;

                lock (Gate)
                {
                    if (!Waiting.TryPop(out next))
                    {
                        _reading = false;
                        return;
                    }
                }

                var size = ImageHeader.TryRead(next.Item.Path);

                if (next.Context is { } context)
                    context.Post(_ => next.Item.SetDimensions(size), null);
                else
                    next.Item.SetDimensions(size);
            }
        }
    }

    public override string ToString() => RelativePath;
}
