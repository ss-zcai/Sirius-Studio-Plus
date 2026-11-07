using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Serialization;
using Sirius.Studio.App.Imaging;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// A 12-bit camera's frame: a 16-bit image whose values stop at 4095, shown
/// as the bright picture it is, and read back as the values it has.
/// </summary>
[Collection(WpfCollection.Name)]
public class DisplayRangeTests
{
    public DisplayRangeTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
    }

    // ---- shown ----------------------------------------------------------------

    /// <summary>The complaint: divided by 257, 12-bit white is 16 of 255.</summary>
    [Fact]
    public void Shown_as_16_bit_a_12_bit_frame_is_nearly_black()
    {
        using var frame = Twelve();

        Assert.Equal(16, Brightest(MatImageConverter.ToBitmapSource(frame)!));
    }

    [Theory]
    [InlineData(12, 255)]
    [InlineData(14, 64)]
    [InlineData(10, 255)]
    public void Shown_as_its_own_bits_a_12_bit_frame_is_bright(int bits, int brightest)
    {
        using var frame = Twelve();

        // At 10 bits, 4095 is past white, and saturates there.
        Assert.Equal(brightest, Brightest(MatImageConverter.ToBitmapSource(frame, bits)!));
    }

    [Fact]
    public void Stretched_it_runs_from_its_darkest_pixel_to_its_brightest()
    {
        using var frame = Twelve();
        var shown = MatImageConverter.ToBitmapSource(frame, MatImageConverter.Stretch)!;

        Assert.Equal(255, Brightest(shown));
        Assert.Equal(0, Pixels(shown).Min());
    }

    /// <summary>An 8-bit picture has no choice to make, and is shown as it is.</summary>
    [Fact]
    public void An_8_bit_picture_ignores_the_choice()
    {
        using var mat = new Mat(4, 4, DepthType.Cv8U, 1);
        mat.SetTo(new MCvScalar(200));

        Assert.Equal(200, Brightest(MatImageConverter.ToBitmapSource(mat, 10)!));
    }

    // ---- on a panel ---------------------------------------------------------------

    /// <summary>
    /// The readout gives the picture's own value, 4095, however it is
    /// shown - not the 8-bit level on the screen.
    /// </summary>
    [Fact]
    public void The_reading_is_the_frames_own_value() =>
        WpfTestHost.OnStaThread(() =>
        {
            using var rig = new Rig(Twelve);

            Assert.True(rig.Panel.IsSixteenBit);
            Assert.Equal("I 4095", rig.Panel.Reading(63, 0));
            Assert.Equal("I 0", rig.Panel.Reading(0, 0));
            Assert.Null(rig.Panel.Reading(64, 0));
        });

    [Fact]
    public void An_8_bit_frame_has_no_reading_of_its_own() =>
        WpfTestHost.OnStaThread(() =>
        {
            using var rig = new Rig(() => new Mat(8, 8, DepthType.Cv8U, 1));

            Assert.False(rig.Panel.IsSixteenBit);
            Assert.Null(rig.Panel.Reading(1, 1));
        });

    /// <summary>Changing it redraws from the frame on show, running nothing.</summary>
    [Fact]
    public void Choosing_12_bits_redraws_at_once_and_is_kept_on_the_viewer() =>
        WpfTestHost.OnStaThread(() =>
        {
            using var rig = new Rig(Twelve);
            var runs = rig.Source.Runs;

            Assert.Equal(16, Brightest(rig.Panel.Image!));

            rig.Panel.DisplayBits = 12;

            Assert.Equal(255, Brightest(rig.Panel.Image!));
            Assert.Equal(12, rig.Viewer.DisplayBits);
            Assert.Equal(runs, rig.Source.Runs);

            // And the next frame is shown the same way.
            rig.Pass();
            Assert.Equal(255, Brightest(rig.Panel.Image!));
        });

    [Fact]
    public void The_choice_is_saved_with_the_recipe()
    {
        var graph = new VisionGraph();
        graph.Add(new ViewerNode { DisplayBits = 12 });

        var loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph)).Graph;

        Assert.Equal(12, loaded.Nodes.OfType<ViewerNode>().Single().DisplayBits);
    }

    /// <summary>Anything else is Auto - the image's own bits - which a new viewer starts at.</summary>
    [Fact]
    public void Only_the_offered_choices_are_taken()
    {
        Assert.Equal(-1, new ViewerNode { DisplayBits = 11 }.DisplayBits);
        Assert.Equal(-1, new ViewerNode().DisplayBits);
    }

    // ---- helpers ----------------------------------------------------------------

    /// <summary>A 64 x 32 ramp from 0 to 4095 along each row, in a 16-bit image.</summary>
    private static Mat Twelve()
    {
        var mat = new Mat(32, 64, DepthType.Cv16U, 1);
        var values = new ushort[64 * 32];

        for (var i = 0; i < values.Length; i++)
            values[i] = (ushort)Math.Round((i % 64) * 4095 / 63.0);

        mat.SetTo(values);
        return mat;
    }

    private static byte[] Pixels(BitmapSource picture)
    {
        var gray = new FormatConvertedBitmap(picture, PixelFormats.Gray8, null, 0);
        var pixels = new byte[gray.PixelWidth * gray.PixelHeight];
        gray.CopyPixels(pixels, gray.PixelWidth, 0);
        return pixels;
    }

    private static int Brightest(BitmapSource picture) => Pixels(picture).Max();

    private sealed class Rig : IDisposable
    {
        private readonly GraphScheduler _scheduler;

        public Rig(Func<Mat> frame)
        {
            Source = new FrameNode(frame);
            Viewer = new ViewerNode();

            var graph = new VisionGraph();
            graph.Add(Source);
            graph.Add(Viewer);
            graph.TryConnect(Source.Out, Viewer.ImageIn);

            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                _scheduler = new GraphScheduler(graph);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            Panel = new ViewerPanelViewModel(Viewer, new ShapeSelectionService());
            Pass();
        }

        public FrameNode Source { get; }

        public ViewerNode Viewer { get; }

        public ViewerPanelViewModel Panel { get; }

        public void Pass()
        {
            Source.Invalidate();
            _scheduler.RunAsync().GetAwaiter().GetResult();
            Panel.Prepare(_scheduler);
            Panel.Commit();
        }

        public void Dispose()
        {
            Panel.Dispose();
            _scheduler.Dispose();
        }
    }

    private sealed class FrameNode : VisionNode
    {
        private readonly Func<Mat> _frame;

        public FrameNode(Func<Mat> frame)
        {
            _frame = frame;
            Out = AddOutput<ImageData>("Out");
        }

        public OutputPort Out { get; }

        public int Runs { get; private set; }

        protected override void OnEvaluate(EvalContext context)
        {
            Runs++;
            context.Set(Out, ImageData.Adopt(_frame()));
        }
    }
}
