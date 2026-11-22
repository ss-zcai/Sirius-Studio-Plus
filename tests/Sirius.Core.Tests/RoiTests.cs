using System.IO;
using System.Windows;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Core.Shapes;
using Sirius.IV;
using Sirius.Studio.App.Controls;
using Sirius.Studio.App.Imaging;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The viewer's rectangle (F31) and its crops saved as ROIs (F32): the
/// geometry of keeping it upright, its reading, and the pixels a crop holds.
/// </summary>
[Collection(WpfCollection.Name)]
public class RoiTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("sirius-roi-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void Near(Point expected, Point actual, double tolerance = 1e-6)
    {
        Assert.True(
            Math.Abs(expected.X - actual.X) < tolerance && Math.Abs(expected.Y - actual.Y) < tolerance,
            $"expected {expected}, got {actual}");
    }

    // ---- drawn corner to corner -----------------------------------------------

    /// <summary>
    /// Dragged any way - down and right, up and left, either diagonal - it
    /// is the same level rectangle, top left first, so a crop of it comes
    /// out as it looks, never turned or mirrored.
    /// </summary>
    [Theory]
    [InlineData(10, 20, 110, 70)]
    [InlineData(110, 70, 10, 20)]
    [InlineData(110, 20, 10, 70)]
    [InlineData(10, 70, 110, 20)]
    public void A_rectangle_dragged_any_way_is_kept_top_left_first(double ax, double ay, double bx, double by)
    {
        var kept = Measurement.BoxCorners(new Point(ax, ay), new Point(bx, by));

        Near(new Point(10, 20), kept[0]);
        Near(new Point(110, 20), kept[1]);
        Near(new Point(110, 70), kept[2]);
        Assert.Equal(0, Measurement.RectangleAngle(kept), 6);
    }

    [Fact]
    public void A_drag_along_a_line_still_makes_a_rectangle_a_pixel_across()
    {
        var kept = Measurement.BoxCorners(new Point(10, 20), new Point(60, 20));

        Assert.Equal(50, Measurement.Distance(kept[0], kept[1]), 6);
        Assert.Equal(1, Measurement.Distance(kept[1], kept[2]), 6);
    }

    [Fact]
    public void A_rectangle_being_dragged_reads_as_the_box_so_far()
    {
        Assert.StartsWith("L 100 × W 50 px", Measurement.Describe(ViewerTool.Rectangle, [new(110, 70), new(10, 20)]));
    }

    [Fact]
    public void T_moves_the_top_round_the_sides_and_back_after_four()
    {
        Point[] kept = [new(0, 0), new(200, 0), new(200, 100)];

        var once = Measurement.NextTop(kept);

        // The right side is the top now: a crop is turned a quarter.
        Assert.Equal(90, Measurement.RectangleAngle(once), 6);
        Near(new Point(200, 0), once[0]);
        Near(new Point(200, 100), once[1]);
        Near(new Point(0, 100), once[2]);

        var round = Measurement.NextTop(Measurement.NextTop(Measurement.NextTop(once)));
        for (var i = 0; i < 3; i++)
            Near(kept[i], round[i]);
    }

    // ---- the reading ---------------------------------------------------------

    [Fact]
    public void The_reading_is_length_by_width_the_longer_first()
    {
        var level = Measurement.Describe(ViewerTool.Rectangle, [new(0, 0), new(80, 0), new(80, 200)]);

        Assert.StartsWith("L 200 × W 80 px", level);
        Assert.DoesNotContain("∠", level);
    }

    [Fact]
    public void A_turned_rectangle_says_its_angle()
    {
        var turned = Measurement.TurnRectangle([new Point(0, 0), new Point(200, 0), new Point(200, 80)], 12.5);

        var reading = Measurement.Describe(ViewerTool.Rectangle, turned);

        Assert.Contains("L 200 × W 80 px", reading);
        Assert.Contains("∠ 12.5°", reading);
    }

    [Fact]
    public void A_rectangle_is_picked_by_its_outline_not_its_inside()
    {
        var rectangle = new Measurement(
            ViewerTool.Rectangle,
            [new Point(0, 0), new Point(200, 0), new Point(200, 100)],
            System.Windows.Media.Colors.Gold);

        Assert.Equal(0, rectangle.DistanceTo(new Point(200, 50)), 6);
        Assert.Equal(2, rectangle.DistanceTo(new Point(100, 102)), 6);
        Assert.True(rectangle.DistanceTo(new Point(100, 50)) >= 50);
    }

    // ---- editing --------------------------------------------------------------

    [Fact]
    public void A_corner_dragged_resizes_from_the_corner_opposite()
    {
        Point[] kept = [new(10, 10), new(110, 10), new(110, 60)];

        // The bottom right, out to (150, 90): the top left stays.
        var resized = Measurement.ResizeRectangle(kept, 2, new Point(150, 90));

        Near(new Point(10, 10), resized[0]);
        Near(new Point(150, 10), resized[1]);
        Near(new Point(150, 90), resized[2]);
    }

    [Fact]
    public void A_corner_dragged_past_its_opposite_does_not_turn_the_rectangle_inside_out()
    {
        Point[] kept = [new(10, 10), new(110, 10), new(110, 60)];

        // The top left dragged beyond the bottom right.
        var resized = Measurement.ResizeRectangle(kept, 0, new Point(130, 80));

        Near(new Point(110, 60), resized[0]);
        Near(new Point(130, 60), resized[1]);
        Near(new Point(130, 80), resized[2]);
        Assert.Equal(0, Measurement.RectangleAngle(resized), 6);
    }

    [Fact]
    public void A_turned_rectangle_resizes_along_its_own_sides()
    {
        var kept = Measurement.TurnRectangle([new Point(0, 0), new Point(100, 0), new Point(100, 50)], 30);
        var far = Measurement.RectangleCorners(kept[0], kept[1], kept[2])[2];

        // Pulling the bottom right 20 further along the rectangle's own
        // right-hand direction lengthens it by 20 and keeps its turn.
        var (sin, cos) = Math.SinCos(30 * Math.PI / 180);
        var resized = Measurement.ResizeRectangle(kept, 2, new Point(far.X + (20 * cos), far.Y + (20 * sin)));

        Assert.Equal(120, Measurement.Distance(resized[0], resized[1]), 6);
        Assert.Equal(50, Measurement.Distance(resized[1], resized[2]), 6);
        Assert.Equal(30, Measurement.RectangleAngle(resized), 6);
        Near(kept[0], resized[0]);
    }

    [Fact]
    public void The_knob_is_beyond_the_middle_of_the_top()
    {
        Near(new Point(50, -20), Measurement.RectangleKnob([new Point(0, 0), new Point(100, 0), new Point(100, 40)], 20));

        // Turned a quarter: the top is on the right, and so is the knob.
        var turned = Measurement.NextTop([new Point(0, 0), new Point(100, 0), new Point(100, 40)]);
        Near(new Point(120, 20), Measurement.RectangleKnob(turned, 20));
    }

    // ---- the crop -------------------------------------------------------------

    /// <summary>A 16-bit picture whose every pixel says where it is: 100 × y + x.</summary>
    private static Mat Numbered(int width, int height)
    {
        var mat = new Mat(height, width, DepthType.Cv16U, 1);
        var values = new ushort[width * height];

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                values[(y * width) + x] = (ushort)((100 * y) + x);

        mat.SetTo(values);
        return mat;
    }

    private static ushort[] Values(Mat mat)
    {
        var values = new ushort[mat.Width * mat.Height];
        mat.CopyTo(values);
        return values;
    }

    [Fact]
    public void A_level_crop_is_the_pixels_inside_it_value_for_value()
    {
        using var picture = Numbered(40, 30);

        using var crop = RoiCrop.Cut(picture, new Point(5, 7), new Point(9, 7), new Point(9, 10));

        Assert.Equal(4, crop.Width);
        Assert.Equal(3, crop.Height);
        Assert.Equal(DepthType.Cv16U, crop.Depth);
        Assert.Equal(new ushort[] { 705, 706, 707, 708, 805, 806, 807, 808, 905, 906, 907, 908 }, Values(crop));
    }

    /// <summary>
    /// The right side as the top: the right-hand column of the picture is
    /// the crop's top row, read downwards - the picture turned a quarter
    /// anticlockwise, as the rectangle is.
    /// </summary>
    [Fact]
    public void A_crop_with_the_right_side_as_top_is_the_picture_turned_a_quarter()
    {
        using var picture = Numbered(4, 3);
        var kept = Measurement.NextTop([new Point(0, 0), new Point(4, 0), new Point(4, 3)]);

        using var crop = RoiCrop.Cut(picture, kept[0], kept[1], kept[2]);
        using var expected = new Mat();
        CvInvoke.Rotate(picture, expected, RotateFlags.Rotate90CounterClockwise);

        Assert.Equal(expected.Width, crop.Width);
        Assert.Equal(expected.Height, crop.Height);
        Assert.Equal(Values(expected), Values(crop));
    }

    [Fact]
    public void A_turned_crop_is_as_large_as_the_rectangle_and_black_outside_the_picture()
    {
        using var picture = new Mat(100, 100, DepthType.Cv8U, 1);
        picture.SetTo(new Emgu.CV.Structure.MCvScalar(200));

        // 60 by 20, turned 30 degrees, its left end hanging off the picture.
        var kept = Measurement.TurnRectangle([new Point(-20, 40), new Point(40, 40), new Point(40, 60)], 30);

        using var crop = RoiCrop.Cut(picture, kept[0], kept[1], kept[2]);

        Assert.Equal(60, crop.Width);
        Assert.Equal(20, crop.Height);

        var values = new byte[crop.Width * crop.Height];
        crop.CopyTo(values);

        Assert.Equal(0, values[0]);
        Assert.Equal(200, values[(10 * 60) + 50]);
    }

    [Fact]
    public void A_set_is_numbered_after_the_name_chosen()
    {
        Assert.Equal(Path.Combine("C:\\out", "board-3.png"), RoiCrop.Numbered(Path.Combine("C:\\out", "board.png"), 3));
    }

    [Fact]
    public void A_deep_crop_saved_as_a_jpeg_is_brought_to_eight_bits_by_its_own_bits()
    {
        using var crop = new Mat(2, 2, DepthType.Cv16U, 1);
        crop.SetTo(new Emgu.CV.Structure.MCvScalar(4095));
        var path = Path.Combine(_folder, "deep.jpg");

        RoiCrop.Save(crop, path, bits: 12);

        using var read = CvInvoke.Imread(path, ImreadModes.Unchanged);
        Assert.Equal(DepthType.Cv8U, read.Depth);
        double min = 0, max = 0;
        System.Drawing.Point at = default, at2 = default;
        CvInvoke.MinMaxLoc(read, ref min, ref max, ref at, ref at2);

        Assert.True(max > 250, "12-bit full scale should be white, not nearly black");
    }

    // ---- the viewer -----------------------------------------------------------

    [Fact]
    public void A_viewer_restores_saved_rectangles_and_numbers_them_as_drawn() =>
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var viewer = new ImageViewer
            {
                ImageSource = new System.Windows.Media.Imaging.WriteableBitmap(
                    64, 48, 96, 96, System.Windows.Media.PixelFormats.Gray8, null),
            };

            viewer.Measure(new Size(900, 700));
            viewer.Arrange(new Rect(0, 0, 900, 700));
            viewer.UpdateLayout();

            viewer.Measurements =
            [
                Rectangle(1, 1, 20, 1, 20, 10),
                new MeasurementRecord("PointToPoint", [new Point2(0, 0), new Point2(5, 5)], "#FFFFD700", null),
                Rectangle(30, 5, 50, 5, 50, 40),
            ];

            var rectangles = viewer.Rectangles();

            Assert.Equal(2, rectangles.Count);
            Assert.Equal(30, rectangles[1].Points[0].X);
        });

    /// <summary>
    /// The turning cursor is a real .cur - one 32-pixel image, its hot spot
    /// in the middle, something drawn in it and its corners clear.
    /// </summary>
    [Fact]
    public void The_turning_cursor_is_a_cursor_file_with_its_hot_spot_in_the_middle() =>
        WpfTestHost.OnStaThread(() =>
        {
            var file = TurnCursor.CursorFile();

            Assert.Equal(22 + 40 + (32 * 32 * 4) + (32 * 4), file.Length);
            Assert.Equal(2, BitConverter.ToUInt16(file, 2));
            Assert.Equal(TurnCursor.HotX, BitConverter.ToUInt16(file, 10));
            Assert.Equal(TurnCursor.HotY, BitConverter.ToUInt16(file, 12));

            var alphas = Enumerable.Range(0, 32 * 32).Select(i => file[62 + (i * 4) + 3]).ToArray();
            Assert.True(alphas.Count(a => a > 200) > 60, "the arrow should be drawn");
            Assert.Equal(0, alphas[0]);

            Assert.NotEqual(System.Windows.Input.Cursors.Hand, TurnCursor.Value);
        });

    /// <summary>
    /// The cursor is drawn from res\cursors\turn-cursor.svg, built in -
    /// not the fallback shape - with the hot spot its "hotspot" circle marks.
    /// </summary>
    [Fact]
    public void The_turning_cursor_is_drawn_from_its_svg() =>
        WpfTestHost.OnStaThread(() =>
        {
            using var stream = typeof(TurnCursor).Assembly.GetManifestResourceStream(TurnCursor.DrawingName);
            Assert.NotNull(stream);

            var drawn = TurnCursor.FromSvg(stream!);
            Assert.NotNull(drawn);
            Assert.Equal((16, 17), (drawn!.Value.HotX, drawn.Value.HotY));

            // Something sizeable, on the artboard - whatever it is drawn as.
            var bounds = drawn.Value.Outline.Bounds;
            Assert.True(bounds.Width > 8 && bounds.Height > 8, $"the drawing is only {bounds}");
            Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= 32 && bounds.Bottom <= 32, $"the drawing is off the artboard: {bounds}");
        });

    [Fact]
    public void A_drawing_scaled_to_its_view_box_gives_the_cursors_32_pixels() =>
        WpfTestHost.OnStaThread(() =>
        {
            const string svg = """
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
                  <rect x="8" y="8" width="48" height="16" />
                  <circle id="hotspot" cx="32" cy="16" r="1" />
                </svg>
                """;

            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svg));
            var drawn = TurnCursor.FromSvg(stream);

            Assert.NotNull(drawn);
            Assert.Equal(new Rect(4, 4, 24, 8), drawn!.Value.Outline.Bounds);
            Assert.Equal((16, 8), (drawn.Value.HotX, drawn.Value.HotY));
        });

    /// <summary>
    /// A corner shows a two-way arrow along its own diagonal, turned with
    /// the rectangle; the knob a hand; the selected outline Move.
    /// </summary>
    [Fact]
    public void A_selected_rectangles_corners_show_resize_arrows_along_their_diagonals() =>
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var viewer = new ImageViewer
            {
                ImageSource = new System.Windows.Media.Imaging.WriteableBitmap(
                    200, 200, 96, 96, System.Windows.Media.PixelFormats.Gray8, null),
            };

            viewer.Measure(new Size(900, 700));
            viewer.Arrange(new Rect(0, 0, 900, 700));
            viewer.UpdateLayout();

            viewer.Measurements = [Rectangle(20, 20, 120, 20, 120, 80)];
            var rectangle = viewer.Rectangles()[0];

            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(ImageViewer).GetMethod("Select", flags)!.Invoke(viewer, [rectangle]);

            System.Windows.Input.Cursor CursorAt(int handle) =>
                (System.Windows.Input.Cursor)typeof(ImageViewer).GetMethod("HandleCursor", flags)!.Invoke(viewer, [handle])!;

            Assert.Equal(System.Windows.Input.Cursors.SizeNWSE, CursorAt(0));
            Assert.Equal(System.Windows.Input.Cursors.SizeNESW, CursorAt(1));
            Assert.Equal(System.Windows.Input.Cursors.SizeNWSE, CursorAt(2));
            Assert.Equal(System.Windows.Input.Cursors.SizeNESW, CursorAt(3));
            Assert.Same(TurnCursor.Value, CursorAt(4));
            Assert.NotEqual(System.Windows.Input.Cursors.Hand, CursorAt(4));

            // Turned a quarter, the top left corner sits where the top right
            // was, and its arrow turns with it.
            var turned = Measurement.TurnRectangle(rectangle.Points, 90);
            for (var i = 0; i < 3; i++)
                rectangle.Points[i] = turned[i];

            Assert.Equal(System.Windows.Input.Cursors.SizeNESW, CursorAt(0));
        });

    /// <summary>
    /// A region with a band has its inner and outer edges drawn round it,
    /// dashed and turned with it; with none, only the region.
    /// </summary>
    [Fact]
    public void The_region_editor_draws_the_band_round_the_region() =>
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var viewer = new ImageViewer
            {
                ImageSource = new System.Windows.Media.Imaging.WriteableBitmap(
                    400, 300, 96, 96, System.Windows.Media.PixelFormats.Gray8, null),
                IsRoiEnabled = true,
                Roi = new System.Drawing.Rectangle(100, 50, 200, 120),
                RoiAngle = 10,
            };

            viewer.Measure(new Size(900, 700));
            viewer.Arrange(new Rect(0, 0, 900, 700));
            viewer.UpdateLayout();

            System.Windows.Shapes.Path? Band() =>
                viewer.HandleLayer.Children.OfType<System.Windows.Shapes.Path>().SingleOrDefault();

            // No band: the region shaded and solid, as any region.
            Assert.Null(Band());
            Assert.True(viewer.RoiShape.StrokeDashArray is null or { Count: 0 });
            Assert.NotEqual(System.Windows.Media.Brushes.Transparent, viewer.RoiShape.Fill);

            viewer.RoiColour = System.Windows.Media.Color.FromRgb(0x38, 0xBD, 0xF8);
            viewer.RoiBand = 20;
            var band = Band();

            // The band shaded between its two solid edges, turned with the
            // region, under the corner marks and taking no clicks...
            Assert.NotNull(band);
            Assert.Same(band, viewer.HandleLayer.Children[0]);
            Assert.True(band!.StrokeDashArray is null or { Count: 0 });
            Assert.False(band.IsHitTestVisible);

            var edges = (System.Windows.Media.GeometryGroup)band.Data;
            Assert.Equal(System.Windows.Media.FillRule.EvenOdd, edges.FillRule);
            Assert.Equal(
                new[] { new Rect(80, 30, 240, 160), new Rect(120, 70, 160, 80) },
                edges.Children.Cast<System.Windows.Media.RectangleGeometry>().Select(r => r.Rect).ToArray());
            Assert.Equal(10, ((System.Windows.Media.RotateTransform)edges.Transform).Angle);

            // ...and the rectangle drawn between them dashed and clear.
            Assert.True(viewer.RoiShape.StrokeDashArray is { Count: > 0 });
            Assert.Equal(System.Windows.Media.Brushes.Transparent, viewer.RoiShape.Fill);

            // The corner marks in the region's colour, not amber.
            var marks = viewer.HandleLayer.Children.OfType<System.Windows.Shapes.Rectangle>().ToArray();
            Assert.Equal(4, marks.Length);
            Assert.All(marks, mark => Assert.Equal(
                System.Windows.Media.Color.FromRgb(0x38, 0xBD, 0xF8),
                ((System.Windows.Media.SolidColorBrush)mark.Stroke).Color));
        });

    private static MeasurementRecord Rectangle(double x0, double y0, double x1, double y1, double x2, double y2) =>
        new("Rectangle", [new Point2(x0, y0), new Point2(x1, y1), new Point2(x2, y2)], "#FFFFD700", null);

    // ---- Sirius IV: every picture -----------------------------------------------

    [Fact]
    public void Sirius_iv_names_each_crop_after_its_picture_and_keeps_alike_names_apart()
    {
        var pictures = new[]
        {
            new ImageFileItem(Path.Combine(_folder, "Cam1", "part.png"), _folder),
            new ImageFileItem(Path.Combine(_folder, "Cam2", "part.png"), _folder),
            new ImageFileItem(Path.Combine(_folder, "other.png"), _folder),
        };

        var two = new RoiBatch.Roi[] { new(new(0, 0), new(5, 0), new(5, 5)), new(new(1, 1), new(6, 1), new(6, 6)) };
        var targets = RoiBatch.Targets(pictures, two, "C:\\rois");

        Assert.Equal(
            new[] { "Cam1_part-1.png", "Cam1_part-2.png", "Cam2_part-1.png", "Cam2_part-2.png", "other-1.png", "other-2.png" },
            targets.Select(t => Path.GetFileName(t.Path)));
    }

    [Fact]
    public void Sirius_iv_cuts_the_same_rectangles_from_every_picture_keeping_their_bits()
    {
        var paths = new[] { "a.png", "b.png" }.Select(name => Path.Combine(_folder, name)).ToArray();

        foreach (var path in paths)
        {
            using var picture = Numbered(40, 30);
            CvInvoke.Imwrite(path, picture);
        }

        File.WriteAllText(Path.Combine(_folder, "broken.png"), "not a picture");

        var pictures = paths.Append(Path.Combine(_folder, "broken.png"))
            .Select(path => new ImageFileItem(path, _folder))
            .ToArray();

        var rois = RoiBatch.From(
        [
            Rectangle(0, 0, 10, 0, 10, 5),
            new MeasurementRecord("PointToPoint", [new Point2(0, 0), new Point2(5, 5)], "#FFFFD700", null),
            Rectangle(20, 10, 30, 10, 30, 30),
        ]);

        var output = Directory.CreateDirectory(Path.Combine(_folder, "out")).FullName;
        var reports = new List<int>();

        var result = RoiBatch.Run(
            RoiBatch.Targets(pictures, rois, output), new SyncProgress(reports.Add));

        Assert.Equal(3, result.Pictures);
        Assert.Equal(4, result.Saved);
        Assert.Single(result.Problems);
        Assert.Contains("broken.png", result.Problems[0]);
        Assert.Equal(new[] { 1, 2, 3 }, reports);

        using var second = CvInvoke.Imread(Path.Combine(output, "b-2.png"), ImreadModes.Unchanged);
        Assert.Equal(DepthType.Cv16U, second.Depth);
        Assert.Equal(10, second.Width);
        Assert.Equal(20, second.Height);
        Assert.Equal(1020, Values(second)[0]);
    }

    /// <summary>
    /// Each picture's own rectangles, where they were drawn: four on one,
    /// three on another, none on a third - which writes nothing.
    /// </summary>
    [Fact]
    public void Sirius_iv_cuts_each_pictures_own_rectangles_from_it()
    {
        var paths = new[] { "a.png", "b.png", "c.png" }.Select(name => Path.Combine(_folder, name)).ToArray();

        foreach (var path in paths)
        {
            using var picture = Numbered(40, 30);
            CvInvoke.Imwrite(path, picture);
        }

        var pictures = paths.Select(path => new ImageFileItem(path, _folder)).ToArray();
        RoiBatch.Roi Box(int x, int y, int w, int h) => new(new(x, y), new(x + w, y), new(x + w, y + h));

        var cuts = new RoiBatch.Cut[]
        {
            new(pictures[0], [Box(0, 0, 4, 4), Box(5, 0, 4, 4), Box(10, 0, 4, 4), Box(15, 0, 4, 4)]),
            new(pictures[1], [Box(0, 0, 8, 2), Box(0, 5, 8, 2), Box(0, 10, 8, 3)]),
            new(pictures[2], []),
        };

        var output = Directory.CreateDirectory(Path.Combine(_folder, "own")).FullName;
        var targets = RoiBatch.Targets(cuts, output);

        Assert.Equal(
            ["a-1.png", "a-2.png", "a-3.png", "a-4.png", "b-1.png", "b-2.png", "b-3.png"],
            targets.Select(target => Path.GetFileName(target.Path)));

        var result = RoiBatch.Run(targets);

        Assert.Equal((2, 7), (result.Pictures, result.Saved));
        Assert.Empty(result.Problems);

        // b's own third rectangle: 8 × 3, not a's 4 × 4.
        using var third = CvInvoke.Imread(Path.Combine(output, "b-3.png"), ImreadModes.Unchanged);
        Assert.Equal((8, 3), (third.Width, third.Height));
        Assert.False(File.Exists(Path.Combine(output, "c-1.png")));
    }

    [Fact]
    public void Rois_from_all_offers_each_pictures_own_unless_only_the_one_shown_has_any()
    {
        var a = new ImageFileItem(Path.Combine(_folder, "a.png"), _folder);
        var b = new ImageFileItem(Path.Combine(_folder, "b.png"), _folder);
        RoiBatch.Roi[] three = [new(new(0, 0), new(1, 0), new(1, 1)), new(new(0, 0), new(1, 0), new(1, 1)), new(new(0, 0), new(1, 0), new(1, 1))];

        // Drawn on two: each its own.
        var (own, same, sameForAll) = RoisFromAllWindow.Describe([new(a, three), new(b, three[..1])], (b, three[..1]), 120);
        Assert.Equal("Each picture's own ROIs - 4 ROIs drawn on 2 pictures", own);
        Assert.Equal("The 1 ROI on b.png, cut from each of the 120 pictures listed", same);
        Assert.False(sameForAll);

        // Drawn only on the one shown: the same places from all, as a training set begins.
        Assert.True(RoisFromAllWindow.Describe([new(a, three)], (a, three), 120).SameForAll);
    }

    /// <summary>
    /// A crop is in its picture's own format: a 12-bit TIF's are TIFs that
    /// keep every value - a line's pictures, cut for training - into a
    /// folder made for them.
    /// </summary>
    [Fact]
    public void A_tif_picture_gives_tif_crops_that_keep_its_bits()
    {
        var path = Path.Combine(_folder, "part_FAIL.tif");

        using (var picture = Numbered(40, 30))
            CvInvoke.Imwrite(path, picture);

        var output = Path.Combine(_folder, "station ROIs");
        RoiBatch.Roi[] one = [new(new(20, 10), new(30, 10), new(30, 30))];
        var targets = RoiBatch.Targets([new ImageFileItem(path, _folder)], one, output);

        Assert.Equal(Path.Combine(output, "part_FAIL-1.tif"), Assert.Single(targets).Path);

        var result = RoiBatch.Run(targets);
        Assert.Equal(1, result.Saved);

        using var crop = CvInvoke.Imread(targets[0].Path, ImreadModes.Unchanged);
        Assert.Equal(DepthType.Cv16U, crop.Depth);
        Assert.Equal(1020, Values(crop)[0]);

        Assert.Equal(".tif", RoiBatch.ExtensionFor("a.TIFF"));
        Assert.Equal(".jpg", RoiBatch.ExtensionFor("a.jpeg"));
        Assert.Equal(".bmp", RoiBatch.ExtensionFor("a.bmp"));
        Assert.Equal(".png", RoiBatch.ExtensionFor("a.png"));
    }

    [Fact]
    public void The_rois_are_offered_a_folder_beside_the_pictures_not_among_them()
    {
        Assert.Equal(@"D:\Line\images\Top ROIs", RoisFromAllWindow.SuggestedFolder(@"D:\Line\images\Top"));
        Assert.Equal(@"D:\Line\images\Top ROIs", RoisFromAllWindow.SuggestedFolder(@"D:\Line\images\Top\"));
        Assert.Equal(@"D:\ROIs", RoisFromAllWindow.SuggestedFolder(@"D:\"));
        Assert.EndsWith("ROIs", RoisFromAllWindow.SuggestedFolder(null), StringComparison.Ordinal);
    }

    /// <summary>Each choice's words wrap inside the window: the second ran off its edge, "…each of the 14".</summary>
    [Fact]
    public void The_rois_window_wraps_its_choices_inside_it()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var picture = new ImageFileItem(Path.Combine(_folder, "20261122-114526-844_000007_FAIL.tif"), _folder);
            RoiBatch.Roi[] three = [new(new(0, 0), new(1, 0), new(1, 1)), new(new(0, 0), new(1, 0), new(1, 1)), new(new(0, 0), new(1, 0), new(1, 1))];

            var window = RoisFromAllWindow.Unshown([new(picture, three)], (picture, three), 14, @"D:\Line\images\Circularity Check");
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;

            try
            {
                window.Show();
                WpfTestHost.Settle(window.Dispatcher);

                var root = (FrameworkElement)window.Content;

                foreach (var name in new[] { "OwnText", "SameText" })
                {
                    var text = (System.Windows.Controls.TextBlock)window.FindName(name);
                    var right = text.TranslatePoint(new Point(text.ActualWidth, 0), root).X;
                    Assert.True(right <= root.ActualWidth + 0.5, $"{name} runs to {right:0}, past {root.ActualWidth:0}.");
                }

                Assert.Equal(@"D:\Line\images\Circularity Check ROIs", ((System.Windows.Controls.TextBox)window.FindName("FolderBox")).Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Reports at once, on the calling thread, so the test can count them.</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
