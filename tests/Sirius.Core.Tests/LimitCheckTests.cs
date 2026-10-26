using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Studio.App.ViewModels;
using Sirius.Core.Shapes;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Turning a reading into a verdict.
/// </summary>
/// <remarks>
/// A recipe could find a feature, measure it and write the number on the
/// picture; deciding whether that number was acceptable was left to
/// whoever was looking at the screen. This is the step that closes it.
/// </remarks>
public class LimitCheckTests
{
    public LimitCheckTests() => TestCatalog.Ensure();

    // ---- the decision ----------------------------------------------------

    [Theory]
    [InlineData(5, true)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(0.99, false)]
    [InlineData(10.01, false)]
    public void Between_accepts_the_band_and_both_ends(double value, bool expected) =>
        Assert.Equal(expected, LimitCheckNode.IsWithin(value, LimitKind.Between, 1, 10, 0, 0));

    [Theory]
    [InlineData(10, true)]
    [InlineData(1000, true)]
    [InlineData(9.99, false)]
    public void At_least_has_no_upper_bound(double value, bool expected) =>
        Assert.Equal(expected, LimitCheckNode.IsWithin(value, LimitKind.AtLeast, 10, 0, 0, 0));

    [Theory]
    [InlineData(10, true)]
    [InlineData(-1000, true)]
    [InlineData(10.01, false)]
    public void At_most_has_no_lower_bound(double value, bool expected) =>
        Assert.Equal(expected, LimitCheckNode.IsWithin(value, LimitKind.AtMost, 0, 10, 0, 0));

    /// <summary>
    /// How a tolerance arrives from a drawing: 10 ± 0.1.
    /// </summary>
    [Theory]
    [InlineData(10, true)]
    [InlineData(10.1, true)]
    [InlineData(9.9, true)]
    [InlineData(10.11, false)]
    [InlineData(9.89, false)]
    public void Nominal_accepts_either_side(double value, bool expected) =>
        Assert.Equal(expected, LimitCheckNode.IsWithin(value, LimitKind.Nominal, 0, 0, 10, 0.1));

    /// <summary>
    /// A reading exactly on a limit passes. A limit is the edge of what
    /// is acceptable, not the first value that is not - a drawing saying
    /// 10 ± 0.1 accepts 10.1.
    /// </summary>
    [Fact]
    public void A_reading_on_the_limit_passes()
    {
        Assert.True(LimitCheckNode.IsWithin(10, LimitKind.Between, 1, 10, 0, 0));
        Assert.True(LimitCheckNode.IsWithin(1, LimitKind.Between, 1, 10, 0, 0));
        Assert.True(LimitCheckNode.IsWithin(10.1, LimitKind.Nominal, 0, 0, 10, 0.1));
    }

    /// <summary>
    /// A tolerance of nothing is a check for one exact value, which is
    /// odd but not wrong, and better than silently accepting everything.
    /// </summary>
    [Fact]
    public void A_tolerance_of_nothing_accepts_only_the_nominal()
    {
        Assert.True(LimitCheckNode.IsWithin(10, LimitKind.Nominal, 0, 0, 10, 0));
        Assert.False(LimitCheckNode.IsWithin(10.0001, LimitKind.Nominal, 0, 0, 10, 0));
    }

    /// <summary>
    /// Nothing measured is not a pass. A failed find publishes zero, and
    /// a band that happens to include zero would wave it through; NaN is
    /// what a reading that could not be taken should be, and it fails.
    /// </summary>
    [Fact]
    public void A_reading_that_is_not_a_number_fails() =>
        Assert.False(LimitCheckNode.IsWithin(double.NaN, LimitKind.Between, -1, 1, 0, 0));

    // ---- the node --------------------------------------------------------

    [Fact]
    public void It_publishes_the_verdict_and_the_value()
    {
        var (pass, value, _) = Run(node =>
        {
            node.Value = 7;
            node.Minimum = 1;
            node.Maximum = 10;
        });

        Assert.True(pass);
        Assert.Equal(7, value);
    }

    /// <summary>
    /// The number goes through unchanged, so a check can sit in the
    /// middle of a chain rather than at the end of one.
    /// </summary>
    [Fact]
    public void A_failing_check_still_passes_the_number_on()
    {
        var (pass, value, _) = Run(node =>
        {
            node.Value = 99;
            node.Minimum = 1;
            node.Maximum = 10;
        });

        Assert.False(pass);
        Assert.Equal(99, value);
    }

    /// <summary>
    /// A band typed the wrong way round is as likely to be the wrong
    /// numbers as the wrong order, so it is said rather than silently
    /// swapped - but the check still runs.
    /// </summary>
    [Fact]
    public void A_band_the_wrong_way_round_warns()
    {
        var graph = new VisionGraph();
        var check = new LimitCheckNode { Value = 5, Minimum = 10, Maximum = 1 };
        graph.Add(check);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Warning, check.Status);
        Assert.True((bool)scheduler.GetOutput(check.PassOut)!);
    }

    // ---- what it draws ---------------------------------------------------

    [Theory]
    [InlineData(5, 0xFF22C55Eu)]
    [InlineData(50, 0xFFEF4444u)]
    public void The_annotation_takes_the_colour_of_the_verdict(double value, uint colour)
    {
        var (_, _, annotation) = Run(
            node =>
            {
                node.Value = value;
                node.Minimum = 1;
                node.Maximum = 10;
            },
            new LineShape(new Point2(0, 0), new Point2(10, 0)));

        Assert.Equal(colour, Assert.Single(annotation.Shapes).Colour);
    }

    /// <summary>
    /// The reading keeps its place on the picture; the verdict is added
    /// to it rather than drawn somewhere else as a second mark.
    /// </summary>
    [Fact]
    public void The_verdict_joins_the_reading_already_drawn()
    {
        var (_, _, annotation) = Run(
            node =>
            {
                node.Value = 5;
                node.Minimum = 1;
                node.Maximum = 10;
            },
            new LineShape(new Point2(0, 0), new Point2(10, 0)) { Label = "5.00 px" });

        Assert.Equal("5.00 px  PASS", annotation.Shapes[0].Label);
    }

    /// <summary>
    /// With nothing already written, the number goes on with the
    /// verdict: a verdict alone says nothing about how close the part
    /// came, which is what anybody tuning a limit needs to see.
    /// </summary>
    [Fact]
    public void An_unlabelled_shape_gets_the_reading_too()
    {
        var (_, _, annotation) = Run(
            node =>
            {
                node.Value = 12.345;
                node.Minimum = 1;
                node.Maximum = 10;
                node.Decimals = 1;
            },
            new LineShape(new Point2(0, 0), new Point2(10, 0)));

        Assert.Equal("12.3  FAIL", annotation.Shapes[0].Label);
    }

    [Fact]
    public void The_verdict_can_be_left_off()
    {
        var (_, _, annotation) = Run(
            node =>
            {
                node.Value = 5;
                node.ShowVerdict = false;
            },
            new LineShape(new Point2(0, 0), new Point2(10, 0)) { Label = "5.00 px" });

        Assert.Equal("5.00 px", annotation.Shapes[0].Label);
    }

    /// <summary>
    /// One verdict, not one per shape: twenty blobs checked against one
    /// limit would otherwise be twenty copies of the same word.
    /// </summary>
    [Fact]
    public void A_set_of_shapes_is_labelled_once()
    {
        var (_, _, annotation) = Run(
            node =>
            {
                node.Value = 5;
                node.Minimum = 1;
                node.Maximum = 10;
            },
            new CircleShape(new Point2(0, 0), 5),
            new CircleShape(new Point2(30, 0), 5));

        Assert.Equal(2, annotation.Count);
        Assert.Contains("PASS", annotation.Shapes[0].Label!, StringComparison.Ordinal);
        Assert.Null(annotation.Shapes[1].Label);
        Assert.All(annotation.Shapes, shape => Assert.Equal(0xFF22C55Eu, shape.Colour));
    }

    [Fact]
    public void With_nothing_wired_in_there_is_nothing_to_draw()
    {
        var (_, _, annotation) = Run(node => node.Value = 5);

        Assert.Equal(0, annotation.Count);
    }

    /// <summary>
    /// The frame travels with the shapes, or a viewer checking they
    /// belong to the picture would drop them.
    /// </summary>
    [Fact]
    public void The_annotation_keeps_the_frame_it_came_from()
    {
        var (_, _, annotation) = Run(
            node => node.Value = 5,
            new LineShape(new Point2(0, 0), new Point2(10, 0)));

        Assert.Equal(640, annotation.FrameWidth);
        Assert.Equal(480, annotation.FrameHeight);
    }

    // ---- the whole chain --------------------------------------------------

    /// <summary>
    /// Measure into a check, which is what this was built for: two
    /// shapes, the distance between them, and a verdict on it.
    /// </summary>
    [Theory]
    [InlineData(4.5, 5.5, true)]
    [InlineData(1.0, 2.0, false)]
    public void A_measurement_can_be_checked(double minimum, double maximum, bool expected)
    {
        var graph = new VisionGraph();
        var a = new ShapeSourceNode(new CircleShape(new Point2(0, 0), 1));
        var b = new ShapeSourceNode(new CircleShape(new Point2(3, 4), 1));
        var measure = new MeasureNode();
        var check = new LimitCheckNode { Minimum = minimum, Maximum = maximum };

        foreach (var node in new VisionNode[] { a, b, measure, check })
            graph.Add(node);

        graph.TryConnect(a.ShapesOut, measure.FirstIn);
        graph.TryConnect(b.ShapesOut, measure.SecondIn);

        var value = check.Inputs.Single(p => p.Name == nameof(LimitCheckNode.Value));

        Assert.True(graph.TryConnect(measure.ValueOut, value).Success);
        Assert.True(graph.TryConnect(measure.AnnotationOut, check.AnnotationIn).Success);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        // 3-4-5: centre to centre is exactly 5.
        Assert.Equal(5, (double)scheduler.GetOutput(check.ValueOut)!, 6);
        Assert.Equal(expected, (bool)scheduler.GetOutput(check.PassOut)!);

        var drawn = (ShapeSet)scheduler.GetOutput(check.AnnotationOut)!;

        Assert.Equal(expected ? 0xFF22C55Eu : 0xFFEF4444u, drawn.Shapes[0].Colour);
        Assert.Contains(expected ? "PASS" : "FAIL", drawn.Shapes[0].Label!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A limit can itself come from a wire, so one tolerance can drive
    /// several checks and be changed in one place.
    /// </summary>
    [Fact]
    public void A_limit_can_be_driven_by_a_number_node()
    {
        var graph = new VisionGraph();
        var number = new NumberNode { Value = 50 };
        var check = new LimitCheckNode { Value = 20, Kind = LimitKind.AtMost, Maximum = 1 };

        graph.Add(number);
        graph.Add(check);

        var maximum = check.Inputs.Single(p => p.Name == nameof(LimitCheckNode.Maximum));

        Assert.True(graph.TryConnect(number.ValueOut, maximum).Success);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.True((bool)scheduler.GetOutput(check.PassOut)!);
    }

    // ---- the verdict leaving the recipe -----------------------------------

    /// <summary>
    /// A check implements the interface the shell aggregates over, so
    /// the status line can say PASS or FAIL without the shell knowing
    /// what a Limit Check is.
    /// </summary>
    [Fact]
    public void A_check_offers_its_verdict_to_whatever_is_outside_the_graph()
    {
        var check = new LimitCheckNode();

        Assert.Same(check.PassOut, ((IJudgingNode)check).VerdictPort);
    }

    /// <summary>
    /// Nothing else judges yet, so a recipe full of find tools and
    /// measurements reports no verdict rather than a vacuous pass.
    /// </summary>
    [Fact]
    public void Nothing_else_claims_to_judge()
    {
        var judging = NodeCatalog.All
            .Where(d => typeof(IJudgingNode).IsAssignableFrom(d.NodeType))
            .Select(d => d.DisplayName)
            .ToArray();

        Assert.Equal(["Limit Check"], judging);
    }

    // ---- what the status line says ----------------------------------------

    /// <summary>
    /// No checks, no verdict. A recipe of find tools and measurements
    /// has not decided anything, and saying PASS would be a claim it
    /// never made.
    /// </summary>
    [Fact]
    public void A_recipe_that_judges_nothing_reports_nothing() =>
        Assert.Null(MainViewModel.Verdict([], _ => true));

    [Fact]
    public void One_check_reads_as_one_word()
    {
        Assert.Equal("PASS", MainViewModel.Verdict(Checks(true), Reading(true)));
        Assert.Equal("FAIL", MainViewModel.Verdict(Checks(false), Reading(false)));
    }

    /// <summary>
    /// With several, the count matters: which of them failed is on the
    /// picture, but how many is worth knowing before looking.
    /// </summary>
    [Fact]
    public void Several_checks_are_counted()
    {
        var verdicts = new[] { true, false, true, false, false };
        var checks = Checks(verdicts);

        Assert.Equal("FAIL (3 of 5)", MainViewModel.Verdict(checks, Reading(verdicts)));
    }

    [Fact]
    public void All_passing_says_how_many_agreed()
    {
        var verdicts = new[] { true, true, true };

        Assert.Equal("PASS (3 checks)", MainViewModel.Verdict(Checks(verdicts), Reading(verdicts)));
    }

    /// <summary>
    /// A check downstream of a node that failed never produced a
    /// verdict. No answer is not a pass.
    /// </summary>
    [Fact]
    public void A_check_that_never_ran_counts_as_failed() =>
        Assert.Equal("FAIL", MainViewModel.Verdict(Checks(true), _ => null));

    private static IJudgingNode[] Checks(params bool[] verdicts) =>
        verdicts.Select(_ => (IJudgingNode)new LimitCheckNode()).ToArray();

    /// <summary>Answers for the checks in the order they were made.</summary>
    private static Func<OutputPort, object?> Reading(params bool[] verdicts)
    {
        var answers = new Queue<bool>(verdicts);

        return _ => answers.Count > 0 ? answers.Dequeue() : null;
    }

    // ---- plumbing ---------------------------------------------------------

    /// <summary>Publishes a fixed shape set, standing in for a find tool.</summary>
    private sealed class ShapeSourceNode : VisionNode
    {
        public ShapeSourceNode(params Shape[] shapes)
        {
            Set = new ShapeSet(shapes, 640, 480);
            ShapesOut = AddOutput<ShapeSet>("Out");
        }

        public ShapeSet Set { get; }

        public OutputPort ShapesOut { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(ShapesOut, Set);
    }

    private static (bool Pass, double Value, ShapeSet Annotation) Run(
        Action<LimitCheckNode> configure, params Shape[] attached)
    {
        var graph = new VisionGraph();
        var check = new LimitCheckNode();
        configure(check);
        graph.Add(check);

        if (attached.Length > 0)
        {
            var source = new ShapeSourceNode(attached);
            graph.Add(source);
            graph.TryConnect(source.ShapesOut, check.AnnotationIn);
        }

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        return (
            (bool)scheduler.GetOutput(check.PassOut)!,
            (double)scheduler.GetOutput(check.ValueOut)!,
            (ShapeSet)scheduler.GetOutput(check.AnnotationOut)!);
    }

    private static GraphScheduler CreateScheduler(VisionGraph graph)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return new GraphScheduler(graph);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
