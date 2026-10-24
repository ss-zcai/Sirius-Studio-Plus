using Sirius.Core.Graph;
using Sirius.Core.Nodes;

namespace Sirius.Vision.Nodes;

/// <summary>
/// A whole number, for driving connectable parameters on other nodes.
/// </summary>
/// <remarks>
/// On its own this does nothing. Its purpose is to let one value feed several
/// parameters at once - wire it into two thresholds and they stay in step - and
/// to give a single place to adjust a figure used in more than one node.
///
/// The slider range is set by the Min and Max parameters rather than fixed in
/// code, because a useful range depends entirely on what the value drives.
/// </remarks>
[VisionNode(
    "Integer",
    TypeKey = "source.integer",
    Category = NodeCategory.Source,
    Icon = "ph_integer",
    Description = "A whole number that can drive parameters on other nodes.")]
public sealed class IntegerNode : VisionNode
{
    private int _value = 128;
    private int _minimum;
    private int _maximum = 255;

    public IntegerNode()
    {
        ValueOut = AddOutput<int>("Out");
    }

    public OutputPort ValueOut { get; }

    [Param("Value",
        Description = "Sent to every parameter wired to this node.",
        Editor = ParamEditor.Slider,
        MinSource = nameof(Minimum),
        MaxSource = nameof(Maximum))]
    public int Value
    {
        get => _value;
        set => SetParam(ref _value, Clamp(value));
    }

    [Param("Min", Group = "Range", Description = "Lower end of the slider.")]
    public int Minimum
    {
        get => _minimum;
        set
        {
            if (SetParam(ref _minimum, value))
                Value = Clamp(_value);
        }
    }

    [Param("Max", Group = "Range", Description = "Upper end of the slider.")]
    public int Maximum
    {
        get => _maximum;
        set
        {
            if (SetParam(ref _maximum, value))
                Value = Clamp(_value);
        }
    }

    /// <summary>
    /// Keeps the value inside the range. Tolerates an inverted range rather
    /// than throwing while the operator is still typing the second bound.
    /// </summary>
    private int Clamp(int value) =>
        _minimum <= _maximum ? Math.Clamp(value, _minimum, _maximum) : value;

    protected override void OnEvaluate(EvalContext context) => context.Set(ValueOut, _value);
}

/// <summary>A real number, for parameters that are not whole counts.</summary>
[VisionNode(
    "Float",
    TypeKey = "source.float",
    Category = NodeCategory.Source,
    Icon = "ph_float",
    Description = "A real number that can drive parameters on other nodes.")]
public sealed class FloatNode : VisionNode
{
    private double _value = 1;
    private double _minimum;
    private double _maximum = 10;

    public FloatNode()
    {
        ValueOut = AddOutput<double>("Out");
    }

    public OutputPort ValueOut { get; }

    [Param("Value",
        Description = "Sent to every parameter wired to this node.",
        Step = 0.01,
        Editor = ParamEditor.Slider,
        MinSource = nameof(Minimum),
        MaxSource = nameof(Maximum))]
    public double Value
    {
        get => _value;
        set => SetParam(ref _value, Clamp(value));
    }

    [Param("Min", Group = "Range", Description = "Lower end of the slider.")]
    public double Minimum
    {
        get => _minimum;
        set
        {
            if (SetParam(ref _minimum, value))
                Value = Clamp(_value);
        }
    }

    [Param("Max", Group = "Range", Description = "Upper end of the slider.")]
    public double Maximum
    {
        get => _maximum;
        set
        {
            if (SetParam(ref _maximum, value))
                Value = Clamp(_value);
        }
    }

    private double Clamp(double value) =>
        _minimum <= _maximum ? Math.Clamp(value, _minimum, _maximum) : value;

    protected override void OnEvaluate(EvalContext context) => context.Set(ValueOut, _value);
}

/// <summary>
/// A two-component value, such as a kernel size or an offset.
/// </summary>
[VisionNode(
    "Vector",
    TypeKey = "source.vector",
    Category = NodeCategory.Source,
    Icon = "ph_vector",
    Description = "A pair of numbers, for sizes and offsets.")]
public sealed class VectorNode : VisionNode
{
    private double _x = 3;
    private double _y = 3;

    public VectorNode()
    {
        VectorOut = AddOutput<Vector2>("Out");
    }

    public OutputPort VectorOut { get; }

    [Param("X", Step = 0.01)]
    public double X
    {
        get => _x;
        set => SetParam(ref _x, value);
    }

    [Param("Y", Step = 0.01)]
    public double Y
    {
        get => _y;
        set => SetParam(ref _y, value);
    }

    /// <summary>Read-only echo of the pair, so the inspector shows "(3, 3)".</summary>
    public string Formatted => new Vector2(_x, _y).ToString();

    protected override void OnEvaluate(EvalContext context) =>
        context.Set(VectorOut, new Vector2(_x, _y));
}
