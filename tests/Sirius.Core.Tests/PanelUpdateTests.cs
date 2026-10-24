using System.ComponentModel;
using Sirius.Core.Graph;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The two-phase panel update.
/// </summary>
/// <remarks>
/// Panels are refreshed in two steps so the costly part - turning a native
/// image into a bitmap - happens off the UI thread. That only works if Prepare
/// stays silent: a property change raised from a worker thread would reach the
/// binding layer from the wrong thread.
/// </remarks>
public class PanelUpdateTests
{
    public PanelUpdateTests() => TestCatalog.Ensure();

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

    private static (VisionGraph Graph, GraphScheduler Scheduler, ViewerPanelViewModel Panel) BuildViewer()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 32, Height = 24, Value = 180 };
        var viewer = new ViewerNode();
        graph.Add(source);
        graph.Add(viewer);
        graph.TryConnect(source.ImageOut, viewer.ImageIn);

        return (graph, CreateScheduler(graph), new ViewerPanelViewModel(viewer, new ShapeSelectionService()));
    }

    [Fact]
    public async Task Prepare_raises_no_property_changes()
    {
        var (_, scheduler, panel) = BuildViewer();
        using var _scheduler = scheduler;
        await scheduler.RunAsync();

        var raised = new List<string?>();
        panel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        panel.Prepare(scheduler);

        Assert.Empty(raised);
    }

    [Fact]
    public async Task Commit_publishes_what_prepare_staged()
    {
        var (_, scheduler, panel) = BuildViewer();
        using var _scheduler = scheduler;
        await scheduler.RunAsync();

        panel.Prepare(scheduler);
        Assert.Null(panel.Image);

        panel.Commit();

        Assert.NotNull(panel.Image);
        Assert.Equal("32 x 24 x 1", panel.SizeText);
    }

    /// <summary>
    /// The bitmap must be frozen, or handing it from a worker thread to the UI
    /// would throw.
    /// </summary>
    [Fact]
    public async Task The_prepared_bitmap_is_frozen()
    {
        var (_, scheduler, panel) = BuildViewer();
        using var _scheduler = scheduler;
        await scheduler.RunAsync();

        panel.Prepare(scheduler);
        panel.Commit();

        Assert.True(panel.Image!.IsFrozen, "the bitmap is not frozen and cannot cross threads");
    }

    [Fact]
    public async Task Prepare_runs_on_a_worker_thread_without_error()
    {
        var (_, scheduler, panel) = BuildViewer();
        using var _scheduler = scheduler;
        await scheduler.RunAsync();

        // The whole point of the split: this is what the run loop does.
        await Task.Run(() => panel.Prepare(scheduler));
        panel.Commit();

        Assert.NotNull(panel.Image);
    }

    [Fact]
    public async Task An_analysis_panel_follows_the_same_two_phase_rule()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 16, Height = 16, Value = 60 };
        var histogram = new HistogramNode();
        graph.Add(source);
        graph.Add(histogram);
        graph.TryConnect(source.ImageOut, histogram.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        var panel = new HistogramPanelViewModel(histogram);

        var raised = new List<string?>();
        panel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        panel.Prepare(scheduler);
        Assert.Empty(raised);
        Assert.Null(panel.Data);

        panel.Commit();
        Assert.NotNull(panel.Data);
        Assert.Contains(nameof(HistogramPanelViewModel.Data), raised);
    }
}
