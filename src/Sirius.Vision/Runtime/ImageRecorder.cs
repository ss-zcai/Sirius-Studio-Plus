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
/// <c>…_FAIL.sann</c> beside it. Day folders older than
/// <see cref="ImageSaving.KeepDays"/> are deleted when saving starts and each
/// time the day changes.
/// </para>
/// <para>
/// <b>A full disk stops it</b>, before Windows does: below
/// <see cref="MinimumFreeBytes"/> free, nothing more is written and the
/// station says so. The inspections carry on.
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
    public ImageRecorder(ImageSaving saving, string folder, string station, Func<string, long>? freeSpace = null)
    {
        ArgumentNullException.ThrowIfNull(saving);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        Saving = saving;
        Folder = folder;
        Station = station;
        _freeSpace = freeSpace ?? FreeSpace;
        _writer = Task.Run(WriteAsync);
    }

    /// <summary>The recorder for a station of a project; the runtime's <see cref="InspectionRecorders.Factory"/>.</summary>
    public static IInspectionRecorder? For(Project project, Station station) =>
        station.Saving.IsOn ? new ImageRecorder(station.Saving, station.Saving.FolderFor(project, station), station.Name) : null;

    public ImageSaving Saving { get; }

    /// <summary>The station's folder, which day folders go under.</summary>
    public string Folder { get; }

    public string Station { get; }

    public long Saved => Interlocked.Read(ref _saved);

    /// <summary>Pictures not saved because the disk was not keeping up, or was full.</summary>
    public long Skipped => Interlocked.Read(ref _skipped);

    public string? Problem => _problem;

    public bool Wants(Verdict verdict, int nodeErrors) => Saving.Wants(verdict, nodeErrors);

    public void Record(InspectionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (_disposed)
            return;

        if (_full)
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
    internal static AnnotationFile Describe(InspectionRecord record, ImageData image)
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
            Passed = snapshot.Verdict.Passed,
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

        if (day != _day)
        {
            _day = day;
            Directory.CreateDirectory(folder);
            ForgetOldDays(job.Time);
        }
        else
        {
            Directory.CreateDirectory(folder);
        }

        if (_freeSpace(folder) < MinimumFreeBytes)
        {
            _full = true;
            Interlocked.Increment(ref _skipped);
            _problem = $"Saving pictures has stopped: the disk holding {Folder} has less than 1 GB free.";
            return;
        }

        var path = FreePath(folder, ImageSaving.FileStem(job.Time, job.Serial, job.Verdict), ExtensionFor(job.Pixels.Depth));

        Save(job.Pixels, path, Saving.Format);

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

        // A write that went through puts right one that did not.
        if (_problem is { } problem && problem.StartsWith("A picture could not be saved", StringComparison.Ordinal))
            _problem = null;
    }

    /// <summary>
    /// The extension to write: the one chosen, unless the picture is deeper
    /// than 8 bits and the format cannot hold it - then PNG, which can, so a
    /// 12-bit frame is never quietly cut to 8.
    /// </summary>
    private string ExtensionFor(DepthType depth) =>
        depth == DepthType.Cv8U ? ImageSaving.ExtensionOf(Saving.Format) : ".png";

    private static void Save(Mat pixels, string path, ImageFileFormat format)
    {
        // OpenCV picks the format by the extension, so the temporary file keeps it.
        var temporary = Path.ChangeExtension(path, ".saving" + Path.GetExtension(path));

        try
        {
            var written = format == ImageFileFormat.Jpeg && path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                ? CvInvoke.Imwrite(temporary, pixels, new KeyValuePair<ImwriteFlags, int>(ImwriteFlags.JpegQuality, 95))
                : CvInvoke.Imwrite(temporary, pixels);

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

    /// <summary>Deletes the day folders older than the days kept - today being the first of them.</summary>
    private void ForgetOldDays(DateTime now)
    {
        if (Saving.KeepDays <= 0 || !Directory.Exists(Folder))
            return;

        var oldest = now.Date.AddDays(1 - Saving.KeepDays);

        foreach (var folder in Directory.EnumerateDirectories(Folder))
        {
            if (ImageSaving.DayOf(Path.GetFileName(folder)) is { } day && day < oldest)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Open in a viewer, perhaps; tried again tomorrow.
                    _problem = $"An old day's pictures could not be deleted ({Path.GetFileName(folder)}): {ex.Message}";
                }
            }
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
        if (_disposed)
            return;

        _disposed = true;
        _queue.Writer.TryComplete();

        if (!_writer.Wait(TimeSpan.FromSeconds(30)))
            _problem = "Saving pictures did not finish in time; some were not written.";

        while (_queue.Reader.TryRead(out var left))
            left.Pixels.Dispose();
    }

    private sealed record Job(Mat Pixels, int Bits, DateTime Time, long Serial, string Verdict, AnnotationFile? Annotations);
}
