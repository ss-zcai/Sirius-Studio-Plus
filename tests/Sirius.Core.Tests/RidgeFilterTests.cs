using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;
using static Sirius.Core.Tests.EdgeTestKit;

namespace Sirius.Core.Tests;

/// <summary>
/// Ridge Filter: thin lines stand out whichever way they run, at their
/// depth times the Gain, and spots and flat ground drop away.
/// </summary>
/// <remarks>
/// Pictures drawn exactly: grey ground at 150, a line 3 wide through the
/// centre, antialiased across one pixel at its sides.
/// </remarks>
public class RidgeFilterTests
{
    public RidgeFilterTests() => TestCatalog.Ensure();

    private const int Size = 160;
    private const int Centre = Size / 2;
    private const int Ground = 150;

    /// <summary>
    /// Ground with a line <paramref name="depth"/> darker (lighter if
    /// negative) through the centre at <paramref name="degrees"/>.
    /// </summary>
    private static ImageData Line(double degrees, double depth, double width = 3) =>
        Draw((x, y) =>
        {
            var angle = degrees * Math.PI / 180;
            var across = Math.Abs((-(x - Centre) * Math.Sin(angle)) + ((y - Centre) * Math.Cos(angle)));
            return depth * Math.Clamp((width / 2) + 0.5 - across, 0, 1);
        });

    /// <summary>Ground with a round spot <paramref name="depth"/> darker at the centre.</summary>
    private static ImageData Spot(double diameter, double depth) =>
        Draw((x, y) =>
        {
            var from = Math.Sqrt(((x - Centre) * (x - Centre)) + ((y - Centre) * (y - Centre)));
            return depth * Math.Clamp((diameter / 2) + 0.5 - from, 0, 1);
        });

    private static ImageData Draw(Func<int, int, double> darker)
    {
        var data = new byte[Size * Size];

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
                data[(y * Size) + x] = (byte)Math.Round(Ground - darker(x, y));
        }

        var mat = new Mat(Size, Size, DepthType.Cv8U, 1);
        mat.SetTo(data);
        return ImageData.Adopt(mat);
    }

    private static byte[] Filter(ImageData picture, Action<RidgeFilterNode>? configure = null)
    {
        var node = new RidgeFilterNode();
        configure?.Invoke(node);

        var (graph, _) = Wire(() => picture, node);
        var result = Run(graph);
        Assert.Equal(NodeStatus.Ready, node.Status);

        var image = result.Get<ImageData>(node.ImageOut);
        Assert.Equal(DepthType.Cv8U, image.Depth);

        var values = new byte[Size * Size];
        image.Mat.CopyTo(values);
        return values;
    }

    private static byte At(byte[] values, int x, int y) => values[(y * Size) + x];

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(77)]
    [InlineData(90)]
    [InlineData(135)]
    public void A_line_comes_out_at_its_depth_times_the_gain_whichever_way_it_runs(double degrees)
    {
        var values = Filter(Line(degrees, depth: 20));

        // 20 deep at a Gain of 4.
        Assert.InRange(At(values, Centre, Centre), 64, 96);

        // 30 pixels to its side, the ground is flat.
        var angle = degrees * Math.PI / 180;
        var x = (int)Math.Round(Centre - (30 * Math.Sin(angle)));
        var y = (int)Math.Round(Centre + (30 * Math.Cos(angle)));
        Assert.Equal(0, At(values, x, y));
    }

    [Fact]
    public void Flat_ground_is_zero()
    {
        var values = Filter(Draw((_, _) => 0));

        Assert.All(values, value => Assert.Equal(0, value));
    }

    [Fact]
    public void A_round_spot_as_deep_and_as_wide_as_a_line_is_much_weaker()
    {
        var line = At(Filter(Line(30, depth: 20)), Centre, Centre);
        var spot = At(Filter(Spot(3, depth: 20)), Centre, Centre);

        Assert.True(spot < line / 4, $"a spot read {spot} against a line's {line}");
    }

    [Fact]
    public void A_faint_stretch_reads_in_proportion_so_one_threshold_keeps_it()
    {
        var faint = At(Filter(Line(30, depth: 6)), Centre, Centre);

        // 6 deep at 4 is 24: clear of the ground's 0, where the raw picture
        // held it only 6 grey levels under 150.
        Assert.InRange(faint, 18, 30);
    }

    [Fact]
    public void Polarity_picks_dark_lines_light_lines_or_both()
    {
        var dark = Line(30, depth: 20);
        var light = Line(30, depth: -20);

        Assert.True(At(Filter(dark), Centre, Centre) > 60);
        Assert.Equal(0, At(Filter(light), Centre, Centre));

        Assert.Equal(0, At(Filter(dark, n => n.Polarity = LinePolarity.Light), Centre, Centre));
        Assert.True(At(Filter(light, n => n.Polarity = LinePolarity.Light), Centre, Centre) > 60);

        Assert.True(At(Filter(dark, n => n.Polarity = LinePolarity.Both), Centre, Centre) > 60);
        Assert.True(At(Filter(light, n => n.Polarity = LinePolarity.Both), Centre, Centre) > 60);
    }

    [Fact]
    public void A_wider_line_is_found_at_its_own_width()
    {
        var picture = Line(30, depth: 20, width: 9);

        var narrow = At(Filter(picture), Centre, Centre);
        var matched = At(Filter(picture, n => n.LineWidth = 9), Centre, Centre);

        Assert.InRange(matched, 64, 96);
        Assert.True(narrow < matched / 2, $"a 3-wide filter read a 9-wide line at {narrow}");
    }

    [Fact]
    public void A_deeper_picture_keeps_its_depth_and_stays_within_its_bits()
    {
        var mat = new Mat(Size, Size, DepthType.Cv16U, 1);
        var data = new ushort[Size * Size];

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
                data[(y * Size) + x] = (ushort)(Math.Abs(x - Centre) <= 1 ? 400 : 3000);
        }

        mat.SetTo(data);
        var node = new RidgeFilterNode { Gain = 50 };
        var (graph, _) = Wire(() => ImageData.Adopt(mat.Clone(), bits: 12), node);

        var image = Run(graph).Get<ImageData>(node.ImageOut);
        Assert.Equal(DepthType.Cv16U, image.Depth);
        Assert.Equal(12, image.Bits);

        var values = new ushort[Size * Size];
        image.Mat.CopyTo(values);
        Assert.Equal(4095, values[(Centre * Size) + Centre]);
        Assert.All(values, value => Assert.InRange(value, 0, 4095));
    }

    [Fact]
    public void A_colour_picture_is_refused_with_the_way_out()
    {
        var mat = new Mat(Size, Size, DepthType.Cv8U, 3);
        mat.SetTo(new Emgu.CV.Structure.MCvScalar(150, 150, 150));
        var node = new RidgeFilterNode();
        var (graph, _) = Wire(() => ImageData.Adopt(mat.Clone()), node);

        Run(graph);

        Assert.Equal(NodeStatus.Error, node.Status);
        Assert.Contains("Colour Convert", node.StatusMessage);
    }

    [Theory]
    [InlineData(0.8)]
    [InlineData(1.5)]
    [InlineData(4.5)]
    public void The_kernels_read_flat_ground_a_slope_and_a_parabola_exactly(double sigma)
    {
        var radius = (int)Math.Ceiling(4 * sigma);
        var taps = new float[3][];

        for (var order = 0; order < 3; order++)
        {
            using var kernel = RidgeFilterNode.Kernel(radius, sigma, order);
            taps[order] = new float[kernel.Cols];
            kernel.CopyTo(taps[order]);
        }

        double Read(float[] kernel, Func<double, double> f) =>
            kernel.Select((k, i) => k * f(i - radius)).Sum();

        Assert.Equal(1, Read(taps[0], _ => 1), 4);
        Assert.Equal(0, Read(taps[1], _ => 1), 4);
        Assert.Equal(1, Read(taps[1], x => x), 4);
        Assert.Equal(0, Read(taps[2], _ => 1), 4);
        Assert.Equal(0, Read(taps[2], x => x), 4);
        Assert.Equal(2, Read(taps[2], x => x * x), 4);
    }
}
