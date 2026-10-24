using Sirius.Core.Graph;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Interactive line profiling on a viewer panel.
/// </summary>
/// <remarks>
/// A profile drawn on the image is sampled from a copy of the frame the panel
/// keeps while profiling is on. The copy can only be taken inside an
/// evaluation pass, because the graph reclaims its buffers as soon as the pass
/// ends, so turning profiling on has to ask for a pass and the next one has to
/// honour it. Each step is pinned here: break any of them and the chart shows
/// "No profile" with a line plainly drawn on the image.
/// </remarks>
public class ViewerProfilingTests
{
    public ViewerProfilingTests() => TestCatalog.Ensure();

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

    private static (GraphScheduler Scheduler, ViewerPanelViewModel Panel) BuildViewer()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 64, Height = 48, Value = 180 };
        var viewer = new ViewerNode();
        graph.Add(source);
        graph.Add(viewer);
        graph.TryConnect(source.ImageOut, viewer.ImageIn);

        return (CreateScheduler(graph), new ViewerPanelViewModel(viewer, new ShapeSelectionService()));
    }

    [Fact]
    public void Turning_profiling_on_asks_for_a_pass()
    {
        var (scheduler, panel) = BuildViewer();
        using var _ = scheduler;

        var requests = 0;
        panel.ProfileDataRequested += (_, _) => requests++;

        panel.IsProfilingEnabled = true;

        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task A_pass_while_profiling_produces_a_source_to_sample()
    {
        var (scheduler, panel) = BuildViewer();
        using var _ = scheduler;

        panel.IsProfilingEnabled = true;

        await scheduler.RunAsync();
        panel.Prepare(scheduler);
        panel.Commit();

        Assert.NotNull(panel.ProfileSource);
    }

    /// <summary>
    /// The pass that profiling asks for is a re-run of an unchanged graph, so
    /// every node is cached and none is evaluated. The frame still has to be
    /// captured: reading the cache is what the panel does, and skipping that
    /// on a cached pass is the difference between a working chart and one that
    /// only fills in after some unrelated edit dirties the graph.
    /// </summary>
    [Fact]
    public async Task A_fully_cached_pass_still_captures_the_frame()
    {
        var (scheduler, panel) = BuildViewer();
        using var _ = scheduler;

        // Warm the cache first, exactly as the viewer being on screen does.
        await scheduler.RunAsync();
        panel.Prepare(scheduler);
        panel.Commit();

        panel.IsProfilingEnabled = true;

        var result = await scheduler.RunAsync();
        Assert.Equal(0, result.Evaluated);

        panel.Prepare(scheduler);
        panel.Commit();

        Assert.NotNull(panel.ProfileSource);
    }

    [Fact]
    public async Task The_captured_frame_samples_the_image()
    {
        var (scheduler, panel) = BuildViewer();
        using var _ = scheduler;

        panel.IsProfilingEnabled = true;
        await scheduler.RunAsync();
        panel.Prepare(scheduler);
        panel.Commit();

        var data = panel.ProfileSource!.Sample(
            new System.Drawing.PointF(0, 10),
            new System.Drawing.PointF(63, 10),
            thickness: 1);

        Assert.NotNull(data);
        Assert.NotEmpty(data.Gray().Samples);
        Assert.All(data.Gray().Samples, sample => Assert.Equal(180f, sample, 1));
    }

    /// <summary>
    /// Prepare reads the switch on a worker thread while it is flipped on the
    /// UI thread, so a pass already past Prepare commits no frame although
    /// profiling is on. Left there, the chart would stay empty until some
    /// unrelated edit happened to dirty the graph.
    /// </summary>
    [Fact]
    public async Task A_pass_that_missed_the_switch_asks_again()
    {
        var (scheduler, panel) = BuildViewer();
        using var _ = scheduler;

        await scheduler.RunAsync();

        // Prepare first, with profiling still off - the race, deterministically.
        panel.Prepare(scheduler);
        panel.IsProfilingEnabled = true;

        var requests = 0;
        panel.ProfileDataRequested += (_, _) => requests++;

        panel.Commit();

        Assert.Null(panel.ProfileSource);
        Assert.Equal(1, requests);
    }

    /// <summary>
    /// A viewer with nothing wired into it has no frame to capture, so the
    /// retry above must not turn into a pass every pass.
    /// </summary>
    [Fact]
    public async Task A_viewer_with_no_image_does_not_ask_again()
    {
        var graph = new VisionGraph();
        var viewer = new ViewerNode();
        graph.Add(viewer);

        using var scheduler = CreateScheduler(graph);
        var panel = new ViewerPanelViewModel(viewer, new ShapeSelectionService());

        panel.IsProfilingEnabled = true;
        await scheduler.RunAsync();
        panel.Prepare(scheduler);

        var requests = 0;
        panel.ProfileDataRequested += (_, _) => requests++;

        panel.Commit();

        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Turning_profiling_off_releases_the_frame()
    {
        var (scheduler, panel) = BuildViewer();
        using var _ = scheduler;

        panel.IsProfilingEnabled = true;
        await scheduler.RunAsync();
        panel.Prepare(scheduler);
        panel.Commit();
        Assert.NotNull(panel.ProfileSource);

        panel.IsProfilingEnabled = false;

        Assert.Null(panel.ProfileSource);
    }
}
