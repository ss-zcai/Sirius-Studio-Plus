using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;
using ZXing;
using ZXing.Common;

namespace Sirius.Core.Tests;

/// <summary>
/// Reading what is printed on a part.
/// </summary>
/// <remarks>
/// Both earlier builds left this as a stub - the old Barcode node
/// returned a hardcoded string - so every test here decodes a code that
/// was really drawn, and the payload asserted is one that had to survive
/// encoding, rendering, rotation and decoding to come back.
/// </remarks>
public class ReadCodeTests
{
    public ReadCodeTests() => TestCatalog.Ensure();

    /// <summary>
    /// Draws a real code onto a grey frame at a given place and angle.
    /// </summary>
    private static ImageData Frame(
        string payload, BarcodeFormat format, int width = 640, int height = 480,
        int codeWidth = 200, int codeHeight = 200, double angle = 0,
        int centreX = 320, int centreY = 240)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = format,
            Options = new EncodingOptions
            {
                Width = codeWidth,
                Height = codeHeight,
                Margin = 4,
                PureBarcode = true,
            },
        };

        var drawn = writer.Write(payload);

        // The writer gives BGRA; the frame is grey, which is what a camera
        // would hand over.
        using var code = new Mat(drawn.Height, drawn.Width, DepthType.Cv8U, 4);
        code.SetTo(new MCvScalar(255, 255, 255, 255));
        System.Runtime.InteropServices.Marshal.Copy(
            drawn.Pixels, 0, code.DataPointer, drawn.Pixels.Length);

        using var codeGrey = new Mat();
        CvInvoke.CvtColor(code, codeGrey, ColorConversion.Bgra2Gray);

        var frame = new Mat(height, width, DepthType.Cv8U, 1);
        frame.SetTo(new MCvScalar(255));

        // Place it, then turn the whole frame about the same centre, which
        // puts the code at an angle without any edge artefacts around it.
        var left = centreX - (codeGrey.Width / 2);
        var top = centreY - (codeGrey.Height / 2);

        using (var destination = new Mat(
            frame, new System.Drawing.Rectangle(left, top, codeGrey.Width, codeGrey.Height)))
        {
            codeGrey.CopyTo(destination);
        }

        if (Math.Abs(angle) > 1e-9)
        {
            using var affine = new Matrix<double>(2, 3);
            CvInvoke.GetRotationMatrix2D(
                new System.Drawing.PointF(centreX, centreY), angle, 1.0, affine);

            var turned = new Mat();
            CvInvoke.WarpAffine(
                frame, turned, affine, frame.Size, Inter.Cubic, Warp.Default,
                BorderType.Constant, new MCvScalar(255));

            frame.Dispose();
            frame = turned;
        }

        return ImageData.Adopt(frame, ColourSpace.Gray);
    }

    /// <summary>Publishes a prepared frame.</summary>
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

    private static (string Text, int Count, ShapeSet Codes) Run(
        ImageData image, Action<ReadCodeNode>? configure = null)
    {
        var graph = new VisionGraph();
        var source = new FrameNode(image);
        var reader = new ReadCodeNode();
        configure?.Invoke(reader);

        graph.Add(source);
        graph.Add(reader);
        graph.TryConnect(source.ImageOut, reader.ImageIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        return (
            (string)scheduler.GetOutput(reader.TextOut)!,
            (int)scheduler.GetOutput(reader.CountOut)!,
            (ShapeSet)scheduler.GetOutput(reader.CodesOut)!);
    }

    // ---- the symbologies -------------------------------------------------

    [Theory]
    [InlineData(BarcodeFormat.QR_CODE, "SIRIUS-1234")]
    [InlineData(BarcodeFormat.DATA_MATRIX, "DMX-9876")]
    [InlineData(BarcodeFormat.AZTEC, "AZ-55")]
    [InlineData(BarcodeFormat.PDF_417, "PDF417-TEST")]
    public void It_reads_a_two_dimensional_code(BarcodeFormat format, string payload)
    {
        using var image = Frame(payload, format);

        var (text, count, _) = Run(image);

        Assert.Equal(payload, text);
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData(BarcodeFormat.CODE_128, "ABC-123456")]
    [InlineData(BarcodeFormat.CODE_39, "PART9")]
    [InlineData(BarcodeFormat.ITF, "12345678")]
    public void It_reads_a_one_dimensional_barcode(BarcodeFormat format, string payload)
    {
        using var image = Frame(payload, format, codeWidth: 400, codeHeight: 120);

        var (text, count, _) = Run(image, node => node.Region = new OrientedRect(320, 240, 500, 220, 0));

        Assert.Equal(payload, text);
        Assert.Equal(1, count);
    }

    /// <summary>
    /// A retail code, which is the whole of what OpenCV's own barcode
    /// module can read and the reason the decoder is not OpenCV's.
    /// </summary>
    [Fact]
    public void It_reads_a_retail_code()
    {
        using var image = Frame("9781234567897", BarcodeFormat.EAN_13, codeWidth: 400, codeHeight: 120);

        var (text, _, _) = Run(image, node => node.Region = new OrientedRect(320, 240, 500, 220, 0));

        Assert.Equal("9781234567897", text);
    }

    // ---- the region ------------------------------------------------------

    /// <summary>
    /// A code outside the region is not read. That is the point of having
    /// one: the label on the box behind the part is not the part's.
    /// </summary>
    [Fact]
    public void A_code_outside_the_region_is_not_read()
    {
        using var image = Frame("SIRIUS-1234", BarcodeFormat.QR_CODE, centreX: 500, centreY: 360);

        var (text, count, _) = Run(image, node => node.Region = new OrientedRect(120, 120, 200, 200, 0));

        Assert.Equal(string.Empty, text);
        Assert.Equal(0, count);
    }

    [Fact]
    public void A_code_inside_the_region_is()
    {
        using var image = Frame("SIRIUS-1234", BarcodeFormat.QR_CODE, centreX: 500, centreY: 360);

        var (text, _, _) = Run(image, node => node.Region = new OrientedRect(500, 360, 260, 260, 0));

        Assert.Equal("SIRIUS-1234", text);
    }

    /// <summary>
    /// A part lying at an angle. The region is turned to match and the
    /// crop is straightened before decoding, so this reads as easily as
    /// one square to the camera.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(-45)]
    public void It_reads_a_code_on_a_turned_part(double angle)
    {
        using var image = Frame("TURNED-7", BarcodeFormat.QR_CODE, angle: angle);

        var (text, _, _) = Run(image, node => node.Region = new OrientedRect(320, 240, 260, 260, angle));

        Assert.Equal("TURNED-7", text);
    }

    // ---- what it draws ---------------------------------------------------

    /// <summary>
    /// The outline lands on the code in the original picture, not in the
    /// straightened crop the decoder saw.
    /// </summary>
    [Fact]
    public void The_outline_is_drawn_where_the_code_is()
    {
        using var image = Frame("SIRIUS-1234", BarcodeFormat.QR_CODE, centreX: 200, centreY: 300);

        var (_, _, codes) = Run(image, node => node.Region = new OrientedRect(200, 300, 260, 260, 0));

        var outline = Assert.Single(codes.Shapes);
        var centre = ShapeGeometry.Centre(outline);

        // Within half the code's width of where it was drawn: the corners
        // ZXing reports are the finder patterns, not the paper edge.
        Assert.True(Math.Abs(centre.X - 200) < 50, $"centre X was {centre.X}");
        Assert.True(Math.Abs(centre.Y - 300) < 50, $"centre Y was {centre.Y}");
    }

    [Fact]
    public void The_outline_is_labelled_with_what_was_read()
    {
        using var image = Frame("SIRIUS-1234", BarcodeFormat.QR_CODE);

        var (_, _, codes) = Run(image);

        Assert.Equal("SIRIUS-1234", codes.Shapes[0].Label);
    }

    [Fact]
    public void The_label_can_be_left_off()
    {
        using var image = Frame("SIRIUS-1234", BarcodeFormat.QR_CODE);

        var (_, _, codes) = Run(image, node => node.ShowText = false);

        Assert.Null(codes.Shapes[0].Label);
    }

    /// <summary>
    /// The frame travels with the shapes, and it is the whole picture's -
    /// not the crop's - or a viewer would draw them in the wrong place.
    /// </summary>
    [Fact]
    public void The_codes_belong_to_the_whole_picture()
    {
        using var image = Frame("SIRIUS-1234", BarcodeFormat.QR_CODE);

        var (_, _, codes) = Run(image);

        Assert.Equal(640, codes.FrameWidth);
        Assert.Equal(480, codes.FrameHeight);
    }

    // ---- choosing a symbology --------------------------------------------

    /// <summary>
    /// Naming the symbology rules out a misread. A QR is not a Code 128,
    /// and a recipe that says so will not quietly accept one.
    /// </summary>
    [Fact]
    public void The_wrong_symbology_reads_nothing()
    {
        using var image = Frame("SIRIUS-1234", BarcodeFormat.QR_CODE);

        var (text, count, _) = Run(image, node => node.Symbology = CodeSymbology.Code128);

        Assert.Equal(string.Empty, text);
        Assert.Equal(0, count);
    }

    [Fact]
    public void The_right_symbology_reads_it()
    {
        using var image = Frame("SIRIUS-1234", BarcodeFormat.QR_CODE);

        var (text, _, _) = Run(image, node => node.Symbology = CodeSymbology.QrCode);

        Assert.Equal("SIRIUS-1234", text);
    }

    [Fact]
    public void Retail_covers_the_whole_ean_and_upc_family()
    {
        var formats = ReadCodeNode.FormatsFor(CodeSymbology.RetailEanUpc)!;

        Assert.Equal(
            [BarcodeFormat.EAN_13, BarcodeFormat.EAN_8, BarcodeFormat.UPC_A, BarcodeFormat.UPC_E],
            formats);
    }

    [Fact]
    public void Any_names_no_formats_at_all() =>
        Assert.Null(ReadCodeNode.FormatsFor(CodeSymbology.Any));

    /// <summary>
    /// Every choice but Any maps to something. A symbology added to the
    /// list and forgotten here would silently read everything.
    /// </summary>
    [Fact]
    public void Every_symbology_offered_means_something()
    {
        foreach (var symbology in Enum.GetValues<CodeSymbology>())
        {
            if (symbology == CodeSymbology.Any)
                continue;

            Assert.NotEmpty(ReadCodeNode.FormatsFor(symbology)!);
        }
    }

    // ---- nothing there ----------------------------------------------------

    [Fact]
    public void A_blank_frame_reads_nothing_rather_than_failing()
    {
        using var blank = new Mat(480, 640, DepthType.Cv8U, 1);
        blank.SetTo(new MCvScalar(255));

        using var image = ImageData.CopyOf(blank, ColourSpace.Gray);

        var (text, count, codes) = Run(image);

        Assert.Equal(string.Empty, text);
        Assert.Equal(0, count);
        Assert.Equal(0, codes.Count);
    }

    /// <summary>
    /// A count of nothing is what a Limit Check reads to fail the part,
    /// which is how a missing code becomes a reject.
    /// </summary>
    [Fact]
    public void A_missing_code_fails_a_limit_check()
    {
        var graph = new VisionGraph();

        using var blank = new Mat(480, 640, DepthType.Cv8U, 1);
        blank.SetTo(new MCvScalar(255));
        using var image = ImageData.CopyOf(blank, ColourSpace.Gray);

        var source = new FrameNode(image);
        var reader = new ReadCodeNode();
        var check = new LimitCheckNode { Kind = LimitKind.AtLeast, Minimum = 1 };

        foreach (var node in new VisionNode[] { source, reader, check })
            graph.Add(node);

        graph.TryConnect(source.ImageOut, reader.ImageIn);

        var value = check.Inputs.Single(p => p.Name == nameof(LimitCheckNode.Value));
        Assert.True(graph.TryConnect(reader.CountOut, value).Success);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.False((bool)scheduler.GetOutput(check.PassOut)!);
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
