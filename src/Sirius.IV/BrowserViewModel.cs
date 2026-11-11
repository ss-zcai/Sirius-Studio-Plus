using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

/// <summary>How many pictures are shown at once, and how they are laid out.</summary>
public enum ViewerLayout
{
    [Description("One")]
    Single = 1,

    [Description("Two side by side")]
    Two = 2,

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
    private List<ImageFileItem> _all = [];
    private IReadOnlyList<ImageFileItem> _chosen = [];
    private string? _folder;
    private FileFilter _filter;
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
        SetLayoutCommand = new RelayCommand<ViewerLayout>(layout => Layout = layout);
        ReadSettings();
        ArrangeTiles();
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

    public string Title => _folder is null ? AppTitle : $"{Path.GetFileName(_folder.TrimEnd(Path.DirectorySeparatorChar))} - {AppTitle}";

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

    /// <summary>Opens a folder - or a picture, opening its folder with it chosen.</summary>
    public async Task OpenAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        var picture = File.Exists(full) ? full : null;
        var folder = picture is null ? full : Path.GetDirectoryName(full)!;

        if (!Directory.Exists(folder))
        {
            Problem = $"{folder} does not exist.";
            return;
        }

        Folder = folder;
        LastFolder = folder;
        SaveSettings();

        await ListAsync(picture is null ? null : [picture]).ConfigureAwait(true);
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

        _all = found;
        ApplyFilter(keep);
    }

    /// <summary>The pictures under a folder, sorted by path - for a station's, the order they were taken.</summary>
    internal static List<ImageFileItem> List(string folder, bool subfolders)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = subfolders,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        return Directory.EnumerateFiles(folder, "*", options)
            .Where(path => Extensions.Contains(Path.GetExtension(path)) && !path.Contains(".saving.", StringComparison.OrdinalIgnoreCase))
            .Select(path => new ImageFileItem(path, folder))
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- the list --------------------------------------------------------------

    /// <summary>The pictures the filter lets through, in order.</summary>
    public ObservableCollection<ImageFileItem> Files { get; } = [];

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

    /// <summary>"312 pictures, 12 failed".</summary>
    public string CountText
    {
        get
        {
            if (!HasFolder)
                return string.Empty;

            var failed = _all.Count(item => item.Failed);
            var shown = Files.Count == _all.Count ? string.Empty : $"{Files.Count} shown of ";
            return $"{shown}{_all.Count} picture{(_all.Count == 1 ? string.Empty : "s")}" + (failed > 0 ? $", {failed} failed" : string.Empty);
        }
    }

    /// <summary>
    /// After the list is made again: the pictures to choose now, by path -
    /// raised for the window, which owns the list's selection.
    /// </summary>
    public event EventHandler<IReadOnlyList<ImageFileItem>>? ChooseRequested;

    private void ApplyFilter(IReadOnlyCollection<string>? keep)
    {
        var shown = _all.Where(item => _filter switch
        {
            FileFilter.Failed => item.Failed,
            FileFilter.Passed => item.Passed,
            _ => true,
        });

        Files.Clear();

        foreach (var item in shown)
            Files.Add(item);

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));

        var kept = keep is { Count: > 0 }
            ? Files.Where(item => keep.Contains(item.Path, StringComparer.OrdinalIgnoreCase)).ToArray()
            : [];

        IReadOnlyList<ImageFileItem> chosen = kept.Length > 0 ? kept : Files.Take(1).ToArray();
        Choose(chosen);
        ChooseRequested?.Invoke(this, chosen);
    }

    /// <summary>A folder open with no pictures to show.</summary>
    public bool IsEmpty => HasFolder && Files.Count == 0 && !IsBusy;

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
        ViewerLayout.Nine => 3,
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

        if (_chosen.Count > 1)
            return _chosen.Take(places).ToArray();

        if (_chosen.Count == 0)
            return [];

        var start = Files.IndexOf(_chosen[0]);
        return start < 0 ? _chosen : Files.Skip(start).Take(places).ToArray();
    }

    private void Show()
    {
        var shown = Shown();

        for (var i = 0; i < Tiles.Count; i++)
            Tiles[i].Show(i < shown.Count ? shown[i] : null);
    }

    private void ArrangeTiles()
    {
        var places = (int)_layout;

        while (Tiles.Count > places)
        {
            Tiles[^1].Show(null);
            Tiles.RemoveAt(Tiles.Count - 1);
        }

        while (Tiles.Count < places)
            Tiles.Add(new TileViewModel());

        OnPropertyChanged(nameof(Columns));
        OnPropertyChanged(nameof(Rows));
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

/// <summary>A picture in the list.</summary>
public sealed class ImageFileItem
{
    public ImageFileItem(string path, string folder)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        RelativePath = System.IO.Path.GetRelativePath(folder, path);

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

    public bool Failed => Verdict is "FAIL" or "ERROR";

    public bool Passed => Verdict is "PASS";

    /// <summary>The annotation file beside it, by name.</summary>
    public string AnnotationPath => Sirius.Core.Annotations.AnnotationFile.PathFor(Path);

    public override string ToString() => RelativePath;
}
