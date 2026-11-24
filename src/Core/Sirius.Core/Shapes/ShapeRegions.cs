using System.Drawing;
using Sirius.Core.Nodes;

namespace Sirius.Core.Shapes;

/// <summary>
/// A shape on a wire, read as the region a parameter holds.
/// </summary>
/// <remarks>
/// <para>
/// A region is a shape. The box Find Blobs puts round a part, the line Find
/// Line found, the circle Find Circle fitted - each is where the next tool
/// should look, in the coordinates of the image it was found in. So a
/// region parameter that can be wired takes a shape set as readily as it
/// takes its own type, and the tool searches wherever the shape says.
/// </para>
/// <para>
/// A shape seldom says everything a region needs. A line has a length and
/// a direction but no width; a circle has a radius but says nothing of how
/// wide a band to search round it. What the shape leaves unsaid comes from
/// the value the region already holds - the one drawn or typed in the
/// properties panel - so a wired line sets where Find Line looks and the
/// drawn region still sets how far either side.
/// </para>
/// <para>
/// The same rule settles which way round a region is. A box, a line and an
/// ellipse can each be written several ways - turned a half turn, or a
/// quarter turn with its sides swapped - and a tool reads them differently:
/// which side Find Line's calipers start from, which way its region runs.
/// Of the ways a shape can be written, the one nearest the drawn region's
/// angle is taken, so the region keeps facing the way it was drawn
/// however the shape that drives it happened to be reported.
/// </para>
/// </remarks>
public static class ShapeRegions
{
    /// <summary>How many sides a round shape is given when it becomes a path.</summary>
    private const int RoundCorners = 72;

    /// <summary>Whether a shape set can drive a parameter of <paramref name="target"/>.</summary>
    public static bool CanDrive(Type target) =>
        target == typeof(OrientedRect) ||
        target == typeof(Ring) ||
        target == typeof(Rectangle) ||
        target == typeof(Point2) ||
        target == typeof(PathBand);

    /// <summary>
    /// <paramref name="shape"/> as a value of <paramref name="target"/>,
    /// taking what it does not say from <paramref name="current"/>.
    /// </summary>
    public static object From(Shape shape, Type target, object? current)
    {
        ArgumentNullException.ThrowIfNull(shape);

        if (target == typeof(OrientedRect))
            return ToRect(shape, current is OrientedRect rect ? rect : new OrientedRect(0, 0, 100, 40, 0));

        if (target == typeof(Ring))
            return ToRing(shape, current is Ring ring ? ring : new Ring(0, 0, 80, 120));

        if (target == typeof(Rectangle))
            return ToRectangle(shape, current is Rectangle rectangle ? rectangle : new Rectangle(0, 0, 100, 100));

        if (target == typeof(Point2))
            return CentreOf(shape);

        if (target == typeof(PathBand))
            return ToPath(shape, current as PathBand ?? PathBand.Default);

        throw new ArgumentException($"A shape cannot drive a {target.Name}.", nameof(target));
    }

    /// <summary>
    /// The point a shape is at: a point's own position, a producer's
    /// centroid when it gave one, otherwise the middle of its extent.
    /// </summary>
    public static Point2 CentreOf(Shape shape) => shape switch
    {
        PointShape point => point.At,
        CircleShape circle => circle.Centre,
        EllipseShape ellipse => ellipse.Centre,
        OrientedRectShape box => box.Centre,
        _ when shape.Metric(ShapeMetric.CentroidX) is { } x && shape.Metric(ShapeMetric.CentroidY) is { } y => new Point2(x, y),
        _ => shape.Bounds.Centre,
    };

    private static OrientedRect ToRect(Shape shape, OrientedRect current) => shape switch
    {
        PointShape point => current with { CentreX = point.At.X, CentreY = point.At.Y },

        // A line says where and how long, and which way - not how wide.
        LineShape line => Facing(
            new Point2((line.Start.X + line.End.X) / 2, (line.Start.Y + line.End.Y) / 2),
            line.Length,
            current.Height,
            Math.Atan2(line.End.Y - line.Start.Y, line.End.X - line.Start.X) * 180 / Math.PI,
            current.Angle,
            quarterTurns: false),

        OrientedRectShape box => Facing(box.Centre, box.Width, box.Height, box.AngleDegrees, current.Angle, quarterTurns: true),

        EllipseShape ellipse => Facing(
            ellipse.Centre, ellipse.MajorAxis, ellipse.MinorAxis, ellipse.Angle, current.Angle, quarterTurns: true),

        CircleShape circle => new OrientedRect(
            circle.Centre.X, circle.Centre.Y, circle.Radius * 2, circle.Radius * 2, current.Angle),

        _ => Facing(shape.Bounds.Centre, shape.Bounds.Width, shape.Bounds.Height, 0, current.Angle, quarterTurns: true),
    };

    /// <summary>
    /// The way of writing a box nearest <paramref name="towards"/>: turned
    /// by half turns, or by quarter turns with its sides swapped.
    /// </summary>
    private static OrientedRect Facing(
        Point2 centre, double width, double height, double angle, double towards, bool quarterTurns)
    {
        var best = new OrientedRect(centre.X, centre.Y, width, height, angle);
        var bestOff = double.MaxValue;

        for (var turn = -4; turn <= 4; turn++)
        {
            if (!quarterTurns && turn % 2 != 0)
                continue;

            var candidate = angle + (turn * 90);
            var off = Math.Abs(Wrap(candidate - towards));

            if (off >= bestOff - 1e-9)
                continue;

            var swapped = turn % 2 != 0;

            bestOff = off;
            best = new OrientedRect(
                centre.X,
                centre.Y,
                swapped ? height : width,
                swapped ? width : height,
                towards + Wrap(candidate - towards));
        }

        return best;
    }

    /// <summary>An angle difference in (-180, 180].</summary>
    private static double Wrap(double degrees)
    {
        var wrapped = degrees % 360;

        if (wrapped > 180)
            wrapped -= 360;
        else if (wrapped <= -180)
            wrapped += 360;

        return wrapped;
    }

    private static Ring ToRing(Shape shape, Ring current) => shape switch
    {
        // A circle is the edge to search round; the band keeps its width.
        CircleShape circle => Ring.FromMean(circle.Centre.X, circle.Centre.Y, circle.Radius, current.HalfWidth),

        EllipseShape ellipse => Ring.FromMean(
            ellipse.Centre.X, ellipse.Centre.Y, (ellipse.MajorAxis + ellipse.MinorAxis) / 4, current.HalfWidth),

        _ => current.WithCentre(CentreOf(shape).X, CentreOf(shape).Y),
    };

    private static Rectangle ToRectangle(Shape shape, Rectangle current)
    {
        if (shape is PointShape point)
        {
            return current with
            {
                X = (int)Math.Round(point.At.X - (current.Width / 2.0)),
                Y = (int)Math.Round(point.At.Y - (current.Height / 2.0)),
            };
        }

        var bounds = shape.Bounds;
        var left = (int)Math.Floor(bounds.X);
        var top = (int)Math.Floor(bounds.Y);

        return new Rectangle(
            left,
            top,
            Math.Max(1, (int)Math.Ceiling(bounds.Right) - left),
            Math.Max(1, (int)Math.Ceiling(bounds.Bottom) - top));
    }

    private static PathBand ToPath(Shape shape, PathBand current)
    {
        switch (shape)
        {
            case PolygonShape polygon when polygon.Points.Count >= 2:
                return new PathBand(polygon.Points, polygon.Closed, current.Width);

            case LineShape line:
                return new PathBand([line.Start, line.End], closed: false, current.Width);

            case OrientedRectShape box:
                return new PathBand(box.Corners(), closed: true, current.Width);

            case RectShape rect:
                return new PathBand(
                    [
                        new Point2(rect.Rect.X, rect.Rect.Y),
                        new Point2(rect.Rect.Right, rect.Rect.Y),
                        new Point2(rect.Rect.Right, rect.Rect.Bottom),
                        new Point2(rect.Rect.X, rect.Rect.Bottom),
                    ],
                    closed: true,
                    current.Width);

            case CircleShape circle:
                return new PathBand(
                    Round(circle.Centre, circle.Radius, circle.Radius, 0), closed: true, current.Width);

            case EllipseShape ellipse:
                return new PathBand(
                    Round(ellipse.Centre, ellipse.MajorAxis / 2, ellipse.MinorAxis / 2, ellipse.Angle),
                    closed: true,
                    current.Width);

            default:
            {
                // A point, or a polygon too small to follow: the drawn path
                // moved so its middle is there.
                var at = CentreOf(shape);
                var middle = new Point2(current.Points.Average(p => p.X), current.Points.Average(p => p.Y));

                return current.Moved(at.X - middle.X, at.Y - middle.Y);
            }
        }
    }

    /// <summary>
    /// Corners round an ellipse, clockwise from the end of its major axis -
    /// the direction angles run on an image.
    /// </summary>
    private static Point2[] Round(Point2 centre, double a, double b, double degrees)
    {
        var tilt = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(tilt), Math.Sin(tilt));

        return Enumerable.Range(0, RoundCorners)
            .Select(i =>
            {
                var t = 2 * Math.PI * i / RoundCorners;
                var (x, y) = (a * Math.Cos(t), b * Math.Sin(t));

                return new Point2(centre.X + (x * cos) - (y * sin), centre.Y + (x * sin) + (y * cos));
            })
            .ToArray();
    }
}
