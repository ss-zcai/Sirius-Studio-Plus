using System.Drawing;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Vision.Nodes;

namespace Sirius.Vision;

/// <summary>
/// One edge a caliper found.
/// </summary>
/// <param name="At">Where, in image coordinates.</param>
/// <param name="Strength">The intensity step, signed: positive is dark to light along the scan.</param>
/// <param name="Caliper">Which caliper found it, counting from zero.</param>
/// <param name="Along">
/// Its place along the row: degrees clockwise round a ring, or pixels from
/// the start of a rectangle's long side.
/// </param>
/// <param name="Across">
/// Its place across the band: the distance from a ring's centre, or the
/// signed distance from a rectangle's middle line.
/// </param>
public readonly record struct EdgePoint(Point2 At, double Strength, int Caliper, double Along, double Across);

/// <summary>
/// Where one caliper is cast along a path: a point on the path, the
/// direction across it, and how far along the path the point is.
/// </summary>
/// <param name="At">The point on the path the caliper is centred on.</param>
/// <param name="NormalX">Unit direction across the path, X - the path's direction turned a quarter clockwise.</param>
/// <param name="NormalY">Unit direction across the path, Y.</param>
/// <param name="Along">Distance along the path from its first point, in pixels.</param>
public readonly record struct PathStation(Point2 At, double NormalX, double NormalY, double Along)
{
    /// <summary>The point <paramref name="across"/> pixels across the path from this station.</summary>
    public Point2 Across(double across) => new(At.X + (NormalX * across), At.Y + (NormalY * across));
}

/// <summary>
/// How the calipers read an edge - everything but where they are cast.
/// </summary>
public readonly record struct CaliperSettings(
    int Count,
    int Width,
    double Spacing,
    int Smoothing,
    double MinimumContrast,
    EdgePolarity Polarity,
    EdgeChoice Choice);

/// <summary>
/// Casts calipers round a ring or across a rectangle and collects the edges
/// they find.
/// </summary>
/// <remarks>
/// <para>
/// Shared by Find Circle, Find Line, Ring Calipers and Line Calipers, which
/// differ in what they do with the edges and not at all in how they find
/// them. One implementation, because two would drift: an edge found by
/// Ring Calipers should be the very edge Find Circle would have fitted to,
/// so that switching between them changes what is done with the points
/// rather than the points.
/// </para>
/// <para>
/// Edges come back in the order the calipers were cast, which is the order
/// round the ring or along the row - the order a contour through them
/// needs. A caliper that finds nothing leaves a gap rather than a guess.
/// </para>
/// </remarks>
public static class CaliperScan
{
    /// <summary>Radial calipers through a ring.</summary>
    public static List<EdgePoint> AroundRing(
        LineProfileSource sampler,
        Ring band,
        double startAngle,
        double sweepAngle,
        RadialDirection direction,
        CaliperSettings settings,
        CancellationToken cancellationToken)
    {
        var edges = new List<EdgePoint>(settings.Count);

        var step = RingStep(sweepAngle, settings.Count);

        var (fromRadius, toRadius) = direction == RadialDirection.Outward
            ? (band.InnerRadius, band.OuterRadius)
            : (band.OuterRadius, band.InnerRadius);

        for (var i = 0; i < settings.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var degrees = startAngle + (i * step);
            var radians = degrees * Math.PI / 180.0;
            var ux = Math.Cos(radians);
            var uy = Math.Sin(radians);

            var start = new Point2(band.CentreX + (ux * fromRadius), band.CentreY + (uy * fromRadius));
            var end = new Point2(band.CentreX + (ux * toRadius), band.CentreY + (uy * toRadius));

            if (!TryRead(sampler, start, end, settings, cancellationToken, out var hit))
                continue;

            var radius = direction == RadialDirection.Outward
                ? fromRadius + hit.Offset
                : fromRadius - hit.Offset;

            edges.Add(new EdgePoint(
                new Point2(band.CentreX + (ux * radius), band.CentreY + (uy * radius)),
                hit.Strength,
                i,
                Normalise(degrees),
                radius));
        }

        return edges;
    }

    /// <summary>
    /// Calipers spaced along a rectangle's long side, each crossing its width.
    /// </summary>
    /// <remarks>
    /// Everything is worked out in the rectangle's own frame and rotated out
    /// at the end, which keeps the rotation in one place - the lesson from
    /// the oriented ROI, where it was spread across three calculations and
    /// wrong in two.
    /// </remarks>
    public static List<EdgePoint> AcrossRect(
        LineProfileSource sampler,
        OrientedRect region,
        ScanDirection direction,
        CaliperSettings settings,
        CancellationToken cancellationToken)
    {
        var edges = new List<EdgePoint>(settings.Count);

        var radians = region.Angle * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        var halfLength = region.Width / 2;
        var halfWidth = region.Height / 2;

        var step = settings.Count > 1 ? region.Width / (settings.Count - 1) : 0;

        var (fromAcross, toAcross) = direction == ScanDirection.Across
            ? (-halfWidth, halfWidth)
            : (halfWidth, -halfWidth);

        for (var i = 0; i < settings.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var along = settings.Count > 1 ? -halfLength + (i * step) : 0;

            var start = InRect(region, cos, sin, along, fromAcross);
            var end = InRect(region, cos, sin, along, toAcross);

            if (!TryRead(sampler, start, end, settings, cancellationToken, out var hit))
                continue;

            var across = direction == ScanDirection.Across
                ? fromAcross + hit.Offset
                : fromAcross - hit.Offset;

            edges.Add(new EdgePoint(
                InRect(region, cos, sin, along, across),
                hit.Strength,
                i,
                along + halfLength,
                across));
        }

        return edges;
    }

    /// <summary>
    /// Where <paramref name="count"/> calipers fall along a path, evenly by
    /// distance along it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An open path has one at each end; a closed one divides its length by
    /// the count, as a full ring does, so the last does not land on the
    /// first.
    /// </para>
    /// <para>
    /// Each caliper crosses the path square to the chord either side of it
    /// rather than to the side it sits on, so one at a corner bisects the
    /// corner instead of taking whichever side it happened to fall on. The
    /// chord spans a caliper's spacing, which is what the calipers can
    /// resolve anyway.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<PathStation> Stations(PathBand path, int count)
    {
        var length = path.Length;

        if (length <= 0 || count < 1)
            return [];

        var step = path.Closed ? length / count : length / Math.Max(1, count - 1);
        var reach = Math.Max(0.5, step / 2);
        var stations = new PathStation[count];

        for (var i = 0; i < count; i++)
        {
            var along = count == 1 && !path.Closed ? length / 2 : i * step;

            var behind = PointAt(path, path.Closed ? along - reach : Math.Max(0, along - reach));
            var ahead = PointAt(path, path.Closed ? along + reach : Math.Min(length, along + reach));

            var dx = ahead.X - behind.X;
            var dy = ahead.Y - behind.Y;
            var chord = Math.Sqrt((dx * dx) + (dy * dy));

            var (nx, ny) = chord < 1e-12 ? (0.0, 1.0) : (-dy / chord, dx / chord);

            stations[i] = new PathStation(PointAt(path, along), nx, ny, along);
        }

        return stations;
    }

    /// <summary>The point <paramref name="along"/> pixels along a path, round again when it is closed.</summary>
    public static Point2 PointAt(PathBand path, double along)
    {
        var points = path.Points;
        var length = path.Length;

        if (points.Count == 0)
            return default;

        if (path.Closed && length > 0)
            along = ((along % length) + length) % length;

        var sides = path.Closed ? points.Count : points.Count - 1;

        for (var i = 0; i < sides; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            var side = a.DistanceTo(b);

            if (along <= side || i == sides - 1)
            {
                var t = side < 1e-12 ? 0 : Math.Clamp(along / side, 0, 1);
                return new Point2(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));
            }

            along -= side;
        }

        return points[^1];
    }

    /// <summary>
    /// Calipers spaced along a path, each crossing it, the band's width wide.
    /// </summary>
    /// <param name="direction">
    /// Across scans from the side the path's direction turned a quarter
    /// anticlockwise points to - on the image, the left of the path as it
    /// is drawn - to the other.
    /// </param>
    public static List<EdgePoint> AlongPath(
        LineProfileSource sampler,
        PathBand path,
        IReadOnlyList<PathStation> stations,
        ScanDirection direction,
        CaliperSettings settings,
        CancellationToken cancellationToken)
    {
        var edges = new List<EdgePoint>(stations.Count);
        var half = path.HalfWidth;

        var (fromAcross, toAcross) = direction == ScanDirection.Across ? (-half, half) : (half, -half);

        for (var i = 0; i < stations.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var station = stations[i];

            if (!TryRead(sampler, station.Across(fromAcross), station.Across(toAcross), settings, cancellationToken, out var hit))
                continue;

            var across = direction == ScanDirection.Across ? fromAcross + hit.Offset : fromAcross - hit.Offset;

            edges.Add(new EdgePoint(station.Across(across), hit.Strength, i, station.Along, across));
        }

        return edges;
    }

    /// <summary>Whether a sweep goes all the way round, so the first and last calipers meet.</summary>
    public static bool IsFullCircle(double sweepAngle) => sweepAngle >= 359.999;

    /// <summary>
    /// Degrees between neighbouring calipers round a ring.
    /// </summary>
    /// <remarks>
    /// The last caliper lands on the first when the sweep is a full circle,
    /// so the sweep is divided by the count rather than one less - two
    /// readings of one angle would weight that part of the edge twice.
    /// </remarks>
    public static double RingStep(double sweepAngle, int count) =>
        sweepAngle / (IsFullCircle(sweepAngle) ? count : Math.Max(1, count - 1));

    /// <summary>
    /// An edge from a ring, as a point carrying what was measured: where it
    /// is, its angle round the ring, its distance from the centre and how
    /// sharp it was.
    /// </summary>
    public static PointShape RingPoint(EdgePoint edge, uint colour) =>
        new(edge.At)
        {
            Colour = colour,
            Metrics = new Dictionary<string, double>
            {
                [ShapeMetric.CentroidX] = edge.At.X,
                [ShapeMetric.CentroidY] = edge.At.Y,
                [ShapeMetric.Angle] = edge.Along,
                [ShapeMetric.Radius] = edge.Across,
                [ShapeMetric.Contrast] = Math.Abs(edge.Strength),
            },
        };

    /// <summary>
    /// An edge from a ring, measured against a reference circle: its radius
    /// from that circle's centre, and its deviation from that circle.
    /// </summary>
    /// <remarks>
    /// The reference is what makes the radius mean something. Measured from
    /// wherever a band happened to be drawn, a perfectly round edge rises and
    /// falls once round the ring by however far off-centre the band was;
    /// measured from a circle fitted to the edge, what is left is the edge's
    /// own departure from round.
    /// </remarks>
    public static PointShape RingPoint(EdgePoint edge, uint colour, Point2 centre, double referenceRadius)
    {
        var radius = edge.At.DistanceTo(centre);

        return new PointShape(edge.At)
        {
            Colour = colour,
            Metrics = new Dictionary<string, double>
            {
                [ShapeMetric.CentroidX] = edge.At.X,
                [ShapeMetric.CentroidY] = edge.At.Y,
                [ShapeMetric.Angle] = edge.Along,
                [ShapeMetric.Radius] = radius,
                [ShapeMetric.Deviation] = radius - referenceRadius,
                [ShapeMetric.Contrast] = Math.Abs(edge.Strength),
            },
        };
    }

    /// <summary>
    /// An edge from a rectangle, as a point carrying where it is, how far
    /// along the row, how far off the middle line and how sharp it was.
    /// </summary>
    public static PointShape LinePoint(EdgePoint edge, uint colour) =>
        new(edge.At)
        {
            Colour = colour,
            Metrics = new Dictionary<string, double>
            {
                [ShapeMetric.CentroidX] = edge.At.X,
                [ShapeMetric.CentroidY] = edge.At.Y,
                [ShapeMetric.Along] = edge.Along,
                [ShapeMetric.Offset] = edge.Across,
                [ShapeMetric.Contrast] = Math.Abs(edge.Strength),
            },
        };

    /// <summary>
    /// Each edge's distance off <paramref name="line"/>, signed the way
    /// its offset is: positive towards the side of the region the
    /// calipers' Across direction ends on.
    /// </summary>
    /// <remarks>
    /// A fitted line's direction comes out either way round, so the sign is
    /// taken from the region rather than the line - otherwise a burr would
    /// read positive on one image and negative on the next.
    /// </remarks>
    public static double[] Deviations(IReadOnlyList<EdgePoint> edges, LineFitResult line, OrientedRect region)
    {
        if (!line.Success)
            return new double[edges.Count];

        var radians = region.Angle * Math.PI / 180.0;
        var (acrossX, acrossY) = (-Math.Sin(radians), Math.Cos(radians));

        var (nx, ny) = (-line.DirectionY, line.DirectionX);

        if ((nx * acrossX) + (ny * acrossY) < 0)
            (nx, ny) = (-nx, -ny);

        return edges
            .Select(edge => ((edge.At.X - line.Point.X) * nx) + ((edge.At.Y - line.Point.Y) * ny))
            .ToArray();
    }

    private static bool TryRead(
        LineProfileSource sampler,
        Point2 start,
        Point2 end,
        CaliperSettings settings,
        CancellationToken cancellationToken,
        out CaliperHit hit)
    {
        hit = default;

        var profile = sampler.Sample(
            new PointF((float)start.X, (float)start.Y),
            new PointF((float)end.X, (float)end.Y),
            settings.Spacing,
            settings.Width,
            ProfileInterpolation.Linear,
            warn: null,
            cancellationToken);

        if (profile.Channels.Count == 0)
            return false;

        // One channel: a colour frame is read as its first channel rather
        // than averaged, because averaging can cancel an edge that is strong
        // in one channel and reversed in another.
        return Caliper.TryFind(
            profile.Channels[0].Samples,
            settings.Spacing,
            settings.Polarity,
            settings.MinimumContrast,
            settings.Smoothing,
            settings.Choice,
            out hit);
    }

    /// <summary>
    /// A point given in a rectangle's own frame - <paramref name="along"/>
    /// its long axis from its centre, <paramref name="across"/> its short
    /// one - in image coordinates.
    /// </summary>
    public static Point2 InRect(OrientedRect region, double along, double across)
    {
        var radians = region.Angle * Math.PI / 180.0;
        return InRect(region, Math.Cos(radians), Math.Sin(radians), along, across);
    }

    /// <summary>A point in a rectangle's own frame, in image coordinates.</summary>
    private static Point2 InRect(OrientedRect region, double cos, double sin, double along, double across) =>
        new(
            region.CentreX + (along * cos) - (across * sin),
            region.CentreY + (along * sin) + (across * cos));

    /// <summary>An angle in [0, 360), so the same direction always reads the same.</summary>
    private static double Normalise(double degrees)
    {
        var wrapped = degrees % 360;
        return wrapped < 0 ? wrapped + 360 : wrapped;
    }
}
