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
/// <para>
/// The driver is found the way the application finds it - by loading
/// whatever Sirius.Cameras.*.dll sits beside the program. On a PC without
/// MVS there is no Hikrobot driver, and without a virtual camera there is
/// nothing to grab from: either way these pass having checked only what
/// they can.
/// </para>
/// <para>
/// Virtual cameras only, as the Basler tests use only pylon's emulator: a
/// real camera on the bench may be open in the application, or wired to a
/// trigger, and a test run is no business grabbing from it.
/// </para>
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

    /// <summary>The virtual cameras MVS lists - their serial numbers start "Vir".</summary>
    private static IEnumerable<CameraInfo> Virtual(ICameraDriver driver) =>
        driver.Enumerate().Where(camera => camera.Serial.StartsWith("Vir", StringComparison.OrdinalIgnoreCase));

    private static CameraInfo? Any(ICameraDriver driver) => Virtual(driver).FirstOrDefault();

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
        foreach (var info in Virtual(driver))
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

    /// <summary>
    /// Closed, a camera closes no further and grabs no more: at exit a
    /// grab can still be waiting its turn as the camera closes, and a call
    /// on MVS's handle once it is gone is a crash, not an error.
    /// </summary>
    [Fact]
    public void A_closed_camera_closes_once_and_grabs_no_more()
    {
        if (Hikrobot() is not { } driver || Any(driver) is not { } info)
            return;

        var camera = CameraDrivers.Acquire(info.Label)!;
        CameraDrivers.Release(camera);

        camera.Dispose();
        Assert.Throws<ObjectDisposedException>(() => camera.Grab(TimeSpan.FromMilliseconds(300), CancellationToken.None));
        Assert.Throws<ObjectDisposedException>(() => camera.Exposure = 1234);
    }

    /// <summary>
    /// Two stations, each with its own Hikrobot camera, running at once -
    /// the runtime MVP's two-camera case (runtime design, section 0, step
    /// 3). Needs two virtual cameras in MVS; passes having checked nothing
    /// with fewer.
    /// </summary>
    [Fact]
    public void Two_stations_run_two_hikrobot_cameras_at_once()
    {
        if (Hikrobot() is not { } driver)
            return;

        var cameras = Virtual(driver).Take(2).ToArray();

        if (cameras.Length < 2)
            return;

        Sirius.Core.Runtime.StationRunner Station(CameraInfo info)
        {
            var graph = new VisionGraph();
            var node = new CameraNode { Device = info.Label, Trigger = CameraTrigger.Software };
            graph.Add(node);

            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                return new Sirius.Core.Runtime.StationRunner(info.Serial, graph);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        using var first = Station(cameras[0]);
        using var second = Station(cameras[1]);

        try
        {
            first.Start();
            second.Start();

            var clock = Stopwatch.StartNew();

            while ((first.Counts.Total < 20 || second.Counts.Total < 20) && clock.Elapsed < TimeSpan.FromSeconds(30))
                Thread.Sleep(20);

            Task.WhenAll(first.StopAsync(), second.StopAsync()).GetAwaiter().GetResult();

            Assert.True(first.Counts.Total >= 20, $"{cameras[0].Label}: {first.Counts.Total} frames, {first.LastError}");
            Assert.True(second.Counts.Total >= 20, $"{cameras[1].Label}: {second.Counts.Total} frames, {second.LastError}");
            Assert.Equal(0, first.Counts.WithErrors);
            Assert.Equal(0, second.Counts.WithErrors);
        }
        finally
        {
            foreach (var info in cameras)
            {
                if (CameraDrivers.Acquire(info.Label) is { } camera)
                    CameraDrivers.Release(camera);
            }
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
