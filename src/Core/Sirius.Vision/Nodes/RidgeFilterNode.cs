using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;

namespace Sirius.Vision.Nodes;

/// <summary>Which lines a ridge filter answers to.</summary>
public enum LinePolarity
{
    /// <summary>Lines darker than around them - a scratch seen in light.</summary>
    Dark,

    /// <summary>Lines lighter than around them - a scratch lit from the side.</summary>
    Light,

    /// <summary>Either.</summary>
    Both,
}

/// <summary>
/// Makes thin lines stand out, in any direction, and leaves spots and flat
/// ground dark: a scratch filter to put in front of a threshold.
/// </summary>
/// <remarks>
/// <para>
/// A threshold judges each pixel alone, so a scratch that fades along its
/// length comes out as dashes, and lowering the threshold lets the dust in
/// first. This looks at how the brightness curves around each pixel - the
/// Hessian, the second derivatives smoothed at the line's scale. Across a
/// line it curves sharply; along it, hardly at all. A spot curves both
/// ways alike, and flat ground neither way. The answer is the curvature
/// across less the curvature along, so a line stands out wherever it runs,
/// faint stretches included, and round dust largely drops away. The
/// direction falls out of the same sum, so no angles are tried.
/// </para>
/// <para>
/// The smoothing is half the Line Width. The result is scaled so that a
/// line that width comes out at its depth - how much darker (or lighter)
/// it is than around it - times the Gain: a scratch 10 grey levels deep
/// is 40 at the default Gain of 4. Flat ground is 0, so a plain Threshold
/// behind it needs no local mean.
/// </para>
/// <para>
/// The Hessian only looks about a line's width around each pixel. It
/// lifts faint stretches but does not join a scratch across a true break,
/// where it is no darker than the ground.
/// </para>
/// </remarks>
[VisionNode(
    "Ridge Filter",
    TypeKey = "filter.ridge",
    Category = NodeCategory.Filtering,
    Icon = "ph_ridge",
    Description = "Makes thin lines such as scratches stand out in any direction, and spots and flat ground drop away.")]
public sealed class RidgeFilterNode : ImageFilterNode
{
    /// <summary>
    /// How deep a line of width 2σ reads, as a share of its depth, after the
    /// σ²-scaled second derivative: 2·φ(1), φ the unit normal density.
    /// </summary>
    internal const double DepthShare = 0.48394144903828673;

    private double _lineWidth = 3;
    private LinePolarity _polarity = LinePolarity.Dark;
    private double _gain = 4;

    [Param("Line Width",
        Description = "How wide the lines are, in pixels. Narrower lines still show, weaker; wider ones are taken as ground.",
        Min = 1, Max = 40, Step = 0.5, Editor = ParamEditor.Slider)]
    public double LineWidth
    {
        get => _lineWidth;
        set => SetParam(ref _lineWidth, Math.Clamp(value, 1, 40));
    }

    [Param("Polarity", Description = "Dark lines on lighter ground, light lines on darker ground, or both.")]
    public LinePolarity Polarity
    {
        get => _polarity;
        set => SetParam(ref _polarity, value);
    }

    [Param("Gain",
        Description = "A line comes out at its depth times this: 10 grey levels deep is 40 at 4.",
        Min = 0.1, Max = 50, Step = 0.1, Editor = ParamEditor.Slider)]
    public double Gain
    {
        get => _gain;
        set => SetParam(ref _gain, Math.Clamp(value, 0.1, 50));
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context)
    {
        if (source.Channels != 1)
        {
            throw new InvalidOperationException(
                $"Ridge Filter needs a single-channel image but received {source.Channels} channels. " +
                "Insert a Colour Convert node set to BGR to Gray.");
        }

        var sigma = _lineWidth / 2;

        using var strength = Ridges(source.Mat, sigma, _polarity);

        // σ² makes the curvature a brightness; DepthShare makes it the line's depth.
        strength.ConvertTo(destination, source.Depth, _gain * sigma * sigma / DepthShare);

        // A 10- or 12-bit picture stays within its own range.
        if (source.Depth == DepthType.Cv16U && source.MaxValue < ushort.MaxValue)
        {
            using var ceiling = new ScalarArray(source.MaxValue);
            CvInvoke.Min(destination, ceiling, destination);
        }
    }

    /// <summary>
    /// The curvature across a line less that along it, per pixel, zero where
    /// there is no line of <paramref name="polarity"/>. Caller disposes.
    /// </summary>
    internal static Mat Ridges(Mat image, double sigma, LinePolarity polarity)
    {
        using var input = new Mat();
        image.ConvertTo(input, DepthType.Cv32F);

        var radius = (int)Math.Ceiling(4 * sigma);
        using var smooth = Kernel(radius, sigma, 0);
        using var slope = Kernel(radius, sigma, 1);
        using var bend = Kernel(radius, sigma, 2);

        using var xx = new Mat();
        using var yy = new Mat();
        using var xy = new Mat();
        var anchor = new Point(-1, -1);
        CvInvoke.SepFilter2D(input, xx, DepthType.Cv32F, bend, smooth, anchor, 0, BorderType.Reflect101);
        CvInvoke.SepFilter2D(input, yy, DepthType.Cv32F, smooth, bend, anchor, 0, BorderType.Reflect101);
        CvInvoke.SepFilter2D(input, xy, DepthType.Cv32F, slope, slope, anchor, 0, BorderType.Reflect101);

        var result = new Mat(image.Rows, image.Cols, DepthType.Cv32F, 1);
        Combine(xx, yy, xy, result, polarity);
        return result;
    }

    /// <summary>
    /// The eigenvalues of [xx xy; xy yy] at each pixel, a ≥ b, made into the
    /// ridge strength.
    /// </summary>
    /// <remarks>
    /// A dark line is a valley: across it the curvature a is strongly
    /// positive. Its strength is a - |b|: a line (b ≈ 0) keeps all of a, a
    /// round spot (b ≈ a) almost none, and a saddle or a light line
    /// (b &lt; -a) none. A light line is the same turned over.
    /// </remarks>
    private static unsafe void Combine(Mat xx, Mat yy, Mat xy, Mat result, LinePolarity polarity)
    {
        var rows = result.Rows;
        var cols = result.Cols;

        for (var row = 0; row < rows; row++)
        {
            var pxx = (float*)(xx.DataPointer + (row * xx.Step));
            var pyy = (float*)(yy.DataPointer + (row * yy.Step));
            var pxy = (float*)(xy.DataPointer + (row * xy.Step));
            var output = (float*)(result.DataPointer + (row * result.Step));

            for (var col = 0; col < cols; col++)
            {
                var mean = (pxx[col] + pyy[col]) * 0.5f;
                var half = (pxx[col] - pyy[col]) * 0.5f;
                var spread = MathF.Sqrt((half * half) + (pxy[col] * pxy[col]));
                var a = mean + spread;
                var b = mean - spread;

                var dark = a - MathF.Abs(b);
                var light = -b - MathF.Abs(a);

                var value = polarity switch
                {
                    LinePolarity.Dark => dark,
                    LinePolarity.Light => light,
                    _ => MathF.Max(dark, light),
                };

                output[col] = value > 0 ? value : 0;
            }
        }
    }

    /// <summary>
    /// A row of the Gaussian (order 0) or its first or second derivative,
    /// corrected for sampling so each reads a straight line, a slope or a
    /// parabola exactly. Caller disposes.
    /// </summary>
    internal static Mat Kernel(int radius, double sigma, int order)
    {
        var size = (2 * radius) + 1;
        var gauss = new double[size];
        var values = new double[size];

        for (var i = 0; i < size; i++)
        {
            double x = i - radius;
            gauss[i] = Math.Exp(-(x * x) / (2 * sigma * sigma));
        }

        var total = gauss.Sum();

        for (var i = 0; i < size; i++)
        {
            double x = i - radius;
            var g = gauss[i] / total;

            values[i] = order switch
            {
                0 => g,

                // Correlated, not convolved: +x so that a rising slope reads positive.
                1 => x * g,
                _ => ((x * x) - (sigma * sigma)) * g,
            };
        }

        if (order == 2)
        {
            // Flat ground reads 0: take out the share of the Gaussian it holds.
            var offset = values.Sum();

            for (var i = 0; i < size; i++)
                values[i] -= offset * (gauss[i] / total);
        }

        if (order > 0)
        {
            // x reads 1 for a slope; x² reads 2 for a parabola.
            var probe = 0.0;

            for (var i = 0; i < size; i++)
            {
                double x = i - radius;
                probe += values[i] * (order == 1 ? x : x * x);
            }

            var wanted = order == 1 ? 1.0 : 2.0;

            for (var i = 0; i < size; i++)
                values[i] *= wanted / probe;
        }

        var kernel = new Mat(1, size, DepthType.Cv32F, 1);
        kernel.SetTo(values.Select(v => (float)v).ToArray());
        return kernel;
    }
}
