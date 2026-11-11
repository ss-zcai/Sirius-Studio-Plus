using System.Globalization;
using Sirius.Core.Graph;

namespace Sirius.Core.Projects;

/// <summary>Which inspections a station keeps the picture of.</summary>
public enum ImageSavePolicy
{
    /// <summary>None: nothing is written.</summary>
    None,

    /// <summary>
    /// The inspections that failed - and those that could not be judged
    /// because a node went wrong, which are what someone will want to look at.
    /// </summary>
    [System.ComponentModel.Description("Failed only")]
    Failed,

    /// <summary>Every inspection.</summary>
    All,
}

/// <summary>The file a saved picture is written as.</summary>
public enum ImageFileFormat
{
    /// <summary>Lossless, and keeps 10-, 12- and 16-bit pictures as they are. The default.</summary>
    [System.ComponentModel.Description("PNG")]
    Png,

    /// <summary>Small, but lossy and 8-bit only: a deeper picture is written as PNG instead.</summary>
    [System.ComponentModel.Description("JPEG")]
    Jpeg,

    /// <summary>Uncompressed, 8-bit only: a deeper picture is written as PNG instead.</summary>
    [System.ComponentModel.Description("BMP")]
    Bmp,
}

/// <summary>
/// How a station keeps the pictures it inspects, and the annotations found
/// on them - chosen per station in Sirius Builder, done by the runtime.
/// </summary>
/// <param name="Policy">Which inspections are kept.</param>
/// <param name="Format">The picture's file type.</param>
/// <param name="Folder">
/// Where, absolute or relative to the project folder; null or blank for the
/// project's <c>images</c> folder. Each station writes into a folder of its
/// own name under it, and a folder for each day under that.
/// </param>
/// <param name="KeepDays">Days of pictures kept; older days' folders are deleted. 0 keeps everything.</param>
/// <param name="Annotations">
/// Writes a <c>.sann</c> file beside each picture: the shapes drawn on it,
/// the verdict and the published results.
/// </param>
public sealed record ImageSaving(
    ImageSavePolicy Policy = ImageSavePolicy.None,
    ImageFileFormat Format = ImageFileFormat.Png,
    string? Folder = null,
    int KeepDays = 0,
    bool Annotations = true)
{
    /// <summary>The project folder's own place for pictures.</summary>
    public const string DefaultFolder = "images";

    /// <summary>The longest a station can be told to keep pictures: ten years.</summary>
    public const int MaxKeepDays = 3650;

    /// <summary>Saving nothing, which is where every station starts.</summary>
    public static ImageSaving Off { get; } = new();

    /// <summary>Whether this saves anything at all.</summary>
    public bool IsOn => Policy != ImageSavePolicy.None;

    /// <summary>Whether an inspection with this verdict, and this many node errors, is kept.</summary>
    public bool Wants(Verdict verdict, int nodeErrors) => Policy switch
    {
        ImageSavePolicy.All => true,
        ImageSavePolicy.Failed => verdict.Passed == false || nodeErrors > 0,
        _ => false,
    };

    /// <summary>The folder every station of <paramref name="project"/> saves under, in full.</summary>
    public string RootFor(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var folder = string.IsNullOrWhiteSpace(Folder) ? DefaultFolder : Environment.ExpandEnvironmentVariables(Folder.Trim());
        return Path.GetFullPath(Path.Combine(project.Folder, folder));
    }

    /// <summary>The folder one station saves into: a folder of its name under the root.</summary>
    public string FolderFor(Project project, Station station)
    {
        ArgumentNullException.ThrowIfNull(station);
        return Path.Combine(RootFor(project), SafeName(station.Name));
    }

    /// <summary>
    /// What is wrong with these settings, or null. Checked as they are set,
    /// so the runtime never meets them.
    /// </summary>
    public string? Problem()
    {
        if (KeepDays is < 0 or > MaxKeepDays)
            return $"Keep for must be between 0 (keep everything) and {MaxKeepDays} days.";

        if (!string.IsNullOrWhiteSpace(Folder))
        {
            try
            {
                _ = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Environment.ExpandEnvironmentVariables(Folder.Trim())));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return $"'{Folder}' is not a folder name Windows accepts.";
            }
        }

        return null;
    }

    /// <summary>"Failed only, as PNG, kept 30 days" - for a station's line in Builder.</summary>
    public string Summary
    {
        get
        {
            if (!IsOn)
                return "Saves no images";

            var which = Policy == ImageSavePolicy.All ? "Saves every image" : "Saves failed images";
            var kept = KeepDays > 0 ? $", {KeepDays} days" : string.Empty;
            return $"{which}, {Format.ToString().ToUpperInvariant()}{kept}";
        }
    }

    // ---- names ------------------------------------------------------------

    /// <summary>A day's folder: "2026-11-11".</summary>
    public static string DayFolder(DateTime time) => time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Reads a day folder's name back, or null for a folder that is not one.</summary>
    public static DateTime? DayOf(string folderName) =>
        DateTime.TryParseExact(folderName, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;

    /// <summary>
    /// A picture's name, without its extension: when, which inspection, and
    /// how it went - "20261111-142305-123_000042_FAIL". Sorted by name, a
    /// folder is in the order the pictures were taken.
    /// </summary>
    public static string FileStem(DateTime time, long serial, Verdict verdict, int nodeErrors) =>
        FileStem(time, serial, VerdictWord(verdict, nodeErrors));

    /// <summary>A picture's name, with the verdict already in a word.</summary>
    public static string FileStem(DateTime time, long serial, string verdictWord) =>
        string.Create(CultureInfo.InvariantCulture, $"{time:yyyyMMdd-HHmmss-fff}_{serial:000000}_{verdictWord}");

    /// <summary>PASS, FAIL, ERROR - a node went wrong - or NONE when nothing judges.</summary>
    public static string VerdictWord(Verdict verdict, int nodeErrors) =>
        nodeErrors > 0 && verdict.Passed != false ? "ERROR"
        : verdict.Passed switch
        {
            true => "PASS",
            false => "FAIL",
            null => "NONE",
        };

    /// <summary>A station's name as a folder name: what Windows will not take replaced with '_'.</summary>
    public static string SafeName(string name)
    {
        var safe = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return safe.Length == 0 ? "station" : safe;
    }

    /// <summary>The extension a picture in <paramref name="format"/> is written with.</summary>
    public static string ExtensionOf(ImageFileFormat format) => format switch
    {
        ImageFileFormat.Jpeg => ".jpg",
        ImageFileFormat.Bmp => ".bmp",
        _ => ".png",
    };
}
