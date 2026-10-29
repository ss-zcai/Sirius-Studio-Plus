using System.IO;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Deciding about a part, and keeping the decisions: Compare, Check Logic,
/// Grade, Worst Grade, Counter, Result Log, Filter Shapes - and the
/// inspection run that tells a part being inspected from a slider moving.
/// </summary>
public class JudgingTests
{
    public JudgingTests() => TestCatalog.Ensure();

    // ---- Compare ------------------------------------------------------------------

    [Theory]
    [InlineData(1, Comparison.LessThan, 2, 0, true)]
    [InlineData(2, Comparison.LessThan, 2, 0, false)]
    [InlineData(2, Comparison.AtMost, 2, 0, true)]
    [InlineData(2.05, Comparison.EqualTo, 2, 0.1, true)]
    [InlineData(2.2, Comparison.EqualTo, 2, 0.1, false)]
    [InlineData(2.2, Comparison.NotEqualTo, 2, 0.1, true)]
    [InlineData(2, Comparison.AtLeast, 2, 0, true)]
    [InlineData(2, Comparison.GreaterThan, 2, 0, false)]
    public void Compare_holds_as_asked(double a, Comparison comparison, double b, double tolerance, bool expected) =>
        Assert.Equal(expected, CompareNode.Holds(a, comparison, b, tolerance));

    [Fact]
    public void A_missing_number_compares_as_false_every_way()
    {
        foreach (var comparison in Enum.GetValues<Comparison>())
            Assert.False(CompareNode.Holds(double.NaN, comparison, 1, 0));
    }

    // ---- Check Logic --------------------------------------------------------------

    [Theory]
    [InlineData(LogicRule.All, false)]
    [InlineData(LogicRule.Any, true)]
    [InlineData(LogicRule.None, false)]
    [InlineData(LogicRule.ExactlyOne, true)]
    public void Check_logic_combines_what_is_wired_in(LogicRule rule, bool expected)
    {
        var graph = new VisionGraph();
        var passes = new CompareNode { A = 1, Comparison = Comparison.LessThan, B = 2 };
        var fails = new CompareNode { A = 3, Comparison = Comparison.LessThan, B = 2 };
        var logic = new CheckLogicNode { Rule = rule };

        graph.Add(passes);
        graph.Add(fails);
        graph.Add(logic);

        graph.TryConnect(passes.ResultOut, logic.Sockets[0]);
        Assert.True(logic.Fit());
        graph.TryConnect(fails.ResultOut, logic.Sockets[1]);
        logic.Fit();

        Assert.Equal(3, logic.Sockets.Count);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(expected, scheduler.GetOutput(logic.ResultOut));
        Assert.Equal(1, scheduler.GetOutput(logic.TrueCountOut));
    }

    // ---- grades --------------------------------------------------------------------

    private static readonly string[] Letters = ["A", "B", "C", "Reject"];
    private static readonly double[] Rising = [0.05, 0.1, 0.2];

    [Theory]
    [InlineData(0.01, "A")]
    [InlineData(0.05, "A")]
    [InlineData(0.07, "B")]
    [InlineData(0.2, "C")]
    [InlineData(0.21, "Reject")]
    [InlineData(double.NaN, "Reject")]
    public void A_value_falls_in_the_band_its_limits_give(double value, string expected)
    {
        var grade = GradeNode.Classify(
            value, Letters, Rising, BetterIs.Lower, new HashSet<string>(["Reject"]));

        Assert.Equal(expected, grade.Name);
        Assert.Equal(expected != "Reject", grade.Passes);
    }

    [Fact]
    public void With_higher_better_the_limits_fall()
    {
        var failing = new HashSet<string>(["C", "Reject"]);
        double[] falling = [0.9, 0.8, 0.7];

        Assert.Equal("A", GradeNode.Classify(0.95, Letters, falling, BetterIs.Higher, failing).Name);
        Assert.Equal("B", GradeNode.Classify(0.85, Letters, falling, BetterIs.Higher, failing).Name);

        var c = GradeNode.Classify(0.75, Letters, falling, BetterIs.Higher, failing);
        Assert.Equal("C", c.Name);
        Assert.False(c.Passes);
    }

    [Fact]
    public void Any_fail_is_worse_than_every_pass_and_rank_breaks_the_rest()
    {
        var a = new Grade("A", 0, true);
        var c = new Grade("C", 2, true);
        var reject = new Grade("Reject", 3, false);

        Assert.Equal(c, Grade.Worst([a, c]));
        Assert.Equal(Grade.Fail, Grade.Worst([c, Grade.Fail]));
        Assert.Equal(reject, Grade.Worst([reject, Grade.Fail]));
        Assert.Null(Grade.Worst([]));
    }

    [Fact]
    public void The_grade_node_grades_and_colours_the_measurement()
    {
        var graph = new VisionGraph();
        var shapes = new ShapesNode(new PointShape(new Point2(10, 10)));
        var grade = new GradeNode { Value = 0.07 };

        graph.Add(shapes);
        graph.Add(grade);
        graph.TryConnect(shapes.Out, grade.AnnotationIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal("B", ((Grade)scheduler.GetOutput(grade.GradeOut)!).Name);
        Assert.Equal(true, scheduler.GetOutput(grade.PassOut));

        var drawn = scheduler.GetOutput<ShapeSet>(grade.AnnotationOut)!.Shapes.Single();
        Assert.Equal(0xFFF59E0Bu, drawn.Colour);
        Assert.Equal("0.07  B", drawn.Label);
    }

    [Fact]
    public void Grades_and_limits_that_do_not_pair_up_are_an_error()
    {
        var graph = new VisionGraph();
        var grade = new GradeNode { Limits = "0.05, 0.1" };
        graph.Add(grade);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Error, grade.Status);
    }

    [Fact]
    public void Worst_grade_takes_grades_and_yes_no_together()
    {
        var graph = new VisionGraph();
        var graded = new GradeNode { Value = 0.15 };
        var compared = new CompareNode { A = 1, Comparison = Comparison.LessThan, B = 2 };
        var worst = new WorstGradeNode();

        graph.Add(graded);
        graph.Add(compared);
        graph.Add(worst);
        graph.TryConnect(graded.GradeOut, worst.Sockets[0]);
        worst.Fit();
        graph.TryConnect(compared.ResultOut, worst.Sockets[1]);
        worst.Fit();

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal("C", ((Grade)scheduler.GetOutput(worst.GradeOut)!).Name);
        Assert.Equal(true, scheduler.GetOutput(worst.PassOut));

        compared.A = 5;
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(Grade.Fail, scheduler.GetOutput(worst.GradeOut));
    }

    // ---- the status line ------------------------------------------------------------

    /// <summary>
    /// Two checks wired into Check Logic are one decision: the recipe's
    /// verdict is the logic's, and the two are not counted again.
    /// </summary>
    [Fact]
    public void Checks_that_feed_another_check_are_left_to_it()
    {
        var graph = new VisionGraph();
        var first = new LimitCheckNode();
        var second = new CompareNode();
        var logic = new CheckLogicNode();
        var alone = new LimitCheckNode();

        graph.Add(first);
        graph.Add(second);
        graph.Add(logic);
        graph.Add(alone);
        graph.TryConnect(first.PassOut, logic.Sockets[0]);
        logic.Fit();
        graph.TryConnect(second.ResultOut, logic.Sockets[1]);

        var final = MainViewModel.Final([first, second, logic, alone]).ToArray();

        Assert.Equal([logic, (IJudgingNode)alone], final);
    }

    [Fact]
    public void The_status_line_names_the_worst_grade()
    {
        var b = new GradeNode();
        var c = new GradeNode();
        var answers = new Dictionary<OutputPort, object?>
        {
            [b.PassOut] = true,
            [b.GradeOut] = new Grade("B", 1, true),
            [c.PassOut] = true,
            [c.GradeOut] = new Grade("C", 2, true),
        };

        Assert.Equal("PASS (2 checks)  grade C", MainViewModel.Verdict([b, c], port => answers[port]));
    }

    // ---- inspections -----------------------------------------------------------------

    /// <summary>
    /// Re-runs after an edit are not inspections: a Counter counts only
    /// what InspectAsync runs - and each of those, even of the same image.
    /// </summary>
    [Fact]
    public void A_counter_counts_inspections_and_not_re_runs()
    {
        var graph = new VisionGraph();
        var compare = new CompareNode { A = 1, Comparison = Comparison.LessThan, B = 2 };
        var counter = new CounterNode();

        graph.Add(compare);
        graph.Add(counter);
        graph.TryConnect(compare.ResultOut, counter.ResultIn);

        using var scheduler = CreateScheduler(graph);

        scheduler.RunAsync().GetAwaiter().GetResult();
        compare.A = 1.5;
        scheduler.RunAsync().GetAwaiter().GetResult();
        Assert.Equal(0, scheduler.GetOutput(counter.TotalOut));

        scheduler.InspectAsync().GetAwaiter().GetResult();
        scheduler.InspectAsync().GetAwaiter().GetResult();
        compare.A = 3;
        scheduler.InspectAsync().GetAwaiter().GetResult();

        Assert.Equal(3, scheduler.GetOutput(counter.TotalOut));
        Assert.Equal(2, scheduler.GetOutput(counter.PassedOut));
        Assert.Equal(1, scheduler.GetOutput(counter.FailedOut));
        Assert.Equal(200d / 3, (double)scheduler.GetOutput(counter.YieldOut)!, 6);
        Assert.Equal("Pass 2, Fail 1", scheduler.GetOutput(counter.CountsOut));

        counter.Reset();
        counter.Invalidate();
        scheduler.RunAsync().GetAwaiter().GetResult();
        Assert.Equal(0, scheduler.GetOutput(counter.TotalOut));
    }

    [Fact]
    public void A_counter_tallies_each_grade_best_first_and_failing_last()
    {
        var graph = new VisionGraph();
        var grade = new GradeNode();
        var counter = new CounterNode();

        graph.Add(grade);
        graph.Add(counter);
        graph.TryConnect(grade.GradeOut, counter.ResultIn);

        using var scheduler = CreateScheduler(graph);

        foreach (var value in new[] { 0.3, 0.07, 0.01, 0.08, 0.15 })
        {
            grade.Value = value;
            scheduler.InspectAsync().GetAwaiter().GetResult();
        }

        Assert.Equal("A 1, B 2, C 1, Reject 1", scheduler.GetOutput(counter.CountsOut));
        Assert.Equal(80d, (double)scheduler.GetOutput(counter.YieldOut)!, 6);
    }

    [Fact]
    public void A_counter_inside_a_sub_graph_counts_inspections_too()
    {
        var graph = new VisionGraph();
        var sub = new SubGraphNode();
        var counter = new CounterNode();
        sub.Inner.Add(counter);
        graph.Add(sub);

        using var scheduler = CreateScheduler(graph);

        scheduler.InspectAsync().GetAwaiter().GetResult();
        scheduler.InspectAsync().GetAwaiter().GetResult();

        Assert.Equal(2, scheduler.GetOutput(counter.TotalOut));
    }

    // ---- Result Log -------------------------------------------------------------------

    [Fact]
    public void A_result_log_writes_a_row_per_inspection_under_one_heading()
    {
        var folder = Scratch();
        var graph = new VisionGraph();
        var grade = new GradeNode { Name = "Roundness" };
        var log = new ResultLogNode { LogFile = Path.Combine(folder, "results.csv"), NewFileEachDay = false };

        graph.Add(grade);
        graph.Add(log);
        graph.TryConnect(grade.ValueOut, log.Sockets[0]);
        log.Fit();
        graph.TryConnect(grade.GradeOut, log.Sockets[1]);
        log.Fit();

        using var scheduler = CreateScheduler(graph);

        scheduler.RunAsync().GetAwaiter().GetResult();
        Assert.False(File.Exists(log.LogFile));

        grade.Value = 0.03;
        scheduler.InspectAsync().GetAwaiter().GetResult();
        grade.Value = 0.5;
        scheduler.InspectAsync().GetAwaiter().GetResult();

        var lines = File.ReadAllLines(log.LogFile);
        Assert.Equal(3, lines.Length);
        Assert.Equal("Time,Image,Roundness Value,Roundness Grade", lines[0]);
        Assert.EndsWith(",0.03,A", lines[1]);
        Assert.EndsWith(",0.5,Reject", lines[2]);
        Assert.Equal(2, scheduler.GetOutput(log.RowsOut));
    }

    /// <summary>
    /// Excel holds a CSV it has open. The rows wait rather than being
    /// lost, and go in when the file is free.
    /// </summary>
    [Fact]
    public void Rows_are_held_while_the_file_is_locked()
    {
        var folder = Scratch();
        var graph = new VisionGraph();
        var compare = new CompareNode();
        var log = new ResultLogNode { LogFile = Path.Combine(folder, "results.csv"), NewFileEachDay = false };

        graph.Add(compare);
        graph.Add(log);
        graph.TryConnect(compare.ResultOut, log.Sockets[0]);

        using var scheduler = CreateScheduler(graph);
        scheduler.InspectAsync().GetAwaiter().GetResult();

        using (new FileStream(log.LogFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            scheduler.InspectAsync().GetAwaiter().GetResult();
            Assert.Equal(NodeStatus.Warning, log.Status);
        }

        scheduler.InspectAsync().GetAwaiter().GetResult();

        Assert.Equal(4, File.ReadAllLines(log.LogFile).Length);
        Assert.Equal(3, scheduler.GetOutput(log.RowsOut));
    }

    [Fact]
    public void Different_columns_start_a_new_file_rather_than_mixing()
    {
        var folder = Scratch();
        var file = Path.Combine(folder, "results.csv");
        File.WriteAllText(file, "Time,Image,Something Else\r\n");

        var graph = new VisionGraph();
        var compare = new CompareNode();
        var log = new ResultLogNode { LogFile = file, NewFileEachDay = false };
        graph.Add(compare);
        graph.Add(log);
        graph.TryConnect(compare.ResultOut, log.Sockets[0]);

        using var scheduler = CreateScheduler(graph);
        scheduler.InspectAsync().GetAwaiter().GetResult();

        Assert.Single(File.ReadAllLines(file));

        var written = (string)scheduler.GetOutput(log.FileOut)!;
        Assert.NotEqual(file, written);
        Assert.Equal("Time,Image,Compare Result", File.ReadAllLines(written)[0]);
    }

    [Fact]
    public void A_day_is_written_into_the_name()
    {
        Assert.Equal(
            Path.Combine("logs", "results-2026-10-29.csv"),
            ResultLogNode.Dated(Path.Combine("logs", "results.csv"), new DateTime(2026, 10, 29)));
    }

    [Fact]
    public void Fields_with_commas_or_quotes_are_quoted()
    {
        Assert.Equal("a,\"b,c\",\"say \"\"hi\"\"\"", ResultLogNode.Line(["a", "b,c", "say \"hi\""]));
        Assert.Equal("TRUE", ResultLogNode.Format(true));
        Assert.Equal("B", ResultLogNode.Format(new Grade("B", 1, true)));
        Assert.Equal("0.125", ResultLogNode.Format(0.125));
    }

    // ---- Filter Shapes ----------------------------------------------------------------

    [Fact]
    public void Filter_shapes_keeps_what_is_in_range_and_gives_the_rest()
    {
        var graph = new VisionGraph();
        var shapes = new ShapesNode(
            Measured(10), Measured(50), Measured(90), new PointShape(new Point2(0, 0)));
        var filter = new FilterShapesNode { Measure = ShapeMeasure.Area, Range = new ValueRange(20, 90) };

        graph.Add(shapes);
        graph.Add(filter);
        graph.TryConnect(shapes.Out, filter.ShapesIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var kept = scheduler.GetOutput<ShapeSet>(filter.KeptOut)!;
        Assert.Equal([50d, 90d], kept.Shapes.Select(shape => shape.Metric(ShapeMetric.Area)!.Value));
        Assert.Equal(2, scheduler.GetOutput<ShapeSet>(filter.RejectedOut)!.Count);
        Assert.Equal(2, scheduler.GetOutput(filter.CountOut));
    }

    // ---- plumbing ---------------------------------------------------------------------

    private static Shape Measured(double area) =>
        new PointShape(new Point2(area, area)) { Metrics = new Dictionary<string, double> { [ShapeMetric.Area] = area } };

    private static string Scratch()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sirius-judging-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private sealed class ShapesNode : VisionNode
    {
        private readonly ShapeSet _set;

        public ShapesNode(params Shape[] shapes)
        {
            _set = new ShapeSet(shapes, 640, 480);
            Out = AddOutput<ShapeSet>("Out");
        }

        public OutputPort Out { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(Out, _set);
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
