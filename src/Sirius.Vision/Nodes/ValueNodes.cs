using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>How a Number node's value is entered.</summary>
public enum NumberEntry
{
    /// <summary>Whole numbers only; the slider steps by one.</summary>
    Integer,

    /// <summary>Any value.</summary>
    Decimal,
}

/// <summary>
/// A number, for driving connectable parameters on other nodes.
/// </summary>
/// <remarks>
/// <para>
/// On its own this does nothing. Its purpose is to let one value feed
/// several parameters at once - wire it into two thresholds and they stay
/// in step - and to give a single place to adjust a figure used in more
/// than one node.
/// </para>
/// <para>
/// One node where there were two, Integer and Float, and the dropdown
/// chooses how the value is <em>entered</em> rather than what type crosses
/// the wire. The output is always a double, because the distinction was
/// never visible downstream: <c>VisionGraph.IsCompatible</c> already treats
/// every numeric type as interchangeable, and <c>EvalContext.Param</c>
/// converts on read. A dropdown that switched the port's type would be
/// machinery with no observable effect, and changing a wired node's port
/// type is the one way to make that choice actually dangerous.
/// </para>
/// <para>
/// What the setting does buy is the editor: whole numbers step by one and
/// show no decimal point, which is what a kernel size or an iteration
/// count wants.
/// </para>
/// </remarks>
[VisionNode(
    "Number",
    TypeKey = "source.number",
    Category = NodeCategory.Source,
    Icon = "ph_number",
    Description = "A number that can drive parameters on other nodes.")]
public sealed class NumberNode : VisionNode
{
    private NumberEntry _entry = NumberEntry.Integer;
    private double _value = 128;
    private double _minimum;
    private double _maximum = 255;

    public NumberNode()
    {
        ValueOut = AddOutput<double>("Out");
    }

    public OutputPort ValueOut { get; }

    [Param("Type", Description = "Whole numbers, or any value. Affects entry only; the wire carries a number either way.")]
    public NumberEntry Entry
    {
        get => _entry;
        set => SetParam(ref _entry, value);
    }

    [Param("Value",
        Description = "Sent to every parameter wired to this node.",
        Editor = ParamEditor.Slider,
        StepSource = nameof(Step),
        MinSource = nameof(Minimum),
        MaxSource = nameof(Maximum))]
    /// <remarks>
    /// Clamped to the range but deliberately not rounded to the entry
    /// type. Rounding here would silently alter a value that arrived from
    /// a saved recipe or a wire, which is a data change disguised as a
    /// display preference - and it contradicts what the Type setting
    /// claims to do. Whole-number entry is enforced by the slider's step,
    /// where the operator can see it happening.
    /// </remarks>
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
                Value = _value;
        }
    }

    [Param("Max", Group = "Range", Description = "Upper end of the slider.")]
    public double Maximum
    {
        get => _maximum;
        set
        {
            if (SetParam(ref _maximum, value))
                Value = _value;
        }
    }

    /// <summary>What the slider steps by, which is what the type decides.</summary>
    public double Step => _entry == NumberEntry.Integer ? 1 : 0.01;

    private double Clamp(double value) =>
        _minimum <= _maximum ? Math.Clamp(value, _minimum, _maximum) : value;

    protected override void OnEvaluate(EvalContext context) => context.Set(ValueOut, _value);
}

/// <summary>
/// A line of text, for labelling and for parameters that take a word.
/// </summary>
[VisionNode(
    "Text",
    TypeKey = "source.text",
    Category = NodeCategory.Source,
    Icon = "ph_text",
    Description = "A line of text, for labels and text parameters.")]
public sealed class TextNode : VisionNode
{
    private string _text = string.Empty;

    public TextNode()
    {
        TextOut = AddOutput<string>("Out");
    }

    public OutputPort TextOut { get; }

    [Param("Text", Description = "Sent to every parameter wired to this node.")]
    public string Text
    {
        get => _text;
        set => SetParam(ref _text, value ?? string.Empty);
    }

    protected override void OnEvaluate(EvalContext context) => context.Set(TextOut, _text);
}

/// <summary>
/// A grid of numbers - a kernel, a transform, or a vector as the
/// single-row case.
/// </summary>
/// <remarks>
/// Written the way the 2D Filter already wrote its kernel: numbers
/// separated by spaces or commas, rows by semicolons. A vector is one row,
/// so "3, 3" and "1 0 -1; 2 0 -2; 1 0 -1" are the same kind of thing and
/// there is no second node for the simpler case.
/// </remarks>
[VisionNode(
    "Matrix",
    TypeKey = "source.matrix",
    Category = NodeCategory.Source,
    Icon = "ph_matrix",
    Description = "A grid of numbers, rows separated by ';'. One row is a vector.")]
public sealed class MatrixNode : VisionNode
{
    private string _values = "3, 3";

    public MatrixNode()
    {
        MatrixOut = AddOutput<NumberMatrix>("Out");
    }

    public OutputPort MatrixOut { get; }

    [Param("Values",
        Description = "Numbers separated by commas or spaces, rows by ';' - \"3, 3\" or \"1 0; 0 1\".")]
    public string Values
    {
        get => _values;
        set => SetParam(ref _values, value ?? string.Empty);
    }

    /// <summary>The parsed value, or empty when the text does not read.</summary>
    public NumberMatrix Value => NumberMatrix.Parse(_values);

    /// <summary>Read-only echo, so the inspector can show what was understood.</summary>
    public string Formatted => Value.ToString();

    protected override void OnEvaluate(EvalContext context)
    {
        if (!NumberMatrix.TryParse(_values, out var matrix))
        {
            // Reported rather than thrown: a half-typed matrix is a normal
            // state for a text box to be in, and failing the pass while
            // somebody is still typing would be unusable. A ragged grid
            // lands here too, which is the other common typing mistake.
            context.Warn($"'{_values}' is not a rectangular grid of numbers.");
            context.Set(MatrixOut, NumberMatrix.Empty);
            return;
        }

        context.Set(MatrixOut, matrix);
    }
}

/// <summary>
/// Draws a value on the image as a label.
/// </summary>
/// <remarks>
/// <para>
/// The way a number gets from the graph onto the picture. A measurement,
/// a count, a part code read from somewhere - anything that has to be
/// seen by whoever is looking at the result rather than only consumed by
/// the next node.
/// </para>
/// <para>
/// Labels are drawn beside a shape, which the viewer has always done for
/// <c>Shape.Label</c>. So this makes a shape to hang the text on: the
/// shapes wired into it when there are any, one per shape, or a bare
/// point at the given position when there are not.
/// </para>
/// </remarks>
[VisionNode(
    "Label",
    TypeKey = "output.label",
    Category = NodeCategory.Output,
    Icon = "ph_measure_profile",
    Description = "Draws a number or a line of text on the image.")]
public sealed class LabelNode : VisionNode
{
    private const uint Default = 0xFFE6E6E6;

    private string _text = string.Empty;
    private double _number;
    private int _decimals = 2;
    private double _x = 50;
    private double _y = 50;
    private string _colour = "#E6E6E6";

    public LabelNode()
    {
        ShapesIn = AddInput<ShapeSet>("At", isRequired: false);
        ShapesOut = AddOutput<ShapeSet>("Out");
    }

    /// <summary>
    /// Shapes to label, one each. Optional: without them the label is
    /// placed at the given position.
    /// </summary>
    public InputPort ShapesIn { get; }

    public OutputPort ShapesOut { get; }

    [Param("Text",
        Connectable = true,
        Description = "What to write. Wire a Text node in, or leave blank to write the number.")]
    public string Text
    {
        get => _text;
        set => SetParam(ref _text, value ?? string.Empty);
    }

    [Param("Number",
        Connectable = true,
        Description = "Written when Text is blank - wire a measurement in.")]
    public double Number
    {
        get => _number;
        set => SetParam(ref _number, value);
    }

    [Param("Decimals", Min = 0, Max = 6, Step = 1, Description = "Figures after the point.")]
    public int Decimals
    {
        get => _decimals;
        set => SetParam(ref _decimals, Math.Clamp(value, 0, 6));
    }

    [Param("X", Group = "Position", Connectable = true,
        Description = "Where to write it when nothing is wired into At.")]
    public double X
    {
        get => _x;
        set => SetParam(ref _x, value);
    }

    [Param("Y", Group = "Position", Connectable = true)]
    public double Y
    {
        get => _y;
        set => SetParam(ref _y, value);
    }

    [Param("Colour", Editor = ParamEditor.Colour, Description = "Pick one, or type #RRGGBB.")]
    public string Colour
    {
        get => _colour;
        set => SetParam(ref _colour, value ?? string.Empty);
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var text = context.Param(nameof(Text), _text);

        if (string.IsNullOrEmpty(text))
        {
            var number = context.Param(nameof(Number), _number);
            text = number.ToString("F" + _decimals, System.Globalization.CultureInfo.InvariantCulture);
        }

        var colour = ShapeStyleNode.ParseColour(_colour, context, Default);
        var attached = context.Get<ShapeSet>(ShapesIn);

        if (attached is { Count: > 0 })
        {
            // One label per shape, placed at each shape's own centre, so a
            // count of blobs can be written on the blobs.
            var labelled = attached.Shapes
                .Select(shape => (Shape)new PointShape(ShapeGeometry.Centre(shape))
                {
                    Label = text,
                    Colour = colour,
                })
                .ToArray();

            context.Set(
                ShapesOut,
                new ShapeSet(labelled, attached.FrameWidth, attached.FrameHeight));

            return;
        }

        var at = new Point2(
            context.Param(nameof(X), _x),
            context.Param(nameof(Y), _y));

        context.Set(
            ShapesOut,
            new ShapeSet([new PointShape(at) { Label = text, Colour = colour }], 0, 0));
    }
}
