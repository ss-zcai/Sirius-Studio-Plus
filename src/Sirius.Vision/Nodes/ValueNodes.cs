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
/// A list of numbers travelling as one value - a size, an offset, a kernel
/// extent, a per-channel setting.
/// </summary>
/// <remarks>
/// One text box rather than a numeric field per component. It was a pair of
/// X and Y fields, and two is the wrong number as soon as anything wants
/// three: a kernel is two, an RGB offset is three, a per-channel threshold
/// is however many channels the frame has. Fields would have to be added
/// and removed as the count changed, and "3, 3, 1" is quicker to type and
/// quicker to read than three boxes.
/// </remarks>
[VisionNode(
    "Vector",
    TypeKey = "source.vector",
    Category = NodeCategory.Source,
    Icon = "ph_vector",
    Description = "A list of numbers, for sizes, offsets and per-channel settings.")]
public sealed class VectorNode : VisionNode
{
    private string _components = "3, 3";

    public VectorNode()
    {
        VectorOut = AddOutput<NumberVector>("Out");
    }

    public OutputPort VectorOut { get; }

    [Param("Components",
        Description = "Numbers separated by commas or spaces, such as \"3, 3\" or \"3, 3, 1\".")]
    public string Components
    {
        get => _components;
        set => SetParam(ref _components, value ?? string.Empty);
    }

    /// <summary>The parsed value, or empty when the text does not read.</summary>
    public NumberVector Value => NumberVector.Parse(_components);

    /// <summary>Read-only echo, so the inspector can show what was understood.</summary>
    public string Formatted => Value.ToString();

    protected override void OnEvaluate(EvalContext context)
    {
        if (!NumberVector.TryParse(_components, out var vector))
        {
            // Reported rather than thrown: a half-typed vector is a normal
            // state for a text box to be in, and failing the whole pass
            // while somebody is still typing would be unusable.
            context.Warn($"'{_components}' is not a list of numbers.");
            context.Set(VectorOut, NumberVector.Empty);
            return;
        }

        context.Set(VectorOut, vector);
    }
}
