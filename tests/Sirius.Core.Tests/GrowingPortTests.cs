using System.Windows;
using Sirius.Core.Graph;
using Sirius.Core.Graph.Undo;
using Sirius.Core.Nodes;
using Sirius.Core.Serialization;
using Sirius.Core.Shapes;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// A viewer takes as many annotations as are wired into it.
/// </summary>
/// <remarks>
/// <para>
/// One image usually wants more than one overlay: blobs that passed in
/// green, ones that failed in red, a fitted circle over both. An input takes
/// a single connection and that rule is worth keeping, so the viewer makes
/// more sockets instead - one spare at the end, another as soon as it is
/// filled.
/// </para>
/// <para>
/// The count is derived from the wiring and never stored, which removes a
/// whole class of bug - a saved count disagreeing with the saved
/// connections - and creates one: a freshly built node has fewer sockets
/// than a saved recipe names. That is what
/// <see cref="IGrowingPorts.EnsureInput"/> is for, and it is the case most
/// worth testing here.
/// </para>
/// </remarks>
public class GrowingPortTests
{
    public GrowingPortTests() => TestCatalog.Ensure();

    private static ShapeSet Blobs(int count, int width = 64, int height = 48) =>
        new(
            Enumerable.Range(0, count)
                .Select(i => (Shape)new PointShape(new Point2(i, i)))
                .ToArray(),
            width,
            height);

    private sealed record Rig(VisionGraph Graph, ViewerNode Viewer, GraphEditorViewModel Editor);

    private static Rig Build()
    {
        var graph = new VisionGraph();
        var viewer = new ViewerNode();
        graph.Add(viewer);

        return new Rig(graph, viewer, new GraphEditorViewModel(graph, new UndoStack(graph)));
    }

    /// <summary>A node that emits a shape set, added to the graph.</summary>
    private static BlobFilterNode AddProducer(Rig rig)
    {
        var producer = new BlobFilterNode();
        rig.Graph.Add(producer);
        return producer;
    }

    private static OutputPort ShapesOf(BlobFilterNode node) =>
        node.Outputs.First(port => port.DataType == typeof(ShapeSet));

    /// <summary>
    /// A shape set chosen by the test, so the merge can be checked against
    /// known counts and frames.
    /// </summary>
    private sealed class ShapeSourceNode : VisionNode
    {
        public ShapeSourceNode() => ShapesOut = AddOutput<ShapeSet>("Out");

        public OutputPort ShapesOut { get; }

        public ShapeSet Set { get; init; } = ShapeSet.Empty;

        protected override void OnEvaluate(EvalContext context) => context.Set(ShapesOut, Set);
    }

    // ---- growing -----------------------------------------------------------

    /// <summary>
    /// It starts the way it always looked: one shapes socket, so an existing
    /// recipe and an existing screenshot still match.
    /// </summary>
    [Fact]
    public void A_new_viewer_has_one_shapes_socket()
    {
        var rig = Build();

        Assert.Single(rig.Viewer.ShapeInputs);
        Assert.Equal("Shapes", rig.Viewer.ShapeInputs[0].Name);
    }

    [Fact]
    public void Filling_the_last_socket_makes_another()
    {
        var rig = Build();
        var producer = AddProducer(rig);

        rig.Graph.TryConnect(ShapesOf(producer), rig.Viewer.ShapesIn);
        rig.Viewer.Fit();

        Assert.Equal(2, rig.Viewer.ShapeInputs.Count);
        Assert.Equal("Shapes 2", rig.Viewer.ShapeInputs[1].Name);
        Assert.False(rig.Viewer.ShapeInputs[1].IsConnected);
    }

    /// <summary>
    /// Four producers, four sockets, and a spare - which is the whole point
    /// of the exercise.
    /// </summary>
    [Fact]
    public void It_keeps_growing_as_producers_are_added()
    {
        var rig = Build();

        for (var i = 0; i < 4; i++)
        {
            var producer = AddProducer(rig);
            var socket = rig.Viewer.ShapeInputs.First(port => !port.IsConnected);

            rig.Graph.TryConnect(ShapesOf(producer), socket);
            rig.Viewer.Fit();
        }

        Assert.Equal(5, rig.Viewer.ShapeInputs.Count);
        Assert.Equal(4, rig.Viewer.ShapeInputs.Count(port => port.IsConnected));
    }

    /// <summary>
    /// And stops. A picture with a dozen overlays is unreadable, and a loop
    /// that adds sockets for ever is worse than a refusal.
    /// </summary>
    [Fact]
    public void It_stops_growing_somewhere()
    {
        var rig = Build();

        for (var i = 0; i < 40; i++)
        {
            var socket = rig.Viewer.ShapeInputs.FirstOrDefault(port => !port.IsConnected);

            if (socket is null)
                break;

            var producer = AddProducer(rig);
            rig.Graph.TryConnect(ShapesOf(producer), socket);
            rig.Viewer.Fit();
        }

        Assert.InRange(rig.Viewer.ShapeInputs.Count, 2, 12);
    }

    // ---- shrinking ---------------------------------------------------------

    [Fact]
    public void Freeing_a_socket_collapses_the_spares()
    {
        var rig = Build();
        var first = AddProducer(rig);
        var second = AddProducer(rig);

        rig.Graph.TryConnect(ShapesOf(first), rig.Viewer.ShapesIn);
        rig.Viewer.Fit();
        rig.Graph.TryConnect(ShapesOf(second), rig.Viewer.ShapeInputs[1]);
        rig.Viewer.Fit();

        Assert.Equal(3, rig.Viewer.ShapeInputs.Count);

        rig.Graph.Disconnect(rig.Viewer.ShapeInputs[1].Connection!);
        rig.Viewer.Fit();

        Assert.Equal(2, rig.Viewer.ShapeInputs.Count);
    }

    /// <summary>
    /// A gap in the middle is left alone. Closing it would renumber the
    /// sockets below, which moves somebody's wire to a different row for no
    /// reason they asked for.
    /// </summary>
    [Fact]
    public void A_gap_in_the_middle_is_left_alone()
    {
        var rig = Build();
        var first = AddProducer(rig);
        var second = AddProducer(rig);

        rig.Graph.TryConnect(ShapesOf(first), rig.Viewer.ShapesIn);
        rig.Viewer.Fit();
        rig.Graph.TryConnect(ShapesOf(second), rig.Viewer.ShapeInputs[1]);
        rig.Viewer.Fit();

        // Free the first, leaving the second connected below it.
        rig.Graph.Disconnect(rig.Viewer.ShapesIn.Connection!);
        rig.Viewer.Fit();

        Assert.Equal("Shapes", rig.Viewer.ShapeInputs[0].Name);
        Assert.False(rig.Viewer.ShapeInputs[0].IsConnected);
        Assert.True(rig.Viewer.ShapeInputs[1].IsConnected);
    }

    /// <summary>
    /// Shrinking never takes a connected socket with it, however many spares
    /// it is trying to remove.
    /// </summary>
    [Fact]
    public void Shrinking_never_drops_a_wire()
    {
        var rig = Build();
        var producer = AddProducer(rig);

        rig.Graph.TryConnect(ShapesOf(producer), rig.Viewer.ShapesIn);

        for (var i = 0; i < 5; i++)
            rig.Viewer.Fit();

        Assert.Single(rig.Graph.Connections);
        Assert.True(rig.Viewer.ShapesIn.IsConnected);
    }

    [Fact]
    public void Fit_is_safe_to_call_when_nothing_changed()
    {
        var rig = Build();

        Assert.False(rig.Viewer.Fit());
        Assert.False(rig.Viewer.Fit());
        Assert.Single(rig.Viewer.ShapeInputs);
    }

    // ---- saving and opening -------------------------------------------------

    /// <summary>
    /// The case the derived count creates: a saved recipe names sockets a
    /// freshly built node does not have yet. Without
    /// <see cref="IGrowingPorts.EnsureInput"/> the recipe opens having
    /// quietly dropped every wire past the second.
    /// </summary>
    [Fact]
    public void A_saved_recipe_keeps_every_overlay()
    {
        var rig = Build();

        for (var i = 0; i < 3; i++)
        {
            var producer = AddProducer(rig);
            rig.Graph.TryConnect(
                ShapesOf(producer),
                rig.Viewer.ShapeInputs.First(port => !port.IsConnected));
            rig.Viewer.Fit();
        }

        var result = GraphSerializer.Deserialize(GraphSerializer.Serialize(rig.Graph));
        var restored = result.Graph.Nodes.OfType<ViewerNode>().Single();

        Assert.Empty(result.Warnings);
        Assert.Equal(3, restored.ShapeInputs.Count(port => port.IsConnected));
    }

    /// <summary>
    /// A recipe written before the viewer could grow still opens: the first
    /// socket kept the bare name it has always had.
    /// </summary>
    [Fact]
    public void A_recipe_from_before_this_still_opens()
    {
        var rig = Build();
        var producer = AddProducer(rig);
        rig.Graph.TryConnect(ShapesOf(producer), rig.Viewer.ShapesIn);

        var document = GraphSerializer.ToDocument(rig.Graph);

        Assert.Contains(document.Connections, record => record.ToPort == "Shapes");

        var result = GraphSerializer.FromDocument(document);

        Assert.Empty(result.Warnings);
        Assert.True(result.Graph.Nodes.OfType<ViewerNode>().Single().ShapesIn.IsConnected);
    }

    /// <summary>
    /// A socket name from a build that allowed more of them than this one is
    /// refused rather than honoured without limit.
    /// </summary>
    [Theory]
    [InlineData("Shapes 900")]
    [InlineData("Shapes nine")]
    [InlineData("Shapes ")]
    [InlineData("Whatever")]
    public void A_name_it_would_never_have_made_is_refused(string name) =>
        Assert.Null(((IGrowingPorts)new ViewerNode()).EnsureInput(name));

    // ---- what the panel draws ------------------------------------------------

    /// <summary>
    /// Two producers, one picture. Concatenated rather than chosen between,
    /// which is the thing that could not be done before.
    /// </summary>
    [Fact]
    public void Every_connected_set_is_drawn()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 64, Height = 48, Value = 200 };
        var viewer = new ViewerNode();

        graph.Add(source);
        graph.Add(viewer);
        graph.TryConnect(source.ImageOut, viewer.ImageIn);

        var first = new ShapeSourceNode { Set = Blobs(3) };
        var second = new ShapeSourceNode { Set = Blobs(2) };
        graph.Add(first);
        graph.Add(second);

        graph.TryConnect(first.ShapesOut, viewer.ShapesIn);
        viewer.Fit();
        graph.TryConnect(second.ShapesOut, viewer.ShapeInputs[1]);
        viewer.Fit();

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var panel = new ViewerPanelViewModel(viewer, new ShapeSelectionService());
        panel.Prepare(scheduler);
        panel.Commit();

        Assert.Equal(5, panel.Shapes!.Count);

        panel.Dispose();
    }

    /// <summary>
    /// A set measured in a different frame is left out rather than drawn in
    /// the wrong place - which would look like a bad fit rather than a
    /// wiring mistake.
    /// </summary>
    [Fact]
    public void A_set_from_another_frame_is_not_drawn()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 64, Height = 48, Value = 200 };
        var viewer = new ViewerNode();

        graph.Add(source);
        graph.Add(viewer);
        graph.TryConnect(source.ImageOut, viewer.ImageIn);

        var matching = new ShapeSourceNode { Set = Blobs(3, 64, 48) };
        var foreign = new ShapeSourceNode { Set = Blobs(4, 1000, 1000) };
        graph.Add(matching);
        graph.Add(foreign);

        graph.TryConnect(matching.ShapesOut, viewer.ShapesIn);
        viewer.Fit();
        graph.TryConnect(foreign.ShapesOut, viewer.ShapeInputs[1]);
        viewer.Fit();

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var panel = new ViewerPanelViewModel(viewer, new ShapeSelectionService());
        panel.Prepare(scheduler);
        panel.Commit();

        Assert.Equal(3, panel.Shapes!.Count);

        // And the node says why, rather than leaving it as shapes that are
        // simply not there.
        Assert.Equal(NodeStatus.Warning, viewer.Status);
        Assert.Contains("different image size", viewer.StatusMessage!, StringComparison.Ordinal);

        panel.Dispose();
    }

    /// <summary>
    /// One producer still shares its selection with a table reading the same
    /// wire. Two producers share none, because the merged indices no longer
    /// mean the same thing on both sides - picking the fifth shape would
    /// light up the fifth row of a table that does not contain it.
    /// </summary>
    [Fact]
    public void Selection_is_shared_only_while_there_is_one_producer()
    {
        var rig = Build();
        var first = AddProducer(rig);

        rig.Graph.TryConnect(ShapesOf(first), rig.Viewer.ShapesIn);
        rig.Viewer.Fit();

        var panel = new ViewerPanelViewModel(rig.Viewer, new ShapeSelectionService());
        panel.Prepare(CreateScheduler(rig.Graph));

        Assert.NotNull(Producer(panel));

        var second = AddProducer(rig);
        rig.Graph.TryConnect(ShapesOf(second), rig.Viewer.ShapeInputs[1]);
        rig.Viewer.Fit();

        Assert.Null(Producer(panel));

        panel.Dispose();
    }

    private static OutputPort? Producer(ViewerPanelViewModel panel) =>
        (OutputPort?)typeof(ImagePanelViewModel)
            .GetProperty("ShapeProducer", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(panel);

    // ---- the canvas keeps up -------------------------------------------------

    /// <summary>
    /// The socket has to appear on the canvas, not only on the node. A node
    /// that grew a port the editor never re-read is a socket nobody can
    /// drag a wire to.
    /// </summary>
    [Fact]
    public void The_canvas_shows_the_new_socket()
    {
        var rig = Build();
        var producer = AddProducer(rig);
        rig.Editor.Rebuild();

        var before = rig.Editor.Nodes
            .First(node => ReferenceEquals(node.Node, rig.Viewer))
            .Inputs.Count;

        rig.Graph.TryConnect(ShapesOf(producer), rig.Viewer.ShapesIn);
        rig.Editor.FitPorts();

        var after = rig.Editor.Nodes
            .First(node => ReferenceEquals(node.Node, rig.Viewer))
            .Inputs.Count;

        Assert.Equal(before + 1, after);
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
