using System.IO;
using System.Windows;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Studio.App.Controls;
using Sirius.Studio.App.Imaging;

namespace Sirius.IV;

/// <summary>
/// Rectangles cut from the pictures in the list: each picture's own, drawn
/// on it - or the same ones, drawn on one, from every picture, turning a
/// day's pictures into a set of ROI images for training a model.
/// </summary>
/// <remarks>
/// Each picture's crops are named after it and numbered as its rectangles
/// are - <c>&lt;picture&gt;-1.tif</c>, <c>-2</c> - into one folder, in the
/// picture's own format. Read from the file each time, not from what a
/// viewer shows, so a 12-bit TIF gives 12-bit TIF crops.
/// </remarks>
internal static class RoiBatch
{
    /// <summary>A rectangle as it is kept: top left, top right, bottom right.</summary>
    public sealed record Roi(Point TopLeft, Point TopRight, Point BottomRight);

    /// <summary>What a run did.</summary>
    public sealed record Result(int Pictures, int Saved, IReadOnlyList<string> Problems, bool Cancelled = false);

    /// <summary>One file to write: a picture's crop by one rectangle.</summary>
    public sealed record Target(ImageFileItem Picture, int Number, string Path, Roi Roi);

    /// <summary>A picture and the rectangles to cut from it.</summary>
    public sealed record Cut(ImageFileItem Picture, IReadOnlyList<Roi> Rois);

    /// <summary>The rectangles among a picture's measurements, in the order drawn.</summary>
    public static IReadOnlyList<Roi> From(IEnumerable<MeasurementRecord> measurements) =>
        measurements
            .Where(m => m.Tool == nameof(ViewerTool.Rectangle) && m.Points.Count >= 3)
            .Select(m => new Roi(
                new Point(m.Points[0].X, m.Points[0].Y),
                new Point(m.Points[1].X, m.Points[1].Y),
                new Point(m.Points[2].X, m.Points[2].Y)))
            .ToArray();

    /// <summary>The same rectangles from every picture.</summary>
    public static IReadOnlyList<Target> Targets(IReadOnlyList<ImageFileItem> pictures, IReadOnlyList<Roi> rois, string folder) =>
        Targets(pictures.Select(picture => new Cut(picture, rois)).ToArray(), folder);

    /// <summary>
    /// Every file a run writes: each picture's rectangles, numbered from 1 on
    /// it. Named after the picture, or - where two pictures of one name are
    /// in the list, from two cameras' folders - after its path from the
    /// folder open, so neither overwrites the other. A picture with no
    /// rectangles writes nothing.
    /// </summary>
    public static IReadOnlyList<Target> Targets(IReadOnlyList<Cut> cuts, string folder)
    {
        var alike = cuts
            .Select(cut => cut.Picture)
            .GroupBy(p => Path.GetFileNameWithoutExtension(p.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var targets = new List<Target>();

        foreach (var (picture, rois) in cuts)
        {
            var stem = Path.GetFileNameWithoutExtension(picture.Name);

            if (alike.Contains(stem))
            {
                stem = Path.ChangeExtension(picture.RelativePath, null)
                    .Replace(Path.DirectorySeparatorChar, '_')
                    .Replace(Path.AltDirectorySeparatorChar, '_');
            }

            var extension = ExtensionFor(picture.Path);

            for (var number = 1; number <= rois.Count; number++)
                targets.Add(new Target(picture, number, Path.Combine(folder, $"{stem}-{number}{extension}"), rois[number - 1]));
        }

        return targets;
    }

    /// <summary>
    /// A crop's extension: its picture's own, so a TIF's crops are TIFs and a
    /// PNG's PNGs; ".tiff" and ".jpeg" written as ".tif" and ".jpg"; PNG for
    /// a kind it cannot be written as.
    /// </summary>
    internal static string ExtensionFor(string picture) => Path.GetExtension(picture).ToLowerInvariant() switch
    {
        ".tif" or ".tiff" => ".tif",
        ".jpg" or ".jpeg" => ".jpg",
        ".bmp" => ".bmp",
        _ => ".png",
    };

    /// <summary>
    /// Cuts and writes every target, a picture at a time, saying after each
    /// how many pictures are done. A picture that cannot be read or written
    /// is noted and passed over; the rest go on.
    /// </summary>
    public static Result Run(
        IReadOnlyList<Target> targets,
        IProgress<int>? progress = null,
        CancellationToken cancel = default)
    {
        var problems = new List<string>();
        var saved = 0;
        var done = 0;

        foreach (var picture in targets.GroupBy(t => t.Picture))
        {
            if (cancel.IsCancellationRequested)
                return new Result(done, saved, problems, Cancelled: true);

            try
            {
                using var source = CvInvoke.Imread(picture.Key.Path, ImreadModes.Unchanged);

                if (source.IsEmpty)
                {
                    problems.Add($"{picture.Key.Name}: not a picture that can be read.");
                }
                else
                {
                    foreach (var target in picture)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target.Path)!);

                        var roi = target.Roi;
                        using var crop = RoiCrop.Cut(source, roi.TopLeft, roi.TopRight, roi.BottomRight);
                        RoiCrop.Save(crop, target.Path, BitsOf(source));
                        saved++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Emgu.CV.Util.CvException
                                           or InvalidOperationException or ArgumentException)
            {
                problems.Add($"{picture.Key.Name}: {ex.Message}");
            }

            done++;
            progress?.Report(done);
        }

        return new Result(done, saved, problems);
    }

    /// <summary>The bits a PNG cannot hold are brought down by: a float picture's, mostly.</summary>
    private static int BitsOf(Mat source) => source.Depth == DepthType.Cv16U ? 16 : 8;
}
