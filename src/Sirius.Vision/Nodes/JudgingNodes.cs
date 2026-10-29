using System.Globalization;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>How Compare compares A with B.</summary>
public enum Comparison
{
    /// <summary>A &lt; B.</summary>
    LessThan,

    /// <summary>A &lt;= B.</summary>
    AtMost,

    /// <summary>A = B, within the tolerance.</summary>
    EqualTo,

    /// <summary>A differs from B by more than the tolerance.</summary>
    NotEqualTo,

    /// <summary>A &gt;= B.</summary>
    AtLeast,

    /// <summary>A &gt; B.</summary>
    GreaterThan,
}

/// <summary>
/// Compares two numbers - two measurements, or one against a value typed in.
/// </summary>
/// <remarks>
/// Limit Check holds a number to limits written on a drawing; this holds
/// one measurement against another - this gap no wider than that one, this
/// count equal to that count. Both are checks, so both count toward the
/// recipe's verdict unless something downstream judges their result.
/// </remarks>
[VisionNode(
    "Compare",
    TypeKey = "judging.compare",
    Category = NodeCategory.Judging,
    Icon = "ph_compare",
    Description = "Compares two numbers: less than, at most, equal within a tolerance, at least, greater than.")]
public sealed class CompareNode : VisionNode, IJudgingNode
{
    private double _a;
    private double _b;
    private Comparison _comparison = Comparison.AtMost;
    private double _tolerance;

    public CompareNode()
    {
        ResultOut = AddOutput<bool>("Result");
        DifferenceOut = AddOutput<double>("Difference");
    }

    /// <summary>Whether the comparison holds.</summary>
    public OutputPort ResultOut { get; }

    /// <summary>A less B, so how close it came can be read and logged.</summary>
    public OutputPort DifferenceOut { get; }

    OutputPort IJudgingNode.VerdictPort => ResultOut;

    [Param("A", Connectable = true, Description = "The first number. Wire a measurement in.")]
    public double A
    {
        get => _a;
        set => SetParam(ref _a, value);
    }

    [Param("Is", Description = "How A must compare with B.")]
    public Comparison Comparison
    {
        get => _comparison;
        set => SetParam(ref _comparison, value);
    }

    [Param("B", Connectable = true, Description = "The second number: another measurement, or a value typed in.")]
    public double B
    {
        get => _b;
        set => SetParam(ref _b, value);
    }

    [Param("Tolerance", Min = 0, Connectable = true,
        Description = "For Equal To and Not Equal To: how far apart A and B may be and still count as equal.")]
    public double Tolerance
    {
        get => _tolerance;
        set => SetParam(ref _tolerance, Math.Max(0, value));
    }

    /// <summary>Whether <paramref name="a"/> compares with <paramref name="b"/> as asked. Never true of a missing number.</summary>
    public static bool Holds(double a, Comparison comparison, double b, double tolerance)
    {
        if (double.IsNaN(a) || double.IsNaN(b))
            return false;

        return comparison switch
        {
            Comparison.LessThan => a < b,
            Comparison.AtMost => a <= b,
            Comparison.EqualTo => Math.Abs(a - b) <= Math.Abs(tolerance),
            Comparison.NotEqualTo => Math.Abs(a - b) > Math.Abs(tolerance),
            Comparison.AtLeast => a >= b,
            _ => a > b,
        };
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var a = context.Param(nameof(A), _a);
        var b = context.Param(nameof(B), _b);
        var tolerance = context.Param(nameof(Tolerance), _tolerance);

        context.Set(ResultOut, Holds(a, _comparison, b, tolerance));
        context.Set(DifferenceOut, a - b);
    }
}

/// <summary>How Check Logic combines its inputs.</summary>
public enum LogicRule
{
    /// <summary>True when every input is.</summary>
    All,

    /// <summary>True when at least one is.</summary>
    Any,

    /// <summary>True when none is - a single input, inverted.</summary>
    None,

    /// <summary>True when exactly one is.</summary>
    ExactlyOne,
}

/// <summary>
/// Combines yes/no results: all of them, any of them, none of them.
/// </summary>
/// <remarks>
/// "Every check passed" without a chain of nodes two at a time: wiring one
/// input adds the next. An input whose producer failed has no answer, and
/// no answer counts as no rather than being left out - a check that did
/// not run has not passed.
/// </remarks>
[VisionNode(
    "Check Logic",
    TypeKey = "judging.logic",
    Category = NodeCategory.Judging,
    Icon = "ph_check_logic",
    Description = "Combines yes/no results - all, any, none, exactly one. Wiring an input adds another.")]
public sealed class CheckLogicNode : VisionNode, IJudgingNode, IGrowingPorts
{
    private const string First = "In";

    private readonly GrowingInputs _row;
    private LogicRule _rule = LogicRule.All;

    public CheckLogicNode()
    {
        AddInput<bool>(First, isRequired: false);
        ResultOut = AddOutput<bool>("Result");
        TrueCountOut = AddOutput<int>("True Count");

        _row = new GrowingInputs(
            First,
            limit: 16,
            () => Sockets,
            (after, name) => InsertInput<bool>(Inputs.ToList().IndexOf(after) + 1, name, isRequired: false),
            RemoveInput,
            FindInput);
    }

    /// <summary>The yes/no sockets, in order.</summary>
    public IReadOnlyList<InputPort> Sockets => Inputs
        .Where(port => port.DataType == typeof(bool) && !IsParameterPort(port))
        .ToArray();

    public OutputPort ResultOut { get; }

    /// <summary>How many inputs were true.</summary>
    public OutputPort TrueCountOut { get; }

    OutputPort IJudgingNode.VerdictPort => ResultOut;

    [Param("Rule", Description = "All true, any true, none true, or exactly one true.")]
    public LogicRule Rule
    {
        get => _rule;
        set => SetParam(ref _rule, value);
    }

    public bool Fit() => _row.Fit();

    public InputPort? EnsureInput(string name) => _row.Ensure(name);

    /// <summary>The rule applied to some answers.</summary>
    public static bool Apply(LogicRule rule, IReadOnlyCollection<bool> answers) => rule switch
    {
        LogicRule.All => answers.All(answer => answer),
        LogicRule.Any => answers.Any(answer => answer),
        LogicRule.None => !answers.Any(answer => answer),
        _ => answers.Count(answer => answer) == 1,
    };

    protected override void OnEvaluate(EvalContext context)
    {
        var wired = _row.Connected.ToArray();

        if (wired.Length == 0)
        {
            context.Warn("Nothing is wired in.");
            context.Set(ResultOut, false);
            context.Set(TrueCountOut, 0);
            return;
        }

        var answers = wired.Select(port => context.Get<object>(port) is true).ToArray();
        var unanswered = wired.Count(port => context.Get<object>(port) is null);

        if (unanswered > 0)
            context.Warn($"{unanswered} input(s) had no answer and counted as no.");

        context.Set(ResultOut, Apply(_rule, answers));
        context.Set(TrueCountOut, answers.Count(answer => answer));
    }
}

/// <summary>Which way Grade's limits run.</summary>
public enum BetterIs
{
    /// <summary>Smaller values grade higher - a deviation, a defect size.</summary>
    Lower,

    /// <summary>Larger values grade higher - a match score, a contrast.</summary>
    Higher,
}

/// <summary>
/// Grades a number into bands - A, B, C, Reject - rather than passing or
/// failing it.
/// </summary>
/// <remarks>
/// <para>
/// The grades are a list, best first, and the limits divide the number
/// line between them: with grades A, B, C, Reject and limits 0.05, 0.1,
/// 0.2, a value up to 0.05 is A, up to 0.1 is B, up to 0.2 is C, and
/// anything above is Reject. A value exactly on a limit takes the better
/// grade, as a value on a Limit Check's limit passes.
/// </para>
/// <para>
/// Which grades are acceptable is said separately, so a part can be graded
/// C and still ship. The grade, a pass or fail, the number and the
/// incoming drawing recoloured by grade all come out: green for the best
/// grade, amber for the others that pass, red for those that fail.
/// </para>
/// </remarks>
[VisionNode(
    "Grade",
    TypeKey = "judging.grade",
    Category = NodeCategory.Judging,
    Icon = "ph_grade",
    Description = "Grades a number into bands - A, B, C, Reject - with the grades that fail named. " +
                  "Colours the measurement by grade.")]
public sealed class GradeNode : VisionNode, IGradingNode
{
    private const uint Best = 0xFF22C55E;
    private const uint Acceptable = 0xFFF59E0B;
    private const uint Rejected = 0xFFEF4444;

    private double _value;
    private string _grades = "A, B, C, Reject";
    private string _limits = "0.05, 0.1, 0.2";
    private string _failing = "Reject";
    private BetterIs _betterIs = BetterIs.Lower;
    private int _decimals = 2;
    private bool _showGrade = true;

    public GradeNode()
    {
        AnnotationIn = AddInput<ShapeSet>("Annotation", isRequired: false);

        GradeOut = AddOutput<Grade>("Grade");
        PassOut = AddOutput<bool>("Pass");
        ValueOut = AddOutput<double>("Value");
        AnnotationOut = AddOutput<ShapeSet>("Annotation");
    }

    /// <summary>The drawing to colour by grade.</summary>
    public InputPort AnnotationIn { get; }

    public OutputPort GradeOut { get; }

    /// <summary>Whether the grade is one that passes.</summary>
    public OutputPort PassOut { get; }

    /// <summary>The number, unchanged.</summary>
    public OutputPort ValueOut { get; }

    public OutputPort AnnotationOut { get; }

    OutputPort IJudgingNode.VerdictPort => PassOut;

    OutputPort IGradingNode.GradePort => GradeOut;

    [Param("Value", Connectable = true, Description = "The number to grade. Wire a measurement in.")]
    public double Value
    {
        get => _value;
        set => SetParam(ref _value, value);
    }

    [Param("Grades", Group = "Grades",
        Description = "The grades, best first, separated by commas.")]
    public string Grades
    {
        get => _grades;
        set => SetParam(ref _grades, value ?? string.Empty);
    }

    [Param("Limits", Group = "Grades",
        Description = "Where each grade ends, one fewer than there are grades, separated by commas. " +
                      "A value on a limit takes the better grade.")]
    public string Limits
    {
        get => _limits;
        set => SetParam(ref _limits, value ?? string.Empty);
    }

    [Param("Better Is", Group = "Grades",
        Description = "Lower: smaller values grade higher, limits rising. Higher: larger values grade higher, limits falling.")]
    public BetterIs BetterIs
    {
        get => _betterIs;
        set => SetParam(ref _betterIs, value);
    }

    [Param("Failing", Group = "Grades",
        Description = "The grades that fail the part, separated by commas. The rest pass.")]
    public string Failing
    {
        get => _failing;
        set => SetParam(ref _failing, value ?? string.Empty);
    }

    [Param("Decimals", Min = 0, Max = 6, Step = 1,
        Description = "Figures after the point in the label.")]
    public int Decimals
    {
        get => _decimals;
        set => SetParam(ref _decimals, Math.Clamp(value, 0, 6));
    }

    [Param("Show Grade", Description = "Write the grade on the annotation.")]
    public bool ShowGrade
    {
        get => _showGrade;
        set => SetParam(ref _showGrade, value);
    }

    /// <summary>
    /// The grade <paramref name="value"/> falls in. A missing number takes
    /// the worst grade: it has not been shown to be any better.
    /// </summary>
    public static Grade Classify(
        double value, IReadOnlyList<string> grades, IReadOnlyList<double> limits, BetterIs betterIs, ISet<string> failing)
    {
        var index = grades.Count - 1;

        if (!double.IsNaN(value))
        {
            for (var i = 0; i < limits.Count && i < grades.Count - 1; i++)
            {
                var within = betterIs == BetterIs.Lower ? value <= limits[i] : value >= limits[i];

                if (within)
                {
                    index = i;
                    break;
                }
            }
        }

        return new Grade(grades[index], index, !failing.Contains(grades[index]));
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var value = context.Param(nameof(Value), _value);

        var grades = Split(_grades);
        if (grades.Length == 0)
            throw new InvalidOperationException("Name at least one grade, best first - for example A, B, C, Reject.");

        var limits = ParseLimits(_limits);

        if (limits.Length != grades.Length - 1)
        {
            throw new InvalidOperationException(
                $"{grades.Length} grades need {grades.Length - 1} limits between them; {limits.Length} given.");
        }

        var ordered = _betterIs == BetterIs.Lower
            ? limits.Zip(limits.Skip(1)).All(pair => pair.First <= pair.Second)
            : limits.Zip(limits.Skip(1)).All(pair => pair.First >= pair.Second);

        if (!ordered)
        {
            context.Warn(_betterIs == BetterIs.Lower
                ? "The limits should rise from the best grade to the worst."
                : "The limits should fall from the best grade to the worst.");
        }

        var failing = new HashSet<string>(Split(_failing), StringComparer.OrdinalIgnoreCase);
        var unknown = failing.Where(name => !grades.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();

        if (unknown.Length > 0)
            context.Warn($"Failing names {string.Join(", ", unknown)}, which is not among the grades.");

        var grade = Classify(value, grades, limits, _betterIs, failing);

        context.Set(GradeOut, grade);
        context.Set(PassOut, grade.Passes);
        context.Set(ValueOut, value);
        context.Set(AnnotationOut, Annotate(context, grade, value));
    }

    /// <summary>The incoming drawing recoloured by grade, the first shape labelled with it.</summary>
    private ShapeSet Annotate(EvalContext context, Grade grade, double value)
    {
        var incoming = context.Get<ShapeSet>(AnnotationIn);

        if (incoming is not { Count: > 0 })
            return ShapeSet.Empty;

        var colour = !grade.Passes ? Rejected : grade.Rank == 0 ? Best : Acceptable;
        var marked = new List<Shape>(incoming.Count);

        for (var i = 0; i < incoming.Count; i++)
        {
            var shape = incoming.Shapes[i];

            marked.Add(shape with
            {
                Colour = colour,
                Label = i == 0 && _showGrade ? LabelFor(shape, grade, value) : shape.Label,
            });
        }

        return new ShapeSet(marked, incoming.FrameWidth, incoming.FrameHeight);
    }

    private string LabelFor(Shape shape, Grade grade, double value) =>
        string.IsNullOrEmpty(shape.Label)
            ? string.Format(CultureInfo.InvariantCulture, "{0:F" + _decimals + "}  {1}", value, grade.Name)
            : $"{shape.Label}  {grade.Name}";

    internal static string[] Split(string text) =>
        text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static double[] ParseLimits(string text)
    {
        var parts = Split(text);
        var limits = new double[parts.Length];

        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out limits[i]))
                throw new InvalidOperationException($"'{parts[i]}' in Limits is not a number.");
        }

        return limits;
    }
}

/// <summary>
/// The worst of several grades or yes/no results - the part's grade from
/// every check on it.
/// </summary>
/// <remarks>
/// A yes/no reads as Pass or Fail. Any failing grade is worse than every
/// passing one; between two that both pass, or both fail, the one further
/// down its own list is worse. An input whose producer failed has no
/// answer, and counts as Fail.
/// </remarks>
[VisionNode(
    "Worst Grade",
    TypeKey = "judging.worstGrade",
    Category = NodeCategory.Judging,
    Icon = "ph_worst_grade",
    Description = "The worst of the grades and yes/no results wired in - the part's overall grade. " +
                  "Wiring an input adds another.")]
public sealed class WorstGradeNode : VisionNode, IGradingNode, IGrowingPorts
{
    private const string First = "In";

    private readonly GrowingInputs _row;

    public WorstGradeNode()
    {
        AddInput<object>(First, isRequired: false);
        GradeOut = AddOutput<Grade>("Grade");
        PassOut = AddOutput<bool>("Pass");

        _row = new GrowingInputs(
            First,
            limit: 16,
            () => Sockets,
            (after, name) => InsertInput<object>(Inputs.ToList().IndexOf(after) + 1, name, isRequired: false),
            RemoveInput,
            FindInput);
    }

    /// <summary>The input sockets, in order.</summary>
    public IReadOnlyList<InputPort> Sockets => Inputs
        .Where(port => port.DataType == typeof(object) && !IsParameterPort(port))
        .ToArray();

    public OutputPort GradeOut { get; }

    public OutputPort PassOut { get; }

    OutputPort IJudgingNode.VerdictPort => PassOut;

    OutputPort IGradingNode.GradePort => GradeOut;

    public bool Fit() => _row.Fit();

    public InputPort? EnsureInput(string name) => _row.Ensure(name);

    protected override void OnEvaluate(EvalContext context)
    {
        var wired = _row.Connected.ToArray();

        if (wired.Length == 0)
        {
            context.Warn("Nothing is wired in.");
            context.Set(GradeOut, Grade.Fail);
            context.Set(PassOut, false);
            return;
        }

        var grades = new List<Grade>(wired.Length);

        foreach (var port in wired)
        {
            var value = context.Get<object>(port);

            switch (Grade.From(value))
            {
                case { } grade:
                    grades.Add(grade);
                    break;

                case null when value is null:
                    context.Warn($"{port.DisplayName} had no answer and counted as Fail.");
                    grades.Add(Grade.Fail);
                    break;

                default:
                    context.Warn($"{port.DisplayName} is not a grade or a yes/no, and was left out.");
                    break;
            }
        }

        var worst = Grade.Worst(grades) ?? Grade.Fail;

        context.Set(GradeOut, worst);
        context.Set(PassOut, worst.Passes);
    }
}

/// <summary>
/// Counts inspections, and how many passed, failed and took each grade.
/// </summary>
/// <remarks>
/// <para>
/// Counts an inspection - a press of Run, each image of a folder run - and
/// not the re-runs that follow every edit, which would count the tuning
/// rather than the parts. With nothing wired in it counts inspections;
/// with a yes/no or a grade, it counts each outcome too.
/// </para>
/// <para>
/// The counts last for the session: they are not saved with the recipe,
/// and Reset Counters on the Run menu clears them.
/// </para>
/// </remarks>
[VisionNode(
    "Counter",
    TypeKey = "judging.counter",
    Category = NodeCategory.Judging,
    Icon = "ph_counter",
    Description = "Counts inspections - each Run, each image of a folder run - and how many passed, failed " +
                  "and took each grade. Reset from the Run menu.")]
public sealed class CounterNode : VisionNode, IInspectionNode
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (int Rank, bool Passes, int Count)> _byGrade = new(StringComparer.Ordinal);
    private int _total;
    private int _passed;
    private int _failed;

    public CounterNode()
    {
        ResultIn = AddInput<object>("Result", isRequired: false);

        TotalOut = AddOutput<int>("Total");
        PassedOut = AddOutput<int>("Passed");
        FailedOut = AddOutput<int>("Failed");
        YieldOut = AddOutput<double>("Yield %");
        CountsOut = AddOutput<string>("Counts");
    }

    /// <summary>A yes/no or a grade to tally. Left unwired, only inspections are counted.</summary>
    public InputPort ResultIn { get; }

    public OutputPort TotalOut { get; }

    public OutputPort PassedOut { get; }

    public OutputPort FailedOut { get; }

    /// <summary>Passed as a percentage of those judged.</summary>
    public OutputPort YieldOut { get; }

    /// <summary>Each grade and its count - "A 12, B 3, Reject 1" - best first, failing grades last.</summary>
    public OutputPort CountsOut { get; }

    /// <summary>Starts every count again from zero.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _total = 0;
            _passed = 0;
            _failed = 0;
            _byGrade.Clear();
        }
    }

    protected override void OnEvaluate(EvalContext context)
    {
        lock (_gate)
        {
            if (context.IsInspection)
                Count(context);

            context.Set(TotalOut, _total);
            context.Set(PassedOut, _passed);
            context.Set(FailedOut, _failed);
            context.Set(YieldOut, _passed + _failed == 0 ? 0d : 100d * _passed / (_passed + _failed));
            context.Set(CountsOut, Describe());
        }
    }

    private void Count(EvalContext context)
    {
        _total++;

        if (!ResultIn.IsConnected)
            return;

        var value = context.Get<object>(ResultIn);
        var grade = Grade.From(value);

        if (grade is null)
        {
            // A check that did not run has not passed.
            if (value is not null)
                context.Warn("Result is not a grade or a yes/no; counted as Fail.");

            grade = Grade.Fail;
        }

        if (grade.Passes)
            _passed++;
        else
            _failed++;

        _byGrade[grade.Name] = _byGrade.TryGetValue(grade.Name, out var tally)
            ? tally with { Count = tally.Count + 1 }
            : (grade.Rank, grade.Passes, 1);
    }

    private string Describe() => string.Join(
        ", ",
        _byGrade
            .OrderBy(entry => !entry.Value.Passes)
            .ThenBy(entry => entry.Value.Rank)
            .Select(entry => $"{entry.Key} {entry.Value.Count}"));
}
