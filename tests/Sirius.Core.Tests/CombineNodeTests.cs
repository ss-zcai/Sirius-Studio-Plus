using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Nodes that combine two images.
/// </summary>
/// <remarks>
/// The arithmetic is where these go quietly wrong. Multiply, screen and
/// overlay are defined on values from zero to one, so an 8-bit product has
/// to be scaled by 255 - miss it and every such blend saturates to white,
/// which looks like a lighting problem rather than a bug.
/// </remarks>
public class CombineNodeTests
{
    public CombineNodeTests() => TestCatalog.Ensure();

    private static GraphScheduler CreateScheduler(VisionGraph graph)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return new GraphScheduler(graph);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>A flat frame of one value.</summary>
    private sealed class FlatNode : VisionNode
    {
        public FlatNode(double value, int width = 8, int height = 8, int channels = 1,
                        DepthType depth = DepthType.Cv8U)
        {
            Value = value;
            Frame = width;
            Rows = height;
            Channels = channels;
            Depth = depth;
            ImageOut = AddOutput<ImageData>("Out");
        }

        public double Value { get; }

        public int Frame { get; }

        public int Rows { get; }

        public int Channels { get; }

        public DepthType Depth { get; }

        public OutputPort ImageOut { get; }

        protected override void OnEvaluate(EvalContext context)
        {
            var mat = new Mat(Rows, Frame, Depth, Channels);
            mat.SetTo(new MCvScalar(Value, Value, Value, Value));
            context.Set(ImageOut, ImageData.Adopt(mat));
        }
    }

    /// <summary>Runs one combining node over two flat frames and reads a pixel.</summary>
    private static double Run(
        TwoImageNode node, FlatNode first, FlatNode second, FlatNode? mask = null)
    {
        var graph = new VisionGraph();
        graph.Add(first);
        graph.Add(second);
        graph.Add(node);
        graph.TryConnect(first.ImageOut, node.FirstIn);
        graph.TryConnect(second.ImageOut, node.SecondIn);

        if (mask is not null && node is ImageBlendNode blend)
        {
            graph.Add(mask);
            graph.TryConnect(mask.ImageOut, blend.MaskIn);
        }

        using var scheduler = CreateScheduler(graph);
        var result = scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(0, result.Failed);

        var image = scheduler.GetOutput<ImageData>(node.ImageOut)!;
        return CvInvoke.Mean(image.Mat).V0;
    }

    /// <summary>The message a node failed with. Nodes report rather than throw.</summary>
    private static string Failure(
        TwoImageNode node, FlatNode first, FlatNode second)
    {
        var graph = new VisionGraph();
        graph.Add(first);
        graph.Add(second);
        graph.Add(node);
        graph.TryConnect(first.ImageOut, node.FirstIn);
        graph.TryConnect(second.ImageOut, node.SecondIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Error, node.Status);
        Assert.NotNull(node.StatusMessage);

        return node.StatusMessage!;
    }

    // ---- logic --------------------------------------------------------------

    [Theory]
    [InlineData(LogicOperation.And, 255, 255, 255)]
    [InlineData(LogicOperation.And, 255, 0, 0)]
    [InlineData(LogicOperation.Or, 255, 0, 255)]
    [InlineData(LogicOperation.Or, 0, 0, 0)]
    [InlineData(LogicOperation.Xor, 255, 255, 0)]
    [InlineData(LogicOperation.Xor, 255, 0, 255)]
    [InlineData(LogicOperation.Subtract, 255, 0, 255)]
    [InlineData(LogicOperation.Subtract, 255, 255, 0)]
    [InlineData(LogicOperation.Subtract, 0, 255, 0)]
    public void Masks_combine_as_the_operation_says(
        LogicOperation operation, double a, double b, double expected) =>
        Assert.Equal(
            expected,
            Run(new ImageLogicNode { Operation = operation }, new FlatNode(a), new FlatNode(b)),
            1);

    /// <summary>
    /// The reason binary is the default. Two mid-greys bitwise-ANDed come out
    /// near black, which is correct about the bits and wrong about the
    /// question - 0x7F AND 0x80 is zero.
    /// </summary>
    [Fact]
    public void Greyscale_is_read_as_set_or_not_rather_than_bit_by_bit()
    {
        var logical = Run(
            new ImageLogicNode { Operation = LogicOperation.And },
            new FlatNode(127), new FlatNode(128));

        var bitwise = Run(
            new ImageLogicNode { Operation = LogicOperation.And, AsBinary = false },
            new FlatNode(127), new FlatNode(128));

        Assert.Equal(255, logical, 1);
        Assert.Equal(0, bitwise, 1);
    }

    [Fact]
    public void Binary_output_is_zero_or_full_whatever_went_in()
    {
        Assert.Equal(
            255,
            Run(new ImageLogicNode { Operation = LogicOperation.Or },
                new FlatNode(3), new FlatNode(0)),
            1);
    }

    // ---- blend arithmetic ---------------------------------------------------

    [Theory]
    [InlineData(BlendMode.Normal, 40, 200, 200)]
    [InlineData(BlendMode.Add, 100, 50, 150)]
    [InlineData(BlendMode.Subtract, 100, 40, 60)]
    [InlineData(BlendMode.Difference, 40, 100, 60)]
    [InlineData(BlendMode.Darken, 40, 100, 40)]
    [InlineData(BlendMode.Lighten, 40, 100, 100)]
    public void Blend_modes_combine_as_named(
        BlendMode mode, double a, double b, double expected) =>
        Assert.Equal(
            expected,
            Run(new ImageBlendNode { Mode = mode }, new FlatNode(a), new FlatNode(b)),
            1);

    /// <summary>
    /// The scaling. Multiplying 128 by 128 on an 8-bit frame is 64, not
    /// saturated white - the operands are halves of full scale.
    /// </summary>
    [Fact]
    public void Multiply_is_scaled_to_the_depth()
    {
        Assert.Equal(
            64,
            Run(new ImageBlendNode { Mode = BlendMode.Multiply }, new FlatNode(128), new FlatNode(128)),
            1);
    }

    [Fact]
    public void Multiplying_by_white_leaves_the_base_alone()
    {
        Assert.Equal(
            100,
            Run(new ImageBlendNode { Mode = BlendMode.Multiply }, new FlatNode(100), new FlatNode(255)),
            1);
    }

    [Fact]
    public void Screening_with_black_leaves_the_base_alone()
    {
        Assert.Equal(
            100,
            Run(new ImageBlendNode { Mode = BlendMode.Screen }, new FlatNode(100), new FlatNode(0)),
            1);
    }

    /// <summary>Screen is the inverse of multiply, and brightens.</summary>
    [Fact]
    public void Screen_brightens_where_multiply_darkens()
    {
        var screened = Run(
            new ImageBlendNode { Mode = BlendMode.Screen }, new FlatNode(128), new FlatNode(128));

        // 255 - 127*127/255 is 191.75, and an 8-bit frame rounds it.
        Assert.Equal(192, screened, 1);
    }

    /// <summary>
    /// Flat-field correction: dividing a frame by itself gives an even field
    /// at full scale, which is what makes the mode useful for undoing uneven
    /// lighting.
    /// </summary>
    [Fact]
    public void Dividing_by_itself_gives_an_even_field()
    {
        Assert.Equal(
            255,
            Run(new ImageBlendNode { Mode = BlendMode.Divide }, new FlatNode(90), new FlatNode(90)),
            1);
    }

    [Theory]
    [InlineData(BlendMode.Overlay, 64, 128, 64)]
    [InlineData(BlendMode.HardLight, 128, 64, 64)]
    public void Overlay_and_hard_light_are_the_same_with_the_roles_swapped(
        BlendMode mode, double a, double b, double expected) =>
        Assert.Equal(expected, Run(new ImageBlendNode { Mode = mode }, new FlatNode(a), new FlatNode(b)), 2);

    /// <summary>
    /// Opacity fades the blended result towards the base, not the blend
    /// image towards the base - the difference shows on any mode but Normal.
    /// </summary>
    [Fact]
    public void Opacity_fades_the_result_towards_the_base()
    {
        Assert.Equal(
            150,
            Run(
                new ImageBlendNode { Mode = BlendMode.Normal, Opacity = 0.5 },
                new FlatNode(100),
                new FlatNode(200)),
            1);
    }

    [Fact]
    public void Zero_opacity_leaves_the_base_untouched()
    {
        Assert.Equal(
            100,
            Run(
                new ImageBlendNode { Mode = BlendMode.Difference, Opacity = 0 },
                new FlatNode(100),
                new FlatNode(200)),
            1);
    }

    /// <summary>
    /// A mask limits the blend. Over half a frame, the mean lands halfway
    /// between the base and the fully blended result.
    /// </summary>
    [Fact]
    public void A_mask_limits_the_blend_to_where_it_allows()
    {
        var graph = new VisionGraph();
        var baseImage = new FlatNode(100);
        var blend = new FlatNode(200);
        var node = new ImageBlendNode { Mode = BlendMode.Normal };

        graph.Add(baseImage);
        graph.Add(blend);
        graph.Add(node);
        graph.TryConnect(baseImage.ImageOut, node.FirstIn);
        graph.TryConnect(blend.ImageOut, node.SecondIn);

        var mask = new HalfMaskNode();
        graph.Add(mask);
        graph.TryConnect(mask.ImageOut, node.MaskIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var result = scheduler.GetOutput<ImageData>(node.ImageOut)!;
        Assert.Equal(150, CvInvoke.Mean(result.Mat).V0, 1);
    }

    /// <summary>White over the top half, black over the bottom.</summary>
    private sealed class HalfMaskNode : VisionNode
    {
        public HalfMaskNode() => ImageOut = AddOutput<ImageData>("Out");

        public OutputPort ImageOut { get; }

        protected override void OnEvaluate(EvalContext context)
        {
            var mat = new Mat(8, 8, DepthType.Cv8U, 1);
            mat.SetTo(new MCvScalar(0));

            using var top = new Mat(mat, new System.Drawing.Rectangle(0, 0, 8, 4));
            top.SetTo(new MCvScalar(255));

            context.Set(ImageOut, ImageData.Adopt(mat));
        }
    }

    // ---- mismatches ---------------------------------------------------------

    /// <summary>
    /// Cropping to the overlap, padding or resampling would each hide a
    /// mistake in the recipe. The message has to say which way they differ,
    /// or the operator is left comparing two node panels by eye.
    /// </summary>
    [Fact]
    public void Images_of_different_sizes_are_refused_by_name()
    {
        var message = Failure(
            new ImageLogicNode(),
            new FlatNode(255, width: 8, height: 8),
            new FlatNode(255, width: 16, height: 8));

        Assert.Contains("8x8", message, StringComparison.Ordinal);
        Assert.Contains("16x8", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Images_with_different_channel_counts_are_refused()
    {
        var message = Failure(
            new ImageBlendNode(),
            new FlatNode(255, channels: 1),
            new FlatNode(255, channels: 3));

        Assert.Contains("channels", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Images_of_different_depths_are_refused()
    {
        var message = Failure(
            new ImageBlendNode(),
            new FlatNode(255, depth: DepthType.Cv8U),
            new FlatNode(255, depth: DepthType.Cv32F));

        Assert.Contains("depth", message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- colour and depth ---------------------------------------------------

    [Fact]
    public void A_colour_image_blends_channel_by_channel()
    {
        Assert.Equal(
            64,
            Run(
                new ImageBlendNode { Mode = BlendMode.Multiply },
                new FlatNode(128, channels: 3),
                new FlatNode(128, channels: 3)),
            1);
    }

    /// <summary>
    /// A float frame runs from zero to one, so the scaling that makes an
    /// 8-bit multiply work must not be applied to it.
    /// </summary>
    [Fact]
    public void A_float_image_multiplies_against_one_rather_than_255()
    {
        Assert.Equal(
            0.25,
            Run(
                new ImageBlendNode { Mode = BlendMode.Multiply },
                new FlatNode(0.5, depth: DepthType.Cv32F),
                new FlatNode(0.5, depth: DepthType.Cv32F)),
            3);
    }
}
