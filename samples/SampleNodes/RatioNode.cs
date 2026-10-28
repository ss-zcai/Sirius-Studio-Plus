using Sirius.Plugins;

namespace Sirius.Samples;

/// <summary>
/// One number divided by another.
/// </summary>
/// <remarks>
/// A node with no pictures at all, which is most of what a measurement
/// recipe needs once something has been found: arithmetic on what was
/// measured. Value inputs are read with <see cref="IRunContext.ReadValue{T}"/>.
/// </remarks>
[CustomNode("Ratio",
    TypeKey = "samples.ratio",
    Group = NodeGroup.Analysis,
    Description = "Divides A by B - say, a measured width by a nominal one.")]
public sealed class RatioNode : ICustomNode
{
    private InPort<double> _a = null!;
    private InPort<double> _b = null!;
    private OutPort<double> _ratio = null!;

    [Setting(Min = 0, Max = 6, Step = 1,
             Description = "Decimal places to round the result to.")]
    public int Decimals { get; set; } = 3;

    public void Declare(INodeShape shape)
    {
        _a = shape.Input<double>("A");
        _b = shape.Input<double>("B");
        _ratio = shape.Output<double>("Ratio", "A / B");
    }

    public void Run(IRunContext context)
    {
        if (context.ReadValue(_a) is not { } a || context.ReadValue(_b) is not { } b)
            return;

        if (b == 0)
        {
            context.Warn("B is zero, so there is no ratio.");
            context.Publish(_ratio, double.NaN);
            return;
        }

        context.Publish(_ratio, Math.Round(a / b, Math.Clamp(Decimals, 0, 15)));
    }
}
