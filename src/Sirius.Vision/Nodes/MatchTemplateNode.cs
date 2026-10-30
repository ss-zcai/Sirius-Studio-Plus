using System.Drawing;
using System.Globalization;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>
/// Finds where a template image appears in the input, and how well it
/// matches there.
/// </summary>
/// <remarks>
/// <para>
/// Normalised cross-correlation: the template slid over every position,
/// scored from -1 to 1 by how alike the two are once each is brought to
/// the same mean and contrast - so a match survives the lighting being a
/// little brighter or darker than when the template was taken. Each match
/// is the best remaining score above Min Score; around it the scores are
/// cleared, so one feature is not found twice a pixel apart. Its position
/// is refined to a fraction of a pixel by fitting a parabola through the
/// scores either side of the peak.
/// </para>
/// <para>
/// The template is an image wired in: an Image File of a good part, with
/// the feature cut out by Crop or Region of Interest. Position only - a
/// part turned by more than a few degrees scores poorly.
/// </para>
/// </remarks>
[VisionNode(
    "Match Template",
    TypeKey = "analysis.matchTemplate",
    Category = NodeCategory.Analysis,
    Icon = "ph_match_template",
    Description = "Finds where a template image appears in the input, scored 0 to 1. Wire the template from an " +
                  "Image File of a good part through Crop. Position only, not rotation.")]
public sealed class MatchTemplateNode : VisionNode
{
    private const uint Found = 0xFF22C55E;

    private double _minimumScore = 0.8;
    private int _maximumCount = 1;
    private double _separation = 0.5;
    private Rectangle _searchRegion = Rectangle.Empty;

    public MatchTemplateNode()
    {
        ImageIn = AddInput<ImageData>("In");
        TemplateIn = AddInput<ImageData>("Template");

        MatchesOut = AddOutput<ShapeSet>("Matches");
        CountOut = AddOutput<int>("Count");
        ScoreOut = AddOutput<double>("Best Score");
        PositionOut = AddOutput<Point2>("Position");
    }

    public InputPort ImageIn { get; }

    /// <summary>What to look for: an image of the feature, cut from a good part.</summary>
    public InputPort TemplateIn { get; }

    /// <summary>One rectangle per match, the template's size, with its score.</summary>
    public OutputPort MatchesOut { get; }

    public OutputPort CountOut { get; }

    /// <summary>The best match's score, 0 when there is none above Min Score.</summary>
    public OutputPort ScoreOut { get; }

    /// <summary>The best match's centre.</summary>
    public OutputPort PositionOut { get; }

    [Param("Min Score", Min = 0, Max = 1, Step = 0.01, Connectable = true,
        Description = "How alike a place must be to count as a match: 1 is identical, 0.8 a usual starting point.")]
    public double MinimumScore
    {
        get => _minimumScore;
        set => SetParam(ref _minimumScore, Math.Clamp(value, 0, 1));
    }

    [Param("Max Count", Min = 1, Max = 500, Step = 1,
        Description = "The most matches to report, best first.")]
    public int MaximumCount
    {
        get => _maximumCount;
        set => SetParam(ref _maximumCount, Math.Clamp(value, 1, 500));
    }

    [Param("Separation", Min = 0.05, Max = 1, Step = 0.05,
        Description = "How far apart two matches must be, as a fraction of the template's size.")]
    public double Separation
    {
        get => _separation;
        set => SetParam(ref _separation, Math.Clamp(value, 0.05, 1));
    }

    [Param("Search Region", Editor = ParamEditor.Region,
        Description = "Where to look. Left empty, the whole image. A smaller region is faster and cannot be " +
                      "fooled by something alike elsewhere.")]
    public Rectangle SearchRegion
    {
        get => _searchRegion;
        set => SetParam(ref _searchRegion, value);
    }

    /// <summary>One place the template was found.</summary>
    public readonly record struct Match(Point2 TopLeft, double Score);

    protected override void OnEvaluate(EvalContext context)
    {
        var image = context.Get<ImageData>(ImageIn)!;
        var template = context.Get<ImageData>(TemplateIn)!;

        var region = _searchRegion.Width > 0 && _searchRegion.Height > 0
            ? Rectangle.Intersect(_searchRegion, new Rectangle(0, 0, image.Width, image.Height))
            : new Rectangle(0, 0, image.Width, image.Height);

        if (template.Width > region.Width || template.Height > region.Height)
        {
            throw new InvalidOperationException(
                $"The template ({template.Width} x {template.Height}) is larger than where it is looked for " +
                $"({region.Width} x {region.Height}).");
        }

        using var grey = Grey(image);
        using var pattern = Grey(template);
        using var searched = new Mat(grey, region);

        var matches = Find(
            searched,
            pattern,
            context.Param(nameof(MinimumScore), _minimumScore),
            _maximumCount,
            _separation,
            context.CancellationToken);

        var shapes = matches
            .Select(match => (Shape)Outline(
                new Point2(match.TopLeft.X + region.X, match.TopLeft.Y + region.Y), template, match.Score))
            .ToArray();

        if (!HasDetail(pattern))
            context.Warn("The template is one grey level, so there is nothing in it to find. Cut it from a part of the image with some detail.");
        else if (matches.Count == 0)
            context.Warn($"Nothing matched above {_minimumScore.ToString("0.00", CultureInfo.InvariantCulture)}.");

        context.Set(MatchesOut, new ShapeSet(shapes, image.Width, image.Height));
        context.Set(CountOut, matches.Count);
        context.Set(ScoreOut, matches.Count == 0 ? 0d : matches[0].Score);
        context.Set(
            PositionOut,
            shapes.Length == 0 ? default : ShapeGeometry.Centre(shapes[0]));
    }

    /// <summary>
    /// The best places <paramref name="template"/> matches in
    /// <paramref name="image"/>, best first, as top-left corners.
    /// </summary>
    public static IReadOnlyList<Match> Find(
        Mat image, Mat template, double minimumScore, int maximumCount, double separation,
        CancellationToken cancellationToken = default)
    {
        // A template of one grey level has nothing to match on, and OpenCV
        // scores it as a perfect match everywhere rather than nowhere.
        if (!HasDetail(template))
            return [];

        using var scores = new Mat();
        CvInvoke.MatchTemplate(image, template, scores, TemplateMatchingType.CcoeffNormed);

        // A flat patch of the image scores not-a-number, which would win
        // every comparison it is not part of.
        CvInvoke.PatchNaNs(scores, 0);

        var found = new List<Match>();
        var clearX = Math.Max(1, (int)Math.Round(template.Width * separation));
        var clearY = Math.Max(1, (int)Math.Round(template.Height * separation));

        while (found.Count < maximumCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double min = 0, max = 0;
            Point minAt = default, maxAt = default;
            CvInvoke.MinMaxLoc(scores, ref min, ref max, ref minAt, ref maxAt);

            if (max < minimumScore)
                break;

            found.Add(new Match(Refine(scores, maxAt), Math.Min(1, max)));

            // Cleared around it, so the next best is somewhere else.
            CvInvoke.Rectangle(
                scores,
                Rectangle.FromLTRB(maxAt.X - clearX, maxAt.Y - clearY, maxAt.X + clearX, maxAt.Y + clearY),
                new MCvScalar(-1),
                -1);
        }

        return found;
    }

    /// <summary>Whether a template has any variation in it at all.</summary>
    private static bool HasDetail(Mat template)
    {
        var mean = new MCvScalar();
        var spread = new MCvScalar();
        CvInvoke.MeanStdDev(template, ref mean, ref spread);
        return spread.V0 > 1e-3;
    }

    /// <summary>
    /// The peak to a fraction of a pixel: a parabola through the score at
    /// the peak and either side of it, along each axis.
    /// </summary>
    private static Point2 Refine(Mat scores, Point peak)
    {
        var data = (float[,])scores.GetData();
        var rows = data.GetLength(0);
        var columns = data.GetLength(1);

        static double Offset(double before, double at, double after)
        {
            var curve = before - (2 * at) + after;
            return Math.Abs(curve) < 1e-12 ? 0 : Math.Clamp((before - after) / (2 * curve), -0.5, 0.5);
        }

        var x = peak.X > 0 && peak.X < columns - 1
            ? Offset(data[peak.Y, peak.X - 1], data[peak.Y, peak.X], data[peak.Y, peak.X + 1])
            : 0;

        var y = peak.Y > 0 && peak.Y < rows - 1
            ? Offset(data[peak.Y - 1, peak.X], data[peak.Y, peak.X], data[peak.Y + 1, peak.X])
            : 0;

        return new Point2(peak.X + x, peak.Y + y);
    }

    private static RectShape Outline(Point2 topLeft, ImageData template, double score) =>
        new(new Bounds(topLeft.X, topLeft.Y, template.Width, template.Height))
        {
            Colour = Found,
            StrokeWidth = 2,
            Label = score.ToString("0.000", CultureInfo.InvariantCulture),
            Metrics = new Dictionary<string, double>
            {
                [ShapeMetric.CentroidX] = topLeft.X + (template.Width / 2.0),
                [ShapeMetric.CentroidY] = topLeft.Y + (template.Height / 2.0),
                [ShapeMetric.Score] = score,
            },
        };

    /// <summary>One channel, 8-bit or float, as matching needs: a colour image as its brightness.</summary>
    private static Mat Grey(ImageData image)
    {
        var grey = new Mat();

        switch (image.Channels)
        {
            case 3:
                CvInvoke.CvtColor(image.Mat, grey, ColorConversion.Bgr2Gray);
                break;
            case 4:
                CvInvoke.CvtColor(image.Mat, grey, ColorConversion.Bgra2Gray);
                break;
            default:
                image.Mat.CopyTo(grey);
                break;
        }

        // MatchTemplate takes 8-bit or 32-bit float; a 16-bit camera image
        // is neither.
        if (grey.Depth is not (DepthType.Cv8U or DepthType.Cv32F))
            grey.ConvertTo(grey, DepthType.Cv32F);

        return grey;
    }
}
