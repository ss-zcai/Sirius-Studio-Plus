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
/// The template is taught: draw Teach Region round the feature on a good
/// part and press Teach, and the feature is kept in the recipe. Or wire
/// one into Template - an Image File through Crop - which is used instead
/// while it is wired. Position only - a part turned by more than a few
/// degrees scores poorly.
/// </para>
/// </remarks>
[VisionNode(
    "Match Template",
    TypeKey = "analysis.matchTemplate",
    Category = NodeCategory.Analysis,
    Icon = "ph_match_template",
    Description = "Finds where a template appears in the input, scored 0 to 1. Draw Teach Region round the " +
                  "feature on a good part and press Teach, or wire a template in. Position only, not rotation.")]
public sealed class MatchTemplateNode : VisionNode, INodeActions
{
    private readonly object _gate = new();
    private Rectangle _teachRegion = new(100, 100, 80, 60);
    private string _taught = string.Empty;
    private Mat? _taughtImage;
    private Mat? _lastCut;
    private const uint Found = 0xFF22C55E;

    private double _minimumScore = 0.8;
    private int _maximumCount = 1;
    private double _separation = 0.5;
    private Rectangle _searchRegion = Rectangle.Empty;

    public MatchTemplateNode()
    {
        ImageIn = AddInput<ImageData>("In");
        TemplateIn = AddInput<ImageData>("Template", isRequired: false);

        MatchesOut = AddOutput<ShapeSet>("Matches");
        CountOut = AddOutput<int>("Count");
        ScoreOut = AddOutput<double>("Best Score");
        PositionOut = AddOutput<Point2>("Position");
        TemplateOut = AddOutput<ImageData>("Template");
    }

    public InputPort ImageIn { get; }

    /// <summary>
    /// What to look for, when it comes from elsewhere: an image of the
    /// feature, cut from a good part. Used instead of the taught template
    /// while it is wired.
    /// </summary>
    public InputPort TemplateIn { get; }

    /// <summary>The template in use, for a preview of what is being looked for.</summary>
    public OutputPort TemplateOut { get; }

    [Param("Teach Region", Group = "Teach", Editor = ParamEditor.Region,
        Description = "Round the feature to look for. Draw it on a good part, then press Teach.")]
    public Rectangle TeachRegion
    {
        get => _teachRegion;
        set => SetParam(ref _teachRegion, value);
    }

    /// <summary>
    /// The taught template, as a PNG in text, kept in the recipe. Set by
    /// Teach; empty when nothing has been taught.
    /// </summary>
    [Param("Taught Template", Hidden = true)]
    public string TaughtTemplate
    {
        get => _taught;
        set
        {
            if (!SetParam(ref _taught, value ?? string.Empty))
                return;

            lock (_gate)
            {
                _taughtImage?.Dispose();
                _taughtImage = null;
            }
        }
    }

    /// <summary>Teach: keep what Teach Region holds on the latest image as the template.</summary>
    public IReadOnlyList<NodeAction> Actions =>
    [
        new NodeAction(
            "Teach",
            "Keep what Teach Region holds on the current image as the template to look for.",
            Teach),
    ];

    /// <summary>
    /// The feature under Teach Region on the latest image, kept as the
    /// template - or why it cannot be.
    /// </summary>
    /// <remarks>
    /// From the image the node last ran on, which is the one on screen; a
    /// copy of the region is kept from each run for the purpose, since the
    /// image itself is not the node's to hold.
    /// </remarks>
    public string? Teach()
    {
        string encoded;

        lock (_gate)
        {
            if (_lastCut is not { IsEmpty: false } cut)
                return "There is no image to teach from yet. Run the recipe with a good part, then press Teach.";

            using var png = new Emgu.CV.Util.VectorOfByte();
            CvInvoke.Imencode(".png", cut, png);
            encoded = Convert.ToBase64String(png.ToArray());
        }

        // A parameter like any other: saved, undone, and the recipe re-runs.
        TaughtTemplate = encoded;
        return null;
    }

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

        KeepTeachRegion(image);

        using var source = TemplateFor(context);

        if (source is null)
        {
            context.Warn("No template yet: draw Teach Region round the feature on a good part and press Teach, or wire one into Template.");
            context.Set(MatchesOut, new ShapeSet([], image.Width, image.Height));
            context.Set(CountOut, 0);
            context.Set(ScoreOut, 0d);
            context.Set(PositionOut, default(Point2));
            return;
        }

        var template = new Size(source.Width, source.Height);

        var region = _searchRegion.Width > 0 && _searchRegion.Height > 0
            ? Rectangle.Intersect(_searchRegion, new Rectangle(0, 0, image.Width, image.Height))
            : new Rectangle(0, 0, image.Width, image.Height);

        if (template.Width > region.Width || template.Height > region.Height)
        {
            throw new InvalidOperationException(
                $"The template ({template.Width} x {template.Height}) is larger than where it is looked for " +
                $"({region.Width} x {region.Height}).");
        }

        context.Set(TemplateOut, ImageData.Adopt(source.Clone()));

        using var grey = Grey(image);
        using var pattern = Grey(source);
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

    /// <summary>
    /// A copy of what Teach Region holds on this image, for Teach to keep -
    /// small, so it costs little to take on every run.
    /// </summary>
    private void KeepTeachRegion(ImageData image)
    {
        var cut = Rectangle.Intersect(_teachRegion, new Rectangle(0, 0, image.Width, image.Height));

        lock (_gate)
        {
            _lastCut?.Dispose();
            _lastCut = cut.Width > 1 && cut.Height > 1 ? new Mat(image.Mat, cut).Clone() : null;
        }
    }

    /// <summary>
    /// The template for this run: the one wired in, or the one taught - a
    /// copy, the caller's to dispose - or null when there is neither.
    /// </summary>
    private Mat? TemplateFor(EvalContext context)
    {
        if (context.Get<ImageData>(TemplateIn) is { } wired)
            return wired.Mat.Clone();

        lock (_gate)
        {
            if (_taughtImage is null && _taught.Length > 0)
            {
                var png = Convert.FromBase64String(_taught);
                _taughtImage = new Mat();
                CvInvoke.Imdecode(png, ImreadModes.Unchanged, _taughtImage);
            }

            return _taughtImage is { IsEmpty: false } taught ? taught.Clone() : null;
        }
    }

    private static RectShape Outline(Point2 topLeft, Size template, double score) =>
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

    private static Mat Grey(ImageData image) => Grey(image.Mat);

    /// <summary>One channel, 8-bit or float, as matching needs: a colour image as its brightness.</summary>
    private static Mat Grey(Mat image)
    {
        var grey = new Mat();

        switch (image.NumberOfChannels)
        {
            case 3:
                CvInvoke.CvtColor(image, grey, ColorConversion.Bgr2Gray);
                break;
            case 4:
                CvInvoke.CvtColor(image, grey, ColorConversion.Bgra2Gray);
                break;
            default:
                image.CopyTo(grey);
                break;
        }

        // MatchTemplate takes 8-bit or 32-bit float; a 16-bit camera image
        // is neither.
        if (grey.Depth is not (DepthType.Cv8U or DepthType.Cv32F))
            grey.ConvertTo(grey, DepthType.Cv32F);

        return grey;
    }
}
