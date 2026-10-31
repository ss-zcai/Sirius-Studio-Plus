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
/// while it is wired.
/// </para>
/// <para>
/// Teach Region may be turned, as Region of Interest's is. The feature is
/// then straightened as it is cut, the angle is kept with it, and each
/// image is searched turned by that angle - so a feature that sits at a
/// known angle on a fixtured part is found, and drawn at that angle.
/// Position only: a part turned by more than a few degrees from the taught
/// angle scores poorly.
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
    private OrientedRect _teachRect = new(140, 130, 80, 60, 0);
    private string _taught = string.Empty;
    private Mat? _taughtImage;
    private double _taughtAngle;
    private Mat? _lastCut;
    private double _lastCutAngle;
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

    [Param("Teach Region", Group = "Teach", Editor = ParamEditor.Rect, Action = "Teach",
        Description = "Round the feature to look for, as centre x, centre y, width, height and angle. Draw it " +
                      "on a good part, then press Teach.")]
    public OrientedRect TeachRect
    {
        get => _teachRect;
        set => SetParam(ref _teachRect, value);
    }

    /// <summary>
    /// The taught template, kept in the recipe as the angle it was taught
    /// at and a PNG, in text: "12.5|iVBOR...". Set by Teach; empty when
    /// nothing has been taught.
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
            encoded = _lastCutAngle.ToString("R", CultureInfo.InvariantCulture) + "|" +
                      Convert.ToBase64String(png.ToArray());
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

        using var source = TemplateFor(context, out var angle);

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

        context.Set(TemplateOut, ImageData.Adopt(source.Clone()));

        using var grey = Grey(image);
        using var pattern = Grey(source);
        using var searched = new Mat(grey, region);
        using var frame = Turned(searched, angle, out var back);

        if (template.Width > frame.Width || template.Height > frame.Height)
        {
            throw new InvalidOperationException(
                $"The template ({template.Width} x {template.Height}) is larger than where it is looked for " +
                $"({region.Width} x {region.Height}).");
        }

        var matches = Find(
            frame,
            pattern,
            context.Param(nameof(MinimumScore), _minimumScore),
            _maximumCount,
            _separation,
            context.CancellationToken);

        var shapes = matches
            .Select(match =>
            {
                // The middle of the match in the turned frame, carried back
                // to where it is on the image.
                var middle = back.MapPoint(new Point2(
                    match.TopLeft.X + (template.Width / 2.0),
                    match.TopLeft.Y + (template.Height / 2.0)))!.Value;

                return (Shape)Outline(new Point2(middle.X + region.X, middle.Y + region.Y), template, angle, match.Score);
            })
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
    /// A copy of what Teach Region holds on this image, straightened, for
    /// Teach to keep - small, so it costs little to take on every run.
    /// </summary>
    private void KeepTeachRegion(ImageData image)
    {
        var rect = _teachRect;
        var width = (int)Math.Round(rect.Width);
        var height = (int)Math.Round(rect.Height);

        Mat? cut = null;

        if (width > 1 && height > 1)
        {
            // As Region of Interest straightens: turned about the region's
            // centre, which is then moved to the middle of the cut.
            using var turn = new Matrix<double>(2, 3);
            CvInvoke.GetRotationMatrix2D(
                new PointF((float)rect.CentreX, (float)rect.CentreY), rect.Angle, 1, turn);
            turn.Data[0, 2] += (width / 2.0) - rect.CentreX;
            turn.Data[1, 2] += (height / 2.0) - rect.CentreY;

            cut = new Mat();
            CvInvoke.WarpAffine(
                image.Mat, cut, turn, new Size(width, height), Inter.Linear, Warp.Default, BorderType.Replicate);
        }

        lock (_gate)
        {
            _lastCut?.Dispose();
            _lastCut = cut;
            _lastCutAngle = rect.Angle;
        }
    }

    /// <summary>
    /// The template for this run, and the angle it is looked for at: the
    /// one wired in, upright, or the one taught at the angle it was taught
    /// at - a copy, the caller's to dispose - or null when there is neither.
    /// </summary>
    private Mat? TemplateFor(EvalContext context, out double angle)
    {
        angle = 0;

        if (context.Get<ImageData>(TemplateIn) is { } wired)
            return wired.Mat.Clone();

        lock (_gate)
        {
            if (_taughtImage is null && _taught.Length > 0)
            {
                // "angle|png", or a bare png from before angles were kept.
                var bar = _taught.IndexOf('|');

                _taughtAngle = bar > 0 && double.TryParse(
                    _taught[..bar], NumberStyles.Float, CultureInfo.InvariantCulture, out var taughtAt)
                    ? taughtAt
                    : 0;

                _taughtImage = new Mat();
                CvInvoke.Imdecode(Convert.FromBase64String(_taught[(bar + 1)..]), ImreadModes.Unchanged, _taughtImage);
            }

            angle = _taughtAngle;
            return _taughtImage is { IsEmpty: false } taught ? taught.Clone() : null;
        }
    }

    /// <summary>
    /// <paramref name="image"/> turned by <paramref name="angle"/>, on a
    /// canvas big enough to hold all of it - so a feature at that angle
    /// stands upright, as the template was cut - and the way back from it.
    /// </summary>
    private static Mat Turned(Mat image, double angle, out ShapeMap back)
    {
        if (Math.Abs(angle) < 0.01)
        {
            back = ShapeMap.Translation(0, 0);
            return image.Clone();
        }

        using var turn = new Matrix<double>(2, 3);
        CvInvoke.GetRotationMatrix2D(new PointF(image.Width / 2f, image.Height / 2f), angle, 1, turn);

        var cos = Math.Abs(turn.Data[0, 0]);
        var sin = Math.Abs(turn.Data[0, 1]);
        var width = (int)Math.Ceiling((image.Width * cos) + (image.Height * sin));
        var height = (int)Math.Ceiling((image.Width * sin) + (image.Height * cos));

        turn.Data[0, 2] += (width / 2.0) - (image.Width / 2.0);
        turn.Data[1, 2] += (height / 2.0) - (image.Height / 2.0);

        var turned = new Mat();
        CvInvoke.WarpAffine(image, turned, turn, new Size(width, height), Inter.Linear, Warp.Default, BorderType.Constant);

        using var inverse = new Matrix<double>(2, 3);
        CvInvoke.InvertAffineTransform(turn, inverse);

        var m = inverse.Data;
        back = ShapeMap.Affine(m[0, 0], m[0, 1], m[0, 2], m[1, 0], m[1, 1], m[1, 2]);
        return turned;
    }

    /// <summary>A match drawn where it is: upright, or turned by the angle it was found at.</summary>
    private static Shape Outline(Point2 centre, Size template, double angle, double score)
    {
        var metrics = new Dictionary<string, double>
        {
            [ShapeMetric.CentroidX] = centre.X,
            [ShapeMetric.CentroidY] = centre.Y,
            [ShapeMetric.Score] = score,
        };

        var label = score.ToString("0.000", CultureInfo.InvariantCulture);

        return Math.Abs(angle) < 0.01
            ? new RectShape(new Bounds(centre.X - (template.Width / 2.0), centre.Y - (template.Height / 2.0), template.Width, template.Height))
            {
                Colour = Found,
                StrokeWidth = 2,
                Label = label,
                Metrics = metrics,
            }
            : new OrientedRectShape(centre, template.Width, template.Height, angle)
            {
                Colour = Found,
                StrokeWidth = 2,
                Label = label,
                Metrics = metrics,
            };
    }

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
