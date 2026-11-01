using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;
using static Sirius.Core.Tests.EdgeTestKit;

namespace Sirius.Core.Tests;

/// <summary>
/// Missing and extra material along a straight edge, and the straightness
/// Line Calipers reads off one.
/// </summary>
/// <remarks>
/// Parts drawn exactly: dark air above, the part light below an edge along
/// y = 200, with a notch cut 15 deep into it or a bump standing 12 above
/// it, 40 wide at x = 280 to 320. The region runs along the edge, 80 high,
/// so its calipers start in the air - which is the side Direction says is
/// out of the part.
/// </remarks>
public class LineDefectsTests
{
    public LineDefectsTests() => TestCatalog.Ensure();

    private const int Width = 600;
    private const int Height = 400;

    private static LineDefectsNode Node(Action<LineDefectsNode>? configure = null)
    {
        var node = new LineDefectsNode { Region = new OrientedRect(300, 200, 400, 80, 0) };
        configure?.Invoke(node);
        return node;
    }

    /// <summary>The part below y = <paramref name="edge"/>(x).</summary>
    private static Func<ImageData> Part(Func<double, double> edge) =>
        () => Render(Width, Height, (x, y) => y - edge(x));

    private static readonly Func<ImageData> Straight = Part(_ => 200);

    private static readonly Func<ImageData> Notched = Part(x => x is >= 280 and <= 320 ? 215 : 200);

    private static readonly Func<ImageData> Bumped = Part(x => x is >= 280 and <= 320 ? 188 : 200);

    private static GraphScheduler Inspect(Func<ImageData> picture, LineDefectsNode node, Calibration? calibration = null)
    {
        var (graph, _) = Wire(picture, node);

        if (calibration is { } given)
        {
            var scale = new CalibrationValue(given);
            graph.Add(scale);
            graph.TryConnect(scale.Out, node.CalibrationIn);
        }

        return Run(graph);
    }

    [Fact]
    public void A_straight_edge_passes_with_a_small_margin()
    {
        var node = Node();
        var result = Inspect(Straight, node);

        Assert.True(result.Get<bool>(node.PassOut));
        Assert.Equal(0, result.Get<int>(node.CountOut));
        Assert.InRange(Math.Abs(result.Get<double>(node.WorstOut)), 0, 0.3);

        var line = Assert.IsType<LineShape>(result.Shapes(node.ReferenceOut).Shapes[0]);
        Assert.InRange(line.Start.Y, 199.7, 200.3);
        Assert.InRange(line.End.Y, 199.7, 200.3);
    }

    [Fact]
    public void A_notch_is_missing_material_where_and_as_deep_as_it_was_cut()
    {
        var node = Node();
        var result = Inspect(Notched, node);

        Assert.False(result.Get<bool>(node.PassOut));

        var defect = Assert.Single(result.Shapes(node.DefectsOut).Shapes);
        Assert.StartsWith("Missing", defect.Label, StringComparison.Ordinal);
        Assert.InRange(defect.Metric(ShapeMetric.Deviation)!.Value, -15.5, -14.5);
        Assert.Equal(0xFFEF4444u, defect.Colour);

        // 40 wide, from 280 to 320: 180 to 220 along a region starting at 100.
        Assert.InRange(defect.Metric(ShapeMetric.Along)!.Value, 190, 210);
        Assert.InRange(defect.Metric(ShapeMetric.Length)!.Value, 36, 46);
        Assert.InRange(defect.Metric(ShapeMetric.Area)!.Value, 520, 700);
        Assert.InRange(result.Get<double>(node.WorstOut), -15.5, -14.5);
    }

    [Fact]
    public void A_bump_is_extra_material_as_high_as_it_stands()
    {
        var node = Node();
        var result = Inspect(Bumped, node);

        var defect = Assert.Single(result.Shapes(node.DefectsOut).Shapes);
        Assert.StartsWith("Excess", defect.Label, StringComparison.Ordinal);
        Assert.InRange(defect.Metric(ShapeMetric.Deviation)!.Value, 11.5, 12.5);
        Assert.Equal(0xFF3B82F6u, defect.Colour);
    }

    /// <summary>
    /// Scanning the other way says the part is on the other side, so the
    /// same notch - material gone from the light side - now reads as extra
    /// material on a dark part.
    /// </summary>
    [Fact]
    public void Direction_says_which_side_is_the_part()
    {
        var node = Node(n => n.Direction = ScanDirection.Back);

        var defect = Assert.Single(Inspect(Notched, node).Shapes(node.DefectsOut).Shapes);
        Assert.StartsWith("Excess", defect.Label, StringComparison.Ordinal);
    }

    /// <summary>
    /// A chip a quarter of the edge long. A plain fit leans into it and
    /// reads it shallow; the vote follows the good edge and reads it at its
    /// true depth.
    /// </summary>
    [Fact]
    public void A_long_chip_does_not_tilt_the_reference_towards_itself()
    {
        var node = Node();
        var result = Inspect(Part(x => x is >= 380 and <= 480 ? 210 : 200), node);

        var line = Assert.IsType<LineShape>(result.Shapes(node.ReferenceOut).Shapes[0]);
        Assert.InRange(line.Start.Y, 199.7, 200.3);
        Assert.InRange(line.End.Y, 199.7, 200.3);

        var defect = Assert.Single(result.Shapes(node.DefectsOut).Shapes);
        Assert.InRange(defect.Metric(ShapeMetric.Deviation)!.Value, -10.5, -9.5);
    }

    /// <summary>A tilted edge in an upright region: the depth is square to the edge, not along the calipers.</summary>
    [Fact]
    public void A_tilted_edge_is_measured_square_to_itself()
    {
        // A notch 12 deep square to an edge rising 8 degrees.
        var picture = () => Render(Width, Height, (x, y) =>
        {
            var along = ((x - 300) * Math.Cos(8 * Math.PI / 180)) + ((y - 200) * Math.Sin(8 * Math.PI / 180));
            var off = (-(x - 300) * Math.Sin(8 * Math.PI / 180)) + ((y - 200) * Math.Cos(8 * Math.PI / 180));
            return along is >= -20 and <= 20 ? off - 12 : off;
        });

        var node = Node();
        var result = Inspect(picture, node);

        var defect = Assert.Single(result.Shapes(node.DefectsOut).Shapes);
        Assert.InRange(defect.Metric(ShapeMetric.Deviation)!.Value, -12.5, -11.5);
    }

    [Fact]
    public void A_defect_within_tolerance_passes()
    {
        var node = Node(n => n.InnerTolerance = 20);

        Assert.True(Inspect(Notched, node).Get<bool>(node.PassOut));
    }

    /// <summary>At 0.1 mm a pixel a 1 mm tolerance is 10 px, which the 15 px notch exceeds - reported in mm.</summary>
    [Fact]
    public void Tolerances_and_readings_are_in_the_calibrations_unit()
    {
        var node = Node(n => n.InnerTolerance = 1);
        var result = Inspect(Notched, node, new Calibration(0.1, "mm"));

        var defect = Assert.Single(result.Shapes(node.DefectsOut).Shapes);
        Assert.Contains("mm", defect.Label, StringComparison.Ordinal);
        Assert.InRange(result.Get<double>(node.WorstOut), -1.55, -1.45);
    }

    /// <summary>
    /// An edge that bows 6 px over its length with a notch in it: against
    /// a straight line the bow itself fails; against its local form only
    /// the notch does, and Form reports the bow.
    /// </summary>
    [Fact]
    public void Against_its_local_form_a_bowed_edge_shows_only_its_notch()
    {
        double Bow(double x) => 200 + (6 * Math.Sin(Math.PI * (x - 100) / 400));

        var picture = Part(x => x is >= 280 and <= 300 ? Bow(x) + 10 : Bow(x));

        var straight = Node(n => n.InnerTolerance = n.OuterTolerance = 2);
        Assert.True(Inspect(picture, straight).Get<int>(straight.CountOut) > 1);

        var local = Node(n => n.ReferenceKind = LineDefectReference.LocalForm);
        var result = Inspect(picture, local);

        var defect = Assert.Single(result.Shapes(local.DefectsOut).Shapes);
        Assert.StartsWith("Missing", defect.Label, StringComparison.Ordinal);
        Assert.InRange(defect.Metric(ShapeMetric.Deviation)!.Value, -11, -8.5);
        Assert.InRange(result.Get<double>(local.FormOut), 4, 7);
        Assert.Equal(["Deviation", "Form"], result.Get<ProfileData>(local.ProfileOut).Channels.Select(c => c.Name));
    }

    /// <summary>A line wired in sets where to search and, with Reference Wired, what to measure against.</summary>
    [Fact]
    public void A_wired_line_is_followed_and_measured_against()
    {
        var node = Node(n =>
        {
            n.Region = new OrientedRect(50, 50, 20, 20, 180);
            n.ReferenceKind = LineDefectReference.Wired;
            n.BandWidth = 60;
        });

        var (graph, _) = Wire(Notched, node);
        var line = new ShapesNode(new LineShape(new Point2(500, 200), new Point2(100, 200)));
        graph.Add(line);
        Assert.True(graph.TryConnect(line.Out, node.LineIn).Success);

        var result = Run(graph);

        var defect = Assert.Single(result.Shapes(node.DefectsOut).Shapes);

        // The drawn region faced the other way, and the wired line was
        // given right to left; turned to face the drawn region, the scans
        // start below - in the part - so the notch is extra material in the air.
        Assert.StartsWith("Excess", defect.Label, StringComparison.Ordinal);
        Assert.InRange(Math.Abs(defect.Metric(ShapeMetric.Deviation)!.Value), 14.5, 15.5);
    }

    // ---- Line Calipers' straightness ----------------------------------------------

    private static LineCalipersNode Calipers() => new()
    {
        Region = new OrientedRect(300, 200, 400, 80, 0),
        CaliperCount = 81,
    };

    [Fact]
    public void A_straight_edge_reads_as_straight()
    {
        var node = Calipers();
        var (graph, _) = Wire(Straight, node);
        var result = Run(graph);

        Assert.InRange(result.Get<double>(node.StraightnessOut), 0, 0.2);
    }

    /// <summary>
    /// A bump 12 high on an otherwise straight edge: straightness is the
    /// bump's height, less the little the least-squares line rises towards it.
    /// </summary>
    [Fact]
    public void Straightness_is_the_height_of_a_bump_above_the_line()
    {
        var node = Calipers();
        var (graph, _) = Wire(Bumped, node);
        var result = Run(graph);

        Assert.InRange(result.Get<double>(node.StraightnessOut), 11.5, 12.5);

        // Out of the region's middle towards where the scans start is
        // negative offset, and so negative deviation.
        var points = result.Shapes(node.PointsOut).Shapes;
        var highest = points.MinBy(point => point.Metric(ShapeMetric.Deviation))!;
        Assert.InRange(((PointShape)highest).At.X, 279, 321);

        var profile = result.Get<ProfileData>(node.ProfileOut);
        Assert.Equal("Deviation", profile.Channels[0].Name);
        Assert.Equal(81, profile.Length);
        Assert.Equal(5, profile.Spacing, 6);
    }
}
