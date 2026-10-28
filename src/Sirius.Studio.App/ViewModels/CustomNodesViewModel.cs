using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Sirius.Vision.Plugins;

namespace Sirius.Studio.App.ViewModels;

/// <summary>Where a custom node library was found.</summary>
public enum CustomNodeSource
{
    /// <summary>The <c>plugins</c> folder beside the application.</summary>
    Shipped,

    /// <summary>The per-user plugins folder under application data.</summary>
    PerUser,

    /// <summary>Chosen by hand in the Custom Nodes window, and remembered.</summary>
    Added,
}

/// <summary>One library, as the Custom Nodes window lists it.</summary>
public sealed class CustomNodeLibraryViewModel : ObservableObject
{
    private bool _isRemoved;

    public CustomNodeLibraryViewModel(PluginLibrary library, CustomNodeSource source)
    {
        Library = library;
        Source = source;
    }

    public PluginLibrary Library { get; }

    public CustomNodeSource Source { get; }

    public string Title => Library.Title;

    public string Path => Library.Path;

    public IReadOnlyList<string> Problems => Library.Problems;

    public bool HasProblems => Problems.Count > 0;

    /// <summary>True when nothing from it reached the toolbox.</summary>
    public bool Failed => Library.Nodes.Count == 0;

    /// <summary>"Adds Gamma, Bright Region." or why it added nothing.</summary>
    public string Summary => Failed
        ? "Added no nodes."
        : $"Adds {string.Join(", ", Library.Nodes.Select(node => node.DisplayName))}.";

    public string SourceText => Source switch
    {
        CustomNodeSource.Shipped => "From the application's plugins folder",
        CustomNodeSource.PerUser => "From your plugins folder",
        _ => "Added by hand",
    };

    /// <summary>
    /// Only a library added by hand can be removed here; one in a plugins
    /// folder is removed by deleting it from the folder.
    /// </summary>
    public bool CanRemove => Source == CustomNodeSource.Added && !IsRemoved;

    /// <summary>
    /// Forgotten, so it will not load next time. Its nodes stay in the
    /// toolbox until then: a recipe open now may be using them, and code
    /// already loaded cannot be taken back out of a running process.
    /// </summary>
    public bool IsRemoved
    {
        get => _isRemoved;
        set
        {
            if (SetProperty(ref _isRemoved, value))
                OnPropertyChanged(nameof(CanRemove));
        }
    }
}

/// <summary>
/// The custom node libraries: finding them at startup, adding and removing
/// them, and remembering which were added by hand.
/// </summary>
public sealed class CustomNodesViewModel : ObservableObject
{
    private readonly string _settingsPath;
    private readonly List<string> _remembered;

    public CustomNodesViewModel(string settingsPath, string shippedFolder, string userFolder)
    {
        _settingsPath = settingsPath;
        ShippedFolder = shippedFolder;
        UserFolder = userFolder;
        _remembered = ReadRemembered(settingsPath);
    }

    /// <summary>The application's own plugins folder.</summary>
    public string ShippedFolder { get; }

    /// <summary>This user's plugins folder.</summary>
    public string UserFolder { get; }

    public ObservableCollection<CustomNodeLibraryViewModel> Libraries { get; } = [];

    /// <summary>Raised when new nodes reached the catalogue, so the toolbox can show them.</summary>
    public event EventHandler? CatalogueChanged;

    /// <summary>The paths added by hand, in the order they were added.</summary>
    public IReadOnlyList<string> Remembered => _remembered;

    /// <summary>
    /// Loads every library from both folders and every one remembered.
    /// </summary>
    /// <remarks>
    /// A remembered file that has since gone is still listed, with the
    /// reason, rather than silently forgotten - otherwise a library that
    /// moved would simply vanish and its nodes with it.
    /// </remarks>
    public void LoadAll()
    {
        var before = Libraries.Count;

        foreach (var path in PluginLoader.FindLibraries(ShippedFolder))
            Install(path, CustomNodeSource.Shipped);

        foreach (var path in PluginLoader.FindLibraries(UserFolder))
            Install(path, CustomNodeSource.PerUser);

        foreach (var path in _remembered.ToArray())
            Install(path, CustomNodeSource.Added);

        if (Libraries.Count != before)
            CatalogueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds a library chosen by hand, and remembers it for next time.</summary>
    public CustomNodeLibraryViewModel Add(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);

        if (Find(fullPath) is { } existing)
        {
            // Removed and then added again in the same session: it never
            // stopped being loaded, so just remember it again.
            if (existing.IsRemoved)
            {
                existing.IsRemoved = false;
                Remember(fullPath);
            }

            return existing;
        }

        var source = SourceOf(fullPath);
        var row = Install(fullPath, source);

        if (source == CustomNodeSource.Added)
            Remember(fullPath);

        if (!row.Failed)
            CatalogueChanged?.Invoke(this, EventArgs.Empty);

        return row;
    }

    /// <summary>Forgets a library added by hand. Takes effect when the application next starts.</summary>
    public void Remove(CustomNodeLibraryViewModel library)
    {
        if (!library.CanRemove)
            return;

        library.IsRemoved = true;
        _remembered.RemoveAll(path => string.Equals(path, library.Path, StringComparison.OrdinalIgnoreCase));
        WriteRemembered();
        OnPropertyChanged(nameof(NeedsRestart));
    }

    /// <summary>Whether a removal is waiting for the application to restart.</summary>
    public bool NeedsRestart => Libraries.Any(library => library.IsRemoved);

    /// <summary>
    /// A line for the status bar when something did not load, or null when
    /// everything did.
    /// </summary>
    public string? ProblemSummary
    {
        get
        {
            var troubled = Libraries.Count(library => library.HasProblems);

            return troubled == 0
                ? null
                : $"{troubled} custom node librar{(troubled == 1 ? "y" : "ies")} had problems loading - " +
                  "see Tools > Custom Nodes.";
        }
    }

    private CustomNodeLibraryViewModel Install(string path, CustomNodeSource source)
    {
        if (Find(System.IO.Path.GetFullPath(path)) is { } existing)
            return existing;

        var row = new CustomNodeLibraryViewModel(PluginLoader.Install(path), source);
        Libraries.Add(row);
        OnPropertyChanged(nameof(ProblemSummary));
        return row;
    }

    private CustomNodeLibraryViewModel? Find(string fullPath) =>
        Libraries.FirstOrDefault(library =>
            string.Equals(library.Path, fullPath, StringComparison.OrdinalIgnoreCase));

    private CustomNodeSource SourceOf(string fullPath) =>
        IsUnder(fullPath, ShippedFolder) ? CustomNodeSource.Shipped
        : IsUnder(fullPath, UserFolder) ? CustomNodeSource.PerUser
        : CustomNodeSource.Added;

    private static bool IsUnder(string path, string folder)
    {
        var root = System.IO.Path.GetFullPath(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar) +
                   System.IO.Path.DirectorySeparatorChar;

        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private void Remember(string fullPath)
    {
        if (_remembered.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            return;

        _remembered.Add(fullPath);
        WriteRemembered();
    }

    private static List<string> ReadRemembered(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path))?.Libraries ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged list means starting without the hand-added
            // libraries, which the operator can add again - not failing to
            // start at all.
            return [];
        }
    }

    private void WriteRemembered()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(
                _settingsPath,
                JsonSerializer.Serialize(new Settings { Libraries = _remembered }, WriteOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth interrupting anybody over: the library is loaded
            // now, and only next session's start would miss it.
        }
    }

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private sealed class Settings
    {
        public List<string> Libraries { get; set; } = [];
    }
}
