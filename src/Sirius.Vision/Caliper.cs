namespace Sirius.Vision;

/// <summary>Which way the intensity steps across an edge.</summary>
public enum EdgePolarity
{
    /// <summary>Whichever step is strongest.</summary>
    Either,

    /// <summary>Dark before, light after - scanning in the caliper's direction.</summary>
    DarkToLight,

    /// <summary>Light before, dark after.</summary>
    LightToDark,
}

/// <summary>Which edge to take when a scan crosses more than one.</summary>
public enum EdgeChoice
{
    /// <summary>The steepest, wherever it falls.</summary>
    Strongest,

    /// <summary>The one nearest the start of the scan.</summary>
    First,

    /// <summary>The one nearest the end.</summary>
    Last,
}

/// <summary>One edge found along one scan.</summary>
/// <param name="Offset">
/// Distance from the start of the scan, in the units the samples were spaced
/// in - so pixels when the spacing was one.
/// </param>
/// <param name="Strength">
/// Height of the intensity step, as the smoothed derivative's peak. Signed:
/// positive is dark to light.
/// </param>
public readonly record struct CaliperHit(double Offset, double Strength);

/// <summary>
/// Finds an intensity step along a sampled line.
/// </summary>
/// <remarks>
/// <para>
/// The primitive every "find" tool is built from. A caliper samples the
/// image along a short line crossing the feature it is looking for, and
/// reports where the intensity steps and by how much. A circle finder casts
/// them radially, a line finder casts them along a rectangle, a curve
/// finder along a spline - the difference is where the lines go, not what
/// happens on each one.
/// </para>
/// <para>
/// Sampling is not done here. <see cref="LineProfileSource.Sample"/> already
/// walks a line at a chosen spacing with optional averaging across a band,
/// which is exactly a caliper's sampling, and it does it with the same
/// interpolation the profile chart uses. This takes the samples it produced.
/// </para>
/// </remarks>
public static class Caliper
{
    /// <summary>
    /// Finds the strongest matching edge in a sampled profile.
    /// </summary>
    /// <param name="samples">Intensities along the scan, evenly spaced.</param>
    /// <param name="spacing">Distance between samples, in pixels.</param>
    /// <param name="polarity">Which direction of step to accept.</param>
    /// <param name="minimumContrast">
    /// Smallest step worth calling an edge, in intensity units per pixel.
    /// Rejects the noise that otherwise produces an edge on every scan,
    /// including the ones crossing nothing.
    /// </param>
    /// <param name="smoothing">
    /// Width of the moving average applied before differentiating, in
    /// samples. Forced odd so it cannot shift the result by half a sample.
    /// </param>
    /// <param name="choice">Which edge to take when several qualify.</param>
    public static bool TryFind(
        ReadOnlySpan<float> samples,
        double spacing,
        EdgePolarity polarity,
        double minimumContrast,
        int smoothing,
        EdgeChoice choice,
        out CaliperHit hit)
    {
        hit = default;

        if (samples.Length < 3 || spacing <= 0)
            return false;

        var smoothed = Smooth(samples, smoothing);
        var derivative = Differentiate(smoothed, spacing);

        var bestIndex = -1;
        var bestScore = 0.0;

        for (var i = 0; i < derivative.Length; i++)
        {
            var value = derivative[i];

            if (!Accepts(polarity, value) || Math.Abs(value) < minimumContrast)
                continue;

            // A peak, not merely a steep place: an edge is where the
            // derivative turns, and without this a long ramp reports its
            // middle sample and a slightly different ramp reports a
            // different one.
            if (!IsPeak(derivative, i))
                continue;

            var score = Math.Abs(value);

            var better = choice switch
            {
                EdgeChoice.First => bestIndex < 0,
                EdgeChoice.Last => true,
                _ => score > bestScore,
            };

            if (!better)
                continue;

            bestIndex = i;
            bestScore = score;

            if (choice == EdgeChoice.First)
                break;
        }

        if (bestIndex < 0)
            return false;

        // Sub-pixel by the centroid of the derivative across the peak's
        // own support, rather than by a parabola through three samples.
        //
        // A parabola assumes a rounded top. A perfectly hard step - a
        // thresholded image, or an edge falling exactly between two pixels
        // - smooths into a straight ramp whose derivative is a flat
        // plateau, where three samples say nothing and the answer lands on
        // whichever end was looked at first. Measured against a known edge
        // that cost half the smoothing width, growing with it: nothing at
        // width 5, three quarters of a pixel at 11, two and a quarter at
        // 21.
        //
        // A centroid has no such assumption. Over a symmetric plateau it
        // returns the middle; over a rounded peak it returns the peak; over
        // an asymmetric blur it returns the balance point, which is what an
        // edge position means.
        var offset = Centroid(derivative, bestIndex);

        hit = new CaliperHit(offset * spacing, derivative[bestIndex]);
        return true;
    }

    /// <summary>
    /// The intensity-weighted centre of the derivative peak containing
    /// <paramref name="index"/>.
    /// </summary>
    /// <remarks>
    /// The support is the run either side of the peak that stays above
    /// half its height - the usual half-maximum rule. Stopping there
    /// rather than at a zero crossing keeps a neighbouring edge's tail out
    /// of the average, which would otherwise drag the reading towards it.
    /// </remarks>
    private static double Centroid(double[] values, int index)
    {
        var peak = Math.Abs(values[index]);

        if (peak <= 0)
            return index;

        var half = peak / 2;

        var first = index;
        while (first > 0 && Math.Abs(values[first - 1]) >= half &&
               Math.Sign(values[first - 1]) == Math.Sign(values[index]))
        {
            first--;
        }

        var last = index;
        while (last < values.Length - 1 && Math.Abs(values[last + 1]) >= half &&
               Math.Sign(values[last + 1]) == Math.Sign(values[index]))
        {
            last++;
        }

        double weighted = 0, total = 0;

        for (var i = first; i <= last; i++)
        {
            var weight = Math.Abs(values[i]) - (half / 2);

            if (weight <= 0)
                continue;

            weighted += i * weight;
            total += weight;
        }

        return total > 0 ? weighted / total : index;
    }

    private static bool Accepts(EdgePolarity polarity, double derivative) => polarity switch
    {
        EdgePolarity.DarkToLight => derivative > 0,
        EdgePolarity.LightToDark => derivative < 0,
        _ => derivative != 0,
    };

    /// <summary>
    /// Whether this sample is a local extremum of the derivative, in the
    /// direction its own sign implies.
    /// </summary>
    private static bool IsPeak(double[] derivative, int index)
    {
        var value = derivative[index];
        var previous = index > 0 ? derivative[index - 1] : 0;
        var next = index < derivative.Length - 1 ? derivative[index + 1] : 0;

        return value >= 0
            ? value >= previous && value >= next
            : value <= previous && value <= next;
    }

    /// <summary>
    /// A moving average, which is the cheapest thing that stops the
    /// derivative chasing single-pixel noise.
    /// </summary>
    /// <remarks>
    /// Width is forced odd and the window is centred, so smoothing cannot
    /// move an edge - an even window would bias every reading by half a
    /// sample in the scan direction, which on a circle reads as a radius
    /// that is wrong by a consistent amount and looks like a calibration
    /// error.
    /// </remarks>
    private static float[] Smooth(ReadOnlySpan<float> samples, int width)
    {
        var result = new float[samples.Length];

        if (width <= 1)
        {
            samples.CopyTo(result);
            return result;
        }

        var half = (Math.Max(3, width) | 1) / 2;

        for (var i = 0; i < samples.Length; i++)
        {
            double total = 0;
            var count = 0;

            for (var k = -half; k <= half; k++)
            {
                var j = i + k;

                if (j < 0 || j >= samples.Length)
                    continue;

                total += samples[j];
                count++;
            }

            result[i] = (float)(total / count);
        }

        return result;
    }

    /// <summary>
    /// Central differences, so element i is the slope <em>at</em> sample i
    /// rather than between i and i+1.
    /// </summary>
    /// <remarks>
    /// Centred on purpose. A forward difference sits half a sample along
    /// from the index it is stored at, and every reading then needs that
    /// half back - a correction that is easy to write once and easy to
    /// forget when the peak finding changes. The ends keep a one-sided
    /// difference, which is only ever the edge of the scan.
    /// </remarks>
    private static double[] Differentiate(float[] samples, double spacing)
    {
        var result = new double[samples.Length];

        for (var i = 0; i < samples.Length; i++)
        {
            var low = Math.Max(0, i - 1);
            var high = Math.Min(samples.Length - 1, i + 1);

            result[i] = high == low ? 0 : (samples[high] - samples[low]) / ((high - low) * spacing);
        }

        return result;
    }
}
