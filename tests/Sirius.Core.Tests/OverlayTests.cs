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
/// Shapes carried through a node that makes a new image from an old one:
/// Crop, Resize, Region of Interest, Polar to Linear.
/// </summary>
public class OverlayTests
{
    public OverlayTests() => TestCatalog.Ensure();

    [Fact]
    public void A_crop_moves_shapes_by_its_corner()
    {
        var crop = new CropNode { Roi = new System.Drawing.Rectangle(100, 100, 200, 200) };
        var circle = new CircleShape(new Point2(150, 120), 30)
        {
            Metrics = new Dictionary<string, double> { [ShapeMetric.Radius] = 30, [ShapeMetric.CentroidX] = 150 },
        };

        var carried = Run(Blank, crop, crop.ImageIn, crop.OverlayIn, crop.OverlayOut, circle);

        Assert.Equal((200, 200), (carried.FrameWidth, carried.FrameHeight));
        var moved = Assert.IsType<CircleShape>(Assert.Single(carried.Shapes));
        Assert.Equal(new Point2(50, 20), moved.Centre);
        Assert.Equal(30, moved.Radius);
        Assert.Equal(30, moved.Metric(ShapeMetric.Radius));
        Assert.Equal(50, moved.Metric(ShapeMetric.CentroidX));
    }

    [Fact]
    public void An_even_resize_scales_a_circle_and_its_area()
    {
        var resize = new ResizeNode { ScaleX = 0.5, ScaleY = 0.5 };
        var circle = new CircleShape(new Point2(100.5, 100.5), 40)
        {
            Metrics = new Dictionary<string, double> { [ShapeMetric.Area] = 5000, [ShapeMetric.Circularity] = 0.98 },
        };

        var carried = Run(Blank, resize, resize.ImageIn, resize.OverlayIn, resize.OverlayOut, circle);

        Assert.Equal((200, 200), (carried.FrameWidth, carried.FrameHeight));
        var scaled = Assert.IsType<CircleShape>(Assert.Single(carried.Shapes));
        Assert.Equal(new Point2(50, 50), scaled.Centre);
        Assert.Equal(20, scaled.Radius);
        Assert.Equal(1250, scaled.Metric(ShapeMetric.Area));
        Assert.Equal(0.98, scaled.Metric(ShapeMetric.Circularity));
    }

    [Fact]
    public void An_uneven_resize_makes_a_circle_an_ellipse_and_drops_lengths_it_cannot_convert()
    {
        var resize = new ResizeNode { ScaleX = 2, ScaleY = 1 };
        var circle = new CircleShape(new Point2(100, 100), 40)
        {
            Metrics = new Dictionary<string, double> { [ShapeMetric.Radius] = 40 },
        };

        var carried = Run(Blank, resize, resize.ImageIn, resize.OverlayIn, resize.OverlayOut, circle);

        var ellipse = Assert.IsType<EllipseShape>(Assert.Single(carried.Shapes));
        Assert.Equal(160, ellipse.MajorAxis, 6);
        Assert.Equal(80, ellipse.MinorAxis, 6);
        Assert.Equal(0, ellipse.Angle);
        Assert.Null(ellipse.Metric(ShapeMetric.Radius));
    }

    /// <summary>
    /// Checked against the pixels: a dot drawn in the source, a turned
    /// region straightened, and the carried point where the dot landed.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(-55)]
    public void A_straightened_region_carries_a_point_to_where_its_pixel_went(double angle)
    {
        var dot = new Point2(232, 215);
        var roi = new RegionOfInterestNode { Rect = new OrientedRect(210, 200, 120, 80, angle) };

        Mat Picture()
        {
            var mat = Blank();
            CvInvoke.Circle(mat, new System.Drawing.Point((int)dot.X, (int)dot.Y), 2, new MCvScalar(255), -1);
            return mat;
        }

        var (carried, image) = RunWithImage(Picture, roi, roi.ImageIn, roi.OverlayIn, roi.OverlayOut, roi.ImageOut, new PointShape(dot));

        var point = Assert.IsType<PointShape>(Assert.Single(carried.Shapes));

        // Where the dot landed: the centre of its bright pixels.
        var moments = CvInvoke.Moments(image.Mat);
        var landed = new Point2(moments.M10 / moments.M00, moments.M01 / moments.M00);

        Assert.InRange(point.At.X, landed.X - 1, landed.X + 1);
        Assert.InRange(point.At.Y, landed.Y - 1, landed.Y + 1);
    }

    [Fact]
    public void Unwrapping_places_a_point_by_its_angle_and_radius_and_drops_one_outside_the_band()
    {
        var polar = new PolarToLinearNode { Band = new Ring(200, 200, 50, 150) };

        // At angle 90 (straight down, y growing downward) and radius 100:
        // a quarter of the way along a full ring, half way down the band.
        var carried = Run(
            Blank,
            polar,
            polar.ImageIn,
            polar.OverlayIn,
            polar.OverlayOut,
            new PointShape(new Point2(200, 300)),
            new PointShape(new Point2(200, 380)));

        var point = Assert.IsType<PointShape>(Assert.Single(carried.Shapes));
        Assert.InRange(point.At.X / carried.FrameWidth, 0.24, 0.26);
        Assert.InRange(point.At.Y / carried.FrameHeight, 0.49, 0.51);
    }

    /// <summary>
    /// A circle round the band's centre unwraps to a straight line across
    /// the strip - cut at the seam rather than joined back across it.
    /// </summary>
    [Fact]
    public void A_circle_round_the_centre_unwraps_to_a_straight_line()
    {
        var polar = new PolarToLinearNode { Band = new Ring(200, 200, 50, 150) };

        var carried = Run(
            Blank, polar, polar.ImageIn, polar.OverlayIn, polar.OverlayOut, new CircleShape(new Point2(200, 200), 100));

        var row = carried.FrameHeight / 2.0 - 0.5;

        foreach (var piece in carried.Shapes.Cast<PolygonShape>())
        {
            Assert.False(piece.Closed);
            Assert.All(piece.Points, p => Assert.InRange(p.Y, row - 1, row + 1));

            for (var i = 1; i < piece.Points.Count; i++)
                Assert.True(Math.Abs(piece.Points[i].X - piece.Points[i - 1].X) < carried.FrameWidth / 2.0);
        }

        var span = carried.Shapes.Cast<PolygonShape>().Sum(p => p.Points.Max(q => q.X) - p.Points.Min(q => q.X));
        Assert.InRange(span, carried.FrameWidth - 10, carried.FrameWidth);
    }

    [Fact]
    public void Shapes_from_another_image_size_are_carried_with_a_warning()
    {
        var crop = new CropNode { Roi = new System.Drawing.Rectangle(0, 0, 100, 100) };
        var graph = new VisionGraph();
        var picture = new PictureNode(Blank);
        var shapes = new ShapesNode(new ShapeSet([new PointShape(new Point2(10, 10))], 640, 480));

        graph.Add(picture);
        graph.Add(shapes);
        graph.Add(crop);
        graph.TryConnect(picture.Out, crop.ImageIn);
        graph.TryConnect(shapes.Out, crop.OverlayIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Warning, crop.Status);
        Assert.Single(scheduler.GetOutput<ShapeSet>(crop.OverlayOut)!.Shapes);
    }

    // ---- plumbing ----------------------------------------------------------------------

    private static Mat Blank()
    {
        var mat = new Mat(400, 400, DepthType.Cv8U, 1);
        mat.SetTo(new MCvScalar(0));
        return mat;
    }

    private static ShapeSet Run(
        Func<Mat> picture, VisionNode node, InputPort imageIn, InputPort overlayIn, OutputPort overlayOut,
        params Shape[] shapes) =>
        RunWithImage(picture, node, imageIn, overlayIn, overlayOut, null, shapes).Overlay;

    private static (ShapeSet Overlay, ImageData Image) RunWithImage(
        Func<Mat> picture, VisionNode node, InputPort imageIn, InputPort overlayIn, OutputPort overlayOut,
        OutputPort? imageOut, params Shape[] shapes)
    {
        var graph = new VisionGraph();
        var source = new PictureNode(picture);
        var found = new ShapesNode(new ShapeSet(shapes, 400, 400));

        graph.Add(source);
        graph.Add(found);
        graph.Add(node);
        graph.TryConnect(source.Out, imageIn);
        graph.TryConnect(found.Out, overlayIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.NotEqual(NodeStatus.Error, node.Status);

        var image = imageOut is null ? null! : scheduler.GetOutput<ImageData>(imageOut)!;

        // Cloned, since the scheduler's cache is disposed with it.
        return (scheduler.GetOutput<ShapeSet>(overlayOut)!, image is null ? null! : ImageData.Adopt(image.Mat.Clone()));
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
        private readonly ShapeSet _set;

        public ShapesNode(ShapeSet set)
        {
            _set = set;
            Out = AddOutput<ShapeSet>("Out");
        }

        public OutputPort Out { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(Out, _set);
    }
}
