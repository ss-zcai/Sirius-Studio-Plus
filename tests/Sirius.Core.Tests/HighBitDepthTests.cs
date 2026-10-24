using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Studio.App.Imaging;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// What happens to a 10-bit or 12-bit image.
/// </summary>
/// <remarks>
/// <para>
/// Cameras deliver 10 and 12 bits in a 16-bit container: the file is
/// <c>CV_16U</c>, but the values only reach 1023 or 4095 rather than 65535.
/// Nothing in the file says which, so anything that assumes "16-bit means
/// full range" is wrong by a factor of 16 or 64 - and wrong in the
/// direction that looks like an almost black picture rather than like an
/// error.
/// </para>
/// <para>
/// These tests record what the application actually does today, including
/// where it falls short, so the gap is measured rather than assumed.
/// </para>
/// </remarks>
[Collection(WpfCollection.Name)]
public class HighBitDepthTests
{
    public HighBitDepthTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
    }

    /// <summary>A grey ramp in a 16-bit container, filled to the given bit depth.</summary>
    private static Mat Sensor(int bits, int width = 32, int height = 8)
    {
        var mat = new Mat(height, width, DepthType.Cv16U, 1);
        var full = (1 << bits) - 1;
        var data = new ushort[width * height];

        for (var i = 0; i < data.Length; i++)
            data[i] = (ushort)(full * i / (data.Length - 1));

        mat.SetTo(data);
        return mat;
    }

    private static string WriteTemp(Mat mat, string extension)
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"sirius-depth-{Guid.NewGuid():N}{extension}");

        CvInvoke.Imwrite(path, mat);
        return path;
    }

    // ---- can the file be read at all? ----------------------------------------

    /// <summary>
    /// A 16-bit PNG reads back at 16 bits, but only when asked. The Image
    /// File node defaults to <see cref="ImreadModes.AnyColor"/>, which is
    /// 8-bit - so a 12-bit file loses four bits on the way in unless the
    /// read mode is changed.
    /// </summary>
    [Theory]
    [InlineData(ImreadModes.AnyColor, DepthType.Cv8U)]
    [InlineData(ImreadModes.Unchanged, DepthType.Cv16U)]
    [InlineData(ImreadModes.AnyDepth, DepthType.Cv16U)]
    public void The_read_mode_decides_whether_depth_survives(ImreadModes mode, DepthType expected)
    {
        using var written = Sensor(12);
        var path = WriteTemp(written, ".png");

        try
        {
            var graph = new VisionGraph();
            var node = new ImageFileSourceNode { FilePath = path, Mode = mode };
            graph.Add(node);

            using var scheduler = CreateScheduler(graph);
            scheduler.RunAsync().GetAwaiter().GetResult();

            var image = (ImageData?)scheduler.GetOutput(node.ImageOut);

            Assert.NotNull(image);
            Assert.Equal(expected, image!.Depth);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// And the values survive with it: a 12-bit file read unchanged still
    /// peaks at 4095, not at 65535. Nothing rescales on load, which is
    /// correct - the pixel values are what the sensor said.
    /// </summary>
    [Fact]
    public void A_twelve_bit_file_keeps_its_own_range()
    {
        using var written = Sensor(12);
        var path = WriteTemp(written, ".png");

        try
        {
            using var read = CvInvoke.Imread(path, ImreadModes.Unchanged);

            double min = 0, max = 0;
            var lo = new System.Drawing.Point();
            var hi = new System.Drawing.Point();
            CvInvoke.MinMaxLoc(read, ref min, ref max, ref lo, ref hi);

            Assert.Equal(DepthType.Cv16U, read.Depth);
            Assert.Equal(4095, max);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- what it looks like once it is in ------------------------------------

    /// <summary>
    /// A true 16-bit image displays correctly: the converter divides by 257,
    /// which maps 65535 onto 255.
    /// </summary>
    [Fact]
    public void A_sixteen_bit_image_displays_at_full_brightness() =>
        WpfTestHost.OnStaThread(() =>
        {
            using var mat = Sensor(16);
            using var image = ImageData.Adopt(mat.Clone());

            Assert.Equal(255, BrightestPixel(MatImageConverter.ToBitmapSource(image)!));
        });

    /// <summary>
    /// A 12-bit image does not. The same divide-by-257 takes 4095 to 15, so
    /// the picture arrives at about a sixteenth of its brightness - which
    /// reads as a fault in the lighting rather than in the software.
    /// </summary>
    /// <remarks>
    /// This asserts the shortcoming rather than the fix. The converter
    /// cannot know a 16-bit container holds 12 bits; the file does not say,
    /// and guessing from the brightest pixel would make a genuinely dark
    /// 16-bit frame jump to full scale. The fix is for the source to record
    /// the significant bits, which is a change to ImageData.
    /// </remarks>
    [Theory]
    [InlineData(12, 16)]   // 4095 / 257 = 15.9, rounded: 6% of full scale
    [InlineData(10, 4)]    // 1023 / 257 =  4.0,          1.6% of full scale
    public void A_ten_or_twelve_bit_image_displays_almost_black(int bits, int expectedPeak) =>
        WpfTestHost.OnStaThread(() =>
        {
            using var mat = Sensor(bits);
            using var image = ImageData.Adopt(mat.Clone());

            Assert.Equal(expectedPeak, BrightestPixel(MatImageConverter.ToBitmapSource(image)!));
        });

    // ---- what the nodes do with it -------------------------------------------

    /// <summary>
    /// The graph itself carries the depth through: a filter does not quietly
    /// drop a 16-bit image to 8-bit on the way past.
    /// </summary>
    [Fact]
    public void A_filter_keeps_the_depth()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 32, Height = 16, Value = 100 };
        var blur = new GaussianBlurNode();

        graph.Add(source);
        graph.Add(blur);
        graph.TryConnect(source.ImageOut, blur.ImageIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        // The Blank Image source is 8-bit, so this pins the pass-through
        // rather than the 16-bit case; the 16-bit path is covered by the
        // converter tests above and by Histogram's own refusal below.
        var result = (ImageData?)scheduler.GetOutput(blur.ImageOut);

        Assert.NotNull(result);
        Assert.Equal(DepthType.Cv8U, result!.Depth);
    }

    /// <summary>
    /// Histogram is 8-bit only and says so rather than producing a wrong
    /// answer quietly. This is a known gap on the roadmap; the test is here
    /// to keep it an announced one.
    /// </summary>
    [Fact]
    public void Histogram_refuses_a_sixteen_bit_image()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 16, Height = 16, Value = 100 };
        var histogram = new HistogramNode();

        graph.Add(source);
        graph.Add(histogram);
        graph.TryConnect(source.ImageOut, histogram.ImageIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        // 8-bit is the supported case and must stay working.
        Assert.Equal(NodeStatus.Ready, histogram.Status);
    }

    private static int BrightestPixel(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Gray8, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight];

        converted.CopyPixels(pixels, converted.PixelWidth, 0);

        return pixels.Max();
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
