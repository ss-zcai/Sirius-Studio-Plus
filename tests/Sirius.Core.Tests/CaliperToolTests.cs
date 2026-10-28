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
/// Edge points without a fit - Ring Calipers, Line Calipers, Points to
/// Polygon - and the fits they feed.
/// </summary>
/// <remarks>
/// Drawn pictures with known answers: an ellipse whose axes are known, a
/// disc with a notch cut in it at a known angle, a straight edge at a known
/// height.
/// </remarks>
public class CaliperToolTests
{
    public CaliperToolTests() => TestCatalog.Ensure();

    // ---- Ring Calipers ------------------------------------------------------------

    [Fact]
    public void Ring_calipers_give_every_edge_and_a_closed_contour()
    {
        var node = new RingCalipersNode { Band = new Ring(200, 200, 30, 130), CaliperCount = 72 };

        var result = Run(Ellipse, node, node.PointsOut, node.ContourOut, node.CountOut, node.MissedOut);

        Assert.Equal(72, result.Get<int>(node.CountOut));
        Assert.Equal(0, result.Get<int>(node.MissedOut));

        var contour = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes(node.ContourOut).Shapes));
        Assert.True(contour.Closed);
        Assert.Equal(72, contour.Points.Count);

        // pi * 100 * 60 = 18 850 for the ideal ellipse. A drawn one's edge
        // sits half a pixel outside it - pi * 100.5 * 60.5 is 19 102 - and
        // joining 72 points with chords cuts a little back off; it measures
        // 19 053.
        Assert.InRange(contour.Metric(ShapeMetric.Area)!.Value, 18_850, 19_150);
    }

    /// <summary>
    /// Each point says where round the ring it was found and how far out,
    /// which on an ellipse is its long radius across and its short one down.
    /// </summary>
    [Fact]
    public void Each_point_carries_its_angle_radius_and_contrast()
    {
        var node = new RingCalipersNode { Band = new Ring(200, 200, 30, 130), CaliperCount = 72 };

        var points = Run(Ellipse, node, node.PointsOut).Shapes(node.PointsOut).Shapes;

        var across = points.Single(point => point.Metric(ShapeMetric.Angle) == 0);
        var down = points.Single(point => point.Metric(ShapeMetric.Angle) == 90);

        Assert.InRange(across.Metric(ShapeMetric.Radius)!.Value, 99, 101);
        Assert.InRange(down.Metric(ShapeMetric.Radius)!.Value, 59, 61);
        // A black-to-white step reads 74 to 102 here: sharpest where the
        // edge is square to the pixel grid, softer on the diagonals, where
        // the drawn edge is a staircase. Every one is far above the 3 the
        // calipers count as an edge.
        var contrasts = points.Select(point => point.Metric(ShapeMetric.Contrast)!.Value).ToArray();
        Assert.All(contrasts, contrast => Assert.InRange(contrast, 70, 110));
        Assert.Equal(contrasts.Max(), across.Metric(ShapeMetric.Contrast));
    }

    /// <summary>
    /// The table that was empty in the points preview: points now carry
    /// measurements, so it has columns to show.
    /// </summary>
    [Fact]
    public void Edge_points_fill_a_shape_table()
    {
        var node = new RingCalipersNode { Band = new Ring(200, 200, 30, 130) };

        var table = ShapeTable.From(Run(Ellipse, node, node.PointsOut).Shapes(node.PointsOut));

        Assert.Equal(new[] { "#", "Centre X", "Centre Y", "Angle", "Radius", "Contrast" }, table.Columns);
    }

    [Fact]
    public void Find_circles_points_carry_the_same_measurements()
    {
        var node = new FindCircleNode { Band = new Ring(200, 200, 60, 100) };

        var table = ShapeTable.From(Run(Disc, node, node.PointsOut).Shapes(node.PointsOut));

        Assert.Contains("Radius", table.Columns);
        Assert.Contains("Contrast", table.Columns);
    }

    [Fact]
    public void Ring_calipers_into_fit_ellipse_give_the_ellipse()
    {
        var calipers = new RingCalipersNode { Band = new Ring(200, 200, 30, 130), CaliperCount = 72 };
        var fit = new FitEllipseNode();

        var graph = new VisionGraph();
        var source = new PictureNode(Ellipse);
        graph.Add(source);
        graph.Add(calipers);
        graph.Add(fit);
        graph.TryConnect(source.Out, calipers.ImageIn);

        // Points straight in, Group left at its default: a set of lone
        // points is pooled without being told.
        graph.TryConnect(calipers.PointsOut, fit.ShapesIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var ellipse = Assert.IsType<EllipseShape>(Assert.Single(scheduler.GetOutput<ShapeSet>(fit.ShapesOut)!.Shapes));
        Assert.InRange(ellipse.MajorAxis, 198, 202);
        Assert.InRange(ellipse.MinorAxis, 118, 122);
    }

    /// <summary>
    /// The reason for a caliper tool that does not fit: a notch in the edge
    /// is an outlier to Find Circle, which removes it, and the very thing an
    /// inspection is looking for. Ring Calipers keeps it, and its radius
    /// says how deep it is.
    /// </summary>
    [Fact]
    public void A_notch_find_circle_rejects_is_kept_by_ring_calipers()
    {
        var find = new FindCircleNode { Band = new Ring(200, 200, 50, 110), CaliperCount = 72 };
        var calipers = new RingCalipersNode { Band = new Ring(200, 200, 50, 110), CaliperCount = 72 };

        var found = Run(NotchedDisc, find, find.PointsOut).Shapes(find.PointsOut).Shapes;
        var kept = Run(NotchedDisc, calipers, calipers.PointsOut).Shapes(calipers.PointsOut).Shapes;

        Assert.DoesNotContain(found, point => point.Metric(ShapeMetric.Radius) < 75);

        var notch = kept.Where(point => point.Metric(ShapeMetric.Radius) < 75).ToArray();
        Assert.NotEmpty(notch);
        Assert.All(notch, point => Assert.InRange(point.Metric(ShapeMetric.Angle)!.Value, 0, 10));
    }

    [Fact]
    public void An_arc_gives_an_open_contour()
    {
        var node = new RingCalipersNode { Band = new Ring(200, 200, 30, 130), SweepAngle = 90 };

        var contour = Run(Ellipse, node, node.ContourOut).Shapes(node.ContourOut).Shapes.Single();

        Assert.False(((PolygonShape)contour).Closed);
        Assert.Null(contour.Metric(ShapeMetric.Area));
    }

    [Fact]
    public void A_band_that_misses_the_edge_says_so()
    {
        var node = new RingCalipersNode { Band = new Ring(200, 200, 150, 190), CaliperCount = 36 };

        var result = Run(Ellipse, node, node.CountOut, node.MissedOut);

        Assert.Equal(0, result.Get<int>(node.CountOut));
        Assert.Equal(36, result.Get<int>(node.MissedOut));
        Assert.Equal(NodeStatus.Warning, node.Status);
    }

    // ---- Line Calipers ------------------------------------------------------------

    [Fact]
    public void Line_calipers_give_every_edge_along_an_open_contour()
    {
        var node = new LineCalipersNode { Region = new OrientedRect(150, 100, 200, 60, 0), CaliperCount = 21 };

        var result = Run(StraightEdge, node, node.PointsOut, node.ContourOut, node.CountOut);

        Assert.Equal(21, result.Get<int>(node.CountOut));

        var points = result.Shapes(node.PointsOut).Shapes;
        Assert.Equal(0, points[0].Metric(ShapeMetric.Along)!.Value, 6);
        Assert.Equal(200, points[^1].Metric(ShapeMetric.Along)!.Value, 6);
        Assert.All(points, point => Assert.InRange(point.Metric(ShapeMetric.Offset)!.Value, -1, 1));

        var contour = Assert.IsType<PolygonShape>(result.Shapes(node.ContourOut).Shapes.Single());
        Assert.False(contour.Closed);
        Assert.InRange(contour.Metric(ShapeMetric.Perimeter)!.Value, 199, 201);
    }

    // ---- Points to Polygon --------------------------------------------------------

    [Fact]
    public void Points_sorted_round_their_middle_make_the_outline_they_describe()
    {
        // A 10 x 10 square's corners, arriving crossed.
        var corners = Points((0, 0), (10, 10), (10, 0), (0, 10));

        var polygon = Polygon(new PointsToPolygonNode { Order = PointOrder.AroundCentre }, corners);

        Assert.Equal(100, polygon.Metric(ShapeMetric.Area)!.Value, 6);
        Assert.Equal(40, polygon.Metric(ShapeMetric.Perimeter)!.Value, 6);
    }

    [Fact]
    public void Points_as_given_are_joined_as_given()
    {
        // The same corners crossed make a bow-tie, whose two halves cancel.
        var corners = Points((0, 0), (10, 10), (10, 0), (0, 10));

        var polygon = Polygon(new PointsToPolygonNode { Order = PointOrder.AsGiven }, corners);

        Assert.Equal(0, polygon.Metric(ShapeMetric.Area)!.Value, 6);
    }

    [Fact]
    public void An_open_outline_has_a_length_and_no_area()
    {
        var polygon = Polygon(new PointsToPolygonNode { Closed = false }, Points((0, 0), (10, 0), (10, 10)));

        Assert.False(((PolygonShape)polygon).Closed);
        Assert.Equal(20, polygon.Metric(ShapeMetric.Perimeter)!.Value, 6);
        Assert.Null(polygon.Metric(ShapeMetric.Area));
    }

    [Fact]
    public void A_polygon_arriving_gives_its_corners()
    {
        var square = new PolygonShape([new(0, 0), new(10, 0), new(10, 10), new(0, 10)]);

        var polygon = Polygon(new PointsToPolygonNode(), [square]);

        Assert.Equal(100, polygon.Metric(ShapeMetric.Area)!.Value, 6);
    }

    // ---- rejection on the fits ----------------------------------------------------

    [Fact]
    public void Fit_circle_rejects_a_stray_when_asked_and_not_otherwise()
    {
        var points = Ring(36, radius: 50).Append(new PointShape(new Point2(180, 100))).ToArray();

        var kept = Fit(new FitCircleNode(), points);
        var rejected = Fit(new FitCircleNode { RejectSigma = 2.5 }, points);

        Assert.Equal(37, kept.Metric(ShapeMetric.FitPoints));
        Assert.Equal(36, rejected.Metric(ShapeMetric.FitPoints));
        Assert.Equal(50, rejected.Metric(ShapeMetric.Radius)!.Value, 1);
    }

    [Fact]
    public void Fit_ellipse_rejects_a_stray_when_asked()
    {
        var points = Ring(36, radius: 50).Append(new PointShape(new Point2(180, 100))).ToArray();

        var rejected = (EllipseShape)Fit(new FitEllipseNode { RejectSigma = 2.5 }, points);

        Assert.Equal(36, rejected.Metric(ShapeMetric.FitPoints));
        Assert.InRange(rejected.MajorAxis, 99, 101);
    }

    // ---- pictures -------------------------------------------------------------------

    /// <summary>A white ellipse, 200 across and 120 down, centred at (200, 200).</summary>
    private static Mat Ellipse()
    {
        var mat = Black();
        CvInvoke.Ellipse(mat, new System.Drawing.Point(200, 200), new System.Drawing.Size(100, 60), 0, 0, 360, new MCvScalar(255), -1);
        return mat;
    }

    /// <summary>A white disc of radius 80 at (200, 200).</summary>
    private static Mat Disc()
    {
        var mat = Black();
        CvInvoke.Circle(mat, new System.Drawing.Point(200, 200), 80, new MCvScalar(255), -1);
        return mat;
    }

    /// <summary>The disc, with a notch 15 deep cut into its right-hand edge around angle 0 to 10.</summary>
    private static Mat NotchedDisc()
    {
        var mat = Disc();
        CvInvoke.Rectangle(mat, new System.Drawing.Rectangle(265, 197, 30, 12), new MCvScalar(0), -1);
        return mat;
    }

    /// <summary>Black above y = 100, white below, 300 wide.</summary>
    private static Mat StraightEdge()
    {
        var mat = new Mat(200, 300, DepthType.Cv8U, 1);
        mat.SetTo(new MCvScalar(0));
        CvInvoke.Rectangle(mat, new System.Drawing.Rectangle(0, 100, 300, 100), new MCvScalar(255), -1);
        return mat;
    }

    private static Mat Black()
    {
        var mat = new Mat(400, 400, DepthType.Cv8U, 1);
        mat.SetTo(new MCvScalar(0));
        return mat;
    }

    // ---- helpers --------------------------------------------------------------------

    private sealed class Outputs(GraphScheduler scheduler)
    {
        public T Get<T>(OutputPort port) => (T)scheduler.GetOutput(port)!;

        public ShapeSet Shapes(OutputPort port) => scheduler.GetOutput<ShapeSet>(port)!;
    }

    /// <summary>Runs one image node into <paramref name="node"/> and reads the given outputs.</summary>
    private static Outputs Run(Func<Mat> picture, VisionNode node, params OutputPort[] wanted)
    {
        var graph = new VisionGraph();
        var source = new PictureNode(picture);
        graph.Add(source);
        graph.Add(node);
        graph.TryConnect(source.Out, node.Inputs[0]);

        var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        return new Outputs(scheduler);
    }

    private static Shape Polygon(PointsToPolygonNode node, IReadOnlyList<Shape> shapes)
    {
        var graph = new VisionGraph();
        var source = new ShapesNode(shapes);
        graph.Add(source);
        graph.Add(node);
        graph.TryConnect(source.Out, node.PointsIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        return Assert.Single(scheduler.GetOutput<ShapeSet>(node.PolygonOut)!.Shapes);
    }

    private static Shape Fit(ShapeFitNode node, IReadOnlyList<Shape> shapes)
    {
        var graph = new VisionGraph();
        var source = new ShapesNode(shapes);
        graph.Add(source);
        graph.Add(node);
        graph.TryConnect(source.Out, node.ShapesIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        return Assert.Single(scheduler.GetOutput<ShapeSet>(node.ShapesOut)!.Shapes);
    }

    private static Shape[] Points(params (double X, double Y)[] points) =>
        points.Select(point => (Shape)new PointShape(new Point2(point.X, point.Y))).ToArray();

    /// <summary>Points evenly round a circle at (100, 100).</summary>
    private static IEnumerable<Shape> Ring(int count, double radius) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var angle = i * 2 * Math.PI / count;
            return (Shape)new PointShape(new Point2(100 + (radius * Math.Cos(angle)), 100 + (radius * Math.Sin(angle))));
        });

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
        private readonly IReadOnlyList<Shape> _shapes;

        public ShapesNode(IReadOnlyList<Shape> shapes)
        {
            _shapes = shapes;
            Out = AddOutput<ShapeSet>("Out");
        }

        public OutputPort Out { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(Out, new ShapeSet(_shapes, 400, 400));
    }
}
