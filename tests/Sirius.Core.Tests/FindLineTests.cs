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
/// Finding a straight edge with calipers across a rectangle.
/// </summary>
/// <remarks>
/// The same caliper core as Find Circle aimed along a rectangle, so these
/// test the aiming rather than the edge finding - where the scans go, what
/// the region's angle does to them, and whether a turned region finds a
/// turned edge at the right angle.
///
/// The test edges are rendered analytically, for the reason
/// <c>FindCircleTests</c> records: a drawn edge sits where the rasteriser
/// put it, not where it was asked for, and measuring sub-pixel accuracy
/// against it concludes the wrong thing.
/// </remarks>
public class FindLineTests
{
    public FindLineTests() => TestCatalog.Ensure();

    private const int Size = 400;

    /// <summary>
    /// A dark field with a light half-plane, its boundary passing through
    /// <paramref name="distance"/> pixels along the normal at
    /// <paramref name="angleDegrees"/>.
    /// </summary>
    /// <remarks>
    /// Intensity ramps across one pixel at the boundary, so the 50%
    /// crossing - what an edge detector finds - is exactly on the line.
    /// </remarks>
    private static ImageData Edge(
        double angleDegrees, double distance, byte dark = 30, byte light = 220)
    {
        var radians = angleDegrees * Math.PI / 180.0;

        // Normal to the edge; the edge itself runs perpendicular to this.
        var nx = Math.Cos(radians);
        var ny = Math.Sin(radians);

        var mat = new Mat(Size, Size, DepthType.Cv8U, 1);
        var data = new byte[Size * Size];

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var signed = ((x - (Size / 2.0)) * nx) + ((y - (Size / 2.0)) * ny) - distance;
                var coverage = Math.Clamp(0.5 + signed, 0, 1);

                data[(y * Size) + x] = (byte)Math.Round(dark + ((light - dark) * coverage));
            }
        }

        mat.SetTo(data);
        return ImageData.Adopt(mat);
    }

    private static (LineShape? Line, int Points, double Angle, NodeStatus Status, string? Message)
        Run(ImageData image, Action<FindLineNode> configure)
    {
        var graph = new VisionGraph();
        var source = new PreparedImageNode(image);
        var finder = new FindLineNode();
        configure(finder);

        graph.Add(source);
        graph.Add(finder);
        graph.TryConnect(source.ImageOut, finder.ImageIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var lines = scheduler.GetOutput(finder.LineOut) as ShapeSet;
        var points = scheduler.GetOutput(finder.PointsOut) as ShapeSet;
        var angle = scheduler.GetOutput(finder.AngleOut) as double? ?? 0;

        return (
            lines?.Shapes.OfType<LineShape>().FirstOrDefault(),
            points?.Count ?? 0,
            angle,
            finder.Status,
            finder.StatusMessage);
    }

    private sealed class PreparedImageNode : VisionNode
    {
        private readonly ImageData _image;

        public PreparedImageNode(ImageData image)
        {
            _image = image;
            ImageOut = AddOutput<ImageData>("Out");
        }

        public OutputPort ImageOut { get; }

        protected override void OnEvaluate(EvalContext context) =>
            context.Forward(ImageOut, _image);
    }

    /// <summary>Perpendicular distance from a point to the fitted segment's line.</summary>
    private static double DistanceTo(LineShape line, double x, double y)
    {
        var dx = line.End.X - line.Start.X;
        var dy = line.End.Y - line.Start.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));

        return Math.Abs(((x - line.Start.X) * -dy / length) + ((y - line.Start.Y) * dx / length));
    }

    // ---- a horizontal edge ---------------------------------------------------

    /// <summary>
    /// A vertical normal makes a horizontal edge through the middle of the
    /// frame. The region straddles it, long axis along it.
    /// </summary>
    [Fact]
    public void It_finds_a_horizontal_edge()
    {
        using var image = Edge(90, 0);

        var (line, points, angle, status, message) = Run(image, node =>
        {
            node.Region = new OrientedRect(200, 200, 240, 60, 0);
        });

        Assert.Equal(NodeStatus.Ready, status);
        Assert.NotNull(line);
        Assert.True(points >= 20, $"only {points} points: {message}");

        // The edge runs along y = 200, so the fit should sit on it.
        Assert.True(DistanceTo(line!, 100, 200) < 0.2, "the fitted line misses the edge");
        Assert.True(DistanceTo(line, 300, 200) < 0.2);
        Assert.True(Math.Abs(Normalise(angle)) < 0.3, $"angle {angle:0.###}");
    }

    /// <summary>
    /// Offset from centre, so a fit that simply returned the middle of the
    /// region would pass the test above and fail this one.
    /// </summary>
    [Theory]
    [InlineData(12.0)]
    [InlineData(-9.5)]
    public void It_finds_an_offset_edge(double offset)
    {
        using var image = Edge(90, offset);

        var (line, _, _, _, _) = Run(image, node =>
        {
            node.Region = new OrientedRect(200, 200, 240, 80, 0);
        });

        Assert.NotNull(line);
        Assert.True(
            DistanceTo(line!, 200, 200 + offset) < 0.2,
            $"the fit is not on the edge at y = {200 + offset}");
    }

    // ---- a turned edge -------------------------------------------------------

    /// <summary>
    /// A turned region finds a turned edge, and reports the angle. This is
    /// the part that is easy to get wrong: the scans have to turn with the
    /// region, and the points have to come back out in image coordinates.
    /// </summary>
    [Theory]
    [InlineData(20)]
    [InlineData(-35)]
    [InlineData(60)]
    public void It_finds_a_turned_edge(double edgeAngle)
    {
        // The edge runs at edgeAngle, so its normal is 90 degrees off.
        using var image = Edge(edgeAngle + 90, 0);

        var (line, _, angle, _, message) = Run(image, node =>
        {
            node.Region = new OrientedRect(200, 200, 200, 60, edgeAngle);
        });

        Assert.NotNull(line);

        var error = Normalise(angle - edgeAngle);

        Assert.True(Math.Abs(error) < 0.5, $"angle off by {error:0.###} degrees: {message}");
        Assert.True(DistanceTo(line!, 200, 200) < 0.3, "the fit misses the edge through the centre");
    }

    // ---- polarity and direction ----------------------------------------------

    /// <summary>
    /// Scanning across from the dark side meets dark to light, so asking
    /// for the opposite finds nothing.
    /// </summary>
    [Fact]
    public void Polarity_excludes_the_wrong_edge()
    {
        using var image = Edge(90, 0);

        var (line, points, _, status, _) = Run(image, node =>
        {
            node.Region = new OrientedRect(200, 200, 240, 60, 0);
            node.Direction = ScanDirection.Across;
            node.Polarity = EdgePolarity.LightToDark;
        });

        Assert.Null(line);
        Assert.Equal(0, points);
        Assert.Equal(NodeStatus.Warning, status);
    }

    [Fact]
    public void Polarity_accepts_the_right_edge()
    {
        using var image = Edge(90, 0);

        var (line, _, _, _, _) = Run(image, node =>
        {
            node.Region = new OrientedRect(200, 200, 240, 60, 0);
            node.Direction = ScanDirection.Across;
            node.Polarity = EdgePolarity.DarkToLight;
        });

        Assert.NotNull(line);
        Assert.True(DistanceTo(line!, 200, 200) < 0.2);
    }

    [Fact]
    public void Direction_reverses_what_polarity_means()
    {
        using var image = Edge(90, 0);

        var (line, _, _, _, _) = Run(image, node =>
        {
            node.Region = new OrientedRect(200, 200, 240, 60, 0);
            node.Direction = ScanDirection.Back;
            node.Polarity = EdgePolarity.LightToDark;
        });

        Assert.NotNull(line);
        Assert.True(DistanceTo(line!, 200, 200) < 0.2);
    }

    // ---- robustness ------------------------------------------------------------

    /// <summary>
    /// A blob sitting in the band sends one caliper to the wrong place.
    /// Rejection should drop it rather than let it tilt the line.
    /// </summary>
    [Fact]
    public void An_outlier_is_rejected()
    {
        using var clean = Edge(90, 0);
        using var scarred = clean.Mat.Clone();

        CvInvoke.Circle(
            scarred, new System.Drawing.Point(110, 180), 5, new MCvScalar(255), -1, LineType.AntiAlias);

        using var image = ImageData.Adopt(scarred.Clone());

        var (line, _, angle, _, _) = Run(image, node =>
        {
            node.Region = new OrientedRect(200, 200, 240, 60, 0);
            node.RejectSigma = 2.0;
        });

        Assert.NotNull(line);
        Assert.True(Math.Abs(Normalise(angle)) < 0.5, $"the outlier tilted the line to {angle:0.###}");
    }

    [Fact]
    public void A_region_that_misses_the_edge_reports_it()
    {
        using var image = Edge(90, 0);

        var (line, points, _, status, message) = Run(image, node =>
        {
            // Entirely inside the light half, well away from the boundary.
            node.Region = new OrientedRect(200, 320, 240, 40, 0);
            node.MinimumContrast = 5;
        });

        Assert.Null(line);
        Assert.Equal(0, points);
        Assert.Equal(NodeStatus.Warning, status);
        Assert.Contains("No edge", message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A region without area cannot be constructed, so the node needs no
    /// check for one - <see cref="OrientedRect"/> clamps both sides to at
    /// least a pixel.
    /// </summary>
    /// <remarks>
    /// Asserted on the type rather than on the node, because the first
    /// version of this test expected the node to raise an error and the
    /// node could not: the guard it was testing was unreachable code.
    /// </remarks>
    [Fact]
    public void A_region_cannot_have_zero_area()
    {
        var flat = new OrientedRect(200, 200, 0, 60, 0);

        Assert.True(flat.Width >= 1);
        Assert.True(flat.Height >= 1);
    }

    /// <summary>
    /// A perfectly hard step - a thresholded image, or an edge falling
    /// exactly between two pixels - still reads in the right place.
    /// </summary>
    /// <remarks>
    /// Smoothing turns a hard step into a straight ramp, whose derivative
    /// is a plateau of identical values rather than a peak. Taking the
    /// first of them put the edge at the plateau's leading corner, half
    /// the smoothing width early; the centre of the plateau is the edge.
    /// Found by the offset case below at -9.5, which lands the boundary
    /// exactly between pixel rows.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(11)]
    public void A_hard_step_is_not_biased_by_smoothing(int smoothing)
    {
        // Half-pixel offset: every pixel is fully dark or fully light.
        using var image = Edge(90, -9.5);

        var (line, _, _, _, _) = Run(image, node =>
        {
            node.Region = new OrientedRect(200, 200, 240, 80, 0);
            node.Smoothing = smoothing;
        });

        Assert.NotNull(line);
        Assert.True(
            DistanceTo(line!, 200, 190.5) < 0.2,
            $"smoothing {smoothing} moved the edge off y = 190.5");
    }

    // ---- the fit itself ---------------------------------------------------------

    /// <summary>
    /// Total least squares, so a vertical line is representable. An
    /// ordinary y-on-x regression cannot express one at all, and a caliper
    /// line finder aimed at a vertical edge is half of all uses.
    /// </summary>
    [Fact]
    public void The_fit_handles_a_vertical_line()
    {
        var points = new[]
        {
            new Point2(100, 10), new Point2(100, 40), new Point2(100, 90), new Point2(100, 150),
        };

        var fit = LineFit.Fit(points);

        Assert.True(fit.Success);
        Assert.True(fit.Error < 1e-9, $"error {fit.Error}");
        Assert.True(Math.Abs(Math.Abs(fit.DirectionY) - 1) < 1e-9, "the direction is not vertical");
    }

    /// <summary>Degrees folded into -90..90, since a line has no head or tail.</summary>
    private static double Normalise(double degrees)
    {
        var value = degrees % 180;

        if (value > 90)
            value -= 180;

        if (value < -90)
            value += 180;

        return value;
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
