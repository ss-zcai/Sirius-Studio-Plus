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
/// Missing and extra material round a circular edge.
/// </summary>
/// <remarks>
/// Drawn parts with known defects: a light disc of radius 120 at
/// (200, 200) on black, with a notch cut into its top, a bump added to it,
/// or a large flat taken off - the cases in the two example images.
/// </remarks>
public class CircleDefectsTests
{
    public CircleDefectsTests() => TestCatalog.Ensure();

    private static CircleDefectsNode Node(Action<CircleDefectsNode>? configure = null)
    {
        var node = new CircleDefectsNode { Band = new Ring(200, 200, 90, 150) };
        configure?.Invoke(node);
        return node;
    }

    // ---- a good part ---------------------------------------------------------------

    [Fact]
    public void A_round_part_passes_with_a_small_margin()
    {
        var node = Node();
        var result = Run(Disc, node);

        Assert.True(result.Get<bool>(node.PassOut));
        Assert.Equal(0, result.Get<int>(node.CountOut));
        Assert.InRange(Math.Abs(result.Get<double>(node.WorstOut)), 0, 1);

        var circle = Assert.IsType<CircleShape>(result.Shapes(node.ReferenceOut).Shapes[0]);
        Assert.InRange(circle.Radius, 119.5, 121);
    }

    // ---- the two examples ------------------------------------------------------------

    /// <summary>
    /// A notch 20 deep at the top - 270 degrees, measured clockwise from +X
    /// with y down - is one missing defect, as deep as it was cut.
    /// </summary>
    [Fact]
    public void A_notch_is_missing_material_where_and_as_deep_as_it_was_cut()
    {
        var node = Node();
        var result = Run(Notched, node);

        Assert.False(result.Get<bool>(node.PassOut));

        var defect = Assert.Single(result.Shapes(node.DefectsOut).Shapes);
        Assert.StartsWith("Missing", defect.Label, StringComparison.Ordinal);
        // Half a pixel either way for where a filled rectangle's edge lies.
        Assert.InRange(defect.Metric(ShapeMetric.Deviation)!.Value, -21.5, -19.5);
        Assert.InRange(defect.Metric(ShapeMetric.Angle)!.Value, 265, 290);

        // The notch is 40 x 20 less the sliver of disc that was never there.
        Assert.InRange(defect.Metric(ShapeMetric.Area)!.Value, 650, 850);
        Assert.Equal(0xFFEF4444u, defect.Colour);
    }

    /// <summary>
    /// A bump 40 wide standing 15 above the top. Its highest point is a
    /// corner, not its middle: 30 to the side and 135 up is 138.3 from the
    /// centre, 18.3 outside the circle - and that is what is reported.
    /// </summary>
    [Fact]
    public void A_bump_is_extra_material_as_high_as_it_stands()
    {
        var node = Node();
        var result = Run(Bumped, node);

        var defect = Assert.Single(result.Shapes(node.DefectsOut).Shapes);
        Assert.StartsWith("Excess", defect.Label, StringComparison.Ordinal);
        Assert.InRange(defect.Metric(ShapeMetric.Deviation)!.Value, 17.5, 19);
        Assert.Equal(0xFF3B82F6u, defect.Colour);
    }

    /// <summary>
    /// A flat cut off the top covering nearly a quarter of the edge. A
    /// least-squares circle leans into it and reads it shallow; the
    /// consensus fit follows the good edge and reads it at its true depth
    /// of 30.
    /// </summary>
    [Fact]
    public void A_large_defect_does_not_drag_the_reference_towards_itself()
    {
        var node = Node();
        var result = Run(Flatted, node);

        var circle = Assert.IsType<CircleShape>(result.Shapes(node.ReferenceOut).Shapes[0]);
        Assert.InRange(circle.Radius, 119.5, 121);
        Assert.InRange(circle.Centre.Y, 199, 201);

        var defect = Assert.Single(result.Shapes(node.DefectsOut).Shapes);
        Assert.InRange(defect.Metric(ShapeMetric.Deviation)!.Value, -31, -29);

        // What the plain fit would have made of it, for comparison.
        var points = result.Shapes(node.PointsOut).Shapes.Select(shape => ((PointShape)shape).At).ToArray();
        var plain = CircleFit.Fit(points);
        Assert.True(Math.Abs(plain.Centre.Y - 200) > 2, "the plain fit was expected to lean into the flat");
    }

    [Fact]
    public void A_defect_within_tolerance_passes()
    {
        var node = Node(n => n.InnerTolerance = 25);

        Assert.True(Run(Notched, node).Get<bool>(node.PassOut));
    }

    /// <summary>At 0.1 mm a pixel, a 1.5 mm inner tolerance is 15 px, which the 20 px notch still exceeds.</summary>
    [Fact]
    public void Tolerances_and_readings_are_in_the_calibrations_unit()
    {
        var node = Node(n => n.InnerTolerance = 1.5);
        var result = Run(Notched, node, new Calibration(0.1, "mm"));

        var defect = Assert.Single(result.Shapes(node.DefectsOut).Shapes);
        Assert.Contains("mm", defect.Label, StringComparison.Ordinal);
        Assert.InRange(result.Get<double>(node.WorstOut), -2.15, -1.95);
    }

    /// <summary>A part of radius 120 against a nominal 110 is oversize all the way round.</summary>
    [Fact]
    public void Against_a_nominal_radius_an_oversize_part_fails_all_round()
    {
        var node = Node(n =>
        {
            n.ReferenceKind = DefectReference.Nominal;
            n.NominalRadius = 110;
        });

        var defect = Assert.Single(Run(Disc, node).Shapes(node.DefectsOut).Shapes);
        Assert.StartsWith("Excess", defect.Label, StringComparison.Ordinal);
        Assert.InRange(defect.Metric(ShapeMetric.Deviation)!.Value, 9, 11.5);
    }

    // ---- grouping calipers into defects --------------------------------------------

    private static double[] Edge(int count, params (int From, int To, double Value)[] stretches)
    {
        var deviation = new double[count];

        foreach (var (from, to, value) in stretches)
        {
            for (var i = from; i <= to; i++)
                deviation[i] = value;
        }

        return deviation;
    }

    [Fact]
    public void A_defect_across_the_seam_is_one_defect()
    {
        var runs = CircleDefectsNode.FindRuns(Edge(36, (34, 35, -5), (0, 2, -5)), 2, 2, 0, 1, wraps: true);

        var run = Assert.Single(runs);
        Assert.Equal(DefectKind.Missing, run.Kind);
        Assert.Equal(34, run.Start);
        Assert.Equal(5, run.Length);
    }

    [Fact]
    public void An_arc_does_not_wrap()
    {
        var runs = CircleDefectsNode.FindRuns(Edge(36, (34, 35, -5), (0, 2, -5)), 2, 2, 0, 1, wraps: false);

        Assert.Equal(2, runs.Count);
    }

    [Fact]
    public void Runs_a_small_gap_apart_merge_and_short_ones_drop()
    {
        var deviation = Edge(36, (10, 12, 5), (14, 16, 5), (25, 25, 5));

        var runs = CircleDefectsNode.FindRuns(deviation, 2, 2, mergeGap: 1, minimumCalipers: 2, wraps: true);

        var run = Assert.Single(runs);
        Assert.Equal((DefectKind.Excess, 10, 7), (run.Kind, run.Start, run.Length));
    }

    /// <summary>
    /// The ends of a defect are followed out while the edge is still more
    /// than half the tolerance off, so its length is where it is, not where
    /// it first crosses the line.
    /// </summary>
    [Fact]
    public void A_defect_runs_out_to_where_it_is_half_out()
    {
        var deviation = Edge(36, (9, 9, 1.5), (10, 12, 5), (13, 13, 1.5), (14, 14, 0.5));

        var run = Assert.Single(CircleDefectsNode.FindRuns(deviation, 2, 2, 0, 1, wraps: true));

        Assert.Equal((9, 5), (run.Start, run.Length));
    }

    /// <summary>A burr that runs past the band leaves calipers with no edge beside it; they are part of it.</summary>
    [Fact]
    public void No_edge_beside_a_defect_is_part_of_it_and_alone_is_its_own()
    {
        var deviation = Edge(36, (10, 12, 5), (20, 21, 0));
        deviation[13] = double.NaN;
        deviation[14] = double.NaN;
        deviation[30] = double.NaN;

        var runs = CircleDefectsNode.FindRuns(deviation, 2, 2, 0, 1, wraps: true);

        Assert.Equal(2, runs.Count);
        Assert.Equal((DefectKind.Excess, 10, 5), (runs[0].Kind, runs[0].Start, runs[0].Length));
        Assert.Equal((DefectKind.OutOfBand, 30, 1), (runs[1].Kind, runs[1].Start, runs[1].Length));
    }

    [Fact]
    public void The_consensus_fit_is_the_same_every_time()
    {
        var points = Enumerable.Range(0, 90)
            .Select(i => i * 4 * Math.PI / 180)
            .Select(a => new Point2(100 + (50 * Math.Cos(a)), 100 + ((a < 1 ? 40 : 50) * Math.Sin(a))))
            .ToArray();

        var first = CircleFit.FitConsensus(points, 0.5);
        var second = CircleFit.FitConsensus(points, 0.5);

        Assert.Equal(first.Centre, second.Centre);
        Assert.Equal(first.Radius, second.Radius);
        Assert.Equal(50, first.Radius, 1);
    }

    // ---- pictures ----------------------------------------------------------------------

    private static Mat Disc()
    {
        var mat = new Mat(400, 400, DepthType.Cv8U, 1);
        mat.SetTo(new MCvScalar(40));
        CvInvoke.Circle(mat, new System.Drawing.Point(200, 200), 120, new MCvScalar(230), -1, LineType.AntiAlias);
        return mat;
    }

    /// <summary>The disc, with a notch 40 wide cut 20 deep into its top.</summary>
    private static Mat Notched()
    {
        var mat = Disc();
        CvInvoke.Rectangle(mat, new System.Drawing.Rectangle(190, 70, 40, 30), new MCvScalar(40), -1);
        return mat;
    }

    /// <summary>The disc, with a bump 40 wide standing 15 above its top.</summary>
    private static Mat Bumped()
    {
        var mat = Disc();
        CvInvoke.Rectangle(mat, new System.Drawing.Rectangle(190, 65, 40, 20), new MCvScalar(230), -1);
        return mat;
    }

    /// <summary>The disc, with everything above y = 110 cut off: a flat 30 deep over about 80 degrees.</summary>
    private static Mat Flatted()
    {
        var mat = Disc();
        CvInvoke.Rectangle(mat, new System.Drawing.Rectangle(0, 0, 400, 110), new MCvScalar(40), -1);
        return mat;
    }

    // ---- plumbing -----------------------------------------------------------------------

    private sealed class Outputs(GraphScheduler scheduler)
    {
        public T Get<T>(OutputPort port) => (T)scheduler.GetOutput(port)!;

        public ShapeSet Shapes(OutputPort port) => scheduler.GetOutput<ShapeSet>(port)!;
    }

    private static Outputs Run(Func<Mat> picture, CircleDefectsNode node, Calibration? calibration = null)
    {
        var graph = new VisionGraph();
        var source = new PictureNode(picture);
        graph.Add(source);
        graph.Add(node);
        graph.TryConnect(source.Out, node.ImageIn);

        if (calibration is { } given)
        {
            var scale = new CalibrationNode(given);
            graph.Add(scale);
            graph.TryConnect(scale.Out, node.CalibrationIn);
        }

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        GraphScheduler scheduler;
        try
        {
            scheduler = new GraphScheduler(graph);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        scheduler.RunAsync().GetAwaiter().GetResult();
        return new Outputs(scheduler);
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

        protected override void OnEvaluate(EvalContext context) =>
            context.Set(Out, ImageData.Adopt(_picture()));
    }

    private sealed class CalibrationNode : VisionNode
    {
        private readonly Calibration _calibration;

        public CalibrationNode(Calibration calibration)
        {
            _calibration = calibration;
            Out = AddOutput<Calibration>("Out");
        }

        public OutputPort Out { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(Out, _calibration);
    }
}
