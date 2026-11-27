using System.Globalization;
using System.Text.RegularExpressions;
using Sirius.Core.Annotations;
using Sirius.Core.Projects;

namespace Sirius.Vision.Runtime;

/// <summary>What a round of housekeeping did.</summary>
/// <param name="Deleted">Pictures deleted - each with its <c>.sann</c> - and whole days counted by their pictures.</param>
/// <param name="Sampled">Pictures moved to the Kept folder instead.</param>
/// <param name="Freed">Bytes deleted.</param>
/// <param name="Problems">What could not be deleted or moved, and why.</param>
public sealed record HousekeepingResult(int Deleted, int Sampled, long Freed, IReadOnlyList<string> Problems)
{
    public static HousekeepingResult Nothing { get; } = new(0, 0, 0, []);
}

/// <summary>
/// Keeps a station's saved pictures within its limits (F20c): days older
/// than it keeps, then - over its most size, or under the free space it
/// leaves on the drive - the oldest passed pictures, then the oldest
/// failures. A sample of what goes is moved to the station's <c>Kept</c>
/// folder instead, for records, and never touched again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only its own.</b> Within a day folder it deletes only pictures named
/// as a station names them - when, which inspection, how it went - and
/// their <c>.sann</c>; a whole day past the days kept goes with whatever
/// else is in it, as it always has. Folders that are not days - <c>Kept</c>
/// among them - are left alone.
/// </para>
/// <para>
/// <b>Not after every picture.</b> Over a limit, it clears to 90% of the
/// most size, and to 1 GB more than the free space it leaves, so the next
/// round is a while off.
/// </para>
/// <para>
/// <b>The sample is by inspection number</b> - 1% keeps those numbered a
/// multiple of 100 - so it is spread evenly through a day, passed and
/// failed alike, and the same pictures are kept whether a day goes whole
/// or a picture at a time.
/// </para>
/// </remarks>
public sealed class ImageHousekeeper
{
    /// <summary>
    /// What a station's picture is called: "20261122-142248-000_000042_FAIL",
    /// perhaps with "_2" - or with any result of the station's, "_REVIEW" (F33).
    /// </summary>
    private static readonly Regex StationName = new(
        @"^\d{8}-\d{6}-\d{3}_(?<serial>\d+)_(?<verdict>[A-Za-z0-9-]+)(_\d+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Pictures this new are left: the one being written may not have its .sann yet.</summary>
    private static readonly TimeSpan Settling = TimeSpan.FromMinutes(1);

    private readonly Func<string, long> _freeSpace;
    private long _size = -1;

    /// <param name="saving">The station's limits.</param>
    /// <param name="folder">The station's folder, its day folders under it.</param>
    /// <param name="freeSpace">Free bytes on the drive holding a folder.</param>
    public ImageHousekeeper(ImageSaving saving, string folder, Func<string, long> freeSpace)
    {
        Saving = saving ?? throw new ArgumentNullException(nameof(saving));
        Folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _freeSpace = freeSpace ?? throw new ArgumentNullException(nameof(freeSpace));
    }

    public ImageSaving Saving { get; }

    /// <summary>
    /// The station's results (F33): a picture named by one that counts as a
    /// pass goes first, as PASS does; any other result is kept as long as a
    /// failure.
    /// </summary>
    public ResultList Results { get; init; } = ResultList.Default;

    public string Folder { get; }

    /// <summary>Where the sample goes: the station's <c>Kept</c> folder.</summary>
    public string KeptFolder => Path.Combine(Folder, ImageSaving.KeptFolder);

    /// <summary>Freed beyond the free space left, so the next round is a while off: 1 GB; a test's own.</summary>
    public long FreeMargin { get; init; } = 1L << 30;

    /// <summary>The clock, for a test to move.</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;

    /// <summary>Bytes in the station's day folders - the Kept folder not among them; -1 until measured.</summary>
    public long Size => Interlocked.Read(ref _size);

    /// <summary>A picture written: its bytes, picture and .sann, added to the size.</summary>
    public void Added(long bytes)
    {
        if (Interlocked.Read(ref _size) >= 0)
            Interlocked.Add(ref _size, bytes);
    }

    /// <summary>Whether a limit is passed: the size known and over the most, or the drive under what is left free.</summary>
    public bool IsDue(long free) =>
        (Saving.MaxGigabytes > 0 && Size > ImageSaving.Bytes(Saving.MaxGigabytes)) ||
        (Saving.LeaveFreeGigabytes > 0 && free < ImageSaving.Bytes(Saving.LeaveFreeGigabytes));

    /// <summary>
    /// A round: days past those kept, then pictures past the most size or
    /// the free space left - a sample of each moved to Kept instead.
    /// </summary>
    public HousekeepingResult Run()
    {
        var problems = new List<string>();
        var deleted = 0;
        var sampled = 0;
        long freed = 0;

        if (!Directory.Exists(Folder))
            return HousekeepingResult.Nothing;

        var now = Now();

        // ---- days past those kept ----

        if (Saving.KeepDays > 0)
        {
            var oldest = now.Date.AddDays(1 - Saving.KeepDays);

            foreach (var (day, folder) in Days().Where(day => day.Day < oldest))
            {
                var pictures = PicturesIn(folder, day).ToArray();

                foreach (var picture in pictures.Where(p => Saving.Samples(p.Serial)))
                {
                    if (Keep(picture, problems))
                        sampled++;
                }

                var bytes = BytesIn(folder);

                try
                {
                    Directory.Delete(folder, recursive: true);
                    deleted += pictures.Count(p => !Saving.Samples(p.Serial));
                    freed += bytes;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Open in a viewer, perhaps; tried again next round.
                    problems.Add($"An old day's pictures could not be deleted ({Path.GetFileName(folder)}): {ex.Message}");
                }
            }
        }

        // ---- size, and the free space left ----

        if (Saving.MaxGigabytes > 0 || Saving.LeaveFreeGigabytes > 0)
        {
            var size = Measure();
            var free = _freeSpace(Folder);

            var sizeNeed = Saving.MaxGigabytes > 0 && size > ImageSaving.Bytes(Saving.MaxGigabytes)
                ? size - (long)(ImageSaving.Bytes(Saving.MaxGigabytes) * 0.9)
                : 0;
            var freeNeed = Saving.LeaveFreeGigabytes > 0 && free < ImageSaving.Bytes(Saving.LeaveFreeGigabytes)
                ? ImageSaving.Bytes(Saving.LeaveFreeGigabytes) + FreeMargin - free
                : 0;

            if (sizeNeed > 0 || freeNeed > 0)
            {
                // Passed first, oldest first; then failures, oldest first.
                var order = Days()
                    .SelectMany(day => PicturesIn(day.Folder, day.Day))
                    .Where(picture => now - picture.Written > Settling)
                    .OrderBy(picture => picture.Failed)
                    .ThenBy(picture => picture.Day)
                    .ThenBy(picture => picture.Stem, StringComparer.Ordinal);

                foreach (var picture in order)
                {
                    if (sizeNeed <= 0 && freeNeed <= 0)
                        break;

                    if (Saving.Samples(picture.Serial))
                    {
                        // Moved, not freed: out of the station's size, still on the drive.
                        if (Keep(picture, problems))
                        {
                            sampled++;
                            sizeNeed -= picture.Bytes;
                        }

                        continue;
                    }

                    if (Delete(picture, problems))
                    {
                        deleted++;
                        freed += picture.Bytes;
                        sizeNeed -= picture.Bytes;
                        freeNeed -= picture.Bytes;
                    }
                }

                // Days emptied, but today, go.
                foreach (var (day, folder) in Days().Where(day => day.Day < now.Date))
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(folder).Any())
                            Directory.Delete(folder);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Left for the next round.
                    }
                }
            }
        }

        // What is left, counted afresh: cheaper to trust than to keep exact through every move.
        if (Saving.MaxGigabytes > 0 || Saving.LeaveFreeGigabytes > 0 || Interlocked.Read(ref _size) >= 0)
            Interlocked.Exchange(ref _size, Days().Sum(day => BytesIn(day.Folder)));

        return new HousekeepingResult(deleted, sampled, freed, problems);
    }

    /// <summary>The bytes in the station's day folders, counted; kept as <see cref="Size"/>.</summary>
    public long Measure()
    {
        var size = Days().Sum(day => BytesIn(day.Folder));
        Interlocked.Exchange(ref _size, size);
        return size;
    }

    // ---- the station's folder ----------------------------------------------------------

    private sealed record Picture(string Folder, DateTime Day, string Stem, long Serial, bool Failed, IReadOnlyList<string> Files, long Bytes, DateTime Written);

    /// <summary>The day folders, oldest first; folders that are not days - Kept among them - left out.</summary>
    private IEnumerable<(DateTime Day, string Folder)> Days()
    {
        if (!Directory.Exists(Folder))
            return [];

        return Directory.EnumerateDirectories(Folder)
            .Select(folder => (Day: ImageSaving.DayOf(Path.GetFileName(folder)), Folder: folder))
            .Where(entry => entry.Day is not null)
            .Select(entry => (entry.Day!.Value, entry.Folder))
            .OrderBy(entry => entry.Value)
            .ToArray();
    }

    /// <summary>Whether a picture named with <paramref name="word"/> passed - or was not judged, NONE - and so goes first.</summary>
    private bool Passes(string word) =>
        word is "PASS" or "NONE" || Results.Find(word) is { Accepts: true };

    /// <summary>A day's pictures named as a station names them, each with its .sann; files half written left out.</summary>
    private IEnumerable<Picture> PicturesIn(string folder, DateTime day)
    {
        FileInfo[] files;

        try
        {
            files = new DirectoryInfo(folder).GetFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var group in files.GroupBy(file => Path.GetFileNameWithoutExtension(file.Name), StringComparer.OrdinalIgnoreCase))
        {
            if (StationName.Match(group.Key) is not { Success: true } match)
                continue;

            // A picture and its .sann, nothing else - a ".saving" file being written has another stem.
            var pictures = group.Where(file => !file.Extension.Equals(AnnotationFile.Extension, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (pictures.Length == 0)
                continue;

            yield return new Picture(
                folder,
                day,
                group.Key,
                long.Parse(match.Groups["serial"].Value, CultureInfo.InvariantCulture),
                !Passes(match.Groups["verdict"].Value),
                group.Select(file => file.FullName).ToArray(),
                group.Sum(file => file.Length),
                group.Max(file => file.LastWriteTime));
        }
    }

    private static long BytesIn(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Moves a picture and its .sann to Kept, under its day.</summary>
    private bool Keep(Picture picture, List<string> problems)
    {
        try
        {
            var into = Path.Combine(KeptFolder, ImageSaving.DayFolder(picture.Day));
            Directory.CreateDirectory(into);

            foreach (var file in picture.Files)
                File.Move(file, Path.Combine(into, Path.GetFileName(file)), overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{picture.Stem} could not be moved to {ImageSaving.KeptFolder}: {ex.Message}");
            return false;
        }
    }

    private static bool Delete(Picture picture, List<string> problems)
    {
        try
        {
            foreach (var file in picture.Files)
                File.Delete(file);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{picture.Stem} could not be deleted: {ex.Message}");
            return false;
        }
    }
}
