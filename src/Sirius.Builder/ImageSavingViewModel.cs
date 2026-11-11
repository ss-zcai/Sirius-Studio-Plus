using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sirius.Core.Nodes;
using Sirius.Core.Projects;

namespace Sirius.Builder;

/// <summary>
/// A station's image saving, being edited: which pictures it keeps when it
/// runs, as what, where, and for how long. Nothing is written until
/// <see cref="Apply"/>.
/// </summary>
public sealed class ImageSavingViewModel : ObservableObject
{
    private readonly Project _project;
    private ImageSaving _draft;
    private string _keepText;
    private string? _problem;

    public ImageSavingViewModel(Project project, Station station)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        Station = station ?? throw new ArgumentNullException(nameof(station));
        _draft = station.Saving;
        _keepText = _draft.KeepDays.ToString(CultureInfo.InvariantCulture);
        BrowseCommand = new RelayCommand(BrowseFolder);
        Categories = PropertyCategory.From(Rows());
    }

    public Station Station { get; }

    /// <summary>The settings as they stand in the dialog.</summary>
    public ImageSaving Draft => _draft;

    public IReadOnlyList<PropertyCategory> Categories { get; }

    public IRelayCommand BrowseCommand { get; }

    /// <summary>Picks a folder; replaced in tests.</summary>
    internal Func<string?, string?> PickFolder { get; set; } = start =>
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Save Images In",
            InitialDirectory = start is not null && Directory.Exists(start) ? start : string.Empty,
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    };

    /// <summary>Why the settings cannot be kept as they are, or null.</summary>
    public string? Problem
    {
        get => _problem;
        private set => SetProperty(ref _problem, value);
    }

    /// <summary>Where this station's pictures go, in full: the folder a day's folders are made in.</summary>
    public string StationFolder => _draft.FolderFor(_project, Station);

    /// <summary>"…\images\Top\2026-11-11\20261111-142305-123_000042_FAIL.png" - what one will be called.</summary>
    public string Example => Path.Combine(
        StationFolder,
        ImageSaving.DayFolder(DateTime.Today),
        ImageSaving.FileStem(DateTime.Today.AddHours(14.38), 42, "FAIL") + ImageSaving.ExtensionOf(_draft.Format));

    /// <summary>Keeps the settings in the project. Returns false, with <see cref="Problem"/> set, when they cannot be.</summary>
    public bool Apply()
    {
        if ((KeepProblem() ?? _draft.Problem()) is { } problem)
        {
            Problem = problem;
            return false;
        }

        try
        {
            _project.SetSaving(Station, _draft);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Problem = $"The project could not be saved: {ex.Message}";
            return false;
        }
    }

    private string? KeepProblem() =>
        int.TryParse(_keepText, NumberStyles.Integer, CultureInfo.CurrentCulture, out _)
            ? null
            : "Keep for is a whole number of days: 0 keeps everything.";

    private void Change(ImageSaving draft)
    {
        _draft = draft;
        Problem = draft.Problem();

        foreach (var category in Categories)
            category.Refresh();

        OnPropertyChanged(nameof(Draft));
        OnPropertyChanged(nameof(StationFolder));
        OnPropertyChanged(nameof(Example));
    }

    private void BrowseFolder()
    {
        if (PickFolder(Directory.Exists(StationFolder) ? StationFolder : _draft.RootFor(_project)) is { } chosen)
            Change(_draft with { Folder = Relative(chosen) });
    }

    /// <summary>A folder inside the project, written relative to it, so the project can be moved; others as they are.</summary>
    private string? Relative(string folder)
    {
        var full = Path.GetFullPath(folder);
        var relative = Path.GetRelativePath(_project.Folder, full);

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return full;

        return relative == "." || string.Equals(relative, ImageSaving.DefaultFolder, StringComparison.OrdinalIgnoreCase)
            ? null
            : relative;
    }

    private IEnumerable<PropertyRow> Rows()
    {
        bool On() => _draft.IsOn;

        yield return new PropertyRow(
            "Saving", "Save", PropertyEditor.Choice,
            () => _draft.Policy,
            value => { if (value is ImageSavePolicy policy) Change(_draft with { Policy = policy }); },
            "Which inspections keep their picture: none, those that failed - and those a node went wrong in - or all of them.",
            options: () => Enum.GetValues<ImageSavePolicy>().Select(policy => new PropertyOption(TypeNames.OptionName(policy), policy)).ToArray());

        yield return new PropertyRow(
            "Saving", "Format", PropertyEditor.Choice,
            () => _draft.Format,
            value => { if (value is ImageFileFormat format) Change(_draft with { Format = format }); },
            "PNG keeps every value, 10-, 12- and 16-bit too, and can be inspected again exactly. JPEG is smaller but lossy; " +
            "JPEG and BMP hold 8 bits only, so a deeper picture is saved as PNG whichever is chosen.",
            options: () => Enum.GetValues<ImageFileFormat>().Select(format => new PropertyOption(TypeNames.OptionName(format), format)).ToArray(),
            enabled: On);

        yield return new PropertyRow(
            "Saving", "Annotations", PropertyEditor.Check,
            () => _draft.Annotations,
            value => { if (value is bool on) Change(_draft with { Annotations = on }); },
            "A .sann file beside each picture with the same name: the shapes the recipe's viewers drew, the verdict and the " +
            "published results. Sirius IV shows them over the picture.",
            enabled: On);

        yield return new PropertyRow(
            "Where", "Folder", PropertyEditor.File,
            () => _draft.Folder ?? string.Empty,
            value => Change(_draft with { Folder = string.IsNullOrWhiteSpace(value as string) ? null : ((string)value!).Trim() }),
            "Blank for the project's images folder. A folder inside the project is kept relative to it, so the project can be " +
            "copied to the machine as it is; another drive suits a line that saves every picture. Each station saves into a " +
            "folder of its own name here, with a folder for each day.",
            enabled: On,
            browse: BrowseCommand);

        yield return new PropertyRow(
            "Where", "Station's folder", PropertyEditor.ReadOnly,
            () => StationFolder,
            description: "Where this station's pictures go.");

        yield return new PropertyRow(
            "Where", "Keep for (days)", PropertyEditor.Text,
            () => _keepText,
            value =>
            {
                _keepText = (value as string ?? string.Empty).Trim();

                if (int.TryParse(_keepText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var days))
                    Change(_draft with { KeepDays = days });
                else
                    Problem = KeepProblem();
            },
            "Days of pictures kept, today among them: older days' folders are deleted when the station starts and as each " +
            "day begins. 0 keeps everything. Saving stops by itself when the disk has less than 1 GB free.",
            enabled: On);
    }
}
