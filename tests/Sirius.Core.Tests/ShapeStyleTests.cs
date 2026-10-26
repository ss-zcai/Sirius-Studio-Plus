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

    // ---- what styling must not destroy ------------------------------------

    /// <summary>
    /// Recolouring read text used to throw the text away. Labelling
    /// defaulted to None, and None meant "write nothing", which also
    /// meant "erase whatever is there" - so a Shape Style wired in to
    /// change the colour of an OCR result removed the one thing the
    /// operator was looking at.
    /// </summary>
    [Fact]
    public void Styling_keeps_a_label_the_shape_arrived_with()
    {
        var set = Style(
            node => node.Colour = "#FF0000",
            new PointShape(new Point2(10, 20)) { Label = "R13763128" });

        Assert.Equal("R13763128", set.Shapes[0].Label);
        Assert.Equal(0xFFFF0000, set.Shapes[0].Colour);
    }

    [Fact]
    public void Keeping_is_what_a_styler_does_by_default() =>
        Assert.Equal(ShapeLabelling.Keep, new ShapeStyleNode().Labelling);

    /// <summary>
    /// None still clears one, which is what it is for; it is simply no
    /// longer the default.
    /// </summary>
    [Fact]
    public void None_clears_a_label()
    {
        var set = Style(
            node => node.Labelling = ShapeLabelling.None,
            new PointShape(new Point2(10, 20)) { Label = "R13763128" });

        Assert.Null(set.Shapes[0].Label);
    }

    [Fact]
    public void Index_still_replaces_it()
    {
        var set = Style(
            node => node.Labelling = ShapeLabelling.Index,
            new PointShape(new Point2(10, 20)) { Label = "R13763128" },
            new PointShape(new Point2(30, 40)) { Label = "889071064827" });

        Assert.Equal("1", set.Shapes[0].Label);
        Assert.Equal("2", set.Shapes[1].Label);
    }

    /// <summary>
    /// The typeface and size travel too. Reshaping builds a new shape,
    /// so anything not carried over by hand is lost - and a caption
    /// sized in image pixels would snap back to a fixed size on screen
    /// the moment it was recoloured.
    /// </summary>
    [Theory]
    [InlineData(ShapeForm.Outline)]
    [InlineData(ShapeForm.BoundingBox)]
    [InlineData(ShapeForm.Centre)]
    public void Styling_keeps_how_the_label_is_written(ShapeForm form)
    {
        var set = Style(
            node => node.Form = form,
            new CircleShape(new Point2(50, 50), 20)
            {
                Label = "PASS",
                LabelSize = 36,
                LabelFont = "Georgia",
            });

        Assert.Equal("PASS", set.Shapes[0].Label);
        Assert.Equal(36, set.Shapes[0].LabelSize);
        Assert.Equal("Georgia", set.Shapes[0].LabelFont);
    }

    /// <summary>Styles a set through the real node.</summary>
    private static ShapeSet Style(Action<ShapeStyleNode> configure, params Shape[] shapes)
    {
        var graph = new VisionGraph();
        var source = new StyleSourceNode(shapes);
        var style = new ShapeStyleNode();
        configure(style);

        graph.Add(source);
        graph.Add(style);
        graph.TryConnect(source.ShapesOut, style.ShapesIn);

        using var scheduler = StyleScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        return (ShapeSet)scheduler.GetOutput(style.ShapesOut)!;
    }

    private sealed class StyleSourceNode : VisionNode
    {
        private readonly ShapeSet _set;

        public StyleSourceNode(Shape[] shapes)
        {
            _set = new ShapeSet(shapes, 640, 480);
            ShapesOut = AddOutput<ShapeSet>("Out");
        }

        public OutputPort ShapesOut { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(ShapesOut, _set);
    }

    private static GraphScheduler StyleScheduler(VisionGraph graph)
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
}
