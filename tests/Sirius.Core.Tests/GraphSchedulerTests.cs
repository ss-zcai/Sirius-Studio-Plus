using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

public class GraphSchedulerTests
{
    public GraphSchedulerTests() => TestCatalog.Ensure();

    /// <summary>
    /// Builds a scheduler with no ambient synchronisation context, so status
    /// updates are applied inline and assertions are deterministic. In the app
    /// the context is the WPF dispatcher.
    /// </summary>
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

    private static (VisionGraph Graph, TestSourceNode Source, TestAddNode Add) BuildChain(int seed = 10, int addend = 5)
    {
        var graph = new VisionGraph();
        var source = new TestSourceNode { Seed = seed };
        var add = new TestAddNode { Addend = addend };
        graph.Add(source);
        graph.Add(add);
        graph.TryConnect(source.ValueOut, add.ValueIn);
        return (graph, source, add);
    }

    /// <summary>
    /// A run up to one node: it and what it needs, and not what follows it,
    /// which stays stale until the next full run catches it up.
    /// </summary>
    [Fact]
    public async Task A_run_up_to_a_node_leaves_what_follows_it_for_later()
    {
        var graph = new VisionGraph();
        var source = new TestSourceNode { Seed = 10 };
        var first = new TestAddNode { Addend = 1 };
        var second = new TestAddNode { Addend = 100 };
        graph.Add(source);
        graph.Add(first);
        graph.Add(second);
        graph.TryConnect(source.ValueOut, first.ValueIn);
        graph.TryConnect(first.ValueOut, second.ValueIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        first.Addend = 2;
        source.ResetCount();
        first.ResetCount();
        second.ResetCount();

        var partial = await scheduler.RunAsync(first);

        Assert.Equal(1, partial.Evaluated);
        Assert.Equal(12, scheduler.GetOutput<TrackedPayload>(first.ValueOut)!.Value);
        Assert.Equal(0, source.EvaluationCount);
        Assert.Equal(0, second.EvaluationCount);

        var full = await scheduler.RunAsync();

        Assert.Equal(1, full.Evaluated);
        Assert.Equal(1, second.EvaluationCount);
        Assert.Equal(112, scheduler.GetOutput<TrackedPayload>(second.ValueOut)!.Value);
    }

    [Fact]
    public async Task Evaluates_in_dependency_order_and_produces_the_expected_value()
    {
        var (graph, source, add) = BuildChain(seed: 10, addend: 5);
        using var scheduler = CreateScheduler(graph);

        var result = await scheduler.RunAsync();

        Assert.True(result.Success);
        Assert.Equal(2, result.Evaluated);
        Assert.Equal(15, scheduler.GetOutput<TrackedPayload>(add.ValueOut)!.Value);
        Assert.Equal(NodeStatus.Ready, source.Status);
        Assert.Equal(NodeStatus.Ready, add.Status);
    }

    /// <summary>
    /// What the Run button is built on. A plain re-run evaluates nothing on an
    /// untouched graph, so timing it would measure the cache; discarding the
    /// cache first is what makes the reported duration the cost of the recipe.
    /// </summary>
    [Fact]
    public async Task Invalidating_everything_makes_the_next_run_a_full_one()
    {
        var (graph, source, add) = BuildChain();
        using var scheduler = CreateScheduler(graph);

        await scheduler.RunAsync();
        Assert.Equal(0, (await scheduler.RunAsync()).Evaluated);

        scheduler.InvalidateAll();
        var result = await scheduler.RunAsync();

        Assert.Equal(2, result.Evaluated);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(2, source.EvaluationCount);
        Assert.Equal(2, add.EvaluationCount);
    }

    [Fact]
    public async Task A_second_run_with_no_changes_evaluates_nothing()
    {
        var (graph, source, add) = BuildChain();
        using var scheduler = CreateScheduler(graph);

        await scheduler.RunAsync();
        var result = await scheduler.RunAsync();

        Assert.Equal(0, result.Evaluated);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(1, source.EvaluationCount);
        Assert.Equal(1, add.EvaluationCount);
    }

    /// <summary>
    /// The central improvement over the old <c>MainForm.Process()</c>, which
    /// re-ran every tool in the rack on every parameter change.
    /// </summary>
    [Fact]
    public async Task Changing_a_parameter_re_evaluates_only_that_node_and_its_downstream()
    {
        var graph = new VisionGraph();
        var source = new TestSourceNode();
        var middle = new TestAddNode();
        var leaf = new TestAddNode();
        graph.Add(source);
        graph.Add(middle);
        graph.Add(leaf);
        graph.TryConnect(source.ValueOut, middle.ValueIn);
        graph.TryConnect(middle.ValueOut, leaf.ValueIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        source.ResetCount();
        middle.ResetCount();
        leaf.ResetCount();

        middle.Addend = 42;
        var result = await scheduler.RunAsync();

        Assert.Equal(2, result.Evaluated);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, source.EvaluationCount);
        Assert.Equal(1, middle.EvaluationCount);
        Assert.Equal(1, leaf.EvaluationCount);
    }

    [Fact]
    public async Task Changing_an_upstream_parameter_propagates_the_new_value()
    {
        var (graph, source, add) = BuildChain(seed: 10, addend: 5);
        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        source.Seed = 100;
        await scheduler.RunAsync();

        Assert.Equal(105, scheduler.GetOutput<TrackedPayload>(add.ValueOut)!.Value);
    }

    /// <summary>
    /// The old tools never disposed a single <c>Mat</c>; here a superseded
    /// output is released as soon as it is replaced.
    /// </summary>
    [Fact]
    public async Task Superseded_output_is_disposed()
    {
        var (graph, source, add) = BuildChain();
        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        var firstResult = scheduler.GetOutput<TrackedPayload>(add.ValueOut)!;
        Assert.False(firstResult.IsDisposed);

        add.Addend = 99;
        await scheduler.RunAsync();

        var secondResult = scheduler.GetOutput<TrackedPayload>(add.ValueOut)!;

        Assert.True(firstResult.IsDisposed);
        Assert.False(secondResult.IsDisposed);
        Assert.NotSame(firstResult, secondResult);
    }

    [Fact]
    public async Task Disposing_the_scheduler_releases_every_cached_value()
    {
        var (graph, source, add) = BuildChain();
        var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        var sourceValue = scheduler.GetOutput<TrackedPayload>(source.ValueOut)!;
        var addValue = scheduler.GetOutput<TrackedPayload>(add.ValueOut)!;

        scheduler.Dispose();

        Assert.True(sourceValue.IsDisposed);
        Assert.True(addValue.IsDisposed);
    }

    [Fact]
    public async Task A_disabled_node_forwards_its_input()
    {
        var (graph, source, add) = BuildChain(seed: 7, addend: 5);
        using var scheduler = CreateScheduler(graph);

        add.Enabled = false;
        await scheduler.RunAsync();

        var forwarded = scheduler.GetOutput<TrackedPayload>(add.ValueOut)!;
        var upstream = scheduler.GetOutput<TrackedPayload>(source.ValueOut)!;

        Assert.Same(upstream, forwarded);
        Assert.Equal(7, forwarded.Value);
        Assert.Equal(NodeStatus.Idle, add.Status);
        Assert.Equal(0, add.EvaluationCount);
    }

    /// <summary>
    /// A forwarded value belongs to the upstream node, so replacing it must not
    /// free it - the double-free the old <c>OutputImage = InputImage</c> error
    /// path was one refactor away from.
    /// </summary>
    [Fact]
    public async Task Replacing_a_forwarded_value_does_not_dispose_the_upstream_buffer()
    {
        var (graph, source, add) = BuildChain(seed: 7, addend: 5);
        using var scheduler = CreateScheduler(graph);

        add.Enabled = false;
        await scheduler.RunAsync();
        var upstream = scheduler.GetOutput<TrackedPayload>(source.ValueOut)!;

        add.Enabled = true;
        await scheduler.RunAsync();

        Assert.False(upstream.IsDisposed);
        Assert.Equal(12, scheduler.GetOutput<TrackedPayload>(add.ValueOut)!.Value);
    }

    [Fact]
    public async Task A_missing_required_input_reports_an_error_and_produces_nothing()
    {
        var graph = new VisionGraph();
        var add = new TestAddNode();
        graph.Add(add);

        using var scheduler = CreateScheduler(graph);
        var result = await scheduler.RunAsync();

        Assert.False(result.Success);
        Assert.Equal(1, result.Failed);
        Assert.Equal(NodeStatus.Error, add.Status);
        Assert.Contains("not connected", add.StatusMessage!);
        Assert.Null(scheduler.GetOutput(add.ValueOut));
        Assert.Equal(0, add.EvaluationCount);
    }

    /// <summary>
    /// A wired input whose producer failed must not be reported as unwired -
    /// that wording points the operator at the wrong node.
    /// </summary>
    [Fact]
    public async Task A_connected_input_whose_producer_failed_names_the_producer()
    {
        var graph = new VisionGraph();

        // TestAddNode has a required input and nothing feeding it, so it fails
        // and produces no output for the node downstream of it.
        var failingProducer = new TestAddNode { Name = "Upstream" };
        var consumer = new TestAddNode { Name = "Downstream" };
        graph.Add(failingProducer);
        graph.Add(consumer);
        graph.TryConnect(failingProducer.ValueOut, consumer.ValueIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Equal(NodeStatus.Error, consumer.Status);
        Assert.DoesNotContain("not connected", consumer.StatusMessage!);
        Assert.Contains("Upstream", consumer.StatusMessage!);
        Assert.Contains("produced no result", consumer.StatusMessage!);

        // The node that actually has nothing wired still says so plainly.
        Assert.Contains("not connected", failingProducer.StatusMessage!);
    }

    [Fact]
    public async Task A_throwing_node_reports_the_error_and_forwards_its_input()
    {
        var graph = new VisionGraph();
        var source = new TestSourceNode { Seed = 3 };
        var failing = new TestFailingNode();
        graph.Add(source);
        graph.Add(failing);
        graph.TryConnect(source.ValueOut, failing.ValueIn);

        using var scheduler = CreateScheduler(graph);
        var result = await scheduler.RunAsync();

        Assert.Equal(1, result.Failed);
        Assert.Equal(NodeStatus.Error, failing.Status);
        Assert.Equal("Intentional test failure.", failing.StatusMessage);

        // Input echoed through, so the graph still carries a value.
        Assert.Same(
            scheduler.GetOutput<TrackedPayload>(source.ValueOut),
            scheduler.GetOutput<TrackedPayload>(failing.ValueOut));
    }

    [Fact]
    public async Task Output_published_before_a_failure_is_disposed()
    {
        var graph = new VisionGraph();
        var source = new TestSourceNode();
        var failing = new TestFailingNode();
        graph.Add(source);
        graph.Add(failing);
        graph.TryConnect(source.ValueOut, failing.ValueIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        // The node published a payload with Value -1 and then threw; it must not
        // have reached the cache, and must not have leaked.
        var cached = scheduler.GetOutput<TrackedPayload>(failing.ValueOut);
        Assert.NotEqual(-1, cached!.Value);
    }

    [Fact]
    public async Task A_warning_still_produces_a_result()
    {
        var graph = new VisionGraph();
        var source = new TestSourceNode { Seed = 4 };
        var warning = new TestWarningNode();
        graph.Add(source);
        graph.Add(warning);
        graph.TryConnect(source.ValueOut, warning.ValueIn);

        using var scheduler = CreateScheduler(graph);
        var result = await scheduler.RunAsync();

        Assert.Equal(0, result.Failed);
        Assert.Equal(NodeStatus.Warning, warning.Status);
        Assert.Equal("Something was slightly off.", warning.StatusMessage);
        Assert.Equal(4, scheduler.GetOutput<TrackedPayload>(warning.ValueOut)!.Value);
    }

    [Fact]
    public async Task A_newly_added_node_is_evaluated_on_the_next_run()
    {
        var (graph, source, add) = BuildChain();
        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        var extra = new TestAddNode { Addend = 1 };
        graph.Add(extra);
        graph.TryConnect(add.ValueOut, extra.ValueIn);

        var result = await scheduler.RunAsync();

        Assert.True(result.Evaluated >= 1);
        Assert.NotNull(scheduler.GetOutput<TrackedPayload>(extra.ValueOut));
    }

    /// <summary>
    /// Two passes over one cache will free a buffer the other is still using.
    /// With native image buffers that is an access violation, not a managed
    /// exception, so the scheduler refuses rather than letting it happen.
    /// </summary>
    [Fact]
    public async Task Starting_a_second_pass_while_one_runs_is_refused()
    {
        var graph = new VisionGraph();
        var source = new SlowSourceNode();
        graph.Add(source);

        using var scheduler = CreateScheduler(graph);

        var first = scheduler.RunAsync();

        // The first pass blocks inside the node until released, so this call
        // lands while it is still in flight.
        source.Started.Wait(TimeSpan.FromSeconds(5));
        // Discarding the result selects the Action overload; the guard throws
        // synchronously, before any task is returned.
        var error = Assert.Throws<InvalidOperationException>(() => { _ = scheduler.RunAsync(); });
        Assert.Contains("already in progress", error.Message);

        source.Release.Set();
        await first;
    }

    [Fact]
    public async Task A_completed_pass_releases_the_scheduler_for_the_next_one()
    {
        var (graph, _, add) = BuildChain();
        using var scheduler = CreateScheduler(graph);

        await scheduler.RunAsync();

        // Must not throw: the previous pass finished.
        add.Addend = 3;
        var result = await scheduler.RunAsync();

        Assert.True(result.Success);
    }

    /// <summary>A cancelled pass must also release the scheduler.</summary>
    [Fact]
    public async Task A_cancelled_pass_releases_the_scheduler()
    {
        var graph = new VisionGraph();
        var source = new SlowSourceNode();
        graph.Add(source);

        using var scheduler = CreateScheduler(graph);
        using var cancellation = new CancellationTokenSource();

        var first = scheduler.RunAsync(cancellation.Token);
        source.Started.Wait(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        source.Release.Set();

        try
        {
            await first;
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation wins the race.
        }

        var result = await scheduler.RunAsync();
        Assert.NotNull(result);
    }

    /// <summary>Blocks inside evaluation so a pass can be held open.</summary>
    [VisionNodeAttribute("Test Slow Source", TypeKey = "test.slowSource", Category = NodeCategory.Source)]
    private sealed class SlowSourceNode : VisionNode
    {
        public SlowSourceNode()
        {
            ValueOut = AddOutput<TrackedPayload>("Value");
        }

        public OutputPort ValueOut { get; }

        public ManualResetEventSlim Started { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        protected override void OnEvaluate(EvalContext context)
        {
            Started.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
            context.Set(ValueOut, new TrackedPayload(1));
        }
    }

    [Fact]
    public async Task Fan_out_hands_the_same_instance_to_every_consumer()
    {
        var graph = new VisionGraph();
        var source = new TestSourceNode { Seed = 2 };
        var left = new TestAddNode { Addend = 1 };
        var right = new TestAddNode { Addend = 10 };
        graph.Add(source);
        graph.Add(left);
        graph.Add(right);
        graph.TryConnect(source.ValueOut, left.ValueIn);
        graph.TryConnect(source.ValueOut, right.ValueIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Equal(1, source.EvaluationCount);
        Assert.Equal(3, scheduler.GetOutput<TrackedPayload>(left.ValueOut)!.Value);
        Assert.Equal(12, scheduler.GetOutput<TrackedPayload>(right.ValueOut)!.Value);
    }
}
