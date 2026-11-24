using System.Globalization;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>How a Limit Check decides.</summary>
public enum LimitKind
{
    /// <summary>Inside a band, inclusive of both ends.</summary>
    Between,

    /// <summary>At or above the minimum, with no upper bound.</summary>
    AtLeast,

    /// <summary>At or below the maximum, with no lower bound.</summary>
    AtMost,

    /// <summary>
    /// Within a tolerance either side of a nominal value, which is how a
    /// tolerance arrives from a drawing.
    /// </summary>
    Nominal,
}

/// <summary>
/// Compares a number against limits and says whether the part passed.
/// </summary>
/// <remarks>
/// <para>
/// What turns a measurement into an inspection. Until now a recipe could
/// find a feature, measure it and write the reading on the picture, and
/// the last step - deciding whether that reading is acceptable - was left
/// to whoever was looking at the screen.
/// </para>
/// <para>
/// It takes a number rather than a measurement, so anything that produces
/// one can be checked: a Measure node's reading, a blob's area through
/// Select Shape, a count. Wiring Measure's Value in is the case it was
/// built for, and wiring its Annotation in as well is what makes the
/// verdict visible - the measurement already drawn on the image turns
/// green or red rather than a second mark appearing beside it.
/// </para>
/// <para>
/// The verdict is published three ways because three different things
/// want it: <c>Pass</c> for the graph, the recoloured annotation for the
/// operator, and <c>Value</c> passed through so a check can sit in the
/// middle of a chain without breaking it.
/// </para>
/// </remarks>
[VisionNode(
    "Limit Check",
    TypeKey = "analysis.limitCheck",
    Category = NodeCategory.Judging,
    Icon = "ph_limit_check",
    Description = "Passes or fails a number against limits, and colours the measurement.")]
public sealed class LimitCheckNode : VisionNode, IJudgingNode
{
    /// <summary>Green, as a found feature is drawn.</summary>
    private const uint Passed = 0xFF22C55E;

    /// <summary>Red, which is used for nothing else that is drawn.</summary>
    private const uint Failed = 0xFFEF4444;

    private LimitKind _kind = LimitKind.Between;
    private double _value;
    private double _minimum;
    private double _maximum = 100;
    private double _nominal;
    private double _tolerance = 1;
    private int _decimals = 2;
    private bool _showVerdict = true;

    public LimitCheckNode()
    {
        AnnotationIn = AddInput<ShapeSet>("Annotation", isRequired: false);

        PassOut = AddOutput<bool>("Pass");
        ValueOut = AddOutput<double>("Value");
        AnnotationOut = AddOutput<ShapeSet>("Annotation");
    }

    /// <summary>
    /// The drawing to colour by the verdict - a Measure node's
    /// annotation, or the shapes a find tool produced.
    /// </summary>
    public InputPort AnnotationIn { get; }

    /// <summary>Whether the number was within the limits.</summary>
    public OutputPort PassOut { get; }

    /// <inheritdoc />
    OutputPort IJudgingNode.VerdictPort => PassOut;

    /// <summary>The number, unchanged, so a check can sit mid-chain.</summary>
    public OutputPort ValueOut { get; }

    /// <summary>What came in, recoloured, and labelled with the verdict.</summary>
    public OutputPort AnnotationOut { get; }

    [Param("Value", Connectable = true,
        Description = "The number to check. Wire a measurement in.")]
    public double Value
    {
        get => _value;
        set => SetParam(ref _value, value);
    }

    [Param("Check", Description = "Which limits apply.")]
    public LimitKind Kind
    {
        get => _kind;
        set => SetParam(ref _kind, value);
    }

    [Param("Minimum", Group = "Limits", Connectable = true,
        Description = "Used by Between and At Least.")]
    public double Minimum
    {
        get => _minimum;
        set => SetParam(ref _minimum, value);
    }

    [Param("Maximum", Group = "Limits", Connectable = true,
        Description = "Used by Between and At Most.")]
    public double Maximum
    {
        get => _maximum;
        set => SetParam(ref _maximum, value);
    }

    [Param("Nominal", Group = "Limits", Connectable = true,
        Description = "Used by Nominal: the value the part is meant to be.")]
    public double Nominal
    {
        get => _nominal;
        set => SetParam(ref _nominal, value);
    }

    [Param("Tolerance", Group = "Limits", Connectable = true, Min = 0,
        Description = "Used by Nominal: how far either side of it still passes.")]
    public double Tolerance
    {
        get => _tolerance;
        set => SetParam(ref _tolerance, Math.Max(0, value));
    }

    [Param("Decimals", Min = 0, Max = 6, Step = 1,
        Description = "Figures after the point in the verdict label.")]
    public int Decimals
    {
        get => _decimals;
        set => SetParam(ref _decimals, Math.Clamp(value, 0, 6));
    }

    [Param("Verdict", Description = "Write PASS or FAIL on the annotation.")]
    public bool ShowVerdict
    {
        get => _showVerdict;
        set => SetParam(ref _showVerdict, value);
    }

    /// <summary>
    /// Whether <paramref name="value"/> is acceptable.
    /// </summary>
    /// <remarks>
    /// Static and pure, because this is the one piece anybody will want
    /// to argue about - whether a reading exactly on a limit passes. It
    /// does: a limit is the edge of what is acceptable, not the first
    /// value that is not, and a drawing saying 10 ± 0.1 accepts 10.1.
    /// </remarks>
    public static bool IsWithin(
        double value, LimitKind kind, double minimum, double maximum,
        double nominal, double tolerance)
    {
        if (double.IsNaN(value))
            return false;

        return kind switch
        {
            LimitKind.AtLeast => value >= minimum,
            LimitKind.AtMost => value <= maximum,
            LimitKind.Nominal => Math.Abs(value - nominal) <= Math.Abs(tolerance),
            _ => value >= Math.Min(minimum, maximum) && value <= Math.Max(minimum, maximum),
        };
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var value = context.Param(nameof(Value), _value);
        var minimum = context.Param(nameof(Minimum), _minimum);
        var maximum = context.Param(nameof(Maximum), _maximum);
        var nominal = context.Param(nameof(Nominal), _nominal);
        var tolerance = context.Param(nameof(Tolerance), _tolerance);

        if (_kind == LimitKind.Between && minimum > maximum)
        {
            // Said rather than silently swapped, because a band typed the
            // wrong way round is as likely to be the wrong numbers as the
            // wrong order. The check still runs, on the band as read.
            context.Warn(
                $"Minimum ({minimum}) is above Maximum ({maximum}); " +
                "checking between them anyway.");
        }

        var pass = IsWithin(value, _kind, minimum, maximum, nominal, tolerance);

        context.Set(PassOut, pass);
        context.Set(ValueOut, value);
        context.Set(AnnotationOut, Annotate(context, pass, value));
    }

    /// <summary>
    /// The incoming drawing, recoloured by the verdict.
    /// </summary>
    /// <remarks>
    /// Recoloured rather than added to. The measurement is already on the
    /// picture in the place it was taken; drawing a second mark to carry
    /// the verdict would say the same thing twice and cover something
    /// else to do it.
    /// </remarks>
    private ShapeSet Annotate(EvalContext context, bool pass, double value)
    {
        var incoming = context.Get<ShapeSet>(AnnotationIn);

        if (incoming is not { Count: > 0 })
            return ShapeSet.Empty;

        var colour = pass ? Passed : Failed;
        var marked = new List<Shape>(incoming.Count);

        for (var i = 0; i < incoming.Count; i++)
        {
            var shape = incoming.Shapes[i];

            marked.Add(shape with
            {
                Colour = colour,

                // Only the first is labelled. A set of twenty blobs
                // checked against one limit would otherwise be twenty
                // copies of the same word.
                Label = i == 0 ? LabelFor(shape, pass, value) : shape.Label,
            });
        }

        return new ShapeSet(marked, incoming.FrameWidth, incoming.FrameHeight);
    }

    /// <summary>
    /// The verdict, kept beside the reading it was reached from.
    /// </summary>
    /// <remarks>
    /// "12.34  PASS" rather than "PASS" alone: a verdict without its
    /// number says nothing about how close the part came, which is the
    /// thing anybody tuning a limit needs to see.
    /// </remarks>
    private string? LabelFor(Shape shape, bool pass, double value)
    {
        if (!_showVerdict)
            return shape.Label;

        var verdict = pass ? "PASS" : "FAIL";

        return string.IsNullOrEmpty(shape.Label)
            ? string.Format(CultureInfo.InvariantCulture, "{0:F" + _decimals + "}  {1}", value, verdict)
            : $"{shape.Label}  {verdict}";
    }
}
