using Sirius.Core.Graph;
using Sirius.Core.Shapes;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Measuring between two found shapes.
/// </summary>
/// <remarks>
/// The thing that turns found geometry into a number a recipe can pass or
/// fail on. The viewer has measured interactively from the start, but
/// those readings live on the picture and nothing downstream can see them.
/// </remarks>
public class MeasureNodeTests
{
    public MeasureNodeTests() => TestCatalog.Ensure();

    /// <summary>Publishes a fixed shape set, standing in for a find tool.</summary>
    private sealed class ShapeSourceNode : VisionNode
    {
        public ShapeSourceNode(params Shape[] shapes)
        {
            Set = new ShapeSet(shapes, 640, 480);
            ShapesOut = AddOutput<ShapeSet>("Out");
        }

        public ShapeSet Set { get; init; }

        public OutputPort ShapesOut { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(ShapesOut, Set);
    }

    private static (double Value, ShapeSet Annotation, NodeStatus Status, string? Message) Run(
        Shape[] a, Shape[] b, Action<MeasureNode>? configure = null)
    {
        var graph = new VisionGraph();
        var first = new ShapeSourceNode(a);
        var second = new ShapeSourceNode(b);
        var measure = new MeasureNode();
        configure?.Invoke(measure);

        graph.Add(first);
        graph.Add(second);
        graph.Add(measure);
        graph.TryConnect(first.ShapesOut, measure.FirstIn);
        graph.TryConnect(second.ShapesOut, measure.SecondIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        return (
            (double)(scheduler.GetOutput(measure.ValueOut) ?? 0d),
            (ShapeSet)scheduler.GetOutput(measure.AnnotationOut)!,
            measure.Status,
            measure.StatusMessage);
    }

    // ---- the case it was asked for -------------------------------------------

    /// <summary>
    /// Two circles found by two Find Circle tools, and the distance
    /// between them.
    /// </summary>
    [Fact]
    public void It_measures_centre_to_centre_between_two_circles()
    {
        var (value, _, status, _) = Run(
            [new CircleShape(new Point2(100, 100), 30)],
            [new CircleShape(new Point2(400, 500), 50)]);

        // 3-4-5: 300 and 400 apart is 500.
        Assert.Equal(500, value, 6);
        Assert.Equal(NodeStatus.Ready, status);
    }

    /// <summary>
    /// The gap between them, which is the centre distance less both radii
    /// - and the measurement somebody usually means by "how far apart".
    /// </summary>
    [Fact]
    public void It_measures_the_gap_between_two_circles()
    {
        var (value, _, _, _) = Run(
            [new CircleShape(new Point2(0, 0), 30)],
            [new CircleShape(new Point2(500, 0), 50)],
            node => node.Kind = MeasureKind.EdgeToEdge);

        Assert.Equal(420, value, 6);
    }

    /// <summary>
    /// Overlapping circles read as nearly nothing rather than as a
    /// negative, because the outlines genuinely cross.
    /// </summary>
    [Fact]
    public void Overlapping_circles_have_no_gap()
    {
        var (value, _, _, _) = Run(
            [new CircleShape(new Point2(0, 0), 100)],
            [new CircleShape(new Point2(50, 0), 100)],
            node => node.Kind = MeasureKind.EdgeToEdge);

        Assert.True(value < 1, $"overlapping circles reported a gap of {value:0.###}");
    }

    // ---- the annotation --------------------------------------------------------

    /// <summary>
    /// The reading is drawn the way a hand-drawn measurement is: a line
    /// across what was measured, labelled with the number.
    /// </summary>
    [Fact]
    public void The_reading_is_drawn_and_labelled()
    {
        var (_, annotation, _, _) = Run(
            [new CircleShape(new Point2(100, 100), 30)],
            [new CircleShape(new Point2(400, 500), 50)]);

        var line = Assert.IsType<LineShape>(Assert.Single(annotation.Shapes));

        Assert.Equal("500.00 px", line.Label);
        Assert.Equal(new Point2(100, 100), line.Start);
        Assert.Equal(new Point2(400, 500), line.End);
        Assert.Equal(640, annotation.FrameWidth);
    }

    [Fact]
    public void The_label_follows_the_settings()
    {
        var (_, annotation, _, _) = Run(
            [new CircleShape(new Point2(0, 0), 10)],
            [new CircleShape(new Point2(3, 4), 10)],
            node =>
            {
                node.Decimals = 1;
                node.Units = "mm";
            });

        Assert.Equal("5.0 mm", ((LineShape)annotation.Shapes[0]).Label);
    }

    [Fact]
    public void The_label_can_be_turned_off()
    {
        var (_, annotation, _, _) = Run(
            [new CircleShape(new Point2(0, 0), 10)],
            [new CircleShape(new Point2(3, 4), 10)],
            node => node.ShowLabel = false);

        Assert.Null(((LineShape)annotation.Shapes[0]).Label);
    }

    /// <summary>
    /// The gap is drawn between the points on the outlines it was read
    /// across, not between the centres - otherwise the picture would
    /// disagree with the number.
    /// </summary>
    [Fact]
    public void The_gap_is_drawn_across_the_gap()
    {
        var (_, annotation, _, _) = Run(
            [new CircleShape(new Point2(0, 0), 30)],
            [new CircleShape(new Point2(500, 0), 50)],
            node => node.Kind = MeasureKind.EdgeToEdge);

        var line = (LineShape)annotation.Shapes[0];

        Assert.Equal(new Point2(30, 0), line.Start);
        Assert.Equal(new Point2(450, 0), line.End);
    }

    // ---- lines -----------------------------------------------------------------

    [Theory]
    [InlineData(0, 30, 30)]
    [InlineData(0, 90, 90)]
    [InlineData(10, 350, 20)]
    [InlineData(0, 170, 10)]
    [InlineData(45, 225, 0)]
    public void It_measures_the_angle_between_two_lines(
        double firstDegrees, double secondDegrees, double expected)
    {
        var (value, _, _, _) = Run(
            [Line(firstDegrees)],
            [Line(secondDegrees)],
            node => node.Kind = MeasureKind.Angle);

        Assert.Equal(expected, value, 6);
    }

    private static LineShape Line(double degrees)
    {
        var radians = degrees * Math.PI / 180.0;

        return new LineShape(
            new Point2(0, 0),
            new Point2(100 * Math.Cos(radians), 100 * Math.Sin(radians)));
    }

    /// <summary>
    /// A circle has no direction, so an angle to one is refused rather
    /// than answered with something arbitrary.
    /// </summary>
    [Fact]
    public void An_angle_to_a_circle_is_refused()
    {
        var (value, annotation, status, message) = Run(
            [Line(0)],
            [new CircleShape(new Point2(10, 10), 5)],
            node => node.Kind = MeasureKind.Angle);

        Assert.Equal(0, value);
        Assert.Empty(annotation.Shapes);
        Assert.Equal(NodeStatus.Warning, status);
        Assert.Contains("no direction", message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// How far a feature sits off a datum line, measured square to it.
    /// </summary>
    [Fact]
    public void It_measures_perpendicular_to_a_line()
    {
        var (value, _, _, _) = Run(
            [new CircleShape(new Point2(50, 40), 5)],
            [new LineShape(new Point2(0, 0), new Point2(100, 0))],
            node => node.Kind = MeasureKind.PerpendicularToB);

        Assert.Equal(40, value, 6);
    }

    // ---- what it does with awkward input ----------------------------------------

    /// <summary>
    /// Both inputs are required, so an unwired node is an error - the
    /// same as any other node missing what it needs, rather than a
    /// special case that quietly reads zero.
    /// </summary>
    [Fact]
    public void Nothing_wired_in_is_an_error()
    {
        var graph = new VisionGraph();
        var measure = new MeasureNode();
        graph.Add(measure);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Error, measure.Status);
    }

    /// <summary>
    /// Wired but empty is different: the tool upstream ran and found
    /// nothing, which is a normal state for a recipe to pass through and
    /// not a wiring mistake.
    /// </summary>
    [Fact]
    public void Wired_but_empty_is_a_warning()
    {
        var (value, annotation, status, message) = Run(
            [],
            [new CircleShape(new Point2(0, 0), 10)]);

        Assert.Equal(0, value);
        Assert.Empty(annotation.Shapes);
        Assert.Equal(NodeStatus.Warning, status);
        Assert.Contains("no shape", message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Several shapes on one input measures from the first and says so,
    /// rather than guessing which pair was meant.
    /// </summary>
    [Fact]
    public void Several_shapes_measures_from_the_first_and_says_so()
    {
        var (value, _, status, message) = Run(
            [
                new CircleShape(new Point2(0, 0), 10),
                new CircleShape(new Point2(900, 900), 10),
            ],
            [new CircleShape(new Point2(3, 4), 10)]);

        Assert.Equal(5, value, 6);
        Assert.Equal(NodeStatus.Warning, status);
        Assert.Contains("Select Shape", message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A centroid published by the producer beats the middle of a
    /// bounding box: a blob's centroid is a real measurement of where its
    /// mass is, and the box centre is not.
    /// </summary>
    [Fact]
    public void A_published_centroid_is_preferred()
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

        var (value, _, _, _) = Run(
            [lopsided],
            [new PointShape(new Point2(20, 55))]);

        Assert.Equal(50, value, 6);
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
