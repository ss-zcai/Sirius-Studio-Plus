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

    // ---- kept upright -------------------------------------------------------

    [Fact]
    public void A_rectangle_drawn_along_its_top_keeps_its_corners_as_drawn()
    {
        var kept = Measurement.UprightCorners(new Point(10, 20), new Point(110, 20), new Point(50, 70));

        Near(new Point(10, 20), kept[0]);
        Near(new Point(110, 20), kept[1]);
        Near(new Point(110, 70), kept[2]);
    }

    /// <summary>
    /// Whichever side is drawn first, and whichever way along it, the top
    /// is the side facing up the image and the left is on the left: a crop
    /// of a level part comes out as it looks, never turned or mirrored.
    /// </summary>
    [Theory]
    [InlineData(110, 70, 10, 70, 50, 20, 110)]   // the bottom, right to left, then up
    [InlineData(10, 20, 10, 70, 90, 40, 90)]     // the left side, downwards, then across
    [InlineData(110, 70, 110, 20, 10, 40, 110)]  // the right side, upwards, then across
    public void Any_side_drawn_first_gives_the_same_upright_rectangle(
        double ax, double ay, double bx, double by, double cx, double cy, double right)
    {
        var kept = Measurement.UprightCorners(new Point(ax, ay), new Point(bx, by), new Point(cx, cy));

        Near(new Point(10, 20), kept[0]);
        Near(new Point(right, 20), kept[1]);
        Near(new Point(right, 70), kept[2]);
    }

    [Fact]
    public void A_turned_rectangle_keeps_the_side_facing_most_nearly_up_as_its_top()
    {
        // A board turned 10 degrees clockwise, its long side drawn first
        // along what is now its bottom.
        var corners = Measurement.TurnRectangle(
            [new Point(0, 0), new Point(200, 0), new Point(200, 100)], 10);
        var all = Measurement.RectangleCorners(corners[0], corners[1], corners[2]);

        var kept = Measurement.UprightCorners(all[2], all[3], all[0]);

        Assert.Equal(10, Measurement.RectangleAngle(kept), 6);
        Near(all[0], kept[0]);
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

        var targets = RoiBatch.Targets(pictures, 2, "C:\\rois");

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
            RoiBatch.Targets(pictures, rois.Count, output), rois, new SyncProgress(reports.Add));

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

    /// <summary>Reports at once, on the calling thread, so the test can count them.</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
