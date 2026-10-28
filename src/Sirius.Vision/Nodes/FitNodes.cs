using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Util;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>Which points of the incoming shapes a fit is computed from.</summary>
public enum FitSource
{
    /// <summary>Every point of each outline. A blob's contour.</summary>
    Outline,

    /// <summary>One point per shape, at its centre.</summary>
    Centres,
}

/// <summary>Whether each shape is fitted on its own, or all of them together.</summary>
public enum FitGrouping
{
    /// <summary>One fitted shape per incoming shape.</summary>
    EachShape,

    /// <summary>One fitted shape from every point of every shape.</summary>
    AllTogether,
}

/// <summary>
/// Base for a node that fits geometry to the points of other geometry.
/// </summary>
/// <remarks>
/// <para>
/// Fitting is not finding. These take shapes that something has already
/// located - blobs, most often - and reduce their points to the ideal
/// figure underneath. A blob is a ragged contour; what is usually wanted
/// from it is the circle it is a picture of, to sub-pixel precision and
/// with a number saying how well it matched.
/// </para>
/// <para>
/// Every fit reports a <see cref="ShapeMetric.FitError"/>: the
/// root-mean-square distance from the points to the figure. It is the most
/// useful thing a fit produces and the most often left out - a circle
/// fitted to a clean ring and one fitted to a broken arc look identical
/// until it is read.
/// </para>
/// </remarks>
public abstract class ShapeFitNode : VisionNode
{
    private FitSource _from = FitSource.Outline;
    private FitGrouping _grouping = FitGrouping.EachShape;
    private int _minimumPoints = 5;

    protected ShapeFitNode()
    {
        ShapesIn = AddInput<ShapeSet>("Shapes");
        ShapesOut = AddOutput<ShapeSet>("Shapes");
        CountOut = AddOutput<int>("Count");
    }

    public InputPort ShapesIn { get; }

    public OutputPort ShapesOut { get; }

    public OutputPort CountOut { get; }

    [Param(
        "From",
        Description = "Outline fits the contour of each shape. Centres takes " +
                      "one point per shape, for fitting through a row of features.")]
    public FitSource From
    {
        get => _from;
        set => SetParam(ref _from, value);
    }

    [Param(
        "Group",
        Description = "One fit per shape, or a single fit through all of them.")]
    public FitGrouping Grouping
    {
        get => _grouping;
        set => SetParam(ref _grouping, value);
    }

    [Param(
        "Minimum Points",
        Min = 2,
        Max = 1000,
        Editor = ParamEditor.Slider,
        Description = "Fits with fewer points than this are skipped rather " +
                      "than reported with no confidence.")]
    public int MinimumPoints
    {
        get => _minimumPoints;
        set => SetParam(ref _minimumPoints, Math.Max(2, value));
    }

    /// <summary>How many points this kind of fit needs at the very least.</summary>
    protected abstract int RequiredPoints { get; }

    protected override void OnEvaluate(EvalContext context)
    {
        var input = context.Get<ShapeSet>(ShapesIn)!;
        var fitted = new List<Shape>();
        var skipped = 0;

        foreach (var group in Group(input))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (group.Count < Math.Max(RequiredPoints, _minimumPoints))
            {
                skipped++;
                continue;
            }

            if (Fit(group) is { } shape)
                fitted.Add(shape);
            else
                skipped++;
        }

        if (skipped > 0)
        {
            context.Warn(
                $"{skipped} of {Count(input)} had too few points to fit, or could not be " +
                $"fitted. {Descriptor.DisplayName} needs at least " +
                $"{Math.Max(RequiredPoints, _minimumPoints)}.");
        }

        context.Set(ShapesOut, new ShapeSet(fitted, input.FrameWidth, input.FrameHeight));
        context.Set(CountOut, fitted.Count);
    }

    /// <summary>The fitted figure, or null when the points do not admit one.</summary>
    protected abstract Shape? Fit(IReadOnlyList<Point2> points);

    private int Count(ShapeSet input) =>
        Pooled(input) ? 1 : input.Count;

    /// <summary>
    /// Whether every point is fitted as one set: when asked, or when every
    /// shape is a lone point.
    /// </summary>
    /// <remarks>
    /// A point on its own cannot be fitted to anything, so fitting each one
    /// separately - the default, which is right for blobs - would quietly
    /// produce nothing from the edge points Find Circle, Ring Calipers and
    /// the rest give. Points are what those tools are for fitting through,
    /// so they are pooled without having to be told.
    /// </remarks>
    private bool Pooled(ShapeSet input) =>
        _grouping == FitGrouping.AllTogether ||
        (input.Count > 0 && input.Shapes.All(shape => shape is PointShape));

    /// <summary>The point sets to fit, one per result.</summary>
    private IEnumerable<IReadOnlyList<Point2>> Group(ShapeSet input)
    {
        if (Pooled(input))
        {
            yield return input.Shapes.SelectMany(PointsOf).ToArray();
            yield break;
        }

        foreach (var shape in input.Shapes)
            yield return PointsOf(shape).ToArray();
    }

    /// <summary>
    /// The points a shape contributes.
    /// </summary>
    /// <remarks>
    /// Centres give one point each, which only means anything pooled - a
    /// line through a row of pads, a circle through a ring of holes. On
    /// their own they cannot describe a figure, which the minimum-points
    /// check then catches.
    /// </remarks>
    private IEnumerable<Point2> PointsOf(Shape shape)
    {
        if (_from == FitSource.Centres)
        {
            var bounds = shape.Bounds;
            yield return new Point2(
                bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));

            yield break;
        }

        switch (shape)
        {
            case PolygonShape polygon:
                foreach (var point in polygon.Points)
                    yield return point;

                break;

            case OrientedRectShape oriented:
                foreach (var corner in oriented.Corners())
                    yield return corner;

                break;

            case RectShape rect:
                yield return new Point2(rect.Rect.X, rect.Rect.Y);
                yield return new Point2(rect.Rect.X + rect.Rect.Width, rect.Rect.Y);
                yield return new Point2(
                    rect.Rect.X + rect.Rect.Width, rect.Rect.Y + rect.Rect.Height);
                yield return new Point2(rect.Rect.X, rect.Rect.Y + rect.Rect.Height);
                break;

            case LineShape line:
                yield return line.Start;
                yield return line.End;
                break;

            case PointShape point:
                yield return point.At;
                break;

            // A circle or an ellipse is already an ideal figure; fitting
            // another to its outline would be inventing points.
            default:
                var box = shape.Bounds;
                yield return new Point2(box.X + (box.Width / 2), box.Y + (box.Height / 2));
                break;
        }
    }

    protected static VectorOfPointF ToVector(IReadOnlyList<Point2> points) =>
        new(points.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray());

    /// <summary>
    /// Root-mean-square distance from the points to the fitted figure.
    /// </summary>
    protected static double RootMeanSquare(
        IReadOnlyList<Point2> points, Func<Point2, double> distance)
    {
        if (points.Count == 0)
            return 0;

        var total = points.Sum(point => Math.Pow(distance(point), 2));
        return Math.Sqrt(total / points.Count);
    }
}

/// <summary>Fits a circle to the points of each shape.</summary>
[VisionNode(
    "Fit Circle",
    TypeKey = "fit.circle",
    Category = NodeCategory.Fitting,
    Icon = "ph_fit_circle",
    Description = "The best circle through the points of a blob or a set of features.")]
public sealed class FitCircleNode : ShapeFitNode
{
    private double _rejectSigma;

    protected override int RequiredPoints => 3;

    /// <summary>
    /// Find Circle's rejection, so a circle fitted to caliper points here
    /// is as robust as Find Circle's own.
    /// </summary>
    /// <remarks>
    /// Off by default, unlike Find Circle: a recipe saved before this setting
    /// existed fits exactly as it did, and a blob's outline - what this was
    /// first used for - has no strays to reject.
    /// </remarks>
    [Param("Reject Above", Group = "Fit", Min = 0, Max = 6, Step = 0.1,
        Description = "Drop points this many standard deviations off the fit, then fit again. Zero keeps all.")]
    public double RejectSigma
    {
        get => _rejectSigma;
        set => SetParam(ref _rejectSigma, Math.Clamp(value, 0, 6));
    }

    protected override Shape? Fit(IReadOnlyList<Point2> points)
    {
        // A least-squares circle, not the smallest enclosing one: an
        // enclosing circle is decided by three extreme points and so is
        // pulled about by a single bad one, which is the opposite of what
        // fitting is for.
        var fit = CircleFit.FitRobust(points, _rejectSigma);

        if (!fit.Success)
            return null;

        var (centre, radius) = (fit.Centre, fit.Radius);
        points = fit.Used;

        var error = RootMeanSquare(
            points,
            point => Math.Abs(Distance(point, centre) - radius));

        return new CircleShape(centre, radius)
        {
            Metrics = new Dictionary<string, double>
            {
                [ShapeMetric.CentroidX] = centre.X,
                [ShapeMetric.CentroidY] = centre.Y,
                [ShapeMetric.Radius] = radius,
                [ShapeMetric.FitError] = error,
                [ShapeMetric.FitPoints] = points.Count,
            },
        };
    }

    private static double Distance(Point2 a, Point2 b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

}

/// <summary>Fits an ellipse to the points of each shape.</summary>
[VisionNode(
    "Fit Ellipse",
    TypeKey = "fit.ellipse",
    Category = NodeCategory.Fitting,
    Icon = "ph_fit_ellipse",
    Description = "The best ellipse through the points of a blob or a set of features.")]
public sealed class FitEllipseNode : ShapeFitNode
{
    private double _rejectSigma;

    protected override int RequiredPoints => 5;

    /// <summary>The same rejection Fit Circle and Find Circle use, against the ellipse.</summary>
    [Param("Reject Above", Group = "Fit", Min = 0, Max = 6, Step = 0.1,
        Description = "Drop points this many standard deviations off the fit, then fit again. Zero keeps all.")]
    public double RejectSigma
    {
        get => _rejectSigma;
        set => SetParam(ref _rejectSigma, Math.Clamp(value, 0, 6));
    }

    protected override Shape? Fit(IReadOnlyList<Point2> points)
    {
        if (FitOnce(points) is not { } fitted)
            return null;

        // Sigma clipping, as CircleFit.FitRobust does it: fit, drop the
        // points further off than the mean deviation plus so many spreads,
        // fit again - a few times, and never down to too few to fit.
        if (_rejectSigma > 0)
        {
            for (var pass = 0; pass < 3; pass++)
            {
                var (centreNow, majorNow, minorNow, angleNow) = fitted;

                var deviations = points
                    .Select(point => EllipseResidual(point, centreNow, majorNow, minorNow, angleNow))
                    .ToArray();

                var mean = deviations.Average();
                var spread = Math.Sqrt(deviations.Average(d => (d - mean) * (d - mean)));

                if (spread < 1e-9)
                    break;

                var limit = mean + (_rejectSigma * spread);
                var survivors = points.Where((_, i) => deviations[i] <= limit).ToArray();

                if (survivors.Length == points.Count || survivors.Length < RequiredPoints)
                    break;

                if (FitOnce(survivors) is not { } next)
                    break;

                points = survivors;
                fitted = next;
            }
        }

        var (centre, major, minor, angle) = fitted;

        var error = RootMeanSquare(points, point => EllipseResidual(point, centre, major, minor, angle));

        return new EllipseShape(centre, major, minor, angle)
        {
            Metrics = new Dictionary<string, double>
            {
                [ShapeMetric.CentroidX] = centre.X,
                [ShapeMetric.CentroidY] = centre.Y,
                [ShapeMetric.MajorAxis] = major,
                [ShapeMetric.MinorAxis] = minor,
                [ShapeMetric.Angle] = angle,
                [ShapeMetric.AspectRatio] = minor > 0 ? major / minor : 0,
                [ShapeMetric.FitError] = error,
                [ShapeMetric.FitPoints] = points.Count,
            },
        };
    }

    /// <summary>One ellipse through every point given, or null when they do not make one.</summary>
    private static (Point2 Centre, double Major, double Minor, double Angle)? FitOnce(IReadOnlyList<Point2> points)
    {
        using var vector = ToVector(points);
        var box = CvInvoke.FitEllipse(vector);

        var major = Math.Max(box.Size.Width, box.Size.Height);
        var minor = Math.Min(box.Size.Width, box.Size.Height);

        if (major <= 0 || double.IsNaN(major))
            return null;

        // OpenCV gives the angle of the width axis; this reports the angle
        // of the longer one, so "angle" means the same thing here as it
        // does on a blob's oriented rectangle.
        var angle = box.Size.Width >= box.Size.Height ? box.Angle : box.Angle + 90;

        return (new Point2(box.Center.X, box.Center.Y), major, minor, angle);
    }

    /// <summary>
    /// Roughly how far a point lies from the ellipse.
    /// </summary>
    /// <remarks>
    /// The true distance to an ellipse has no closed form, so this is the
    /// radial one: along the ray from the centre, how far the point is from
    /// where the ellipse crosses it. Exact on a circle and close enough on
    /// anything that is nearly one, which is what gets fitted.
    /// </remarks>
    private static double EllipseResidual(
        Point2 point, Point2 centre, double major, double minor, double angle)
    {
        var radians = -angle * Math.PI / 180.0;
        var dx = point.X - centre.X;
        var dy = point.Y - centre.Y;

        var x = (dx * Math.Cos(radians)) - (dy * Math.Sin(radians));
        var y = (dx * Math.Sin(radians)) + (dy * Math.Cos(radians));

        var a = major / 2;
        var b = minor / 2;

        if (a <= 0 || b <= 0)
            return 0;

        var distance = Math.Sqrt((x * x) + (y * y));

        if (distance < 1e-9)
            return Math.Min(a, b);

        // Where the ray through the point meets the ellipse.
        var scale = 1.0 / Math.Sqrt(((x * x) / (a * a)) + ((y * y) / (b * b)));

        return Math.Abs(distance - (distance * scale));
    }
}

/// <summary>Fits a line to the points of each shape.</summary>
[VisionNode(
    "Fit Line",
    TypeKey = "fit.line",
    Category = NodeCategory.Fitting,
    Icon = "ph_fit_line",
    Description = "The best line through the points of a blob or a row of features.")]
public sealed class FitLineNode : ShapeFitNode
{
    private DistType _distance = DistType.L2;

    protected override int RequiredPoints => 2;

    [Param(
        "Robustness",
        Description = "L2 is least squares, which one outlier can pull. The " +
                      "others weight distant points down, at the cost of a few " +
                      "iterations.")]
    public DistType Distance
    {
        get => _distance;
        set => SetParam(ref _distance, value);
    }

    protected override Shape? Fit(IReadOnlyList<Point2> points)
    {
        // The overload taking points gives the direction and a point on
        // the line directly, rather than packing both into an array.
        var raw = points.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();

        CvInvoke.FitLine(raw, out var direction, out var onLine, _distance, 0, 0.01, 0.01);

        double dx = direction.X;
        double dy = direction.Y;
        double x0 = onLine.X;
        double y0 = onLine.Y;

        if (Math.Abs(dx) < 1e-12 && Math.Abs(dy) < 1e-12)
            return null;

        // Drawn between the extreme projections of the points, so the
        // segment covers what was fitted rather than running off the frame.
        var projections = points
            .Select(p => ((p.X - x0) * dx) + ((p.Y - y0) * dy))
            .ToArray();

        var start = new Point2(x0 + (projections.Min() * dx), y0 + (projections.Min() * dy));
        var end = new Point2(x0 + (projections.Max() * dx), y0 + (projections.Max() * dy));

        var error = RootMeanSquare(
            points,
            point => Math.Abs(((point.X - x0) * -dy) + ((point.Y - y0) * dx)));

        var length = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));

        return new LineShape(start, end)
        {
            Metrics = new Dictionary<string, double>
            {
                [ShapeMetric.CentroidX] = (start.X + end.X) / 2,
                [ShapeMetric.CentroidY] = (start.Y + end.Y) / 2,
                [ShapeMetric.Length] = length,
                [ShapeMetric.Angle] = Math.Atan2(dy, dx) * 180.0 / Math.PI,
                [ShapeMetric.FitError] = error,
                [ShapeMetric.FitPoints] = points.Count,
            },
        };
    }
}
