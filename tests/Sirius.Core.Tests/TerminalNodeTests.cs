using Sirius.Core.Graph;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Display nodes terminate a branch: they present data rather than transform
/// it, so they expose no image output to chain onwards from.
/// </summary>
public class TerminalNodeTests
{
    public TerminalNodeTests() => TestCatalog.Ensure();

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

    [Fact]
    public void A_viewer_has_no_outputs()
    {
        var viewer = new ViewerNode();

        Assert.Empty(viewer.Outputs);
        Assert.Null(viewer.PrimaryOutput);

        // The image it shows, and optionally geometry to draw over it. The
        // shapes input is what makes an annotation nothing more than a wire.
        Assert.Equal(2, viewer.Inputs.Count);
        Assert.True(viewer.ImageIn.IsRequired);
        Assert.False(viewer.ShapesIn.IsRequired);
    }

    [Fact]
    public void Histogram_exposes_its_measurement_but_not_the_image()
    {
        var node = new HistogramNode();

        Assert.Null(node.FindOutput("Out"));
        Assert.NotNull(node.FindOutput("Histogram"));
    }

    [Fact]
    public void Line_profile_exposes_its_measurement_but_not_the_image()
    {
        var node = new LineProfileNode();

        Assert.Null(node.FindOutput("Out"));
        Assert.NotNull(node.FindOutput("Profile"));
    }

    /// <summary>
    /// With no output of its own, a viewer's panel reads what was fed in.
    /// </summary>
    [Fact]
    public async Task A_viewer_input_can_be_read_back_from_the_scheduler()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 48, Height = 24 };
        var viewer = new ViewerNode();
        graph.Add(source);
        graph.Add(viewer);
        graph.TryConnect(source.ImageOut, viewer.ImageIn);

        using var scheduler = CreateScheduler(graph);
        var result = await scheduler.RunAsync();

        Assert.True(result.Success);
        Assert.Equal(NodeStatus.Ready, viewer.Status);

        var image = scheduler.GetInput<ImageData>(viewer.ImageIn);
        Assert.NotNull(image);
        Assert.Equal(48, image!.Width);

        // Same instance the producer published - the viewer copies nothing.
        Assert.Same(scheduler.GetOutput<ImageData>(source.ImageOut), image);
    }

    [Fact]
    public void Reading_an_unconnected_input_returns_nothing()
    {
        var graph = new VisionGraph();
        var viewer = new ViewerNode();
        graph.Add(viewer);

        using var scheduler = CreateScheduler(graph);

        Assert.Null(scheduler.GetInput<ImageData>(viewer.ImageIn));
    }

    /// <summary>
    /// Terminal nodes make branching the normal way to watch a mid-graph
    /// image, so one output must still feed several of them.
    /// </summary>
    [Fact]
    public async Task One_output_can_feed_a_viewer_and_a_histogram_at_once()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 32, Height = 32, Value = 90 };
        var viewer = new ViewerNode();
        var histogram = new HistogramNode();

        graph.Add(source);
        graph.Add(viewer);
        graph.Add(histogram);

        Assert.True(graph.TryConnect(source.ImageOut, viewer.ImageIn).Success);
        Assert.True(graph.TryConnect(source.ImageOut, histogram.ImageIn).Success);

        using var scheduler = CreateScheduler(graph);
        var result = await scheduler.RunAsync();

        Assert.True(result.Success);
        Assert.NotNull(scheduler.GetInput<ImageData>(viewer.ImageIn));
        Assert.NotNull(scheduler.GetOutput<HistogramData>(histogram.HistogramOut));
    }

    [Fact]
    public async Task A_viewer_with_nothing_wired_in_reports_the_problem()
    {
        var graph = new VisionGraph();
        var viewer = new ViewerNode();
        graph.Add(viewer);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Equal(NodeStatus.Error, viewer.Status);
        Assert.Contains("not connected", viewer.StatusMessage!);
    }
}
