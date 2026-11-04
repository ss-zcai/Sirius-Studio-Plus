using System.Windows;
using NodeGraphKit.Model;
using NodeGraphKit.Serialization;
using NodeGraphKit.ViewModels;

namespace NodeGraphKit.Tests;

internal static class TestNodes
{
    public static readonly PortType Number = PortTypes.Register("Test Number", "#FBBF24");
    public static readonly PortType Text = PortTypes.Register("Test Text", "#34D399");

    public static readonly NodeDefinition Value = new("test.value", "Value")
    {
        Outputs = [new("Out", Number)],
        Parameters = [ParameterDefinition.Number("Value", 1, minimum: 0, maximum: 10)],
    };

    public static readonly NodeDefinition Sum = new("test.sum", "Sum")
    {
        Inputs = [new("A", Number), new("B", Number)],
        Outputs = [new("Out", Number)],
        Parameters =
        [
            ParameterDefinition.Choice("Mode", ["Add", "Subtract"]),
            ParameterDefinition.Integer("Places", 2, minimum: 0, maximum: 6),
            ParameterDefinition.Boolean("Round"),
            ParameterDefinition.Text("Note", "hello"),
        ],
    };

    public static readonly NodeDefinition Show = new("test.show", "Show")
    {
        Inputs = [new("Text", Text), new("Anything", PortTypes.Any)],
    };

    public static NodeCatalog Catalog() => new([Value, Sum, Show]);
}

public class GraphTests
{
    private static (NodeGraph Graph, GraphNode Value, GraphNode Sum, GraphNode Show) Build()
    {
        var graph = new NodeGraph();
        var value = new GraphNode(TestNodes.Value);
        var sum = new GraphNode(TestNodes.Sum);
        var show = new GraphNode(TestNodes.Show);

        graph.Add(value);
        graph.Add(sum);
        graph.Add(show);
        return (graph, value, sum, show);
    }

    [Fact]
    public void An_output_wires_to_an_input_of_its_type_and_both_ends_show_it()
    {
        var (graph, value, sum, _) = Build();

        var result = graph.Connect(value.Output("Out")!, sum.Input("A")!);

        Assert.True(result.Success);
        Assert.True(value.Output("Out")!.IsConnected);
        Assert.True(sum.Input("A")!.IsConnected);
        Assert.False(sum.Input("B")!.IsConnected);
    }

    [Fact]
    public void Types_that_do_not_match_are_refused_with_a_reason()
    {
        var (graph, value, _, show) = Build();

        var result = graph.Connect(value.Output("Out")!, show.Input("Text")!);

        Assert.False(result.Success);
        Assert.Contains("cannot go into", result.Message);
        Assert.Empty(graph.Connections);
    }

    [Fact]
    public void An_any_input_takes_every_type()
    {
        var (graph, value, _, show) = Build();

        Assert.True(graph.Connect(value.Output("Out")!, show.Input("Anything")!).Success);
    }

    [Fact]
    public void A_wire_into_a_taken_input_replaces_the_old_one()
    {
        var (graph, value, sum, _) = Build();
        var other = new GraphNode(TestNodes.Value);
        graph.Add(other);

        var first = graph.Connect(value.Output("Out")!, sum.Input("A")!).Connection;
        var second = graph.Connect(other.Output("Out")!, sum.Input("A")!);

        Assert.Same(first, second.Replaced);
        Assert.Single(graph.Connections);
        Assert.False(value.Output("Out")!.IsConnected);
    }

    [Fact]
    public void A_loop_is_refused()
    {
        var (graph, _, sum, _) = Build();
        var second = new GraphNode(TestNodes.Sum);
        graph.Add(second);

        graph.Connect(sum.Output("Out")!, second.Input("A")!);
        var result = graph.Connect(second.Output("Out")!, sum.Input("A")!);

        Assert.False(result.Success);
        Assert.Contains("loop", result.Message);
    }

    [Fact]
    public void Removing_a_node_removes_its_wires()
    {
        var (graph, value, sum, _) = Build();
        graph.Connect(value.Output("Out")!, sum.Input("A")!);

        graph.Remove(sum);

        Assert.Empty(graph.Connections);
        Assert.False(value.Output("Out")!.IsConnected);
    }

    [Fact]
    public void The_running_order_puts_every_node_after_what_feeds_it()
    {
        var (graph, value, sum, show) = Build();
        graph.Connect(sum.Output("Out")!, show.Input("Anything")!);
        graph.Connect(value.Output("Out")!, sum.Input("A")!);

        var order = graph.TopologicalOrder().ToList();

        Assert.True(order.IndexOf(value) < order.IndexOf(sum));
        Assert.True(order.IndexOf(sum) < order.IndexOf(show));
    }

    [Fact]
    public void Parameters_are_held_in_their_kind_and_range()
    {
        var sum = new GraphNode(TestNodes.Sum);
        var value = new GraphNode(TestNodes.Value);

        value.SetParameter("Value", 99.0);
        sum.SetParameter("Places", 2.6);
        sum.SetParameter("Mode", "Divide");
        sum.SetParameter("Round", "true");

        Assert.Equal(10.0, value.GetParameter("Value"));
        Assert.Equal(3, sum.GetParameter("Places"));
        Assert.Equal("Add", sum.GetParameter("Mode"));
        Assert.Equal(true, sum.GetParameter("Round"));
    }

    [Fact]
    public void A_saved_graph_loads_back_the_same()
    {
        var (graph, value, sum, show) = Build();
        value.Name = "Width";
        value.X = 12;
        value.Accent = NodeAccent.Teal;
        sum.Enabled = false;
        value.SetParameter("Value", 4.5);
        sum.SetParameter("Mode", "Subtract");
        sum.SetParameter("Places", 5);
        sum.SetParameter("Note", "a note");
        graph.Connect(value.Output("Out")!, sum.Input("B")!);
        graph.Connect(sum.Output("Out")!, show.Input("Anything")!);

        var copy = new NodeGraph();
        var result = GraphSerializer.Load(GraphSerializer.Save(graph), TestNodes.Catalog(), copy);

        Assert.Empty(result.Warnings);
        Assert.Equal(GraphSerializer.Save(graph), GraphSerializer.Save(copy));

        var loaded = copy.Find(value.Id)!;
        Assert.Equal("Width", loaded.Name);
        Assert.Equal(NodeAccent.Teal, loaded.Accent);
        Assert.Equal(4.5, loaded.GetParameter("Value"));
        Assert.Equal(5, copy.Find(sum.Id)!.GetParameter("Places"));
        Assert.Equal(2, copy.Connections.Count);
    }

    [Fact]
    public void A_node_type_that_is_gone_is_left_out_and_named()
    {
        var (graph, value, sum, _) = Build();
        graph.Connect(value.Output("Out")!, sum.Input("A")!);
        var json = GraphSerializer.Save(graph);

        var copy = new NodeGraph();
        var result = GraphSerializer.Load(json, new NodeCatalog([TestNodes.Sum, TestNodes.Show]), copy);

        Assert.Equal(2, copy.Nodes.Count);
        Assert.Empty(copy.Connections);
        Assert.Contains(result.Warnings, w => w.Contains("test.value"));
    }
}

public class EditorTests
{
    private static GraphEditorViewModel Editor() => new(TestNodes.Catalog());

    private static void Wire(GraphEditorViewModel editor, NodeViewModel from, string output, NodeViewModel to, string input) =>
        editor.CompleteConnectionCommand.Execute((
            (object)from.Outputs.Single(c => c.Title == output),
            (object)to.Inputs.Single(c => c.Title == input)));

    [Fact]
    public void Adding_a_node_is_one_step_back()
    {
        var editor = Editor();

        editor.AddNode(TestNodes.Value, new Point(10, 20));

        Assert.Single(editor.Nodes);
        Assert.Equal("Undo Add Value", editor.UndoText);

        editor.UndoCommand.Execute(null);
        Assert.Empty(editor.Nodes);

        editor.RedoCommand.Execute(null);
        Assert.Single(editor.Nodes);
        Assert.Equal(new Point(10, 20), editor.Nodes[0].Location);
    }

    [Fact]
    public void A_wire_dragged_backwards_from_the_input_still_connects()
    {
        var editor = Editor();
        var value = editor.AddNode(TestNodes.Value, new Point());
        var sum = editor.AddNode(TestNodes.Sum, new Point(200, 0));

        editor.CompleteConnectionCommand.Execute((
            (object)sum.Inputs[0],
            (object)value.Outputs[0]));

        Assert.Single(editor.Connections);
        Assert.True(sum.Inputs[0].IsConnected);
    }

    [Fact]
    public void Undoing_a_delete_brings_the_node_back_with_its_wires_and_selection()
    {
        var editor = Editor();
        var value = editor.AddNode(TestNodes.Value, new Point());
        var sum = editor.AddNode(TestNodes.Sum, new Point(200, 0));
        Wire(editor, value, "Out", sum, "A");
        var id = sum.Node.Id;

        editor.SelectOnly([sum]);
        editor.DeleteSelectionCommand.Execute(null);

        Assert.Single(editor.Nodes);
        Assert.Empty(editor.Connections);
        Assert.Equal("Undo Delete Sum", editor.UndoText);

        editor.UndoCommand.Execute(null);

        Assert.Equal(2, editor.Nodes.Count);
        Assert.Single(editor.Connections);
        Assert.Equal(id, editor.SelectedNode?.Node.Id);
    }

    [Fact]
    public void Duplicating_copies_the_wires_inside_the_selection_with_new_ids()
    {
        var editor = Editor();
        var value = editor.AddNode(TestNodes.Value, new Point());
        var sum = editor.AddNode(TestNodes.Sum, new Point(200, 0));
        Wire(editor, value, "Out", sum, "A");

        editor.SelectOnly([value, sum]);
        editor.DuplicateCommand.Execute(null);

        Assert.Equal(4, editor.Nodes.Count);
        Assert.Equal(2, editor.Connections.Count);
        Assert.Equal(4, editor.Graph.Nodes.Select(n => n.Id).Distinct().Count());
        Assert.Equal(2, editor.Nodes.Count(n => n.IsSelected));
        Assert.Equal("Undo Duplicate", editor.UndoText);
    }

    [Fact]
    public void Typing_a_value_in_steps_is_one_undo()
    {
        var editor = Editor();
        var value = editor.AddNode(TestNodes.Value, new Point());

        value.Parameters[0].NumericValue = 2;
        value.Parameters[0].NumericValue = 3;
        value.Parameters[0].NumericValue = 4;

        Assert.Equal("Undo Change Value", editor.UndoText);
        editor.UndoCommand.Execute(null);

        Assert.Equal(1.0, editor.Nodes.Single().Node.GetParameter("Value"));
    }

    [Fact]
    public void Colouring_the_selection_is_one_step()
    {
        var editor = Editor();
        var a = editor.AddNode(TestNodes.Value, new Point());
        var b = editor.AddNode(TestNodes.Value, new Point(0, 100));

        editor.SelectOnly([a, b]);
        editor.SetAccentCommand.Execute(NodeAccent.Red);

        Assert.All(editor.Nodes, n => Assert.Equal(NodeAccent.Red, n.Accent));
        editor.UndoCommand.Execute(null);
        Assert.All(editor.Nodes, n => Assert.Equal(NodeAccent.Default, n.Accent));
    }

    [Fact]
    public void Saving_marks_the_graph_clean_until_the_next_edit()
    {
        var editor = Editor();
        editor.AddNode(TestNodes.Value, new Point());
        Assert.True(editor.IsDirty);

        editor.History.MarkSaved();
        Assert.False(editor.IsDirty);

        editor.Nodes[0].Name = "Renamed";
        Assert.True(editor.IsDirty);
    }
}
