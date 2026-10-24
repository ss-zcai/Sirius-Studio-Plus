using Sirius.Core.Shapes;

namespace Sirius.Vision;

/// <summary>A line fitted to a set of points, and how well it fitted.</summary>
/// <param name="Point">A point the line passes through - the centroid of the fit.</param>
/// <param name="DirectionX">Unit direction, X.</param>
/// <param name="DirectionY">Unit direction, Y.</param>
/// <param name="Error">Root-mean-square perpendicular distance to the line.</param>
/// <param name="Used">The points fitted, after any rejection.</param>
public readonly record struct LineFitResult(
    Point2 Point,
    double DirectionX,
    double DirectionY,
    double Error,
    IReadOnlyList<Point2> Used)
{
    public bool Success => !double.IsNaN(Error) && Used.Count >= LineFit.MinimumPoints;

    /// <summary>Degrees, measured the way the rest of the library measures them.</summary>
    public double AngleDegrees => Math.Atan2(DirectionY, DirectionX) * 180.0 / Math.PI;

    /// <summary>Perpendicular distance from a point to this line.</summary>
    public double DistanceTo(Point2 point) =>
        Math.Abs(((point.X - Point.X) * -DirectionY) + ((point.Y - Point.Y) * DirectionX));

    /// <summary>
    /// The segment covering the points that were fitted, rather than a line
    /// running off the frame.
    /// </summary>
    public LineShape ToShape()
    {
        // Copied out first: a lambda inside a struct cannot capture `this`.
        double ox = Point.X, oy = Point.Y, dx = DirectionX, dy = DirectionY;

        var projections = Used
            .Select(p => ((p.X - ox) * dx) + ((p.Y - oy) * dy))
            .ToArray();

        var low = projections.Length == 0 ? 0 : projections.Min();
        var high = projections.Length == 0 ? 0 : projections.Max();

        return new LineShape(
            new Point2(Point.X + (low * DirectionX), Point.Y + (low * DirectionY)),
            new Point2(Point.X + (high * DirectionX), Point.Y + (high * DirectionY)));
    }
}

/// <summary>
/// Fits a line to points, minimising perpendicular distance.
/// </summary>
/// <remarks>
/// <para>
/// Total least squares, by the eigenvector of the scatter matrix. Not the
/// ordinary <c>y = mx + c</c> regression, which minimises vertical error
/// and cannot represent a vertical line at all - and a caliper line finder
/// aimed at a vertical edge is not an edge case, it is half of all uses.
/// </para>
/// <para>
/// Shared with the rejection pattern <see cref="CircleFit"/> uses, for the
/// same reason: one implementation, so a line fitted by one tool agrees
/// with the same line fitted by another.
/// </para>
/// </remarks>
public static class LineFit
{
    /// <summary>The fewest points that determine a line.</summary>
    public const int MinimumPoints = 2;

    public static LineFitResult Fit(IReadOnlyList<Point2> points)
    {
        if (points.Count < MinimumPoints)
            return new LineFitResult(default, 0, 0, double.NaN, points);

        double sx = 0, sy = 0;

        foreach (var point in points)
        {
            sx += point.X;
            sy += point.Y;
        }

        var centroid = new Point2(sx / points.Count, sy / points.Count);

        double xx = 0, yy = 0, xy = 0;

        foreach (var point in points)
        {
            var dx = point.X - centroid.X;
            var dy = point.Y - centroid.Y;

            xx += dx * dx;
            yy += dy * dy;
            xy += dx * dy;
        }

        // Principal eigenvector of [[xx, xy], [xy, yy]] - the direction the
        // points vary along most, which is the line.
        var theta = 0.5 * Math.Atan2(2 * xy, xx - yy);
        var dirX = Math.Cos(theta);
        var dirY = Math.Sin(theta);

        if (xx + yy < 1e-12)
            return new LineFitResult(centroid, 0, 0, double.NaN, points);

        var result = new LineFitResult(centroid, dirX, dirY, 0, points);

        return result with { Error = Residual(points, result) };
    }

    /// <summary>
    /// Fits, drops the points furthest from the result, and fits again.
    /// </summary>
    /// <remarks>
    /// A caliper that crossed a scratch or the neighbouring feature reports
    /// an edge that is real and in the wrong place. Sigma clipping rather
    /// than RANSAC, for the same reason as the circle: the points are
    /// mostly good and mostly already on the line.
    /// </remarks>
    public static LineFitResult FitRobust(
        IReadOnlyList<Point2> points,
        double sigma = 2.5,
        int passes = 3)
    {
        var result = Fit(points);

        if (!result.Success || sigma <= 0)
            return result;

        var kept = points;

        for (var pass = 0; pass < passes; pass++)
        {
            var deviations = kept.Select(result.DistanceTo).ToArray();
            var mean = deviations.Average();
            var spread = Math.Sqrt(deviations.Average(d => (d - mean) * (d - mean)));

            if (spread < 1e-9)
                break;

            var limit = mean + (sigma * spread);
            var survivors = kept.Where((_, i) => deviations[i] <= limit).ToArray();

            if (survivors.Length == kept.Count || survivors.Length < MinimumPoints)
                break;

            var next = Fit(survivors);

            if (!next.Success)
                break;

            kept = survivors;
            result = next;
        }

        return result;
    }

    private static double Residual(IReadOnlyList<Point2> points, LineFitResult line)
    {
        if (points.Count == 0)
            return double.NaN;

        var total = 0.0;

        foreach (var point in points)
        {
            var distance = line.DistanceTo(point);
            total += distance * distance;
        }

        return Math.Sqrt(total / points.Count);
    }
}
