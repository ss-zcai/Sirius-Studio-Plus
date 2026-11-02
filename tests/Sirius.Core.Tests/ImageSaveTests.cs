using System.IO;
using System.Reflection;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sirius.Core.Shapes;
using Sirius.Studio.App.Controls;
using Sirius.Studio.App.Imaging;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Saving the picture a viewer shows: alone, or with what is drawn over it.
/// </summary>
[Collection(WpfCollection.Name)]
public class ImageSaveTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-save-" + Guid.NewGuid().ToString("N"));

    public ImageSaveTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
        Directory.CreateDirectory(_folder);
    }

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

    // ---- the file ---------------------------------------------------------------

    /// <summary>A plain save is the pixels as they are - PNG is lossless.</summary>
    [Fact]
    public void A_png_keeps_every_pixel() =>
        WpfTestHost.OnStaThread(() =>
        {
            var picture = Gradient(300, 200);
            var path = Path.Combine(_folder, "plain.png");

            ImageFileWriter.Save(picture, path);

            var read = Read(path);
            Assert.Equal((300, 200), (read.PixelWidth, read.PixelHeight));
            Assert.Equal(Pixels(picture), Pixels(new FormatConvertedBitmap(read, PixelFormats.Gray8, null, 0)));
        });

    [Theory]
    [InlineData("frame.jpg")]
    [InlineData("frame.bmp")]
    [InlineData("frame.tif")]
    public void Every_offered_format_writes_a_readable_file(string name) =>
        WpfTestHost.OnStaThread(() =>
        {
            var path = Path.Combine(_folder, name);

            // With an alpha channel, as a picture with overlays has.
            ImageFileWriter.Save(new FormatConvertedBitmap(Gradient(64, 48), PixelFormats.Pbgra32, null, 0), path);

            var read = Read(path);
            Assert.Equal((64, 48), (read.PixelWidth, read.PixelHeight));
        });

    [Fact]
    public void Saving_over_a_file_replaces_it_and_leaves_nothing_behind() =>
        WpfTestHost.OnStaThread(() =>
        {
            var path = Path.Combine(_folder, "again.png");

            ImageFileWriter.Save(Gradient(10, 10), path);
            ImageFileWriter.Save(Gradient(20, 10), path);

            Assert.Equal(20, Read(path).PixelWidth);
            Assert.Equal(["again.png"], Directory.GetFiles(_folder).Select(Path.GetFileName));
        });

    [Fact]
    public void The_suggested_name_is_the_panel_and_the_time() =>
        Assert.Equal(
            "Ring Calipers 1 - In 2026-11-02 153012.png",
            ImageFileWriter.SuggestedName("Ring Calipers 1 - In", new DateTime(2026, 11, 2, 15, 30, 12)));

    [Fact]
    public void A_title_with_characters_a_file_cannot_have_is_made_safe() =>
        Assert.Equal(
            "a-b-c 2026-11-02 153012.png",
            ImageFileWriter.SuggestedName("a/b:c", new DateTime(2026, 11, 2, 15, 30, 12)));

    // ---- deeper than 8 bits -------------------------------------------------------

    /// <summary>
    /// A 16-bit frame is saved 16-bit. The viewer shows it divided by 257,
    /// and saved like that a 12-bit camera's frame lost its low bits and
    /// could not be run through a recipe again with the same answer.
    /// </summary>
    [Theory]
    [InlineData("deep.png")]
    [InlineData("deep.tif")]
    public void A_16_bit_frame_is_saved_with_its_own_values(string name)
    {
        using var mat = Ramp16(64, 32);
        var path = Path.Combine(_folder, name);

        Assert.True(ImageFileWriter.CanHold(DepthType.Cv16U, path));

        ImageFileWriter.Save(mat, path);

        using var read = CvInvoke.Imread(path, ImreadModes.AnyDepth | ImreadModes.AnyColor);
        Assert.Equal(DepthType.Cv16U, read.Depth);
        Assert.Equal(Values16(mat), Values16(read));
    }

    /// <summary>
    /// Readable by other programs too, which see the red where it is -
    /// OpenCV's frames are BGR, a PNG's are RGB.
    /// </summary>
    [Fact]
    public void A_16_bit_colour_frame_comes_out_in_the_right_order() =>
        WpfTestHost.OnStaThread(() =>
        {
            using var mat = new Mat(4, 4, DepthType.Cv16U, 3);
            mat.SetTo(new MCvScalar(1000, 2000, 3000)); // blue, green, red
            var path = Path.Combine(_folder, "colour.png");

            ImageFileWriter.Save(mat, path);

            var read = Read(path);
            Assert.Equal(PixelFormats.Rgb48, read.Format);

            var first = new ushort[3];
            read.CopyPixels(new Int32Rect(0, 0, 1, 1), first, 6, 0);
            Assert.Equal(new ushort[] { 3000, 2000, 1000 }, first);
        });

    /// <summary>A float result keeps its values in a TIFF - and only there.</summary>
    [Fact]
    public void A_float_image_keeps_its_values_in_a_tiff()
    {
        using var mat = new Mat(8, 8, DepthType.Cv32F, 1);
        mat.SetTo(new MCvScalar(-12.5));
        var path = Path.Combine(_folder, "float.tif");

        Assert.True(ImageFileWriter.CanHold(DepthType.Cv32F, path));
        Assert.False(ImageFileWriter.CanHold(DepthType.Cv32F, Path.Combine(_folder, "float.png")));

        ImageFileWriter.Save(mat, path);

        using var read = CvInvoke.Imread(path, ImreadModes.AnyDepth | ImreadModes.AnyColor);
        Assert.Equal(DepthType.Cv32F, read.Depth);

        var values = new float[64];
        read.CopyTo(values);
        Assert.All(values, value => Assert.Equal(-12.5f, value));
    }

    [Theory]
    [InlineData("a.png", true)]
    [InlineData("a.tif", true)]
    [InlineData("a.jpg", false)]
    [InlineData("a.bmp", false)]
    public void Only_png_and_tiff_hold_16_bits(string name, bool holds) =>
        Assert.Equal(holds, ImageFileWriter.CanHold(DepthType.Cv16U, name));

    /// <summary>
    /// The panel keeps the last deep frame to save, and lets it go when an
    /// 8-bit one replaces it - whose display bitmap is its own values.
    /// </summary>
    [Fact]
    public void A_viewer_saves_the_deep_frame_it_is_showing() =>
        WpfTestHost.OnStaThread(() =>
        {
            var source = new FrameNode { Frame = () => Ramp16(64, 32) };
            var viewer = new ViewerNode();
            var graph = new VisionGraph();
            graph.Add(source);
            graph.Add(viewer);
            graph.TryConnect(source.Out, viewer.ImageIn);

            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            using var scheduler = new GraphScheduler(graph);
            SynchronizationContext.SetSynchronizationContext(previous);

            using var panel = new ViewerPanelViewModel(viewer, new ShapeSelectionService());

            void Pass()
            {
                source.Invalidate();
                scheduler.RunAsync().GetAwaiter().GetResult();
                panel.Prepare(scheduler);
                panel.Commit();
            }

            Pass();
            Assert.Equal(DepthType.Cv16U, panel.OriginalDepth);

            var path = Path.Combine(_folder, "from-viewer.png");
            panel.SaveOriginal(path);

            using (var expected = Ramp16(64, 32))
            using (var read = CvInvoke.Imread(path, ImreadModes.AnyDepth | ImreadModes.AnyColor))
                Assert.Equal(Values16(expected), Values16(read));

            source.Frame = () => new Mat(32, 16, DepthType.Cv8U, 1);
            Pass();
            Assert.Null(panel.OriginalDepth);

            // And back again, into the buffer kept from the first.
            source.Frame = () => Ramp16(64, 32);
            Pass();
            Assert.Equal(DepthType.Cv16U, panel.OriginalDepth);
        });

    // ---- with overlays ----------------------------------------------------------

    /// <summary>
    /// The overlays are drawn at the image's own resolution, where they
    /// were found - not at the size of the panel, nor where the zoom is.
    /// </summary>
    [Fact]
    public void Overlays_are_drawn_over_the_whole_picture_at_full_size() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Viewer(new CircleShape(new Point2(1000, 500), 300));

            var saved = RenderWithOverlays(viewer);

            Assert.Equal((2000, 1000), (saved.PixelWidth, saved.PixelHeight));

            // On the circle's edge, the default green; at its centre, the
            // black picture under it.
            Assert.True(IsGreen(PixelAt(saved, 1300, 500)), "No green on the circle's edge");
            Assert.Equal((0, 0, 0), PixelAt(saved, 1000, 500));
        });

    /// <summary>
    /// Labels come too, in their box. They have no size until laid out, so
    /// a render that skipped layout would save the lines and lose the words.
    /// </summary>
    [Fact]
    public void Labels_are_saved_with_their_shapes() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Viewer(new CircleShape(new Point2(1000, 500), 300) { Label = "R 300" });

            var saved = RenderWithOverlays(viewer);

            // Drawn as on a picture a thousand pixels across, so two image
            // pixels to a screen one: the box starts 16 right of the centre
            // and 40 above it.
            Assert.NotEqual((0, 0, 0), PixelAt(saved, 1020, 464));
        });

    /// <summary>The show buttons are the options: a hidden layer is not saved.</summary>
    [Fact]
    public void A_hidden_layer_is_left_out() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Viewer(new CircleShape(new Point2(1000, 500), 300));
            ((Canvas)viewer.FindName("ShapeLayer")).Visibility = Visibility.Collapsed;

            var saved = RenderWithOverlays(viewer);

            Assert.Equal((0, 0, 0), PixelAt(saved, 1300, 500));
        });

    /// <summary>Saving draws the overlays at a weight of its own, then puts the screen's back.</summary>
    [Fact]
    public void Saving_leaves_the_screen_as_it_was() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Viewer(new CircleShape(new Point2(1000, 500), 300));
            var before = Weight(viewer);

            RenderWithOverlays(viewer);

            Assert.Equal(before, Weight(viewer), 9);
        });

    // ---- helpers ----------------------------------------------------------------

    private static ImageViewer Viewer(params Shape[] shapes)
    {
        WpfTestHost.EnsureApplicationResources();

        var viewer = new ImageViewer
        {
            ImageSource = BitmapSource.Create(
                2000, 1000, 96, 96, PixelFormats.Gray8, null, new byte[2000 * 1000], 2000),
            Shapes = new ShapeSet(shapes, 2000, 1000),
        };

        viewer.Measure(new Size(500, 400));
        viewer.Arrange(new Rect(0, 0, 500, 400));
        viewer.UpdateLayout();
        viewer.FitToWindow();

        return viewer;
    }

    private static BitmapSource RenderWithOverlays(ImageViewer viewer) =>
        (BitmapSource)typeof(ImageViewer)
            .GetMethod("RenderWithOverlays", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewer, null)!;

    private static double Weight(ImageViewer viewer) =>
        ((System.Windows.Shapes.Path)((Canvas)viewer.FindName("ShapeLayer")).Children[0]).StrokeThickness;

    private static BitmapSource Gradient(int width, int height)
    {
        var pixels = new byte[width * height];

        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)(i * 7);

        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
    }

    /// <summary>A 16-bit ramp using the whole range, low bits and all.</summary>
    private static Mat Ramp16(int width, int height)
    {
        var mat = new Mat(height, width, DepthType.Cv16U, 1);
        var values = new ushort[width * height];

        for (var i = 0; i < values.Length; i++)
            values[i] = (ushort)(i * 31 + 7);

        mat.SetTo(values);
        return mat;
    }

    private static ushort[] Values16(Mat mat)
    {
        var values = new ushort[mat.Width * mat.Height];
        mat.CopyTo(values);
        return values;
    }

    private sealed class FrameNode : VisionNode
    {
        public FrameNode() => Out = AddOutput<ImageData>("Out");

        public OutputPort Out { get; }

        public Func<Mat> Frame { get; set; } = () => new Mat();

        protected override void OnEvaluate(EvalContext context) => context.Set(Out, ImageData.Adopt(Frame()));
    }

    private static BitmapSource Read(string path)
    {
        using var stream = File.OpenRead(path);
        return BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    }

    private static byte[] Pixels(BitmapSource picture)
    {
        var pixels = new byte[picture.PixelWidth * picture.PixelHeight];
        picture.CopyPixels(pixels, picture.PixelWidth, 0);
        return pixels;
    }

    private static (byte R, byte G, byte B) PixelAt(BitmapSource picture, int x, int y)
    {
        var bgra = new byte[4];
        new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0)
            .CopyPixels(new Int32Rect(x, y, 1, 1), bgra, 4, 0);

        return (bgra[2], bgra[1], bgra[0]);
    }

    private static bool IsGreen((byte R, byte G, byte B) pixel) =>
        pixel.G > 120 && pixel.G > pixel.R * 2 && pixel.G > pixel.B;
}
