using Sirius.Core.Graph;
using Xunit;

namespace Sirius.Core.Tests;

public class VisionGraphTests
{
    public VisionGraphTests() => TestCatalog.Ensure();

    [Fact]
    public void Connecting_compatible_ports_succeeds()
    {
        var graph = new VisionGraph();
        var source = new TestSourceNode();
        var add = new TestAddNode();
        graph.Add(source);
        graph.Add(add);

        var result = graph.TryConnect(source.ValueOut, add.ValueIn);

        Assert.True(result.Success);
        Assert.Single(graph.Connections);
        Assert.True(add.ValueIn.IsConnected);
        Assert.True(source.ValueOut.IsConnected);
    }

    [Fact]
    public void Connecting_mismatched_types_is_rejected()
    {
        var graph = new VisionGraph();
        var text = new TestTextNode();
        var add = new TestAddNode();
        graph.Add(text);
        graph.Add(add);

        var result = graph.TryConnect(text.TextOut, add.ValueIn);

        Assert.False(result.Success);
        Assert.Equal(ConnectionRejection.TypeMismatch, result.Reason);
        Assert.Empty(graph.Connections);
    }

    [Fact]
    public void Connecting_a_node_to_itself_is_rejected()
    {
        var graph = new VisionGraph();
        var text = new TestTextNode();
        graph.Add(text);

        var result = graph.TryConnect(text.TextOut, text.TextIn);

        Assert.False(result.Success);
        Assert.Equal(ConnectionRejection.SameNode, result.Reason);
    }

    [Fact]
    public void Closing_a_loop_is_rejected()
    {
        var graph = new VisionGraph();
        var source = new TestSourceNode();
        var first = new TestAddNode();
        var second = new TestAddNode();
        graph.Add(source);
        graph.Add(first);
        graph.Add(second);

        graph.TryConnect(source.ValueOut, first.ValueIn);
        graph.TryConnect(first.ValueOut, second.ValueIn);

        // second -> first would close the loop first -> second -> first.
        var result = graph.TryConnect(second.ValueOut, first.ValueIn);

        Assert.False(result.Success);
        Assert.Equal(ConnectionRejection.WouldCreateCycle, result.Reason);
    }

    [Fact]
    public void An_input_accepts_only_one_connection_and_replaces_the_previous()
    {
        var graph = new VisionGraph();
        var firstSource = new TestSourceNode();
        var secondSource = new TestSourceNode();
        var add = new TestAddNode();
        graph.Add(firstSource);
        graph.Add(secondSource);
        graph.Add(add);

        graph.TryConnect(firstSource.ValueOut, add.ValueIn);
        graph.TryConnect(secondSource.ValueOut, add.ValueIn);

        Assert.Single(graph.Connections);
        Assert.Same(secondSource.ValueOut, add.ValueIn.Connection!.Source);
        Assert.False(firstSource.ValueOut.IsConnected);
    }

    [Fact]
    public void An_output_can_fan_out_to_several_inputs()
    {
        var graph = new VisionGraph();
        var source = new TestSourceNode();
        var left = new TestAddNode();
        var right = new TestAddNode();
        graph.Add(source);
        graph.Add(left);
        graph.Add(right);

        Assert.True(graph.TryConnect(source.ValueOut, left.ValueIn).Success);
        Assert.True(graph.TryConnect(source.ValueOut, right.ValueIn).Success);

        Assert.Equal(2, graph.Connections.Count);
        Assert.Equal(2, source.ValueOut.Connections.Count);
    }

    [Fact]
    public void Removing_a_node_removes_its_connections()
    {
        var graph = new VisionGraph();
        var source = new TestSourceNode();
        var add = new TestAddNode();
        graph.Add(source);
        graph.Add(add);
        graph.TryConnect(source.ValueOut, add.ValueIn);

        graph.Remove(source);

        Assert.Empty(graph.Connections);
        Assert.False(add.ValueIn.IsConnected);
    }

    [Fact]
    public void Topological_sort_places_dependencies_first()
    {
        var graph = new VisionGraph();
        var source = new TestSourceNode();
        var first = new TestAddNode();
        var second = new TestAddNode();

        // Added out of dependency order on purpose.
        graph.Add(second);
        graph.Add(first);
        graph.Add(source);

        graph.TryConnect(source.ValueOut, first.ValueIn);
        graph.TryConnect(first.ValueOut, second.ValueIn);

        Assert.True(graph.TryTopologicalSort(out var ordered, out var error));
        Assert.Null(error);
        Assert.Equal(new VisionNode[] { source, first, second }, ordered);
    }

    [Fact]
    public void Downstream_set_includes_the_node_and_everything_it_feeds()
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

        var affected = graph.GetSelfAndDownstream(middle);

        Assert.Equal(2, affected.Count);
        Assert.Contains(middle, affected);
        Assert.Contains(leaf, affected);
        Assert.DoesNotContain(source, affected);
    }
}
