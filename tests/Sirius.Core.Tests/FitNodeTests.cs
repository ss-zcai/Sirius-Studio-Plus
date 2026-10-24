using Sirius.Core.Graph;
using Sirius.Core.Shapes;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Fitting ideal figures to located points.
/// </summary>
/// <remarks>
/// Every case feeds points taken from a figure whose answer is known, so a
/// fit that is confidently wrong has something to be wrong against. The
/// reported error matters as much as the geometry: a circle fitted to a
/// clean ring and one fitted to a scattered arc look identical until it is
/// read.
/// </remarks>
public class FitNodeTests
{
    public FitNodeTests() => TestCatalog.Ensure();

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

    /// <summary>Emits whatever shapes a case needs.</summary>
    private sealed class ShapesNode : VisionNode
    {
        private readonly Shape[] _shapes;

        public ShapesNode(params Shape[] shapes)
        {
            _shapes = shapes;
            ShapesOut = AddOutput<ShapeSet>("Shapes");
        }

        public OutputPort ShapesOut { get; }

        protected override void OnEvaluate(EvalContext context) =>
            context.Set(ShapesOut, new ShapeSet(_shapes, 500, 500));
    }

    private static ShapeSet Run(ShapeFitNode fit, params Shape[] input)
    {
        var graph = new VisionGraph();
        var source = new ShapesNode(input);

        graph.Add(source);
        graph.Add(fit);
        graph.TryConnect(source.ShapesOut, fit.ShapesIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.NotEqual(NodeStatus.Error, fit.Status);

        return scheduler.GetOutput<ShapeSet>(fit.ShapesOut)!;
    }

    /// <summary>Points on a circle, as a blob's contour would be.</summary>
    private static PolygonShape Ring(double cx, double cy, double radius, int count = 60)
    {
        var points = Enumerable.Range(0, count)
            .Select(i =>
            {
                var angle = 2 * Math.PI * i / count;
                return new Point2(cx + (radius * Math.Cos(angle)), cy + (radius * Math.Sin(angle)));
            })
            .ToArray();

        return new PolygonShape(points);
    }

    /// <summary>Points on an ellipse, turned.</summary>
    private static PolygonShape Oval(
        double cx, double cy, double a, double b, double degrees, int count = 60)
    {
        var radians = degrees * Math.PI / 180.0;

        var points = Enumerable.Range(0, count)
            .Select(i =>
            {
                var angle = 2 * Math.PI * i / count;
                var x = a * Math.Cos(angle);
                var y = b * Math.Sin(angle);

                return new Point2(
                    cx + (x * Math.Cos(radians)) - (y * Math.Sin(radians)),
                    cy + (x * Math.Sin(radians)) + (y * Math.Cos(radians)));
            })
            .ToArray();

        return new PolygonShape(points);
    }

    // ---- circle -------------------------------------------------------------

    [Fact]
    public void A_circle_is_recovered_from_its_own_outline()
    {
        var fitted = Run(new FitCircleNode(), Ring(200, 150, 40));
        var circle = Assert.IsType<CircleShape>(Assert.Single(fitted.Shapes));

        Assert.Equal(200, circle.Centre.X, 2);
        Assert.Equal(150, circle.Centre.Y, 2);
        Assert.Equal(40, circle.Radius, 2);
    }

    /// <summary>
    /// A fit through exact points has essentially no error, which is what
    /// makes the number worth reporting at all.
    /// </summary>
    [Fact]
    public void A_clean_circle_reports_almost_no_error()
    {
        var fitted = Run(new FitCircleNode(), Ring(200, 150, 40));
        var error = Assert.Single(fitted.Shapes).Metric(ShapeMetric.FitError);

        Assert.NotNull(error);
        Assert.True(error < 0.01, $"a perfect ring reported an error of {error}");
    }

    [Fact]
    public void A_scattered_circle_reports_a_larger_error()
    {
        var clean = Ring(200, 150, 40);
        var noise = new Random(7);

        var rough = new PolygonShape(clean.Points
            .Select(p => new Point2(p.X + (noise.NextDouble() * 6) - 3, p.Y))
            .ToArray());

        var cleanError = Assert.Single(Run(new FitCircleNode(), clean).Shapes)
            .Metric(ShapeMetric.FitError)!.Value;

        var roughError = Assert.Single(Run(new FitCircleNode(), rough).Shapes)
            .Metric(ShapeMetric.FitError)!.Value;

        Assert.True(
            roughError > cleanError + 0.5,
            $"scatter barely moved the error: {cleanError} to {roughError}");
    }

    /// <summary>
    /// A least-squares fit, not the smallest enclosing circle. One point
    /// pushed outward should move it a little, not redefine it.
    /// </summary>
    [Fact]
    public void One_stray_point_does_not_redefine_the_circle()
    {
        var ring = Ring(200, 150, 40);
        var strayed = ring.Points.ToList();
        strayed[0] = new Point2(400, 150);

        var fitted = Run(new FitCircleNode(), new PolygonShape(strayed));
        var circle = Assert.IsType<CircleShape>(Assert.Single(fitted.Shapes));

        Assert.InRange(circle.Radius, 40, 70);
    }

    // ---- ellipse ------------------------------------------------------------

    [Fact]
    public void An_ellipse_is_recovered_from_its_own_outline()
    {
        var fitted = Run(new FitEllipseNode(), Oval(250, 200, 60, 30, 0));
        var ellipse = Assert.IsType<EllipseShape>(Assert.Single(fitted.Shapes));

        Assert.Equal(250, ellipse.Centre.X, 1);
        Assert.Equal(200, ellipse.Centre.Y, 1);
        Assert.Equal(120, ellipse.MajorAxis, 1);
        Assert.Equal(60, ellipse.MinorAxis, 1);
    }

    /// <summary>
    /// The angle reported is of the longer axis, so it means the same thing
    /// as the angle on a blob's oriented rectangle.
    /// </summary>
    [Fact]
    public void The_angle_describes_the_long_axis()
    {
        var fitted = Run(new FitEllipseNode(), Oval(250, 200, 60, 30, 30));
        var ellipse = Assert.IsType<EllipseShape>(Assert.Single(fitted.Shapes));

        Assert.True(ellipse.MajorAxis > ellipse.MinorAxis);
        Assert.Equal(30, ellipse.Angle % 180, 0);
    }

    [Fact]
    public void A_circle_fitted_as_an_ellipse_has_equal_axes()
    {
        var fitted = Run(new FitEllipseNode(), Ring(200, 150, 40));
        var ellipse = Assert.IsType<EllipseShape>(Assert.Single(fitted.Shapes));

        Assert.Equal(ellipse.MajorAxis, ellipse.MinorAxis, 1);
        Assert.Equal(1, ellipse.Metric(ShapeMetric.AspectRatio)!.Value, 2);
    }

    /// <summary>
    /// The bounds of a turned ellipse are its true extent, not the box of
    /// the un-turned one, which would be too small on the diagonal.
    /// </summary>
    [Fact]
    public void A_turned_ellipse_reports_its_true_extent()
    {
        var upright = new EllipseShape(new Point2(0, 0), 100, 20, 0).Bounds;
        var turned = new EllipseShape(new Point2(0, 0), 100, 20, 90).Bounds;

        Assert.Equal(100, upright.Width, 3);
        Assert.Equal(20, turned.Width, 3);
        Assert.Equal(100, turned.Height, 3);
    }

    // ---- line ---------------------------------------------------------------

    [Fact]
    public void A_line_is_recovered_from_points_along_it()
    {
        var points = Enumerable.Range(0, 20)
            .Select(i => new Point2(10 + (i * 5), 100 + (i * 5)))
            .ToArray();

        var fitted = Run(new FitLineNode(), new PolygonShape(points, Closed: false));
        var line = Assert.IsType<LineShape>(Assert.Single(fitted.Shapes));

        Assert.Equal(45, Math.Abs(line.Metric(ShapeMetric.Angle)!.Value), 1);
        Assert.True(line.Metric(ShapeMetric.FitError) < 0.01);
    }

    /// <summary>
    /// The segment covers the points it was fitted to, rather than running
    /// off to the edge of the frame.
    /// </summary>
    [Fact]
    public void The_segment_spans_the_points_it_was_fitted_to()
    {
        var points = Enumerable.Range(0, 11)
            .Select(i => new Point2(100 + (i * 10), 50))
            .ToArray();

        var fitted = Run(new FitLineNode(), new PolygonShape(points, Closed: false));
        var line = Assert.IsType<LineShape>(Assert.Single(fitted.Shapes));

        Assert.Equal(100, Math.Min(line.Start.X, line.End.X), 1);
        Assert.Equal(200, Math.Max(line.Start.X, line.End.X), 1);
    }

    /// <summary>
    /// The reason Centres exists: a line through a row of separate
    /// features, which fitting each one on its own cannot express.
    /// </summary>
    [Fact]
    public void A_line_can_be_fitted_through_the_centres_of_several_shapes()
    {
        var pads = Enumerable.Range(0, 5)
            .Select(i => (Shape)Ring(100 + (i * 40), 200, 8))
            .ToArray();

        var node = new FitLineNode
        {
            From = FitSource.Centres,
            Grouping = FitGrouping.AllTogether,
            MinimumPoints = 2,
        };

        var line = Assert.IsType<LineShape>(Assert.Single(Run(node, pads).Shapes));

        Assert.Equal(0, Math.Abs(line.Metric(ShapeMetric.Angle)!.Value) % 180, 1);
        Assert.Equal(100, Math.Min(line.Start.X, line.End.X), 0);
        Assert.Equal(260, Math.Max(line.Start.X, line.End.X), 0);
    }

    // ---- grouping and refusals ---------------------------------------------

    [Fact]
    public void Each_shape_is_fitted_on_its_own_by_default()
    {
        var fitted = Run(new FitCircleNode(), Ring(100, 100, 20), Ring(300, 300, 50));

        Assert.Equal(2, fitted.Count);
    }

    [Fact]
    public void All_together_gives_one_fit()
    {
        var node = new FitCircleNode { Grouping = FitGrouping.AllTogether };
        var fitted = Run(node, Ring(100, 100, 20), Ring(300, 300, 50));

        Assert.Single(fitted.Shapes);
    }

    /// <summary>
    /// Too few points is reported rather than fitted through. Three points
    /// always lie on exactly one circle, so a fit through them would look
    /// perfect and mean nothing.
    /// </summary>
    [Fact]
    public void A_shape_with_too_few_points_is_skipped_and_said_so()
    {
        var node = new FitEllipseNode();

        var fitted = Run(
            node,
            new PolygonShape([new Point2(0, 0), new Point2(10, 0), new Point2(5, 5)]));

        Assert.Empty(fitted.Shapes);
        Assert.Equal(NodeStatus.Warning, node.Status);
        Assert.Contains("too few points", node.StatusMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_fitted_set_keeps_the_frame_it_was_measured_on()
    {
        var fitted = Run(new FitCircleNode(), Ring(200, 150, 40));

        Assert.Equal(500, fitted.FrameWidth);
        Assert.Equal(500, fitted.FrameHeight);
    }
}
