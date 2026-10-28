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
