using Sirius.Core.Graph;
using Sirius.Core.Graph.Undo;
using Sirius.Core.Nodes;
using Sirius.Core.Serialization;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Undo and redo over the graph itself, with no shell involved.
/// </summary>
/// <remarks>
/// The stack works on <see cref="VisionGraph"/> and nothing else, which is
/// what lets these run without a UI thread - and what keeps the mechanism
/// honest, since an edit that needed the canvas to undo properly would not
/// compile here.
/// </remarks>
public class UndoStackTests
{
    public UndoStackTests() => TestCatalog.Ensure();

    private sealed record Fixture(
        VisionGraph Graph,
        UndoStack Stack,
        UndoRecorder Recorder);

    /// <summary>A source feeding a threshold, with recording already on.</summary>
    private static Fixture Build()
    {
        var graph = new VisionGraph();
        var stack = new UndoStack(graph);
        var recorder = new UndoRecorder(graph, stack);

        return new Fixture(graph, stack, recorder);
    }

    private static (ConstantImageNode Source, ThresholdNode Tool) AddPair(Fixture fixture)
    {
        var source = new ConstantImageNode { Width = 32, Height = 24, Value = 200 };
        var tool = new ThresholdNode { Threshold = 128 };

        fixture.Graph.Add(source);
        fixture.Stack.Record(new AddNodeEdit(source));

        fixture.Graph.Add(tool);
        fixture.Stack.Record(new AddNodeEdit(tool));

        return (source, tool);
    }

    // ---- nothing to undo ---------------------------------------------------

    [Fact]
    public void A_new_stack_has_nothing_to_undo()
    {
        var fixture = Build();

        Assert.False(fixture.Stack.CanUndo);
        Assert.False(fixture.Stack.CanRedo);
        Assert.Null(fixture.Stack.UndoLabel);
        Assert.False(fixture.Stack.Undo());
    }

    // ---- structure ---------------------------------------------------------

    [Fact]
    public void Adding_a_node_undoes_and_redoes()
    {
        var fixture = Build();
        var node = new ThresholdNode();

        fixture.Graph.Add(node);
        fixture.Stack.Record(new AddNodeEdit(node));

        Assert.True(fixture.Stack.Undo());
        Assert.Empty(fixture.Graph.Nodes);

        Assert.True(fixture.Stack.Redo());
        Assert.Same(node, Assert.Single(fixture.Graph.Nodes));
    }

    /// <summary>
    /// The same instance comes back, not an equal one. The shell holds node
    /// references in panels, previews and the scheduler's cache; a restored
    /// copy would leave every one of them pointing at a node that is no
    /// longer in the graph, and nothing would throw - the panels would just
    /// quietly stop updating.
    /// </summary>
    [Fact]
    public void Undoing_a_delete_restores_the_same_node()
    {
        var fixture = Build();
        var (_, tool) = AddPair(fixture);

        var edit = new RemoveNodesEdit(fixture.Graph, [tool]);
        fixture.Graph.Remove(tool);
        fixture.Stack.Record(edit);

        fixture.Stack.Undo();

        Assert.Same(tool, fixture.Graph.Nodes.Single(n => n is ThresholdNode));
    }

    /// <summary>
    /// The case that makes undo feel broken when it is missed: the node comes
    /// back unwired, because removing it dropped its connections and reported
    /// none of them.
    /// </summary>
    [Fact]
    public void Undoing_a_delete_restores_the_wires()
    {
        var fixture = Build();
        var (source, tool) = AddPair(fixture);
        fixture.Graph.TryConnect(source.ImageOut, tool.ImageIn);

        var edit = new RemoveNodesEdit(fixture.Graph, [tool]);
        fixture.Graph.Remove(tool);
        fixture.Stack.Record(edit);

        Assert.Empty(fixture.Graph.Connections);

        fixture.Stack.Undo();

        var connection = Assert.Single(fixture.Graph.Connections);
        Assert.Same(source.ImageOut, connection.Source);
        Assert.Same(tool.ImageIn, connection.Target);
    }

    /// <summary>
    /// Deleting two connected nodes and undoing restores the wire between
    /// them - which only works if the nodes are put back before the wires.
    /// </summary>
    [Fact]
    public void Undoing_a_multiple_delete_restores_the_wire_between_them()
    {
        var fixture = Build();
        var (source, tool) = AddPair(fixture);
        fixture.Graph.TryConnect(source.ImageOut, tool.ImageIn);

        var edit = new RemoveNodesEdit(fixture.Graph, [source, tool]);
        fixture.Graph.Remove(source);
        fixture.Graph.Remove(tool);
        fixture.Stack.Record(edit);

        fixture.Stack.Undo();

        Assert.Equal(2, fixture.Graph.Nodes.Count);
        Assert.Single(fixture.Graph.Connections);
    }

    [Fact]
    public void Connecting_undoes()
    {
        var fixture = Build();
        var (source, tool) = AddPair(fixture);

        fixture.Graph.TryConnect(source.ImageOut, tool.ImageIn);
        fixture.Stack.Record(new ConnectEdit(source.ImageOut, tool.ImageIn, replaced: null));

        fixture.Stack.Undo();
        Assert.Empty(fixture.Graph.Connections);

        fixture.Stack.Redo();
        Assert.Single(fixture.Graph.Connections);
    }

    /// <summary>
    /// An input holds one wire, so landing on a taken input silently replaces
    /// what was there. Undo has to put the old one back, not merely remove
    /// the new one.
    /// </summary>
    [Fact]
    public void Undoing_a_connection_restores_what_it_displaced()
    {
        var fixture = Build();
        var first = new ConstantImageNode { Width = 8, Height = 8, Value = 10 };
        var second = new ConstantImageNode { Width = 8, Height = 8, Value = 20 };
        var tool = new ThresholdNode();

        foreach (var node in new VisionNode[] { first, second, tool })
            fixture.Graph.Add(node);

        fixture.Graph.TryConnect(first.ImageOut, tool.ImageIn);

        var replaced = tool.ImageIn.Connection!.Source;
        fixture.Graph.TryConnect(second.ImageOut, tool.ImageIn);
        fixture.Stack.Record(new ConnectEdit(second.ImageOut, tool.ImageIn, replaced));

        Assert.Same(second.ImageOut, tool.ImageIn.Connection!.Source);

        fixture.Stack.Undo();

        Assert.Same(first.ImageOut, tool.ImageIn.Connection!.Source);
    }

    [Fact]
    public void Disconnecting_undoes()
    {
        var fixture = Build();
        var (source, tool) = AddPair(fixture);
        fixture.Graph.TryConnect(source.ImageOut, tool.ImageIn);

        var connection = fixture.Graph.Connections.Single();
        var edit = new DisconnectEdit([connection]);
        fixture.Graph.Disconnect(connection);
        fixture.Stack.Record(edit);

        fixture.Stack.Undo();
        Assert.Single(fixture.Graph.Connections);

        // Redo has to find the wire the undo above created, which is a
        // different Connection object from the one this edit was built with.
        fixture.Stack.Redo();
        Assert.Empty(fixture.Graph.Connections);
    }

    // ---- properties, recorded by watching ----------------------------------

    [Fact]
    public void A_parameter_change_undoes_without_anything_being_wired_up()
    {
        var fixture = Build();
        var (_, tool) = AddPair(fixture);

        tool.Threshold = 200;

        Assert.Equal("Change Threshold", fixture.Stack.UndoLabel);

        fixture.Stack.Undo();
        Assert.Equal(128, tool.Threshold);

        fixture.Stack.Redo();
        Assert.Equal(200, tool.Threshold);
    }

    [Theory]
    [InlineData(nameof(VisionNode.Name))]
    [InlineData(nameof(VisionNode.Enabled))]
    [InlineData(nameof(VisionNode.X))]
    [InlineData(nameof(VisionNode.Accent))]
    [InlineData(nameof(VisionNode.Measurements))]
    public void The_nodes_own_work_is_undoable(string property) =>
        Assert.Contains(property, UndoRecorder.UndoableProperties(new ThresholdNode()));

    /// <summary>
    /// The invariant that keeps the allow-list from drifting: anything the
    /// serialiser writes is work, and work that cannot be undone is the gap
    /// this whole mechanism exists to close.
    /// </summary>
    [Fact]
    public void Everything_saved_is_undoable()
    {
        var graph = new VisionGraph();

        // One of each, so a parameter on any node in the library counts.
        foreach (var descriptor in NodeCatalog.All)
            graph.Add(descriptor.CreateInstance());

        var document = GraphSerializer.ToDocument(graph);

        foreach (var (node, record) in graph.Nodes.Zip(document.Nodes))
        {
            var undoable = UndoRecorder.UndoableProperties(node);

            foreach (var parameter in record.Parameters.Keys)
            {
                Assert.True(
                    undoable.Contains(parameter),
                    $"{node.Descriptor.DisplayName}.{parameter} is saved but cannot be undone");
            }
        }
    }

    /// <summary>
    /// The engine writes Status and timings on every pass. Recording those
    /// would fill the stack with evaluation noise, and Undo would step
    /// backwards through things nobody did.
    /// </summary>
    [Theory]
    [InlineData(nameof(VisionNode.Status))]
    [InlineData(nameof(VisionNode.StatusMessage))]
    [InlineData(nameof(VisionNode.LastEvaluationTime))]
    public void What_the_engine_writes_is_not_undoable(string property) =>
        Assert.DoesNotContain(property, UndoRecorder.UndoableProperties(new ThresholdNode()));

    /// <summary>
    /// Undoing sets properties, and the recorder is watching properties.
    /// Without the guard, an undo records itself as new work: the stack then
    /// describes a history that never happened and redo walks sideways.
    /// </summary>
    [Fact]
    public void An_undo_does_not_record_itself()
    {
        var fixture = Build();
        var (_, tool) = AddPair(fixture);

        // Starting from an empty history, so what is left afterwards is only
        // what this test put there.
        fixture.Stack.Clear();

        tool.Threshold = 200;
        fixture.Stack.Undo();

        Assert.False(fixture.Stack.CanUndo, "the undo was recorded as a new edit");
        Assert.True(fixture.Stack.CanRedo);
    }

    /// <summary>
    /// A new edit makes the redo branch unreachable: replaying it onto a
    /// graph that has moved on is not redoing anything.
    /// </summary>
    [Fact]
    public void Editing_after_an_undo_drops_the_redo()
    {
        var fixture = Build();
        var (_, tool) = AddPair(fixture);

        tool.Threshold = 200;
        fixture.Stack.Undo();
        Assert.True(fixture.Stack.CanRedo);

        tool.Threshold = 50;

        Assert.False(fixture.Stack.CanRedo);
    }

    // ---- coalescing --------------------------------------------------------

    /// <summary>
    /// A slider dragged across its range raises a change per pixel. Without
    /// merging, undo walks back through two hundred of them and the operator
    /// concludes it is broken.
    /// </summary>
    [Fact]
    public void A_drag_is_one_step_back()
    {
        var fixture = Build();
        var (_, tool) = AddPair(fixture);
        fixture.Stack.Clear();

        for (var value = 129; value <= 200; value++)
            tool.Threshold = value;

        fixture.Stack.Undo();

        Assert.Equal(128, tool.Threshold);
        Assert.False(fixture.Stack.CanUndo, "the drag left more than one step on the stack");
    }

    /// <summary>
    /// And two separate decisions stay separate, which is the other half of
    /// the same rule - a window long enough to swallow a change of mind would
    /// be worse than no merging at all.
    /// </summary>
    [Fact]
    public async Task Two_decisions_are_two_steps_back()
    {
        var fixture = Build();
        var (_, tool) = AddPair(fixture);

        tool.Threshold = 100;
        await Task.Delay(PropertyEdit.MergeWindow + TimeSpan.FromMilliseconds(120));
        tool.Threshold = 200;

        fixture.Stack.Undo();
        Assert.Equal(100, tool.Threshold);

        fixture.Stack.Undo();
        Assert.Equal(128, tool.Threshold);
    }

    [Fact]
    public void Different_parameters_do_not_merge()
    {
        var fixture = Build();
        var (_, tool) = AddPair(fixture);

        tool.Threshold = 200;
        var before = tool.Name;
        tool.Name = "Board";

        // One step back takes the rename and leaves the threshold, because
        // merging is per property - a window that swallowed the pair would
        // undo two unrelated decisions at once.
        fixture.Stack.Undo();

        Assert.Equal(before, tool.Name);
        Assert.Equal(200, tool.Threshold);
    }

    // ---- batches -----------------------------------------------------------

    [Fact]
    public void A_batch_undoes_as_one_step()
    {
        var fixture = Build();
        var (_, tool) = AddPair(fixture);
        var second = new ThresholdNode();
        fixture.Graph.Add(second);
        fixture.Stack.Record(new AddNodeEdit(second));

        using (fixture.Stack.Begin("Colour 2 nodes"))
        {
            tool.Accent = NodeAccent.Red;
            second.Accent = NodeAccent.Red;
        }

        Assert.Equal("Colour 2 nodes", fixture.Stack.UndoLabel);

        fixture.Stack.Undo();

        Assert.Equal(NodeAccent.Default, tool.Accent);
        Assert.Equal(NodeAccent.Default, second.Accent);
    }

    /// <summary>
    /// A batch holding one edit keeps that edit's own label, which is more
    /// useful than the batch's guess at what it was going to hold.
    /// </summary>
    [Fact]
    public void A_batch_of_one_keeps_its_edits_label()
    {
        var fixture = Build();
        var (_, tool) = AddPair(fixture);

        using (fixture.Stack.Begin("Delete"))
            tool.Accent = NodeAccent.Blue;

        Assert.Equal($"Colour {tool.Name}", fixture.Stack.UndoLabel);
    }

    [Fact]
    public void An_empty_batch_records_nothing()
    {
        var fixture = Build();
        AddPair(fixture);

        var before = fixture.Stack.UndoLabel;

        using (fixture.Stack.Begin("Nothing happened"))
        {
        }

        Assert.Equal(before, fixture.Stack.UndoLabel);
    }

    /// <summary>
    /// A batch inside a batch joins the outer one. Starting a second history
    /// would leave the inner edits recorded separately and undone out of
    /// order.
    /// </summary>
    [Fact]
    public void A_nested_batch_joins_the_outer_one()
    {
        var fixture = Build();
        var (_, tool) = AddPair(fixture);

        using (fixture.Stack.Begin("Outer"))
        {
            tool.Accent = NodeAccent.Red;

            using (fixture.Stack.Begin("Inner"))
                tool.Name = "Board";
        }

        Assert.Equal("Outer", fixture.Stack.UndoLabel);

        fixture.Stack.Undo();

        Assert.Equal(NodeAccent.Default, tool.Accent);
        Assert.NotEqual("Board", tool.Name);
    }

    // ---- what needs re-running ---------------------------------------------

    /// <summary>
    /// Undoing a move must not re-run the recipe. A position says nothing
    /// about what the graph computes, and undo is exactly the operation
    /// somebody repeats quickly.
    /// </summary>
    [Fact]
    public void Undoing_a_move_does_not_ask_for_a_run()
    {
        var fixture = Build();
        var (_, tool) = AddPair(fixture);
        GraphEditEventArgs? applied = null;

        fixture.Stack.Applied += (_, e) => applied = e;

        tool.X = 400;
        fixture.Stack.Undo();

        Assert.NotNull(applied);
        Assert.False(applied!.AffectsResults);
    }

    [Fact]
    public void Undoing_a_parameter_asks_for_a_run()
    {
        var fixture = Build();
        var (_, tool) = AddPair(fixture);
        GraphEditEventArgs? applied = null;

        fixture.Stack.Applied += (_, e) => applied = e;

        tool.Threshold = 77;
        fixture.Stack.Undo();

        Assert.True(applied!.AffectsResults);
    }

    // ---- bounds and lifecycle ----------------------------------------------

    /// <summary>
    /// Bounded, and the oldest goes rather than refusing to record new work:
    /// losing the far past is what the operator expects, losing the present
    /// is not.
    /// </summary>
    [Fact]
    public void The_history_is_bounded()
    {
        var fixture = Build();
        var (_, tool) = AddPair(fixture);

        // Each is its own step: the recorder's merge window is on consecutive
        // edits to one property, and these alternate.
        for (var i = 0; i < UndoStack.Depth + 50; i++)
            tool.Name = $"Name {i}";

        var steps = 0;
        while (fixture.Stack.Undo())
            steps++;

        Assert.True(steps <= UndoStack.Depth, $"{steps} steps are on a stack bounded at {UndoStack.Depth}");
    }

    [Fact]
    public void Clearing_forgets_everything()
    {
        var fixture = Build();
        var (_, tool) = AddPair(fixture);

        tool.Threshold = 200;
        fixture.Stack.Undo();

        fixture.Stack.Clear();

        Assert.False(fixture.Stack.CanUndo);
        Assert.False(fixture.Stack.CanRedo);
    }

    /// <summary>
    /// Loading a recipe sets every property on every node. That is one
    /// action, not a hundred and forty.
    /// </summary>
    [Fact]
    public void Nothing_is_recorded_while_recording_is_paused()
    {
        var fixture = Build();
        var (_, tool) = AddPair(fixture);

        using (fixture.Recorder.Pause())
        {
            tool.Threshold = 200;
            tool.Name = "Board";
        }

        // The changes stuck, and the top of the stack is still the node's
        // own creation - so one step back removes the node rather than
        // walking through the properties the load set.
        Assert.Equal(200, tool.Threshold);
        Assert.Equal("Board", tool.Name);

        fixture.Stack.Undo();

        Assert.DoesNotContain(tool, fixture.Graph.Nodes);
    }

    /// <summary>
    /// A node taken out of the graph stops being watched, or the stack would
    /// grow from edits to something that is no longer part of the recipe.
    /// </summary>
    [Fact]
    public void A_removed_node_is_no_longer_watched()
    {
        var fixture = Build();
        var (_, tool) = AddPair(fixture);

        fixture.Graph.Remove(tool);
        fixture.Stack.Clear();

        tool.Threshold = 200;

        Assert.False(fixture.Stack.CanUndo);
    }

    /// <summary>
    /// A node added straight to the graph is watched from then on, without
    /// the recorder being told about it.
    /// </summary>
    [Fact]
    public void A_node_added_later_is_watched()
    {
        var fixture = Build();
        var added = new ThresholdNode { Threshold = 10 };

        fixture.Graph.Add(added);
        fixture.Stack.Clear();

        added.Threshold = 90;

        Assert.True(fixture.Stack.CanUndo);

        fixture.Stack.Undo();
        Assert.Equal(10, added.Threshold);
    }
}
