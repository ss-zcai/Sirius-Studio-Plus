using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// How a parameter's number is written beside its slider.
/// </summary>
/// <remarks>
/// Every slider read "9.00" regardless of type, which describes a precision
/// the parameter does not have - a kernel size cannot be 9.25, and reading
/// that it might undermines trust in the rest of the numbers.
/// </remarks>
public class ParameterFormatTests
{
    public ParameterFormatTests() => TestCatalog.Ensure();

    private static ParameterViewModel Parameter(VisionNode node, string name)
    {
        var descriptor = node.Descriptor.Parameters.Single(p => p.Name == name);
        return new ParameterViewModel(node, descriptor);
    }

    /// <summary>A whole-numbered parameter is written whole.</summary>
    [Fact]
    public void An_integer_parameter_shows_no_decimals()
    {
        var kernel = Parameter(new GaussianBlurNode { KernelSize = 9 }, nameof(GaussianBlurNode.KernelSize));

        Assert.Equal("0", kernel.ValueFormat);
        Assert.Equal("9", kernel.NumericText);
    }

    /// <summary>
    /// A grey level is whole even where the type is floating point, because
    /// OpenCV takes a double and a float image can want a fraction. The step
    /// is what says which it is.
    /// </summary>
    [Fact]
    public void A_grey_level_shows_no_decimals_either()
    {
        var threshold = Parameter(new ThresholdNode { Threshold = 128 }, nameof(ThresholdNode.Threshold));

        Assert.Equal(1, threshold.Step);
        Assert.Equal("0", threshold.ValueFormat);
        Assert.Equal("128", threshold.NumericText);
    }

    /// <summary>
    /// A genuinely continuous parameter keeps its decimals - a blur sigma of
    /// 1.4 is a different blur from 1.
    /// </summary>
    [Fact]
    public void A_continuous_parameter_keeps_its_decimals()
    {
        var sigma = Parameter(new GaussianBlurNode { SigmaX = 1.4 }, nameof(GaussianBlurNode.SigmaX));

        Assert.Equal("0.##", sigma.ValueFormat);
        Assert.Equal("1.4", sigma.NumericText);
    }

    [Fact]
    public void Typing_a_number_sets_the_parameter()
    {
        var node = new GaussianBlurNode();
        var kernel = Parameter(node, nameof(GaussianBlurNode.KernelSize));

        kernel.NumericText = "7";

        Assert.Equal(7, node.KernelSize);
    }

    /// <summary>
    /// The node coerces - a kernel must be odd - and the editor shows what
    /// was accepted rather than what was asked for.
    /// </summary>
    [Fact]
    public void The_display_follows_what_the_node_accepted()
    {
        var node = new GaussianBlurNode();
        var kernel = Parameter(node, nameof(GaussianBlurNode.KernelSize));

        kernel.NumericText = "8";

        Assert.Equal(9, node.KernelSize);
        Assert.Equal("9", kernel.NumericText);
    }

    [Fact]
    public void Nonsense_is_rejected_and_the_old_value_stays()
    {
        var node = new GaussianBlurNode { KernelSize = 5 };
        var kernel = Parameter(node, nameof(GaussianBlurNode.KernelSize));

        kernel.NumericText = "not a number";

        Assert.Equal(5, node.KernelSize);
        Assert.Equal("5", kernel.NumericText);
    }
}
