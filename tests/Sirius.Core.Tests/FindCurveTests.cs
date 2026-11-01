using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Serialization;
using Sirius.Core.Shapes;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;
using static Sirius.Core.Tests.EdgeTestKit;

namespace Sirius.Core.Tests;

/// <summary>
/// Finding an edge of any shape along a drawn path, and the path itself as
/// a parameter: drawn, typed, saved and wired.
/// </summary>
public class FindCurveTests
{
    public FindCurveTests() => TestCatalog.Ensure();

    private const int Width = 600;
    private const int Height = 400;

    /// <summary>A wave: the part below y = 200 + 30 sin, one period every 200 px.</summary>
    private static double Wave(double x) => 200 + (30 * Math.Sin(2 * Math.PI * (x - 100) / 200));

    private static double WaveSlope(double x) => 30 * 2 * Math.PI / 200 * Math.Cos(2 * Math.PI * (x - 100) / 200);

    private static ImageData Waved() => Render(Width, Height, (x, y) => y - Wave(x));

    [Fact]
    public void A_wavy_edge_is_followed_to_a_fraction_of_a_pixel()
    {
        // Drawn straight along the middle, the band wide enough for the wave.
        var node = new FindCurveNode
        {
            Path = new PathBand([new Point2(100, 200), new Point2(300, 200), new Point2(500, 200)], closed: false, width: 100),
            CaliperCount = 81,
        };

        var (graph, _) = Wire(Waved, node);
        var result = Run(graph);

        var curve = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes(node.CurveOut).Shapes));
        Assert.False(curve.Closed);
        Assert.Equal(81, result.Get<int>(node.CountOut));
        Assert.Equal(0, result.Get<int>(node.MissedOut));

        // Square to the wave, which is what a distance from an edge means.
        foreach (var point in curve.Points.Where(point => point.X is > 105 and < 495))
        {
            var off = Math.Abs(point.Y - Wave(point.X)) / Math.Sqrt(1 + Math.Pow(WaveSlope(point.X), 2));
            Assert.True(off < 0.6, $"the curve is {off:0.###} px off the edge at x = {point.X:0.#}");
        }

        Assert.True(curve.Metric(ShapeMetric.FitError) < 0.5);
    }

    /// <summary>
    /// A spike 15 high and 7 wide that one caliper lands on: set aside, so
    /// the curve runs on along the edge rather than up the spike.
    /// </summary>
    [Fact]
    public void A_point_that_caught_a_spike_is_set_aside()
    {
        var picture = () => Render(Width, Height, (x, y) => y - (x is >= 296.5 and <= 303.5 ? 185 : 200));

        var node = new FindCurveNode
        {
            Path = new PathBand([new Point2(100, 200), new Point2(500, 200)], closed: false, width: 60),
            CaliperCount = 81,
        };

        var (graph, _) = Wire(picture, node);
        var result = Run(graph);

        Assert.Equal(80, result.Get<int>(node.CountOut));

        var curve = (PolygonShape)result.Shapes(node.CurveOut).Shapes[0];
        var middle = curve.Points.MinBy(point => Math.Abs(point.X - 300));
        Assert.InRange(middle.Y, 199.5, 200.5);
    }

    /// <summary>
    /// A circle wired into Path is followed all the way round: a closed
    /// curve round a disc of radius 120, its length the circumference.
    /// </summary>
    [Fact]
    public void A_wired_circle_becomes_a_closed_path()
    {
        var disc = () => Render(Width, Height, (x, y) => 120 - Math.Sqrt(((x - 300) * (x - 300)) + ((y - 200) * (y - 200))));

        var node = new FindCurveNode { CaliperCount = 120, Path = PathBand.Default.WithWidth(40) };
        var (graph, _) = Wire(disc, node);

        var circle = new ShapesNode(new CircleShape(new Point2(296, 203), 112));
        graph.Add(circle);
        Assert.True(graph.TryConnect(circle.Out, node.FindInput(nameof(FindCurveNode.Path))!).Success);

        var result = Run(graph);

        var curve = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes(node.CurveOut).Shapes));
        Assert.True(curve.Closed);
        Assert.InRange(curve.Metric(ShapeMetric.Length)!.Value, 2 * Math.PI * 120 - 4, 2 * Math.PI * 120 + 1);

        foreach (var point in curve.Points)
            Assert.InRange(point.DistanceTo(new Point2(300, 200)), 119.4, 120.6);
    }

    [Fact]
    public void Calipers_are_spaced_evenly_along_a_path_and_cross_it_square()
    {
        var open = new PathBand([new Point2(0, 0), new Point2(100, 0), new Point2(100, 100)], closed: false, width: 10);
        var stations = CaliperScan.Stations(open, 5);

        Assert.Equal(new[] { 0.0, 50, 100, 150, 200 }, stations.Select(station => station.Along));
        Assert.Equal(new Point2(50, 0), stations[1].At);

        // Square across the first side - its direction turned a quarter
        // clockwise on the image, which is down.
        Assert.Equal(0, stations[1].NormalX, 9);
        Assert.Equal(1, stations[1].NormalY, 9);

        // At the corner, across the bisector.
        Assert.Equal(new Point2(100, 0), stations[2].At);
        Assert.Equal(-Math.Sqrt(0.5), stations[2].NormalX, 6);
        Assert.Equal(Math.Sqrt(0.5), stations[2].NormalY, 6);

        // A closed path does not put its last caliper on its first.
        var closed = new PathBand([new Point2(0, 0), new Point2(100, 0), new Point2(100, 100), new Point2(0, 100)], closed: true, width: 10);
        Assert.Equal(new[] { 0.0, 100, 200, 300 }, CaliperScan.Stations(closed, 4).Select(station => station.Along));
    }

    // ---- the path as a parameter -------------------------------------------------

    [Fact]
    public void A_path_reads_back_what_it_writes()
    {
        var path = new PathBand([new Point2(10, 20), new Point2(30.5, 40), new Point2(50, 20)], closed: true, width: 24);

        Assert.Equal("10, 20; 30.5, 40; 50, 20 | 24 | closed", path.ToString());
        Assert.True(PathBand.TryParse(path.ToString(), 40, out var read));
        Assert.Equal(path, read);

        // The width may be left off, keeping the one the path had.
        Assert.True(PathBand.TryParse("0, 0; 10, 0", 33, out var bare));
        Assert.Equal(33, bare.Width);
        Assert.False(bare.Closed);

        Assert.False(PathBand.TryParse("0, 0", 40, out _));
        Assert.False(PathBand.TryParse("0, 0; ten, 0", 40, out _));
    }

    [Fact]
    public void A_path_is_saved_with_the_recipe()
    {
        var graph = new VisionGraph();
        var node = new FindCurveNode
        {
            Path = new PathBand([new Point2(1, 2), new Point2(3, 4), new Point2(5, 9)], closed: true, width: 17),
        };
        graph.Add(node);

        var reopened = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph)).Graph;

        var copy = Assert.IsType<FindCurveNode>(Assert.Single(reopened.Nodes));
        Assert.Equal(node.Path, copy.Path);
    }

    // ---- shapes driving regions ---------------------------------------------------

    /// <summary>
    /// A found box drives Find Line's region: where the box is and how big,
    /// turned to face the way the drawn region does, so the calipers start
    /// on the same side however the box was reported.
    /// </summary>
    [Fact]
    public void A_wired_box_is_the_region_facing_the_way_the_drawn_one_does()
    {
        var drawn = new OrientedRect(0, 0, 100, 40, 0);

        // The same box reported a quarter turn round with its sides swapped.
        var box = new OrientedRectShape(new Point2(200, 150), 40, 300, 90);
        var region = (OrientedRect)ShapeRegions.From(box, typeof(OrientedRect), drawn);

        Assert.Equal(new OrientedRect(200, 150, 300, 40, 0), region);

        // A line keeps the drawn width and takes the line's length.
        var line = new LineShape(new Point2(300, 100), new Point2(100, 100));
        var along = (OrientedRect)ShapeRegions.From(line, typeof(OrientedRect), drawn);
        Assert.Equal(new OrientedRect(200, 100, 200, 40, 0), along);

        // A circle drives a band of the drawn width; a point moves it.
        var ring = (Ring)ShapeRegions.From(new CircleShape(new Point2(5, 6), 50), typeof(Ring), new Ring(0, 0, 10, 30));
        Assert.Equal(Ring.FromMean(5, 6, 50, 10), ring);

        var moved = (Ring)ShapeRegions.From(new PointShape(new Point2(7, 8)), typeof(Ring), new Ring(0, 0, 10, 30));
        Assert.Equal(new Ring(7, 8, 10, 30), moved);
    }

    [Fact]
    public void Shapes_can_be_wired_into_a_region_and_a_finder_searches_there()
    {
        var picture = () => Render(Width, Height, (x, y) => y - 250);

        var finder = new FindLineNode { Region = new OrientedRect(50, 50, 30, 60, 0) };
        var (graph, _) = Wire(picture, finder);

        var box = new ShapesNode(new RectShape(new Bounds(150, 220, 300, 60)));
        graph.Add(box);

        var regionPort = finder.FindInput(nameof(FindLineNode.Region))!;
        Assert.True(graph.TryConnect(box.Out, regionPort).Success);

        var result = Run(graph);

        var line = Assert.IsType<LineShape>(Assert.Single(result.Shapes(finder.LineOut).Shapes));
        Assert.InRange(line.Start.Y, 249.7, 250.3);
        Assert.InRange(Math.Min(line.Start.X, line.End.X), 145, 160);
    }

    /// <summary>Shapes drive geometry, and nothing else - a number is not a shape's to set.</summary>
    [Fact]
    public void Shapes_connect_only_to_geometry()
    {
        var graph = new VisionGraph();
        var shapes = new ShapesNode(new PointShape(new Point2(1, 1)));
        var finder = new FindLineNode();
        graph.Add(shapes);
        graph.Add(finder);

        Assert.True(graph.CanConnect(shapes.Out, finder.FindInput(nameof(FindLineNode.Region))!).Success);
        Assert.False(graph.CanConnect(shapes.Out, finder.ImageIn).Success);
    }
}
