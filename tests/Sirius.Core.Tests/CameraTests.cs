using Emgu.CV;
using Sirius.Core.Graph;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Cameras;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The Camera node, against the simulated camera that is always there.
/// </summary>
public class CameraTests
{
    private static readonly string Simulated = $"Simulated test pattern [{SimulatedCameraDriver.SerialNumber}]";

    public CameraTests() => TestCatalog.Ensure();

    private static (VisionGraph Graph, CameraNode Camera, GraphScheduler Scheduler) Build(string device)
    {
        var graph = new VisionGraph();
        var camera = new CameraNode { Device = device };
        graph.Add(camera);

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return (graph, camera, new GraphScheduler(graph));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static Mat Frame(GraphScheduler scheduler, CameraNode camera) =>
        scheduler.GetOutput<ImageData>(camera.ImageOut)!.Mat.Clone();

    private static bool Same(Mat a, Mat b)
    {
        using var difference = new Mat();
        CvInvoke.AbsDiff(a, b, difference);
        return CvInvoke.CountNonZero(difference) == 0;
    }

    [Fact]
    public void Each_inspection_takes_a_new_frame_and_a_re_run_keeps_it()
    {
        var (_, camera, scheduler) = Build(Simulated);

        using (scheduler)
        {
            scheduler.InspectAsync().GetAwaiter().GetResult();
            using var first = Frame(scheduler, camera);

            // A re-run after an edit somewhere else: the same picture.
            camera.Invalidate();
            scheduler.RunAsync().GetAwaiter().GetResult();
            using var again = Frame(scheduler, camera);
            Assert.True(Same(first, again));

            scheduler.InspectAsync().GetAwaiter().GetResult();
            using var next = Frame(scheduler, camera);
            Assert.False(Same(first, next));
        }
    }

    /// <summary>Changing exposure is meant to be seen: it takes a new frame, and a brighter one.</summary>
    [Fact]
    public void Changing_exposure_takes_a_new_brighter_frame()
    {
        var (_, camera, scheduler) = Build(Simulated);

        using (scheduler)
        {
            scheduler.InspectAsync().GetAwaiter().GetResult();
            var before = CvInvoke.Mean(scheduler.GetOutput<ImageData>(camera.ImageOut)!.Mat).V0;

            camera.Exposure = 20_000;
            scheduler.RunAsync().GetAwaiter().GetResult();
            var after = CvInvoke.Mean(scheduler.GetOutput<ImageData>(camera.ImageOut)!.Mat).V0;

            Assert.True(after > before * 1.5, $"expected brighter: {before} then {after}");
        }
    }

    [Fact]
    public void A_camera_that_is_not_there_says_so()
    {
        var (_, camera, scheduler) = Build("Basler acA1920-40gm [00000000]");

        using (scheduler)
        {
            scheduler.InspectAsync().GetAwaiter().GetResult();

            Assert.Equal(NodeStatus.Error, camera.Status);
            Assert.Contains("was not found", camera.StatusMessage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_camera_is_found_by_its_serial_number()
    {
        Assert.Equal("23456789", CameraInfo.SerialIn("Basler acA1920-40gm [23456789]"));
        Assert.Null(CameraInfo.SerialIn("no serial here"));
    }

    /// <summary>
    /// The Device list is every camera found - and the one the recipe
    /// names, when it is not among them, so an unplugged camera still shows
    /// which one is wanted.
    /// </summary>
    [Fact]
    public void The_device_list_shows_what_is_found_and_keeps_what_the_recipe_names()
    {
        var node = new CameraNode { Device = "Basler acA1920-40gm [23456789]" };
        var device = new NodeViewModel(node).Parameters.Single(p => p.DisplayName == "Device");

        var options = device.Options.Cast<string>().ToArray();

        Assert.True(device.IsComboBox);
        Assert.Equal("Basler acA1920-40gm [23456789]", options[0]);
        Assert.Contains(Simulated, options);
        Assert.Equal("Basler acA1920-40gm [23456789]", device.SelectedOption);

        device.SelectedOption = Simulated;
        Assert.Equal(Simulated, node.Device);
    }
}
