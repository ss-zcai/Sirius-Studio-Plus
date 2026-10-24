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

        // Sub-pixel: a parabola through the peak and its neighbours. The
        // derivative of a step blurred by the lens and by the smoothing
        // above is close enough to parabolic near its top that this is
        // worth a good fraction of a pixel, which is the whole reason a
        // caliper beats thresholding.
        var offset = bestIndex + Vertex(derivative, bestIndex);

        // The derivative sits between samples i and i+1, so its position is
        // half a sample further along than its index.
        hit = new CaliperHit((offset + 0.5) * spacing, derivative[bestIndex]);
        return true;
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
    /// Where the parabola through three samples peaks, relative to the
    /// middle one, clamped to the interval it can meaningfully describe.
    /// </summary>
    private static double Vertex(double[] values, int index)
    {
        if (index <= 0 || index >= values.Length - 1)
            return 0;

        double a = values[index - 1], b = values[index], c = values[index + 1];
        var denominator = a - (2 * b) + c;

        if (Math.Abs(denominator) < 1e-12)
            return 0;

        return Math.Clamp(0.5 * (a - c) / denominator, -0.5, 0.5);
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
    /// Forward differences, so element i is the step between samples i and
    /// i+1, divided by the spacing to give intensity per pixel.
    /// </summary>
    private static double[] Differentiate(float[] samples, double spacing)
    {
        var result = new double[Math.Max(0, samples.Length - 1)];

        for (var i = 0; i < result.Length; i++)
            result[i] = (samples[i + 1] - samples[i]) / spacing;

        return result;
    }
}
