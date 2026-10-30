using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Brightness inside a region or inside each shape. The picture is 50 on
/// the left half and 200 on the right, so every answer is known.
/// </summary>
public class RegionStatsTests
{
    public RegionStatsTests() => TestCatalog.Ensure();

    private static Mat Halves()
    {
        var mat = new Mat(200, 200, DepthType.Cv8U, 1);
        mat.SetTo(new MCvScalar(50));
        CvInvoke.Rectangle(mat, new System.Drawing.Rectangle(100, 0, 100, 200), new MCvScalar(200), -1);
        return mat;
    }

    [Fact]
    public void A_region_inside_one_half_reads_its_level()
    {
        var node = new RegionStatsNode { Region = new OrientedRect(50, 100, 40, 40, 0) };
        var result = Run(node);

        Assert.Equal(50, result.Get<double>(node.MeanOut), 6);
        Assert.Equal(0, result.Get<double>(node.StdDevOut), 6);
        Assert.Equal(50, result.Get<double>(node.MinimumOut));
        Assert.Equal(50, result.Get<double>(node.MaximumOut));
    }

    /// <summary>Half on each side: the mean between, the spread the half-difference, darkest and brightest the two.</summary>
    [Fact]
    public void A_region_across_the_boundary_reads_both()
    {
        var node = new RegionStatsNode { Region = new OrientedRect(100, 100, 40, 40, 0) };
        var result = Run(node);

        Assert.InRange(result.Get<double>(node.MeanOut), 120, 130);
        Assert.InRange(result.Get<double>(node.StdDevOut), 70, 76);
        Assert.Equal(50, result.Get<double>(node.MinimumOut));
        Assert.Equal(200, result.Get<double>(node.MaximumOut));
    }

    [Fact]
    public void Each_shape_wired_in_gets_its_own_reading()
    {
        var node = new RegionStatsNode();
        var result = Run(
            node,
            new CircleShape(new Point2(40, 40), 15),
            new CircleShape(new Point2(160, 160), 15),
            new PointShape(new Point2(60, 150)));

        var shapes = result.Shapes(node.ShapesOut).Shapes;

        Assert.Equal([50d, 200d, 50d], shapes.Select(shape => shape.Metric(ShapeMetric.Mean)!.Value));
        Assert.Equal(50, result.Get<double>(node.MeanOut), 6);

        var table = ShapeTable.From(result.Shapes(node.ShapesOut));
        Assert.Contains("Mean", table.Columns);
        Assert.Contains("Std Dev", table.Columns);
    }

    /// <summary>A shape hanging over the edge is measured on the part of it that is there.</summary>
    [Fact]
    public void A_shape_over_the_edge_is_measured_on_what_is_inside()
    {
        using var image = Halves();

        var reading = RegionStatsNode.Measure(image, new CircleShape(new Point2(195, 100), 20), 1);

        Assert.NotNull(reading);
        Assert.Equal(200, reading.Value.Mean, 6);
        Assert.True(reading.Value.Pixels < Math.PI * 20 * 20);
        Assert.Null(RegionStatsNode.Measure(image, new CircleShape(new Point2(500, 500), 20), 1));
    }

    [Fact]
    public void Filter_shapes_can_keep_the_dark_ones()
    {
        var stats = new RegionStatsNode();
        var filter = new FilterShapesNode { Measure = ShapeMeasure.Mean, Range = new ValueRange(null, 100) };

        var graph = new VisionGraph();
        var picture = new PictureNode(Halves);
        var shapes = new ShapesNode(
            new ShapeSet([new CircleShape(new Point2(40, 40), 15), new CircleShape(new Point2(160, 160), 15)], 200, 200));

        graph.Add(picture);
        graph.Add(shapes);
        graph.Add(stats);
        graph.Add(filter);
        graph.TryConnect(picture.Out, stats.ImageIn);
        graph.TryConnect(shapes.Out, stats.ShapesIn);
        graph.TryConnect(stats.ShapesOut, filter.ShapesIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var kept = Assert.IsType<CircleShape>(Assert.Single(scheduler.GetOutput<ShapeSet>(filter.KeptOut)!.Shapes));
        Assert.Equal(new Point2(40, 40), kept.Centre);
    }

    // ---- plumbing ----------------------------------------------------------------------

    private sealed class Outputs(GraphScheduler scheduler)
    {
        public T Get<T>(OutputPort port) => (T)scheduler.GetOutput(port)!;

        public ShapeSet Shapes(OutputPort port) => scheduler.GetOutput<ShapeSet>(port)!;
    }

    private static Outputs Run(RegionStatsNode node, params Shape[] shapes)
    {
        var graph = new VisionGraph();
        var picture = new PictureNode(Halves);
        graph.Add(picture);
        graph.Add(node);
        graph.TryConnect(picture.Out, node.ImageIn);

        if (shapes.Length > 0)
        {
            var source = new ShapesNode(new ShapeSet(shapes, 200, 200));
            graph.Add(source);
            graph.TryConnect(source.Out, node.ShapesIn);
        }

        var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();
        return new Outputs(scheduler);
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

    private sealed class PictureNode : VisionNode
    {
        private readonly Func<Mat> _picture;

        public PictureNode(Func<Mat> picture)
        {
            _picture = picture;
            Out = AddOutput<ImageData>("Out");
        }

        public OutputPort Out { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(Out, ImageData.Adopt(_picture()));
    }

    private sealed class ShapesNode : VisionNode
    {
        private readonly ShapeSet _set;

        public ShapesNode(ShapeSet set)
        {
            _set = set;
            Out = AddOutput<ShapeSet>("Out");
        }

        public OutputPort Out { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(Out, _set);
    }
}
