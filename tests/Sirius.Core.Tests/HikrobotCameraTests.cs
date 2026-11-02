using System.Diagnostics;
using System.Reflection;
using Sirius.Core.Graph;
using Sirius.Vision;
using Sirius.Vision.Cameras;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The Hikrobot driver, against a virtual camera made with MVS's Virtual
/// Device Tool.
/// </summary>
/// <remarks>
/// The driver is found the way the application finds it - by loading
/// whatever Sirius.Cameras.*.dll sits beside the program. On a PC without
/// MVS there is no Hikrobot driver, and without a virtual camera there is
/// nothing to grab from: either way these pass having checked only what
/// they can.
/// </remarks>
[Collection(HikrobotCollection.Name)]
public class HikrobotCameraTests
{
    public HikrobotCameraTests() => TestCatalog.Ensure();

    private static ICameraDriver? Hikrobot()
    {
        CameraDrivers.LoadInstalled(AppContext.BaseDirectory);
        return CameraDrivers.All.FirstOrDefault(driver => driver.Name == "Hikrobot");
    }

    /// <summary>The first camera MVS lists - on a test PC, the virtual one.</summary>
    private static CameraInfo? Any(ICameraDriver driver) => driver.Enumerate().FirstOrDefault();

    [Fact]
    public void The_driver_loads_where_MVS_is_installed()
    {
        if (!MvsInstalled())
            return;

        var driver = Hikrobot();

        Assert.True(driver is not null, "No Hikrobot driver: " + string.Join("; ", CameraDrivers.LoadProblems));
        Assert.All(driver!.Enumerate(), camera =>
        {
            Assert.Equal("Hikrobot", camera.Driver);
            Assert.False(string.IsNullOrEmpty(camera.Serial));
        });
    }

    [Theory]
    [InlineData(CameraTrigger.Software)]
    [InlineData(CameraTrigger.FreeRun)]
    public void Frames_come_back_as_images(CameraTrigger trigger)
    {
        if (Hikrobot() is not { } driver)
            return;

        // Every camera there is - a mono and a colour one, ideally, so both
        // ways a frame becomes an image are taken.
        foreach (var info in driver.Enumerate())
        {
            var camera = CameraDrivers.Acquire(info.Label)!;

            try
            {
                camera.Trigger = trigger;
                camera.Exposure = 5000;
                camera.Gain = 1;

                using var first = camera.Grab(TimeSpan.FromSeconds(5), CancellationToken.None);
                using var second = camera.Grab(TimeSpan.FromSeconds(5), CancellationToken.None);

                Assert.True(first.Width > 0 && first.Height > 0);
                Assert.Equal((first.Width, first.Height), (second.Width, second.Height));

                // Hikrobot names a mono model ...M and a colour one ...C.
                if (info.Model.EndsWith('M'))
                    Assert.Equal(1, first.Channels);
                else if (info.Model.EndsWith('C'))
                    Assert.Equal(3, first.Channels);
            }
            finally
            {
                // Let go, as the application does when it closes: a camera
                // a test run leaves open can stay taken after it ends.
                CameraDrivers.Release(camera);
            }
        }
    }

    /// <summary>Waiting for a hardware trigger that never comes ends at the timeout, not never.</summary>
    [Fact]
    public void A_hardware_trigger_that_never_comes_times_out()
    {
        if (Hikrobot() is not { } driver || Any(driver) is not { } info)
            return;

        var camera = CameraDrivers.Acquire(info.Label)!;

        try
        {
            camera.Trigger = CameraTrigger.Hardware;

            var clock = Stopwatch.StartNew();
            Assert.Throws<TimeoutException>(() => camera.Grab(TimeSpan.FromMilliseconds(300), CancellationToken.None));
            Assert.InRange(clock.ElapsedMilliseconds, 250, 3000);
        }
        finally
        {
            CameraDrivers.Release(camera);
        }
    }

    [Fact]
    public void The_camera_node_takes_frames_from_a_hikrobot_camera()
    {
        if (Hikrobot() is not { } driver || Any(driver) is not { } info)
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

            try
            {
                Assert.Equal(NodeStatus.Ready, node.Status);
                Assert.True(scheduler.GetOutput<ImageData>(node.ImageOut)!.Width > 0);
                Assert.Contains(info.Label, node.ChoicesFor(nameof(CameraNode.Device)));
            }
            finally
            {
                if (CameraDrivers.Acquire(info.Label) is { } camera)
                    CameraDrivers.Release(camera);
            }
        }
    }

    /// <summary>MVS keeps a GigE camera's address as one number, the first part in its top byte.</summary>
    [Theory]
    [InlineData(0xC0A80164u, "192.168.1.100")]
    [InlineData(0x0A000001u, "10.0.0.1")]
    [InlineData(0u, "")]
    public void An_address_reads_the_usual_way_round(uint ip, string expected)
    {
        // By name: the driver is an empty library where MVS was not there
        // to build it against, and naming it here would not compile.
        if (Hikrobot()?.GetType().GetMethod("Address", BindingFlags.NonPublic | BindingFlags.Static) is not { } address)
            return;

        Assert.Equal(expected, address.Invoke(null, [ip]));
    }

    private static bool MvsInstalled() =>
        Environment.GetEnvironmentVariable("MVCAM_COMMON_RUNENV") is { Length: > 0 } root && System.IO.Directory.Exists(root);
}

/// <summary>One camera, one test at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HikrobotCollection
{
    public const string Name = "Hikrobot cameras";
}
