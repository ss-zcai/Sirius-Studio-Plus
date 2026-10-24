using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;

namespace Sirius.Vision.Nodes;

/// <summary>
/// Base for a node that combines two images into one.
/// </summary>
/// <remarks>
/// <para>
/// The two frames must agree on size, channel count and depth. Combining
/// mismatched frames can only be done by inventing something - cropping to
/// the overlap, padding, or resampling - and each of those hides a mistake
/// in the recipe rather than reporting it. A recipe that puts two different
/// images into one operator is wrong, and the message says which way.
/// </para>
/// <para>
/// Buffers follow the engine's ownership rule: the inputs belong to the
/// scheduler's cache and must not be disposed or written to.
/// </para>
/// </remarks>
public abstract class TwoImageNode : VisionNode
{
    /// <param name="firstName">Port name for the first image. Saved recipes
    /// reference it, so it must not change once a node has shipped.</param>
    /// <param name="secondName">Port name for the second image.</param>
    protected TwoImageNode(string firstName, string secondName)
    {
        FirstIn = AddInput<ImageData>(firstName);
        SecondIn = AddInput<ImageData>(secondName);
        ImageOut = AddOutput<ImageData>("Out");
    }

    public InputPort FirstIn { get; }

    public InputPort SecondIn { get; }

    public OutputPort ImageOut { get; }

    protected sealed override void OnEvaluate(EvalContext context)
    {
        // Both required, so the scheduler guarantees these are non-null.
        var first = context.Get<ImageData>(FirstIn)!;
        var second = context.Get<ImageData>(SecondIn)!;

        RequireMatching(first, second);

        var destination = new Mat();
        try
        {
            Combine(first, second, destination, context);
        }
        catch
        {
            destination.Dispose();
            throw;
        }

        if (destination.IsEmpty)
        {
            destination.Dispose();
            throw new InvalidOperationException($"{Descriptor.DisplayName} produced an empty image.");
        }

        context.Set(ImageOut, ImageData.Adopt(destination));
    }

    /// <summary>
    /// Writes the combination into <paramref name="destination"/>. Neither
    /// input may be disposed or modified.
    /// </summary>
    protected abstract void Combine(
        ImageData first, ImageData second, Mat destination, EvalContext context);

    private void RequireMatching(ImageData first, ImageData second)
    {
        var name = Descriptor.DisplayName;

        if (first.Width != second.Width || first.Height != second.Height)
        {
            throw new InvalidOperationException(
                $"{name} needs two images of the same size, but {FirstIn.DisplayName} is " +
                $"{first.Width}x{first.Height} and {SecondIn.DisplayName} is " +
                $"{second.Width}x{second.Height}. Resize one of them first.");
        }

        if (first.Channels != second.Channels)
        {
            throw new InvalidOperationException(
                $"{name} needs two images with the same number of channels, but " +
                $"{FirstIn.DisplayName} has {first.Channels} and {SecondIn.DisplayName} has " +
                $"{second.Channels}. Use Colour Convert or RGB Channel on one of them.");
        }

        if (first.Depth != second.Depth)
        {
            throw new InvalidOperationException(
                $"{name} needs two images of the same depth, but {FirstIn.DisplayName} is " +
                $"{first.Depth} and {SecondIn.DisplayName} is {second.Depth}.");
        }
    }

    /// <summary>
    /// The value a fully bright pixel holds at this depth.
    /// </summary>
    /// <remarks>
    /// Multiply, screen and overlay are defined on values from zero to one,
    /// so an 8-bit product has to be divided by 255 and a float one by 1. Get
    /// this wrong and every such blend saturates to white.
    /// </remarks>
    protected static double FullScale(DepthType depth) => depth switch
    {
        DepthType.Cv8U or DepthType.Cv8S => 255.0,
        DepthType.Cv16U or DepthType.Cv16S => 65535.0,
        _ => 1.0,
    };
}

/// <summary>Logical operations on two masks.</summary>
public enum LogicOperation
{
    /// <summary>In both. The intersection.</summary>
    And,

    /// <summary>In either. The union.</summary>
    Or,

    /// <summary>In one but not both. The symmetric difference.</summary>
    Xor,

    /// <summary>In the first but not the second.</summary>
    Subtract,
}

/// <summary>
/// Combines two masks with a logical operation.
/// </summary>
/// <remarks>
/// <para>
/// The everyday use is combining the results of two thresholds: bright
/// enough <b>and</b> inside the board, say, or either of two defect
/// detectors. Subtract - in A but not B - is how a region is excluded, which
/// is the one the other three cannot express between them.
/// </para>
/// <para>
/// Not is missing on purpose: Invert already does it.
/// </para>
/// </remarks>
[VisionNode(
    "Logic",
    TypeKey = "combine.logic",
    Category = NodeCategory.Combine,
    Icon = "ph_logic",
    Description = "AND, OR, XOR or subtract two masks.")]
public sealed class ImageLogicNode : TwoImageNode
{
    private LogicOperation _operation = LogicOperation.And;
    private bool _asBinary = true;

    public ImageLogicNode()
        : base("A", "B")
    {
    }

    [Param("Operation", Description = "How the two masks are combined.")]
    public LogicOperation Operation
    {
        get => _operation;
        set => SetParam(ref _operation, value);
    }

    [Param(
        "Treat As Binary",
        Description = "Read any non-zero pixel as set, and write 0 or 255. " +
                      "Clear to operate on the raw bits instead, which only " +
                      "makes sense on images that are already masks.")]
    public bool AsBinary
    {
        get => _asBinary;
        set => SetParam(ref _asBinary, value);
    }

    protected override void Combine(
        ImageData first, ImageData second, Mat destination, EvalContext context)
    {
        if (!_asBinary)
        {
            Apply(first.Mat, second.Mat, destination);
            return;
        }

        // Reduced to 0 or 255 first, so the operation means what its name
        // says whatever came in. A greyscale frame bitwise-ANDed with another
        // is a legitimate thing to ask for and almost never what was meant:
        // 0x7F AND 0x80 is zero, and two mid-grey images come out black.
        using var left = new Mat();
        using var right = new Mat();

        CvInvoke.Compare(first.Mat, new ScalarArray(new MCvScalar(0)), left, CmpType.NotEqual);
        CvInvoke.Compare(second.Mat, new ScalarArray(new MCvScalar(0)), right, CmpType.NotEqual);

        Apply(left, right, destination);
    }

    private void Apply(IInputArray first, IInputArray second, IOutputArray destination)
    {
        switch (_operation)
        {
            case LogicOperation.Or:
                CvInvoke.BitwiseOr(first, second, destination);
                break;

            case LogicOperation.Xor:
                CvInvoke.BitwiseXor(first, second, destination);
                break;

            case LogicOperation.Subtract:
                // A and not B, rather than a saturating arithmetic subtract:
                // on masks they agree, and on raw bits this is the one that
                // answers "in A but not B".
                using (var inverted = new Mat())
                {
                    CvInvoke.BitwiseNot(second, inverted);
                    CvInvoke.BitwiseAnd(first, inverted, destination);
                }

                break;

            default:
                CvInvoke.BitwiseAnd(first, second, destination);
                break;
        }
    }
}

/// <summary>How two images are mixed.</summary>
public enum BlendMode
{
    /// <summary>The blend layer, faded in by opacity alone.</summary>
    Normal,

    /// <summary>Sum, saturating at full scale. Brightens.</summary>
    Add,

    /// <summary>Base minus blend, clamped at zero.</summary>
    Subtract,

    /// <summary>Absolute difference. Zero where the two agree.</summary>
    Difference,

    /// <summary>Product. Darkens; white leaves the base alone.</summary>
    Multiply,

    /// <summary>Base divided by blend. Flat-field correction.</summary>
    Divide,

    /// <summary>Inverse multiply. Brightens; black leaves the base alone.</summary>
    Screen,

    /// <summary>The darker of the two, per pixel.</summary>
    Darken,

    /// <summary>The lighter of the two, per pixel.</summary>
    Lighten,

    /// <summary>Multiply where the base is dark, screen where it is light.</summary>
    Overlay,

    /// <summary>Overlay with the roles reversed - the blend layer decides.</summary>
    HardLight,
}

/// <summary>
/// Mixes two images, the way layers combine in an image editor.
/// </summary>
/// <remarks>
/// <para>
/// Several of these earn their place in inspection rather than in design.
/// <see cref="BlendMode.Difference"/> against a golden sample is change
/// detection. <see cref="BlendMode.Subtract"/> removes a captured
/// background. <see cref="BlendMode.Divide"/> by a blank-field image is
/// flat-field correction, which is the honest way to undo uneven lighting.
/// <see cref="BlendMode.Darken"/> and <see cref="BlendMode.Lighten"/> are a
/// per-pixel minimum and maximum, useful for combining exposures.
/// </para>
/// <para>
/// Three or more images are combined by chaining, which is also how layers
/// actually work: each blends with everything beneath it.
/// </para>
/// <para>
/// Soft Light is deliberately absent. It has several incompatible
/// definitions in common use, and a tool that quietly picks one of them is
/// worse than one that does not offer it.
/// </para>
/// </remarks>
[VisionNode(
    "Blend",
    TypeKey = "combine.blend",
    Category = NodeCategory.Combine,
    Icon = "ph_blend",
    Description = "Mixes two images with a blend mode and an opacity.")]
public sealed class ImageBlendNode : TwoImageNode
{
    private BlendMode _mode = BlendMode.Normal;
    private double _opacity = 1.0;

    public ImageBlendNode()
        : base("Base", "Blend")
    {
        MaskIn = AddInput<ImageData>("Mask", isRequired: false);
    }

    /// <summary>
    /// Limits the blend to where this is non-zero. Optional.
    /// </summary>
    /// <remarks>
    /// Wired from a threshold or a region, this is how a correction is
    /// applied to part of a frame - the alternative being to crop, blend and
    /// paste back, which loses the coordinates everything else is measured
    /// in.
    /// </remarks>
    public InputPort MaskIn { get; }

    [Param("Mode", Description = "How the two images are mixed.")]
    public BlendMode Mode
    {
        get => _mode;
        set => SetParam(ref _mode, value);
    }

    [Param(
        "Opacity",
        Min = 0,
        Max = 1,
        Editor = ParamEditor.Slider,
        Description = "How much of the blended result replaces the base. " +
                      "Applied to the result of the mode, not to the blend image.")]
    public double Opacity
    {
        get => _opacity;
        set => SetParam(ref _opacity, Math.Clamp(value, 0, 1));
    }

    protected override void Combine(
        ImageData first, ImageData second, Mat destination, EvalContext context)
    {
        var scale = FullScale(first.Depth);

        using var blended = new Mat();
        Mix(first.Mat, second.Mat, blended, scale);

        // Opacity fades the *result* towards the base, which is what makes a
        // half-strength overlay look like a half-strength overlay rather than
        // like an overlay with a faded blend layer.
        using var faded = new Mat();

        if (_opacity >= 1.0)
        {
            blended.CopyTo(faded);
        }
        else
        {
            CvInvoke.AddWeighted(first.Mat, 1 - _opacity, blended, _opacity, 0, faded, first.Depth);
        }

        if (context.Get<ImageData>(MaskIn) is not { } mask)
        {
            faded.CopyTo(destination);
            return;
        }

        RequireMaskMatches(first, mask);

        // Base everywhere, the blend only where the mask allows it.
        first.Mat.CopyTo(destination);
        faded.CopyTo(destination, mask.Mat);
    }

    private void RequireMaskMatches(ImageData image, ImageData mask)
    {
        if (mask.Width != image.Width || mask.Height != image.Height)
        {
            throw new InvalidOperationException(
                $"{Descriptor.DisplayName} needs a mask the same size as the images, but it is " +
                $"{mask.Width}x{mask.Height} against {image.Width}x{image.Height}.");
        }

        if (mask.Channels != 1)
        {
            throw new InvalidOperationException(
                $"{Descriptor.DisplayName} needs a single-channel mask, but it has " +
                $"{mask.Channels} channels. Threshold it, or take one channel.");
        }
    }

    private void Mix(Mat baseImage, Mat blend, Mat result, double scale)
    {
        switch (_mode)
        {
            case BlendMode.Add:
                CvInvoke.Add(baseImage, blend, result);
                break;

            case BlendMode.Subtract:
                CvInvoke.Subtract(baseImage, blend, result);
                break;

            case BlendMode.Difference:
                CvInvoke.AbsDiff(baseImage, blend, result);
                break;

            case BlendMode.Multiply:
                CvInvoke.Multiply(baseImage, blend, result, 1.0 / scale);
                break;

            case BlendMode.Divide:
                // OpenCV writes zero where the divisor is zero, which is the
                // only defensible answer and avoids an infinity.
                CvInvoke.Divide(baseImage, blend, result, scale);
                break;

            case BlendMode.Screen:
                Screen(baseImage, blend, result, scale);
                break;

            case BlendMode.Darken:
                CvInvoke.Min(baseImage, blend, result);
                break;

            case BlendMode.Lighten:
                CvInvoke.Max(baseImage, blend, result);
                break;

            case BlendMode.Overlay:
                HardLight(blend, baseImage, result, scale);
                break;

            case BlendMode.HardLight:
                HardLight(baseImage, blend, result, scale);
                break;

            default:
                blend.CopyTo(result);
                break;
        }
    }

    /// <summary>Inverse multiply: full - (full - a)(full - b) / full.</summary>
    private static void Screen(Mat first, Mat second, Mat result, double scale)
    {
        using var full = new ScalarArray(new MCvScalar(scale, scale, scale, scale));
        using var left = new Mat();
        using var right = new Mat();
        using var product = new Mat();

        CvInvoke.Subtract(full, first, left);
        CvInvoke.Subtract(full, second, right);
        CvInvoke.Multiply(left, right, product, 1.0 / scale);
        CvInvoke.Subtract(full, product, result);
    }

    /// <summary>
    /// Multiply where <paramref name="chooser"/> is dark, screen where it is
    /// light. Overlay and hard light are the same operation with the two
    /// images swapped, so one implementation serves both.
    /// </summary>
    private static void HardLight(Mat chooser, Mat other, Mat result, double scale)
    {
        using var dark = new Mat();
        using var light = new Mat();
        using var selector = new Mat();

        // Doubled, because each half of the chooser's range is mapped across
        // the whole of the output's.
        CvInvoke.Multiply(chooser, other, dark, 2.0 / scale);

        using (var doubled = new Mat())
        using (var half = new ScalarArray(new MCvScalar(scale / 2, scale / 2, scale / 2, scale / 2)))
        {
            CvInvoke.Subtract(chooser, half, doubled);
            CvInvoke.Multiply(doubled, new ScalarArray(new MCvScalar(2, 2, 2, 2)), doubled);
            Screen(doubled, other, light, scale);

            CvInvoke.Compare(chooser, half, selector, CmpType.LessThan);
        }

        light.CopyTo(result);
        dark.CopyTo(result, selector);
    }
}
