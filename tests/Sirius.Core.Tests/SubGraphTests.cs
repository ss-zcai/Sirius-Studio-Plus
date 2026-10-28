using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Serialization;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// A graph used as one node: its sockets, their types, and running it.
/// </summary>
/// <remarks>
/// The boundary nodes inside are the sockets outside, so most of what can go
/// wrong is the two drifting apart - a boundary deleted while its socket is
/// still wired, an undo that brings the boundary back without its wire, a
/// type inferred on one side and not the other.
/// </remarks>
public class SubGraphTests
{
    public SubGraphTests() => TestCatalog.Ensure();

    // ---- sockets ----------------------------------------------------------------

    [Fact]
    public void A_new_sub_graph_has_one_way_in_and_one_way_out_of_any_type()
    {
        var sub = new SubGraphNode();

        var input = Assert.Single(sub.Inputs);
        var output = Assert.Single(sub.Outputs);

        Assert.Equal("In", input.DisplayName);
        Assert.Equal("Out", output.DisplayName);
        Assert.Equal(typeof(object), input.DataType);
        Assert.Equal(typeof(object), output.DataType);
    }

    [Fact]
    public void Adding_a_boundary_inside_adds_a_socket_outside_and_says_so()
    {
        var sub = new SubGraphNode();
        var announced = 0;
        sub.PortsChanged += (_, _) => announced++;

        sub.Inner.Add(new GraphInputNode { Name = "Gain" });

        Assert.Equal(new[] { "In", "Gain" }, sub.Inputs.Select(port => port.DisplayName));
        Assert.True(announced > 0);
    }

    [Fact]
    public void Renaming_a_boundary_renames_its_socket()
    {
        var sub = new SubGraphNode();
        var boundary = sub.Inner.Nodes.OfType<GraphInputNode>().Single();

        boundary.Name = "Image";

        Assert.Equal("Image", Assert.Single(sub.Inputs).DisplayName);
    }

    /// <summary>
    /// A socket cannot stay on the node with nothing behind it, so deleting
    /// the boundary takes the wire off outside too - and undoing the
    /// deletion has to put back the same socket, wired as it was.
    /// </summary>
    [Fact]
    public void Deleting_a_boundary_takes_its_wire_and_undoing_it_puts_both_back()
    {
        var (graph, source, sub) = Outer();
        var socket = sub.Inputs.Single();
        Assert.True(graph.TryConnect(source.Out, socket).Success);

        var boundary = sub.Inner.Nodes.OfType<GraphInputNode>().Single();
        sub.Inner.Remove(boundary);

        Assert.Empty(sub.Inputs);
        Assert.Empty(graph.Connections);

        sub.Inner.Add(boundary);

        Assert.Same(socket, Assert.Single(sub.Inputs));
        Assert.Same(source.Out, Assert.Single(graph.Connections).Source);
    }

    /// <summary>
    /// An undo history outside can still hold a socket that has gone. Wiring
    /// it would connect to something the node no longer shows.
    /// </summary>
    [Fact]
    public void A_socket_that_has_gone_cannot_be_wired()
    {
        var (graph, source, sub) = Outer();
        var socket = sub.Inputs.Single();

        sub.Inner.Remove(sub.Inner.Nodes.OfType<GraphInputNode>().Single());

        var result = graph.TryConnect(source.Out, socket);

        Assert.False(result.Success);
        Assert.Contains("no longer", result.Message);
    }

    // ---- types ------------------------------------------------------------------

    [Fact]
    public void Wiring_outside_types_both_sides()
    {
        var (graph, source, sub) = Outer();

        graph.TryConnect(source.Out, sub.Inputs.Single());
        sub.Fit();

        Assert.Equal(typeof(double), sub.Inputs.Single().DataType);
        Assert.Equal(typeof(double), sub.Inner.Nodes.OfType<GraphInputNode>().Single().ValueOut.DataType);
    }

    [Fact]
    public void Wiring_inside_types_the_socket_outside_at_once()
    {
        var sub = new SubGraphNode();
        var add = new AddOneNode();
        sub.Inner.Add(add);

        var boundary = sub.Inner.Nodes.OfType<GraphOutputNode>().Single();
        Assert.True(sub.Inner.TryConnect(add.Out, boundary.ValueIn).Success);

        Assert.Equal(typeof(double), sub.Outputs.Single().DataType);
    }

    /// <summary>
    /// The first wire decides. A second of another type is refused the way
    /// any mismatched wire is, rather than quietly retyping what the first
    /// one was wired into.
    /// </summary>
    [Fact]
    public void Once_typed_a_socket_refuses_a_wire_of_another_type()
    {
        var (graph, source, sub) = Outer();
        graph.TryConnect(source.Out, sub.Inputs.Single());
        sub.Fit();

        var text = new TextSourceNode();
        graph.Add(text);
        var boundary = sub.Inner.Nodes.OfType<GraphInputNode>().Single();
        var consumer = new TextConsumerNode();
        sub.Inner.Add(consumer);

        Assert.False(sub.Inner.TryConnect(boundary.ValueOut, consumer.In).Success);
    }

    [Fact]
    public void Unwiring_both_sides_makes_it_any_again()
    {
        var (graph, source, sub) = Outer();
        var connection = graph.TryConnect(source.Out, sub.Inputs.Single()).Connection!;
        sub.Fit();

        graph.Disconnect(connection);
        sub.Fit();

        Assert.Equal(typeof(object), sub.Inputs.Single().DataType);
    }

    // ---- running ----------------------------------------------------------------

    [Fact]
    public void A_value_goes_in_through_the_inside_and_out()
    {
        var (graph, source, sub) = Outer(value: 5);
        Inside(sub, new AddOneNode());

        var sink = Downstream(graph, sub);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(6.0, scheduler.GetInput(sink.In));
        Assert.Equal(NodeStatus.Ready, sub.Status);
    }

    /// <summary>
    /// A parameter inside is a parameter of the recipe: changing it has to
    /// make the sub-graph, and what depends on it, run again.
    /// </summary>
    [Fact]
    public void Changing_something_inside_re_runs_the_sub_graph()
    {
        var (graph, source, sub) = Outer(value: 5);
        var add = Inside(sub, new AddOneNode());
        var sink = Downstream(graph, sub);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        add.Amount = 10;
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(15.0, scheduler.GetInput(sink.In));
    }

    /// <summary>
    /// With its own cache inside, a sub-graph re-run because of a change
    /// inside costs only what the change affects - here, nothing upstream of
    /// the node that changed.
    /// </summary>
    [Fact]
    public void Only_what_a_change_inside_affects_runs_again()
    {
        var (graph, source, sub) = Outer(value: 5);
        var first = Inside(sub, new AddOneNode());
        var second = new AddOneNode();
        sub.Inner.Add(second);
        sub.Inner.TryConnect(first.Out, second.In);
        sub.Inner.TryConnect(second.Out, sub.Inner.Nodes.OfType<GraphOutputNode>().Single().ValueIn);
        Downstream(graph, sub);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();
        var runsBefore = first.Runs;

        second.Amount = 3;
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(runsBefore, first.Runs);
        Assert.Equal(2, second.Runs);
    }

    [Fact]
    public void A_sub_graph_can_hold_a_sub_graph()
    {
        var (graph, source, outer) = Outer(value: 1);
        var inner = new SubGraphNode();
        Inside(inner, new AddOneNode());
        Inside(outer, inner);
        var sink = Downstream(graph, outer);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(2.0, scheduler.GetInput(sink.In));
    }

    /// <summary>
    /// Viewers, previews and readouts read values through the recipe's
    /// scheduler. A node inside a sub-graph has to be answered too, or
    /// none of them works in there.
    /// </summary>
    [Fact]
    public void A_value_inside_can_be_read_from_outside()
    {
        var (graph, source, sub) = Outer(value: 5);
        var add = Inside(sub, new AddOneNode());
        Downstream(graph, sub);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(6.0, scheduler.GetOutput(add.Out));
    }

    [Fact]
    public void Nothing_inside_a_bypassed_sub_graph_is_read()
    {
        var (graph, source, sub) = Outer(value: 5);
        var add = Inside(sub, new AddOneNode());
        Downstream(graph, sub);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        sub.Enabled = false;

        Assert.Null(scheduler.GetOutput(add.Out));
    }

    [Fact]
    public void A_failure_inside_is_a_warning_on_the_sub_graph()
    {
        var (graph, source, sub) = Outer(value: 5);
        Inside(sub, new FailingNode());
        Downstream(graph, sub);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Warning, sub.Status);
        Assert.Contains("1 node inside failed", sub.StatusMessage);
    }

    [Fact]
    public void A_boundary_outside_any_sub_graph_says_so()
    {
        var graph = new VisionGraph();
        var stray = new GraphInputNode();
        graph.Add(stray);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Warning, stray.Status);
        Assert.Contains("only does something inside a sub-graph", stray.StatusMessage);
    }

    [Fact]
    public void Walking_a_graph_reaches_inside_its_sub_graphs()
    {
        var (graph, _, sub) = Outer();
        var add = Inside(sub, new AddOneNode());

        Assert.Contains(add, GraphTree.AllNodes(graph));
        Assert.Same(sub, GraphTree.OwnerOf(graph, sub.Inner));

        sub.Enabled = false;
        Assert.DoesNotContain(add, GraphTree.AllNodes(graph, enabledOnly: true));
    }

    // ---- saving, opening, pasting ---------------------------------------------------

    [Fact]
    public void A_recipe_with_a_sub_graph_saves_and_reopens_wired_and_working()
    {
        var (graph, source, sub) = Outer(value: 5);
        var add = Inside(sub, new AddOneNode());
        add.Amount = 2;
        sub.Inner.Nodes.OfType<GraphInputNode>().Single().Name = "Value";
        Downstream(graph, sub);

        var loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));
        Assert.False(loaded.HasWarnings, string.Join(Environment.NewLine, loaded.Warnings));

        var reopened = loaded.Graph.Nodes.OfType<SubGraphNode>().Single();
        Assert.Equal("Value", reopened.Inputs.Single().DisplayName);
        Assert.Equal(2, reopened.Inner.Nodes.OfType<AddOneNode>().Single().Amount);
        Assert.Equal(2, loaded.Graph.Connections.Count);

        using var scheduler = CreateScheduler(loaded.Graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var sink = loaded.Graph.Nodes.OfType<NumberSinkNode>().Single();
        Assert.Equal(7.0, scheduler.GetInput(sink.In));
    }

    /// <summary>
    /// A pasted copy shares nothing with the original - its nodes inside get
    /// new ids too, or two sub-graphs' viewers would claim the same panel -
    /// and keeps its wires through the renaming that follows.
    /// </summary>
    [Fact]
    public void A_pasted_sub_graph_has_ids_of_its_own_and_keeps_its_wires()
    {
        var (graph, source, sub) = Outer(value: 5);
        Inside(sub, new AddOneNode());
        Downstream(graph, sub);

        var copied = GraphClipboard.Copy(graph, graph.Nodes);
        var pasted = GraphClipboard.Paste(graph, copied);

        var original = sub.Inner.Nodes.Select(node => node.Id).ToHashSet();
        var copy = pasted.Nodes.OfType<SubGraphNode>().Single();

        Assert.DoesNotContain(copy.Inner.Nodes, node => original.Contains(node.Id));
        Assert.Equal(4, graph.Connections.Count);

        var boundary = copy.Inner.Nodes.OfType<GraphInputNode>().Single();
        Assert.Equal(boundary.Id, copy.Inputs.Single().Name);
    }

    // ---- helpers --------------------------------------------------------------------

    private static (VisionGraph Graph, NumberSourceNode Source, SubGraphNode Sub) Outer(double value = 1)
    {
        var graph = new VisionGraph();
        var source = new NumberSourceNode { Value = value };
        var sub = new SubGraphNode();

        graph.Add(source);
        graph.Add(sub);

        return (graph, source, sub);
    }

    /// <summary>Wires a source into the sub-graph and puts <paramref name="node"/> between its boundaries.</summary>
    private static T Inside<T>(SubGraphNode sub, T node) where T : VisionNode
    {
        var graph = sub.Graph;
        var input = sub.Inner.Nodes.OfType<GraphInputNode>().Single();
        var output = sub.Inner.Nodes.OfType<GraphOutputNode>().Single();

        sub.Inner.Add(node);
        Assert.True(sub.Inner.TryConnect(input.ValueOut, node.Inputs[0]).Success);
        Assert.True(sub.Inner.TryConnect(node.Outputs[0], output.ValueIn).Success);

        if (graph?.Nodes.OfType<NumberSourceNode>().FirstOrDefault() is { } source && !sub.Inputs[0].IsConnected)
            Assert.True(graph!.TryConnect(source.Out, sub.Inputs[0]).Success);

        sub.Fit();
        return node;
    }

    private static NumberSinkNode Downstream(VisionGraph graph, SubGraphNode sub)
    {
        var sink = new NumberSinkNode();
        graph.Add(sink);
        Assert.True(graph.TryConnect(sub.Outputs[0], sink.In).Success);
        sub.Fit();
        return sink;
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

    // ---- nodes for the tests ----------------------------------------------------------

    [VisionNode("Number Source (test)", TypeKey = "tests.subgraph.source")]
    public sealed class NumberSourceNode : VisionNode
    {
        private double _value;

        public NumberSourceNode() => Out = AddOutput<double>("Out");

        [Param]
        public double Value
        {
            get => _value;
            set => SetParam(ref _value, value);
        }

        public OutputPort Out { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(Out, Value);
    }

    [VisionNode("Number Sink (test)", TypeKey = "tests.subgraph.sink")]
    public sealed class NumberSinkNode : VisionNode
    {
        public NumberSinkNode() => In = AddInput<double>("In", isRequired: false);

        public InputPort In { get; }

        protected override void OnEvaluate(EvalContext context)
        {
        }
    }

    [VisionNode("Add One (test)", TypeKey = "tests.subgraph.addOne")]
    public sealed class AddOneNode : VisionNode
    {
        private double _amount = 1;

        public AddOneNode()
        {
            In = AddInput<double>("In");
            Out = AddOutput<double>("Out");
        }

        public InputPort In { get; }

        public OutputPort Out { get; }

        [Param]
        public double Amount
        {
            get => _amount;
            set => SetParam(ref _amount, value);
        }

        public int Runs { get; private set; }

        protected override void OnEvaluate(EvalContext context)
        {
            Runs++;
            context.Set(Out, context.GetValue<double>(In)!.Value + Amount);
        }
    }

    private sealed class FailingNode : VisionNode
    {
        public FailingNode()
        {
            AddInput<double>("In");
            AddOutput<double>("Out");
        }

        protected override void OnEvaluate(EvalContext context) =>
            throw new InvalidOperationException("broken on purpose");
    }

    private sealed class TextSourceNode : VisionNode
    {
        public TextSourceNode() => AddOutput<string>("Out");

        protected override void OnEvaluate(EvalContext context)
        {
        }
    }

    private sealed class TextConsumerNode : VisionNode
    {
        public TextConsumerNode() => In = AddInput<string>("In");

        public InputPort In { get; }

        protected override void OnEvaluate(EvalContext context)
        {
        }
    }
}
