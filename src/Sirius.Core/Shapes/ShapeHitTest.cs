namespace Sirius.Core.Shapes;

/// <summary>
/// How far a point is from a shape's outline.
/// </summary>
/// <remarks>
/// <para>
/// Picking used to be by bounding box, which is right for a small compact
/// blob and wrong for everything else: a fitted circle three thousand
/// pixels across claimed every click inside it, including the whole of the
/// part it was measured on and any annotation sitting there. The shapes are
/// drawn as outlines, so the outline is what should be clickable.
/// </para>
/// <para>
/// Distance to the outline rather than a boolean hit, so the caller can
/// pick the nearest of several overlapping shapes rather than the first it
/// happens to test.
/// </para>
/// </remarks>
public static class ShapeHitTest
{
    /// <summary>How many points an ellipse's outline is sampled at.</summary>
    /// <remarks>
    /// The exact distance from a point to an ellipse is the root of a
    /// quartic. Sampling is a few lines instead, and at this count the
    /// worst error on a large ellipse is well under the tolerance any
    /// pointing device needs anyway.
    /// </remarks>
    private const int EllipseSamples = 180;

    /// <summary>
    /// Distance from <paramref name="point"/> to the shape's drawn outline.
    /// </summary>
    public static double DistanceToOutline(Shape shape, Point2 point)
    {
        ArgumentNullException.ThrowIfNull(shape);

        return shape switch
        {
            PointShape p => Distance(point, p.At),
            LineShape l => ToSegment(point, l.Start, l.End),
            CircleShape c => Math.Abs(Distance(point, c.Centre) - c.Radius),
            RectShape r => ToRectangle(point, r.Rect),
            OrientedRectShape o => ToOrientedRect(point, o),
            EllipseShape e => ToEllipse(point, e),
            PolygonShape g => ToPolygon(point, g),
            _ => ToBounds(point, shape.Bounds),
        };
    }

    private static double Distance(Point2 a, Point2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;

        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>Distance to a line segment, not to the infinite line.</summary>
    private static double ToSegment(Point2 point, Point2 start, Point2 end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = (dx * dx) + (dy * dy);

        if (lengthSquared < 1e-12)
            return Distance(point, start);

        // Clamped, so a point beyond an end measures to that end rather
        // than to where the line would have gone.
        var t = Math.Clamp(
            (((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared, 0, 1);

        return Distance(point, new Point2(start.X + (t * dx), start.Y + (t * dy)));
    }

    /// <summary>Distance to the four sides of an axis-aligned rectangle.</summary>
    private static double ToRectangle(Point2 point, Bounds rect)
    {
        var corners = new[]
        {
            new Point2(rect.X, rect.Y),
            new Point2(rect.Right, rect.Y),
            new Point2(rect.Right, rect.Bottom),
            new Point2(rect.X, rect.Bottom),
        };

        return ToClosedPath(point, corners);
    }

    /// <summary>
    /// Uses the shape's own corners rather than rotating again here - one
    /// rotation, in the type that owns the angle.
    /// </summary>
    private static double ToOrientedRect(Point2 point, OrientedRectShape rect) =>
        ToClosedPath(point, rect.Corners());

    private static double ToEllipse(Point2 point, EllipseShape ellipse)
    {
        var radians = ellipse.Angle * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        var a = ellipse.MajorAxis / 2;
        var b = ellipse.MinorAxis / 2;

        var best = double.MaxValue;

        for (var i = 0; i < EllipseSamples; i++)
        {
            var t = 2 * Math.PI * i / EllipseSamples;
            var x = a * Math.Cos(t);
            var y = b * Math.Sin(t);

            var on = new Point2(
                ellipse.Centre.X + (x * cos) - (y * sin),
                ellipse.Centre.Y + (x * sin) + (y * cos));

            best = Math.Min(best, Distance(point, on));
        }

        return best;
    }

    private static double ToPolygon(Point2 point, PolygonShape polygon)
    {
        if (polygon.Points.Count == 0)
            return double.MaxValue;

        if (polygon.Points.Count == 1)
            return Distance(point, polygon.Points[0]);

        return polygon.Closed
            ? ToClosedPath(point, polygon.Points)
            : ToOpenPath(point, polygon.Points);
    }

    private static double ToClosedPath(Point2 point, IReadOnlyList<Point2> points)
    {
        var best = ToOpenPath(point, points);

        return Math.Min(best, ToSegment(point, points[^1], points[0]));
    }

    private static double ToOpenPath(Point2 point, IReadOnlyList<Point2> points)
    {
        var best = double.MaxValue;

        for (var i = 0; i + 1 < points.Count; i++)
            best = Math.Min(best, ToSegment(point, points[i], points[i + 1]));

        return best;
    }

    /// <summary>
    /// The fallback for a shape this does not know: its bounding box, which
    /// is what picking used to do for everything.
    /// </summary>
    private static double ToBounds(Point2 point, Bounds bounds) =>
        ToRectangle(point, bounds);
}
