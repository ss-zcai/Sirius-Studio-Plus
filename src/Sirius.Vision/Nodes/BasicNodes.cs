using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;

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
    [Param("Threshold", Min = 0, Max = 255, Step = 1, Editor = ParamEditor.Slider, Connectable = true)]
    public double Threshold
    {
        get => _threshold;
        set => SetParam(ref _threshold, value);
    }

    [Param("Max Value", Min = 0, Max = 255, Step = 1, Editor = ParamEditor.Slider)]
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

    [Param("Max Value", Min = 0, Max = 255, Step = 1, Editor = ParamEditor.Slider)]
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

    [Param("Constant", Description = "Subtracted from the local mean.", Min = -50, Max = 50, Editor = ParamEditor.Slider)]
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

        CvInvoke.AdaptiveThreshold(
            source.Mat,
            destination,
            _maxValue,
            _adaptiveType,
            _thresholdType,
            _blockSize,
            _constant);
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

    [Param("Brightness", Min = -100, Max = 100, Editor = ParamEditor.Slider, Connectable = true)]
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

[VisionNode(
    "Crop",
    TypeKey = "basic.crop",
    Category = NodeCategory.Basic,
    Icon = "ph_crop",
    Description = "Extracts a rectangular region of interest.")]
public sealed class CropNode : ImageFilterNode
{
    private Rectangle _roi = new(0, 0, 0, 0);

    [Param("Region", Editor = ParamEditor.Region, Description = "Drawable on the image viewer.")]
    public Rectangle Roi
    {
        get => _roi;
        set => SetParam(ref _roi, value);
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context)
    {
        var bounds = new Rectangle(0, 0, source.Width, source.Height);

        if (_roi.Width <= 0 || _roi.Height <= 0)
        {
            context.Warn("Region is empty; passing the image through unchanged.");
            source.Mat.CopyTo(destination);
            return;
        }

        var clamped = Rectangle.Intersect(_roi, bounds);
        if (clamped.IsEmpty)
        {
            context.Warn("Region lies outside the image; passing the image through unchanged.");
            source.Mat.CopyTo(destination);
            return;
        }

        if (clamped != _roi)
            context.Warn("Region was clipped to the image bounds.");

        // A Mat built from a rectangle is a view onto the source buffer, so it
        // must be copied before being published - otherwise the cached output
        // would reference memory owned by the upstream node.
        using var view = new Mat(source.Mat, clamped);
        view.CopyTo(destination);
    }
}

[VisionNode(
    "Resize",
    TypeKey = "basic.resize",
    Category = NodeCategory.Basic,
    Icon = "ph_resize",
    Description = "Scales the image.")]
public sealed class ResizeNode : ImageFilterNode
{
    private double _scaleX = 1;
    private double _scaleY = 1;
    private Inter _interpolation = Inter.Linear;

    [Param("Scale X", Min = 0.01, Max = 10, Step = 0.01, Connectable = true)]
    public double ScaleX
    {
        get => _scaleX;
        set => SetParam(ref _scaleX, value);
    }

    [Param("Scale Y", Min = 0.01, Max = 10, Step = 0.01, Connectable = true)]
    public double ScaleY
    {
        get => _scaleY;
        set => SetParam(ref _scaleY, value);
    }

    [Param("Interpolation")]
    public Inter Interpolation
    {
        get => _interpolation;
        set => SetParam(ref _interpolation, value);
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context)
    {
        var scaleX = context.Param(nameof(ScaleX), _scaleX);
        var scaleY = context.Param(nameof(ScaleY), _scaleY);

        if (scaleX <= 0 || scaleY <= 0)
            throw new InvalidOperationException("Scale must be greater than zero on both axes.");

        CvInvoke.Resize(source.Mat, destination, Size.Empty, scaleX, scaleY, _interpolation);
    }
}
