using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;

namespace Sirius.Vision.Nodes;

[VisionNode(
    "Gaussian Blur",
    TypeKey = "filter.gaussianBlur",
    Category = NodeCategory.Filtering,
    Icon = "ph_gaussian_blur",
    Description = "Smooths with a Gaussian kernel.")]
public sealed class GaussianBlurNode : ImageFilterNode
{
    private int _kernelSize = 3;
    private double _sigmaX;
    private double _sigmaY;

    [Param("Kernel Size", Min = 1, Max = 99, Step = 2, OddOnly = true, Editor = ParamEditor.Slider)]
    public int KernelSize
    {
        get => _kernelSize;
        set => SetParam(ref _kernelSize, AdaptiveThresholdNode.MakeOdd(value, _kernelSize, minimum: 1));
    }

    [Param("Sigma X", Description = "0 derives sigma from the kernel size.", Min = 0, Max = 50, Step = 0.1)]
    public double SigmaX
    {
        get => _sigmaX;
        set => SetParam(ref _sigmaX, value);
    }

    [Param("Sigma Y", Description = "0 uses Sigma X.", Min = 0, Max = 50, Step = 0.1)]
    public double SigmaY
    {
        get => _sigmaY;
        set => SetParam(ref _sigmaY, value);
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context) =>
        CvInvoke.GaussianBlur(source.Mat, destination, new Size(_kernelSize, _kernelSize), _sigmaX, _sigmaY);
}

[VisionNode(
    "Median Blur",
    TypeKey = "filter.medianBlur",
    Category = NodeCategory.Filtering,
    Icon = "ph_median_blur",
    Description = "Smooths with a median kernel; good against salt-and-pepper noise.")]
public sealed class MedianBlurNode : ImageFilterNode
{
    private int _kernelSize = 3;

    [Param("Kernel Size", Min = 3, Max = 99, Step = 2, OddOnly = true, Editor = ParamEditor.Slider)]
    public int KernelSize
    {
        get => _kernelSize;
        set => SetParam(ref _kernelSize, AdaptiveThresholdNode.MakeOdd(value, _kernelSize, minimum: 3));
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context) =>
        CvInvoke.MedianBlur(source.Mat, destination, _kernelSize);
}

/// <summary>
/// Box (mean) filter. The old <c>BoxFilterTool</c> was an empty
/// <c>// TODO: implementation</c> stub.
/// </summary>
[VisionNode(
    "Box Filter",
    TypeKey = "filter.boxFilter",
    Category = NodeCategory.Filtering,
    Icon = "ph_box_filter",
    Description = "Mean smoothing over a rectangular neighbourhood.")]
public sealed class BoxFilterNode : ImageFilterNode
{
    private int _kernelWidth = 3;
    private int _kernelHeight = 3;
    private bool _normalize = true;

    [Param("Kernel Width", Min = 1, Max = 199, Editor = ParamEditor.Slider)]
    public int KernelWidth
    {
        get => _kernelWidth;
        set => SetParam(ref _kernelWidth, Math.Max(1, value));
    }

    [Param("Kernel Height", Min = 1, Max = 199, Editor = ParamEditor.Slider)]
    public int KernelHeight
    {
        get => _kernelHeight;
        set => SetParam(ref _kernelHeight, Math.Max(1, value));
    }

    [Param("Normalize", Description = "Divide by the kernel area to keep the mean.")]
    public bool Normalize
    {
        get => _normalize;
        set => SetParam(ref _normalize, value);
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context) =>
        CvInvoke.BoxFilter(
            source.Mat,
            destination,
            source.Depth,
            new Size(_kernelWidth, _kernelHeight),
            new Point(-1, -1),
            _normalize);
}

/// <summary>
/// Divides the image by a heavily blurred copy of itself to flatten uneven
/// illumination.
/// </summary>
/// <remarks>
/// Ported from the old <c>NormaliseTool</c>, which carried the reference Python
/// in a comment. The intermediate blurred buffer is disposed here - the original
/// allocated it per call and never released it.
/// </remarks>
[VisionNode(
    "Normalise",
    TypeKey = "filter.normalise",
    Category = NodeCategory.Filtering,
    Icon = "ph_normalise",
    Description = "Flattens uneven illumination by dividing out a blurred copy.")]
public sealed class NormaliseNode : ImageFilterNode
{
    private int _kernelSize = 57;
    private double _scale = 128;

    [Param("Kernel Size", Min = 3, Max = 501, Step = 2, OddOnly = true, Editor = ParamEditor.Slider)]
    public int KernelSize
    {
        get => _kernelSize;
        set => SetParam(ref _kernelSize, AdaptiveThresholdNode.MakeOdd(value, _kernelSize, minimum: 3));
    }

    [Param("Scale", Min = 1, Max = 255, Step = 1, Editor = ParamEditor.Slider)]
    public double Scale
    {
        get => _scale;
        set => SetParam(ref _scale, value);
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context)
    {
        using var blurred = new Mat();
        CvInvoke.BoxFilter(
            source.Mat,
            blurred,
            source.Depth,
            new Size(_kernelSize, _kernelSize),
            new Point(-1, -1));

        CvInvoke.Divide(source.Mat, blurred, destination, _scale);
    }
}

/// <summary>
/// High-pass filter: the image minus its low-frequency content, re-centred.
/// The old <c>HighPassFilterTool</c> was an empty stub.
/// </summary>
[VisionNode(
    "High-pass Filter",
    TypeKey = "filter.highPass",
    Category = NodeCategory.Filtering,
    Icon = "ph_highpass",
    Description = "Keeps edges and fine detail by subtracting a blurred copy.")]
public sealed class HighPassFilterNode : ImageFilterNode
{
    private int _kernelSize = 21;
    private double _offset = 128;

    [Param("Kernel Size", Description = "Larger keeps more detail.", Min = 3, Max = 301, Step = 2, OddOnly = true, Editor = ParamEditor.Slider)]
    public int KernelSize
    {
        get => _kernelSize;
        set => SetParam(ref _kernelSize, AdaptiveThresholdNode.MakeOdd(value, _kernelSize, minimum: 3));
    }

    [Param("Offset", Description = "Mid-grey level added back so negatives stay visible.", Min = 0, Max = 255, Step = 1, Editor = ParamEditor.Slider)]
    public double Offset
    {
        get => _offset;
        set => SetParam(ref _offset, value);
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context)
    {
        using var lowPass = new Mat();
        CvInvoke.GaussianBlur(source.Mat, lowPass, new Size(_kernelSize, _kernelSize), 0);

        // (source - lowPass) + offset, saturating at the source depth.
        CvInvoke.AddWeighted(source.Mat, 1.0, lowPass, -1.0, _offset, destination, source.Depth);
    }
}
