using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Core.Shapes;
using Sirius.Vision;
using Sirius.Vision.Cameras;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The station runner: acquire, inspect, publish, with no screen (runtime
/// MVP step 3).
/// </summary>
public sealed class StationRunnerTests : IDisposable
{
    private static readonly string Simulated = $"Simulated test pattern [{SimulatedCameraDriver.SerialNumber}]";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-station-" + Guid.NewGuid().ToString("N"));

    public StationRunnerTests()
    {
        TestCatalog.Ensure();
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a run over.
        }
    }

    /// <summary>Made with no synchronisation context, so the passes run where the test is.</summary>
    private static StationRunner Runner(string name, VisionGraph recipe, IEnumerable<string>? problems = null)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return new StationRunner(name, recipe, problems);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private void WriteImage(string name, byte value)
    {
        using var mat = new Mat(300, 400, DepthType.Cv8U, 1);
        mat.SetTo(new MCvScalar(value));
        CvInvoke.Imwrite(Path.Combine(_folder, name), mat);
    }

    /// <summary>
    /// A folder of a dark, a bright and a dark image; Region Stats' mean,
    /// published as "Brightness"; a Limit Check passing up to 128; a viewer
    /// published as "Main View"; the check's Maximum published as "Limit".
    /// </summary>
    private VisionGraph FolderRecipe()
    {
        WriteImage("1.png", 50);
        WriteImage("2.png", 200);
        WriteImage("3.png", 60);

        var graph = new VisionGraph();
        var images = new ImageSequenceNode { SourcePath = _folder };
        var stats = new RegionStatsNode();
        var check = new LimitCheckNode { Minimum = 0, Maximum = 128 };
        var viewer = new ViewerNode();

        foreach (var node in new VisionNode[] { images, stats, check, viewer })
            graph.Add(node);

        graph.TryConnect(images.ImageOut, stats.ImageIn);
        graph.TryConnect(images.ImageOut, viewer.ImageIn);
        graph.TryConnect(stats.MeanOut, check.Inputs.Single(port => port.Name == nameof(LimitCheckNode.Value)));
        graph.TryConnect(stats.ShapesOut, viewer.ShapesIn);

        Publishing.Publish(graph, stats, PublishedKind.Result, "Mean", "Brightness");
        Publishing.Publish(graph, check, PublishedKind.Setting, nameof(LimitCheckNode.Maximum), "Limit");
        Publishing.Publish(graph, viewer, PublishedKind.View, string.Empty, "Main View");

        return graph;
    }

    private static VisionGraph CameraRecipe(string device)
    {
        var graph = new VisionGraph();
        var camera = new CameraNode { Device = device };
        graph.Add(camera);
        Publishing.Publish(graph, camera, PublishedKind.Result, "Out", "Frame");
        return graph;
    }

    [Fact]
    public void A_trigger_inspects_once_and_publishes()
    {
        using var station = Runner("Top", FolderRecipe());

        var snapshot = station.TriggerAsync().GetAwaiter().GetResult();

        Assert.NotNull(snapshot);
        Assert.Equal("Top", snapshot!.Station);
        Assert.Equal(1, snapshot.Serial);
        Assert.True(snapshot.Verdict.Passed);
        Assert.Equal(50, Assert.IsType<double>(snapshot["brightness"]), 1);
        Assert.Equal(128d, snapshot["Limit"]);

        var view = Assert.IsType<ViewFrame>(snapshot["Main View"]);
        Assert.IsType<ImageData>(view.Image);
        Assert.IsType<ShapeSet>(view.Overlays.First());

        Assert.Equal(StationState.Ready, station.State);
        Assert.Equal(new StationCounts(1, 1, 0, 0, 0), station.Counts);
        Assert.Same(snapshot, station.Last);
    }

    /// <summary>A folder stands in for a camera: each trigger the next image, round again after the last.</summary>
    [Fact]
    public void A_folder_steps_on_each_trigger_and_wraps()
    {
        using var station = Runner("Top", FolderRecipe());

        var means = new List<double>();

        for (var i = 0; i < 4; i++)
            means.Add((double)station.TriggerAsync().GetAwaiter().GetResult()!["Brightness"]!);

        Assert.Equal([50, 200, 60, 50], means.Select(mean => Math.Round(mean)).ToArray());
        Assert.Equal(new StationCounts(4, 3, 1, 0, 0), station.Counts);
        Assert.Equal(75, station.Counts.Yield);
    }

    [Fact]
    public void Counts_reset_to_zero()
    {
        using var station = Runner("Top", FolderRecipe());

        station.TriggerAsync().GetAwaiter().GetResult();
        station.ResetCounts();

        Assert.Equal(StationCounts.Zero, station.Counts);
    }

    /// <summary>
    /// The screen draws a station's pictures in the handler, which the
    /// station waits for: the picture is still alive there.
    /// </summary>
    [Fact]
    public void The_handler_sees_each_inspection_with_its_picture_alive()
    {
        using var station = Runner("Top", FolderRecipe());
        var seen = new List<double>();

        station.Inspected = snapshot =>
        {
            var image = (ImageData)((ViewFrame)snapshot["Main View"]!).Image!;
            seen.Add(CvInvoke.Mean(image.Mat).V0);
            return Task.CompletedTask;
        };

        station.TriggerAsync().GetAwaiter().GetResult();
        station.TriggerAsync().GetAwaiter().GetResult();

        Assert.Equal([50, 200], seen.Select(mean => Math.Round(mean)).ToArray());
    }

    [Fact]
    public void Running_inspects_until_stopped()
    {
        using var station = Runner("Camera", CameraRecipe(Simulated));

        Assert.True(station.Start());
        Assert.Equal(StationState.Running, station.State);

        // Busy: neither a trigger nor a second start.
        Assert.Null(station.TriggerAsync().GetAwaiter().GetResult());
        Assert.False(station.Start());

        Wait(() => station.Counts.Total >= 5, TimeSpan.FromSeconds(10));

        station.StopAsync().GetAwaiter().GetResult();

        Assert.Equal(StationState.Ready, station.State);
        var total = station.Counts.Total;
        Thread.Sleep(100);
        Assert.Equal(total, station.Counts.Total);
        Assert.IsType<ImageData>(station.Last!["Frame"]);
    }

    /// <summary>A recipe that judges nothing is counted, but neither passed nor failed.</summary>
    [Fact]
    public void A_recipe_judging_nothing_counts_as_unjudged()
    {
        using var station = Runner("Camera", CameraRecipe(Simulated));

        station.TriggerAsync().GetAwaiter().GetResult();

        Assert.Equal(new StationCounts(1, 0, 0, 1, 0), station.Counts);
        Assert.Null(station.Last!.Verdict.Passed);
    }

    /// <summary>A camera that is not there fails the inspection, says why, and is counted.</summary>
    [Fact]
    public void A_missing_camera_is_an_error_not_a_fault()
    {
        using var station = Runner("Camera", CameraRecipe("Basler acA1920-40gm [00000000]"));

        var snapshot = station.TriggerAsync().GetAwaiter().GetResult();

        Assert.Equal(1, snapshot!.NodeErrors);
        Assert.Contains("was not found", station.LastError, StringComparison.Ordinal);
        Assert.Equal(1, station.Counts.WithErrors);
        Assert.Equal(StationState.Ready, station.State);
    }

    /// <summary>A published name whose output has gone is reported, and the station still runs.</summary>
    [Fact]
    public void A_missing_published_output_is_reported()
    {
        var recipe = FolderRecipe();
        var stats = recipe.Nodes.OfType<RegionStatsNode>().Single();
        stats.Published = [.. stats.Published, new PublishedName(PublishedKind.Result, "Gone", "Old")];

        using var station = Runner("Top", recipe);

        Assert.Contains(station.Problems, problem => problem.Contains("'Old'", StringComparison.Ordinal));
        Assert.NotNull(station.TriggerAsync().GetAwaiter().GetResult());
    }

    /// <summary>A station whose recipe will not open is kept, faulted, saying why.</summary>
    [Fact]
    public void A_recipe_that_will_not_open_faults_the_station()
    {
        var project = Project.Create(_folder, "Line");
        File.WriteAllText(project.RecipePath(project.Stations[0]), "not json");

        using var station = StationRunner.Open(project, project.Stations[0]);

        Assert.Equal(StationState.Faulted, station.State);
        Assert.Contains("could not be opened", station.Problem, StringComparison.Ordinal);
        Assert.Null(station.TriggerAsync().GetAwaiter().GetResult());
        Assert.False(station.Start());
    }

    // ---- several stations ----------------------------------------------

    [Fact]
    public void A_project_runtime_holds_a_station_each_and_reads_by_name()
    {
        var project = Project.Create(_folder, "Line", FolderRecipe(), "Top");
        project.AddStation("Empty", new VisionGraph());

        WithoutContext(() =>
        {
            using var runtime = new ProjectRuntime(project);

            Assert.Equal(["Top", "Empty"], runtime.Stations.Select(station => station.Name).ToArray());

            var inspected = runtime.TriggerAllAsync().GetAwaiter().GetResult();

            Assert.Equal(2, inspected.Count);
            Assert.Equal(50, Assert.IsType<double>(runtime.Read("top", "Brightness")), 1);
            Assert.Null(runtime.Read("Top", "No Such Name"));
            Assert.Null(runtime.Read("No Such Station", "Brightness"));
        });
    }

    /// <summary>
    /// Two stations, two cameras, grabbing at the same time - the thing the
    /// MVP has to prove before screens are built on it (runtime design,
    /// section 0, step 3).
    /// </summary>
    [Fact]
    public void Two_stations_grab_from_two_cameras_at_once()
    {
        CameraDrivers.Register(TwinDriver.Instance);
        TwinDriver.ResetOverlap();

        using var left = Runner("Left", CameraRecipe(TwinDriver.Left.Label));
        using var right = Runner("Right", CameraRecipe(TwinDriver.Right.Label));

        left.Start();
        right.Start();

        Wait(() => left.Counts.Total >= 10 && right.Counts.Total >= 10, TimeSpan.FromSeconds(20));

        Task.WhenAll(left.StopAsync(), right.StopAsync()).GetAwaiter().GetResult();

        Assert.Equal(0, left.Counts.WithErrors);
        Assert.Equal(0, right.Counts.WithErrors);
        Assert.True(TwinDriver.MostAtOnce >= 2, $"the two cameras never grabbed at the same time (most at once: {TwinDriver.MostAtOnce})");

        // Each station got its own camera's frames.
        Assert.Equal(1, Mean(left.Last!["Frame"]), 1);
        Assert.Equal(2, Mean(right.Last!["Frame"]), 1);
    }

    private static double Mean(object? image) => CvInvoke.Mean(((ImageData)image!).Mat).V0;

    private static void Wait(Func<bool> condition, TimeSpan limit)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (!condition())
        {
            Assert.True(clock.Elapsed < limit, "timed out waiting");
            Thread.Sleep(10);
        }
    }

    private static void WithoutContext(Action action)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>
    /// Two cameras, each taking 15 ms to grab a frame filled with its own
    /// number, counting how many grabs are in flight at once.
    /// </summary>
    private sealed class TwinDriver : ICameraDriver
    {
        public static readonly TwinDriver Instance = new();

        public static readonly CameraInfo Left = new("Twin", "TWIN-1", "test", string.Empty);
        public static readonly CameraInfo Right = new("Twin", "TWIN-2", "test", string.Empty);

        private static int _inFlight;
        private static int _mostAtOnce;

        public static int MostAtOnce => Volatile.Read(ref _mostAtOnce);

        public static void ResetOverlap() => Interlocked.Exchange(ref _mostAtOnce, 0);

        public string Name => "Twin";

        public IReadOnlyList<CameraInfo> Enumerate() => [Left, Right];

        public ICamera Open(CameraInfo camera) => new TwinCamera(camera, (byte)(camera.Serial == Left.Serial ? 1 : 2));

        private sealed class TwinCamera(CameraInfo info, byte value) : ICamera
        {
            public CameraInfo Info { get; } = info;

            public double Exposure { get; set; } = 10_000;

            public double Gain { get; set; }

            public CameraTrigger Trigger { get; set; } = CameraTrigger.Software;

            public ImageData Grab(TimeSpan timeout, CancellationToken cancellationToken)
            {
                var now = Interlocked.Increment(ref _inFlight);

                for (var most = Volatile.Read(ref _mostAtOnce); now > most; most = Volatile.Read(ref _mostAtOnce))
                    Interlocked.CompareExchange(ref _mostAtOnce, now, most);

                try
                {
                    Thread.Sleep(15);

                    var mat = new Mat(48, 64, DepthType.Cv8U, 1);
                    mat.SetTo(new MCvScalar(value));
                    return ImageData.Adopt(mat);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }

            public void Dispose()
            {
            }
        }
    }
}
