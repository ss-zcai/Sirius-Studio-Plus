using Sirius.Core.Graph;
using Sirius.Core.Shapes;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>Offset Shapes and Transform Shapes.</summary>
public class ShapeTransformTests
{
    public ShapeTransformTests() => TestCatalog.Ensure();

    // ---- offset: the closed forms --------------------------------------------

    [Fact]
    public void A_circle_grows_by_the_offset_exactly_and_keeps_its_look()
    {
        var circle = new CircleShape(new Point2(100, 80), 20)
        {
            Colour = 0xFF00FF00u,
            Label = "hole",
            Metrics = new Dictionary<string, double>
            {
                [ShapeMetric.Radius] = 20,
                [ShapeMetric.FitError] = 0.3,
                [ShapeMetric.Mean] = 120,
            },
        };

        var grown = Assert.IsType<CircleShape>(Assert.Single(Offset(3, circle).Shapes));

        Assert.Equal(new Point2(100, 80), grown.Centre);
        Assert.Equal(23, grown.Radius, 9);
        Assert.Equal(0xFF00FF00u, grown.Colour);
        Assert.Equal("hole", grown.Label);

        // Measured again, kept, and dropped: the radius is the new one, how
        // well it was fitted still holds, the brightness inside does not.
        Assert.Equal(23, grown.Metric(ShapeMetric.Radius)!.Value, 9);
        Assert.Equal(0.3, grown.Metric(ShapeMetric.FitError));
        Assert.Null(grown.Metric(ShapeMetric.Mean));
    }

    [Fact]
    public void A_circle_shrunk_past_nothing_is_dropped_with_a_warning()
    {
        var node = new OffsetShapesNode { Offset = -12 };

        var result = Run(node, new CircleShape(new Point2(50, 50), 10), new CircleShape(new Point2(90, 50), 30));

        var left = Assert.IsType<CircleShape>(Assert.Single(result.Shapes));
        Assert.Equal(18, left.Radius, 9);
        Assert.Equal(NodeStatus.Warning, node.Status);
        Assert.Contains("shrank to nothing", node.StatusMessage);
    }

    [Fact]
    public void An_ellipse_changes_both_axes_by_twice_the_offset()
    {
        var ellipse = new EllipseShape(new Point2(60, 60), 40, 20, 30);

        var grown = Assert.IsType<EllipseShape>(Assert.Single(Offset(-2, ellipse).Shapes));

        Assert.Equal(36, grown.MajorAxis, 9);
        Assert.Equal(16, grown.MinorAxis, 9);
        Assert.Equal(30, grown.Angle);
    }

    [Fact]
    public void A_turned_rectangle_with_sharp_corners_stays_one()
    {
        var rect = new OrientedRectShape(new Point2(100, 100), 60, 20, 25);

        var grown = Assert.IsType<OrientedRectShape>(Assert.Single(Offset(5, rect).Shapes));

        Assert.Equal((70, 30, 25), (grown.Width, grown.Height, grown.AngleDegrees));
        Assert.Equal(rect.Centre, grown.Centre);
    }

    [Fact]
    public void Round_corners_grow_a_rectangle_by_a_rolling_circle()
    {
        var node = new OffsetShapesNode { Offset = 4, Corners = OffsetCorners.Round };
        var rect = new RectShape(new Bounds(10, 10, 40, 20));

        var outline = Assert.IsType<PolygonShape>(Assert.Single(Run(node, rect).Shapes));

        // A rectangle swept by a disc: the box, a strip down every side, and
        // a quarter circle at every corner - less a sliver, the corners being
        // chords within a twentieth of a pixel of the arc.
        var expected = (40 * 20) + (2 * 4 * (40 + 20)) + (Math.PI * 4 * 4);
        Assert.InRange(outline.Metric(ShapeMetric.Area)!.Value, expected - 1.5, expected);

        Assert.All(outline.Points, point => Assert.InRange(DistanceToBox(point, rect.Rect), 3.9, 4.1));
    }

    // ---- offset: outlines ------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_square_outline_grows_and_shrinks_either_way_round(bool reversed)
    {
        Point2[] square = [new(0, 0), new(40, 0), new(40, 40), new(0, 40)];
        var outline = new PolygonShape(reversed ? square.Reverse().ToArray() : square);

        var grown = Assert.IsType<PolygonShape>(Assert.Single(Offset(3, outline).Shapes));
        var shrunk = Assert.IsType<PolygonShape>(Assert.Single(Offset(-3, outline).Shapes));

        Assert.Equal(46 * 46, grown.Metric(ShapeMetric.Area)!.Value, 0.01);
        Assert.Equal(34 * 34, shrunk.Metric(ShapeMetric.Area)!.Value, 0.01);
        Assert.Equal(20, shrunk.Metric(ShapeMetric.CentroidX)!.Value, 0.01);
    }

    [Fact]
    public void A_dumbbell_shrinks_into_its_two_ends()
    {
        // Two 30-pixel squares joined by a bar 4 pixels thick.
        var dumbbell = new PolygonShape(
        [
            new(0, 0), new(30, 0), new(30, 13), new(60, 13), new(60, 0), new(90, 0),
            new(90, 30), new(60, 30), new(60, 17), new(30, 17), new(30, 30), new(0, 30),
        ]);

        var pieces = Offset(-3, dumbbell).Shapes;

        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, piece => Assert.Equal(24 * 24, piece.Metric(ShapeMetric.Area)!.Value, 0.5));
    }

    [Fact]
    public void An_open_path_is_passed_on_with_a_warning()
    {
        var node = new OffsetShapesNode { Offset = 3 };
        var path = new PolygonShape([new(0, 0), new(10, 5), new(20, 0)], Closed: false);

        Assert.Same(path, Assert.Single(Run(node, path).Shapes));
        Assert.Contains("open path", node.StatusMessage);
    }

    // ---- offset: lines and points --------------------------------------------

    [Fact]
    public void A_line_shifts_to_its_right_on_the_image()
    {
        var line = new LineShape(new Point2(10, 50), new Point2(90, 50));

        var moved = Assert.IsType<LineShape>(Assert.Single(Offset(5, line).Shapes));

        // Going right along the image, its right is down: y grows downward.
        Assert.Equal(new Point2(10, 55), moved.Start);
        Assert.Equal(new Point2(90, 55), moved.End);
    }

    [Fact]
    public void A_line_extends_at_both_ends()
    {
        var node = new OffsetShapesNode { Offset = 5, Lines = OffsetLines.Extend };

        var longer = Assert.IsType<LineShape>(Assert.Single(Run(node, new LineShape(new Point2(10, 50), new Point2(90, 50))).Shapes));

        Assert.Equal(new Point2(5, 50), longer.Start);
        Assert.Equal(new Point2(95, 50), longer.End);
    }

    [Fact]
    public void A_point_grows_into_a_circle_round_it()
    {
        var grown = Assert.IsType<CircleShape>(Assert.Single(Offset(6, new PointShape(new Point2(30, 40))).Shapes));

        Assert.Equal(new Point2(30, 40), grown.Centre);
        Assert.Equal(6, grown.Radius);
        Assert.Equal(6, grown.Metric(ShapeMetric.Radius));
    }

    // ---- transform ---------------------------------------------------------------

    [Fact]
    public void Moving_shifts_every_shape()
    {
        var node = new TransformShapesNode { MoveX = 15, MoveY = -5 };

        var moved = Assert.IsType<CircleShape>(Assert.Single(Run(node, new CircleShape(new Point2(100, 100), 10)).Shapes));

        Assert.Equal(100 + 15, moved.Centre.X, 9);
        Assert.Equal(100 - 5, moved.Centre.Y, 9);
        Assert.Equal(10, moved.Radius, 9);
    }

    [Fact]
    public void Turning_about_a_point_is_clockwise_on_the_image()
    {
        var node = new TransformShapesNode { Rotate = 90, About = TransformPivot.Point, Pivot = new Point2(50, 50) };

        var turned = Assert.IsType<PointShape>(Assert.Single(Run(node, new PointShape(new Point2(60, 50))).Shapes));

        // From the right of the pivot to below it: clockwise, as y runs down.
        Assert.Equal(50, turned.At.X, 9);
        Assert.Equal(60, turned.At.Y, 9);
    }

    [Fact]
    public void Turning_each_about_its_centre_leaves_it_in_place()
    {
        var node = new TransformShapesNode { Rotate = 30 };
        var rect = new OrientedRectShape(new Point2(100, 80), 40, 10, 15);

        var turned = Assert.IsType<OrientedRectShape>(Assert.Single(Run(node, rect).Shapes));

        Assert.Equal(100, turned.Centre.X, 9);
        Assert.Equal(80, turned.Centre.Y, 9);
        Assert.Equal(45, turned.AngleDegrees, 9);
    }

    [Fact]
    public void Scaling_is_proportional_and_converts_measurements()
    {
        var node = new TransformShapesNode { Scale = 2 };
        var circle = new CircleShape(new Point2(40, 40), 10)
        {
            Metrics = new Dictionary<string, double> { [ShapeMetric.Radius] = 10, [ShapeMetric.Area] = Math.PI * 100 },
        };

        var scaled = Assert.IsType<CircleShape>(Assert.Single(Run(node, circle).Shapes));

        Assert.Equal(new Point2(40, 40), scaled.Centre);
        Assert.Equal(20, scaled.Radius, 9);
        Assert.Equal(20, scaled.Metric(ShapeMetric.Radius)!.Value, 9);
        Assert.Equal(Math.PI * 400, scaled.Metric(ShapeMetric.Area)!.Value, 6);
    }

    [Fact]
    public void A_wired_shape_gives_the_pivot()
    {
        var graph = new VisionGraph();
        var shapes = new EdgeTestKit.ShapesNode(new PointShape(new Point2(70, 20)));
        var centre = new EdgeTestKit.ShapesNode(new CircleShape(new Point2(50, 20), 5));
        var node = new TransformShapesNode { Rotate = 180, About = TransformPivot.Point };

        graph.Add(shapes);
        graph.Add(centre);
        graph.Add(node);
        graph.TryConnect(shapes.Out, node.ShapesIn);
        Assert.True(graph.TryConnect(centre.Out, node.FindInput(nameof(TransformShapesNode.Pivot))!).Success);

        using var scheduler = EdgeTestKit.Run(graph);
        var turned = Assert.IsType<PointShape>(Assert.Single(scheduler.Shapes(node.ShapesOut).Shapes));

        Assert.Equal(30, turned.At.X, 9);
        Assert.Equal(20, turned.At.Y, 9);
    }

    // ---- plumbing ------------------------------------------------------------------

    private static ShapeSet Offset(double offset, params Shape[] shapes) =>
        Run(new OffsetShapesNode { Offset = offset }, shapes);

    private static ShapeSet Run(VisionNode node, params Shape[] shapes)
    {
        var graph = new VisionGraph();
        var source = new EdgeTestKit.ShapesNode(shapes);

        graph.Add(source);
        graph.Add(node);
        graph.TryConnect(source.Out, node.Inputs[0]);

        using var scheduler = EdgeTestKit.Run(graph);

        Assert.NotEqual(NodeStatus.Error, node.Status);
        return scheduler.Shapes(node.Outputs[0]);
    }

    private static double DistanceToBox(Point2 point, Bounds box)
    {
        var dx = Math.Max(Math.Max(box.X - point.X, 0), point.X - box.Right);
        var dy = Math.Max(Math.Max(box.Y - point.Y, 0), point.Y - box.Bottom);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
