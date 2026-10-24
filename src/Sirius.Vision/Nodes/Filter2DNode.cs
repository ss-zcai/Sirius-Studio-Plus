using System.Drawing;
using System.Globalization;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;

namespace Sirius.Vision.Nodes;

/// <summary>
/// Convolves the image with a user-supplied kernel.
/// </summary>
/// <remarks>
/// The kernel is a size plus a list of numbers rather than a <c>Mat</c>. Sirius
/// Studio held a raw <c>Mat</c> and edited it through a dedicated dialog, which
/// no file format could round-trip; written as text it saves with the recipe
/// and can be typed or pasted.
/// </remarks>
[VisionNode(
    "2D Filter",
    TypeKey = "filter.filter2D",
    Category = NodeCategory.Filtering,
    Icon = "ph_filter2d",
    Description = "Convolves the image with a custom kernel.")]
public sealed class Filter2DNode : ImageFilterNode
{
    /// <summary>Identity: leaves the image unchanged until edited.</summary>
    private const string DefaultKernel = "0 0 0; 0 1 0; 0 0 0";

    private int _kernelSize = 3;
    private string _kernel = DefaultKernel;
    private double _scale = 1;
    private double _offset;

    [Param("Kernel Size", Min = 3, Max = 15, Step = 2, OddOnly = true, Editor = ParamEditor.Slider)]
    public int KernelSize
    {
        get => _kernelSize;
        set => SetParam(ref _kernelSize, AdaptiveThresholdNode.MakeOdd(value, _kernelSize, minimum: 3));
    }

    [Param("Kernel",
        Description = "Row-major values, rows separated by ';'. Must hold Kernel Size squared numbers.")]
    public string Kernel
    {
        get => _kernel;
        set => SetParam(ref _kernel, value ?? string.Empty);
    }

    [Param("Scale", Description = "Multiplies the kernel, for normalising it.", Min = -10, Max = 10, Step = 0.01)]
    public double Scale
    {
        get => _scale;
        set => SetParam(ref _scale, value);
    }

    [Param("Offset", Description = "Added after convolution.", Min = -255, Max = 255, Step = 1, Editor = ParamEditor.Slider)]
    public double Offset
    {
        get => _offset;
        set => SetParam(ref _offset, value);
    }

    protected override void Apply(ImageData source, Mat destination, EvalContext context)
    {
        var values = ParseKernel(_kernel, _kernelSize, out var error);

        if (values is null)
            throw new InvalidOperationException(error);

        using var kernel = new Matrix<float>(_kernelSize, _kernelSize);
        for (var row = 0; row < _kernelSize; row++)
        for (var column = 0; column < _kernelSize; column++)
            kernel.Data[row, column] = (float)(values[(row * _kernelSize) + column] * _scale);

        // Filter2D will not allocate its destination - it requires one already
        // matching the source in size and channel count, and fails with a size
        // assertion otherwise. Copying the source both sizes it and sets depth.
        source.Mat.CopyTo(destination);

        CvInvoke.Filter2D(source.Mat, destination, kernel, new Point(-1, -1), _offset, BorderType.Reflect101);
    }

    /// <summary>
    /// Reads the kernel text, returning null and a reason when it cannot be used.
    /// </summary>
    internal static double[]? ParseKernel(string text, int size, out string error)
    {
        var expected = size * size;

        var tokens = (text ?? string.Empty)
            .Split(new[] { ';', ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length != expected)
        {
            error = $"The kernel needs {expected} values for a {size}x{size} kernel, but {tokens.Length} were given.";
            return null;
        }

        var values = new double[expected];
        for (var i = 0; i < expected; i++)
        {
            if (!double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                error = $"'{tokens[i]}' is not a number.";
                return null;
            }
        }

        error = string.Empty;
        return values;
    }
}
