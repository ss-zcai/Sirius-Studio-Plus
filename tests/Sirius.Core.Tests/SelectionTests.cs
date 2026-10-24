using System.Windows;
using Sirius.Core.Graph;
using Sirius.Core.Graph.Undo;
using Sirius.Core.Nodes;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Covers the editor's selection plumbing.
/// </summary>
/// <remarks>
/// Regression tests for a real defect: <c>SelectedNode</c> was assigned only
/// when a node was added or the graph was rebuilt, so clicking a node on the
/// canvas left the inspector showing whatever was selected last. The canvas
/// reports a click by setting <c>IsSelected</c> on the container (which the
/// item-container style binds to the view-model) and by maintaining its
/// <c>SelectedItems</c> collection, so both routes are exercised here.
/// </remarks>
public class SelectionTests
{
    public SelectionTests() => TestCatalog.Ensure();

    private static (GraphEditorViewModel Editor, NodeViewModel First, NodeViewModel Second) Build()
    {
        var graph = new VisionGraph();
        graph.Add(new ThresholdNode());
        graph.Add(new GaussianBlurNode());

        var editor = new GraphEditorViewModel(graph, new UndoStack(graph));
        return (editor, editor.Nodes[0], editor.Nodes[1]);
    }

    [Fact]
    public void Selecting_a_node_on_the_canvas_updates_SelectedNode()
    {
        var (editor, _, second) = Build();

        second.IsSelected = true;

        Assert.Same(second, editor.SelectedNode);
    }

    [Fact]
    public void Selecting_a_different_node_moves_the_inspector_to_it()
    {
        var (editor, first, second) = Build();

        first.IsSelected = true;
        Assert.Same(first, editor.SelectedNode);

        second.IsSelected = true;
        Assert.Same(second, editor.SelectedNode);
    }

    [Fact]
    public void Deselecting_the_current_node_falls_back_to_another_selected_one()
    {
        var (editor, first, second) = Build();

        first.IsSelected = true;
        second.IsSelected = true;

        // The canvas deselects the earlier node when the selection collapses.
        second.IsSelected = false;

        Assert.Same(first, editor.SelectedNode);
    }

    [Fact]
    public void Clearing_the_selection_empties_the_inspector()
    {
        var (editor, first, _) = Build();

        first.IsSelected = true;
        first.IsSelected = false;

        Assert.Null(editor.SelectedNode);
    }

    [Fact]
    public void The_SelectedItems_collection_also_drives_SelectedNode()
    {
        var (editor, _, second) = Build();

        editor.SelectedItems.Add(second);

        Assert.Same(second, editor.SelectedNode);
    }

    [Fact]
    public void Emptying_SelectedItems_clears_SelectedNode()
    {
        var (editor, first, _) = Build();

        editor.SelectedItems.Add(first);
        editor.SelectedItems.Clear();

        Assert.Null(editor.SelectedNode);
    }

    [Fact]
    public void A_newly_added_node_becomes_the_selected_one()
    {
        var (editor, first, _) = Build();
        first.IsSelected = true;

        var descriptor = NodeCatalog.FindByTypeKey("basic.crop")!;
        var added = editor.AddNode(descriptor, new Point(10, 10));

        Assert.Same(added, editor.SelectedNode);
        Assert.True(added.IsSelected);
        Assert.False(first.IsSelected);
    }

    [Fact]
    public void Removing_the_selected_node_clears_the_inspector()
    {
        var (editor, first, _) = Build();
        first.IsSelected = true;

        editor.RemoveNode(first);

        Assert.Null(editor.SelectedNode);
        Assert.DoesNotContain(first, editor.Nodes);
    }

    /// <summary>
    /// A removed node must stop influencing selection, which is also what stops
    /// its event subscription keeping it alive.
    /// </summary>
    [Fact]
    public void A_removed_node_no_longer_drives_selection()
    {
        var (editor, first, second) = Build();

        editor.RemoveNode(first);
        second.IsSelected = true;

        first.IsSelected = true;

        Assert.Same(second, editor.SelectedNode);
    }

    [Fact]
    public void The_selected_node_exposes_its_parameters_to_the_inspector()
    {
        var (editor, first, _) = Build();

        first.IsSelected = true;

        Assert.Same(first, editor.SelectedNode);
        Assert.True(editor.SelectedNode!.HasParameters);

        // Threshold declares Threshold / Max Value / Type.
        Assert.Contains(editor.SelectedNode.Parameters, p => p.DisplayName == "Threshold");
        Assert.Contains(editor.SelectedNode.Parameters, p => p.DisplayName == "Max Value");
    }

    /// <summary>
    /// Builds source -> threshold so there is a connection to act on.
    /// </summary>
    private static (GraphEditorViewModel Editor, ConnectionViewModel Connection) BuildConnected()
    {
        var graph = new VisionGraph();
        var source = new ImageFileSourceNode();
        var threshold = new ThresholdNode();
        graph.Add(source);
        graph.Add(threshold);
        graph.TryConnect(source.ImageOut, threshold.ImageIn);

        var editor = new GraphEditorViewModel(graph, new UndoStack(graph));
        return (editor, editor.Connections.Single());
    }

    [Fact]
    public void Selecting_a_connection_and_deleting_removes_it()
    {
        var (editor, connection) = BuildConnected();

        connection.IsSelected = true;
        Assert.True(editor.DeleteSelectionCommand.CanExecute(null));

        editor.DeleteSelectionCommand.Execute(null);

        Assert.Empty(editor.Connections);
        Assert.Empty(editor.Graph.Connections);

        // Both nodes survive; only the wire went.
        Assert.Equal(2, editor.Nodes.Count);
    }

    [Fact]
    public void The_SelectedConnections_collection_also_enables_deletion()
    {
        var (editor, connection) = BuildConnected();

        editor.SelectedConnections.Add(connection);
        Assert.True(editor.DeleteSelectionCommand.CanExecute(null));

        editor.DeleteSelectionCommand.Execute(null);

        Assert.Empty(editor.Graph.Connections);
    }

    [Fact]
    public void Removing_a_connection_leaves_the_input_free_to_reconnect()
    {
        var (editor, connection) = BuildConnected();

        editor.RemoveConnectionCommand.Execute(connection);

        var threshold = editor.Graph.Nodes.OfType<ThresholdNode>().Single();
        Assert.False(threshold.ImageIn.IsConnected);

        var source = editor.Graph.Nodes.OfType<ImageFileSourceNode>().Single();
        Assert.True(editor.Graph.TryConnect(source.ImageOut, threshold.ImageIn).Success);
    }

    [Fact]
    public void Deleting_a_node_and_its_connection_together_does_not_double_remove()
    {
        var (editor, connection) = BuildConnected();

        connection.IsSelected = true;
        editor.Nodes[0].IsSelected = true;

        // Connections are deleted before nodes; removing the node would
        // otherwise drop the same wire a second time.
        editor.DeleteSelectionCommand.Execute(null);

        Assert.Empty(editor.Graph.Connections);
        Assert.Single(editor.Nodes);
    }

    [Fact]
    public void Nothing_selected_means_nothing_to_delete()
    {
        var (editor, _) = BuildConnected();

        Assert.False(editor.DeleteSelectionCommand.CanExecute(null));
    }

    [Fact]
    public void A_region_parameter_is_exposed_with_the_region_editor()
    {
        var graph = new VisionGraph();
        graph.Add(new CropNode());
        var editor = new GraphEditorViewModel(graph, new UndoStack(graph));

        editor.Nodes[0].IsSelected = true;

        var region = editor.SelectedNode!.Parameters.Single(p => p.Kind == ParamEditor.Region);
        Assert.Equal("Region", region.DisplayName);
    }
}
