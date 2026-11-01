using Sirius.Vision.Nodes;

namespace Sirius.Vision;

/// <summary>
/// The shape an edge is meant to have, worked out from the edge itself.
/// </summary>
/// <remarks>
/// Shared by Circle Defects and Line Defects - which measure a notch
/// against it - and by Find Curve, which reports it as the curve. One
/// implementation, so an edge followed by one tool is the edge the others
/// follow.
/// </remarks>
public static class EdgeForm
{
    /// <summary>
    /// <paramref name="values"/> - one per caliper, NaN where none was
    /// found - smoothed over <paramref name="window"/> calipers, with what
    /// stands out from the result set aside and the smoothing done again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A running median first, which runs straight across anything narrower
    /// than half the window. Then a short curve - a quadratic over the
    /// window - through what does not stand out from it, which follows the
    /// slope and bend of the edge across a gap where a median would step.
    /// Each pass starts again from every value, so one set aside wrongly
    /// comes back.
    /// </para>
    /// <para>
    /// What stands out is anything further off than
    /// <paramref name="sigma"/> of the edge's own robust spreads, or than
    /// <paramref name="floor"/> if that is larger: a rough edge is not
    /// taken apart for being rough.
    /// </para>
    /// </remarks>
    /// <param name="wraps">Whether the last value sits beside the first - a closed edge.</param>
    /// <param name="bridge">
    /// Where too much has been set aside for the window to hold a curve,
    /// widen the window until it does, rather than keeping the median. For
    /// fitting an edge that curves: the median cuts a peak short, the peak's
    /// own points are set aside for disagreeing with it, and a window that
    /// cannot reach past them would leave the peak cut short for good. The
    /// defect tools leave it off, where a stretch set aside is a defect and
    /// the median across it is the form the part should have had.
    /// </param>
    public static double[] Follow(
        IReadOnlyList<double> values, int window, bool wraps, double floor, double sigma, int passes = 3, bool bridge = false)
    {
        var baseline = CircleDefectsNode.LocalForm(values, window, wraps);

        // A median at the end of an open edge is a median of one side, and
        // on a slope it is nowhere near the edge: start the ends from the
        // curve through the first window instead, or the first pass sets
        // the end points aside for disagreeing with it.
        if (bridge)
            baseline = Ends(values, window, wraps, baseline);

        if (sigma <= 0)
            return Ends(values, window, wraps, Curve(values, window, wraps, baseline, bridge));

        for (var pass = 0; pass < passes; pass++)
        {
            var residual = values
                .Select((value, i) => Math.Abs(value - baseline[i]))
                .Where(double.IsFinite)
                .ToArray();

            if (residual.Length == 0)
                break;

            var limit = Math.Max(floor, sigma * 1.4826 * CircleDefectsNode.Median(residual));

            var kept = values
                .Select((value, i) => double.IsFinite(value) && Math.Abs(value - baseline[i]) <= limit ? value : double.NaN)
                .ToArray();

            baseline = Ends(kept, window, wraps, Curve(kept, window, wraps, baseline, bridge));
        }

        return baseline;
    }

    /// <summary>
    /// The local curve - and, bridging, a wider window wherever the usual
    /// one fell back for want of points either side.
    /// </summary>
    private static double[] Curve(IReadOnlyList<double> values, int window, bool wraps, double[] fallback, bool bridge)
    {
        var curve = CircleDefectsNode.LocalCurve(values, window, wraps, fallback);

        if (!bridge)
            return curve;

        var count = values.Count;
        var half = window / 2;

        for (var i = 0; i < count; i++)
        {
            if (Supported(values, i, half, wraps))
                continue;

            // Wider by a caliper either side at a time, up to four windows -
            // past that the gap is no longer something to bridge.
            for (var reach = half + 1; reach <= 4 * half + 2; reach++)
            {
                if (!Supported(values, i, reach, wraps))
                    continue;

                if (QuadraticAround(values, i, reach, wraps) is { } fitted)
                    curve[i] = fitted;

                break;
            }
        }

        return curve;
    }

    /// <summary>Whether a window <paramref name="reach"/> either side of <paramref name="at"/> has points enough on both sides to hold a curve.</summary>
    private static bool Supported(IReadOnlyList<double> values, int at, int reach, bool wraps)
    {
        var (used, left, right) = (0, 0, 0);

        for (var j = at - reach; j <= at + reach; j++)
        {
            if (Index(values.Count, j, wraps) is not { } k || !double.IsFinite(values[k]))
                continue;

            used++;

            if (j < at)
                left++;
            else if (j > at)
                right++;
        }

        return used >= 6 && left > 0 && right > 0;
    }

    private static double? QuadraticAround(IReadOnlyList<double> values, int at, int reach, bool wraps)
    {
        var known = new List<(double X, double Y)>();

        for (var j = at - reach; j <= at + reach; j++)
        {
            if (Index(values.Count, j, wraps) is { } k && double.IsFinite(values[k]))
                known.Add((j - at, values[k]));
        }

        return Quadratic(known);
    }

    private static int? Index(int count, int j, bool wraps) =>
        wraps ? ((j % count) + count) % count : j >= 0 && j < count ? j : null;

    /// <summary>
    /// The first and last half-window of an open edge, fitted over the
    /// window held inside the edge rather than centred on each caliper.
    /// </summary>
    /// <remarks>
    /// A centred window at the end of an open edge has nothing on one side,
    /// and the local curve falls back to the running median there - which
    /// on a slope lags the edge by the slope across half a window. A curve
    /// through the first window's worth of points, read where each end
    /// caliper is, follows the edge to its end, as a fitted line does. The
    /// first window's worth that are known: where the end's own points have
    /// been set aside, it reaches further in for them.
    /// </remarks>
    private static double[] Ends(IReadOnlyList<double> values, int window, bool wraps, double[] curve)
    {
        var count = values.Count;
        var half = window / 2;

        if (wraps || count < window)
            return curve;

        var first = Known(values, Enumerable.Range(0, count), window);
        var last = Known(values, Enumerable.Range(0, count).Reverse(), window);

        for (var i = 0; i < count; i++)
        {
            if (i >= half && i < count - half)
                continue;

            var near = i < half ? first : last;
            var fitted = Quadratic(near.Select(j => ((double)(j - i), values[j])).ToList());

            if (fitted is { } value)
                curve[i] = value;
        }

        return curve;
    }

    /// <summary>The first <paramref name="wanted"/> indices, in <paramref name="order"/>, whose values are known.</summary>
    private static int[] Known(IReadOnlyList<double> values, IEnumerable<int> order, int wanted) =>
        order.Where(j => double.IsFinite(values[j])).Take(wanted).ToArray();

    /// <summary>A quadratic through the points by least squares, read at x = 0 - or null with too few to hold it.</summary>
    private static double? Quadratic(IReadOnlyList<(double X, double Y)> points)
    {
        double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, t0 = 0, t1 = 0, t2 = 0;

        foreach (var (x, y) in points)
        {
            var x2 = x * x;

            s0 += 1;
            s1 += x;
            s2 += x2;
            s3 += x2 * x;
            s4 += x2 * x2;
            t0 += y;
            t1 += x * y;
            t2 += x2 * y;
        }

        if (s0 < 6)
            return null;

        var det = Determinant(s0, s1, s2, s1, s2, s3, s2, s3, s4);

        return Math.Abs(det) < 1e-9 ? null : Determinant(t0, s1, s2, t1, s2, s3, t2, s3, s4) / det;
    }

    private static double Determinant(
        double a, double b, double c, double d, double e, double f, double g, double h, double i) =>
        (a * ((e * i) - (f * h))) - (b * ((d * i) - (f * g))) + (c * ((d * h) - (e * g)));
}
