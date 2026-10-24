using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;

namespace Sirius.Vision.Nodes;

/// <summary>
/// Shared parameters for the morphology nodes.
/// </summary>
/// <remarks>
/// The structuring element is described by shape and size rather than exposed
/// as a raw <c>Mat</c> property. The old tools held
/// <c>public Mat Kernel { get; set; }</c>, which the property grid could not
/// meaningfully edit - its editor button opened a message box reading
/// "Kernel Setting" - and which no file format could round-trip. Building the
/// element per evaluation from serialisable scalars fixes both.
/// </remarks>
public abstract class MorphologyNodeBase : ImageFilterNode
{
    private MorphShapes _shape = MorphShapes.Rectangle;
    private int _kernelSize = 3;
    private int _iterations = 1;
    private BorderType _borderType = BorderType.Constant;

    [Param("Element Shape")]
    public MorphShapes Shape
    {
        get => _shape;
        set => SetParam(ref _shape, value);
    }

    [Param("Kernel Size", Min = 1, Max = 99, Step = 2, OddOnly = true, Editor = ParamEditor.Slider)]
    public int KernelSize
    {
        get => _kernelSize;
        set => SetParam(ref _kernelSize, AdaptiveThresholdNode.MakeOdd(value, _kernelSize, minimum: 1));
    }

    [Param("Iterations", Min = 1, Max = 20, Editor = ParamEditor.Slider)]
    public int Iterations
    {
        get => _iterations;
        set => SetParam(ref _iterations, Math.Max(1, value));
    }

    [Param("Border Type")]
    public BorderType BorderType
    {
        get => _borderType;
        set => SetParam(ref _borderType, value);
    }

    /// <summary>Builds the structuring element. Caller disposes.</summary>
    protected Mat CreateKernel() =>
        CvInvoke.GetStructuringElement(_shape, new Size(_kernelSize, _kernelSize), new Point(-1, -1));

    protected static MCvScalar BorderValue => new(255, 255, 255);
}

[VisionNode(
    "Dilate",
    TypeKey = "morphology.dilate",
    Category = NodeCategory.Morphology,
    Icon = "ph_dilate",
    Description = "Grows bright regions.")]
public sealed class DilateNode : MorphologyNodeBase
{
    protected override void Apply(ImageData source, Mat destination, EvalContext context)
    {
        using var kernel = CreateKernel();
        CvInvoke.Dilate(
            source.Mat,
            destination,
            kernel,
            new Point(-1, -1),
            Iterations,
            BorderType,
            BorderValue);
    }
}

[VisionNode(
    "Erode",
    TypeKey = "morphology.erode",
    Category = NodeCategory.Morphology,
    Icon = "ph_erode",
    Description = "Shrinks bright regions.")]
public sealed class ErodeNode : MorphologyNodeBase
{
    protected override void Apply(ImageData source, Mat destination, EvalContext context)
    {
        using var kernel = CreateKernel();
        CvInvoke.Erode(
            source.Mat,
            destination,
            kernel,
            new Point(-1, -1),
            Iterations,
            BorderType,
            BorderValue);
    }
}

/// <summary>
/// Compound morphology: open, close, gradient, top hat, black hat.
/// The old <c>MorphologyExTool</c> was an empty stub.
/// </summary>
[VisionNode(
    "Advanced Morphology",
    TypeKey = "morphology.morphologyEx",
    Category = NodeCategory.Morphology,
    Icon = "ph_morphology",
    Description = "Open, close, gradient, top hat or black hat.")]
public sealed class MorphologyExNode : MorphologyNodeBase
{
    private MorphOp _operation = MorphOp.Open;

    [Param("Operation")]
    public MorphOp Operation
    {
        get => _operation;
        set => SetParam(ref _operation, value);
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context)
    {
        using var kernel = CreateKernel();
        CvInvoke.MorphologyEx(
            source.Mat,
            destination,
            _operation,
            kernel,
            new Point(-1, -1),
            Iterations,
            BorderType,
            BorderValue);
    }
}
