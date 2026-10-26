using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using ZXing;
using ZXing.Common;

namespace Sirius.Vision.Nodes;

/// <summary>Which symbologies a Read Code node will accept.</summary>
/// <remarks>
/// A short list rather than every format the decoder knows. Naming the
/// symbology is worth doing because it rules out a misread - a smudged
/// Code 39 will not come back as an EAN-13 if only Code 39 is allowed -
/// and a list of twenty would make that choice harder rather than more
/// precise. Anything missing is still read under Any.
/// </remarks>
public enum CodeSymbology
{
    /// <summary>Whatever is there. Slower, and can misread a damaged code.</summary>
    Any,

    QrCode,
    DataMatrix,
    Aztec,
    Pdf417,
    Code128,
    Code39,
    Interleaved2Of5,
    Codabar,

    /// <summary>EAN-13, EAN-8, UPC-A and UPC-E together: retail codes.</summary>
    RetailEanUpc,
}

/// <summary>
/// Reads barcodes and 2D codes inside a region.
/// </summary>
/// <remarks>
/// <para>
/// Both earlier builds left this as a stub - the old Barcode node returned
/// a hardcoded string - which is why it was kept off the roadmap until
/// there was a real decoder behind it. There is one now: ZXing, which is
/// pure managed, permissively licensed, and reads the symbologies actually
/// printed on parts rather than only the retail ones.
/// </para>
/// <para>
/// Region-based, the way Find Line is. A code occupies a known part of a
/// fixtured image, and searching only there is both faster and far less
/// likely to come back with the label on the box behind the part. The
/// region is the same <see cref="OrientedRect"/> the ROI node uses, so it
/// can be drawn and turned with the machinery that already exists.
/// </para>
/// <para>
/// The region is straightened before decoding rather than the decoder
/// being asked to cope with the angle: a rotated crop is one warp of a
/// few hundred pixels, and it means a code on a part at 30 degrees reads
/// as easily as one square to the camera.
/// </para>
/// </remarks>
[VisionNode(
    "Read Code",
    TypeKey = "read.code",
    Category = NodeCategory.TextAndCodeReading,
    Icon = "ph_read_code",
    Description = "Reads barcodes and 2D codes inside a region.")]
public sealed class ReadCodeNode : VisionNode
{
    /// <summary>Green, as a found feature is drawn.</summary>
    private const uint Found = 0xFF22C55E;

    private OrientedRect _region = new(320, 240, 400, 240, 0);
    private CodeSymbology _symbology = CodeSymbology.Any;
    private bool _readAll;
    private bool _tryHarder = true;
    private bool _showText = true;

    public ReadCodeNode()
    {
        ImageIn = AddInput<ImageData>("In");

        TextOut = AddOutput<string>("Text");
        CodesOut = AddOutput<ShapeSet>("Codes");
        CountOut = AddOutput<int>("Count");
    }

    public InputPort ImageIn { get; }

    /// <summary>The first code read, or empty when nothing was found.</summary>
    public OutputPort TextOut { get; }

    /// <summary>One outline per code, labelled with what it said.</summary>
    public OutputPort CodesOut { get; }

    /// <summary>
    /// How many were read. Wire it into a Limit Check set to At Least 1
    /// and a missing code fails the part.
    /// </summary>
    public OutputPort CountOut { get; }

    [Param("Region",
        Editor = ParamEditor.Rect,
        Connectable = true,
        Description = "Where to look, as x, y, w, h, angle. Draw it on the image.")]
    public OrientedRect Region
    {
        get => _region;
        set => SetParam(ref _region, value);
    }

    [Param("Symbology", Description = "Which kind of code to accept. Any is slower.")]
    public CodeSymbology Symbology
    {
        get => _symbology;
        set => SetParam(ref _symbology, value);
    }

    [Param("Read All", Group = "Reading",
        Description = "Read every code in the region rather than stopping at the first.")]
    public bool ReadAll
    {
        get => _readAll;
        set => SetParam(ref _readAll, value);
    }

    [Param("Try Harder", Group = "Reading",
        Description = "Spend longer on a poor image. Off is faster and stricter.")]
    public bool TryHarder
    {
        get => _tryHarder;
        set => SetParam(ref _tryHarder, value);
    }

    [Param("Label", Group = "Reading", Description = "Draw what was read beside each code.")]
    public bool ShowText
    {
        get => _showText;
        set => SetParam(ref _showText, value);
    }

    /// <summary>The ZXing formats a symbology choice allows, or null for all.</summary>
    internal static IList<BarcodeFormat>? FormatsFor(CodeSymbology symbology) => symbology switch
    {
        CodeSymbology.QrCode => [BarcodeFormat.QR_CODE],
        CodeSymbology.DataMatrix => [BarcodeFormat.DATA_MATRIX],
        CodeSymbology.Aztec => [BarcodeFormat.AZTEC],
        CodeSymbology.Pdf417 => [BarcodeFormat.PDF_417],
        CodeSymbology.Code128 => [BarcodeFormat.CODE_128],
        CodeSymbology.Code39 => [BarcodeFormat.CODE_39],
        CodeSymbology.Interleaved2Of5 => [BarcodeFormat.ITF],
        CodeSymbology.Codabar => [BarcodeFormat.CODABAR],
        CodeSymbology.RetailEanUpc =>
        [
            BarcodeFormat.EAN_13, BarcodeFormat.EAN_8,
            BarcodeFormat.UPC_A, BarcodeFormat.UPC_E,
        ],
        _ => null,
    };

    protected override void OnEvaluate(EvalContext context)
    {
        var source = context.Get<ImageData>(ImageIn)!;
        var region = context.Param(nameof(Region), _region);

        using var straightened = Straighten(source, region);
        using var grey = AsGrey(straightened);

        var results = Decode(grey);

        if (results.Length == 0)
        {
            context.Set(TextOut, string.Empty);
            context.Set(CodesOut, ShapeSet.Empty);
            context.Set(CountOut, 0);
            return;
        }

        context.Set(TextOut, results[0].Text ?? string.Empty);
        context.Set(CountOut, results.Length);
        context.Set(CodesOut, Outline(results, region, grey.Size, source));
    }

    /// <summary>
    /// Decodes, reading one code or every one depending on the setting.
    /// </summary>
    private Result[] Decode(Mat grey)
    {
        var pixels = new byte[grey.Width * grey.Height];
        CopyInto(grey, pixels);

        var luminance = new RGBLuminanceSource(
            pixels, grey.Width, grey.Height, RGBLuminanceSource.BitmapFormat.Gray8);

        var reader = new BarcodeReaderGeneric
        {
            // A code is as likely to be upside down on a part as the right
            // way up, and nothing about which is knowable from the recipe.
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = _tryHarder,
                PossibleFormats = FormatsFor(_symbology),
            },
        };

        if (!_readAll)
            return reader.Decode(luminance) is { } one ? [one] : [];

        return reader.DecodeMultiple(luminance) ?? [];
    }

    /// <summary>
    /// Rotates the region upright into an image of exactly its size.
    /// </summary>
    /// <remarks>
    /// The same trick the ROI node uses: the affine goes straight from the
    /// source to the output, so a region on a large frame costs a warp of
    /// its own area rather than of the whole picture.
    /// </remarks>
    private static Mat Straighten(ImageData source, OrientedRect region)
    {
        var width = Math.Max(1, (int)Math.Round(region.Width));
        var height = Math.Max(1, (int)Math.Round(region.Height));

        using var affine = Transform(region, width, height);

        var destination = new Mat();

        try
        {
            CvInvoke.WarpAffine(
                source.Mat,
                destination,
                affine,
                new System.Drawing.Size(width, height),
                Inter.Linear,
                Warp.Default,
                BorderType.Replicate,
                default);

            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    /// <summary>The source-to-region affine, as a 2x3.</summary>
    private static Matrix<double> Transform(OrientedRect region, int width, int height)
    {
        var centre = new System.Drawing.PointF((float)region.CentreX, (float)region.CentreY);
        var affine = new Matrix<double>(2, 3);

        CvInvoke.GetRotationMatrix2D(centre, region.Angle, 1.0, affine);

        affine.Data[0, 2] += (width / 2.0) - region.CentreX;
        affine.Data[1, 2] += (height / 2.0) - region.CentreY;

        return affine;
    }

    /// <summary>Eight-bit single channel, which is what the decoder reads.</summary>
    private static Mat AsGrey(Mat source)
    {
        var grey = new Mat();

        try
        {
            if (source.NumberOfChannels > 1)
                CvInvoke.CvtColor(source, grey, ColorConversion.Bgr2Gray);
            else
                source.CopyTo(grey);

            if (grey.Depth == DepthType.Cv8U)
                return grey;

            // A 16-bit frame carries the same code; the decoder wants it at
            // eight bits, and the scale is the one the viewer uses.
            var narrowed = new Mat();
            grey.ConvertTo(narrowed, DepthType.Cv8U, 1.0 / 257.0);
            grey.Dispose();

            return narrowed;
        }
        catch
        {
            grey.Dispose();
            throw;
        }
    }

    /// <summary>Copies a Mat's pixels out row by row, respecting its stride.</summary>
    private static void CopyInto(Mat grey, byte[] destination)
    {
        var step = grey.Step;
        var width = grey.Width;

        for (var row = 0; row < grey.Height; row++)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                grey.DataPointer + (row * step), destination, row * width, width);
        }
    }

    /// <summary>
    /// The codes drawn where they are in the original picture.
    /// </summary>
    /// <remarks>
    /// ZXing reports corners in the straightened image, which is not where
    /// anybody is looking. Mapping them back through the inverse of the
    /// warp puts the outline on the code itself, at whatever angle the
    /// part happens to be lying.
    /// </remarks>
    private ShapeSet Outline(
        Result[] results, OrientedRect region, System.Drawing.Size size, ImageData source)
    {
        using var forward = Transform(region, size.Width, size.Height);
        using var inverse = new Matrix<double>(2, 3);
        CvInvoke.InvertAffineTransform(forward, inverse);

        var shapes = new List<Shape>(results.Length);

        foreach (var result in results)
        {
            var points = result.ResultPoints ?? [];

            // A 1D symbology reports two points - the ends of the scan -
            // which is a line rather than an outline, and still says where
            // the code is. Fewer than two says nothing, so the region
            // itself stands in.
            var mapped = points.Length >= 2
                ? points.Select(p => Map(inverse, p.X, p.Y)).ToArray()
                : Corners(region);

            shapes.Add(new PolygonShape(mapped, Closed: mapped.Length > 2)
            {
                Colour = Found,
                StrokeWidth = 2,
                Label = _showText ? result.Text : null,
            });
        }

        return new ShapeSet(shapes, source.Width, source.Height);
    }

    private static Point2 Map(Matrix<double> inverse, double x, double y) => new(
        (inverse.Data[0, 0] * x) + (inverse.Data[0, 1] * y) + inverse.Data[0, 2],
        (inverse.Data[1, 0] * x) + (inverse.Data[1, 1] * y) + inverse.Data[1, 2]);

    /// <summary>The region's own four corners, in image coordinates.</summary>
    private static Point2[] Corners(OrientedRect region)
    {
        var radians = region.Angle * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        var halfWidth = region.Width / 2;
        var halfHeight = region.Height / 2;

        return new[]
        {
            (-halfWidth, -halfHeight), (halfWidth, -halfHeight),
            (halfWidth, halfHeight), (-halfWidth, halfHeight),
        }
        .Select(corner => new Point2(
            region.CentreX + (corner.Item1 * cos) - (corner.Item2 * sin),
            region.CentreY + (corner.Item1 * sin) + (corner.Item2 * cos)))
        .ToArray();
    }
}
