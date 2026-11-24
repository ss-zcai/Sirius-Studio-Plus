using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Finding a rectangular outline with calipers across its four sides (F30).
/// </summary>
/// <remarks>
/// The test parts are rendered analytically, as Find Line's edges are: a
/// drawn edge sits where the rasteriser put it, and measuring sub-pixel
/// accuracy against that concludes the wrong thing.
/// </remarks>
public class FindRectangleTests
{
    public FindRectangleTests() => TestCatalog.Ensure();

    private const int Width = 400;
    private const int Height = 300;

    /// <summary>
    /// A light rectangle on a dark field - centre, size, turned clockwise by
    /// <paramref name="angle"/> - its edges ramped across one pixel so the
    /// half-way crossing is exactly on the outline.
    /// </summary>
    private static ImageData Part(double cx, double cy, double w, double h, double angle, byte dark = 30, byte light = 220)
    {
        var radians = angle * Math.PI / 180;
        var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
        var data = new byte[Width * Height];

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                // Pixel centres on whole numbers, as the calipers count them.
                var (dx, dy) = (x - cx, y - cy);
                var u = (dx * cos) + (dy * sin);
                var v = (-dx * sin) + (dy * cos);

                var inside = Math.Min((w / 2) - Math.Abs(u), (h / 2) - Math.Abs(v));
                var coverage = Math.Clamp(0.5 + inside, 0, 1);

                data[(y * Width) + x] = (byte)Math.Round(dark + ((light - dark) * coverage));
            }
        }

        var mat = new Mat(Height, Width, DepthType.Cv8U, 1);
        mat.SetTo(data);
        return ImageData.Adopt(mat);
    }

    private sealed record Found(
        Shape? Rectangle, IReadOnlyList<Shape> Corners, int Points, double Length, double Breadth, double Angle,
        NodeStatus Status, string? Message);

    private static Found Run(ImageData image, Action<FindRectangleNode> configure)
    {
        var graph = new VisionGraph();
        var source = new PreparedImageNode(image);
        var finder = new FindRectangleNode();
        configure(finder);

        graph.Add(source);
        graph.Add(finder);
        graph.TryConnect(source.ImageOut, finder.ImageIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var rectangle = (scheduler.GetOutput(finder.RectangleOut) as ShapeSet)?.Shapes.FirstOrDefault();
        var corners = (scheduler.GetOutput(finder.CornersOut) as ShapeSet)?.Shapes ?? [];
        var points = (scheduler.GetOutput(finder.PointsOut) as ShapeSet)?.Count ?? 0;

        return new Found(
            rectangle,
            corners,
            points,
            scheduler.GetOutput(finder.LengthOut) as double? ?? 0,
            scheduler.GetOutput(finder.WidthOut) as double? ?? 0,
            scheduler.GetOutput(finder.AngleOut) as double? ?? 0,
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

        protected override void OnEvaluate(EvalContext context) => context.Forward(ImageOut, _image);
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

    // ---- the node ---------------------------------------------------------------

    /// <summary>
    /// The region drawn roughly - off centre, too small one way and too big
    /// the other - finds the part as it is.
    /// </summary>
    [Fact]
    public void It_finds_a_level_rectangle_from_a_rough_region()
    {
        using var image = Part(200, 150, 180, 100, 0);

        var found = Run(image, node =>
        {
            node.Region = new OrientedRect(206, 146, 170, 112, 0);
            node.SearchWidth = 20;
        });

        Assert.Equal(NodeStatus.Ready, found.Status);
        var rectangle = Assert.IsType<OrientedRectShape>(found.Rectangle);

        Assert.Equal(200, rectangle.Centre.X, 0.2);
        Assert.Equal(150, rectangle.Centre.Y, 0.2);
        Assert.Equal(180, found.Length, 0.2);
        Assert.Equal(100, found.Breadth, 0.2);
        Assert.Equal(0, found.Angle, 0.1);
        // Twelve calipers a side; clipping may drop the odd one on a clean edge.
        Assert.InRange(found.Points, 44, 48);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(-25)]
    public void It_finds_a_turned_rectangle_at_its_angle(double angle)
    {
        using var image = Part(200, 150, 200, 110, angle);

        var found = Run(image, node =>
        {
            // Drawn a few degrees off: the bands are wide enough to hold it.
            node.Region = new OrientedRect(198, 152, 196, 114, angle - 3);
            node.SearchWidth = 18;
        });

        Assert.Equal(NodeStatus.Ready, found.Status);
        Assert.Equal(angle, found.Angle, 0.1);
        Assert.Equal(200, found.Length, 0.3);
        Assert.Equal(110, found.Breadth, 0.3);
    }

    /// <summary>
    /// A part a little out of square: Square holds length and width equal,
    /// between the two; without it, each is as it is.
    /// </summary>
    [Fact]
    public void Square_holds_length_and_width_equal()
    {
        using var image = Part(200, 150, 104, 100, 0);

        void Region(FindRectangleNode node)
        {
            node.Region = new OrientedRect(200, 150, 100, 100, 0);
            node.SearchWidth = 15;
        }

        var free = Run(image, Region);
        var square = Run(image, node =>
        {
            Region(node);
            node.Square = true;
        });

        Assert.Equal(104, free.Length, 0.2);
        Assert.Equal(100, free.Breadth, 0.2);

        Assert.Equal(square.Length, square.Breadth, 6);
        Assert.InRange(square.Length, 101, 103);
    }

    /// <summary>
    /// A part seen at an angle is a quadrilateral: Four Lines gives its four
    /// corners where its sides meet, and their angles, rather than a
    /// rectangle forced onto it.
    /// </summary>
    [Fact]
    public void Four_lines_follows_a_part_that_is_not_square()
    {
        // A trapezium: the top narrower than the bottom.
        var data = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var (px, py) = ((double)x, (double)y);
                var t = (py - 100) / 100;
                var half = 80 + (t * 20);
                var inside = Math.Min(Math.Min(py - 100, 200 - py), half - Math.Abs(px - 200));

                // The slanted sides' distance, not their horizontal gap.
                if (half - Math.Abs(px - 200) < Math.Min(py - 100, 200 - py))
                    inside = (half - Math.Abs(px - 200)) * Math.Cos(Math.Atan(0.2));

                data[(y * Width) + x] = (byte)Math.Round(30 + (190 * Math.Clamp(0.5 + inside, 0, 1)));
            }
        }

        var mat = new Mat(Height, Width, DepthType.Cv8U, 1);
        mat.SetTo(data);
        using var image = ImageData.Adopt(mat);

        var found = Run(image, node =>
        {
            node.Region = new OrientedRect(200, 150, 180, 100, 0);
            node.SearchWidth = 20;
            node.Fit = RectangleFitMode.FourLines;
        });

        Assert.Equal(NodeStatus.Ready, found.Status);
        var outline = Assert.IsType<PolygonShape>(found.Rectangle);

        Assert.Equal(120, outline.Points[0].X, 0.5);
        Assert.Equal(100, outline.Points[0].Y, 0.5);
        Assert.Equal(300, outline.Points[2].X, 0.5);
        Assert.Equal(200, outline.Points[2].Y, 0.5);

        // The top corners are wider than square, the bottom ones narrower.
        Assert.True(found.Corners[0].Metric(ShapeMetric.Angle) > 95);
        Assert.True(found.Corners[3].Metric(ShapeMetric.Angle) < 85);
    }

    [Fact]
    public void A_side_with_no_edge_is_named()
    {
        // The part is narrower than the region by far more than the band:
        // the left and right sides find nothing.
        using var image = Part(200, 150, 100, 100, 0);

        var found = Run(image, node =>
        {
            node.Region = new OrientedRect(200, 150, 260, 100, 0);
            node.SearchWidth = 15;
        });

        Assert.Equal(NodeStatus.Warning, found.Status);
        Assert.Contains("right and left sides", found.Message);
        Assert.Null(found.Rectangle);
    }

    // ---- the bands -----------------------------------------------------------------

    [Fact]
    public void Each_sides_band_is_on_that_side_turned_to_cross_it_from_outside()
    {
        var region = new OrientedRect(200, 150, 180, 100, 0);

        var top = FindRectangleNode.Band(region, RectangleSide.Top, 10);
        Assert.Equal((200, 100, 160, 20, 0), (top.CentreX, top.CentreY, top.Width, top.Height, top.Angle));

        var right = FindRectangleNode.Band(region, RectangleSide.Right, 10);
        Assert.Equal((290, 150, 80, 20, 90), (right.CentreX, right.CentreY, right.Width, right.Height, right.Angle));

        var left = FindRectangleNode.Band(region, RectangleSide.Left, 10);
        Assert.Equal(110, left.CentreX, 9);
        Assert.Equal(270, left.Angle);
    }

    // ---- the band drawn round the region -------------------------------------------------

    /// <summary>
    /// The preview of the node's input knows the band round its region,
    /// and hears when Search Width changes, so the band drawn round the
    /// region while it is edited follows the slider.
    /// </summary>
    [Fact]
    public void The_region_editor_is_given_the_band_and_follows_it()
    {
        var finder = new FindRectangleNode { SearchWidth = 25 };
        var region = new Sirius.UI.ViewModels.ParameterViewModel(
            finder, finder.Descriptor.Parameters.Single(p => p.Name == nameof(FindRectangleNode.Region)));

        using var panel = new Sirius.UI.ViewModels.PreviewPanelViewModel(
            finder, finder.ImageIn, null, new Sirius.UI.ViewModels.ShapeSelectionService())
        {
            RegionParameter = region,
        };

        Assert.Equal(25, panel.RoiBand);

        var heard = new List<string?>();
        panel.PropertyChanged += (_, e) => heard.Add(e.PropertyName);

        finder.SearchWidth = 40;

        Assert.Contains(nameof(panel.RoiBand), heard);
        Assert.Equal(40, panel.RoiBand);

        // Let go of, it no longer listens - and says there is no band.
        panel.RegionParameter = null;
        heard.Clear();
        finder.SearchWidth = 10;

        Assert.DoesNotContain(nameof(panel.RoiBand), heard);
        Assert.Equal(0, panel.RoiBand);
    }

    /// <summary>
    /// A region calipers search is drawn sky blue, as Find Circle's band
    /// and Find Curve's path are; a region simply worked on stays amber.
    /// </summary>
    [Theory]
    [InlineData(typeof(FindRectangleNode), true)]
    [InlineData(typeof(FindLineNode), true)]
    [InlineData(typeof(LineCalipersNode), true)]
    [InlineData(typeof(LineDefectsNode), true)]
    [InlineData(typeof(RegionOfInterestNode), false)]
    [InlineData(typeof(RegionStatsNode), false)]
    public void Search_regions_are_drawn_in_the_search_blue(Type nodeType, bool searches)
    {
        var node = (VisionNode)Activator.CreateInstance(nodeType)!;
        var region = node.Descriptor.Parameters.Single(p => p.Attribute.Editor == ParamEditor.Rect);

        Assert.Equal(searches ? ParamAttribute.SearchColour : null, region.Attribute.RegionColour);
    }

    [Fact]
    public void A_region_with_no_band_has_none()
    {
        var finder = new FindLineNode();
        var region = new Sirius.UI.ViewModels.ParameterViewModel(
            finder, finder.Descriptor.Parameters.Single(p => p.Name == nameof(FindLineNode.Region)));

        using var panel = new Sirius.UI.ViewModels.PreviewPanelViewModel(
            finder, finder.ImageIn, null, new Sirius.UI.ViewModels.ShapeSelectionService())
        {
            RegionParameter = region,
        };

        Assert.Equal(0, panel.RoiBand);
    }

    // ---- the fit -----------------------------------------------------------------------

    private static IReadOnlyList<IReadOnlyList<Point2>> SidesOf(double cx, double cy, double w, double h, double angle, int each = 6)
    {
        var radians = angle * Math.PI / 180;
        var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));

        Point2 At(double u, double v) => new(cx + (u * cos) - (v * sin), cy + (u * sin) + (v * cos));

        IReadOnlyList<Point2> Along(Func<double, Point2> point) =>
            Enumerable.Range(0, each).Select(i => point(-0.4 + (0.8 * i / (each - 1)))).ToArray();

        return
        [
            Along(t => At(t * w, -h / 2)),
            Along(t => At(w / 2, t * h)),
            Along(t => At(-t * w, h / 2)),
            Along(t => At(-w / 2, -t * h)),
        ];
    }

    [Fact]
    public void The_rectangle_fit_recovers_an_exact_rectangle()
    {
        var fit = RectangleFit.Fit(SidesOf(120, 80, 60, 30, 20), 18);

        Assert.True(fit.Success);
        Assert.Equal(120, fit.Centre.X, 9);
        Assert.Equal(80, fit.Centre.Y, 9);
        Assert.Equal(60, fit.Width, 9);
        Assert.Equal(30, fit.Height, 9);
        Assert.Equal(20, fit.AngleDegrees, 9);
        Assert.Equal(0, fit.Error, 9);
    }

    [Fact]
    public void The_rectangle_fit_drops_a_stray_point()
    {
        var sides = SidesOf(120, 80, 60, 30, 0, each: 10).Select(side => side.ToList()).ToArray();

        // A caliper that took a scratch 6 pixels inside the top.
        sides[0][4] = sides[0][4] with { Y = sides[0][4].Y + 6 };

        var fit = RectangleFit.FitRobust(sides, 0);

        Assert.Equal(9, fit.Used[0].Count);
        Assert.Equal(30, fit.Height, 6);
    }

    [Fact]
    public void Square_moves_the_sides_with_fewer_points_further()
    {
        // 62 wide, 58 high; the left and right sides have half the points.
        var sides = SidesOf(100, 100, 62, 58, 0, each: 8).ToArray();
        sides[1] = sides[1].Take(4).ToArray();
        sides[3] = sides[3].Take(4).ToArray();

        var fit = RectangleFit.Fit(sides, 0, square: true);

        Assert.Equal(fit.Width, fit.Height, 9);

        // Pulled nearer the better supported top and bottom's 58 than 62.
        Assert.True(fit.Width < 60, $"width {fit.Width}");
    }
}
