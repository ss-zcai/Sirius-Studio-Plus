using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

[VisionNode(
    "Threshold",
    TypeKey = "basic.threshold",
    Category = NodeCategory.Basic,
    Icon = "ph_threshold",
    Description = "Fixed-level thresholding.")]
public sealed class ThresholdNode : ImageFilterNode
{
    private double _threshold = 128;
    private double _maxValue = 255;
    private ThresholdType _thresholdType = ThresholdType.Binary;

    // Range lives here rather than in a UI switch on the property name, which
    // is how the old property grid decided to show a 0-255 trackbar.
    // Connectable: a wire overrides this value, so the level can come from a
    // measurement instead of being fixed.
    [Param("Threshold", Min = 0, Max = 255, Step = 1, Intensity = true, Editor = ParamEditor.Slider, Connectable = true)]
    public double Threshold
    {
        get => _threshold;
        set => SetParam(ref _threshold, value);
    }

    [Param("Max Value", Min = 0, Max = 255, Step = 1, Intensity = true, Editor = ParamEditor.Slider)]
    public double MaxValue
    {
        get => _maxValue;
        set => SetParam(ref _maxValue, value);
    }

    [Param("Type")]
    public ThresholdType ThresholdType
    {
        get => _thresholdType;
        set => SetParam(ref _thresholdType, value);
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context)
    {
        var level = context.Param(nameof(Threshold), _threshold);
        CvInvoke.Threshold(source.Mat, destination, level, _maxValue, _thresholdType);
    }
}

[VisionNode(
    "Adaptive Threshold",
    TypeKey = "basic.adaptiveThreshold",
    Category = NodeCategory.Basic,
    Icon = "ph_adaptive_threshold",
    Description = "Thresholding with a locally computed level. Requires a single-channel input.")]
public sealed class AdaptiveThresholdNode : ImageFilterNode
{
    private double _maxValue = 255;
    private AdaptiveThresholdType _adaptiveType = AdaptiveThresholdType.GaussianC;
    private ThresholdType _thresholdType = ThresholdType.Binary;
    private int _blockSize = 19;
    private double _constant = 11;

    [Param("Max Value", Min = 0, Max = 255, Step = 1, Intensity = true, Editor = ParamEditor.Slider)]
    public double MaxValue
    {
        get => _maxValue;
        set => SetParam(ref _maxValue, value);
    }

    [Param("Adaptive Method")]
    public AdaptiveThresholdType AdaptiveType
    {
        get => _adaptiveType;
        set => SetParam(ref _adaptiveType, value);
    }

    [Param("Type")]
    public ThresholdType ThresholdType
    {
        get => _thresholdType;
        set => SetParam(ref _thresholdType, value);
    }

    /// <summary>
    /// Neighbourhood size. OpenCV requires an odd value greater than 1;
    /// <see cref="ParamAttribute.OddOnly"/> lets the editor enforce that instead
    /// of each setter hand-rolling the snapping logic as the old tool did.
    /// </summary>
    [Param("Block Size", Min = 3, Max = 255, Step = 2, OddOnly = true, Editor = ParamEditor.Slider)]
    public int BlockSize
    {
        get => _blockSize;
        set => SetParam(ref _blockSize, MakeOdd(value, _blockSize, minimum: 3));
    }

    [Param("Constant", Description = "Subtracted from the local mean.", Min = -50, Max = 50, Intensity = true, Editor = ParamEditor.Slider)]
    public double Constant
    {
        get => _constant;
        set => SetParam(ref _constant, value);
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context)
    {
        if (source.Channels != 1)
        {
            throw new InvalidOperationException(
                $"Adaptive Threshold needs a single-channel image but received {source.Channels} channels. " +
                "Insert a Colour Convert node set to BGR to Gray.");
        }

        if (source.Depth == DepthType.Cv8U)
        {
            CvInvoke.AdaptiveThreshold(
                source.Mat, destination, _maxValue, _adaptiveType, _thresholdType, _blockSize, _constant);
            return;
        }

        // OpenCV thresholds adaptively on 8 bits only. A deeper image is
        // scaled down by its own depth, and the settings - in its own
        // values - with it; the result is an 8-bit mask, as Find Blobs reads.
        var scale = 255.0 / source.MaxValue;
        using var narrowed = source.ToEightBit();

        CvInvoke.AdaptiveThreshold(
            narrowed,
            destination,
            Math.Clamp(_maxValue * scale, 0, 255),
            _adaptiveType,
            _thresholdType,
            _blockSize,
            _constant * scale);
    }

    /// <summary>
    /// Snaps to the nearest odd value, moving in the direction of travel so a
    /// spinner or slider does not stall on an even step.
    /// </summary>
    internal static int MakeOdd(int value, int previous, int minimum)
    {
        if (value % 2 == 0)
            value += value > previous ? 1 : -1;

        return Math.Max(minimum, value);
    }
}

[VisionNode(
    "Colour Convert",
    TypeKey = "basic.colourConvert",
    Category = NodeCategory.Basic,
    Icon = "ph_colour_convert",
    Description = "Converts between colour spaces.")]
public sealed class ColourConvertNode : ImageFilterNode
{
    private ColorConversion _conversion = ColorConversion.Bgr2Gray;

    [Param("Conversion")]
    public ColorConversion Conversion
    {
        get => _conversion;
        set => SetParam(ref _conversion, value);
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context) =>
        CvInvoke.CvtColor(source.Mat, destination, _conversion);

    /// <summary>
    /// The whole point of this node, and the one place the engine can know
    /// it. Conversions it does not recognise - most of them are Bayer
    /// patterns - fall back to the channel count rather than a guess.
    /// </summary>
    protected override ColourSpace SpaceOf(ImageData source, Mat destination) =>
        ColourSpaces.ResultOf(_conversion)
        ?? ColourSpaces.FromChannelCount(destination.NumberOfChannels);
}

[VisionNode(
    "Brightness / Contrast",
    TypeKey = "basic.brightnessContrast",
    Category = NodeCategory.Basic,
    Icon = "ph_brightness",
    Description = "Linear intensity adjustment.")]
public sealed class BrightnessContrastNode : ImageFilterNode
{
    private int _brightness;
    private int _contrast;

    [Param("Brightness", Min = -100, Max = 100, Intensity = true, Editor = ParamEditor.Slider, Connectable = true)]
    public int Brightness
    {
        get => _brightness;
        set => SetParam(ref _brightness, value);
    }

    [Param("Contrast", Min = -100, Max = 100, Editor = ParamEditor.Slider, Connectable = true)]
    public int Contrast
    {
        get => _contrast;
        set => SetParam(ref _contrast, value);
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context)
    {
        var alpha = (context.Param(nameof(Contrast), _contrast) / 100.0) + 1.0;
        double beta = context.Param(nameof(Brightness), _brightness);

        // ConvertTo keeps the source depth and saturates, where the old
        // tool's `image * gain + offset` expression promoted the result and
        // could wrap on 8-bit input.
        source.Mat.ConvertTo(destination, source.Depth, alpha, beta);
    }
}

/// <summary>How Resize is told what size to make.</summary>
public enum ResizeBy
{
    /// <summary>By a factor along each axis.</summary>
    Scale,

    /// <summary>To a width and a height in pixels.</summary>
    [System.ComponentModel.Description("Size in pixels")]
    Size,
}

[VisionNode(
    "Resize",
    TypeKey = "basic.resize",
    Category = NodeCategory.Basic,
    Icon = "ph_resize",
    Description = "Scales the image, by a factor or to a width and height in pixels. Shapes wired into Overlay come " +
                  "out scaled with it.")]
public sealed class ResizeNode : ImageFilterNode
{
    /// <summary>
    /// The largest image made to a size, in pixels. A width and a height
    /// typed with a nought too many would otherwise ask for gigabytes.
    /// </summary>
    internal const long MaximumPixels = 500_000_000;

    private ResizeBy _resizeBy = ResizeBy.Scale;
    private double _scaleX = 1;
    private double _scaleY = 1;
    private int _width;
    private int _height;
    private Inter _interpolation = Inter.Linear;

    public ResizeNode()
    {
        OverlayIn = AddInput<ShapeSet>("Overlay", isRequired: false);
        OverlayOut = AddOutput<ShapeSet>("Overlay");
    }

    /// <summary>Shapes found on the input image, to be placed on the resized one.</summary>
    public InputPort OverlayIn { get; }

    /// <summary>The same shapes, scaled with the image.</summary>
    public OutputPort OverlayOut { get; }

    [Param("Resize By",
        Description = "Scale multiplies the image's size by Scale X and Scale Y. Size in pixels makes it Width by " +
                      "Height, whatever size it arrives.")]
    public ResizeBy ResizeBy
    {
        get => _resizeBy;
        set => SetParam(ref _resizeBy, value);
    }

    [Param("Scale X", Group = "By scale", Min = 0.01, Max = 10, Step = 0.01, Connectable = true,
        Description = "Used when Resize By is Scale.")]
    public double ScaleX
    {
        get => _scaleX;
        set => SetParam(ref _scaleX, value);
    }

    [Param("Scale Y", Group = "By scale", Min = 0.01, Max = 10, Step = 0.01, Connectable = true,
        Description = "Used when Resize By is Scale.")]
    public double ScaleY
    {
        get => _scaleY;
        set => SetParam(ref _scaleY, value);
    }

    [Param("Width", Group = "To a size", Min = 0, Max = 30000, Step = 1, Connectable = true,
        Description = "Used when Resize By is Size in pixels. 0 follows Height, keeping the image's proportions; " +
                      "with both 0 the image stays the size it is.")]
    public int Width
    {
        get => _width;
        set => SetParam(ref _width, value);
    }

    [Param("Height", Group = "To a size", Min = 0, Max = 30000, Step = 1, Connectable = true,
        Description = "Used when Resize By is Size in pixels. 0 follows Width, keeping the image's proportions; " +
                      "with both 0 the image stays the size it is.")]
    public int Height
    {
        get => _height;
        set => SetParam(ref _height, value);
    }

    [Param("Interpolation")]
    public Inter Interpolation
    {
        get => _interpolation;
        set => SetParam(ref _interpolation, value);
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context)
    {
        double scaleX, scaleY;

        if (_resizeBy == ResizeBy.Size)
        {
            var (width, height) = TargetSize(
                source.Width, source.Height, context.Param(nameof(Width), _width), context.Param(nameof(Height), _height));

            if ((long)width * height > MaximumPixels)
                throw new InvalidOperationException($"{width} x {height} pixels is too large an image to make.");

            CvInvoke.Resize(source.Mat, destination, new Size(width, height), 0, 0, _interpolation);

            // The factors it came to, for the shapes that go with it.
            scaleX = (double)width / source.Width;
            scaleY = (double)height / source.Height;
        }
        else
        {
            scaleX = context.Param(nameof(ScaleX), _scaleX);
            scaleY = context.Param(nameof(ScaleY), _scaleY);

            if (scaleX <= 0 || scaleY <= 0)
                throw new InvalidOperationException("Scale must be greater than zero on both axes.");

            CvInvoke.Resize(source.Mat, destination, Size.Empty, scaleX, scaleY, _interpolation);
        }

        // Pixel centres, as OpenCV resamples: the centre of source pixel x
        // lands at (x + 0.5) * scale - 0.5.
        Overlays.Carry(
            context,
            OverlayIn,
            OverlayOut,
            ShapeMap.Affine(scaleX, 0, (0.5 * scaleX) - 0.5, 0, scaleY, (0.5 * scaleY) - 0.5),
            source,
            destination.Width,
            destination.Height);
    }

    /// <summary>
    /// The size to make from what was asked for: a side left at nought
    /// follows the other in the image's own proportions, and with both at
    /// nought the image stays the size it is.
    /// </summary>
    internal static (int Width, int Height) TargetSize(int sourceWidth, int sourceHeight, int width, int height)
    {
        if (width < 0 || height < 0)
            throw new InvalidOperationException("Width and Height cannot be less than zero.");

        if (width == 0 && height == 0)
            return (sourceWidth, sourceHeight);

        if (width == 0)
            width = Math.Max(1, (int)Math.Round((double)sourceWidth * height / sourceHeight));
        else if (height == 0)
            height = Math.Max(1, (int)Math.Round((double)sourceHeight * width / sourceWidth));

        return (width, height);
    }
}

/// <summary>How far Rotate and Mirror turns an image, clockwise.</summary>
public enum QuarterTurn
{
    None = 0,

    [System.ComponentModel.Description("90\u00B0")]
    Quarter = 90,

    [System.ComponentModel.Description("180\u00B0")]
    Half = 180,

    [System.ComponentModel.Description("270\u00B0")]
    ThreeQuarters = 270,
}

/// <summary>Which sides of an image Rotate and Mirror swaps.</summary>
public enum MirrorSides
{
    None,

    [System.ComponentModel.Description("Left \u2194 Right")]
    LeftRight,

    [System.ComponentModel.Description("Top \u2194 Bottom")]
    TopBottom,
}

/// <summary>
/// Turns an image by quarter turns and mirrors it.
/// </summary>
/// <remarks>
/// <para>
/// For an image that arrives the wrong way round - a camera mounted on its
/// side, a strip from Polar to Linear that reads backwards - and for
/// nothing subtler: every pixel is moved, none is resampled, so the result
/// is exact and costs almost nothing. A turn by any other angle is a
/// resampling, and another tool.
/// </para>
/// <para>
/// The turn is made first and the mirror after. Between them the two
/// settings reach all eight ways a rectangle can be laid on itself, so no
/// third setting is needed: mirrored both ways is a turn of 180 degrees.
/// </para>
/// </remarks>
[VisionNode(
    "Rotate and Mirror",
    TypeKey = "basic.rotateMirror",
    Category = NodeCategory.Basic,
    Icon = "ph_rotate",
    Description = "Turns the image 90\u00B0, 180\u00B0 or 270\u00B0 and mirrors it left to right or top to bottom - exactly, " +
                  "moving pixels without resampling. Shapes wired into Overlay come out turned with it.")]
public sealed class RotateMirrorNode : ImageFilterNode
{
    private QuarterTurn _rotate = QuarterTurn.None;
    private MirrorSides _mirror = MirrorSides.None;

    public RotateMirrorNode()
    {
        OverlayIn = AddInput<ShapeSet>("Overlay", isRequired: false);
        OverlayOut = AddOutput<ShapeSet>("Overlay");
    }

    /// <summary>Shapes found on the input image, to be placed on the turned one.</summary>
    public InputPort OverlayIn { get; }

    /// <summary>The same shapes, turned and mirrored with the image.</summary>
    public OutputPort OverlayOut { get; }

    [Param("Rotate", Description = "Clockwise. 90\u00B0 and 270\u00B0 swap the image's width and height.")]
    public QuarterTurn Rotate
    {
        get => _rotate;
        set => SetParam(ref _rotate, value);
    }

    [Param("Mirror",
        Description = "After the rotation. Left \u2194 Right swaps the left and right sides, as a mirror on the wall does; " +
                      "Top \u2194 Bottom the top and bottom. Both at once is Rotate 180\u00B0.")]
    public MirrorSides Mirror
    {
        get => _mirror;
        set => SetParam(ref _mirror, value);
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context)
    {
        var turned = _rotate is QuarterTurn.Quarter or QuarterTurn.ThreeQuarters;
        var (width, height) = turned ? (source.Height, source.Width) : (source.Width, source.Height);

        if (_rotate == QuarterTurn.None && _mirror == MirrorSides.None)
        {
            source.Mat.CopyTo(destination);
        }
        else if (_mirror == MirrorSides.None)
        {
            CvInvoke.Rotate(source.Mat, destination, RotationOf(_rotate));
        }
        else if (_rotate == QuarterTurn.None)
        {
            CvInvoke.Flip(source.Mat, destination, FlipOf(_mirror));
        }
        else
        {
            using var rotated = new Mat();
            CvInvoke.Rotate(source.Mat, rotated, RotationOf(_rotate));
            CvInvoke.Flip(rotated, destination, FlipOf(_mirror));
        }

        Overlays.Carry(context, OverlayIn, OverlayOut, Map(_rotate, _mirror, source.Width, source.Height), source, width, height);
    }

    private static RotateFlags RotationOf(QuarterTurn turn) => turn switch
    {
        QuarterTurn.Quarter => RotateFlags.Rotate90Clockwise,
        QuarterTurn.Half => RotateFlags.Rotate180,
        _ => RotateFlags.Rotate90CounterClockwise,
    };

    private static FlipType FlipOf(MirrorSides mirror) =>
        mirror == MirrorSides.LeftRight ? FlipType.Horizontal : FlipType.Vertical;

    /// <summary>
    /// Where a point of a <paramref name="width"/> by <paramref name="height"/>
    /// image is afterwards. Exact: pixel (x, y) of the input is one pixel of
    /// the output, and this says which.
    /// </summary>
    internal static ShapeMap Map(QuarterTurn rotate, MirrorSides mirror, int width, int height)
    {
        // x' = a x + b y + tx,  y' = c x + d y + ty - the turn first.
        var (a, b, tx, c, d, ty) = rotate switch
        {
            QuarterTurn.Quarter => (0.0, -1.0, height - 1.0, 1.0, 0.0, 0.0),
            QuarterTurn.Half => (-1.0, 0.0, width - 1.0, 0.0, -1.0, height - 1.0),
            QuarterTurn.ThreeQuarters => (0.0, 1.0, 0.0, -1.0, 0.0, width - 1.0),
            _ => (1.0, 0.0, 0.0, 0.0, 1.0, 0.0),
        };

        var turned = rotate is QuarterTurn.Quarter or QuarterTurn.ThreeQuarters;
        var (turnedWidth, turnedHeight) = turned ? (height, width) : (width, height);

        // Then the mirror, across the image as it now is.
        if (mirror == MirrorSides.LeftRight)
            (a, b, tx) = (-a, -b, turnedWidth - 1 - tx);
        else if (mirror == MirrorSides.TopBottom)
            (c, d, ty) = (-c, -d, turnedHeight - 1 - ty);

        return ShapeMap.Affine(a, b, tx, c, d, ty);
    }
}
