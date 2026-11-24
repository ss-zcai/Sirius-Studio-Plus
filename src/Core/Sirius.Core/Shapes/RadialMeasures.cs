namespace Sirius.Core.Shapes;

/// <summary>
/// How round an outline is, measured from its centroid: HALCON's
/// circularity and roundness.
/// </summary>
/// <remarks>
/// <para>
/// Shared by Find Blobs and by every closed outline the caliper tools and
/// Points to Polygon make, so a blob and a contour of the same shape read
/// the same.
/// </para>
/// <para>
/// HALCON measures over the outline's pixels, which lie about a pixel
/// apart. An outline here may be a handful of corners - Find Blobs keeps
/// only the corners of a straight run, a polygon only its points - so it is
/// walked a pixel at a time, which puts the distances along a straight
/// side, closer to the centre than its ends, into the mean as HALCON's
/// pixels would.
/// </para>
/// </remarks>
public static class RadialMeasures
{
    /// <summary>
    /// The radial circularity and roundness of the closed outline through
    /// <paramref name="outline"/>, about <paramref name="centre"/>.
    /// </summary>
    /// <param name="area">The area the outline encloses.</param>
    public static (double Circularity, double Roundness) Of(
        IReadOnlyList<Point2> outline, Point2 centre, double area)
    {
        ArgumentNullException.ThrowIfNull(outline);

        if (outline.Count < 3 || area <= 0)
            return (0, 0);

        var distances = new List<double>(outline.Count * 2);

        for (var i = 0; i < outline.Count; i++)
        {
            var a = outline[i];
            var b = outline[(i + 1) % outline.Count];
            var steps = Math.Max(1, (int)Math.Ceiling(a.DistanceTo(b)));

            // Each side from its start, up to but not including its end,
            // which is the next side's start.
            for (var s = 0; s < steps; s++)
            {
                var t = (double)s / steps;
                var point = new Point2(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));
                distances.Add(point.DistanceTo(centre));
            }
        }

        var furthest = distances.Max();
        var mean = distances.Average();
        var spread = Math.Sqrt(distances.Average(distance => (distance - mean) * (distance - mean)));

        // A pixel outline can enclose a touch more than the circle through
        // its furthest point; HALCON caps the ratio at one, and so does this.
        var circularity = furthest > 0 ? Math.Min(1, area / (Math.PI * furthest * furthest)) : 0;
        var roundness = mean > 0 ? 1 - (spread / mean) : 0;

        return (circularity, roundness);
    }

    /// <summary>
    /// The centroid of the area a closed polygon encloses - not the mean of
    /// its corners, which leans towards wherever they happen to be closer
    /// together.
    /// </summary>
    public static Point2 AreaCentroid(IReadOnlyList<Point2> outline)
    {
        ArgumentNullException.ThrowIfNull(outline);

        double twiceArea = 0, x = 0, y = 0;

        for (var i = 0; i < outline.Count; i++)
        {
            var a = outline[i];
            var b = outline[(i + 1) % outline.Count];
            var cross = (a.X * b.Y) - (b.X * a.Y);

            twiceArea += cross;
            x += (a.X + b.X) * cross;
            y += (a.Y + b.Y) * cross;
        }

        if (Math.Abs(twiceArea) < 1e-12)
            return new Point2(outline.Average(point => point.X), outline.Average(point => point.Y));

        return new Point2(x / (3 * twiceArea), y / (3 * twiceArea));
    }
}
