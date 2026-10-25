using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Previews: a viewer opened on one port of one node.
/// </summary>
/// <remarks>
/// The point of binding a preview to a port rather than to a node is that it
/// shows, by construction, the pixels that port carries - so geometry drawn
/// on it is in that port's coordinate space and nothing has to be assumed
/// about what lies between.
/// </remarks>
public class PreviewPanelTests
{
    public PreviewPanelTests() => TestCatalog.Ensure();

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

    /// <summary>A source feeding a threshold, which is what gets previewed.</summary>
    private static (VisionGraph Graph, ConstantImageNode Source, ThresholdNode Tool) Build()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 64, Height = 48, Value = 200 };
        var tool = new ThresholdNode { Threshold = 128 };

        graph.Add(source);
        graph.Add(tool);
        graph.TryConnect(source.ImageOut, tool.ImageIn);

        return (graph, source, tool);
    }

    private static PreviewPanelViewModel Preview(VisionNode node, Port port) =>
        new(node, port, null, new ShapeSelectionService());

    // ---- which ports are offered -------------------------------------------

    [Fact]
    public void Only_ports_with_something_to_draw_are_offered()
    {
        var (_, _, tool) = Build();

        var ports = MainViewModel.PreviewablePorts(tool);

        Assert.All(
            ports,
            port => Assert.True(
                port.DataType == typeof(ImageData) || port.DataType == typeof(ShapeSet),
                $"{port.Name} carries {port.DataType.Name} and has nothing to draw"));
    }

    /// <summary>
    /// A number has nothing to show. Offering it and then refusing would be
    /// worse than not offering it.
    /// </summary>
    [Fact]
    public void A_node_with_no_image_offers_nothing()
    {
        var value = new NumberNode();

        Assert.Empty(MainViewModel.PreviewablePorts(value));
        Assert.Null(MainViewModel.DefaultPreviewPort(value));
    }

    /// <summary>
    /// "What did this do" is the commoner question, so a plain double click
    /// lands on the output.
    /// </summary>
    [Fact]
    public void The_default_port_is_an_output()
    {
        var (_, _, tool) = Build();

        var port = MainViewModel.DefaultPreviewPort(tool);

        Assert.NotNull(port);
        Assert.Equal(PortDirection.Output, port!.Direction);
    }

    // ---- what a preview reads ----------------------------------------------

    [Fact]
    public async Task A_preview_of_an_output_shows_what_the_node_produced()
    {
        var (graph, _, tool) = Build();
        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        var panel = Preview(tool, tool.ImageOut);
        panel.Prepare(scheduler);
        panel.Commit();

        Assert.NotNull(panel.Image);

        // Thresholded, so the frame is binary rather than the source's 200.
        var produced = scheduler.GetOutput<ImageData>(tool.ImageOut)!;
        Assert.Equal($"{produced.Width} x {produced.Height} x {produced.Channels}", panel.SizeText);
    }

    /// <summary>
    /// The distinction the whole design rests on: an input preview shows what
    /// the node was given, not what it made.
    /// </summary>
    [Fact]
    public async Task A_preview_of_an_input_shows_what_the_node_was_given()
    {
        var (graph, source, tool) = Build();
        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        var input = Preview(tool, tool.ImageIn);
        input.Prepare(scheduler);
        input.Commit();

        var upstream = scheduler.GetOutput<ImageData>(source.ImageOut)!;

        Assert.NotNull(input.Image);
        Assert.Equal($"{upstream.Width} x {upstream.Height} x {upstream.Channels}", input.SizeText);
    }

    /// <summary>
    /// Shapes have to be drawn over something, and the only image the node
    /// can speak for is the one it was given. This is the answer to "what is
    /// the out image of a blob tool".
    /// </summary>
    [Fact]
    public async Task A_preview_of_a_shape_port_draws_over_the_nodes_input()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 64, Height = 48, Value = 200 };
        var blobs = new BlobFilterNode();
        graph.Add(source);
        graph.Add(blobs);
        graph.TryConnect(source.ImageOut, blobs.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        var panel = new PreviewPanelViewModel(
            blobs, blobs.ShapesOut, blobs.ImageIn, new ShapeSelectionService());

        panel.Prepare(scheduler);
        panel.Commit();

        Assert.NotNull(panel.Image);
        Assert.Equal(scheduler.GetOutput<ShapeSet>(blobs.ShapesOut), panel.Shapes);
    }

    // ---- identity -----------------------------------------------------------

    /// <summary>
    /// Input and output are different windows. One window arguing with itself
    /// over which to show would defeat the point of choosing.
    /// </summary>
    [Fact]
    public void Each_port_gets_its_own_identity_and_title()
    {
        var (_, _, tool) = Build();

        var input = Preview(tool, tool.ImageIn);
        var output = Preview(tool, tool.ImageOut);

        Assert.NotEqual(input.ContentId, output.ContentId);
        Assert.NotEqual(input.Title, output.Title);
        Assert.Contains(tool.Name, input.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void The_title_follows_the_node_name()
    {
        var (_, _, tool) = Build();
        var panel = Preview(tool, tool.ImageOut);

        tool.Name = "Binarise";

        Assert.Contains("Binarise", panel.Title, StringComparison.Ordinal);
    }

    // ---- opening and closing ------------------------------------------------

    /// <summary>
    /// Asking twice gives the same window rather than stacking a second on
    /// the first, and asks for it to be brought forward.
    /// </summary>
    [Fact]
    public void Opening_the_same_port_twice_reuses_the_window()
    {
        var shell = new MainViewModel();
        var node = shell.Editor.AddNode(
            NodeCatalog.All.First(d => d.DisplayName == "Threshold"),
            new System.Windows.Point(0, 0));

        var port = MainViewModel.DefaultPreviewPort(node.Node)!;

        var focused = new List<string>();
        shell.PanelFocusRequested += (_, id) => focused.Add(id);

        var first = shell.OpenPreview(node.Node, port);
        var second = shell.OpenPreview(node.Node, port);

        Assert.Same(first, second);
        Assert.Single(shell.NodePanels.OfType<PreviewPanelViewModel>());
        Assert.Equal(2, focused.Count);
    }

    [Fact]
    public void Input_and_output_open_as_separate_windows()
    {
        var shell = new MainViewModel();
        var node = shell.Editor.AddNode(
            NodeCatalog.All.First(d => d.DisplayName == "Threshold"),
            new System.Windows.Point(0, 0));

        var tool = (ThresholdNode)node.Node;

        shell.OpenPreview(tool, tool.ImageOut);
        shell.OpenPreview(tool, tool.ImageIn);

        Assert.Equal(2, shell.NodePanels.OfType<PreviewPanelViewModel>().Count());
    }

    /// <summary>
    /// A port carrying a number has nothing to draw, so there is nothing to
    /// open rather than an empty window.
    /// </summary>
    [Fact]
    public void A_port_with_nothing_to_draw_opens_nothing()
    {
        var shell = new MainViewModel();
        var node = shell.Editor.AddNode(
            NodeCatalog.All.First(d => d.DisplayName == "Number"),
            new System.Windows.Point(0, 0));

        var value = (NumberNode)node.Node;

        Assert.Null(shell.OpenPreview(value, value.ValueOut));
        Assert.Empty(shell.NodePanels.OfType<PreviewPanelViewModel>());
    }

    /// <summary>
    /// The regression: a preview is not implied by the graph, so the sweep
    /// that keeps panels in step with the nodes must not build one. It did,
    /// and every closed preview came back the moment anything else opened a
    /// window.
    /// </summary>
    [Fact]
    public void The_panel_sweep_does_not_recreate_a_closed_preview()
    {
        var shell = new MainViewModel();
        var node = shell.Editor.AddNode(
            NodeCatalog.All.First(d => d.DisplayName == "Threshold"),
            new System.Windows.Point(0, 0));

        var preview = shell.OpenPreview(node.Node, MainViewModel.DefaultPreviewPort(node.Node)!);
        Assert.NotNull(preview);

        // What closing the window does.
        shell.NodePanels.Remove(preview!);

        // What anything touching the graph does afterwards.
        shell.Editor.Rebuild();

        Assert.Empty(shell.NodePanels.OfType<PreviewPanelViewModel>());
    }

    /// <summary>
    /// A preview open on a Viewer node must not be mistaken for that node's
    /// own panel, or the sweep would stop rebuilding the viewer.
    /// </summary>
    [Fact]
    public void A_preview_does_not_stand_in_for_the_nodes_own_panel()
    {
        var shell = new MainViewModel();
        var node = shell.Editor.AddNode(
            NodeCatalog.All.First(d => d.DisplayName == "Image Viewer"),
            new System.Windows.Point(0, 0));

        var viewer = (ViewerNode)node.Node;
        shell.OpenPreview(viewer, viewer.ImageIn);

        shell.Editor.Rebuild();

        Assert.Single(shell.NodePanels.OfType<ViewerPanelViewModel>());
        Assert.Single(shell.NodePanels.OfType<PreviewPanelViewModel>());
    }

    /// <summary>Deleting the node closes what was opened on it.</summary>
    [Fact]
    public void Deleting_the_node_closes_its_previews()
    {
        var shell = new MainViewModel();
        var node = shell.Editor.AddNode(
            NodeCatalog.All.First(d => d.DisplayName == "Threshold"),
            new System.Windows.Point(0, 0));

        shell.OpenPreview(node.Node, MainViewModel.DefaultPreviewPort(node.Node)!);
        Assert.Single(shell.NodePanels.OfType<PreviewPanelViewModel>());

        shell.Editor.RemoveNode(node);

        Assert.Empty(shell.NodePanels.OfType<PreviewPanelViewModel>());
    }

    /// <summary>
    /// The properties panel offers the same ports as the canvas menu, and
    /// only while a node that has some is selected.
    /// </summary>
    [Fact]
    public void The_properties_panel_offers_the_selected_nodes_ports()
    {
        var shell = new MainViewModel();
        var node = shell.Editor.AddNode(
            NodeCatalog.All.First(d => d.DisplayName == "Threshold"),
            new System.Windows.Point(0, 0));

        shell.Editor.SelectedNode = node;

        Assert.Equal(
            MainViewModel.PreviewablePorts(node.Node).Count,
            shell.SelectedNodePreviews.Count);

        shell.Editor.SelectedNode = null;

        Assert.Empty(shell.SelectedNodePreviews);
    }

    [Fact]
    public void The_preview_button_opens_that_port()
    {
        var shell = new MainViewModel();
        var node = shell.Editor.AddNode(
            NodeCatalog.All.First(d => d.DisplayName == "Threshold"),
            new System.Windows.Point(0, 0));

        shell.Editor.SelectedNode = node;
        var option = shell.SelectedNodePreviews.First();

        shell.PreviewPortCommand.Execute(option);

        var preview = Assert.Single(shell.NodePanels.OfType<PreviewPanelViewModel>());
        Assert.Same(option.Port, preview.Port);
    }

    // ---- opening from a port ------------------------------------------------

    /// <summary>
    /// Double clicking a port opens that port. The window walks the visual
    /// tree to the connector; what it finds there is a node and a port, which
    /// is exactly what opening a preview needs - so the path is pinned from
    /// the connector onwards.
    /// </summary>
    [Fact]
    public void A_connector_identifies_the_port_to_open()
    {
        var shell = new MainViewModel();
        var node = shell.Editor.AddNode(
            NodeCatalog.All.First(d => d.DisplayName == "Threshold"),
            new System.Windows.Point(0, 0));

        var connector = node.Outputs.First(c => c.Port.DataType == typeof(ImageData));

        var preview = shell.OpenPreview(connector.Node.Node, connector.Port);

        Assert.NotNull(preview);
        Assert.Same(connector.Port, preview!.Port);
    }

    /// <summary>
    /// Both sides of the same node open different windows, which is the point
    /// of being able to pick a port at all.
    /// </summary>
    [Fact]
    public void The_two_sides_of_a_node_open_different_images()
    {
        var shell = new MainViewModel();
        var node = shell.Editor.AddNode(
            NodeCatalog.All.First(d => d.DisplayName == "Gaussian Blur"),
            new System.Windows.Point(0, 0));

        var input = node.Inputs.First(c => c.Port.DataType == typeof(ImageData));
        var output = node.Outputs.First(c => c.Port.DataType == typeof(ImageData));

        var before = shell.OpenPreview(input.Node.Node, input.Port);
        var after = shell.OpenPreview(output.Node.Node, output.Port);

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.NotEqual(before!.ContentId, after!.ContentId);
    }

    /// <summary>
    /// A port carrying a number opens nothing at all, rather than an empty
    /// window explaining that there is nothing to see.
    /// </summary>
    [Fact]
    public void A_value_port_opens_nothing()
    {
        var shell = new MainViewModel();
        var node = shell.Editor.AddNode(
            NodeCatalog.All.First(d => d.DisplayName == "Find Blobs"),
            new System.Windows.Point(0, 0));

        var count = node.Outputs.First(c => c.Port.DataType == typeof(int));

        Assert.Null(shell.OpenPreview(count.Node.Node, count.Port));
        Assert.Empty(shell.NodePanels.OfType<PreviewPanelViewModel>());
    }
}


