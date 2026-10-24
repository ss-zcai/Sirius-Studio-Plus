using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;

namespace Sirius.Vision.Nodes;

/// <summary>Canny edge detection.</summary>
[VisionNode(
    "Canny",
    TypeKey = "basic.canny",
    Category = NodeCategory.Basic,
    Icon = "ph_canny",
    Description = "Finds edges using the Canny detector.")]
public sealed class CannyNode : ImageFilterNode
{
    private double _threshold1 = 50;
    private double _threshold2 = 150;
    private int _apertureSize = 3;

    [Param("Threshold 1",
        Description = "Lower hysteresis threshold; edges below this are discarded.",
        Min = 0, Max = 500, Editor = ParamEditor.Slider, Connectable = true)]
    public double Threshold1
    {
        get => _threshold1;
        set => SetParam(ref _threshold1, value);
    }

    [Param("Threshold 2",
        Description = "Upper hysteresis threshold; edges above this are kept.",
        Min = 0, Max = 500, Editor = ParamEditor.Slider, Connectable = true)]
    public double Threshold2
    {
        get => _threshold2;
        set => SetParam(ref _threshold2, value);
    }

    /// <summary>Sobel aperture. OpenCV accepts only 3, 5 or 7.</summary>
    [Param("Aperture Size", Min = 3, Max = 7, Step = 2, OddOnly = true, Editor = ParamEditor.Slider)]
    public int ApertureSize
    {
        get => _apertureSize;
        set => SetParam(ref _apertureSize, Math.Clamp(AdaptiveThresholdNode.MakeOdd(value, _apertureSize, 3), 3, 7));
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context)
    {
        var lower = context.Param(nameof(Threshold1), _threshold1);
        var upper = context.Param(nameof(Threshold2), _threshold2);

        // Canny expects the lower bound first; swapping them silently produces
        // a near-empty result, so they are ordered here instead.
        if (lower > upper)
            (lower, upper) = (upper, lower);

        CvInvoke.Canny(source.Mat, destination, lower, upper, _apertureSize);
    }
}

/// <summary>Inverts intensities.</summary>
[VisionNode(
    "Invert",
    TypeKey = "basic.invert",
    Category = NodeCategory.Basic,
    Icon = "ph_invert",
    Description = "Inverts the image: light becomes dark and dark becomes light.")]
public sealed class InvertNode : ImageFilterNode
{
    protected override void Apply(ImageData source, Mat destination, EvalContext context) =>
        CvInvoke.BitwiseNot(source.Mat, destination);
}

/// <summary>Extracts one colour channel as a single-channel image.</summary>
[VisionNode(
    "RGB Channel",
    TypeKey = "basic.rgbChannel",
    Category = NodeCategory.Basic,
    Icon = "ph_rgb_channel",
    Description = "Extracts a single colour channel.")]
public sealed class RgbChannelNode : ImageFilterNode
{
    /// <summary>
    /// Named by colour, ordered as OpenCV stores them: blue first.
    /// </summary>
    public enum ColourChannel
    {
        Blue = 0,
        Green = 1,
        Red = 2,
    }

    private ColourChannel _channel = ColourChannel.Red;

    [Param("Channel")]
    public ColourChannel Channel
    {
        get => _channel;
        set => SetParam(ref _channel, value);
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context)
    {
        if (source.Channels == 1)
        {
            context.Warn("The image is already single-channel; passing it through unchanged.");
            source.Mat.CopyTo(destination);
            return;
        }

        var index = (int)_channel;
        if (index >= source.Channels)
        {
            throw new InvalidOperationException(
                $"The image has {source.Channels} channel(s), so there is no {_channel} channel to extract.");
        }

        // Split allocates one Mat per channel; only the requested one is kept,
        // and the rest must be released or every evaluation leaks them.
        var planes = source.Mat.Split();
        try
        {
            planes[index].CopyTo(destination);
        }
        finally
        {
            foreach (var plane in planes)
                plane.Dispose();
        }
    }
}

/// <summary>
/// Builds a mask of the pixels falling inside a colour range.
/// </summary>
/// <remarks>
/// The bounds are six separate channel values rather than two colours. A colour
/// picker cannot be driven by an upstream node, and a packed colour is awkward
/// to adjust one channel at a time, which is what tuning a range actually
/// involves.
/// </remarks>
[VisionNode(
    "Colour Range",
    TypeKey = "basic.colourRange",
    Category = NodeCategory.Basic,
    Icon = "ph_colour_range",
    Description = "Masks pixels whose colour falls within a range.")]
public sealed class ColourRangeNode : ImageFilterNode
{
    private ValueRange _blue = Full;
    private ValueRange _green = Full;
    private ValueRange _red = Full;
    private ColourSpace _space = ColourSpace.Bgr;

    /// <summary>Everything passes until the operator narrows it.</summary>
    private static readonly ValueRange Full = new(0, 255);

    /// <summary>
    /// What the three bands are called, and how far each runs.
    /// </summary>
    /// <remarks>
    /// Read from the frame last evaluated, not from a parameter: the node
    /// upstream already knows, and asking the operator to say it again is
    /// asking them to keep two things in step. Until the first pass it
    /// shows the camera-frame names, which is what an unconverted image
    /// will turn out to be.
    /// </remarks>
    public string Channel1Name => NameOf(0);

    public string Channel2Name => NameOf(1);

    public string Channel3Name => NameOf(2);

    public double Channel1Max => ColourSpaces.MaximumOf(_space, 0, DepthType.Cv8U);

    public double Channel2Max => ColourSpaces.MaximumOf(_space, 1, DepthType.Cv8U);

    public double Channel3Max => ColourSpaces.MaximumOf(_space, 2, DepthType.Cv8U);

    private string NameOf(int channel)
    {
        var names = ColourSpaces.NamesOf(_space, 3);
        return channel < names.Count ? names[channel] : $"Ch{channel}";
    }

    /// <summary>
    /// Notices the frame's colour space, so the inspector can relabel.
    /// </summary>
    private void NoteSpace(ColourSpace space)
    {
        if (_space == space)
            return;

        _space = space;

        foreach (var name in new[]
                 {
                     nameof(Channel1Name), nameof(Channel2Name), nameof(Channel3Name),
                     nameof(Channel1Max), nameof(Channel2Max), nameof(Channel3Max),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    [Param(
        "Blue",
        Min = 0,
        Max = 255,
        NameSource = nameof(Channel1Name),
        MaxSource = nameof(Channel1Max),
        Editor = ParamEditor.Range,
        Description = "First channel values that pass, inclusive.")]
    public ValueRange Blue
    {
        get => _blue;
        set => SetParam(ref _blue, value.Clamp(0, 65535).Rounded());
    }

    [Param(
        "Green",
        Min = 0,
        Max = 255,
        NameSource = nameof(Channel2Name),
        MaxSource = nameof(Channel2Max),
        Editor = ParamEditor.Range,
        Description = "Second channel values that pass, inclusive.")]
    public ValueRange Green
    {
        get => _green;
        set => SetParam(ref _green, value.Clamp(0, 65535).Rounded());
    }

    [Param(
        "Red",
        Min = 0,
        Max = 255,
        NameSource = nameof(Channel3Name),
        MaxSource = nameof(Channel3Max),
        Editor = ParamEditor.Range,
        Description = "Third channel values that pass, inclusive.")]
    public ValueRange Red
    {
        get => _red;
        set => SetParam(ref _red, value.Clamp(0, 65535).Rounded());
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context)
    {
        if (source.Channels < 3)
        {
            throw new InvalidOperationException(
                "Colour Range needs a three-channel image. Remove any Colour Convert to Gray ahead of it.");
        }

        NoteSpace(source.Space);

        // A channel range is always bounded - the setter clamps both ends -
        // but the fallbacks say what an absent one would mean here.
        using var lower = new ScalarArray(
            new MCvScalar(_blue.LowOr(0), _green.LowOr(0), _red.LowOr(0)));
        using var upper = new ScalarArray(
            new MCvScalar(_blue.HighOr(255), _green.HighOr(255), _red.HighOr(255)));

        CvInvoke.InRange(source.Mat, lower, upper, destination);
    }
}
