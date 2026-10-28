using Sirius.Core.Shapes;
using Sirius.Plugins;
using Sirius.Plugins.Testing;
using Xunit;

namespace Sirius.Samples.Tests;

/// <summary>
/// How to test a custom node: build a picture, supply it, execute, and look
/// at what was published. No application, no graph, no OpenCV.
/// </summary>
/// <remarks>
/// TestRun applies the application's own rules - input pictures are
/// read-only, a required input left out fails the run, a wired setting the
/// node never resolves is a warning - so a node that passes here behaves the
/// same way in a recipe.
/// </remarks>
public class SampleNodeTests
{
    /// <summary>A picture of one grey level.</summary>
    private static Picture Filled(int width, int height, byte value)
    {
        var picture = Picture.Allocate(width, height);
        picture.WritableSamples<byte>().Fill(value);
        return picture;
    }

    // ---- Gamma ------------------------------------------------------------------

    [Fact]
    public void Gamma_two_squares_the_mid_tones()
    {
        var output = TestRun.For(new GammaNode { Gamma = 2.0 })
            .Supply("In", Filled(4, 4, 128))
            .Execute()
            .Published<Picture>("Out");

        // 255 * (128 / 255)^2 = 64.25
        Assert.Equal(64, output.Row<byte>(0)[0]);
    }

    /// <summary>The same node on a 16-bit frame - test every sample type a node claims to handle.</summary>
    [Fact]
    public void Gamma_works_on_sixteen_bit_frames()
    {
        var input = Picture.Allocate(4, 4, pixels: PixelType.UInt16);
        input.WritableSamples<ushort>().Fill(32768);

        var output = TestRun.For(new GammaNode { Gamma = 2.0 })
            .Supply("In", input)
            .Execute()
            .Published<Picture>("Out");

        Assert.Equal(16384, output.Row<ushort>(0)[0]);
    }

    /// <summary>A wire into a connectable setting overrides what was typed in the panel.</summary>
    [Fact]
    public void A_wired_gamma_wins_over_the_typed_one()
    {
        var output = TestRun.For(new GammaNode { Gamma = 1.0 })
            .Supply("In", Filled(4, 4, 128))
            .Wire("Gamma", 2.0)
            .Execute()
            .Published<Picture>("Out");

        Assert.Equal(64, output.Row<byte>(0)[0]);
    }

    [Fact]
    public void Gamma_refuses_a_power_of_zero() =>
        Assert.Throws<InvalidOperationException>(() =>
            TestRun.For(new GammaNode { Gamma = 0 }).Supply("In", Filled(4, 4, 1)).Execute());

    // ---- Bright Region ------------------------------------------------------------

    [Fact]
    public void Bright_region_boxes_what_is_above_the_level()
    {
        var input = Picture.Allocate(10, 10);
        for (var y = 3; y < 5; y++)
            input.WritableRow<byte>(y).Slice(2, 3).Fill(255);

        var run = TestRun.For(new BrightRegionNode { Level = 0.5 })
            .Supply("In", input)
            .Execute();

        var box = Assert.IsType<RectShape>(Assert.Single(run.Published<ShapeSet>("Region").Shapes));

        Assert.Equal(new Bounds(2, 3, 3, 2), box.Rect);
        Assert.Equal(0.06, run.Published<double>("Fraction"), 6);
        Assert.True(run.Published<bool>("Found"));
    }

    [Fact]
    public void A_dark_image_has_no_bright_region()
    {
        var run = TestRun.For(new BrightRegionNode())
            .Supply("In", Filled(8, 8, 10))
            .Execute();

        Assert.Empty(run.Published<ShapeSet>("Region").Shapes);
        Assert.False(run.Published<bool>("Found"));
    }

    /// <summary>A warning is part of what a node does, so it is worth a test too.</summary>
    [Fact]
    public void A_colour_image_is_warned_about()
    {
        var run = TestRun.For(new BrightRegionNode())
            .Supply("In", Picture.Allocate(8, 8, 3, layout: ColourLayout.Bgr))
            .Execute();

        Assert.Contains("first of 3 channels", Assert.Single(run.Warnings));
    }

    // ---- Ratio --------------------------------------------------------------------

    [Fact]
    public void Ratio_divides_and_rounds()
    {
        var run = TestRun.For(new RatioNode { Decimals = 2 })
            .Supply("A", 1.0)
            .Supply("B", 3.0)
            .Execute();

        Assert.Equal(0.33, run.Published<double>("Ratio"));
    }

    [Fact]
    public void Ratio_warns_rather_than_fails_on_zero()
    {
        var run = TestRun.For(new RatioNode())
            .Supply("A", 1.0)
            .Supply("B", 0.0)
            .Execute();

        Assert.True(double.IsNaN(run.Published<double>("Ratio")));
        Assert.Single(run.Warnings);
    }
}
