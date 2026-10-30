namespace Sirius.Core.Shapes;

/// <summary>
/// Carries shapes from one image to another made from it - cropped,
/// resized, straightened, unwrapped - so what was found on the first can be
/// drawn and measured on the second.
/// </summary>
/// <remarks>
/// <para>
/// A shape set says which image it was measured on, and a viewer refuses
/// to draw it on any other, because it would land in the wrong place. A
/// node that makes a new image from an old one knows exactly where every
/// point of the old one went, so it can bring the shapes along.
/// </para>
/// <para>
/// A map that keeps shape - a shift, a turn, an even scale - keeps each
/// shape what it was: a circle stays a circle. An uneven scale makes a
/// circle an ellipse. Anything else - polar unwrapping - makes most shapes
/// outlines, traced closely enough to follow the curve.
/// </para>
/// <para>
/// Measurements: positions are re-read where the shape now is. Lengths and
/// areas are converted where the map makes that exact - a length by the
/// scale of an even map, an area by any straight-line map - and dropped
/// where it does not, so that a mapped shape never carries a number that
/// is quietly wrong. Measurements with no unit, contrast or circularity,
/// stay as they were.
/// </para>
/// </remarks>
public sealed class ShapeMap
{
    private static readonly HashSet<string> Positions = new(StringComparer.Ordinal)
    {
        ShapeMetric.CentroidX, ShapeMetric.CentroidY,
        ShapeMetric.BoundsX, ShapeMetric.BoundsY, ShapeMetric.BoundsWidth, ShapeMetric.BoundsHeight,
    };

    private static readonly HashSet<string> Lengths = new(StringComparer.Ordinal)
    {
        ShapeMetric.Perimeter, ShapeMetric.Width, ShapeMetric.Length, ShapeMetric.Radius,
        ShapeMetric.Deviation, ShapeMetric.Along, ShapeMetric.Offset,
        ShapeMetric.MajorAxis, ShapeMetric.MinorAxis, ShapeMetric.FitError,
    };

    private static readonly HashSet<string> Angles = new(StringComparer.Ordinal)
    {
        ShapeMetric.Angle, ShapeMetric.StartAngle, ShapeMetric.EndAngle,
    };

    private readonly Func<Point2, Point2?> _point;
    private readonly (double A, double B, double C, double D)? _linear;
    private readonly double _seam;

    private ShapeMap(Func<Point2, Point2?> point, (double A, double B, double C, double D)? linear, double seam)
    {
        _point = point;
        _linear = linear;
        _seam = seam;
    }

    /// <summary>A straight-line map: x' = a x + b y + tx, y' = c x + d y + ty.</summary>
    public static ShapeMap Affine(double a, double b, double tx, double c, double d, double ty) =>
        new(p => new Point2((a * p.X) + (b * p.Y) + tx, (c * p.X) + (d * p.Y) + ty), (a, b, c, d), 0);

    /// <summary>Everything moved by the same amount - a crop.</summary>
    public static ShapeMap Translation(double dx, double dy) => Affine(1, 0, dx, 0, 1, dy);

    /// <summary>
    /// Any other map, point by point; null for a point with nowhere to go.
    /// </summary>
    /// <param name="seam">
    /// Where the output wraps round, as a distance along X: an outline
    /// crossing it is cut there rather than joined across the whole image.
    /// Zero for none.
    /// </param>
    public static ShapeMap NonLinear(Func<Point2, Point2?> map, double seam = 0) => new(map, null, seam);

    /// <summary>Where one point goes, or null.</summary>
    public Point2? MapPoint(Point2 point) => _point(point);

    /// <summary>Every shape of <paramref name="set"/>, carried to an image of the given size.</summary>
    public ShapeSet Map(ShapeSet set, int frameWidth, int frameHeight)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new ShapeSet(set.Shapes.SelectMany(Map).ToArray(), frameWidth, frameHeight);
    }

    /// <summary>
    /// One shape, carried across: usually one shape, none when it has
    /// nowhere to go, several when an outline is cut at a seam.
    /// </summary>
    public IEnumerable<Shape> Map(Shape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        foreach (var mapped in Geometry(shape))
            yield return mapped with { Metrics = Measurements(shape, mapped) };
    }

    private bool IsSimilarity(out double scale, out double turn)
    {
        scale = 0;
        turn = 0;

        if (_linear is not { } m)
            return false;

        // A turn and an even scale: [s cos, -s sin; s sin, s cos].
        if (Math.Abs(m.A - m.D) > 1e-9 || Math.Abs(m.B + m.C) > 1e-9)
            return false;

        scale = Math.Sqrt((m.A * m.A) + (m.C * m.C));
        turn = Math.Atan2(m.C, m.A) * 180 / Math.PI;
        return scale > 0;
    }

    private bool IsAxisScale(out double sx, out double sy)
    {
        sx = 0;
        sy = 0;

        if (_linear is not { } m || Math.Abs(m.B) > 1e-12 || Math.Abs(m.C) > 1e-12)
            return false;

        sx = m.A;
        sy = m.D;
        return sx > 0 && sy > 0;
    }

    private IEnumerable<Shape> Geometry(Shape shape)
    {
        var similar = IsSimilarity(out var scale, out var turn);
        var axis = IsAxisScale(out var sx, out var sy);

        switch (shape)
        {
            case PointShape point:
                return _point(point.At) is { } at ? [point with { At = at }] : [];

            case LineShape line when _linear is not null:
                return [line with { Start = _point(line.Start)!.Value, End = _point(line.End)!.Value }];

            case CircleShape circle when similar:
                return [circle with { Centre = _point(circle.Centre)!.Value, Radius = circle.Radius * scale }];

            case CircleShape circle when axis:
                return
                [
                    new EllipseShape(
                        _point(circle.Centre)!.Value,
                        2 * circle.Radius * Math.Max(sx, sy),
                        2 * circle.Radius * Math.Min(sx, sy),
                        sx >= sy ? 0 : 90) with
                    {
                        Colour = circle.Colour,
                        StrokeWidth = circle.StrokeWidth,
                        Label = circle.Label,
                    },
                ];

            case EllipseShape ellipse when similar:
                return
                [
                    ellipse with
                    {
                        Centre = _point(ellipse.Centre)!.Value,
                        MajorAxis = ellipse.MajorAxis * scale,
                        MinorAxis = ellipse.MinorAxis * scale,
                        Angle = ellipse.Angle + turn,
                    },
                ];

            case OrientedRectShape rect when similar:
                return
                [
                    rect with
                    {
                        Centre = _point(rect.Centre)!.Value,
                        Width = rect.Width * scale,
                        Height = rect.Height * scale,
                        AngleDegrees = rect.AngleDegrees + turn,
                    },
                ];

            case RectShape rect when axis:
            {
                var corner = _point(new Point2(rect.Rect.X, rect.Rect.Y))!.Value;
                return [rect with { Rect = new Bounds(corner.X, corner.Y, rect.Rect.Width * sx, rect.Rect.Height * sy) }];
            }

            case RectShape rect when similar:
                return
                [
                    new OrientedRectShape(_point(rect.Rect.Centre)!.Value, rect.Rect.Width * scale, rect.Rect.Height * scale, turn)
                    {
                        Colour = rect.Colour,
                        StrokeWidth = rect.StrokeWidth,
                        Label = rect.Label,
                    },
                ];

            case PolygonShape polygon:
                return Traced(shape, polygon.Points, polygon.Closed);

            default:
                // An outline traced round it, and carried point by point.
                return Traced(shape, ShapeGeometry.Outline(shape), closed: shape is not LineShape);
        }
    }

    /// <summary>
    /// An outline carried point by point - densified first when the map
    /// bends straight lines, and cut where it crosses a seam or leaves the
    /// output.
    /// </summary>
    private IEnumerable<Shape> Traced(Shape original, IReadOnlyList<Point2> points, bool closed)
    {
        var source = _linear is null ? Densify(points, closed) : points;
        var runs = new List<List<Point2>> { new() };

        foreach (var point in source)
        {
            if (_point(point) is not { } mapped)
            {
                if (runs[^1].Count > 0)
                    runs.Add([]);

                continue;
            }

            var run = runs[^1];

            if (_seam > 0 && run.Count > 0 && Math.Abs(mapped.X - run[^1].X) > _seam / 2)
            {
                run = [];
                runs.Add(run);
            }

            run.Add(mapped);
        }

        runs.RemoveAll(run => run.Count < 2);

        // Unbroken, a closed outline stays closed - unless closing it would
        // cross the seam, as a ring round the centre of an unwrapping does.
        // Broken, its last piece and its first are one stretch that
        // happened to start mid-way.
        var whole = runs.Count == 1 && runs[0].Count == source.Count &&
                    !(_seam > 0 && Math.Abs(runs[0][^1].X - runs[0][0].X) > _seam / 2);

        if (!whole && closed && runs.Count > 1)
        {
            var joined = runs[^1].Concat(runs[0]).ToList();
            var gap = _seam > 0 && Math.Abs(runs[^1][^1].X - runs[0][0].X) > _seam / 2;

            if (!gap && _point(source[^1]) is not null && _point(source[0]) is not null)
            {
                runs[0] = joined;
                runs.RemoveAt(runs.Count - 1);
            }
        }

        return runs.Select(run => (Shape)new PolygonShape(run.ToArray(), whole && closed)
        {
            Colour = original.Colour,
            StrokeWidth = original.StrokeWidth,
            Label = original.Label,
        });
    }

    /// <summary>Points at most two pixels apart along every edge, so a bending map bends them smoothly.</summary>
    private static IReadOnlyList<Point2> Densify(IReadOnlyList<Point2> points, bool closed)
    {
        if (points.Count < 2)
            return points;

        var dense = new List<Point2>(points.Count * 2);
        var count = closed ? points.Count : points.Count - 1;

        for (var i = 0; i < count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            var steps = Math.Max(1, (int)Math.Ceiling(a.DistanceTo(b) / 2));

            for (var s = 0; s < steps; s++)
            {
                var t = (double)s / steps;
                dense.Add(new Point2(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t)));
            }
        }

        if (!closed)
            dense.Add(points[^1]);

        return dense;
    }

    /// <summary>The original's measurements, re-read or converted for where the shape now is.</summary>
    private IReadOnlyDictionary<string, double> Measurements(Shape original, Shape mapped)
    {
        if (original.Metrics.Count == 0)
            return original.Metrics;

        var similar = IsSimilarity(out var scale, out var turn);
        var area = _linear is { } m ? Math.Abs((m.A * m.D) - (m.B * m.C)) : double.NaN;

        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var (key, value) in original.Metrics)
        {
            if (Positions.Contains(key))
                continue;

            if (key == ShapeMetric.Area)
            {
                if (!double.IsNaN(area))
                    metrics[key] = value * area;
            }
            else if (Lengths.Contains(key))
            {
                if (similar)
                    metrics[key] = value * scale;
            }
            else if (Angles.Contains(key))
            {
                if (similar)
                    metrics[key] = value + turn;
            }
            else if (key == ShapeMetric.AspectRatio)
            {
                if (similar)
                    metrics[key] = value;
            }
            else
            {
                metrics[key] = value;
            }
        }

        if (original.Metrics.ContainsKey(ShapeMetric.CentroidX) || original.Metrics.ContainsKey(ShapeMetric.CentroidY))
        {
            var centre = original.Metrics.TryGetValue(ShapeMetric.CentroidX, out var x) &&
                         original.Metrics.TryGetValue(ShapeMetric.CentroidY, out var y)
                ? _point(new Point2(x, y)) ?? ShapeGeometry.Centre(mapped)
                : ShapeGeometry.Centre(mapped);

            metrics[ShapeMetric.CentroidX] = centre.X;
            metrics[ShapeMetric.CentroidY] = centre.Y;
        }

        if (original.Metrics.ContainsKey(ShapeMetric.BoundsX))
        {
            var bounds = mapped.Bounds;
            metrics[ShapeMetric.BoundsX] = bounds.X;
            metrics[ShapeMetric.BoundsY] = bounds.Y;
            metrics[ShapeMetric.BoundsWidth] = bounds.Width;
            metrics[ShapeMetric.BoundsHeight] = bounds.Height;
        }

        return metrics;
    }
}
