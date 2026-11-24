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

    /// <summary>
    /// Fits the circle that the most points agree with, and then fits it
    /// properly through those points alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RANSAC: a circle through three points picked at random, counted by
    /// how many of all the points lie within <paramref name="threshold"/>
    /// of it, many times over; the best of those is refined by least
    /// squares over the points that agree with it.
    /// </para>
    /// <para>
    /// For an edge with a defect in it, which is the case
    /// <see cref="FitRobust"/> is not built for. A notch or a burr covering
    /// a tenth of the edge is not a few stray points but a run of them, and
    /// it drags a least-squares fit towards itself before any rejection
    /// starts - so the defect is measured against a circle that already
    /// leans into it, and reads shallower than it is. Here the defect
    /// simply loses the vote, up to nearly half the edge.
    /// </para>
    /// <para>
    /// The draws come from a fixed seed, so one image fits to one circle
    /// every time - an inspection that passed a part once and failed it
    /// the next run, on the same picture, would be worse than useless.
    /// </para>
    /// </remarks>
    /// <param name="threshold">How far from a circle a point may be and still agree with it, in pixels.</param>
    /// <param name="iterations">How many three-point circles to try.</param>
    public static CircleFitResult FitConsensus(
        IReadOnlyList<Point2> points,
        double threshold,
        int iterations = 300)
    {
        if (points.Count < MinimumPoints)
            return new CircleFitResult(default, double.NaN, double.NaN, points);

        threshold = Math.Max(threshold, 1e-6);

        var random = new Random(20261030);
        var bestCount = -1;
        var bestError = double.MaxValue;
        (Point2 Centre, double Radius)? best = null;

        for (var i = 0; i < iterations; i++)
        {
            var a = random.Next(points.Count);
            var b = random.Next(points.Count);
            var c = random.Next(points.Count);

            if (a == b || b == c || a == c)
                continue;

            if (Through(points[a], points[b], points[c]) is not { } candidate)
                continue;

            var count = 0;
            var error = 0.0;

            foreach (var point in points)
            {
                var off = Math.Abs(Distance(point, candidate.Centre) - candidate.Radius);

                if (off > threshold)
                    continue;

                count++;
                error += off;
            }

            if (count > bestCount || (count == bestCount && error < bestError))
            {
                bestCount = count;
                bestError = error;
                best = candidate;
            }
        }

        // Too few points, or all of them in a line: the plain fit says so.
        if (best is not { } chosen)
            return Fit(points);

        // Refined twice: once over the points the winning triple agreed
        // with, and again over those the refined circle agrees with, which
        // is the circle the points actually describe rather than the one
        // three of them happened to.
        var result = new CircleFitResult(chosen.Centre, chosen.Radius, double.NaN, points);

        for (var pass = 0; pass < 2; pass++)
        {
            var inliers = points
                .Where(point => Math.Abs(Distance(point, result.Centre) - result.Radius) <= threshold)
                .ToArray();

            if (inliers.Length < MinimumPoints)
                break;

            var refined = Fit(inliers);

            if (!refined.Success)
                break;

            result = refined;
        }

        return result.Success && !double.IsNaN(result.Error) ? result : Fit(points);
    }

    /// <summary>The circle through three points, or null when they lie on a line.</summary>
    private static (Point2 Centre, double Radius)? Through(Point2 a, Point2 b, Point2 c)
    {
        var d = 2 * ((a.X * (b.Y - c.Y)) + (b.X * (c.Y - a.Y)) + (c.X * (a.Y - b.Y)));

        if (Math.Abs(d) < 1e-9)
            return null;

        var a2 = (a.X * a.X) + (a.Y * a.Y);
        var b2 = (b.X * b.X) + (b.Y * b.Y);
        var c2 = (c.X * c.X) + (c.Y * c.Y);

        var centre = new Point2(
            ((a2 * (b.Y - c.Y)) + (b2 * (c.Y - a.Y)) + (c2 * (a.Y - b.Y))) / d,
            ((a2 * (c.X - b.X)) + (b2 * (a.X - c.X)) + (c2 * (b.X - a.X))) / d);

        return (centre, Distance(a, centre));
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
