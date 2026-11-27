using System.Globalization;
using System.Threading.Channels;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Annotations;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Runtime;

/// <summary>
/// Saves a station's pictures, and an annotation file beside each, as its
/// <see cref="ImageSaving"/> says.
/// </summary>
/// <remarks>
/// <para>
/// <b>The line never waits for the disk.</b> <see cref="Record"/> copies the
/// picture - a memory copy, a millisecond or two for a 5 MP frame - and
/// queues it; a thread of its own encodes and writes. When the disk falls
/// so far behind that <see cref="QueueLength"/> pictures are waiting, the
/// next is not saved and the skip is counted and reported, rather than
/// slowing the inspections or filling memory.
/// </para>
/// <para>
/// <b>Where:</b> a folder for each day under the station's folder, and a
/// name that says when, which inspection and how it went -
/// <c>images\Top\2026-11-11\20261111-142305-123_000042_FAIL.png</c> - with
/// <c>…_FAIL.sann</c> beside it.
/// </para>
/// <para>
/// <b>Housekeeping</b> (F20c), on a thread of its own so saving never waits
/// for it: days past those kept, and - over the station's most size, or
/// under the free space it leaves - the oldest passed pictures, then the
/// oldest failures; a sample kept aside. When saving starts, as each day
/// begins, and when a limit is passed. See <see cref="ImageHousekeeper"/>.
/// </para>
/// <para>
/// <b>A full disk stops it</b>, before Windows does: below
/// <see cref="MinimumFreeBytes"/> free, nothing more is written and the
/// station says so. The inspections carry on; saving starts again once
/// housekeeping has made room.
/// </para>
/// </remarks>
public sealed class ImageRecorder : IInspectionRecorder
{
    /// <summary>Pictures waiting to be written before the next is skipped.</summary>
    public const int QueueLength = 16;

    /// <summary>Free space below which nothing more is written: 1 GB.</summary>
    public const long MinimumFreeBytes = 1L << 30;

    private readonly Channel<Job> _queue = Channel.CreateBounded<Job>(
        new BoundedChannelOptions(QueueLength) { SingleReader = true, SingleWriter = true });

    private readonly Task _writer;
    private readonly Func<string, long> _freeSpace;
    private readonly ImageHousekeeper? _housekeeper;
    private Task _housekeeping = Task.CompletedTask;
    private int _housekeepingRunning;

    private long _saved;
    private long _skipped;
    private volatile string? _problem;
    private volatile bool _full;
    private string? _day;
    private bool _disposed;

    /// <param name="saving">What to keep, and how.</param>
    /// <param name="folder">The station's own folder: day folders go under it.</param>
    /// <param name="station">The station's name, written into each annotation file.</param>
    /// <param name="freeSpace">Free bytes on the disk holding a folder; Windows' answer unless a test gives its own.</param>
    /// <param name="results">
    /// The station's results (F33), so housekeeping knows which of the
    /// pictures' names are passes; PASS and FAIL when not given.
    /// </param>
    public ImageRecorder(ImageSaving saving, string folder, string station, Func<string, long>? freeSpace = null, ResultList? results = null)
    {
        ArgumentNullException.ThrowIfNull(saving);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        Saving = saving;
        Folder = folder;
        Station = station;
        _freeSpace = freeSpace ?? FreeSpace;

        if (saving.HasHousekeeping)
            _housekeeper = new ImageHousekeeper(saving, folder, _freeSpace) { Results = results ?? ResultList.Default };

        _writer = Task.Run(WriteAsync);

        // What was left from before: old days, and a station already over its size.
        Housekeep();
    }

    /// <summary>How often a stopped station looks again for room - made by hand, perhaps.</summary>
    internal TimeSpan FullRecheck { get; set; } = TimeSpan.FromSeconds(10);

    private DateTime _fullCheckedAt;

    /// <summary>Stopped for a full disk: whether there is room again, looked at now and then.</summary>
    private bool RoomAgain()
    {
        var now = DateTime.UtcNow;

        if (now - _fullCheckedAt < FullRecheck)
            return false;

        _fullCheckedAt = now;

        if (_freeSpace(Folder) < MinimumFreeBytes)
            return false;

        _full = false;

        if (_problem?.StartsWith("Saving pictures has stopped", StringComparison.Ordinal) == true)
            _problem = null;

        return true;
    }

    /// <summary>What the last round of housekeeping did, or null before one has finished.</summary>
    public HousekeepingResult? LastHousekeeping { get; private set; }

    /// <summary>A round of housekeeping, unless one is going: on a thread of its own.</summary>
    private void Housekeep()
    {
        if (_housekeeper is not { } housekeeper || _disposed || Interlocked.CompareExchange(ref _housekeepingRunning, 1, 0) != 0)
            return;

        _housekeeping = Task.Run(() =>
        {
            try
            {
                var result = housekeeper.Run();
                LastHousekeeping = result;

                if (result.Problems.Count > 0)
                    _problem = result.Problems[0] + (result.Problems.Count > 1 ? $" (and {result.Problems.Count - 1} more)" : string.Empty);

                // Room made: saving goes on.
                if (_full && _freeSpace(Folder) >= MinimumFreeBytes)
                {
                    _full = false;

                    if (_problem?.StartsWith("Saving pictures has stopped", StringComparison.Ordinal) == true)
                        _problem = null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _problem = $"Housekeeping in {Folder} went wrong: {ex.Message}";
            }
            finally
            {
                Interlocked.Exchange(ref _housekeepingRunning, 0);
            }
        });
    }

    /// <summary>Waits for a round of housekeeping that is going - for a test.</summary>
    internal void WaitForHousekeeping() => _housekeeping.Wait(TimeSpan.FromSeconds(30));

    /// <summary>The recorder for a station of a project; the runtime's <see cref="InspectionRecorders.Factory"/>.</summary>
    public static IInspectionRecorder? For(Project project, Station station) =>
        station.Saving.IsOn ? new ImageRecorder(station.Saving, station.Saving.FolderFor(project, station), station.Name, results: station.Results) : null;

    public ImageSaving Saving { get; }

    /// <summary>The station's folder, which day folders go under.</summary>
    public string Folder { get; }

    public string Station { get; }

    public long Saved => Interlocked.Read(ref _saved);

    /// <summary>Pictures not saved because the disk was not keeping up, or was full.</summary>
    public long Skipped => Interlocked.Read(ref _skipped);

    public string? Problem => _problem;

    public bool Wants(Verdict verdict, int nodeErrors) => Saving.Wants(verdict, nodeErrors);

    /// <summary>The most shots held for one part: a set larger than this keeps its last ones.</summary>
    internal const int MostHeld = 32;

    private readonly List<Job> _held = [];

    /// <summary>Shots held, waiting for their part's verdict.</summary>
    internal int Held
    {
        get
        {
            lock (_held)
                return _held.Count;
        }
    }

    public void Hold(InspectionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (_disposed || Saving.Policy == ImageSavePolicy.None || Picture(record) is not { } image)
            return;

        var job = new Job(
            image.Mat.Clone(),
            image.Bits,
            record.Snapshot.Time,
            record.Snapshot.Serial,
            ImageSaving.VerdictWord(record.Snapshot.Verdict, record.Snapshot.NodeErrors),
            Saving.Annotations ? Describe(record, image) : null);

        lock (_held)
        {
            _held.Add(job);

            if (_held.Count > MostHeld)
            {
                _held[0].Pixels.Dispose();
                _held.RemoveAt(0);
            }
        }
    }

    public void Release(Verdict verdict, int nodeErrors)
    {
        Job[] held;

        lock (_held)
        {
            held = [.. _held];
            _held.Clear();
        }

        if (held.Length == 0)
            return;

        if (_disposed || !Wants(verdict, nodeErrors))
        {
            foreach (var job in held)
                job.Pixels.Dispose();

            return;
        }

        var word = ImageSaving.VerdictWord(verdict, nodeErrors);

        foreach (var job in held)
        {
            var judged = job with
            {
                Verdict = word,
                Annotations = job.Annotations?.WithVerdict(verdict.Accepted, verdict.Grade?.Name, nodeErrors, verdict.Result?.Name, verdict.Result?.Code),
            };

            if (!_queue.Writer.TryWrite(judged))
            {
                judged.Pixels.Dispose();
                Interlocked.Increment(ref _skipped);
            }
        }
    }

    public void Record(InspectionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (_disposed)
            return;

        if (_full && !RoomAgain())
        {
            Interlocked.Increment(ref _skipped);
            return;
        }

        if (Picture(record) is not { } image)
        {
            _problem = "Nothing to save: the recipe has no camera, image sequence or image file, and no image viewer.";
            return;
        }

        var job = new Job(
            image.Mat.Clone(),
            image.Bits,
            record.Snapshot.Time,
            record.Snapshot.Serial,
            ImageSaving.VerdictWord(record.Snapshot.Verdict, record.Snapshot.NodeErrors),
            Saving.Annotations ? Describe(record, image) : null);

        if (!_queue.Writer.TryWrite(job))
        {
            job.Pixels.Dispose();
            var skipped = Interlocked.Increment(ref _skipped);
            _problem = $"The disk is not keeping up: {skipped} picture{(skipped == 1 ? " was" : "s were")} not saved.";
        }
    }

    /// <summary>
    /// What is kept: the picture the recipe took, or - for a recipe whose
    /// picture comes from nowhere the station knows - its first viewer's.
    /// </summary>
    private static ImageData? Picture(InspectionRecord record) =>
        record.Acquired as ImageData ?? record.Viewers.Select(viewer => viewer.Image).OfType<ImageData>().FirstOrDefault();

    /// <summary>
    /// The annotation file for a picture, all but its name: one layer for
    /// each viewer, and for each published set of shapes, holding what was
    /// found on this picture - shapes measured on a picture of another size
    /// would not sit where they belong, and are left out.
    /// </summary>
    public static AnnotationFile Describe(InspectionRecord record, ImageData image)
    {
        var layers = new List<AnnotationLayer>();

        foreach (var viewer in record.Viewers)
        {
            var shapes = viewer.Shapes
                .Where(set => set.Count > 0 && set.MatchesFrame(image.Width, image.Height))
                .SelectMany(set => set.Shapes)
                .ToArray();

            if (shapes.Length > 0)
                layers.Add(new AnnotationLayer(viewer.Name, shapes));
        }

        var results = new List<KeyValuePair<string, object?>>();

        foreach (var (name, value) in record.Snapshot.Values)
        {
            switch (value)
            {
                case ShapeSet set when set.Count > 0 && set.MatchesFrame(image.Width, image.Height) &&
                                       layers.All(layer => layer.Name != name):
                    layers.Add(new AnnotationLayer(name, set.Shapes));
                    break;

                case double or float or int or long or bool or string:
                    results.Add(new(name, value));
                    break;

                case Grade grade:
                    results.Add(new(name, grade.Name));
                    break;
            }
        }

        var snapshot = record.Snapshot;

        return new AnnotationFile
        {
            Width = image.Width,
            Height = image.Height,
            Bits = image.Bits,
            Station = snapshot.Station,
            Recipe = record.Recipe,
            Serial = snapshot.Serial,
            Time = snapshot.Time,
            Passed = snapshot.Verdict.Accepted,
            Result = snapshot.Verdict.Result?.Name,
            Code = snapshot.Verdict.Result?.Code,
            Grade = snapshot.Verdict.Grade?.Name,
            NodeErrors = snapshot.NodeErrors,
            Results = results,
            Part = snapshot.Part?.ToArray() ?? [],
            Layers = layers,
        };
    }

    // ---- writing, on a thread of its own ---------------------------------------

    private async Task WriteAsync()
    {
        await foreach (var job in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Write(job);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Emgu.CV.Util.CvException or ArgumentException)
            {
                _problem = $"A picture could not be saved in {Folder}: {ex.Message}";
            }
            finally
            {
                job.Pixels.Dispose();
            }
        }
    }

    private void Write(Job job)
    {
        if (_full)
        {
            Interlocked.Increment(ref _skipped);
            return;
        }

        var day = ImageSaving.DayFolder(job.Time);
        var folder = Path.Combine(Folder, day);

        Directory.CreateDirectory(folder);

        // A new day: the days kept move on.
        if (day != _day)
        {
            var first = _day is null;
            _day = day;

            if (!first)
                Housekeep();
        }

        var free = _freeSpace(folder);

        if (free < MinimumFreeBytes)
        {
            _full = true;
            Interlocked.Increment(ref _skipped);
            _problem = $"Saving pictures has stopped: the disk holding {Folder} has less than 1 GB free.";
            Housekeep();
            return;
        }

        var path = FreePath(folder, ImageSaving.FileStem(job.Time, job.Serial, job.Verdict), ExtensionFor(job.Pixels.Depth));

        Save(job.Pixels, path);

        if (job.Annotations is { } annotations)
        {
            new AnnotationFile
            {
                Image = Path.GetFileName(path),
                Width = annotations.Width,
                Height = annotations.Height,
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
            }.Save(AnnotationFile.PathFor(path));
        }

        Interlocked.Increment(ref _saved);

        if (_housekeeper is { } housekeeper)
        {
            housekeeper.Added(Length(path) + Length(AnnotationFile.PathFor(path)));

            if (housekeeper.IsDue(free))
                Housekeep();
        }

        // A write that went through puts right one that did not.
        if (_problem is { } problem && problem.StartsWith("A picture could not be saved", StringComparison.Ordinal))
            _problem = null;
    }

    /// <summary>
    /// The extension to write: the one chosen, unless the picture is deeper
    /// than 8 bits and the format cannot hold it - then PNG, which can, so a
    /// 12-bit frame is never quietly cut to 8. TIF and PNG hold it as it is.
    /// </summary>
    private string ExtensionFor(DepthType depth) =>
        depth == DepthType.Cv8U || ImageSaving.KeepsDeepPictures(Saving.Format) ? ImageSaving.ExtensionOf(Saving.Format) : ".png";

    private static void Save(Mat pixels, string path)
    {
        // OpenCV picks the format by the extension, so the temporary file keeps it.
        var temporary = Path.ChangeExtension(path, ".saving" + Path.GetExtension(path));

        try
        {
            // LZW for TIF (libtiff's 5): lossless, and asked for rather than left to OpenCV's default.
            var written = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" => CvInvoke.Imwrite(temporary, pixels, new KeyValuePair<ImwriteFlags, int>(ImwriteFlags.JpegQuality, 95)),
                ".tif" => CvInvoke.Imwrite(temporary, pixels, new KeyValuePair<ImwriteFlags, int>(ImwriteFlags.TiffCompression, 5)),
                _ => CvInvoke.Imwrite(temporary, pixels),
            };

            if (!written)
                throw new IOException($"OpenCV could not write {Path.GetFileName(path)}.");

            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    /// <summary>The name, or with "_2" added if two pictures came in the same millisecond.</summary>
    private static string FreePath(string folder, string stem, string extension)
    {
        var path = Path.Combine(folder, stem + extension);

        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{stem}_{i.ToString(CultureInfo.InvariantCulture)}{extension}");

        return path;
    }

    private static long Length(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long FreeSpace(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // A network share may not say; assume there is room.
            return long.MaxValue;
        }
    }

    /// <summary>Writes what is queued - up to half a minute's worth - and stops.</summary>
    public void Dispose()
    {
        lock (_held)
        {
            foreach (var job in _held)
                job.Pixels.Dispose();

            _held.Clear();
        }

        if (_disposed)
            return;

        _disposed = true;
        _queue.Writer.TryComplete();

        if (!_writer.Wait(TimeSpan.FromSeconds(30)))
            _problem = "Saving pictures did not finish in time; some were not written.";

        // A round going is let finish: a picture half moved to Kept is not left behind.
        _housekeeping.Wait(TimeSpan.FromSeconds(30));

        while (_queue.Reader.TryRead(out var left))
            left.Pixels.Dispose();
    }

    private sealed record Job(Mat Pixels, int Bits, DateTime Time, long Serial, string Verdict, AnnotationFile? Annotations);
}
