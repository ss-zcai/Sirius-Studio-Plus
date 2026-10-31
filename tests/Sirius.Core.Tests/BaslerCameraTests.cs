using System.Diagnostics;
using Sirius.Core.Graph;
using Sirius.Vision;
using Sirius.Vision.Cameras;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The Basler driver, against the camera emulator pylon installs.
/// </summary>
/// <remarks>
/// The driver is found the way the application finds it - by loading
/// whatever Sirius.Cameras.*.dll sits beside the program - so on a PC
/// without pylon there is no Basler driver and these pass having checked
/// nothing. The emulator appears only with PYLON_CAMEMU set, which is set
/// here before pylon first looks for cameras.
/// </remarks>
[Collection(BaslerCollection.Name)]
public class BaslerCameraTests
{
    static BaslerCameraTests()
    {
        Environment.SetEnvironmentVariable("PYLON_CAMEMU", "1");
    }

    public BaslerCameraTests() => TestCatalog.Ensure();

    private static ICameraDriver? Basler()
    {
        CameraDrivers.LoadInstalled(AppContext.BaseDirectory);
        return CameraDrivers.All.FirstOrDefault(driver => driver.Name == "Basler");
    }

    private static CameraInfo? Emulated(ICameraDriver driver) =>
        driver.Enumerate().FirstOrDefault(camera => camera.Model.Contains("Emulation", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void The_driver_loads_where_pylon_is_installed_and_lists_the_emulator()
    {
        if (Basler() is not { } driver)
            return;

        var camera = Emulated(driver);

        Assert.NotNull(camera);
        Assert.Equal("Basler", camera.Driver);
        Assert.False(string.IsNullOrEmpty(camera.Serial));
        Assert.Empty(CameraDrivers.LoadProblems);
    }

    [Theory]
    [InlineData(CameraTrigger.Software)]
    [InlineData(CameraTrigger.FreeRun)]
    public void Frames_come_back_as_images(CameraTrigger trigger)
    {
        if (Basler() is not { } driver || Emulated(driver) is not { } info)
            return;

        var camera = CameraDrivers.Acquire(info.Label)!;

        camera.Trigger = trigger;
        camera.Exposure = 5000;
        camera.Gain = 1;

        using var first = camera.Grab(TimeSpan.FromSeconds(5), CancellationToken.None);
        using var second = camera.Grab(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(first.Width > 0 && first.Height > 0);
        Assert.Equal((first.Width, first.Height), (second.Width, second.Height));
    }

    /// <summary>Waiting for a hardware trigger that never comes ends at the timeout, not never.</summary>
    [Fact]
    public void A_hardware_trigger_that_never_comes_times_out()
    {
        if (Basler() is not { } driver || Emulated(driver) is not { } info)
            return;

        var camera = CameraDrivers.Acquire(info.Label)!;
        camera.Trigger = CameraTrigger.Hardware;

        var clock = Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() => camera.Grab(TimeSpan.FromMilliseconds(300), CancellationToken.None));
        Assert.InRange(clock.ElapsedMilliseconds, 250, 3000);

        camera.Trigger = CameraTrigger.Software;
    }

    [Fact]
    public void The_camera_node_takes_frames_from_a_basler_camera()
    {
        if (Basler() is not { } driver || Emulated(driver) is not { } info)
            return;

        var graph = new VisionGraph();
        var node = new CameraNode { Device = info.Label, Trigger = CameraTrigger.Software };
        graph.Add(node);

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        GraphScheduler scheduler;
        try
        {
            scheduler = new GraphScheduler(graph);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        using (scheduler)
        {
            scheduler.InspectAsync().GetAwaiter().GetResult();

            Assert.Equal(NodeStatus.Ready, node.Status);
            Assert.True(scheduler.GetOutput<ImageData>(node.ImageOut)!.Width > 0);
            Assert.Contains(info.Label, node.ChoicesFor(nameof(CameraNode.Device)));
        }
    }
}

/// <summary>One camera, one test at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BaslerCollection
{
    public const string Name = "Basler cameras";
}
