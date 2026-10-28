using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Serialization;
using Sirius.Core.Shapes;
using Sirius.Studio.App.ViewModels;
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

        Assert.Equal(new[] { "#", "X", "Y", "Angle", "Radius", "Deviation", "Contrast" }, table.Columns);
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

    // ---- measured against a circle ------------------------------------------------

    /// <summary>
    /// A band drawn 5 px off the disc's centre: measured from the band, a
    /// round edge swings 10 px in and out once round; measured from a circle
    /// fitted to the points, it is round.
    /// </summary>
    [Fact]
    public void Radius_from_a_fitted_circle_takes_out_where_the_band_was_drawn()
    {
        var band = new RingCalipersNode { Band = new Ring(205, 200, 50, 110), CaliperCount = 72 };
        var fitted = new RingCalipersNode
        {
            Band = new Ring(205, 200, 50, 110), CaliperCount = 72, RadiusFrom = RadiusFrom.Fitted,
        };

        var fromBand = Run(Disc, band, band.PointsOut, band.RoundnessOut);
        var fromFit = Run(Disc, fitted, fitted.PointsOut, fitted.RoundnessOut);

        Assert.InRange(fromBand.Get<double>(band.RoundnessOut), 9, 11);
        Assert.InRange(fromFit.Get<double>(fitted.RoundnessOut), 0, 1.5);

        Assert.All(
            fromFit.Shapes(fitted.PointsOut).Shapes,
            point =>
            {
                Assert.InRange(point.Metric(ShapeMetric.Radius)!.Value, 79, 82);
                Assert.InRange(point.Metric(ShapeMetric.Deviation)!.Value, -1, 1);
            });
    }

    /// <summary>
    /// Find Circle locates the part, Ring Calipers measures it: the band
    /// drawn on the node is nowhere near the disc, and the wired circle
    /// moves it there.
    /// </summary>
    [Fact]
    public void A_wired_circle_puts_the_band_on_its_edge()
    {
        var find = new FindCircleNode { Band = new Ring(200, 200, 50, 110), CaliperCount = 72 };
        var calipers = new RingCalipersNode { Band = new Ring(50, 50, 10, 20), CaliperCount = 72, BandWidth = 30 };

        var graph = new VisionGraph();
        var source = new PictureNode(Disc);
        graph.Add(source);
        graph.Add(find);
        graph.Add(calipers);
        graph.TryConnect(source.Out, find.ImageIn);
        graph.TryConnect(source.Out, calipers.ImageIn);
        graph.TryConnect(find.CircleOut, calipers.CircleIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(72, (int)scheduler.GetOutput(calipers.CountOut)!);
        Assert.All(
            scheduler.GetOutput<ShapeSet>(calipers.PointsOut)!.Shapes,
            point =>
            {
                Assert.InRange(point.Metric(ShapeMetric.Radius)!.Value, 79, 82);
                Assert.InRange(point.Metric(ShapeMetric.Deviation)!.Value, -1, 1);
            });
    }

    [Fact]
    public void Find_circles_points_are_measured_from_the_circle_it_found()
    {
        // The band is off-centre; the fitted circle is not.
        var node = new FindCircleNode { Band = new Ring(206, 197, 50, 110), CaliperCount = 72 };

        var points = Run(Disc, node, node.PointsOut).Shapes(node.PointsOut).Shapes;

        Assert.All(points, point => Assert.InRange(point.Metric(ShapeMetric.Deviation)!.Value, -1, 1));
    }

    /// <summary>
    /// The notch against the circle fitted through every point: 15 deep, and
    /// it reads as most of that, the fit having leant only a little toward it.
    /// </summary>
    [Fact]
    public void A_notch_reads_as_a_dip_in_the_deviation_profile()
    {
        var node = new RingCalipersNode
        {
            Band = new Ring(200, 200, 50, 110), CaliperCount = 72, RadiusFrom = RadiusFrom.Fitted,
        };

        var profile = Run(NotchedDisc, node, node.ProfileOut).Get<ProfileData>(node.ProfileOut);

        Assert.Equal(72, profile.Length);
        Assert.Equal("°", profile.XUnit);
        Assert.Equal(5, profile.Spacing, 6);
        Assert.False(profile.IsLine);

        var samples = profile.Channels[0].Samples;
        Assert.Equal("Deviation", profile.Channels[0].Name);

        // Caliper 0 is at 0 degrees and caliper 1 at 5: inside the notch.
        Assert.InRange(samples[1], -16, -12);
        Assert.InRange(samples[36], -1.5f, 1.5f);
    }

    [Fact]
    public void A_caliper_that_finds_nothing_is_a_gap_at_its_angle()
    {
        var node = new RingCalipersNode { Band = new Ring(200, 200, 50, 110), CaliperCount = 72, StartAngle = 30 };

        var result = Run(FlooredDisc, node, node.ProfileOut, node.MissedOut);
        var profile = result.Get<ProfileData>(node.ProfileOut);

        Assert.Equal(30, profile.XOrigin);
        Assert.True(result.Get<int>(node.MissedOut) > 0);
        Assert.Equal(result.Get<int>(node.MissedOut), profile.Length - profile.ValidCount);

        // 0 degrees is caliper 66 when the ring starts at 30.
        Assert.True(float.IsNaN(profile.Channels[0].Samples[66]));
        Assert.Equal("missed", profile.GapLabel);
    }

    [Fact]
    public void The_profile_can_plot_radius_or_contrast_instead()
    {
        var node = new RingCalipersNode
        {
            Band = new Ring(200, 200, 50, 110), CaliperCount = 36, ProfileValue = RingProfileValue.Radius,
        };

        var profile = Run(Disc, node, node.ProfileOut).Get<ProfileData>(node.ProfileOut);

        Assert.Equal("Radius", profile.Channels[0].Name);
        Assert.All(profile.Channels[0].Samples, sample => Assert.InRange(sample, 79, 82));
    }

    // ---- Line Chart -----------------------------------------------------------------

    /// <summary>The Line Chart's panel charts whatever profile is wired into it.</summary>
    [Fact]
    public void A_line_chart_panel_shows_the_wired_profile()
    {
        var calipers = new RingCalipersNode { Band = new Ring(200, 200, 50, 110), CaliperCount = 36 };
        var chart = new LineChartNode();

        var graph = new VisionGraph();
        var source = new PictureNode(Disc);
        graph.Add(source);
        graph.Add(calipers);
        graph.Add(chart);
        graph.TryConnect(source.Out, calipers.ImageIn);
        graph.TryConnect(calipers.ProfileOut, chart.ProfileIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var panel = new ProfilePanelViewModel(chart);
        panel.Prepare(scheduler);
        panel.Commit();

        Assert.Same(scheduler.GetOutput(calipers.ProfileOut), panel.Data);
        Assert.Equal("Ring Calipers", Assert.Single(panel.Series).Label);
    }

    /// <summary>
    /// Two profiles to compare - the same ring read two ways - each in its
    /// own socket, the second appearing when the first is wired. Two nodes
    /// of one name are numbered so the legend tells them apart.
    /// </summary>
    [Fact]
    public void A_line_chart_grows_a_socket_and_overlays_two_profiles()
    {
        var band = new RingCalipersNode { Band = new Ring(205, 200, 50, 110), CaliperCount = 72 };
        var fitted = new RingCalipersNode
        {
            Band = new Ring(205, 200, 50, 110), CaliperCount = 36, RadiusFrom = RadiusFrom.Fitted,
        };
        var chart = new LineChartNode();

        var graph = new VisionGraph();
        var source = new PictureNode(Disc);
        graph.Add(source);
        graph.Add(band);
        graph.Add(fitted);
        graph.Add(chart);
        graph.TryConnect(source.Out, band.ImageIn);
        graph.TryConnect(source.Out, fitted.ImageIn);

        Assert.Single(chart.ProfileInputs);

        graph.TryConnect(band.ProfileOut, chart.ProfileIn);
        Assert.True(chart.Fit());
        Assert.Equal(["Profile", "Profile 2"], chart.ProfileInputs.Select(port => port.Name));

        graph.TryConnect(fitted.ProfileOut, chart.ProfileInputs[1]);
        chart.Fit();
        Assert.Equal(3, chart.ProfileInputs.Count);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var panel = new ProfilePanelViewModel(chart);
        panel.Prepare(scheduler);
        panel.Commit();

        Assert.Null(panel.Data);
        Assert.Equal(["Ring Calipers 1", "Ring Calipers 2"], panel.Series.Select(series => series.Label));
        Assert.Equal(72, panel.Series[0].Data.Length);
        Assert.Equal(36, panel.Series[1].Data.Length);
    }

    [Fact]
    public void A_line_chart_drops_spare_sockets_when_unwired()
    {
        var calipers = new RingCalipersNode();
        var chart = new LineChartNode();

        var graph = new VisionGraph();
        graph.Add(calipers);
        graph.Add(chart);

        graph.TryConnect(calipers.ProfileOut, chart.ProfileIn);
        chart.Fit();
        Assert.Equal(2, chart.ProfileInputs.Count);

        graph.Disconnect(chart.ProfileIn.Connection!);
        Assert.True(chart.Fit());
        Assert.Single(chart.ProfileInputs);
    }

    /// <summary>
    /// A wire into a grown socket survives saving, and a graph saved while
    /// this was Profile Viewer - its key and its one socket "In" - opens as
    /// a Line Chart with the wire in place.
    /// </summary>
    [Fact]
    public void Line_chart_wiring_survives_saving_and_the_old_name_still_loads()
    {
        var first = new RingCalipersNode();
        var second = new RingCalipersNode();
        var chart = new LineChartNode();

        var graph = new VisionGraph();
        graph.Add(first);
        graph.Add(second);
        graph.Add(chart);
        graph.TryConnect(first.ProfileOut, chart.ProfileIn);
        chart.Fit();
        graph.TryConnect(second.ProfileOut, chart.ProfileInputs[1]);

        var reloaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph)).Graph;
        var loadedChart = reloaded.Nodes.OfType<LineChartNode>().Single();
        Assert.All(loadedChart.ProfileInputs.Take(2), port => Assert.True(port.IsConnected));

        var single = new VisionGraph();
        var one = new RingCalipersNode();
        var oldChart = new LineChartNode();
        single.Add(one);
        single.Add(oldChart);
        single.TryConnect(one.ProfileOut, oldChart.ProfileIn);

        var json = GraphSerializer.Serialize(single)
            .Replace("\"output.lineChart\"", "\"output.profileViewer\"")
            .Replace("\"toPort\": \"Profile\"", "\"toPort\": \"In\"");

        Assert.Contains("output.profileViewer", json);
        Assert.Contains("\"toPort\": \"In\"", json);

        var old = GraphSerializer.Deserialize(json).Graph;
        Assert.True(old.Nodes.OfType<LineChartNode>().Single().ProfileIn.IsConnected);
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

    /// <summary>
    /// The disc with a white bar across its right-hand edge, so the calipers
    /// round angle 0 cross no edge at all.
    /// </summary>
    private static Mat FlooredDisc()
    {
        var mat = Disc();
        CvInvoke.Rectangle(mat, new System.Drawing.Rectangle(245, 188, 80, 24), new MCvScalar(255), -1);
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
