using Sirius.Core.Graph;
using Sirius.Core.Shapes;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Restyling a set of shapes.
/// </summary>
/// <remarks>
/// The labelling cases are here because of a real fault: the viewer numbered
/// every shape itself, so setting Labels to None changed nothing on screen.
/// A node that says "no labels" has to actually produce none, and that is
/// only checkable at this boundary.
/// </remarks>
public class ShapeStyleTests
{
    public ShapeStyleTests() => TestCatalog.Ensure();

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

    /// <summary>Two squares, with an area measurement on each.</summary>
    private sealed class ShapeSourceNode : VisionNode
    {
        public ShapeSourceNode() => ShapesOut = AddOutput<ShapeSet>("Out");

        public OutputPort ShapesOut { get; }

        protected override void OnEvaluate(EvalContext context)
        {
            Shape Square(double x, double y, double size, double area) =>
                new PolygonShape(
                [
                    new Point2(x, y),
                    new Point2(x + size, y),
                    new Point2(x + size, y + size),
                    new Point2(x, y + size),
                ])
                {
                    Metrics = new Dictionary<string, double> { [ShapeMetric.Area] = area },
                };

            context.Set(
                ShapesOut,
                new ShapeSet([Square(10, 10, 20, 400), Square(100, 50, 40, 1600)], 640, 480));
        }
    }

    private static async Task<ShapeSet> RunAsync(Action<ShapeStyleNode> configure)
    {
        var graph = new VisionGraph();
        var source = new ShapeSourceNode();
        var style = new ShapeStyleNode();
        configure(style);

        graph.Add(source);
        graph.Add(style);
        graph.TryConnect(source.ShapesOut, style.ShapesIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        return scheduler.GetOutput<ShapeSet>(style.ShapesOut)!;
    }

    // ---- labelling -------------------------------------------------------

    [Fact]
    public async Task No_labels_means_no_labels()
    {
        var styled = await RunAsync(node => node.Labelling = ShapeLabelling.None);

        Assert.All(styled.Shapes, shape => Assert.Null(shape.Label));
    }

    [Fact]
    public async Task Labelling_by_index_counts_from_one()
    {
        var styled = await RunAsync(node => node.Labelling = ShapeLabelling.Index);

        Assert.Equal("1", styled.Shapes[0].Label);
        Assert.Equal("2", styled.Shapes[1].Label);
    }

    [Fact]
    public async Task Labelling_by_measure_writes_the_value()
    {
        var styled = await RunAsync(node =>
        {
            node.Labelling = ShapeLabelling.Measure;
            node.LabelMeasure = ShapeMeasure.Area;
        });

        Assert.Equal("400", styled.Shapes[0].Label);
        Assert.Equal("1600", styled.Shapes[1].Label);
    }

    /// <summary>
    /// A measure the producer never took has nothing to write, and must not
    /// invent a zero.
    /// </summary>
    [Fact]
    public async Task Labelling_by_a_measure_that_was_not_taken_writes_nothing()
    {
        var styled = await RunAsync(node =>
        {
            node.Labelling = ShapeLabelling.Measure;
            node.LabelMeasure = ShapeMeasure.Solidity;
        });

        Assert.All(styled.Shapes, shape => Assert.Null(shape.Label));
    }

    // ---- colour and weight ----------------------------------------------

    [Theory]
    [InlineData("#22C55E", 0xFF22C55Eu)]
    [InlineData("22C55E", 0xFF22C55Eu)]
    [InlineData("#8022C55E", 0x8022C55Eu)]
    public async Task A_colour_is_read_from_hex(string hex, uint expected)
    {
        var styled = await RunAsync(node => node.Colour = hex);

        Assert.All(styled.Shapes, shape => Assert.Equal(expected, shape.Colour));
    }

    /// <summary>
    /// A typo in a colour is a presentation detail; losing the whole result
    /// over it would be a poor trade.
    /// </summary>
    [Fact]
    public async Task A_colour_that_is_not_a_colour_warns_and_carries_on()
    {
        var graph = new VisionGraph();
        var source = new ShapeSourceNode();
        var style = new ShapeStyleNode { Colour = "chartreuse" };

        graph.Add(source);
        graph.Add(style);
        graph.TryConnect(source.ShapesOut, style.ShapesIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Equal(NodeStatus.Warning, style.Status);
        Assert.Equal(2, scheduler.GetOutput<ShapeSet>(style.ShapesOut)!.Count);
    }

    [Fact]
    public async Task The_stroke_width_is_carried_on_each_shape()
    {
        var styled = await RunAsync(node => node.StrokeWidth = 3.5);

        Assert.All(styled.Shapes, shape => Assert.Equal(3.5, shape.StrokeWidth));
    }

    // ---- form -------------------------------------------------------------

    [Fact]
    public async Task The_outline_form_leaves_the_geometry_alone()
    {
        var styled = await RunAsync(node => node.Form = ShapeForm.Outline);

        Assert.All(styled.Shapes, shape => Assert.IsType<PolygonShape>(shape));
    }

    [Fact]
    public async Task The_bounding_box_form_gives_an_upright_rectangle()
    {
        var styled = await RunAsync(node => node.Form = ShapeForm.BoundingBox);

        var rect = Assert.IsType<RectShape>(styled.Shapes[0]);

        Assert.Equal(10, rect.Rect.X, 3);
        Assert.Equal(20, rect.Rect.Width, 3);
    }

    [Fact]
    public async Task The_centre_form_gives_a_point()
    {
        var styled = await RunAsync(node => node.Form = ShapeForm.Centre);

        var point = Assert.IsType<PointShape>(styled.Shapes[0]);

        Assert.Equal(20, point.At.X, 3);
        Assert.Equal(20, point.At.Y, 3);
    }

    /// <summary>
    /// OpenCV may label either side of a minimum-area rectangle as its width.
    /// The long side is always the width here, so a region cut from one comes
    /// out the same way round every time.
    /// </summary>
    [Fact]
    public async Task The_minimum_rect_form_puts_the_long_side_first()
    {
        var styled = await RunAsync(node => node.Form = ShapeForm.MinimumRect);

        var rect = Assert.IsType<OrientedRectShape>(styled.Shapes[1]);

        Assert.True(
            rect.Width >= rect.Height,
            $"the long side should be the width, got {rect.Width} by {rect.Height}");
    }

    /// <summary>Restyling must not throw away what was measured.</summary>
    [Fact]
    public async Task Measurements_survive_a_change_of_form()
    {
        var styled = await RunAsync(node => node.Form = ShapeForm.BoundingBox);

        Assert.Equal(400, styled.Shapes[0].Metric(ShapeMetric.Area));
        Assert.Equal(1600, styled.Shapes[1].Metric(ShapeMetric.Area));
    }
}
