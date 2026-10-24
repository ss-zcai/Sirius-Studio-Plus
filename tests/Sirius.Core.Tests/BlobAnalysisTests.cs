using System.Drawing;
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
/// Blob detection, measurement, filtering and ordering.
/// </summary>
/// <remarks>
/// Driven against drawn shapes of known size, because every one of these
/// numbers is plausible when wrong: a circularity of 0.7 looks as reasonable
/// as 0.95 unless you know what was drawn.
/// </remarks>
public class BlobAnalysisTests
{
    public BlobAnalysisTests() => TestCatalog.Ensure();

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

    /// <summary>Feeds a drawn binary image straight into the blob finder.</summary>
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

    private static async Task<(ShapeSet Shapes, int Count, BlobFilterNode Node)> RunAsync(
        Action<Mat> draw,
        Action<BlobFilterNode>? configure = null,
        int width = 400,
        int height = 300)
    {
        var graph = new VisionGraph();
        var canvas = new CanvasNode(width, height, draw);
        var blobs = new BlobFilterNode();
        configure?.Invoke(blobs);

        graph.Add(canvas);
        graph.Add(blobs);
        graph.TryConnect(canvas.ImageOut, blobs.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        return (
            scheduler.GetOutput<ShapeSet>(blobs.ShapesOut) ?? ShapeSet.Empty,
            (int)(scheduler.GetOutput(blobs.CountOut) ?? 0),
            blobs);
    }

    private static void FilledCircle(Mat mat, int x, int y, int radius) =>
        CvInvoke.Circle(mat, new Point(x, y), radius, new MCvScalar(255), -1);

    private static void FilledRect(Mat mat, int x, int y, int w, int h) =>
        CvInvoke.Rectangle(mat, new Rectangle(x, y, w, h), new MCvScalar(255), -1);

    // ---- measurement -----------------------------------------------------

    [Fact]
    public async Task A_circle_measures_its_area_and_reads_as_circular()
    {
        var (shapes, count, _) = await RunAsync(
            mat => FilledCircle(mat, 200, 150, 50),
            node => node.Circularity = new ValueRange(0, null));

        Assert.Equal(1, count);

        var blob = shapes.Shapes[0];

        // pi r squared = 7854; a pixelated circle lands slightly under.
        Assert.InRange(blob.Metric(ShapeMetric.Area)!.Value, 7600, 7900);

        // Circularity is biased low on pixel contours, so this is a band, not
        // a point - but it must still be clearly circular.
        Assert.InRange(blob.Metric(ShapeMetric.Circularity)!.Value, 0.85, 1.05);
    }

    [Fact]
    public async Task A_long_rectangle_reads_its_aspect_ratio()
    {
        var (shapes, _, _) = await RunAsync(
            mat => FilledRect(mat, 50, 50, 200, 50),
            node => node.Aspect = new ValueRange(0, null));

        var blob = shapes.Shapes[0];

        Assert.Equal(4.0, blob.Metric(ShapeMetric.AspectRatio)!.Value, 1);
        Assert.InRange(blob.Metric(ShapeMetric.Length)!.Value, 195, 205);
        Assert.InRange(blob.Metric(ShapeMetric.Width)!.Value, 45, 55);
    }

    /// <summary>
    /// Aspect comes from the minimum-area rectangle, so rotating the part must
    /// not change it. From the axis-aligned box it would collapse towards 1.
    /// </summary>
    [Fact]
    public async Task Aspect_ratio_does_not_depend_on_rotation()
    {
        static void Draw(Mat mat)
        {
            var rect = new RotatedRect(new PointF(200, 150), new SizeF(200, 50), 30);
            var points = Array.ConvertAll(rect.GetVertices(), p => new Point((int)p.X, (int)p.Y));
            using var vector = new Emgu.CV.Util.VectorOfPoint(points);
            using var all = new Emgu.CV.Util.VectorOfVectorOfPoint();
            all.Push(vector);
            CvInvoke.FillPoly(mat, all, new MCvScalar(255));
        }

        var (shapes, _, _) = await RunAsync(Draw, node => node.Aspect = new ValueRange(0, null));

        Assert.Equal(4.0, shapes.Shapes[0].Metric(ShapeMetric.AspectRatio)!.Value, 0);
    }

    /// <summary>
    /// Solidity is the "concave factor": a C-shape encloses far less than its
    /// convex hull, a disc encloses all of it.
    /// </summary>
    [Fact]
    public async Task Solidity_separates_a_concave_shape_from_a_convex_one()
    {
        var (convex, _, _) = await RunAsync(
            mat => FilledCircle(mat, 200, 150, 60),
            node => node.Solidity = new ValueRange(0, null));

        var (concave, _, _) = await RunAsync(
            mat =>
            {
                FilledCircle(mat, 200, 150, 60);
                // Bite a wedge out, open to the right.
                CvInvoke.Rectangle(mat, new Rectangle(200, 130, 80, 40), new MCvScalar(0), -1);
            },
            node => node.Solidity = new ValueRange(0, null));

        var solid = convex.Shapes[0].Metric(ShapeMetric.Solidity)!.Value;
        var bitten = concave.Shapes[0].Metric(ShapeMetric.Solidity)!.Value;

        Assert.InRange(solid, 0.97, 1.001);
        Assert.True(bitten < 0.93, $"a bitten disc should be less solid, got {bitten}");
    }

    [Fact]
    public async Task Holes_are_subtracted_and_counted()
    {
        var (shapes, _, _) = await RunAsync(mat =>
        {
            FilledCircle(mat, 200, 150, 60);
            FilledCircle(mat, 200, 150, 30);  // punch it back out
            CvInvoke.Circle(mat, new Point(200, 150), 30, new MCvScalar(0), -1);
        });

        var blob = shapes.Shapes[0];

        Assert.Equal(1, blob.Metric(ShapeMetric.HoleCount)!.Value);

        // A washer: pi(60^2 - 30^2) = 8482, not pi 60^2 = 11310.
        Assert.InRange(blob.Metric(ShapeMetric.Area)!.Value, 8200, 8700);
    }

    // ---- segmentation ----------------------------------------------------

    /// <summary>
    /// The failure this was built to stop. A grey image - a not-quite-black
    /// background with bright features - has no zero pixels, so taking every
    /// non-zero pixel as foreground yields one region covering the whole
    /// frame. That reads as a successful result: an outline is drawn and a
    /// count is reported, and both are wrong.
    /// </summary>
    [Fact]
    public async Task A_grey_image_is_thresholded_rather_than_taken_whole()
    {
        // Background of 20, five discs of 230: never zero anywhere.
        static void Draw(Mat mat)
        {
            mat.SetTo(new MCvScalar(20));

            for (var i = 0; i < 5; i++)
                FilledCircle(mat, 60 + (i * 70), 150, 20);
        }

        var (shapes, count, node) = await RunAsync(Draw);

        Assert.Equal(5, count);
        Assert.Equal(NodeStatus.Ready, node.Status);

        // Each disc, not the whole 400x300 frame.
        Assert.All(
            shapes.Shapes,
            shape => Assert.InRange(shape.Metric(ShapeMetric.Area)!.Value, 1000, 1400));
    }

    /// <summary>
    /// With segmentation off the old behaviour is still available, for a graph
    /// that thresholds upstream - and on a grey image it does exactly the
    /// wrong thing, which is why it is not the default.
    /// </summary>
    [Fact]
    public async Task Segmentation_can_be_turned_off_for_an_already_binary_input()
    {
        static void Draw(Mat mat)
        {
            mat.SetTo(new MCvScalar(20));
            FilledCircle(mat, 200, 150, 40);
        }

        var (shapes, count, _) = await RunAsync(
            Draw,
            node => node.Segmentation = BlobSegmentation.None);

        Assert.Equal(1, count);

        // One region, the whole frame: 400 x 300.
        Assert.InRange(shapes.Shapes[0].Metric(ShapeMetric.Area)!.Value, 100000, 120000);
    }

    [Fact]
    public async Task A_fixed_threshold_uses_the_level_it_is_given()
    {
        // Three plateaus: 60, 140 and 220 on a dark background.
        static void Draw(Mat mat)
        {
            CvInvoke.Rectangle(mat, new Rectangle(40, 120, 60, 60), new MCvScalar(60), -1);
            CvInvoke.Rectangle(mat, new Rectangle(160, 120, 60, 60), new MCvScalar(140), -1);
            CvInvoke.Rectangle(mat, new Rectangle(280, 120, 60, 60), new MCvScalar(220), -1);
        }

        var (_, above100, _) = await RunAsync(Draw, node =>
        {
            node.Segmentation = BlobSegmentation.Fixed;
            node.Threshold = 100;
        });

        var (_, above180, _) = await RunAsync(Draw, node =>
        {
            node.Segmentation = BlobSegmentation.Fixed;
            node.Threshold = 180;
        });

        Assert.Equal(2, above100);
        Assert.Equal(1, above180);
    }

    [Fact]
    public async Task Dark_polarity_thresholds_the_other_way()
    {
        // A bright field with three dark discs punched out of it.
        static void Draw(Mat mat)
        {
            mat.SetTo(new MCvScalar(230));

            for (var i = 0; i < 3; i++)
                CvInvoke.Circle(mat, new Point(100 + (i * 100), 150), 25, new MCvScalar(30), -1);
        }

        var (_, light, _) = await RunAsync(Draw, node => node.RejectTouchingBorder = true);

        var (_, dark, _) = await RunAsync(Draw, node =>
        {
            node.Polarity = BlobPolarity.Dark;
            node.RejectTouchingBorder = true;
        });

        Assert.Equal(0, light);  // the bright field reaches every edge
        Assert.Equal(3, dark);
    }

    // ---- filtering -------------------------------------------------------

    [Fact]
    public async Task The_area_filter_keeps_only_what_is_in_range()
    {
        static void Draw(Mat mat)
        {
            FilledCircle(mat, 80, 80, 10);    // ~314
            FilledCircle(mat, 200, 80, 30);   // ~2827
            FilledCircle(mat, 320, 80, 50);   // ~7854
        }

        Assert.Equal(3, (await RunAsync(Draw)).Count);

        var (_, middle, _) = await RunAsync(Draw, node =>
        {
            node.Area = new ValueRange(1000, 5000);
        });

        Assert.Equal(1, middle);
    }

    [Fact]
    public async Task Circularity_separates_a_disc_from_a_bar()
    {
        static void Draw(Mat mat)
        {
            FilledCircle(mat, 100, 150, 40);
            FilledRect(mat, 200, 130, 160, 20);
        }

        var (shapes, count, _) = await RunAsync(Draw, node => node.Circularity = new ValueRange(0.7, null));

        Assert.Equal(1, count);
        Assert.InRange(shapes.Shapes[0].Metric(ShapeMetric.CentroidX)!.Value, 90, 110);
    }

    /// <summary>
    /// A part cut off by the frame cannot be measured, and is the filter most
    /// easily forgotten - both HALCON and Cognex expose it.
    /// </summary>
    [Fact]
    public async Task Blobs_touching_the_border_can_be_rejected()
    {
        static void Draw(Mat mat)
        {
            FilledCircle(mat, 200, 150, 40);  // clear of the edges
            FilledCircle(mat, 5, 150, 40);    // runs off the left
        }

        Assert.Equal(2, (await RunAsync(Draw)).Count);

        var (shapes, kept, _) = await RunAsync(Draw, node => node.RejectTouchingBorder = true);

        Assert.Equal(1, kept);
        Assert.Equal(0, shapes.Shapes[0].Metric(ShapeMetric.TouchesBorder)!.Value);
    }

    [Fact]
    public async Task Dark_polarity_finds_the_holes_instead()
    {
        // One white square with two dark squares inside it.
        static void Draw(Mat mat)
        {
            FilledRect(mat, 50, 50, 300, 200);
            CvInvoke.Rectangle(mat, new Rectangle(100, 100, 40, 40), new MCvScalar(0), -1);
            CvInvoke.Rectangle(mat, new Rectangle(200, 100, 40, 40), new MCvScalar(0), -1);
        }

        Assert.Equal(1, (await RunAsync(Draw)).Count);

        var (_, dark, _) = await RunAsync(Draw, node =>
        {
            node.Polarity = BlobPolarity.Dark;
            node.RejectTouchingBorder = true;
        });

        Assert.Equal(2, dark);
    }

    // ---- ordering and the cap -------------------------------------------

    [Fact]
    public async Task Sorting_by_area_puts_the_largest_first()
    {
        static void Draw(Mat mat)
        {
            FilledCircle(mat, 80, 80, 10);
            FilledCircle(mat, 320, 220, 50);
            FilledCircle(mat, 200, 80, 30);
        }

        var (shapes, _, _) = await RunAsync(Draw, node =>
        {
            node.SortBy = ShapeMeasure.Area;
            node.Descending = true;
        });

        var areas = shapes.Shapes.Select(s => s.Metric(ShapeMetric.Area)!.Value).ToList();

        Assert.Equal(areas.OrderByDescending(a => a), areas);
    }

    /// <summary>
    /// Reading order is what lets blob <i>n</i> be the same feature on every
    /// part, which sorting by size cannot promise.
    /// </summary>
    [Fact]
    public async Task Sorting_by_position_reads_top_to_bottom_then_left_to_right()
    {
        static void Draw(Mat mat)
        {
            FilledCircle(mat, 300, 60, 20);   // top right
            FilledCircle(mat, 100, 200, 20);  // bottom left
            FilledCircle(mat, 100, 60, 20);   // top left
            FilledCircle(mat, 300, 200, 20);  // bottom right
        }

        var (shapes, _, _) = await RunAsync(Draw, node => node.SortBy = ShapeMeasure.Position);

        var order = shapes.Shapes
            .Select(s => (X: s.Metric(ShapeMetric.CentroidX)!.Value, Y: s.Metric(ShapeMetric.CentroidY)!.Value))
            .ToList();

        Assert.Equal(4, order.Count);
        Assert.All(order.Take(2), p => Assert.InRange(p.Y, 40, 80));
        Assert.True(order[0].X < order[1].X, "the top row should read left to right");
        Assert.All(order.Skip(2), p => Assert.InRange(p.Y, 180, 220));
        Assert.True(order[2].X < order[3].X, "the bottom row should read left to right");
    }

    [Fact]
    public async Task The_maximum_count_keeps_the_largest_and_says_so()
    {
        static void Draw(Mat mat)
        {
            for (var i = 0; i < 8; i++)
                FilledCircle(mat, 40 + (i * 45), 150, 8 + (i * 2));
        }

        var (shapes, kept, node) = await RunAsync(Draw, blob =>
        {
            blob.SortBy = ShapeMeasure.Area;
            blob.Descending = true;
            blob.MaxCount = 3;
        });

        Assert.Equal(3, kept);
        Assert.Equal(3, shapes.Count);

        // The three biggest, not the first three found.
        var smallest = shapes.Shapes.Min(s => s.Metric(ShapeMetric.Area)!.Value);
        Assert.True(smallest > 500, $"kept a small blob: {smallest}");

        Assert.Equal(NodeStatus.Warning, node.Status);
        Assert.Contains("kept 3", node.StatusMessage!);
    }

    // ---- the shape output ------------------------------------------------

    [Fact]
    public async Task The_shape_set_records_the_frame_it_was_measured_in()
    {
        var (shapes, _, _) = await RunAsync(
            mat => FilledCircle(mat, 200, 150, 40),
            width: 640,
            height: 480);

        Assert.Equal(640, shapes.FrameWidth);
        Assert.Equal(480, shapes.FrameHeight);

        Assert.True(shapes.MatchesFrame(640, 480));
        Assert.False(shapes.MatchesFrame(320, 240));
    }

    [Fact]
    public async Task Each_blob_comes_out_as_an_outline()
    {
        var (shapes, _, _) = await RunAsync(mat => FilledRect(mat, 100, 100, 80, 60));

        var polygon = Assert.IsType<PolygonShape>(shapes.Shapes[0]);

        Assert.True(polygon.Closed);
        Assert.True(polygon.Points.Count >= 4);

        var bounds = polygon.Bounds;
        Assert.InRange(bounds.Width, 78, 81);
        Assert.InRange(bounds.Height, 58, 61);
    }

    /// <summary>
    /// Everything is measured by default, because a filter range cannot be
    /// chosen for a value the table never shows.
    /// </summary>
    [Fact]
    public async Task Every_measure_is_taken_by_default()
    {
        var (full, _, _) = await RunAsync(mat => FilledCircle(mat, 200, 150, 40));

        var blob = full.Shapes[0];

        Assert.NotNull(blob.Metric(ShapeMetric.Area));
        Assert.NotNull(blob.Metric(ShapeMetric.Perimeter));
        Assert.NotNull(blob.Metric(ShapeMetric.Circularity));
        Assert.NotNull(blob.Metric(ShapeMetric.Solidity));
        Assert.NotNull(blob.Metric(ShapeMetric.AspectRatio));
        Assert.NotNull(blob.Metric(ShapeMetric.Width));
        Assert.NotNull(blob.Metric(ShapeMetric.Length));
        Assert.NotNull(blob.Metric(ShapeMetric.Angle));
        Assert.NotNull(blob.Metric(ShapeMetric.Extent));
        Assert.NotNull(blob.Metric(ShapeMetric.HoleCount));
        Assert.NotNull(blob.Metric(ShapeMetric.TouchesBorder));
    }

    /// <summary>
    /// Turned off, the expensive measures are skipped unless something asks,
    /// so an unused one leaves no trace. That staging is what keeps an image
    /// of thousands of blobs workable.
    /// </summary>
    [Fact]
    public async Task Measures_nothing_asked_for_are_not_computed()
    {
        var (lean, _, _) = await RunAsync(
            mat => FilledCircle(mat, 200, 150, 40),
            node => node.MeasureAll = false);

        var blob = lean.Shapes[0];

        Assert.NotNull(blob.Metric(ShapeMetric.Area));
        Assert.Null(blob.Metric(ShapeMetric.Solidity));
        Assert.Null(blob.Metric(ShapeMetric.AspectRatio));
        Assert.Null(blob.Metric(ShapeMetric.Circularity));

        var (asked, _, _) = await RunAsync(
            mat => FilledCircle(mat, 200, 150, 40),
            node =>
            {
                node.MeasureAll = false;
                node.Solidity = new ValueRange(0, null);
                node.Aspect = new ValueRange(0, null);
                node.Circularity = new ValueRange(0, null);
            });

        Assert.NotNull(asked.Shapes[0].Metric(ShapeMetric.Solidity));
        Assert.NotNull(asked.Shapes[0].Metric(ShapeMetric.AspectRatio));
        Assert.NotNull(asked.Shapes[0].Metric(ShapeMetric.Circularity));
    }

    /// <summary>
    /// The whole point of shapes being port data: a finder wires into a
    /// viewer and that is all an annotation is. This walks the same path the
    /// viewer panel reads on every pass.
    /// </summary>
    [Fact]
    public async Task Found_blobs_can_be_wired_into_a_viewer_to_be_drawn()
    {
        var graph = new VisionGraph();
        var canvas = new CanvasNode(400, 300, mat => FilledCircle(mat, 200, 150, 40));
        var blobs = new BlobFilterNode();
        var viewer = new ViewerNode();

        graph.Add(canvas);
        graph.Add(blobs);
        graph.Add(viewer);

        graph.TryConnect(canvas.ImageOut, blobs.ImageIn);
        graph.TryConnect(blobs.ImageOut, viewer.ImageIn);

        var wired = graph.TryConnect(blobs.ShapesOut, viewer.ShapesIn);
        Assert.True(wired.Success, wired.Message);

        using var scheduler = CreateScheduler(graph);
        var result = await scheduler.RunAsync();

        Assert.True(result.Success);

        var shapes = scheduler.GetInput<ShapeSet>(viewer.ShapesIn);

        Assert.NotNull(shapes);
        Assert.Equal(1, shapes!.Count);
        Assert.True(shapes.MatchesFrame(400, 300));
    }

    /// <summary>An image cannot be wired into the shapes input, or vice versa.</summary>
    [Fact]
    public void The_shapes_input_only_accepts_shapes()
    {
        var graph = new VisionGraph();
        var canvas = new CanvasNode(100, 100, _ => { });
        var viewer = new ViewerNode();

        graph.Add(canvas);
        graph.Add(viewer);

        Assert.False(graph.TryConnect(canvas.ImageOut, viewer.ShapesIn).Success);
    }

    // ---- selecting one, and tabulating them -----------------------------

    [Fact]
    public async Task Select_shape_takes_the_largest_and_reports_its_measure()
    {
        var graph = new VisionGraph();
        var canvas = new CanvasNode(400, 300, mat =>
        {
            FilledCircle(mat, 80, 150, 15);
            FilledCircle(mat, 200, 150, 45);
            FilledCircle(mat, 320, 150, 25);
        });
        var blobs = new BlobFilterNode();
        var pick = new SelectShapeNode { Mode = ShapeSelection.Largest, Measure = ShapeMeasure.Area };

        graph.Add(canvas);
        graph.Add(blobs);
        graph.Add(pick);
        graph.TryConnect(canvas.ImageOut, blobs.ImageIn);
        graph.TryConnect(blobs.ShapesOut, pick.ShapesIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        var one = scheduler.GetOutput<ShapeSet>(pick.ShapesOut)!;
        var value = (double)(scheduler.GetOutput(pick.ValueOut) ?? 0.0);

        Assert.Equal(1, one.Count);

        // pi 45 squared = 6362.
        Assert.InRange(value, 6000, 6600);
        Assert.Equal(value, one.Shapes[0].Metric(ShapeMetric.Area)!.Value, 3);
    }

    /// <summary>
    /// Selecting by a measure the producer skipped is reachable by design,
    /// because measures are only computed when something asks. It must say so
    /// rather than silently comparing nothing.
    /// </summary>
    [Fact]
    public async Task Selecting_by_an_unmeasured_value_explains_itself()
    {
        var graph = new VisionGraph();
        var canvas = new CanvasNode(400, 300, mat => FilledCircle(mat, 200, 150, 40));

        // Measuring is turned off here to create the condition on purpose:
        // with the default it is taken, and there is nothing to warn about.
        var blobs = new BlobFilterNode { MeasureAll = false };
        var pick = new SelectShapeNode { Mode = ShapeSelection.Largest, Measure = ShapeMeasure.Solidity };

        graph.Add(canvas);
        graph.Add(blobs);
        graph.Add(pick);
        graph.TryConnect(canvas.ImageOut, blobs.ImageIn);
        graph.TryConnect(blobs.ShapesOut, pick.ShapesIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Equal(NodeStatus.Warning, pick.Status);
        Assert.Contains("Solidity", pick.StatusMessage!);
    }

    [Fact]
    public async Task The_table_lists_a_row_per_blob_with_the_measures_taken()
    {
        var (shapes, _, _) = await RunAsync(
            mat =>
            {
                FilledCircle(mat, 100, 150, 20);
                FilledCircle(mat, 250, 150, 30);
            },
            node => node.Circularity = new ValueRange(0, null));

        var table = ShapeTable.From(shapes);

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("#", table.Columns[0]);
        Assert.Contains("Area", table.Columns);
        Assert.Contains("Circularity", table.Columns);

        // Everything is measured by default, so every column is there to
        // choose a filter range from.
        Assert.Contains("Solidity", table.Columns);
        Assert.Contains("Aspect", table.Columns);

        Assert.Equal("1", table.Rows[0][0]);
        Assert.Equal("2", table.Rows[1][0]);
        Assert.All(table.Rows, row => Assert.Equal(table.Columns.Count, row.Length));
    }

    /// <summary>Sorting by a measure must compute it, even with no filter set.</summary>
    [Fact]
    public async Task Sorting_by_a_measure_computes_it()
    {
        var (shapes, _, _) = await RunAsync(
            mat => FilledCircle(mat, 200, 150, 40),
            node =>
            {
                node.MeasureAll = false;
                node.SortBy = ShapeMeasure.Solidity;
            });

        Assert.NotNull(shapes.Shapes[0].Metric(ShapeMetric.Solidity));
    }
}
