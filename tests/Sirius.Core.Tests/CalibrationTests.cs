using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Reporting in millimetres rather than pixels.
/// </summary>
/// <remarks>
/// Every reading this product took was in pixels, and no drawing is
/// written in pixels, so a tolerance could never be stated in the units
/// it arrives in. This is the conversion, and it travels on a wire
/// because a scale is not a property of a recipe - two cameras have two,
/// and a Resize changes one.
/// </remarks>
public class CalibrationTests
{
    public CalibrationTests() => TestCatalog.Ensure();

    // ---- the conversion ---------------------------------------------------

    [Fact]
    public void A_length_converts_by_the_scale()
    {
        var calibration = new Calibration(0.05, "mm");

        Assert.Equal(5, calibration.ToUnits(100), 9);
        Assert.Equal(100, calibration.ToPixels(5), 9);
    }

    /// <summary>
    /// How anybody actually calibrates: measure something known, type
    /// what it really is.
    /// </summary>
    [Fact]
    public void A_known_length_gives_the_scale()
    {
        var calibration = Calibration.FromKnownLength(pixels: 250, length: 10, "mm");

        Assert.Equal(0.04, calibration.UnitsPerPixel, 9);
        Assert.Equal("mm", calibration.Unit);
    }

    /// <summary>
    /// An area scales by the square. Put through the length conversion
    /// it would be wrong by the scale factor itself, which on a typical
    /// lens is a factor of twenty.
    /// </summary>
    [Fact]
    public void An_area_scales_by_the_square()
    {
        var calibration = new Calibration(0.1, "mm");

        Assert.Equal(1, calibration.AreaToUnits(100), 9);
        Assert.NotEqual(calibration.ToUnits(100), calibration.AreaToUnits(100));
    }

    /// <summary>
    /// A scale of nothing collapses every reading to nothing, and a
    /// negative one turns them inside out. Neither is a scale anybody
    /// meant, so neither is accepted.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.PositiveInfinity)]
    public void An_impossible_scale_falls_back_to_pixels(double scale)
    {
        var calibration = new Calibration(scale, "mm");

        Assert.Equal(1, calibration.UnitsPerPixel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_unit_reads_as_pixels(string? unit) =>
        Assert.Equal("px", new Calibration(0.05, unit).Unit);

    [Fact]
    public void Pixels_at_one_to_one_is_not_a_calibration()
    {
        Assert.False(Calibration.None.IsCalibrated);
        Assert.False(new Calibration(1, "px").IsCalibrated);

        Assert.True(new Calibration(1, "mm").IsCalibrated);
        Assert.True(new Calibration(0.05, "px").IsCalibrated);
    }

    [Theory]
    [InlineData("0.05 mm", 0.05, "mm")]
    [InlineData("0.0412 in", 0.0412, "in")]
    [InlineData("2", 2, "px")]
    public void It_reads_what_people_type(string text, double scale, string unit)
    {
        Assert.True(Calibration.TryParse(text, out var calibration));

        Assert.Equal(scale, calibration.UnitsPerPixel, 9);
        Assert.Equal(unit, calibration.Unit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mm")]
    [InlineData("0.05 mm extra")]
    public void Nonsense_is_refused(string? text) =>
        Assert.False(Calibration.TryParse(text, out _));

    [Fact]
    public void It_round_trips_through_its_own_text()
    {
        var original = new Calibration(0.0412, "mm");

        Assert.True(Calibration.TryParse(original.ToString(), out var again));
        Assert.Equal(original, again);
    }

    // ---- the node ---------------------------------------------------------

    [Fact]
    public void The_node_publishes_a_scale_from_a_known_length()
    {
        var (calibration, scale) = Run(node =>
        {
            node.Pixels = 200;
            node.Length = 10;
            node.Unit = "mm";
        });

        Assert.Equal(0.05, calibration.UnitsPerPixel, 9);
        Assert.Equal("mm", calibration.Unit);
        Assert.Equal(0.05, scale, 9);
    }

    [Fact]
    public void The_node_takes_a_scale_typed_straight_in()
    {
        var (calibration, _) = Run(node =>
        {
            node.Source = CalibrationSource.Scale;
            node.UnitsPerPixel = 0.0254;
            node.Unit = "in";
        });

        Assert.Equal(0.0254, calibration.UnitsPerPixel, 9);
        Assert.Equal("in", calibration.Unit);
    }

    /// <summary>
    /// Measuring the reference is what saves anybody counting pixels by
    /// hand, which is the one number that cannot be read off a drawing.
    /// </summary>
    [Fact]
    public void A_reference_on_the_wire_is_measured_for_its_length()
    {
        var (calibration, _) = Run(
            node =>
            {
                node.Pixels = 1;
                node.Length = 25;
                node.Unit = "mm";
            },
            new LineShape(new Point2(100, 100), new Point2(600, 100)));

        // 500 pixels is 25 mm, so a pixel is 0.05 mm - and the typed
        // Pixels of 1 was ignored in favour of what was measured.
        Assert.Equal(0.05, calibration.UnitsPerPixel, 9);
    }

    /// <summary>
    /// Without both halves it reports pixels and says so, rather than
    /// publishing a scale of nothing that silently zeroes every reading
    /// downstream.
    /// </summary>
    [Theory]
    [InlineData(0, 10)]
    [InlineData(100, 0)]
    public void A_half_finished_calibration_warns_and_reports_pixels(double pixels, double length)
    {
        var graph = new VisionGraph();
        var node = new CalibrateNode { Pixels = pixels, Length = length, Unit = "mm" };
        graph.Add(node);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Warning, node.Status);
        Assert.False(((Calibration)scheduler.GetOutput(node.CalibrationOut)!).IsCalibrated);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(100)]
    public void A_circle_reference_is_measured_across_its_diameter(double radius)
    {
        var length = CalibrateNode.LengthOf(new CircleShape(new Point2(0, 0), radius));

        Assert.Equal(radius * 2, length, 9);
    }

    /// <summary>
    /// A producer's own length wins: a Measure node publishes exactly
    /// the reading that was wanted, and re-deriving it from the
    /// annotation's geometry would be a second answer to one question.
    /// </summary>
    [Fact]
    public void A_published_length_beats_the_geometry()
    {
        var shape = new LineShape(new Point2(0, 0), new Point2(10, 0))
        {
            Metrics = new Dictionary<string, double> { [ShapeMetric.Length] = 999 },
        };

        Assert.Equal(999, CalibrateNode.LengthOf(shape), 9);
    }

    // ---- the whole chain ---------------------------------------------------

    /// <summary>
    /// The point of all of it: a distance reported in millimetres, and a
    /// tolerance stated in millimetres passing or failing it.
    /// </summary>
    [Theory]
    [InlineData(0.9, 1.1, true)]
    [InlineData(2.0, 3.0, false)]
    public void A_measurement_is_reported_and_checked_in_real_units(
        double minimum, double maximum, bool expected)
    {
        var graph = new VisionGraph();

        // Two circles 100 pixels apart, with a pixel worth a hundredth
        // of a millimetre: a reading of exactly 1 mm.
        var a = new ShapeSourceNode(new CircleShape(new Point2(0, 0), 5));
        var b = new ShapeSourceNode(new CircleShape(new Point2(100, 0), 5));

        var calibrate = new CalibrateNode
        {
            Source = CalibrationSource.Scale,
            UnitsPerPixel = 0.01,
            Unit = "mm",
        };

        var measure = new MeasureNode();
        var check = new LimitCheckNode { Minimum = minimum, Maximum = maximum };

        foreach (var node in new VisionNode[] { a, b, calibrate, measure, check })
            graph.Add(node);

        graph.TryConnect(a.ShapesOut, measure.FirstIn);
        graph.TryConnect(b.ShapesOut, measure.SecondIn);

        Assert.True(graph.TryConnect(calibrate.CalibrationOut, measure.CalibrationIn).Success);

        var value = check.Inputs.Single(p => p.Name == nameof(LimitCheckNode.Value));
        graph.TryConnect(measure.ValueOut, value);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(1.0, (double)scheduler.GetOutput(measure.ValueOut)!, 9);
        Assert.Equal(expected, (bool)scheduler.GetOutput(check.PassOut)!);
    }

    /// <summary>
    /// And the drawn label says the unit it is in. A reading converted
    /// to millimetres and still labelled "px" would be worse than
    /// either on its own.
    /// </summary>
    [Fact]
    public void The_label_is_written_in_the_calibrated_unit()
    {
        var annotation = Measured(new Calibration(0.01, "mm"));

        Assert.Equal("1.00 mm", annotation.Shapes[0].Label);
    }

    [Fact]
    public void Without_a_calibration_it_stays_in_pixels()
    {
        var annotation = Measured(calibration: null);

        Assert.Equal("100.00 px", annotation.Shapes[0].Label);
    }

    /// <summary>
    /// An angle is not a length, and a scale factor cannot change one:
    /// two lines three degrees apart are three degrees apart whatever a
    /// pixel is worth.
    /// </summary>
    [Fact]
    public void An_angle_is_not_scaled()
    {
        var graph = new VisionGraph();
        var a = new ShapeSourceNode(new LineShape(new Point2(0, 0), new Point2(100, 0)));
        var b = new ShapeSourceNode(new LineShape(new Point2(0, 0), new Point2(100, 100)));

        var calibrate = new CalibrateNode
        {
            Source = CalibrationSource.Scale,
            UnitsPerPixel = 0.01,
            Unit = "mm",
        };

        var measure = new MeasureNode { Kind = MeasureKind.Angle };

        foreach (var node in new VisionNode[] { a, b, calibrate, measure })
            graph.Add(node);

        graph.TryConnect(a.ShapesOut, measure.FirstIn);
        graph.TryConnect(b.ShapesOut, measure.SecondIn);
        graph.TryConnect(calibrate.CalibrationOut, measure.CalibrationIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(45, (double)scheduler.GetOutput(measure.ValueOut)!, 6);
    }

    // ---- plumbing ----------------------------------------------------------

    private static ShapeSet Measured(Calibration? calibration)
    {
        var graph = new VisionGraph();
        var a = new ShapeSourceNode(new CircleShape(new Point2(0, 0), 5));
        var b = new ShapeSourceNode(new CircleShape(new Point2(100, 0), 5));
        var measure = new MeasureNode();

        foreach (var node in new VisionNode[] { a, b, measure })
            graph.Add(node);

        graph.TryConnect(a.ShapesOut, measure.FirstIn);
        graph.TryConnect(b.ShapesOut, measure.SecondIn);

        if (calibration is { } scale)
        {
            var calibrate = new CalibrateNode
            {
                Source = CalibrationSource.Scale,
                UnitsPerPixel = scale.UnitsPerPixel,
                Unit = scale.Unit,
            };

            graph.Add(calibrate);
            graph.TryConnect(calibrate.CalibrationOut, measure.CalibrationIn);
        }

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        return (ShapeSet)scheduler.GetOutput(measure.AnnotationOut)!;
    }

    private sealed class ShapeSourceNode : VisionNode
    {
        private readonly ShapeSet _set;

        public ShapeSourceNode(params Shape[] shapes)
        {
            _set = new ShapeSet(shapes, 640, 480);
            ShapesOut = AddOutput<ShapeSet>("Out");
        }

        public OutputPort ShapesOut { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(ShapesOut, _set);
    }

    private static (Calibration Calibration, double Scale) Run(
        Action<CalibrateNode> configure, params Shape[] reference)
    {
        var graph = new VisionGraph();
        var node = new CalibrateNode();
        configure(node);
        graph.Add(node);

        if (reference.Length > 0)
        {
            var source = new ShapeSourceNode(reference);
            graph.Add(source);
            graph.TryConnect(source.ShapesOut, node.ReferenceIn);
        }

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        return (
            (Calibration)scheduler.GetOutput(node.CalibrationOut)!,
            (double)scheduler.GetOutput(node.ScaleOut)!);
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
