using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The tools carried over from the later Sirius Studio build.
/// </summary>
public class PortedToolTests
{
    public PortedToolTests() => TestCatalog.Ensure();

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

    /// <summary>A three-channel source with a distinct value per channel.</summary>
    [VisionNode("Test Colour", TypeKey = "test.colour", Category = NodeCategory.Source)]
    private sealed class ColourSourceNode : VisionNode
    {
        public ColourSourceNode() => ImageOut = AddOutput<ImageData>("Out");

        public OutputPort ImageOut { get; }

        protected override void OnEvaluate(EvalContext context)
        {
            var mat = new Mat(32, 32, DepthType.Cv8U, 3);
            mat.SetTo(new MCvScalar(10, 120, 240)); // B, G, R
            context.Set(ImageOut, ImageData.Adopt(mat));
        }
    }

    /// <summary>A white square on black, with a hole in it.</summary>
    [VisionNode("Test Washer", TypeKey = "test.washer", Category = NodeCategory.Source)]
    private sealed class WasherSourceNode : VisionNode
    {
        public WasherSourceNode() => ImageOut = AddOutput<ImageData>("Out");

        public OutputPort ImageOut { get; }

        protected override void OnEvaluate(EvalContext context)
        {
            var mat = new Mat(100, 100, DepthType.Cv8U, 1);
            mat.SetTo(new MCvScalar(0));

            // 40x40 solid square with a 10x10 hole: area 1600 - 100 = 1500.
            CvInvoke.Rectangle(mat, new System.Drawing.Rectangle(20, 20, 40, 40), new MCvScalar(255), -1);
            CvInvoke.Rectangle(mat, new System.Drawing.Rectangle(35, 35, 10, 10), new MCvScalar(0), -1);

            context.Set(ImageOut, ImageData.Adopt(mat));
        }
    }

    private static async Task<ImageData> RunAsync(VisionGraph graph, GraphScheduler scheduler, OutputPort port)
    {
        var result = await scheduler.RunAsync();

        // Name the offending node and its message: "1 node(s) failed" alone
        // gives nothing to act on.
        var failures = graph.Nodes
            .Where(node => node.Status == NodeStatus.Error)
            .Select(node => $"{node.Name}: {node.StatusMessage}")
            .ToArray();

        Assert.True(
            result.Success,
            result.Error ?? string.Join(Environment.NewLine, failures));

        return scheduler.GetOutput<ImageData>(port)!;
    }

    [Fact]
    public async Task Invert_flips_intensities()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 16, Height = 16, Value = 200 };
        var invert = new InvertNode();
        graph.Add(source);
        graph.Add(invert);
        graph.TryConnect(source.ImageOut, invert.ImageIn);

        using var scheduler = CreateScheduler(graph);
        var image = await RunAsync(graph, scheduler, invert.ImageOut);

        Assert.Equal(55, CvInvoke.Mean(image.Mat).V0, 0);
    }

    [Fact]
    public async Task Rgb_channel_extracts_the_requested_plane()
    {
        var graph = new VisionGraph();
        var source = new ColourSourceNode();
        var channel = new RgbChannelNode { Channel = RgbChannelNode.ColourChannel.Red };
        graph.Add(source);
        graph.Add(channel);
        graph.TryConnect(source.ImageOut, channel.ImageIn);

        using var scheduler = CreateScheduler(graph);
        var image = await RunAsync(graph, scheduler, channel.ImageOut);

        Assert.Equal(1, image.Channels);
        Assert.Equal(240, CvInvoke.Mean(image.Mat).V0, 0);

        channel.Channel = RgbChannelNode.ColourChannel.Blue;
        var blue = await RunAsync(graph, scheduler, channel.ImageOut);
        Assert.Equal(10, CvInvoke.Mean(blue.Mat).V0, 0);
    }

    [Fact]
    public async Task Colour_range_masks_pixels_inside_the_bounds()
    {
        var graph = new VisionGraph();
        var source = new ColourSourceNode();
        var range = new ColourRangeNode
        {
            Blue = new ValueRange(0, 50),
            Green = new ValueRange(100, 150),
            Red = new ValueRange(200, 255),
        };
        graph.Add(source);
        graph.Add(range);
        graph.TryConnect(source.ImageOut, range.ImageIn);

        using var scheduler = CreateScheduler(graph);
        var inside = await RunAsync(graph, scheduler, range.ImageOut);
        Assert.Equal(255, CvInvoke.Mean(inside.Mat).V0, 0);

        // Move the window off the source colour; nothing should match.
        range.Red = new ValueRange(250, 255);
        var outside = await RunAsync(graph, scheduler, range.ImageOut);
        Assert.Equal(0, CvInvoke.Mean(outside.Mat).V0, 0);
    }

    [Fact]
    public async Task Canny_finds_an_edge_and_ignores_a_flat_image()
    {
        var graph = new VisionGraph();
        var flat = new ConstantImageNode { Width = 64, Height = 64, Value = 128 };
        var canny = new CannyNode { Threshold1 = 50, Threshold2 = 150 };
        graph.Add(flat);
        graph.Add(canny);
        graph.TryConnect(flat.ImageOut, canny.ImageIn);

        using var scheduler = CreateScheduler(graph);
        var image = await RunAsync(graph, scheduler, canny.ImageOut);

        // A uniform image has no edges at all.
        Assert.Equal(0, CvInvoke.Mean(image.Mat).V0, 0);
    }

    /// <summary>
    /// Swapped thresholds would otherwise produce a near-empty result rather
    /// than an error, which is easy to mistake for "no edges here".
    /// </summary>
    [Fact]
    public async Task Canny_tolerates_thresholds_given_the_wrong_way_round()
    {
        var graph = new VisionGraph();
        var source = new WasherSourceNode();
        var canny = new CannyNode { Threshold1 = 200, Threshold2 = 50 };
        graph.Add(source);
        graph.Add(canny);
        graph.TryConnect(source.ImageOut, canny.ImageIn);

        using var scheduler = CreateScheduler(graph);
        var image = await RunAsync(graph, scheduler, canny.ImageOut);

        Assert.True(CvInvoke.Mean(image.Mat).V0 > 0, "no edges found with reversed thresholds");
    }

    [Fact]
    public async Task Filter2D_with_an_identity_kernel_leaves_the_image_alone()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 32, Height = 32, Value = 90 };
        var filter = new Filter2DNode();
        graph.Add(source);
        graph.Add(filter);
        graph.TryConnect(source.ImageOut, filter.ImageIn);

        using var scheduler = CreateScheduler(graph);
        var image = await RunAsync(graph, scheduler, filter.ImageOut);

        Assert.Equal(90, CvInvoke.Mean(image.Mat).V0, 0);
    }

    [Fact]
    public async Task Filter2D_reports_a_kernel_that_does_not_match_its_size()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode();
        var filter = new Filter2DNode { Kernel = "1 2 3" };
        graph.Add(source);
        graph.Add(filter);
        graph.TryConnect(source.ImageOut, filter.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Equal(NodeStatus.Error, filter.Status);
        Assert.Contains("9 values", filter.StatusMessage!);
    }

    [Theory]
    [InlineData("0 0 0; 0 1 0; 0 0 0", true)]
    [InlineData("0,0,0,0,1,0,0,0,0", true)]
    [InlineData("1 2 3 4", false)]
    [InlineData("0 0 0; 0 x 0; 0 0 0", false)]
    public void Kernel_text_is_parsed_leniently_but_validated(string text, bool expected)
    {
        var values = Filter2DNode.ParseKernel(text, 3, out var error);

        Assert.Equal(expected, values is not null);
        if (!expected)
            Assert.NotEmpty(error);
    }

    [Fact]
    public async Task Polar_to_linear_defaults_to_the_image_centre()
    {
        var graph = new VisionGraph();
        var source = new WasherSourceNode();
        var polar = new PolarToLinearNode();
        graph.Add(source);
        graph.Add(polar);
        graph.TryConnect(source.ImageOut, polar.ImageIn);

        using var scheduler = CreateScheduler(graph);
        var image = await RunAsync(graph, scheduler, polar.ImageOut);

        // Defaults resolve at evaluation, so the unwrap must actually contain
        // the shape rather than coming out empty.
        Assert.Equal(100, image.Width);
        Assert.True(CvInvoke.Mean(image.Mat).V0 > 0, "the unwrapped image is empty");
    }

    [Fact]
    public async Task Blob_filter_subtracts_holes_from_the_area()
    {
        var graph = new VisionGraph();
        var source = new WasherSourceNode();

        // The square is 1600px with a 100px hole, so it measures 1500.
        var blobs = new BlobFilterNode { Area = new ValueRange(1400, 1600) };
        graph.Add(source);
        graph.Add(blobs);
        graph.TryConnect(source.ImageOut, blobs.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await RunAsync(graph, scheduler, blobs.ImageOut);

        Assert.Equal(1, scheduler.GetOutput(blobs.CountOut));
    }

    [Fact]
    public async Task Blob_filter_rejects_a_blob_outside_the_range()
    {
        var graph = new VisionGraph();
        var source = new WasherSourceNode();

        // 1700 would only match if the hole were counted as solid.
        var blobs = new BlobFilterNode { Area = new ValueRange(1601, null) };
        graph.Add(source);
        graph.Add(blobs);
        graph.TryConnect(source.ImageOut, blobs.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Equal(0, scheduler.GetOutput(blobs.CountOut));
        Assert.Equal(NodeStatus.Warning, blobs.Status);
    }

    [Fact]
    public async Task Blob_filter_explains_a_colour_input()
    {
        var graph = new VisionGraph();
        var source = new ColourSourceNode();
        var blobs = new BlobFilterNode();
        graph.Add(source);
        graph.Add(blobs);
        graph.TryConnect(source.ImageOut, blobs.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Equal(NodeStatus.Error, blobs.Status);
        Assert.Contains("Threshold", blobs.StatusMessage!);
    }
}
