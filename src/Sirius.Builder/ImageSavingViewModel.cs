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

    /// <summary>What is typed in each number box - most size, leave free, sample - by its row's name.</summary>
    private readonly Dictionary<string, string> _numbers = new(StringComparer.Ordinal);

    private const string MostSize = "Most size (GB)";
    private const string LeaveFree = "Leave free (GB)";
    private const string Sample = "Keep a sample (%)";

    public ImageSavingViewModel(Project project, Station station)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        Station = station ?? throw new ArgumentNullException(nameof(station));
        _draft = station.Saving;
        _keepText = _draft.KeepDays.ToString(CultureInfo.InvariantCulture);
        _numbers[MostSize] = Number(_draft.MaxGigabytes);
        _numbers[LeaveFree] = Number(_draft.LeaveFreeGigabytes);
        _numbers[Sample] = Number(_draft.SamplePercent);
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
        !int.TryParse(_keepText, NumberStyles.Integer, CultureInfo.CurrentCulture, out _)
            ? "Keep for is a whole number of days: 0 keeps everything."
            : _numbers.Where(pair => Parse(pair.Value) is null).Select(pair => $"{pair.Key[..pair.Key.IndexOf(" (", StringComparison.Ordinal)]} is a number: 0 for none.").FirstOrDefault();

    private static string Number(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    private static double? Parse(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ? value : null;

    /// <summary>
    /// The sample kept so far, from this machine's view of the folder - the
    /// line's, when Builder runs on it: "1,204 pictures, 1.2 GB", or that
    /// there is none.
    /// </summary>
    public string KeptText
    {
        get
        {
            var kept = Path.Combine(StationFolder, ImageSaving.KeptFolder);

            // Counted once for a folder: the grid asks again at every change typed.
            if (_kept is { } known && known.Folder == kept)
                return known.Text;

            _kept = (kept, CountKept(kept));
            return _kept.Value.Text;
        }
    }

    private (string Folder, string Text)? _kept;

    private static string CountKept(string kept)
    {
        {
            try
            {
                if (!Directory.Exists(kept))
                    return "None yet";

                var files = new DirectoryInfo(kept).EnumerateFiles("*", SearchOption.AllDirectories).ToArray();
                var pictures = files.Count(file => !file.Extension.Equals(".sann", StringComparison.OrdinalIgnoreCase));
                var bytes = files.Sum(file => file.Length);

                return string.Create(
                    CultureInfo.CurrentCulture,
                    $"{pictures:N0} picture{(pictures == 1 ? string.Empty : "s")}, {bytes / (double)(1L << 30):0.##} GB, in {kept}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"Could not be read: {ex.Message}";
            }
        }
    }

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

    /// <summary>A number of GB, or a percentage, typed; 0 for none.</summary>
    private PropertyRow NumberRow(string name, Func<double, ImageSaving> with, string description) =>
        new(
            "Housekeeping", name, PropertyEditor.Text,
            () => _numbers[name],
            value =>
            {
                _numbers[name] = (value as string ?? string.Empty).Trim();

                if (Parse(_numbers[name]) is { } number)
                    Change(with(number));
                else
                    Problem = KeepProblem();
            },
            description,
            enabled: () => _draft.IsOn);

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
            "TIF and PNG keep every value, 10-, 12- and 16-bit too, and can be inspected again exactly; TIF is what lines " +
            "usually keep. JPEG is smaller but lossy; JPEG and BMP hold 8 bits only, so a deeper picture is saved as PNG " +
            "whichever of them is chosen.",
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
            "Housekeeping", "Keep for (days)", PropertyEditor.Text,
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
            "day begins. 0 keeps everything. Saving stops by itself when the disk has less than 1 GB free, and starts again " +
            "once there is room.",
            enabled: On);

        yield return NumberRow(
            MostSize,
            value => _draft with { MaxGigabytes = value },
            "The most this station's pictures may take - its Kept sample not counted. Over it, the oldest passed pictures " +
            "are deleted first, then the oldest failures, down to 90% of it. 0 for no limit.");

        yield return NumberRow(
            LeaveFree,
            value => _draft with { LeaveFreeGigabytes = value },
            "Free space always left on the drive the pictures are on, for Windows and everything else on the PC. Under it, " +
            "pictures are deleted as for Most size, until 1 GB more than this is free. 0 for none.");

        yield return NumberRow(
            Sample,
            value => _draft with { SamplePercent = value },
            "Of the pictures housekeeping deletes - by days or by size - the share kept for records instead, moved with " +
            "their .sann to the station's Kept folder, which is never deleted by itself. 1 keeps 1 in every 100 inspections, " +
            "spread evenly through the day, passed and failed alike. 0 keeps none.");

        yield return new PropertyRow(
            "Housekeeping", "Kept so far", PropertyEditor.ReadOnly,
            () => KeptText,
            description: "The sample kept, in the station's Kept folder. Sirius IV opens it as any folder; delete it by hand " +
                         "when it is no longer wanted.");
    }
}
