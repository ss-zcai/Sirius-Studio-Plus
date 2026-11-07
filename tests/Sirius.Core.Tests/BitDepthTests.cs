using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Images that say how many bits they use, and tools that follow it: a
/// 12-bit camera's frame is 0 to 4095, and its settings are too.
/// </summary>
public class BitDepthTests
{
    public BitDepthTests() => TestCatalog.Ensure();

    // ---- the image says -----------------------------------------------------

    [Fact]
    public void An_image_uses_all_its_bits_unless_told()
    {
        using var eight = ImageData.Adopt(new Mat(4, 4, DepthType.Cv8U, 1));
        using var sixteen = ImageData.Adopt(new Mat(4, 4, DepthType.Cv16U, 1));
        using var twelve = ImageData.Adopt(new Mat(4, 4, DepthType.Cv16U, 1), bits: 12);
        using var floating = ImageData.Adopt(new Mat(4, 4, DepthType.Cv32F, 1));

        Assert.Equal((8, 255.0), (eight.Bits, eight.MaxValue));
        Assert.Equal((16, 65535.0), (sixteen.Bits, sixteen.MaxValue));
        Assert.Equal((12, 4095.0), (twelve.Bits, twelve.MaxValue));
        Assert.Equal(0, floating.Bits);
    }

    [Fact]
    public void Bits_are_never_more_than_the_type_holds()
    {
        using var image = ImageData.Adopt(new Mat(4, 4, DepthType.Cv8U, 1), bits: 12);

        Assert.Equal(8, image.Bits);
    }

    [Fact]
    public void A_result_keeps_the_bits_while_its_pixel_type_is_the_same()
    {
        using var twelve = Twelve(4095);
        using var blurred = ImageData.From(twelve, twelve.Mat.Clone());

        var narrowed = new Mat();
        twelve.Mat.ConvertTo(narrowed, DepthType.Cv8U, 1.0 / 16);
        using var eight = ImageData.From(twelve, narrowed);

        Assert.Equal(12, blurred.Bits);
        Assert.Equal(8, eight.Bits);
    }

    /// <summary>12-bit white is 8-bit white; by the type's 65535 it was 16.</summary>
    [Fact]
    public void Narrowed_to_eight_bits_by_its_own_depth()
    {
        using var twelve = Twelve(4095);
        using var narrowed = twelve.ToEightBit();

        Assert.Equal(255, narrowed.GetRawData()[0]);
    }

    [Fact]
    public void A_filter_passes_the_bits_on()
    {
        var blur = new GaussianBlurNode();

        var result = RunOne(Twelve(2000), blur, blur.ImageOut);

        Assert.Equal(12, result.Bits);
    }

    // ---- where they come from ------------------------------------------------

    [Fact]
    public void Image_file_reads_its_own_depth_by_default_and_is_told_its_bits()
    {
        var path = Path.Combine(Path.GetTempPath(), $"twelve-{Guid.NewGuid():N}.png");

        using (var twelve = Twelve(3000))
            CvInvoke.Imwrite(path, twelve.Mat);

        try
        {
            var auto = new ImageFileSourceNode { FilePath = path };
            var told = new ImageFileSourceNode { FilePath = path, Bits = ImageBits.Bits12 };

            var (autoImage, toldImage) = (RunSource(auto, auto.ImageOut), RunSource(told, told.ImageOut));

            // Unchanged by default for a new node, so the levels survive.
            Assert.Equal(ImreadModes.Unchanged, new ImageFileSourceNode().Mode);
            Assert.Equal(DepthType.Cv16U, autoImage.Depth);
            Assert.Equal(16, autoImage.Bits);
            Assert.Equal(12, toldImage.Bits);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("PixelType_Gvsp_Mono12", 12)]
    [InlineData("PixelType_Gvsp_Mono10", 10)]
    [InlineData("PixelType_Gvsp_Mono16", 16)]
    [InlineData("PixelType_Gvsp_BayerRG8", null)]
    public void A_hikrobot_format_says_its_sensor_bits(string format, int? bits)
    {
        Assert.Equal(bits, Sirius.Cameras.Hikrobot.HikrobotCamera.SensorBits(format));
    }

    // ---- the tools follow ----------------------------------------------------

    /// <summary>A fixed threshold of 2000 is pixel value 2000, and the mask is 8-bit for tracing.</summary>
    [Fact]
    public void Find_blobs_thresholds_a_twelve_bit_image_in_its_own_values()
    {
        var blobs = new BlobFilterNode { Segmentation = BlobSegmentation.Fixed, Threshold = 2000 };

        var count = RunCount(SpotOn(1000, 3000), blobs);

        Assert.Equal(1, count);
    }

    [Fact]
    public void Find_blobs_thresholds_a_twelve_bit_image_automatically()
    {
        var blobs = new BlobFilterNode { Segmentation = BlobSegmentation.Automatic };

        var count = RunCount(SpotOn(1000, 3000), blobs);

        Assert.Equal(1, count);
    }

    [Fact]
    public void Adaptive_threshold_takes_a_twelve_bit_image()
    {
        var adaptive = new AdaptiveThresholdNode();

        var result = RunOne(SpotOn(1000, 3000), adaptive, adaptive.ImageOut);

        Assert.Equal(DepthType.Cv8U, result.Depth);
        Assert.Equal(NodeStatus.Ready, adaptive.Status);
    }

    /// <summary>Over 4096 levels, not 65536, where it would fill the first sixteenth.</summary>
    [Fact]
    public void The_histogram_spans_the_images_own_levels()
    {
        var histogram = new HistogramNode { BinCount = 256 };
        var graph = new VisionGraph();
        var source = new EdgeTestKit.PictureNode(() => Twelve(4000));

        graph.Add(source);
        graph.Add(histogram);
        graph.TryConnect(source.Out, histogram.ImageIn);

        using var scheduler = EdgeTestKit.Run(graph);
        var data = scheduler.Get<HistogramData>(histogram.HistogramOut);

        // 4000 lands in bin 250 of 256, which counts levels 4000 to 4015 -
        // and every one of the 64 x 64 pixels is there.
        Assert.Equal(4096, data.Levels);
        Assert.Equal(64 * 64, data.Channels[0][250]);
        Assert.Equal((4000, 4015), data.LevelsOf(250));
    }

    /// <summary>White multiplies as one: by 4095, not by the type's 65535.</summary>
    [Fact]
    public void Multiply_takes_twelve_bit_white_as_white()
    {
        var graph = new VisionGraph();
        var first = new EdgeTestKit.PictureNode(() => Twelve(2000));
        var second = new EdgeTestKit.PictureNode(() => Twelve(4095));
        var blend = new ImageBlendNode { Mode = BlendMode.Multiply };

        graph.Add(first);
        graph.Add(second);
        graph.Add(blend);
        graph.TryConnect(first.Out, blend.FirstIn);
        graph.TryConnect(second.Out, blend.SecondIn);

        using var scheduler = EdgeTestKit.Run(graph);
        var result = scheduler.Get<ImageData>(blend.ImageOut);

        Assert.InRange(BitConverter.ToUInt16(result.Mat.GetRawData(), 0), 1999, 2001);
        Assert.Equal(12, result.Bits);
    }

    // ---- the settings follow -------------------------------------------------

    /// <summary>
    /// Run on a 12-bit image, a threshold's slider runs to 4095; the value
    /// itself is never scaled.
    /// </summary>
    [Fact]
    public void A_levels_range_follows_the_depth_it_last_ran_on()
    {
        var threshold = new ThresholdNode { Threshold = 2000 };
        var parameter = new ParameterViewModel(
            threshold, threshold.Descriptor.Parameters.Single(p => p.Name == nameof(ThresholdNode.Threshold)));

        Assert.Equal(255, parameter.Maximum);

        RunOne(Twelve(1000), threshold, threshold.ImageOut);

        Assert.Equal(4095, parameter.Maximum, 6);
        Assert.Equal(2000, threshold.Threshold);
    }

    /// <summary>
    /// Every setting that is a level in the image's values stretches to the
    /// image's top - found missing on Normalise's Scale, so held for all.
    /// </summary>
    [Theory]
    [InlineData(typeof(NormaliseNode), nameof(NormaliseNode.Scale))]
    [InlineData(typeof(HighPassFilterNode), nameof(HighPassFilterNode.Offset))]
    [InlineData(typeof(ThresholdNode), nameof(ThresholdNode.MaxValue))]
    [InlineData(typeof(BrightnessContrastNode), nameof(BrightnessContrastNode.Brightness))]
    [InlineData(typeof(CannyNode), nameof(CannyNode.Threshold2))]
    public void Each_level_setting_runs_to_a_twelve_bit_top(Type type, string name)
    {
        var node = (ImageFilterNode)Activator.CreateInstance(type)!;
        var descriptor = node.Descriptor.Parameters.Single(p => p.Name == name);
        var parameter = new ParameterViewModel(node, descriptor);

        Assert.True(descriptor.Attribute.Intensity, $"{type.Name}.{name} is not marked as a level");

        RunOne(Twelve(1000), node, node.ImageOut);

        // Its 8-bit range, stretched by 4095 / 255: 255 becomes 4095.
        Assert.Equal(descriptor.Attribute.Max * 4095 / 255, parameter.Maximum, 6);
    }

    [Fact]
    public void A_setting_not_in_image_values_keeps_its_range()
    {
        var adaptive = new AdaptiveThresholdNode();
        var blockSize = new ParameterViewModel(
            adaptive, adaptive.Descriptor.Parameters.Single(p => p.Name == nameof(AdaptiveThresholdNode.BlockSize)));

        RunOne(Twelve(1000), adaptive, adaptive.ImageOut);

        Assert.Equal(255, blockSize.Maximum);
    }

    // ---- helpers ---------------------------------------------------------------------

    /// <summary>A 64 x 64 12-bit image at one level.</summary>
    private static ImageData Twelve(int level)
    {
        var mat = new Mat(64, 64, DepthType.Cv16U, 1);
        mat.SetTo(new MCvScalar(level));
        return ImageData.Adopt(mat, ColourSpace.Gray, bits: 12);
    }

    /// <summary>A 12-bit background with a bright square spot in the middle.</summary>
    private static ImageData SpotOn(int background, int spot)
    {
        var mat = new Mat(64, 64, DepthType.Cv16U, 1);
        mat.SetTo(new MCvScalar(background));

        using (var middle = new Mat(mat, new System.Drawing.Rectangle(24, 24, 16, 16)))
            middle.SetTo(new MCvScalar(spot));

        return ImageData.Adopt(mat, ColourSpace.Gray, bits: 12);
    }

    private static ImageData RunOne(ImageData image, VisionNode node, OutputPort output)
    {
        var (graph, _) = EdgeTestKit.Wire(() => ImageData.CopyOf(image.Mat, image.Space, image.Bits), node);

        using var scheduler = EdgeTestKit.Run(graph);
        return scheduler.Get<ImageData>(output).Clone();
    }

    private static ImageData RunSource(VisionNode source, OutputPort output)
    {
        var graph = new VisionGraph();
        graph.Add(source);

        using var scheduler = EdgeTestKit.Run(graph);
        return scheduler.Get<ImageData>(output).Clone();
    }

    private static int RunCount(ImageData image, BlobFilterNode blobs)
    {
        var (graph, _) = EdgeTestKit.Wire(() => image, blobs);

        using var scheduler = EdgeTestKit.Run(graph);

        Assert.NotEqual(NodeStatus.Error, blobs.Status);
        return scheduler.Get<int>(blobs.CountOut);
    }
}
