using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Shapes;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Keeping the part of an image something marks.
/// </summary>
/// <remarks>
/// Distinct from Region of Interest, which cuts a rectangle out and changes
/// the frame. This keeps the frame, so everything measured afterwards is
/// still in the original coordinates - which is the reason to have both.
/// </remarks>
public class MaskNodeTests
{
    public MaskNodeTests() => TestCatalog.Ensure();

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
        public FlatNode(double value, int channels = 1, int size = 20)
        {
            Value = value;
            Channels = channels;
            Size = size;
            ImageOut = AddOutput<ImageData>("Out");
        }

        public double Value { get; }

        public int Channels { get; }

        public int Size { get; }

        public OutputPort ImageOut { get; }

        protected override void OnEvaluate(EvalContext context)
        {
            var mat = new Mat(Size, Size, DepthType.Cv8U, Channels);
            mat.SetTo(new MCvScalar(Value, Value, Value, Value));
            context.Set(ImageOut, ImageData.Adopt(mat));
        }
    }

    /// <summary>White over the left half of a 20x20 frame.</summary>
    private sealed class HalfMaskNode : VisionNode
    {
        public HalfMaskNode() => ImageOut = AddOutput<ImageData>("Out");

        public OutputPort ImageOut { get; }

        protected override void OnEvaluate(EvalContext context)
        {
            var mat = new Mat(20, 20, DepthType.Cv8U, 1);
            mat.SetTo(new MCvScalar(0));

            using (var left = new Mat(mat, new System.Drawing.Rectangle(0, 0, 10, 20)))
                left.SetTo(new MCvScalar(255));

            context.Set(ImageOut, ImageData.Adopt(mat));
        }
    }

    /// <summary>One shape, measured on a 20x20 frame.</summary>
    private sealed class ShapeNode : VisionNode
    {
        private readonly Shape _shape;

        public ShapeNode(Shape shape)
        {
            _shape = shape;
            ShapesOut = AddOutput<ShapeSet>("Shapes");
        }

        public OutputPort ShapesOut { get; }

        protected override void OnEvaluate(EvalContext context) =>
            context.Set(ShapesOut, new ShapeSet([_shape], 20, 20));
    }

    private static (MaskNode Node, GraphScheduler Scheduler) Build(
        FlatNode image, HalfMaskNode? mask = null, ShapeNode? shapes = null)
    {
        var graph = new VisionGraph();
        var node = new MaskNode();

        graph.Add(image);
        graph.Add(node);
        graph.TryConnect(image.ImageOut, node.ImageIn);

        if (mask is not null)
        {
            graph.Add(mask);
            graph.TryConnect(mask.ImageOut, node.MaskIn);
        }

        if (shapes is not null)
        {
            graph.Add(shapes);
            graph.TryConnect(shapes.ShapesOut, node.ShapesIn);
        }

        return (node, CreateScheduler(graph));
    }

    private static double MeanOf(GraphScheduler scheduler, OutputPort port) =>
        CvInvoke.Mean(scheduler.GetOutput<ImageData>(port)!.Mat).V0;

    // ---- a mask image -------------------------------------------------------

    [Fact]
    public async Task Half_a_frame_masked_keeps_half_the_signal()
    {
        var (node, scheduler) = Build(new FlatNode(200), new HalfMaskNode());
        using var _ = scheduler;
        await scheduler.RunAsync();

        // 200 over the left half, the fill of 0 over the right.
        Assert.Equal(100, MeanOf(scheduler, node.ImageOut), 1);
    }

    [Fact]
    public async Task The_fill_decides_what_the_masked_out_part_becomes()
    {
        var (node, scheduler) = Build(new FlatNode(200), new HalfMaskNode());
        node.Fill = 100;

        using var _ = scheduler;
        await scheduler.RunAsync();

        Assert.Equal(150, MeanOf(scheduler, node.ImageOut), 1);
    }

    [Fact]
    public async Task Inverting_keeps_the_other_half()
    {
        var (node, scheduler) = Build(new FlatNode(200), new HalfMaskNode());
        node.Invert = true;

        using var _ = scheduler;
        await scheduler.RunAsync();

        // The same mean either way, so the mask itself is what to look at.
        var mask = scheduler.GetOutput<ImageData>(node.MaskOut)!;
        using var leftHalf = new Mat(mask.Mat, new System.Drawing.Rectangle(0, 0, 10, 20));

        Assert.Equal(100, MeanOf(scheduler, node.ImageOut), 1);
        Assert.Equal(0, CvInvoke.Mean(leftHalf).V0, 1);
    }

    /// <summary>The frame is kept, which is the whole difference from a crop.</summary>
    [Fact]
    public async Task The_frame_keeps_its_size()
    {
        var (node, scheduler) = Build(new FlatNode(200, size: 20), new HalfMaskNode());
        using var _ = scheduler;
        await scheduler.RunAsync();

        var masked = scheduler.GetOutput<ImageData>(node.ImageOut)!;

        Assert.Equal(20, masked.Width);
        Assert.Equal(20, masked.Height);
    }

    [Fact]
    public async Task A_colour_frame_keeps_its_channels_and_its_space()
    {
        var (node, scheduler) = Build(new FlatNode(200, channels: 3), new HalfMaskNode());
        using var _ = scheduler;
        await scheduler.RunAsync();

        var masked = scheduler.GetOutput<ImageData>(node.ImageOut)!;

        Assert.Equal(3, masked.Channels);
        Assert.Equal(ColourSpace.Bgr, masked.Space);
    }

    // ---- a shape ------------------------------------------------------------

    /// <summary>
    /// The point of taking shapes: a located feature becomes the region,
    /// with nobody typing coordinates.
    /// </summary>
    [Fact]
    public async Task A_shape_is_filled_in_as_the_mask()
    {
        var (node, scheduler) = Build(
            new FlatNode(200),
            shapes: new ShapeNode(new RectShape(new Bounds(0, 0, 10, 20))));

        using var _ = scheduler;
        await scheduler.RunAsync();

        Assert.Equal(100, MeanOf(scheduler, node.ImageOut), 1);
    }

    [Fact]
    public async Task A_circle_masks_to_its_own_area()
    {
        var (node, scheduler) = Build(
            new FlatNode(255),
            shapes: new ShapeNode(new CircleShape(new Point2(10, 10), 5)));

        using var _ = scheduler;
        await scheduler.RunAsync();

        // Measured against the area of the disc rather than a magic number,
        // with room for the rasteriser rounding at the rim.
        var mask = scheduler.GetOutput<ImageData>(node.MaskOut)!;
        var filled = CvInvoke.CountNonZero(mask.Mat);
        var area = Math.PI * 25;

        Assert.InRange(filled, area * 0.85, area * 1.15);
    }

    [Fact]
    public async Task The_mask_it_used_is_available_to_look_at()
    {
        var (node, scheduler) = Build(
            new FlatNode(200),
            shapes: new ShapeNode(new RectShape(new Bounds(0, 0, 10, 20))));

        using var _ = scheduler;
        await scheduler.RunAsync();

        var mask = scheduler.GetOutput<ImageData>(node.MaskOut)!;

        Assert.Equal(1, mask.Channels);
        Assert.Equal(127.5, CvInvoke.Mean(mask.Mat).V0, 1);
    }

    /// <summary>Both sources together mark the union, not one or the other.</summary>
    [Fact]
    public async Task An_image_and_a_shape_together_mark_both()
    {
        var (node, scheduler) = Build(
            new FlatNode(200),
            new HalfMaskNode(),
            new ShapeNode(new RectShape(new Bounds(10, 0, 10, 20))));

        using var _ = scheduler;
        await scheduler.RunAsync();

        Assert.Equal(200, MeanOf(scheduler, node.ImageOut), 1);
    }

    // ---- refusals -----------------------------------------------------------

    [Fact]
    public async Task With_nothing_to_mask_with_it_says_so()
    {
        var (node, scheduler) = Build(new FlatNode(200));
        using var _ = scheduler;
        await scheduler.RunAsync();

        Assert.Equal(NodeStatus.Error, node.Status);
        Assert.Contains("wire", node.StatusMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_mask_of_the_wrong_size_is_refused_by_name()
    {
        var (node, scheduler) = Build(new FlatNode(200, size: 30), new HalfMaskNode());
        using var _ = scheduler;
        await scheduler.RunAsync();

        Assert.Equal(NodeStatus.Error, node.Status);
        Assert.Contains("20x20", node.StatusMessage!, StringComparison.Ordinal);
        Assert.Contains("30x30", node.StatusMessage!, StringComparison.Ordinal);
    }
}
