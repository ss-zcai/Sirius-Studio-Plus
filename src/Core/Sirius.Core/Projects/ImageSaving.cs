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
    /// <summary>Lossless, and keeps 10-, 12- and 16-bit pictures as they are.</summary>
    [System.ComponentModel.Description("PNG")]
    Png,

    /// <summary>
    /// Lossless (LZW), and keeps 10-, 12- and 16-bit pictures as they are:
    /// what lines usually keep, beside their annotations. The default.
    /// </summary>
    [System.ComponentModel.Description("TIF")]
    Tiff,

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
/// <param name="MaxGigabytes">
/// The most the station's pictures may take, in GB; 0 for no limit. Over
/// it, the oldest passed pictures go first, then the oldest failures.
/// </param>
/// <param name="LeaveFreeGigabytes">
/// Free space always left on the drive the pictures are on, in GB; 0 for
/// none beyond the 1 GB at which saving stops. Under it, pictures go as
/// for <paramref name="MaxGigabytes"/>.
/// </param>
/// <param name="SamplePercent">
/// Of the pictures housekeeping deletes - by days or by size - the share
/// kept for records instead, moved to the station's <c>Kept</c> folder,
/// which housekeeping never touches: 1 keeps every inspection numbered a
/// multiple of 100. 0 keeps none.
/// </param>
public sealed record ImageSaving(
    ImageSavePolicy Policy = ImageSavePolicy.None,
    ImageFileFormat Format = ImageFileFormat.Tiff,
    string? Folder = null,
    int KeepDays = 0,
    bool Annotations = true,
    double MaxGigabytes = 0,
    double LeaveFreeGigabytes = 0,
    double SamplePercent = 0)
{
    /// <summary>The project folder's own place for pictures.</summary>
    public const string DefaultFolder = "images";

    /// <summary>The longest a station can be told to keep pictures: ten years.</summary>
    public const int MaxKeepDays = 3650;

    /// <summary>The most a size can be set to: a petabyte, beyond any disk a station has.</summary>
    public const double MaxSizeGigabytes = 1_000_000;

    /// <summary>The folder, under a station's, that housekeeping moves its sample to and never touches.</summary>
    public const string KeptFolder = "Kept";

    /// <summary>Whether housekeeping deletes anything at all: by days, by size, or by free space.</summary>
    public bool HasHousekeeping => KeepDays > 0 || MaxGigabytes > 0 || LeaveFreeGigabytes > 0;

    /// <summary>
    /// Which inspections the sample keeps: one in this many, by number - 100
    /// for 1%; 0 for none.
    /// </summary>
    public int SampleEvery => SamplePercent <= 0 ? 0 : Math.Max(1, (int)Math.Round(100 / Math.Min(SamplePercent, 100)));

    /// <summary>Whether an inspection numbered <paramref name="serial"/> is in the sample kept.</summary>
    public bool Samples(long serial) => SampleEvery > 0 && serial > 0 && serial % SampleEvery == 0;

    /// <summary>Bytes in a GB, as Windows counts them.</summary>
    public static long Bytes(double gigabytes) => (long)(gigabytes * (1L << 30));

    /// <summary>Saving nothing, which is where every station starts.</summary>
    public static ImageSaving Off { get; } = new();

    /// <summary>Whether this saves anything at all.</summary>
    public bool IsOn => Policy != ImageSavePolicy.None;

    /// <summary>Whether an inspection with this verdict, and this many node errors, is kept.</summary>
    public bool Wants(Verdict verdict, int nodeErrors) => Policy switch
    {
        ImageSavePolicy.All => true,
        ImageSavePolicy.Failed => verdict.Accepted == false || nodeErrors > 0,
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

        if (double.IsNaN(MaxGigabytes) || MaxGigabytes is < 0 or > MaxSizeGigabytes)
            return "Most size is a number of GB, 0 for no limit.";

        if (double.IsNaN(LeaveFreeGigabytes) || LeaveFreeGigabytes is < 0 or > MaxSizeGigabytes)
            return "Leave free is a number of GB, 0 for none.";

        if (double.IsNaN(SamplePercent) || SamplePercent is < 0 or > 100)
            return "Keep a sample is a percentage from 0 (none) to 100.";

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
            var most = MaxGigabytes > 0 ? string.Create(CultureInfo.CurrentCulture, $", {MaxGigabytes:0.#} GB at most") : string.Empty;
            return $"{which}, {NameOf(Format)}{kept}{most}";
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

    /// <summary>
    /// The station's result - PASS, FAIL, REVIEW (F33) - or ERROR, a node went
    /// wrong, or NONE when nothing judges.
    /// </summary>
    public static string VerdictWord(Verdict verdict, int nodeErrors) =>
        nodeErrors > 0 && verdict.Passed != false ? ResultList.ErrorName
        : verdict.Result is { } result ? result.Name
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
        ImageFileFormat.Tiff => ".tif",
        ImageFileFormat.Jpeg => ".jpg",
        ImageFileFormat.Bmp => ".bmp",
        _ => ".png",
    };

    /// <summary>"TIF", "PNG", "JPEG", "BMP" - as the format is offered.</summary>
    public static string NameOf(ImageFileFormat format) => format switch
    {
        ImageFileFormat.Tiff => "TIF",
        _ => format.ToString().ToUpperInvariant(),
    };

    /// <summary>Whether <paramref name="format"/> keeps a picture deeper than 8 bits as it is: PNG and TIF.</summary>
    public static bool KeepsDeepPictures(ImageFileFormat format) => format is ImageFileFormat.Png or ImageFileFormat.Tiff;
}
