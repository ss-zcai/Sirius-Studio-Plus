using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Shapes;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Finding a circular edge with radial calipers.
/// </summary>
/// <remarks>
/// <para>
/// Tested against drawn circles, because the question a circle finder has
/// to answer is "how close to the truth", and only a synthetic image has a
/// known truth. The tolerances here are the specification: a caliper that
/// is merely in the right neighbourhood is a thresholder with extra steps,
/// and the whole reason to scan and interpolate is to beat the pixel grid.
/// </para>
/// <para>
/// The drawn circles are anti-aliased, which matters - a hard-edged circle
/// would let a sloppy implementation look exact, since the edge would fall
/// on a pixel boundary every time.
/// </para>
/// </remarks>
public class FindCircleTests
{
    public FindCircleTests() => TestCatalog.Ensure();

    private const int Size = 400;

    /// <summary>
    /// A light disc on a dark field, rendered analytically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not <c>CvInvoke.Circle</c>, and the difference is the whole point of
    /// these tests. A filled circle of radius R rasterises to cover pixels
    /// out to roughly R + 0.5, so the intensity edge in the drawn image is
    /// half a pixel outside the radius that was asked for. Measuring
    /// against that radius makes a correct finder look like it has a
    /// half-pixel bias - which is exactly what the first version of these
    /// tests concluded, wrongly.
    /// </para>
    /// <para>
    /// Here the intensity ramps linearly across one pixel at the boundary,
    /// so the 50% crossing - which is what an edge detector finds - is at
    /// the stated radius by construction, and a sub-pixel claim can be
    /// held to it.
    /// </para>
    /// </remarks>
    private static ImageData Disc(
        double centreX, double centreY, double radius, byte dark = 30, byte light = 220)
    {
        var mat = new Mat(Size, Size, DepthType.Cv8U, 1);
        var data = new byte[Size * Size];

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var distance = Math.Sqrt(
                    ((x - centreX) * (x - centreX)) + ((y - centreY) * (y - centreY)));

                var coverage = Math.Clamp(0.5 - (distance - radius), 0, 1);
                data[(y * Size) + x] = (byte)Math.Round(dark + ((light - dark) * coverage));
            }
        }

        mat.SetTo(data);
        return ImageData.Adopt(mat);
    }

    private static (CircleShape? Circle, int Points, NodeStatus Status, string? Message) Run(
        ImageData image, Action<FindCircleNode> configure)
    {
        var graph = new VisionGraph();
        var source = new PreparedImageNode(image);
        var finder = new FindCircleNode();
        configure(finder);

        graph.Add(source);
        graph.Add(finder);
        graph.TryConnect(source.ImageOut, finder.ImageIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var circles = scheduler.GetOutput(finder.CircleOut) as ShapeSet;
        var points = scheduler.GetOutput(finder.PointsOut) as ShapeSet;

        return (
            circles?.Shapes.OfType<CircleShape>().FirstOrDefault(),
            points?.Count ?? 0,
            finder.Status,
            finder.StatusMessage);
    }

    /// <summary>Feeds a ready-made image into a graph.</summary>
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

    // ---- does it find the circle at all? ------------------------------------

    [Fact]
    public void It_finds_a_drawn_circle()
    {
        using var image = Disc(200, 200, 100);

        var (circle, points, status, message) = Run(image, node =>
        {
            node.CentreX = 200;
            node.CentreY = 200;
            node.InnerRadius = 80;
            node.OuterRadius = 120;
        });

        Assert.Equal(NodeStatus.Ready, status);
        Assert.NotNull(circle);
        Assert.True(points >= 40, $"only {points} edge points: {message}");
    }

    /// <summary>
    /// Within a fifth of a pixel on centre and radius. That is the claim
    /// sub-pixel interpolation is making; without it the best possible is
    /// half a pixel and the typical is worse.
    /// </summary>
    [Theory]
    [InlineData(200, 200, 100)]
    [InlineData(180.5, 210.5, 75)]
    [InlineData(205, 195, 130)]
    public void It_is_accurate_to_a_fifth_of_a_pixel(double x, double y, double radius)
    {
        using var image = Disc(x, y, radius);

        var (circle, _, _, message) = Run(image, node =>
        {
            node.CentreX = x + 3;          // deliberately off: the fit should recover
            node.CentreY = y - 3;
            node.InnerRadius = radius - 20;
            node.OuterRadius = radius + 20;
        });

        Assert.NotNull(circle);
        Assert.True(Math.Abs(circle!.Centre.X - x) < 0.2, $"centre X off by {circle.Centre.X - x:0.###}: {message}");
        Assert.True(Math.Abs(circle.Centre.Y - y) < 0.2, $"centre Y off by {circle.Centre.Y - y:0.###}");
        Assert.True(Math.Abs(circle.Radius - radius) < 0.2, $"radius off by {circle.Radius - radius:0.###}");
    }

    /// <summary>
    /// The ring is a prior, not a prison: a centre given a few pixels out
    /// still finds the edge, because every caliper only has to cross it.
    /// </summary>
    [Fact]
    public void A_roughly_placed_ring_still_finds_it()
    {
        using var image = Disc(200, 200, 100);

        var (circle, _, _, _) = Run(image, node =>
        {
            node.CentreX = 208;
            node.CentreY = 193;
            node.InnerRadius = 70;
            node.OuterRadius = 130;
        });

        Assert.NotNull(circle);
        Assert.True(Math.Abs(circle!.Radius - 100) < 0.5, $"radius {circle.Radius:0.##}");
    }

    // ---- polarity and direction ---------------------------------------------

    /// <summary>
    /// Scanning outward across a light disc crosses light to dark, so
    /// asking for dark to light finds nothing - which is the point of
    /// having the setting.
    /// </summary>
    [Fact]
    public void Polarity_excludes_the_wrong_edge()
    {
        using var image = Disc(200, 200, 100);

        var (circle, points, status, _) = Run(image, node =>
        {
            node.CentreX = 200;
            node.CentreY = 200;
            node.InnerRadius = 80;
            node.OuterRadius = 120;
            node.Direction = RadialDirection.Outward;
            node.Polarity = EdgePolarity.DarkToLight;
        });

        Assert.Null(circle);
        Assert.Equal(0, points);
        Assert.Equal(NodeStatus.Warning, status);
    }

    [Fact]
    public void Polarity_accepts_the_right_edge()
    {
        using var image = Disc(200, 200, 100);

        var (circle, _, _, _) = Run(image, node =>
        {
            node.CentreX = 200;
            node.CentreY = 200;
            node.InnerRadius = 80;
            node.OuterRadius = 120;
            node.Direction = RadialDirection.Outward;
            node.Polarity = EdgePolarity.LightToDark;
        });

        Assert.NotNull(circle);
        Assert.True(Math.Abs(circle!.Radius - 100) < 0.2);
    }

    /// <summary>
    /// Scanning inward reverses what the polarity means, so the same edge
    /// is now dark to light. The setting is relative to the scan, not to
    /// the image.
    /// </summary>
    [Fact]
    public void Direction_reverses_what_polarity_means()
    {
        using var image = Disc(200, 200, 100);

        var (circle, _, _, _) = Run(image, node =>
        {
            node.CentreX = 200;
            node.CentreY = 200;
            node.InnerRadius = 80;
            node.OuterRadius = 120;
            node.Direction = RadialDirection.Inward;
            node.Polarity = EdgePolarity.DarkToLight;
        });

        Assert.NotNull(circle);
        Assert.True(Math.Abs(circle!.Radius - 100) < 0.2);
    }

    // ---- an arc, not the whole ring -----------------------------------------

    [Fact]
    public void It_can_search_an_arc()
    {
        using var image = Disc(200, 200, 100);

        var (circle, points, _, _) = Run(image, node =>
        {
            node.CentreX = 200;
            node.CentreY = 200;
            node.InnerRadius = 80;
            node.OuterRadius = 120;
            node.StartAngle = 0;
            node.SweepAngle = 90;
            node.CaliperCount = 20;
        });

        Assert.NotNull(circle);
        Assert.True(points <= 20);

        // A quarter arc still pins a circle, though less tightly than a
        // full ring - the algebraic fit biases small on a short arc.
        Assert.True(Math.Abs(circle!.Radius - 100) < 1.0, $"radius {circle.Radius:0.##}");
    }

    // ---- robustness ----------------------------------------------------------

    /// <summary>
    /// A bright scratch across the band sends one caliper to the wrong
    /// radius. Rejection should drop it; without rejection it drags the
    /// fit, which is what makes this worth testing both ways.
    /// </summary>
    [Fact]
    public void An_outlier_is_rejected()
    {
        using var mat = Disc(200, 200, 100);
        using var scarred = mat.Mat.Clone();

        // A blob out at radius ~115, on one side only.
        CvInvoke.Circle(
            scarred, new System.Drawing.Point(315, 200), 6, new MCvScalar(255), -1, LineType.AntiAlias);

        using var image = ImageData.Adopt(scarred.Clone());

        var with = Run(image, node =>
        {
            node.CentreX = 200;
            node.CentreY = 200;
            node.InnerRadius = 80;
            node.OuterRadius = 125;
            node.RejectSigma = 2.0;
        });

        Assert.NotNull(with.Circle);
        Assert.True(
            Math.Abs(with.Circle!.Radius - 100) < 0.5,
            $"rejection left the radius at {with.Circle.Radius:0.##}");
    }

    /// <summary>
    /// A band that does not contain the edge finds nothing, and says so
    /// rather than fitting a circle through whatever noise it found.
    /// </summary>
    [Fact]
    public void A_band_that_misses_the_edge_reports_it()
    {
        using var image = Disc(200, 200, 100);

        var (circle, points, status, message) = Run(image, node =>
        {
            node.CentreX = 200;
            node.CentreY = 200;
            node.InnerRadius = 20;
            node.OuterRadius = 60;       // entirely inside the disc
            node.MinimumContrast = 5;
        });

        Assert.Null(circle);
        Assert.Equal(0, points);
        Assert.Equal(NodeStatus.Warning, status);
        Assert.Contains("No edge", message!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_inverted_band_is_an_error()
    {
        using var image = Disc(200, 200, 100);

        var (_, _, status, message) = Run(image, node =>
        {
            node.InnerRadius = 120;
            node.OuterRadius = 80;
        });

        Assert.Equal(NodeStatus.Error, status);
        Assert.Contains("outer radius", message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Min Contrast is what keeps a caliper from reporting noise as an
    /// edge. Set high enough, a real but faint edge is ignored.
    /// </summary>
    [Fact]
    public void Minimum_contrast_rejects_a_faint_edge()
    {
        using var image = Disc(200, 200, 100, dark: 100, light: 104);

        var (circle, _, status, _) = Run(image, node =>
        {
            node.CentreX = 200;
            node.CentreY = 200;
            node.InnerRadius = 80;
            node.OuterRadius = 120;
            node.MinimumContrast = 20;
        });

        Assert.Null(circle);
        Assert.Equal(NodeStatus.Warning, status);
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
