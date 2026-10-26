using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.OCR;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Reading printed text off a part.
/// </summary>
/// <remarks>
/// Every test here draws real text onto a frame and reads it back, for
/// the same reason the code reader's do: the predecessors shipped this
/// as a stub, and a test that mocks the engine would pass against one.
/// </remarks>
public class ReadTextTests
{
    public ReadTextTests() => TestCatalog.Ensure();

    /// <summary>Draws text onto a white frame, optionally turned.</summary>
    private static ImageData Frame(
        string text, int width = 640, int height = 480, double scale = 2.0,
        int x = 120, int y = 260, double angle = 0, int thickness = 3)
    {
        var frame = new Mat(height, width, DepthType.Cv8U, 1);
        frame.SetTo(new MCvScalar(255));

        CvInvoke.PutText(
            frame, text, new System.Drawing.Point(x, y),
            FontFace.HersheySimplex, scale, new MCvScalar(0), thickness, LineType.AntiAlias);

        if (Math.Abs(angle) > 1e-9)
        {
            // Negated: OrientedRect measures clockwise, GetRotationMatrix2D
            // turns counter-clockwise, so a region at +10 matches text drawn
            // by rotating the frame by -10.
            using var affine = new Matrix<double>(2, 3);
            CvInvoke.GetRotationMatrix2D(
                new System.Drawing.PointF(width / 2f, height / 2f), -angle, 1.0, affine);

            var turned = new Mat();
            CvInvoke.WarpAffine(
                frame, turned, affine, frame.Size, Inter.Cubic, Warp.Default,
                BorderType.Constant, new MCvScalar(255));

            frame.Dispose();
            frame = turned;
        }

        return ImageData.Adopt(frame, ColourSpace.Gray);
    }

    private sealed class FrameNode : VisionNode
    {
        private readonly ImageData _image;

        public FrameNode(ImageData image)
        {
            _image = image;
            ImageOut = AddOutput<ImageData>("Out");
        }

        public OutputPort ImageOut { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(ImageOut, _image);
    }

    private static (string Text, double Confidence, ShapeSet Words) Run(
        ImageData image, Action<ReadTextNode>? configure = null)
    {
        var graph = new VisionGraph();
        var source = new FrameNode(image);
        var reader = new ReadTextNode { Region = new OrientedRect(320, 240, 620, 200, 0) };
        configure?.Invoke(reader);

        graph.Add(source);
        graph.Add(reader);
        graph.TryConnect(source.ImageOut, reader.ImageIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        return (
            (string)scheduler.GetOutput(reader.TextOut)!,
            (double)scheduler.GetOutput(reader.ConfidenceOut)!,
            (ShapeSet)scheduler.GetOutput(reader.WordsOut)!);
    }

    // ---- the language data ------------------------------------------------

    /// <summary>
    /// It ships with the build. Without it the node cannot do anything
    /// at all, so this failing is worth knowing before any of the rest.
    /// </summary>
    [Fact]
    public void The_language_data_is_installed_beside_the_build()
    {
        Assert.True(
            ReadTextNode.IsLanguageDataPresent,
            $"no eng.traineddata in {ReadTextNode.DataDirectory}");
    }

    // ---- reading ----------------------------------------------------------

    [Theory]
    [InlineData("HELLO")]
    [InlineData("PART123")]
    [InlineData("80108")]
    public void It_reads_what_is_printed(string printed)
    {
        using var image = Frame(printed);

        var (text, confidence, _) = Run(image, node => { });

        Assert.Equal(printed, text);
        Assert.True(confidence > 50, $"confidence was {confidence}");
    }

    /// <summary>
    /// A part lying at an angle. The region turns with it and the crop is
    /// squared up before reading, which is the whole reason the region is
    /// an oriented rectangle rather than a box.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(-10)]
    public void It_reads_text_on_a_turned_part(double angle)
    {
        using var image = Frame("TURNED", angle: angle);

        var (text, _, _) = Run(image, node =>
        {
            node.Region = new OrientedRect(320, 240, 620, 200, angle);
        });

        Assert.Equal("TURNED", text);
    }

    /// <summary>
    /// Small text is magnified before reading rather than the operator
    /// being told to move the camera.
    /// </summary>
    [Fact]
    public void It_reads_text_in_a_small_region()
    {
        using var image = Frame("SMALL", scale: 0.6, thickness: 1, x: 280, y: 250);

        var (text, _, _) = Run(image, node =>
        {
            node.Region = new OrientedRect(320, 240, 160, 30, 0);
        });

        Assert.Equal("SMALL", text);
    }

    // ---- narrowing the problem --------------------------------------------

    /// <summary>
    /// A part number of known alphabet is a far easier question than
    /// "what does this say": letters cannot be returned where only
    /// digits are possible.
    /// </summary>
    [Fact]
    public void A_reading_within_the_alphabet_is_kept()
    {
        using var image = Frame("80108");

        var (text, _, _) = Run(image, node => node.Allowed = "0123456789");

        Assert.Equal("80108", text);
    }

    /// <summary>
    /// Nothing printed is not a quiet empty string with a high score:
    /// the confidence is zero, which is what a Limit Check reads.
    /// </summary>
    [Fact]
    public void A_blank_region_reads_nothing_with_no_confidence()
    {
        using var blank = new Mat(480, 640, DepthType.Cv8U, 1);
        blank.SetTo(new MCvScalar(255));
        using var image = ImageData.CopyOf(blank, ColourSpace.Gray);

        var (text, confidence, words) = Run(image);

        Assert.Equal(string.Empty, text);
        Assert.Equal(0, confidence);
        Assert.Equal(0, words.Count);
    }

    /// <summary>
    /// Raising the bar past what the engine managed drops the read
    /// rather than reporting a guess as an answer.
    /// </summary>
    [Fact]
    public void A_read_below_the_bar_is_dropped()
    {
        using var image = Frame("HELLO");

        var (text, _, _) = Run(image, node =>
        {
            node.MinimumConfidence = 100;
        });

        Assert.Equal(string.Empty, text);
    }

    /// <summary>
    /// A doubtful read fails the part, which is the pair that makes OCR
    /// an inspection rather than a transcription.
    /// </summary>
    [Fact]
    public void Confidence_can_fail_a_limit_check()
    {
        var graph = new VisionGraph();

        using var blank = new Mat(480, 640, DepthType.Cv8U, 1);
        blank.SetTo(new MCvScalar(255));
        using var image = ImageData.CopyOf(blank, ColourSpace.Gray);

        var source = new FrameNode(image);
        var reader = new ReadTextNode { Region = new OrientedRect(320, 240, 620, 200, 0) };
        var check = new LimitCheckNode { Kind = LimitKind.AtLeast, Minimum = 60 };

        foreach (var node in new VisionNode[] { source, reader, check })
            graph.Add(node);

        graph.TryConnect(source.ImageOut, reader.ImageIn);

        var value = check.Inputs.Single(p => p.Name == nameof(LimitCheckNode.Value));
        Assert.True(graph.TryConnect(reader.ConfidenceOut, value).Success);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.False((bool)scheduler.GetOutput(check.PassOut)!);
    }

    // ---- what it draws ----------------------------------------------------

    /// <summary>
    /// The boxes land on the words in the original picture - which means
    /// both the magnification and the rotation have been undone.
    /// </summary>
    [Fact]
    public void Each_word_is_boxed_where_it_is_on_the_picture()
    {
        using var image = Frame("HELLO", x: 150, y: 260);

        var (_, _, words) = Run(image, node => { });

        var box = Assert.Single(words.Shapes);
        var bounds = box.Bounds;

        // PutText draws from the baseline at y, rightwards from x.
        Assert.True(bounds.X > 100 && bounds.X < 220, $"left was {bounds.X}");
        Assert.True(bounds.Y > 180 && bounds.Y < 265, $"top was {bounds.Y}");
    }

    [Fact]
    public void A_word_is_labelled_with_itself()
    {
        using var image = Frame("HELLO");

        var (_, _, words) = Run(image, node => { });

        Assert.Equal("HELLO", words.Shapes[0].Label);
    }

    [Fact]
    public void The_label_can_be_left_off()
    {
        using var image = Frame("HELLO");

        var (_, _, words) = Run(image, node =>
        {
            node.ShowText = false;
        });

        Assert.Null(words.Shapes[0].Label);
    }

    [Fact]
    public void A_word_carries_its_own_confidence()
    {
        using var image = Frame("HELLO");

        var (_, _, words) = Run(image, node => { });

        Assert.True(words.Shapes[0].Metrics.TryGetValue("Confidence", out var confidence));
        Assert.True(confidence > 50, $"confidence metric was {confidence}");
    }

    [Fact]
    public void The_words_belong_to_the_whole_picture()
    {
        using var image = Frame("HELLO");

        var (_, _, words) = Run(image, node => { });

        Assert.Equal(640, words.FrameWidth);
        Assert.Equal(480, words.FrameHeight);
    }

    // ---- the settings -----------------------------------------------------

    [Theory]
    [InlineData(TextLayout.Line, PageSegMode.SingleLine)]
    [InlineData(TextLayout.Character, PageSegMode.SingleChar)]
    [InlineData(TextLayout.Block, PageSegMode.SingleBlock)]
    [InlineData(TextLayout.Scattered, PageSegMode.SparseText)]
    public void Every_layout_names_a_mode(TextLayout layout, PageSegMode expected) =>
        Assert.Equal(expected, ReadTextNode.ModeFor(layout));

    /// <summary>
    /// Text already big enough is left alone; small text is magnified by
    /// a whole number, because resampling by 1.7 puts ringing on the
    /// strokes the engine is trying to read.
    /// </summary>
    [Theory]
    [InlineData(100, 1)]
    [InlineData(64, 1)]
    [InlineData(63, 2)]
    [InlineData(32, 2)]
    [InlineData(16, 4)]
    [InlineData(8, 8)]
    [InlineData(1, 8)]
    [InlineData(0, 8)]
    public void A_small_region_is_magnified_by_a_whole_number(int height, int expected) =>
        Assert.Equal(expected, ReadTextNode.ScaleFor(height));

    // ---- the alphabet, and why it is ours ---------------------------------

    [Theory]
    [InlineData("80108", "0123456789", true)]
    [InlineData("8O1O8", "0123456789", false)]
    [InlineData("PART 123", "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", true)]
    [InlineData("PART-123", "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", false)]
    [InlineData("anything", "", true)]
    [InlineData("anything", null, true)]
    public void An_allowed_set_accepts_or_rejects_a_whole_reading(
        string text, string? allowed, bool expected) =>
        Assert.Equal(expected, ReadTextNode.IsAllowed(text, allowed));

    /// <summary>
    /// Tesseract's own whitelist is ignored by the LSTM engine, which is
    /// why this is applied here. Recorded as a test so that if a future
    /// engine starts honouring it, the duplication is noticed rather
    /// than left to fight itself.
    /// </summary>
    [Fact]
    public void A_reading_outside_the_alphabet_is_dropped_rather_than_repaired()
    {
        using var image = Frame("PART123");

        var (text, _, words) = Run(image, node => node.Allowed = "0123456789");

        Assert.Equal(string.Empty, text);
        Assert.Equal(0, words.Count);
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
