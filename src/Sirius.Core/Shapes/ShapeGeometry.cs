namespace Sirius.Core.Shapes;

/// <summary>
/// Points on and about a shape, for tools that measure between two of them.
/// </summary>
/// <remarks>
/// Separate from <see cref="ShapeHitTest"/>, which answers "how far is this
/// point from the outline". This answers "where is the shape" and "what does
/// its outline look like", which is what a measurement between two shapes
/// needs.
/// </remarks>
public static class ShapeGeometry
{
    /// <summary>
    /// How many points a shape's outline is walked at when there is no
    /// closed form for what is being asked.
    /// </summary>
    /// <remarks>
    /// A measurement is read off a screen by a person, so a tenth of a
    /// pixel is already past what anybody acts on; this is comfortably
    /// inside that on the shapes a vision tool produces. The exact cases
    /// that matter - two circles - do not come through here at all.
    /// </remarks>
    public const int OutlineSamples = 360;

    /// <summary>
    /// The shape's centre: what a measurement between two shapes means by
    /// "from here".
    /// </summary>
    /// <remarks>
    /// The producer's own centroid when it published one, because a blob's
    /// centroid is a real measurement of where its mass is and the middle
    /// of its bounding box is not. Otherwise the shape's own definition of
    /// a centre, and the box only as a last resort.
    /// </remarks>
    public static Point2 Centre(Shape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        if (shape.Metrics.TryGetValue(ShapeMetric.CentroidX, out var x) &&
            shape.Metrics.TryGetValue(ShapeMetric.CentroidY, out var y))
        {
            return new Point2(x, y);
        }

        return shape switch
        {
            PointShape p => p.At,
            CircleShape c => c.Centre,
            EllipseShape e => e.Centre,
            OrientedRectShape o => o.Centre,
            LineShape l => new Point2((l.Start.X + l.End.X) / 2, (l.Start.Y + l.End.Y) / 2),
            PolygonShape g when g.Points.Count > 0 => new Point2(
                g.Points.Average(point => point.X),
                g.Points.Average(point => point.Y)),
            _ => shape.Bounds.Centre,
        };
    }

    /// <summary>
    /// Points walked around the shape's outline, for measuring against
    /// something that has no closed form.
    /// </summary>
    public static IReadOnlyList<Point2> Outline(Shape shape, int samples = OutlineSamples)
    {
        ArgumentNullException.ThrowIfNull(shape);

        samples = Math.Max(4, samples);

        return shape switch
        {
            PointShape p => [p.At],
            LineShape l => Along([l.Start, l.End], samples, closed: false),
            CircleShape c => Round(c.Centre, c.Radius, c.Radius, 0, samples),
            EllipseShape e => Round(e.Centre, e.MajorAxis / 2, e.MinorAxis / 2, e.Angle, samples),
            OrientedRectShape o => Along(o.Corners(), samples, closed: true),
            RectShape r => Along(Corners(r.Rect), samples, closed: true),
            PolygonShape g when g.Points.Count > 0 => Along(g.Points, samples, g.Closed),
            _ => Along(Corners(shape.Bounds), samples, closed: true),
        };
    }

    private static Point2[] Corners(Bounds bounds) =>
    [
        new(bounds.X, bounds.Y),
        new(bounds.Right, bounds.Y),
        new(bounds.Right, bounds.Bottom),
        new(bounds.X, bounds.Bottom),
    ];

    private static Point2[] Round(
        Point2 centre, double semiMajor, double semiMinor, double degrees, int samples)
    {
        var radians = degrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        var points = new Point2[samples];

        for (var i = 0; i < samples; i++)
        {
            var t = 2 * Math.PI * i / samples;
            var x = semiMajor * Math.Cos(t);
            var y = semiMinor * Math.Sin(t);

            points[i] = new Point2(
                centre.X + (x * cos) - (y * sin),
                centre.Y + (x * sin) + (y * cos));
        }

        return points;
    }

    /// <summary>
    /// Walks a path, spacing the samples by length rather than per segment
    /// - so a long side is not represented by the same two points as a
    /// short one.
    /// </summary>
    private static Point2[] Along(IReadOnlyList<Point2> path, int samples, bool closed)
    {
        if (path.Count == 1)
            return [path[0]];

        var vertices = closed ? path.Append(path[0]).ToArray() : path.ToArray();
        var lengths = new double[vertices.Length - 1];
        var total = 0.0;

        for (var i = 0; i < lengths.Length; i++)
        {
            lengths[i] = Distance(vertices[i], vertices[i + 1]);
            total += lengths[i];
        }

        if (total < 1e-9)
            return [vertices[0]];

        var points = new List<Point2>(samples);

        for (var i = 0; i < samples; i++)
        {
            var target = total * i / samples;
            var walked = 0.0;

            for (var segment = 0; segment < lengths.Length; segment++)
            {
                if (walked + lengths[segment] >= target || segment == lengths.Length - 1)
                {
                    var t = lengths[segment] < 1e-9 ? 0 : (target - walked) / lengths[segment];

                    points.Add(new Point2(
                        vertices[segment].X + (t * (vertices[segment + 1].X - vertices[segment].X)),
                        vertices[segment].Y + (t * (vertices[segment + 1].Y - vertices[segment].Y))));

                    break;
                }

                walked += lengths[segment];
            }
        }

        return points.ToArray();
    }

    /// <summary>
    /// The direction a line-like shape runs in, as a unit vector, or null
    /// when the shape has no direction to speak of.
    /// </summary>
    /// <remarks>
    /// A line has one. So does an oriented rectangle and an ellipse, along
    /// their long axis, which is what an angle between a fitted ellipse
    /// and an edge means. A circle does not, and saying so is better than
    /// returning something arbitrary that reads as an answer.
    /// </remarks>
    public static (double X, double Y)? Direction(Shape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        switch (shape)
        {
            case LineShape line:
            {
                var dx = line.End.X - line.Start.X;
                var dy = line.End.Y - line.Start.Y;
                var length = Math.Sqrt((dx * dx) + (dy * dy));

                return length < 1e-9 ? null : (dx / length, dy / length);
            }

            case OrientedRectShape rect:
                return FromDegrees(rect.AngleDegrees);

            case EllipseShape ellipse:
                return FromDegrees(ellipse.Angle);

            default:
                return shape.Metrics.TryGetValue(ShapeMetric.Angle, out var degrees)
                    ? FromDegrees(degrees)
                    : null;
        }
    }

    private static (double X, double Y) FromDegrees(double degrees)
    {
        var radians = degrees * Math.PI / 180.0;

        return (Math.Cos(radians), Math.Sin(radians));
    }

    internal static double Distance(Point2 a, Point2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;

        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
