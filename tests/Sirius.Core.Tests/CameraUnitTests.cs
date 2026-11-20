using System.IO;
using System.Windows;
using Sirius.Builder;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Core.Serialization;
using Sirius.Vision;
using Sirius.Vision.Cameras;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Cameras as a unit of the project (backlog F29): the project file, a
/// station's recipe taking the camera's settings, "in use by another
/// program", and Builder's camera window.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class CameraUnitTests : IDisposable
{
    private static readonly string Simulated = $"Simulated test pattern [{SimulatedCameraDriver.SerialNumber}]";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sirius-cameras-" + Guid.NewGuid().ToString("N"));

    public CameraUnitTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        CameraDrivers.CloseAll();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a run over.
        }
    }

    /// <summary>A recipe whose Camera node takes from <paramref name="device"/> at 5000 µs, software-triggered.</summary>
    private static VisionGraph CameraRecipe(string device = "", string? name = null)
    {
        var graph = new VisionGraph();
        var camera = new CameraNode { Device = device, Exposure = 5000, Trigger = CameraTrigger.Software };

        if (name is not null)
            camera.Name = name;

        graph.Add(camera);
        return graph;
    }

    private static CameraNode TheCamera(VisionGraph recipe) => recipe.Nodes.OfType<CameraNode>().Single();

    // ---- the project file ----------------------------------------------------------------

    [Fact]
    public void Cameras_are_kept_in_the_project_file_and_stations_name_theirs()
    {
        var project = Project.Create(_root, "Line", CameraRecipe(), "Top");

        // None: written as before, with no cameras at all.
        Assert.DoesNotContain("\"cameras\"", File.ReadAllText(project.FilePath), StringComparison.Ordinal);

        var camera = project.AddCamera("Top camera", new CameraSetup(Simulated, Exposure: 8000, Trigger: "Hardware"));
        Assert.True(project.SetStationCamera(project.Stations[0], camera));
        Assert.False(project.SetStationCamera(project.Stations[0], camera));

        var loaded = Project.Load(project.FilePath);
        Assert.Empty(loaded.Warnings);

        var again = Assert.Single(loaded.Project.Cameras);
        Assert.Equal("Top camera", again.Name);
        Assert.Equal(new CameraSetup(Simulated, Exposure: 8000, Trigger: "Hardware"), again.Setup);
        Assert.Equal("Top camera", loaded.Project.Stations[0].Camera);
        Assert.Same(again, loaded.Project.CameraOf(loaded.Project.Stations[0]));

        // Each setting not set is the recipe's: not written.
        var text = File.ReadAllText(project.FilePath);
        Assert.DoesNotContain("\"gain\"", text, StringComparison.Ordinal);
        Assert.Contains("\"camera\": \"Top camera\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_camera_renamed_is_followed_and_one_removed_leaves_its_stations_on_their_recipes()
    {
        var project = Project.Create(_root, "Line", CameraRecipe(), "Top");
        project.AddStation("Bottom", CameraRecipe());

        var camera = project.AddCamera("Camera 1");
        project.SetStationCamera(project.Stations[0], camera);
        project.SetStationCamera(project.Stations[1], camera);

        Assert.Equal("Camera 2", project.SuggestCameraName());
        Assert.NotNull(project.CameraNameProblem("camera 1"));
        Assert.Null(project.CameraNameProblem("CAMERA 1", camera));

        project.RenameCamera(camera, "Overhead");
        Assert.All(project.Stations, station => Assert.Equal("Overhead", station.Camera));
        Assert.Equal(2, project.StationsUsing(camera).Count);

        project.RemoveCamera(camera);
        Assert.Empty(project.Cameras);
        Assert.All(project.Stations, station => Assert.Null(station.Camera));

        // Settings out of range are refused.
        Assert.Throws<ArgumentException>(() => project.AddCamera("Bad", new CameraSetup(Simulated, Exposure: 5)));
        Assert.Throws<ArgumentException>(() => project.AddCamera("Bad", new CameraSetup(Simulated, Trigger: "Sometimes")));
    }

    [Fact]
    public void A_station_naming_a_camera_the_project_lacks_is_warned_of_and_uses_its_recipe()
    {
        var project = Project.Create(_root, "Line", CameraRecipe(), "Top");
        var text = File.ReadAllText(project.FilePath).Replace("\"recipe\":", "\"camera\": \"Nowhere\",\n      \"recipe\":", StringComparison.Ordinal);
        File.WriteAllText(project.FilePath, text);

        var loaded = Project.Load(project.FilePath);

        Assert.Contains(loaded.Warnings, warning => warning.Contains("Nowhere", StringComparison.Ordinal));
        Assert.Null(loaded.Project.Stations[0].Camera);
    }

    // ---- into the recipe ------------------------------------------------------------------

    [Fact]
    public void A_camera_goes_into_the_recipes_camera_node_and_leaves_what_it_does_not_set()
    {
        var recipe = CameraRecipe("Basler old [1111]");
        var camera = new CameraUnit("Top camera", new CameraSetup(Simulated, Gain: 6));

        Assert.Null(StationCamera.Apply(recipe, camera));

        var node = TheCamera(recipe);
        Assert.Equal(Simulated, node.Device);
        Assert.Equal(6, node.Gain);
        Assert.Equal(5000, node.Exposure);                  // as the recipe
        Assert.Equal(CameraTrigger.Software, node.Trigger);

        Assert.Equal(new CameraSetup(Simulated, 5000, 6, "Software", 2000), StationCamera.From(recipe));

        // A trigger by name becomes the node's own.
        StationCamera.Apply(recipe, new CameraUnit("Top camera", new CameraSetup(Simulated, Trigger: "Hardware", Timeout: 9000)));
        Assert.Equal(CameraTrigger.Hardware, node.Trigger);
        Assert.Equal(9000, node.Timeout);
    }

    [Fact]
    public void A_recipe_with_no_camera_node_or_several_says_which_it_needs()
    {
        var camera = new CameraUnit("Top camera", new CameraSetup(Simulated));

        Assert.Contains("no Camera node", StationCamera.Apply(new VisionGraph(), camera), StringComparison.Ordinal);

        var two = CameraRecipe(name: "Left");
        two.Add(new CameraNode { Name = "Top camera" });

        Assert.Null(StationCamera.Apply(two, camera));
        Assert.Equal(Simulated, two.Nodes.OfType<CameraNode>().Single(node => node.Name == "Top camera").Device);
        Assert.Equal(string.Empty, two.Nodes.OfType<CameraNode>().Single(node => node.Name == "Left").Device);

        var unnamed = CameraRecipe(name: "Left");
        unnamed.Add(new CameraNode { Name = "Right" });
        Assert.Contains("2 Camera nodes", StationCamera.Apply(unnamed, camera), StringComparison.Ordinal);
        Assert.Null(StationCamera.From(unnamed));

        Assert.Contains("no camera chosen", StationCamera.Apply(CameraRecipe(), new CameraUnit("Empty")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_station_runs_with_its_projects_camera_and_the_recipe_file_is_left_alone()
    {
        var project = Project.Create(_root, "Line", CameraRecipe("Basler gone [0000]"), "Top");
        var camera = project.AddCamera("Top camera", new CameraSetup(Simulated, Exposure: 7000));
        project.SetStationCamera(project.Stations[0], camera);

        using var runner = StationRunner.Open(project, project.Stations[0]);

        Assert.Empty(runner.Problems);
        Assert.Equal(Simulated, TheCamera(runner.Recipe).Device);
        Assert.Equal(7000, TheCamera(runner.Recipe).Exposure);

        var inspected = runner.TriggerAsync().GetAwaiter().GetResult();
        Assert.NotNull(inspected);
        Assert.Null(runner.LastError);

        // What runs is the project's camera; the file still names its own.
        Assert.Equal("Basler gone [0000]", TheCamera(GraphSerializer.Load(project.RecipePath(project.Stations[0])).Graph).Device);
    }

    // ---- in use by another program -------------------------------------------------------

    private sealed class BusyDriver : ICameraDriver
    {
        public const string Serial = "BUSY-0001";

        public string Name => "Busy";

        public IReadOnlyList<CameraInfo> Enumerate() => [new CameraInfo("Busy", Serial, "held elsewhere", "192.168.0.9")];

        public ICamera Open(CameraInfo camera) => throw new InvalidOperationException("Device access denied (0x80000203).");
    }

    [Fact]
    public void A_camera_there_but_held_by_another_program_says_so()
    {
        CameraDrivers.Register(new BusyDriver());

        var problem = Assert.Throws<CameraInUseException>(() => CameraDrivers.Acquire($"Busy held elsewhere [{BusyDriver.Serial}]"));

        Assert.Contains("another program has it", problem.Message, StringComparison.Ordinal);
        Assert.Contains("Sirius Edge", problem.Message, StringComparison.Ordinal);
        Assert.Contains("access denied", problem.Message, StringComparison.Ordinal);
        Assert.Equal(BusyDriver.Serial, problem.Camera.Serial);
    }

    /// <summary>
    /// A camera just let go of by another program can take a moment to be
    /// free: closed in Builder and opened in Studio, it was said to be in use,
    /// and opened fine a few seconds later. It is tried again meanwhile.
    /// </summary>
    [Fact]
    public void A_camera_just_let_go_of_elsewhere_is_tried_again_before_it_is_said_to_be_in_use()
    {
        var driver = new LossyCameraDriver("LOSSY-OPEN");
        driver.RefuseOpens = 2;
        CameraDrivers.Register(driver);

        var camera = CameraDrivers.Acquire(driver.Device);

        try
        {
            Assert.NotNull(camera);
            Assert.Equal(3, driver.Opens);
        }
        finally
        {
            CameraDrivers.Release(camera!);
        }
    }

    /// <summary>
    /// A camera asked for while the drivers are still being looked for -
    /// a recipe opened as Studio starts, from Builder's Open in Studio -
    /// waits for them. It was "not found", and found when run again.
    /// </summary>
    [Fact]
    public void A_camera_asked_for_while_drivers_are_looked_for_waits_for_them()
    {
        var driver = new LossyCameraDriver("LOSSY-SEARCH");
        var search = new TaskCompletionSource();
        CameraDrivers.SearchUnderWay(search.Task);

        var asked = Task.Run(() => CameraDrivers.Acquire(driver.Device));

        try
        {
            Assert.False(asked.Wait(TimeSpan.FromMilliseconds(300)));
            Assert.True(CameraDrivers.IsSearching);

            // The driver joins as the search ends.
            CameraDrivers.Register(driver);
            search.SetResult();

            Assert.True(asked.Wait(TimeSpan.FromSeconds(10)));
            Assert.NotNull(asked.Result);
            Assert.False(CameraDrivers.IsSearching);
        }
        finally
        {
            search.TrySetResult();

            if (asked.Wait(TimeSpan.FromSeconds(10)) && asked.Result is { } camera)
                CameraDrivers.Release(camera);
        }
    }

    /// <summary>
    /// A frame lost on the network - "The buffer was incompletely grabbed",
    /// after the driver has tried again - is said, and the camera is kept:
    /// it was let go of, and Live stopped, at the first.
    /// </summary>
    [Fact]
    public void The_camera_window_keeps_the_camera_over_a_frame_lost_on_the_network()
    {
        var driver = new LossyCameraDriver("LOSSY-WINDOW");
        CameraDrivers.Register(driver);

        var project = Project.Create(_root, "Line", CameraRecipe(), "Top");
        var unit = project.AddCamera("Top camera", new CameraSetup(driver.Device));

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            using var model = new CameraViewModel(project, unit);

            driver.LoseFrames = 1;
            WpfTestHost.Wait(model.GrabAsync());
            Assert.Contains("came incomplete", model.Problem, StringComparison.Ordinal);
            Assert.Contains("network card", model.Problem, StringComparison.Ordinal);
            Assert.Null(model.Picture);

            WpfTestHost.Wait(model.GrabAsync());
            Assert.Null(model.Problem);
            Assert.NotNull(model.Picture);

            // Opened once: kept over the lost frame, not let go and opened again.
            Assert.Equal(1, driver.Opens);

            // Live goes past a lost frame, and on.
            driver.LoseFrames = 2;
            model.LiveCommand.Execute(null);
            WpfTestHost.PumpUntil(() => driver.LoseFrames == 0 && model.Problem is null);
            Assert.True(model.IsLive);
            model.StopLive();
            Assert.Equal(1, driver.Opens);
        });
    }

    // ---- Builder ---------------------------------------------------------------------------

    private BuilderViewModel Builder(Project project, MessageBoxResult answer = MessageBoxResult.No)
    {
        var model = new BuilderViewModel
        {
            RememberProjects = false,
            Ask = (_, _) => answer,
            OpenPlcWindow = (_, _) => null,
        };

        model.Show(project);
        return model;
    }

    private string RecipeFile(string device)
    {
        var path = Path.Combine(_root, $"recipe-{Guid.NewGuid():N}.sgraph");
        GraphSerializer.Save(CameraRecipe(device), path);
        return path;
    }

    [Fact]
    public void A_station_added_from_a_recipe_is_offered_a_camera_made_from_it()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var project = Project.Create(_root, "Line", firstRecipe: null, firstStation: null);
            var model = Builder(project, MessageBoxResult.Yes);

            var top = model.AddFromRecipe("Top", RecipeFile(Simulated))!;

            var camera = Assert.Single(project.Cameras);
            Assert.Equal("Top camera", camera.Name);
            Assert.Equal(new CameraSetup(Simulated), camera.Setup);     // every setting as the recipe
            Assert.Equal("Top camera", top.Camera);

            // A second station on the same camera is simply given it.
            var again = model.AddFromRecipe("Top 2", RecipeFile(Simulated))!;
            Assert.Single(project.Cameras);
            Assert.Equal("Top camera", again.Camera);

            // Listed after the stations, before the screens.
            Assert.Equal(["Top", "Top 2", "Top camera", "Main"], model.Items.Select(item => item.ToString()));
            Assert.Equal("camera: Top camera", model.Stations[0].CameraText);
            Assert.Equal("for Top, Top 2", model.Cameras[0].UsedByText);
            Assert.Equal("settings as the recipe", model.Cameras[0].SettingsText);

            // Answered no, nothing is made.
            var no = Builder(Project.Create(_root, "Other", firstRecipe: null, firstStation: null));
            no.AddFromRecipe("Top", RecipeFile(Simulated));
            Assert.Empty(no.Project!.Cameras);
        });
    }

    [Fact]
    public void A_stations_camera_is_chosen_from_its_menu_and_one_its_recipe_cannot_take_is_said()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var project = Project.Create(_root, "Line", CameraRecipe(), "Top");
            project.AddStation("Plain", new VisionGraph());
            var model = Builder(project);
            var camera = model.AddCamera("Overhead", new CameraSetup(Simulated))!;

            var window = new BuilderWindow(model) { KeepsLayout = false };

            try
            {
                var items = window.StationCameraItems(model.Stations[0]).OfType<System.Windows.Controls.MenuItem>().ToArray();
                Assert.Equal(["_The Recipe's Own", "Overhead", "_Add Camera…"], items.Select(item => (string)item.Header));
                Assert.True(items[0].IsChecked);

                items[1].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                Assert.Equal("Overhead", project.Stations[0].Camera);
                Assert.Equal("Top takes its pictures from Overhead.", model.StatusText);

                items = window.StationCameraItems(model.Stations[0]).OfType<System.Windows.Controls.MenuItem>().ToArray();
                Assert.True(items[1].IsChecked);

                // A recipe with no Camera node: chosen, but said.
                model.SetStationCamera(model.Stations[1], camera);
                Assert.Contains("but its recipe has no Camera node", model.StatusText, StringComparison.Ordinal);

                // Back to the recipe's own.
                model.SetStationCamera(model.Stations[0], null);
                Assert.Null(project.Stations[0].Camera);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void The_camera_window_finds_uses_sets_grabs_and_saves()
    {
        var project = Project.Create(_root, "Line", CameraRecipe(), "Top");
        var unit = project.AddCamera("Top camera");
        project.SetStationCamera(project.Stations[0], unit);

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            using var model = new CameraViewModel(project, unit);

            // As the recipe: Top's.
            Assert.Equal("Top", model.RecipeStation);
            Assert.True(model.ExposureAsRecipe);
            Assert.Equal(5000, model.EffectiveExposure);
            Assert.StartsWith("As the recipe (Software)", model.TriggerChoices[0].Label, StringComparison.Ordinal);
            Assert.False(model.GrabCommand.CanExecute(null));

            WpfTestHost.Wait(model.FindAsync());
            var simulated = Assert.Single(model.Found, camera => camera.Serial == SimulatedCameraDriver.SerialNumber);

            model.Use(simulated);
            Assert.Equal(Simulated, unit.Setup.Device);
            Assert.True(model.IsChosen(simulated));

            // Typed: the camera's own, in the project at once.
            model.ExposureText = "12000";
            Assert.Equal(12000, unit.Setup.Exposure);
            Assert.False(model.ExposureAsRecipe);
            Assert.Equal(12000, Project.Load(project.FilePath).Project.Cameras[0].Setup.Exposure);

            model.ExposureText = "lots";
            Assert.Contains("not a number", model.Problem, StringComparison.Ordinal);
            Assert.Equal(12000, unit.Setup.Exposure);

            model.ExposureText = "1";
            Assert.Contains("between 10", model.Problem, StringComparison.Ordinal);

            model.ExposureAsRecipe = true;
            Assert.Null(unit.Setup.Exposure);

            model.TriggerValue = "FreeRun";
            Assert.Equal("FreeRun", unit.Setup.Trigger);
            model.TriggerValue = string.Empty;
            Assert.Null(unit.Setup.Trigger);

            // Grabbed through it, and saved as it came.
            WpfTestHost.Wait(model.GrabAsync());
            Assert.Null(model.Problem);
            Assert.NotNull(model.Picture);
            Assert.Equal("640 × 480", model.FrameText);

            var saved = model.SaveImage(Path.Combine(_root, "samples", "frame.png"));
            Assert.NotNull(saved);
            using var read = Emgu.CV.CvInvoke.Imread(saved!, Emgu.CV.CvEnum.ImreadModes.Unchanged);
            Assert.Equal(640, read.Width);
        });
    }

    [Fact]
    public void The_camera_window_is_made_and_Builder_opens_it_for_a_camera()
    {
        var project = Project.Create(_root, "Line", CameraRecipe(), "Top");

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var model = Builder(project);
            var opened = new List<CameraViewModel>();
            model.OpenCameraWindow = (_, camera) =>
            {
                // The window itself, made as Builder makes it.
                var window = new CameraWindow(camera);
                window.Close();
                opened.Add(camera);
            };

            model.AddCamera("Overhead");
            model.Open(model.Cameras[0]);

            Assert.Equal("Overhead", Assert.Single(opened).Camera.Name);
            Assert.Contains("no camera chosen", model.StatusText, StringComparison.Ordinal);
        });
    }
}
