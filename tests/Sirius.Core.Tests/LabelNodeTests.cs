using Sirius.Core.Graph;
using Sirius.Core.Shapes;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Writing a value onto the image.
/// </summary>
/// <remarks>
/// How a number gets from the graph onto the picture: a measurement, a
/// count, a code read from somewhere - anything that has to be seen by
/// whoever is looking at the result rather than only consumed by the next
/// node.
///
/// Built on <c>Shape.Label</c>, which the viewer has drawn all along, so
/// the node's job is only to make a shape to hang the text on.
/// </remarks>
public class LabelNodeTests
{
    public LabelNodeTests() => TestCatalog.Ensure();

    /// <summary>Publishes a fixed shape set, standing in for a find tool.</summary>
    private sealed class ShapeSourceNode : VisionNode
    {
        public ShapeSourceNode(params Shape[] shapes)
        {
            Set = new ShapeSet(shapes, 640, 480);
            ShapesOut = AddOutput<ShapeSet>("Out");
        }

        public ShapeSet Set { get; }

        public OutputPort ShapesOut { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(ShapesOut, Set);
    }

    private static ShapeSet Run(Action<LabelNode> configure, params Shape[] attached)
    {
        var graph = new VisionGraph();
        var label = new LabelNode();
        configure(label);
        graph.Add(label);

        if (attached.Length > 0)
        {
            var source = new ShapeSourceNode(attached);
            graph.Add(source);
            graph.TryConnect(source.ShapesOut, label.ShapesIn);
        }

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        return (ShapeSet)scheduler.GetOutput(label.ShapesOut)!;
    }

    // ---- what it writes --------------------------------------------------------

    [Fact]
    public void It_writes_text_at_a_point()
    {
        var set = Run(node =>
        {
            node.Text = "PASS";
            node.Position = new Point2(120, 80);
        });

        var point = Assert.IsType<PointShape>(Assert.Single(set.Shapes));

        Assert.Equal("PASS", point.Label);
        Assert.Equal(new Point2(120, 80), point.At);
    }

    /// <summary>
    /// With nothing written, the number is - which is what makes a
    /// measurement wired straight in useful without also typing a format.
    /// </summary>
    [Fact]
    public void A_blank_text_writes_the_number()
    {
        var set = Run(node =>
        {
            node.Number = 12.3456;
            node.Decimals = 2;
        });

        Assert.Equal("12.35", ((PointShape)set.Shapes[0]).Label);
    }

    [Fact]
    public void Text_wins_over_the_number()
    {
        var set = Run(node =>
        {
            node.Text = "OK";
            node.Number = 99;
        });

        Assert.Equal("OK", ((PointShape)set.Shapes[0]).Label);
    }

    [Theory]
    [InlineData(0, "12")]
    [InlineData(1, "12.3")]
    [InlineData(4, "12.3456")]
    public void The_decimals_setting_is_honoured(int decimals, string expected)
    {
        var set = Run(node =>
        {
            node.Number = 12.3456;
            node.Decimals = decimals;
        });

        Assert.Equal(expected, ((PointShape)set.Shapes[0]).Label);
    }

    // ---- labelling shapes -------------------------------------------------------

    /// <summary>
    /// One label per shape, at each shape's own centre - so a reading can
    /// be written on every blob rather than once beside the lot.
    /// </summary>
    [Fact]
    public void It_labels_every_shape_wired_in()
    {
        var set = Run(
            node => node.Text = "x",
            new CircleShape(new Point2(10, 20), 5),
            new CircleShape(new Point2(100, 200), 5));

        Assert.Equal(2, set.Count);
        Assert.All(set.Shapes, shape => Assert.Equal("x", shape.Label));

        Assert.Equal(new Point2(10, 20), ((PointShape)set.Shapes[0]).At);
        Assert.Equal(new Point2(100, 200), ((PointShape)set.Shapes[1]).At);
    }

    /// <summary>
    /// The labels belong to the frame the shapes came from, or they would
    /// be dropped by a viewer checking they match the picture.
    /// </summary>
    [Fact]
    public void Labels_keep_the_frame_of_the_shapes_they_mark()
    {
        var set = Run(node => node.Text = "x", new CircleShape(new Point2(10, 20), 5));

        Assert.Equal(640, set.FrameWidth);
        Assert.Equal(480, set.FrameHeight);
    }

    /// <summary>
    /// The position is only consulted when nothing is wired in; with
    /// shapes present it would be an answer to a question nobody asked.
    /// </summary>
    [Fact]
    public void The_position_is_ignored_when_shapes_are_wired_in()
    {
        var set = Run(
            node =>
            {
                node.Text = "x";
                node.Position = new Point2(999, 999);
            },
            new CircleShape(new Point2(10, 20), 5));

        Assert.Equal(new Point2(10, 20), ((PointShape)Assert.Single(set.Shapes)).At);
    }

    /// <summary>
    /// A producer's own centroid beats the middle of a bounding box, the
    /// same way a measurement between two shapes takes it.
    /// </summary>
    [Fact]
    public void It_writes_at_a_published_centroid()
    {
        var lopsided = new PolygonShape(
        [
            new Point2(0, 0), new Point2(100, 0), new Point2(100, 10), new Point2(0, 10),
        ])
        {
            Metrics = new Dictionary<string, double>
            {
                [ShapeMetric.CentroidX] = 20,
                [ShapeMetric.CentroidY] = 5,
            },
        };

        var set = Run(node => node.Text = "x", lopsided);

        Assert.Equal(new Point2(20, 5), ((PointShape)set.Shapes[0]).At);
    }

    [Fact]
    public void An_empty_set_wired_in_falls_back_to_the_position()
    {
        var set = Run(node =>
        {
            node.Text = "none";
            node.Position = new Point2(5, 6);
        });

        Assert.Equal(new Point2(5, 6), ((PointShape)Assert.Single(set.Shapes)).At);
    }

    // ---- colour -------------------------------------------------------------------

    [Fact]
    public void It_takes_a_colour()
    {
        var set = Run(node =>
        {
            node.Text = "x";
            node.Colour = "#FF0000";
        });

        Assert.Equal(0xFFFF0000, set.Shapes[0].Colour);
    }

    /// <summary>
    /// A colour that does not read warns and falls back rather than
    /// failing the node - losing the whole label over a typo in a hex
    /// string would be a poor trade.
    /// </summary>
    [Fact]
    public void A_bad_colour_warns_and_falls_back()
    {
        var graph = new VisionGraph();
        var label = new LabelNode { Text = "x", Colour = "not a colour" };
        graph.Add(label);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Warning, label.Status);
        Assert.NotNull(((ShapeSet)scheduler.GetOutput(label.ShapesOut)!).Shapes[0].Colour);
    }

    // ---- where it goes ---------------------------------------------------------------

    /// <summary>
    /// One parameter rather than an X and a Y. Two were two things that
    /// could be edited half-way, and they shadowed the node's own canvas
    /// coordinates - a test that meant to place a label had been quietly
    /// dragging the node around the graph instead.
    /// </summary>
    [Fact]
    public void The_position_is_one_value()
    {
        var node = new LabelNode();

        Assert.Equal(new Point2(50, 50), node.Position);
        Assert.Contains(node.Inputs, port => port.Name == nameof(LabelNode.Position));
    }

    [Theory]
    [InlineData("100, 200", 100, 200)]
    [InlineData("100,200", 100, 200)]
    [InlineData("100 200", 100, 200)]
    [InlineData("  -12.5 , 7  ", -12.5, 7)]
    public void A_position_reads_what_people_type(string text, double x, double y)
    {
        Assert.True(Point2.TryParse(text, out var point));

        Assert.Equal(new Point2(x, y), point);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("100")]
    [InlineData("100, 200, 300")]
    [InlineData("100, banana")]
    public void A_position_that_is_not_two_numbers_is_refused(string? text) =>
        Assert.False(Point2.TryParse(text, out _));

    [Fact]
    public void A_position_round_trips_through_its_own_text()
    {
        var original = new Point2(12.25, -3.5);

        Assert.True(Point2.TryParse(original.ToString(), out var again));
        Assert.Equal(original, again);
    }

    /// <summary>
    /// A point is a two-value vector, so a Matrix drives one - which is
    /// what lets a position be computed upstream rather than typed.
    /// </summary>
    [Fact]
    public void A_matrix_can_drive_the_position()
    {
        var set = RunDriven("300, 400");

        Assert.Equal(new Point2(300, 400), ((PointShape)Assert.Single(set.Shapes)).At);
    }

    [Fact]
    public void A_column_vector_drives_it_too()
    {
        var set = RunDriven("300; 400");

        Assert.Equal(new Point2(300, 400), ((PointShape)Assert.Single(set.Shapes)).At);
    }

    /// <summary>
    /// A matrix that is not two numbers cannot be a point. The wire is
    /// allowed - a matrix has no size until it has a value - so the node
    /// says so when it reads it, and falls back rather than failing.
    /// </summary>
    [Theory]
    [InlineData("1, 2, 3")]
    [InlineData("1 2; 3 4")]
    public void A_matrix_of_the_wrong_shape_warns_and_falls_back(string values)
    {
        var graph = new VisionGraph();
        var matrix = new MatrixNode { Values = values };
        var label = new LabelNode { Text = "x", Position = new Point2(7, 8) };

        graph.Add(matrix);
        graph.Add(label);

        var port = label.Inputs.Single(p => p.Name == nameof(LabelNode.Position));
        Assert.True(graph.TryConnect(matrix.MatrixOut, port).Success);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var set = (ShapeSet)scheduler.GetOutput(label.ShapesOut)!;

        Assert.Equal(NodeStatus.Warning, label.Status);
        Assert.Equal(new Point2(7, 8), ((PointShape)set.Shapes[0]).At);
    }

    private static ShapeSet RunDriven(string values)
    {
        var graph = new VisionGraph();
        var matrix = new MatrixNode { Values = values };
        var label = new LabelNode { Text = "x" };

        graph.Add(matrix);
        graph.Add(label);

        var port = label.Inputs.Single(p => p.Name == nameof(LabelNode.Position));
        Assert.True(graph.TryConnect(matrix.MatrixOut, port).Success);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        return (ShapeSet)scheduler.GetOutput(label.ShapesOut)!;
    }

    // ---- how it is written ----------------------------------------------------------

    /// <summary>
    /// The size travels with the label, in image pixels, so the caption
    /// stays the same size on the picture however the viewer is zoomed.
    /// </summary>
    [Fact]
    public void It_publishes_the_size_it_was_given()
    {
        var set = Run(node =>
        {
            node.Text = "x";
            node.Size = 48;
        });

        Assert.Equal(48, set.Shapes[0].LabelSize);
    }

    [Theory]
    [InlineData(LabelTypeface.Monospace, "Consolas")]
    [InlineData(LabelTypeface.Sans, "Segoe UI")]
    [InlineData(LabelTypeface.Serif, "Georgia")]
    public void The_typeface_travels_as_a_family_name(LabelTypeface typeface, string family)
    {
        var set = Run(node =>
        {
            node.Text = "x";
            node.Typeface = typeface;
        });

        Assert.Equal(family, set.Shapes[0].LabelFont);
    }

    /// <summary>
    /// Labels on shapes carry it too, not only the one placed at X, Y -
    /// the two paths through the node had drifted apart before.
    /// </summary>
    [Fact]
    public void Labels_on_shapes_are_written_the_same_way()
    {
        var set = Run(
            node =>
            {
                node.Text = "x";
                node.Size = 36;
                node.Typeface = LabelTypeface.Serif;
            },
            new CircleShape(new Point2(10, 20), 5),
            new CircleShape(new Point2(30, 40), 5));

        Assert.All(set.Shapes, shape =>
        {
            Assert.Equal(36, shape.LabelSize);
            Assert.Equal("Georgia", shape.LabelFont);
        });
    }

    /// <summary>
    /// A label with no height is nothing to look at, and a vast one is a
    /// mistyped figure rather than an intention.
    /// </summary>
    [Theory]
    [InlineData(0, 4)]
    [InlineData(-10, 4)]
    [InlineData(5000, 400)]
    public void The_size_is_held_to_something_drawable(double asked, double expected)
    {
        var node = new LabelNode { Size = asked };

        Assert.Equal(expected, node.Size);
    }

    /// <summary>
    /// The socket is called Shapes because that is what goes in it. It
    /// was called At, which said what it was for without saying what it
    /// took, and the first question anybody asked of the node was what
    /// that input wanted.
    /// </summary>
    [Fact]
    public void The_shapes_socket_says_what_it_takes() =>
        Assert.Equal("Shapes", new LabelNode().ShapesIn.Name);

    // ---- text on a wire -------------------------------------------------------------

    /// <summary>
    /// A Text node drives the label, which is the pair working together:
    /// one place to write a caption, used wherever it is needed.
    /// </summary>
    [Fact]
    public void A_text_node_can_drive_the_label()
    {
        var graph = new VisionGraph();
        var text = new TextNode { Text = "Serial 1234" };
        var label = new LabelNode();

        graph.Add(text);
        graph.Add(label);

        var port = label.Inputs.Single(p => p.Name == nameof(LabelNode.Text));
        Assert.True(graph.TryConnect(text.TextOut, port).Success);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var set = (ShapeSet)scheduler.GetOutput(label.ShapesOut)!;

        Assert.Equal("Serial 1234", set.Shapes[0].Label);
    }

    [Fact]
    public void A_text_node_publishes_its_text()
    {
        var graph = new VisionGraph();
        var node = new TextNode { Text = "hello" };
        graph.Add(node);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal("hello", scheduler.GetOutput(node.TextOut));
    }

    /// <summary>
    /// And a measurement drives it, which is the whole chain: find
    /// something, measure it, write the reading on the picture.
    /// </summary>
    [Fact]
    public void A_measurement_can_drive_the_label()
    {
        var graph = new VisionGraph();
        var a = new ShapeSourceNode(new CircleShape(new Point2(0, 0), 10));
        var b = new ShapeSourceNode(new CircleShape(new Point2(3, 4), 10));
        var measure = new MeasureNode();
        var label = new LabelNode { Decimals = 1 };

        foreach (var node in new VisionNode[] { a, b, measure, label })
            graph.Add(node);

        graph.TryConnect(a.ShapesOut, measure.FirstIn);
        graph.TryConnect(b.ShapesOut, measure.SecondIn);

        var number = label.Inputs.Single(p => p.Name == nameof(LabelNode.Number));
        Assert.True(graph.TryConnect(measure.ValueOut, number).Success);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var set = (ShapeSet)scheduler.GetOutput(label.ShapesOut)!;

        Assert.Equal("5.0", set.Shapes[0].Label);
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
}
