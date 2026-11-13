using System.IO;
using System.Windows;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Builder;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Plc;
using Sirius.Plc.Nodes;
using Sirius.Plc.Workflows;
using Sirius.Studio.App.Screens;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Controllers in a project and in Builder (backlog F23b): the project
/// file, the Controllers panel, the PLC window, the workflow editor - and a
/// simulated PLC's bit, set by hand, having a station inspect in a test run.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class BuilderControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sirius-controllers-" + Guid.NewGuid().ToString("N"));

    public BuilderControllerTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
        PlcNodeLibrary.Register();
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a run over.
        }
    }

    /// <summary>A name no other test shares - simulated PLC memories are kept by name for the whole run.</summary>
    private static string Unique(string stem) => $"{stem} {Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>
    /// A project whose station Top inspects a folder of dark pictures - each
    /// passing, published "Brightness" - and keeps them all.
    /// </summary>
    private Project StationProject()
    {
        var images = Path.Combine(_root, "source");
        Directory.CreateDirectory(images);

        foreach (var (name, value) in new[] { ("1.png", 50), ("2.png", 60) })
        {
            using var mat = new Mat(30, 40, DepthType.Cv8U, 1);
            mat.SetTo(new MCvScalar(value));
            CvInvoke.Imwrite(Path.Combine(images, name), mat);
        }

        var recipe = new VisionGraph();
        var sequence = new ImageSequenceNode { SourcePath = images };
        var stats = new RegionStatsNode();
        var check = new LimitCheckNode { Minimum = 0, Maximum = 128 };

        foreach (var node in new VisionNode[] { sequence, stats, check })
            recipe.Add(node);

        recipe.TryConnect(sequence.ImageOut, stats.ImageIn);
        recipe.TryConnect(stats.MeanOut, check.Inputs.Single(port => port.Name == nameof(LimitCheckNode.Value)));
        Publishing.Publish(recipe, stats, PublishedKind.Result, "Mean", "Brightness");

        var project = Project.Create(_root, "Line", recipe, "Top");
        project.SetSaving(project.Stations[0], new ImageSaving(ImageSavePolicy.All));
        return project;
    }

    private static BuilderViewModel Builder(Project project)
    {
        var model = new BuilderViewModel
        {
            RememberProjects = false,
            Ask = (_, _) => MessageBoxResult.No,
            OpenPlcWindow = (_, _) => null,
        };

        model.Show(project);
        return model;
    }

    // ---- the project file ------------------------------------------------------------

    [Fact]
    public void Controllers_are_kept_in_the_project_file_with_their_connection_and_workflow()
    {
        var project = Project.Create(_root, "Line 1", new VisionGraph(), "Top");
        var written = new List<string>();

        var plc = project.AddController("Line 1 PLC", "simulated", "Line 1", path =>
        {
            written.Add(path);
            File.WriteAllText(path, "{}");
        });

        Assert.Equal(Path.Combine(project.Folder, "controllers", "Line 1 PLC.sflow"), Assert.Single(written));
        Assert.Equal("controllers/Line 1 PLC.sflow", plc.Workflow);

        Assert.True(project.SetConnection(plc, "siemens-s7", "192.168.0.10", new Dictionary<string, string> { ["rack"] = "0", ["slot"] = "1" }));
        Assert.False(project.SetConnection(plc, "siemens-s7", "192.168.0.10", new Dictionary<string, string> { ["rack"] = "0", ["slot"] = "1" }));
        project.RenameController(plc, "Press PLC");

        Assert.Equal("There is already a controller called 'press plc'.", project.ControllerNameProblem("press plc"));
        Assert.Equal("PLC 2", project.SuggestControllerName());

        var text = File.ReadAllText(project.FilePath);
        Assert.Contains("\"controllers\"", text);

        var loaded = Project.Load(project.FilePath);
        Assert.Empty(loaded.Warnings);

        var read = Assert.Single(loaded.Project.Controllers);
        Assert.Equal(("Press PLC", "siemens-s7", "192.168.0.10", "controllers/Line 1 PLC.sflow"), (read.Name, read.Driver, read.Address, read.Workflow));
        Assert.Equal("1", read.Options["slot"]);

        // Removed, its workflow stays; a project with none writes none.
        loaded.Project.RemoveController(read);
        Assert.True(File.Exists(Path.Combine(project.Folder, "controllers", "Line 1 PLC.sflow")));
        Assert.DoesNotContain("\"controllers\"", File.ReadAllText(project.FilePath));
    }

    [Fact]
    public void A_controller_whose_workflow_is_missing_is_said_on_opening()
    {
        var project = Project.Create(_root, "Line 2", new VisionGraph(), "Top");
        var plc = project.AddController("PLC 1", "simulated", "x", path => File.WriteAllText(path, "{}"));
        File.Delete(project.WorkflowPath(plc));

        var loaded = Project.Load(project.FilePath);
        Assert.Equal("Controller 'PLC 1': its workflow controllers/PLC 1.sflow is missing.", Assert.Single(loaded.Warnings));

        // Run anyway, it is left out and says why; a driver not installed likewise.
        project.AddController("PLC 2", "no-such-driver", "y", path => WorkflowFile.Save(StarterWorkflow.For("Top"), path));
        var controllers = ProjectControllers.Open(project, null);

        Assert.Empty(controllers.Runners);
        Assert.Contains(controllers.Problems, problem => problem.StartsWith("PLC 1 does not run:", StringComparison.Ordinal));
        Assert.Contains("PLC 2 does not run: No driver called 'no-such-driver' is installed.", controllers.Problems);
    }

    [Fact]
    public async Task Connections_to_a_named_simulated_plc_share_its_memory()
    {
        var name = Unique("Shared");
        var workflow = new SimulatedPlc(SimulatedPlcMemory.Named(name));
        var window = (SimulatedPlc)PlcDrivers.Create(new PlcConnection(SimulatedPlcDriver.Key, name));

        await workflow.ConnectAsync(default);
        await window.ConnectAsync(default);
        await window.WriteAsync(new PlcVariable("MD20", PlcType.Int32), 123456, default);

        Assert.Equal(123456, await workflow.ReadAsync(new PlcVariable("MD20", PlcType.Int32), default));

        // One disconnecting does not disconnect the other; the cable out, both drop.
        await window.DisconnectAsync();
        Assert.True(workflow.IsConnected);

        workflow.Memory.Unplug();
        Assert.False(workflow.IsConnected);
        workflow.Memory.PlugIn();
    }

    // ---- Builder ------------------------------------------------------------------------

    [Fact]
    public void A_controller_added_in_builder_starts_with_a_workflow_that_inspects_the_first_station()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var project = Project.Create(_root, "Line 4", new VisionGraph(), "Top");
            var model = Builder(project);

            Assert.True(model.HasNoControllers);

            var added = model.AddController("Line 4 PLC")!;
            var listed = Assert.Single(model.Controllers);

            Assert.False(model.HasNoControllers);
            Assert.Same(added, listed.Controller);
            Assert.Equal("Simulated PLC · Line 4 PLC", listed.ConnectionText);
            Assert.False(listed.WorkflowMissing);

            // The starter: Trigger at M0.0, a part number, Top inspecting, the result written back.
            var workflow = WorkflowFile.Load(project.WorkflowPath(added)).Graph;
            Assert.Equal(
                ["Trigger", "Part Number", "Part", "Inspect", "Result", "Done", "Error"],
                workflow.Nodes.Select(node => node.Name));
            Assert.Equal("Top", workflow.Nodes.OfType<InspectNode>().Single().Station);
            Assert.Empty(new WorkflowRunner(workflow, new SimulatedPlc(), new FakeStations("Top")).Check());

            // Renamed and removed through the project; the list follows.
            project.RenameController(added, "Press");
            Assert.Equal("Press", Assert.Single(model.Controllers).Name);
        });
    }

    [Fact]
    public void The_project_panel_lists_stations_then_controllers_with_one_selection()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var project = Project.Create(_root, "Line 8", new VisionGraph(), "Top");
            var model = Builder(project);
            var studio = new List<string>();
            model.Launch = (_, file) =>
            {
                studio.Add(file);
                return true;
            };

            Assert.Equal(["Top"], model.Items.Select(Name));

            // A controller added goes after the stations, and is selected.
            model.AddController("PLC 1");
            project.AddStation("Bottom", new VisionGraph());

            Assert.Equal(["Top", "Bottom", "PLC 1"], model.Items.Select(Name));
            Assert.IsType<ControllerViewModel>(model.SelectedItem);
            Assert.Null(model.SelectedStation);
            Assert.Equal("PLC 1", model.SelectedController?.Name);

            // A station chosen: the Station menu acts on it, the Controller menu on nothing.
            model.SelectedItem = model.Items[0];
            Assert.Equal("Top", model.SelectedStation?.Name);
            Assert.Null(model.SelectedController);

            // Opened by what it is: a station in Studio, a controller here.
            model.OpenSelectedCommand.Execute(null);
            Assert.Equal(project.RecipePath(project.Stations[0]), Assert.Single(studio));

            model.Open(model.Items[2]);
            Assert.Single(model.Workflows);
            Assert.Single(studio);

            // Renaming keeps what was selected.
            model.SelectedItem = model.Items[2];
            project.RenameController(project.Controllers[0], "Press");
            Assert.Equal("Press", model.SelectedController?.Name);

            // The panel's templates, icons and all, made for real.
            var window = new BuilderWindow(model) { KeepsLayout = false };

            try
            {
                var list = (System.Windows.Controls.ListBox)window.FindName("ProjectList");
                list.Measure(new Size(240, 600));
                list.Arrange(new Rect(0, 0, 240, 600));
                WpfTestHost.Settle(window.Dispatcher);

                var icons = new List<System.Windows.Media.ImageSource>();
                foreach (var item in model.Items)
                {
                    var container = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(item);
                    icons.Add(FindImage(container)!.Source);
                }

                var resources = Application.Current!;
                Assert.Equal(
                    [resources.FindResource("Icon.ph_camera"), resources.FindResource("Icon.ph_camera"), resources.FindResource("Icon.ph_controller")],
                    icons);
            }
            finally
            {
                window.Close();
            }

            static string Name(object item) => item switch
            {
                StationViewModel station => station.Name,
                ControllerViewModel controller => controller.Name,
                _ => "?",
            };
        });
    }

    private static System.Windows.Controls.Image? FindImage(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (child is System.Windows.Controls.Image image)
                return image;

            if (FindImage(child) is { } found)
                return found;
        }

        return null;
    }

    [Fact]
    public void A_workflow_opens_in_a_tab_is_edited_through_its_settings_and_saved()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var project = Project.Create(_root, "Line 5", new VisionGraph(), "Top");
            project.AddStation("Bottom", new VisionGraph());
            var model = Builder(project);
            model.AddController("PLC 1");

            var shown = new List<WorkflowDocumentViewModel>();
            model.WorkflowShown += (_, workflow) => shown.Add(workflow);

            var document = model.OpenWorkflow(model.Controllers[0])!;
            Assert.Same(document, Assert.Single(model.Workflows));
            Assert.Same(document, model.OpenWorkflow(model.Controllers[0]));
            Assert.Equal(2, shown.Count);
            Assert.Equal("PLC 1", document.Title);
            Assert.Equal("Ready to run.", document.ProblemText);

            // The palette: the PLC's nodes, then the stations'.
            Assert.Equal(["PLC", "Stations"], document.Palette.Select(group => group.Name));
            Assert.Contains(document.Palette[0].Items, item => item.DisplayName == "PLC Monitor");
            Assert.Contains(document.Palette[1].Items, item => item.DisplayName == "Inspect");

            // The Inspect node's settings: its station chosen from the project's.
            var inspect = document.Editor.Nodes.Single(node => node.Node is InspectNode);
            document.Editor.SelectedNode = inspect;

            var station = document.Properties.SelectMany(category => category.Rows).Single(row => row.Name == "Station");
            Assert.Equal(PropertyEditor.Choice, station.Editor);
            Assert.Equal(["Top", "Bottom"], station.Options!.Cast<PropertyOption>().Select(option => option.Label));

            station.Value = "Bottom";
            Assert.Equal("Bottom", ((InspectNode)inspect.Node).Station);
            Assert.True(document.IsModified);
            Assert.Equal("PLC 1 *", document.Title);

            // An address cleared: the check says so at once.
            var read = document.Editor.Nodes.Single(node => node.Node is PlcReadNode);
            document.Editor.SelectedNode = read;
            var address = document.Properties.SelectMany(category => category.Rows).Single(row => row.Name == "Address");
            address.Value = string.Empty;
            Assert.True(document.HasProblems);
            Assert.Equal("Part Number: No address is given.", document.ProblemText);

            // A type shows the name a PLC programmer knows.
            var type = document.Properties.SelectMany(category => category.Rows).Single(row => row.Name == "Type");
            Assert.Contains("Int32 (DINT)", type.Options!.Cast<PropertyOption>().Select(option => option.Label));

            // Undone, the address comes back; saved, the file has the station changed.
            document.UndoCommand.Execute(null);
            Assert.Equal("MW10", ((PlcReadNode)read.Node).Address);

            // A node added from the palette starts on the first station.
            var result = document.AddNode(document.Palette[1].Items.Single(item => item.DisplayName == "Station Result"), new Point(100, 400));
            Assert.Equal("Top", ((StationResultNode)result.Node).Station);

            Assert.True(document.Save());
            Assert.False(document.IsModified);

            var saved = WorkflowFile.Load(project.WorkflowPath(project.Controllers[0])).Graph;
            Assert.Equal("Bottom", saved.Nodes.OfType<InspectNode>().Single().Station);
            Assert.Single(saved.Nodes.OfType<StationResultNode>());

            // Closing with changes asks; No closes without saving.
            ((PlcReadNode)read.Node).Address = "MW12";
            Assert.True(model.CloseWorkflow(document));
            Assert.Empty(model.Workflows);
            Assert.Equal("MW10", WorkflowFile.Load(project.WorkflowPath(project.Controllers[0])).Graph.Nodes.OfType<PlcReadNode>().Single().Address);
        });
    }

    [Fact]
    public void The_builder_window_shows_a_workflow_in_a_tab_with_its_canvas()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var project = Project.Create(_root, "Line 6", new VisionGraph(), "Top");
            var model = Builder(project);
            var window = new BuilderWindow(model) { KeepsLayout = false };

            try
            {
                model.AddController("PLC 1");
                var document = model.OpenWorkflow(model.Controllers[0])!;
                var tab = window.ShowWorkflow(document);

                Assert.Equal("PLC 1", tab.Title);
                var view = Assert.IsType<WorkflowEditorView>(tab.Content);

                // Laid out for real, so every template on the canvas is made.
                view.Measure(new Size(1400, 800));
                view.Arrange(new Rect(0, 0, 1400, 800));
                WpfTestHost.Settle(window.Dispatcher);

                document.Editor.Nodes[0].Node.Name = "Ready";
                Assert.Equal("PLC 1 *", tab.Title);

                // Closed with the project, the tab goes too.
                Assert.True(model.CloseProject());
                Assert.Null(tab.Parent);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void The_plc_window_tests_the_connection_reads_and_writes_and_sets_the_simulator()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var project = Project.Create(_root, "Line 7", new VisionGraph(), "Top");
            var name = Unique("PLC");
            var model = Builder(project);
            var controller = model.AddController(name)!;
            var memory = SimulatedPlcMemory.Named(name);

            var plc = new PlcViewModel(project, controller);
            var window = new PlcWindow(plc);

            try
            {
                Assert.True(plc.IsSimulated);
                Assert.Equal("Simulated PLC", plc.Drivers.Single(driver => (string)driver.Value! == plc.DriverKey).Label);

                WpfTestHost.Wait(plc.TestConnectionAsync());
                Assert.True(plc.IsConnected);
                Assert.StartsWith($"Connected to simulated PLC '{name}'", plc.ConnectionStatus);

                // A DINT, a bit, an INT - read as each.
                memory.Set("MD20", 123456);
                plc.Address = "MD20";
                plc.Type = PlcViewModel.Types.Single(type => type.Type == PlcType.Int32);
                WpfTestHost.Wait(plc.ReadAsync());
                Assert.Equal("123456", plc.ReadResult);

                plc.Address = "M0.0";
                plc.Type = PlcViewModel.Types.Single(type => type.Type == PlcType.Bool);
                WpfTestHost.Wait(plc.ReadAsync());
                Assert.Equal("false", plc.ReadResult);

                plc.Address = "MW10";
                plc.Type = PlcViewModel.Types.Single(type => type.Type == PlcType.Int16);
                plc.Value = "42";
                WpfTestHost.Wait(plc.WriteAsync());
                Assert.Equal((short)42, memory.Get("MW10", PlcType.Int16));
                Assert.Contains(plc.Lines, line => line.EndsWith("Wrote MW10 (Int16): 42", StringComparison.Ordinal));

                // A value an INT cannot hold is refused, in words.
                plc.Value = "70000";
                WpfTestHost.Wait(plc.WriteAsync());
                Assert.Contains(plc.Lines, line => line.Contains("Could not write MW10 (Int16): '70000' is not a value a Int16 can hold.", StringComparison.Ordinal));

                // Set by hand, and a bit flipped: the memory list follows.
                plc.Address = "M0.0";
                plc.Type = PlcViewModel.Types.Single(type => type.Type == PlcType.Bool);
                plc.Value = "true";
                plc.SetInMemory();
                WpfTestHost.PumpUntil(() => plc.Memory.Any(row => row.Address == "M0.0" && row.Value == "true"));

                var bit = plc.Memory.Single(row => row.Address == "M0.0");
                Assert.True(bit.IsBit);
                plc.Toggle(bit);
                Assert.False((bool)memory.Get("M0.0"));

                // Unplugged, nothing answers.
                plc.PlugCommand.Execute(null);
                WpfTestHost.Wait(plc.TestConnectionAsync());
                Assert.False(plc.IsConnected);
                Assert.Equal($"Could not connect: {name} does not answer.", plc.ConnectionStatus);
                plc.PlugCommand.Execute(null);

                // The connection changed here is the project's.
                plc.PlcAddress = "Line 7b";
                Assert.Equal("Line 7b", project.Controllers[0].Address);
                Assert.Equal("Line 7b", Project.Load(project.FilePath).Project.Controllers[0].Address);
            }
            finally
            {
                window.Close();
                WpfTestHost.Wait(plc.DisposeAsync().AsTask());
            }
        });
    }

    // ---- the MVP: a bit set by hand has a station inspect ---------------------------------

    [Fact]
    public void In_a_test_run_a_bit_set_in_the_simulator_has_the_station_inspect_and_the_answer_comes_back()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var project = StationProject();
            var name = Unique("Line PLC");
            var model = Builder(project);
            model.AddController(name);

            var memory = SimulatedPlcMemory.Named(name);
            memory.Set("MW10", (short)42);

            var running = RuntimeWindow.Unshown(project);

            try
            {
                Assert.Empty(running.Controllers.Problems);
                running.StartControllers();

                // Builder's simulator windows, beside the screen, with the workflow's log.
                var opened = new List<PlcViewModel>();
                model.OpenPlcWindow = (_, plc) =>
                {
                    opened.Add(plc);
                    return null;
                };
                model.OpenSimulators(running);
                var simulator = Assert.Single(opened);
                Assert.True(simulator.HasRunner);
                Assert.False(simulator.CanChangeConnection);

                var runner = Assert.Single(running.Controllers.Runners);
                var reads = memory.Reads;
                WpfTestHost.PumpUntil(() => runner.IsConnected && memory.Reads > reads + 1);

                // The operator's click: M0.0 on.
                memory.Set("M0.0", true);

                var station = running.Runtime.Stations[0];
                WpfTestHost.PumpUntil(() => memory.Writes.Count(write => write.Address == "M0.2") == 2, timeoutSeconds: 20);

                // The station inspected, told the part number.
                var snapshot = station.Last!;
                Assert.Equal(42L, snapshot.Part!["PartNumber"]);
                Assert.True(snapshot.Verdict.Passed);

                // The answer: Result on (passed), Done pulsed; no Error.
                Assert.Equal(
                    [("M0.1", (object)true), ("M0.2", true), ("M0.2", false)],
                    memory.Writes.Select(write => (write.Address, write.Value)));
                Assert.Equal(1, runner.Passes);

                WpfTestHost.PumpUntil(() => simulator.WorkflowLog.Any(line => line.Contains("Pass 1 finished", StringComparison.Ordinal)));
                Assert.Contains(simulator.WorkflowLog, line => line.Contains($"Top inspection 1: PASS (PartNumber 42)", StringComparison.Ordinal));
                WpfTestHost.Wait(simulator.DisposeAsync().AsTask());
            }
            finally
            {
                WpfTestHost.Wait(running.Controllers.StopAllAsync());
                running.Close();
            }
        });
    }

    /// <summary>Stations by name only, for a check.</summary>
    private sealed class FakeStations(params string[] names) : IStationBus
    {
        public IReadOnlyList<string> Stations => names;

        public event EventHandler<InspectionSnapshot>? Inspected
        {
            add { }
            remove { }
        }

        public Task<InspectionSnapshot?> InspectAsync(string station, PartValues? part, CancellationToken cancellationToken) =>
            Task.FromResult<InspectionSnapshot?>(null);
    }
}
