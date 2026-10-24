using Sirius.Core.Shapes;

namespace Sirius.Vision;

/// <summary>A circle fitted to a set of points, and how well it fitted.</summary>
/// <param name="Centre">Fitted centre, or the default when the fit failed.</param>
/// <param name="Radius">Fitted radius, or NaN when the fit failed.</param>
/// <param name="Error">Root-mean-square distance from the points to the circle.</param>
/// <param name="Used">The points the fit was taken over, after any rejection.</param>
public readonly record struct CircleFitResult(
    Point2 Centre,
    double Radius,
    double Error,
    IReadOnlyList<Point2> Used)
{
    public bool Success => !double.IsNaN(Radius) && Radius > 0;

    public CircleShape ToShape() => new(Centre, Radius);
}

/// <summary>
/// Fits a circle to points.
/// </summary>
/// <remarks>
/// Shared by Fit Circle, which takes the outline of a blob, and Find
/// Circle, which takes the edge points its calipers found. One
/// implementation because two would drift, and a circle fitted by one tool
/// should agree to the last digit with the same circle fitted by the other.
/// </remarks>
public static class CircleFit
{
    /// <summary>The fewest points that determine a circle.</summary>
    public const int MinimumPoints = 3;

    /// <summary>
    /// Fits once, over every point given.
    /// </summary>
    public static CircleFitResult Fit(IReadOnlyList<Point2> points)
    {
        if (points.Count < MinimumPoints)
            return new CircleFitResult(default, double.NaN, double.NaN, points);

        var (centre, radius) = LeastSquares(points);

        return double.IsNaN(radius)
            ? new CircleFitResult(default, double.NaN, double.NaN, points)
            : new CircleFitResult(centre, radius, Residual(points, centre, radius), points);
    }

    /// <summary>
    /// Fits, discards the points furthest from the result, and fits again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A caliper that crossed a scratch, a reflection or the neighbouring
    /// feature reports an edge that is real and in the wrong place. One
    /// such point drags an algebraic fit noticeably, because it minimises
    /// the square of the error and an outlier's error is large.
    /// </para>
    /// <para>
    /// Sigma clipping rather than RANSAC: the points are mostly good and
    /// mostly already on the circle, which is the case iterative rejection
    /// handles well and the case RANSAC's random sampling is wasted on.
    /// </para>
    /// </remarks>
    /// <param name="sigma">
    /// How many standard deviations from the fitted radius a point may be
    /// before it is dropped. Zero or less rejects nothing.
    /// </param>
    /// <param name="passes">How many times to re-fit after rejecting.</param>
    public static CircleFitResult FitRobust(
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
            var deviations = kept
                .Select(p => Math.Abs(Distance(p, result.Centre) - result.Radius))
                .ToArray();

            var mean = deviations.Average();
            var spread = Math.Sqrt(deviations.Average(d => (d - mean) * (d - mean)));

            // Everything already agrees; another pass would only chip away
            // at points that are not outliers.
            if (spread < 1e-9)
                break;

            var limit = mean + (sigma * spread);
            var survivors = kept.Where((_, i) => deviations[i] <= limit).ToArray();

            // Stop rather than fit through too little: a rejection that
            // leaves two points would "succeed" on a circle through
            // anything.
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

    /// <summary>Root-mean-square distance from the points to the circle.</summary>
    public static double Residual(IReadOnlyList<Point2> points, Point2 centre, double radius)
    {
        if (points.Count == 0)
            return double.NaN;

        var total = 0.0;

        foreach (var point in points)
        {
            var error = Distance(point, centre) - radius;
            total += error * error;
        }

        return Math.Sqrt(total / points.Count);
    }

    private static double Distance(Point2 a, Point2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;

        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// Kasa's algebraic circle fit.
    /// </summary>
    /// <remarks>
    /// Minimising the algebraic rather than the geometric residual, which
    /// makes it a linear solve in three unknowns instead of an iteration.
    /// It biases slightly small on a short arc; on a near-complete ring of
    /// caliper hits, or the outline a blob produces, the difference is well
    /// under the pixel noise it is fitting through.
    /// </remarks>
    /// <summary>
    /// The bare algebraic fit, for a caller that does its own residual and
    /// rejection - Fit Circle, which fits each blob outline separately and
    /// reports per-shape metrics.
    /// </summary>
    public static (Point2 Centre, double Radius) FitForNode(IReadOnlyList<Point2> points) =>
        LeastSquares(points);

    private static (Point2 Centre, double Radius) LeastSquares(IReadOnlyList<Point2> points)
    {
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0, sz = 0;
        var n = points.Count;

        foreach (var point in points)
        {
            var z = (point.X * point.X) + (point.Y * point.Y);

            sx += point.X;
            sy += point.Y;
            sxx += point.X * point.X;
            syy += point.Y * point.Y;
            sxy += point.X * point.Y;
            sxz += point.X * z;
            syz += point.Y * z;
            sz += z;
        }

        // The normal equations, solved by Cramer's rule.
        var a11 = 2 * ((sx * sx) - (n * sxx));
        var a12 = 2 * ((sx * sy) - (n * sxy));
        var a22 = 2 * ((sy * sy) - (n * syy));
        var b1 = (sx * sz) - (n * sxz);
        var b2 = (sy * sz) - (n * syz);

        var determinant = (a11 * a22) - (a12 * a12);

        if (Math.Abs(determinant) < 1e-9)
            return (default, double.NaN);

        var cx = ((b1 * a22) - (b2 * a12)) / determinant;
        var cy = ((a11 * b2) - (a12 * b1)) / determinant;

        var radius = Math.Sqrt(
            (((sxx + syy) - (2 * cx * sx) - (2 * cy * sy)) / n) + (cx * cx) + (cy * cy));

        return (new Point2(cx, cy), radius);
    }
}
