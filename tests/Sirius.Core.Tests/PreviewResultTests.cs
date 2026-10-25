using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// A preview of a node's input shows what that node found in it.
/// </summary>
/// <remarks>
/// Setting a search tool up should be one window: draw the band, see the
/// circle it found, adjust, see it again. Before this the result had to be
/// wired to a separate viewer to be looked at - a round trip through the
/// canvas for something that belongs on the picture it was measured in.
/// </remarks>
public class PreviewResultTests
{
    public PreviewResultTests() => TestCatalog.Ensure();

    private const int Size = 400;

    /// <summary>A light disc on a dark field, with an analytic edge.</summary>
    private static ImageData Disc(double centreX, double centreY, double radius)
    {
        var mat = new Mat(Size, Size, DepthType.Cv8U, 1);
        var data = new byte[Size * Size];

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var distance = Math.Sqrt(
                    ((x - centreX) * (x - centreX)) + ((y - centreY) * (y - centreY)));

                var coverage = Math.Clamp(0.5 - (distance - radius), 0, 1);
                data[(y * Size) + x] = (byte)Math.Round(30 + (190 * coverage));
            }
        }

        mat.SetTo(data);
        return ImageData.Adopt(mat);
    }

    private sealed class PreparedImageNode : VisionNode
    {
        private readonly ImageData _image;

        public PreparedImageNode(ImageData image)
        {
            _image = image;
            ImageOut = AddOutput<ImageData>("Out");
        }

        public OutputPort ImageOut { get; }

        protected override void OnEvaluate(EvalContext context) =>
            context.Forward(ImageOut, _image);
    }

    private static (VisionGraph Graph, FindCircleNode Finder) Build(ImageData image)
    {
        var graph = new VisionGraph();
        var source = new PreparedImageNode(image);
        var finder = new FindCircleNode { Band = new Ring(200, 200, 80, 120) };

        graph.Add(source);
        graph.Add(finder);
        graph.TryConnect(source.ImageOut, finder.ImageIn);

        return (graph, finder);
    }

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

    [Fact]
    public void A_preview_of_the_input_shows_what_was_found()
    {
        using var image = Disc(200, 200, 100);
        var (graph, finder) = Build(image);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var preview = new PreviewPanelViewModel(
            finder, finder.ImageIn, null, new ShapeSelectionService());

        preview.Prepare(scheduler);
        preview.Commit();

        Assert.NotNull(preview.Shapes);
        Assert.Contains(preview.Shapes!.Shapes, shape => shape is CircleShape);

        preview.Dispose();
        _ = graph;
    }

    /// <summary>
    /// Green, so a result is not mistaken for the blue band that was drawn
    /// to look for it, and the edge points amber behind it.
    /// </summary>
    [Fact]
    public void A_result_is_coloured_as_a_result()
    {
        using var image = Disc(200, 200, 100);
        var (graph, finder) = Build(image);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var circle = ((ShapeSet)scheduler.GetOutput(finder.CircleOut)!)
            .Shapes.OfType<CircleShape>().Single();

        var point = ((ShapeSet)scheduler.GetOutput(finder.PointsOut)!)
            .Shapes.OfType<PointShape>().First();

        Assert.Equal(0xFF22C55E, circle.Colour);
        Assert.Equal(0xFFFBBF24, point.Colour);
        Assert.NotEqual(circle.Colour, point.Colour);

        _ = graph;
    }

    /// <summary>
    /// Keyed to the port the shapes came from, so picking the circle here
    /// highlights its row in a Shape Table reading the same wire.
    /// </summary>
    /// <remarks>
    /// The circle, not the edge points. Find Circle publishes both, and
    /// drawing both would break exactly this: the indices of a merged set
    /// match neither table.
    /// </remarks>
    [Fact]
    public void The_selection_is_keyed_to_the_result_port()
    {
        using var image = Disc(200, 200, 100);
        var (graph, finder) = Build(image);

        using var scheduler = CreateScheduler(graph);
        var preview = new PreviewPanelViewModel(
            finder, finder.ImageIn, null, new ShapeSelectionService());

        Assert.Same(finder.CircleOut, Producer(preview));

        preview.Dispose();
        _ = graph;
    }

    /// <summary>
    /// And what is drawn is that port's set exactly, so index 0 on the
    /// image is row 0 in the table.
    /// </summary>
    [Fact]
    public void Only_the_result_is_drawn_not_the_evidence()
    {
        using var image = Disc(200, 200, 100);
        var (graph, finder) = Build(image);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var preview = new PreviewPanelViewModel(
            finder, finder.ImageIn, null, new ShapeSelectionService());

        preview.Prepare(scheduler);
        preview.Commit();

        var expected = (ShapeSet)scheduler.GetOutput(finder.CircleOut)!;

        Assert.Equal(expected.Count, preview.Shapes!.Count);
        Assert.All(preview.Shapes.Shapes, shape => Assert.IsType<CircleShape>(shape));

        preview.Dispose();
        _ = graph;
    }

    private static OutputPort? Producer(PreviewPanelViewModel preview) =>
        (OutputPort?)typeof(ImagePanelViewModel)
            .GetProperty("ShapeProducer", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(preview);

    /// <summary>
    /// Shapes measured in another frame are left out rather than drawn in
    /// the wrong place, which would read as a bad result.
    /// </summary>
    [Fact]
    public void Shapes_from_another_frame_are_not_drawn()
    {
        using var image = Disc(200, 200, 100);

        var graph = new VisionGraph();
        var source = new PreparedImageNode(image);
        var stray = new ForeignShapeNode();

        graph.Add(source);
        graph.Add(stray);
        graph.TryConnect(source.ImageOut, stray.ImageIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var preview = new PreviewPanelViewModel(
            stray, stray.ImageIn, null, new ShapeSelectionService());

        preview.Prepare(scheduler);
        preview.Commit();

        Assert.True(preview.Shapes is null || preview.Shapes.Count == 0);

        preview.Dispose();
    }

    /// <summary>Emits shapes that belong to a different sized image.</summary>
    private sealed class ForeignShapeNode : VisionNode
    {
        public ForeignShapeNode()
        {
            ImageIn = AddInput<ImageData>("In");
            ShapesOut = AddOutput<ShapeSet>("Out");
        }

        public InputPort ImageIn { get; }

        public OutputPort ShapesOut { get; }

        protected override void OnEvaluate(EvalContext context) =>
            context.Set(
                ShapesOut,
                new ShapeSet([new PointShape(new Point2(5, 5))], 4000, 4000));
    }
}
