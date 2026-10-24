using Sirius.Core.Graph;
using Sirius.Core.Serialization;
using Sirius.Core.Shapes;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Copy and paste over a graph, in the recipe's own file format.
/// </summary>
/// <remarks>
/// Using the save format rather than a second format for the clipboard is
/// the whole design: parameters, colours, measurements and the wires between
/// the copied nodes survive a paste with nothing written about any of them.
/// These tests are mostly checks that the format is really carrying all of
/// that, because the failure mode is a paste that looks right and has lost
/// one thing.
/// </remarks>
public class ClipboardTests
{
    public ClipboardTests() => TestCatalog.Ensure();

    private static (VisionGraph Graph, ConstantImageNode Source, ThresholdNode Tool) Build()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 64, Height = 48, Value = 200 };
        var tool = new ThresholdNode { Threshold = 77, Name = "Board", Accent = NodeAccent.Amber };

        graph.Add(source);
        graph.Add(tool);
        graph.TryConnect(source.ImageOut, tool.ImageIn);

        return (graph, source, tool);
    }

    [Fact]
    public void Copying_one_node_pastes_one_node()
    {
        var (graph, _, tool) = Build();

        var result = GraphClipboard.Paste(graph, GraphClipboard.Copy(graph, [tool]));

        var pasted = Assert.Single(result.Nodes);
        Assert.Equal(3, graph.Nodes.Count);
        Assert.NotSame(tool, pasted);
    }

    /// <summary>
    /// Everything the node was carrying comes with it. This is what the
    /// format buys, and a paste that dropped the parameter would look fine
    /// until the recipe ran.
    /// </summary>
    [Fact]
    public void A_pasted_node_keeps_its_settings()
    {
        var (graph, _, tool) = Build();
        tool.Measurements =
        [
            new MeasurementRecord("PointToPoint", [new Point2(1, 2), new Point2(3, 4)], "#FFD700", null, null),
        ];

        var pasted = (ThresholdNode)GraphClipboard
            .Paste(graph, GraphClipboard.Copy(graph, [tool]))
            .Nodes
            .Single();

        Assert.Equal(77, pasted.Threshold);
        Assert.Equal("Board", pasted.Name);
        Assert.Equal(NodeAccent.Amber, pasted.Accent);
        Assert.Single(pasted.Measurements);
    }

    /// <summary>
    /// A fresh id per paste. Keeping the copied one would mean two nodes in
    /// the recipe claiming to be the same node - which the saved file cannot
    /// express, and which the connection records resolve by id, so a second
    /// paste would silently rewire the first.
    /// </summary>
    [Fact]
    public void A_pasted_node_gets_a_new_id()
    {
        var (graph, _, tool) = Build();

        var pasted = GraphClipboard.Paste(graph, GraphClipboard.Copy(graph, [tool])).Nodes.Single();

        Assert.NotEqual(tool.Id, pasted.Id);
        Assert.Equal(graph.Nodes.Count, graph.Nodes.Select(node => node.Id).Distinct().Count());
    }

    /// <summary>
    /// Pasting the same clipboard twice gives two independent nodes, which is
    /// what the fresh id is for.
    /// </summary>
    [Fact]
    public void Pasting_twice_gives_two_nodes()
    {
        var (graph, _, tool) = Build();
        var json = GraphClipboard.Copy(graph, [tool]);

        var first = GraphClipboard.Paste(graph, json).Nodes.Single();
        var second = GraphClipboard.Paste(graph, json).Nodes.Single();

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(4, graph.Nodes.Count);
    }

    /// <summary>
    /// Wires between copied nodes come across, so copying a working pair
    /// gives a working pair.
    /// </summary>
    [Fact]
    public void A_wire_between_copied_nodes_comes_with_them()
    {
        var (graph, source, tool) = Build();

        var result = GraphClipboard.Paste(graph, GraphClipboard.Copy(graph, [source, tool]));

        Assert.Equal(2, result.Nodes.Count);

        var wire = Assert.Single(result.Wires);
        Assert.Contains(wire.Source.Node, result.Nodes);
        Assert.Contains(wire.Target.Node, result.Nodes);

        // The original wire and the pasted one, and no crossover between
        // the two pairs.
        Assert.Equal(2, graph.Connections.Count);
    }

    /// <summary>
    /// A wire to a node that was not copied is dropped rather than carried.
    /// There is nothing for the far end to land on, and pasting into a
    /// different recipe would mean hoping a node of that id happens to be
    /// there.
    /// </summary>
    [Fact]
    public void A_wire_to_a_node_that_was_not_copied_is_dropped()
    {
        var (graph, _, tool) = Build();

        var result = GraphClipboard.Paste(graph, GraphClipboard.Copy(graph, [tool]));

        Assert.Empty(result.Wires);
        Assert.Null(result.Nodes.Single().Inputs.OfType<InputPort>().First().Connection);

        // And the original is still wired, which is the other half of it.
        Assert.Single(graph.Connections);
    }

    /// <summary>
    /// Offset, so a paste is visible rather than sitting exactly on top of
    /// what it came from - and offset equally, so an arrangement of several
    /// nodes keeps its shape.
    /// </summary>
    [Fact]
    public void A_paste_lands_beside_the_original()
    {
        var (graph, source, tool) = Build();
        source.X = 100;
        source.Y = 50;
        tool.X = 300;
        tool.Y = 50;

        var result = GraphClipboard.Paste(graph, GraphClipboard.Copy(graph, [source, tool]), offset: 24);

        var pastedSource = result.Nodes.OfType<ConstantImageNode>().Single();
        var pastedTool = result.Nodes.OfType<ThresholdNode>().Single();

        Assert.Equal(124, pastedSource.X);
        Assert.Equal(74, pastedSource.Y);
        Assert.Equal(pastedTool.X - pastedSource.X, tool.X - source.X);
    }

    [Fact]
    public void Copying_nothing_pastes_nothing()
    {
        var (graph, _, _) = Build();

        var result = GraphClipboard.Paste(graph, GraphClipboard.Copy(graph, []));

        Assert.Empty(result.Nodes);
        Assert.Equal(2, graph.Nodes.Count);
    }

    // ---- what is on the clipboard is anyone's -------------------------------

    /// <summary>
    /// Asked before Paste is enabled, so it has to answer for whatever else
    /// is on the clipboard without throwing.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"nodes\":[]}")]
    [InlineData("[1, 2, 3]")]
    public void Rubbish_cannot_be_pasted(string? text) =>
        Assert.False(GraphClipboard.CanPaste(text));

    [Fact]
    public void A_copied_graph_can_be_pasted()
    {
        var (graph, _, tool) = Build();

        Assert.True(GraphClipboard.CanPaste(GraphClipboard.Copy(graph, [tool])));
    }

    /// <summary>
    /// A whole saved recipe is a valid clipboard payload, since the format is
    /// the same one. Pasting a `.sgraph` file's contents is a reasonable
    /// thing to be able to do, and it falls out rather than being built.
    /// </summary>
    [Fact]
    public void A_whole_recipe_can_be_pasted()
    {
        var (graph, _, _) = Build();
        var saved = GraphSerializer.Serialize(graph);

        var target = new VisionGraph();
        var result = GraphClipboard.Paste(target, saved);

        Assert.Equal(2, result.Nodes.Count);
        Assert.Single(result.Wires);
    }
}
