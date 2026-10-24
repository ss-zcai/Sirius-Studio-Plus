using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Cutting a region out, and straightening a tilted one.
/// </summary>
/// <remarks>
/// The angle convention is settled here rather than by reasoning about it.
/// Rotation sign errors are the classic fault in this kind of code and they
/// produce a picture that is wrong by exactly twice the angle - which looks
/// like a tilted crop, not like a bug.
/// </remarks>
public class RegionOfInterestTests
{
    public RegionOfInterestTests() => TestCatalog.Ensure();

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

    private sealed class CanvasNode : VisionNode
    {
        public CanvasNode(int width, int height, Action<Mat> draw)
        {
            Width = width;
            Height = height;
            Draw = draw;
            ImageOut = AddOutput<ImageData>("Out");
        }

        public OutputPort ImageOut { get; }

        private int Width { get; }

        private int Height { get; }

        private Action<Mat> Draw { get; }

        protected override void OnEvaluate(EvalContext context)
        {
            var mat = new Mat(Height, Width, DepthType.Cv8U, 1);
            mat.SetTo(new MCvScalar(0));
            Draw(mat);
            context.Set(ImageOut, ImageData.Adopt(mat));
        }
    }

    /// <summary>A white bar through the image centre, at the given angle.</summary>
    private static Action<Mat> TiltedBar(double angleDegrees, int length = 200, int thickness = 60) =>
        mat =>
        {
            var rect = new RotatedRect(
                new PointF(mat.Cols / 2f, mat.Rows / 2f),
                new SizeF(length, thickness),
                (float)angleDegrees);

            var points = Array.ConvertAll(rect.GetVertices(), p => new Point((int)p.X, (int)p.Y));

            using var vector = new VectorOfPoint(points);
            using var all = new VectorOfVectorOfPoint();
            all.Push(vector);

            CvInvoke.FillPoly(mat, all, new MCvScalar(255));
        };

    private static async Task<(ImageData Image, ShapeSet Shapes)> RunAsync(
        Action<Mat> draw,
        Action<RegionOfInterestNode> configure,
        int width = 400,
        int height = 400)
    {
        var graph = new VisionGraph();
        var canvas = new CanvasNode(width, height, draw);
        var roi = new RegionOfInterestNode();
        configure(roi);

        graph.Add(canvas);
        graph.Add(roi);
        graph.TryConnect(canvas.ImageOut, roi.ImageIn);

        var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        var image = scheduler.GetOutput<ImageData>(roi.ImageOut)!;
        var shapes = scheduler.GetOutput<ShapeSet>(roi.ShapesOut)!;

        // The caller reads pixels, so the scheduler must outlive this method;
        // copying the result frees it from the cache's lifetime.
        var copy = ImageData.CopyOf(image.Mat);
        scheduler.Dispose();

        return (copy, shapes);
    }

    /// <summary>Proportion of the image that is bright.</summary>
    private static double WhiteFraction(ImageData image)
    {
        using var binary = new Mat();
        CvInvoke.Threshold(image.Mat, binary, 128, 255, ThresholdType.Binary);

        return CvInvoke.CountNonZero(binary) / (double)(image.Width * image.Height);
    }

    // ---- straightening ---------------------------------------------------

    /// <summary>
    /// The whole point: a region laid along a tilted feature comes out
    /// upright and full. Get the rotation sign wrong and the bar leaves the
    /// frame, so the test is a strong one despite being a single number.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(45)]
    [InlineData(-30)]
    [InlineData(75)]
    public async Task A_tilted_region_comes_out_upright(double angle)
    {
        var cut = await RunAsync(
            TiltedBar(angle),
            node =>
            {
                node.Rect = new OrientedRect(200, 200, 200, 60, angle);
                node.Alignment = RoiAlignment.Straighten;
            });

        using var image = cut.Image;

        Assert.Equal(200, image.Width);
        Assert.Equal(60, image.Height);

        // The region was laid exactly over the bar, so the cut-out should be
        // almost entirely bar.
        Assert.True(
            WhiteFraction(image) > 0.95,
            $"at {angle} degrees only {WhiteFraction(image):P0} of the cut-out was the bar");
    }

    /// <summary>
    /// The companion to the above: at the wrong angle the same region catches
    /// mostly background, so the test above cannot pass by accident.
    /// </summary>
    [Fact]
    public async Task A_region_at_the_wrong_angle_misses_the_feature()
    {
        var cut = await RunAsync(
            TiltedBar(45),
            node =>
            {
                node.Rect = new OrientedRect(200, 200, 200, 60, -45);
                node.Alignment = RoiAlignment.Straighten;
            });

        using var image = cut.Image;

        Assert.True(WhiteFraction(image) < 0.6, "a region 90 degrees out should not be full");
    }

    [Fact]
    public async Task Scale_enlarges_the_cut_out()
    {
        var cut = await RunAsync(
            TiltedBar(0),
            node =>
            {
                node.Rect = new OrientedRect(200, 200, 100, 50, 0);
                node.Scale = 2;
            });

        using var image = cut.Image;

        Assert.Equal(200, image.Width);
        Assert.Equal(100, image.Height);
    }

    [Fact]
    public async Task Pixels_outside_the_image_take_the_fill_value()
    {
        var cut = await RunAsync(
            _ => { },
            node =>
            {
                // Hanging off the top-left corner.
                node.Rect = new OrientedRect(0, 0, 100, 100, 0);
                node.Fill = 255;
            });

        using var image = cut.Image;

        // Three quarters of the region is off the image and so is fill.
        Assert.InRange(WhiteFraction(image), 0.7, 0.8);
    }

    // ---- the bounding-box mode -------------------------------------------

    /// <summary>
    /// HALCON's unaligned mode: the surrounding upright box, no interpolation.
    /// A 200x60 bar at 45 degrees needs about 184 pixels each way.
    /// </summary>
    [Fact]
    public async Task The_bounding_box_mode_cuts_the_surrounding_upright_box()
    {
        var cut = await RunAsync(
            TiltedBar(45),
            node =>
            {
                node.Rect = new OrientedRect(200, 200, 200, 60, 45);
                node.Alignment = RoiAlignment.BoundingBox;
            });

        using var image = cut.Image;

        Assert.InRange(image.Width, 180, 190);
        Assert.InRange(image.Height, 180, 190);

        // The bar runs corner to corner, so well under half the box is bright.
        Assert.InRange(WhiteFraction(image), 0.2, 0.5);
    }

    [Fact]
    public async Task The_bounding_box_mode_is_clipped_to_the_image()
    {
        var cut = await RunAsync(
            _ => { },
            node =>
            {
                node.Rect = new OrientedRect(20, 20, 200, 200, 0);
                node.Alignment = RoiAlignment.BoundingBox;
            });

        using var image = cut.Image;

        // Centred at 20,20 with a 200 box: only 120 x 120 is on the image.
        Assert.Equal(120, image.Width);
        Assert.Equal(120, image.Height);
    }

    // ---- the region output and the wire ----------------------------------

    [Fact]
    public async Task The_region_is_reported_for_drawing_on_the_source()
    {
        var cut = await RunAsync(
            TiltedBar(30),
            node =>
            {
                node.Rect = new OrientedRect(180, 220, 120, 40, 30);
            });

        var shapes = cut.Shapes;

        var rect = Assert.IsType<OrientedRectShape>(Assert.Single(shapes.Shapes));

        Assert.Equal(180, rect.Centre.X, 3);
        Assert.Equal(220, rect.Centre.Y, 3);
        Assert.Equal(30, rect.AngleDegrees, 3);
        Assert.True(shapes.MatchesFrame(400, 400));
    }

    /// <summary>
    /// Fixturing in miniature: the region follows a located feature instead of
    /// sitting where it was typed.
    /// </summary>
    [Fact]
    public async Task A_wired_region_follows_a_found_shape()
    {
        var graph = new VisionGraph();
        var canvas = new CanvasNode(400, 400, TiltedBar(30, length: 160, thickness: 40));
        var blobs = new BlobFilterNode { Aspect = new ValueRange(0, null) };
        var pick = new SelectShapeNode { Mode = ShapeSelection.Largest };
        var style = new ShapeStyleNode { Form = ShapeForm.MinimumRect };
        var roi = new RegionOfInterestNode { Alignment = RoiAlignment.Straighten };

        graph.Add(canvas);
        graph.Add(blobs);
        graph.Add(pick);
        graph.Add(style);
        graph.Add(roi);

        graph.TryConnect(canvas.ImageOut, blobs.ImageIn);
        graph.TryConnect(blobs.ShapesOut, pick.ShapesIn);
        graph.TryConnect(pick.ShapesOut, style.ShapesIn);
        graph.TryConnect(canvas.ImageOut, roi.ImageIn);

        var wired = graph.TryConnect(style.ShapesOut, roi.RegionIn);
        Assert.True(wired.Success, wired.Message);

        using var scheduler = CreateScheduler(graph);
        var result = await scheduler.RunAsync();
        Assert.True(result.Success);

        var cut = scheduler.GetOutput<ImageData>(roi.ImageOut)!;

        // The region came from the bar's own minimum-area rectangle, so the
        // cut-out is the bar, upright and nearly full - without anyone typing
        // a centre or an angle.
        Assert.InRange(cut.Width, 150, 175);
        Assert.InRange(cut.Height, 30, 50);
        Assert.True(
            WhiteFraction(cut) > 0.9,
            $"the fixtured cut was only {WhiteFraction(cut):P0} bar");
    }
}
