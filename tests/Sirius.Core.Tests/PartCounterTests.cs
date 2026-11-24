using System.IO;
using Sirius.Builder;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Core.Screens;
using Sirius.Plc;
using Sirius.Plc.Workflows;
using Sirius.UI.Screens;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// A counter of a controller's parts (F25): chosen in Builder's designer
/// as a counter's station is, and counting on the running screen.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class PartCounterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-parts-" + Guid.NewGuid().ToString("N"));

    public PartCounterTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
        PlcNodeLibrary.Register();
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

    private Project LineWithController()
    {
        var project = Project.Create(_folder, "Line", new VisionGraph(), "Top");
        project.AddController("Line 1 PLC", "simulated", "Line 1", path => WorkflowFile.Save(StarterWorkflow.For("Top"), path));
        return project;
    }

    // ---- a controller on the line (F23c) --------------------------------------------------

    [Fact]
    public void A_controller_widget_is_chosen_from_the_projects_controllers()
    {
        var project = LineWithController();

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();
            var designer = new ScreenDesignerViewModel(new StationRecipes(project));
            var widget = designer.Add(WidgetKind.Controller);

            // Controllers only; none chosen yet is a problem.
            Assert.Equal(["Line 1 PLC"], widget.StationOptions.Select(option => option.Label));
            Assert.Equal("no controller chosen", widget.Caption);
            Assert.Contains("Controller: no controller is named.", designer.ProblemText, StringComparison.Ordinal);

            widget.StationValue = ScreenDesignerViewModel.ControllerPrefix + "Line 1 PLC";
            Assert.Equal(("Line 1 PLC", (string?)null), (widget.Widget.Controller, widget.Widget.Station));
            Assert.Equal("Line 1 PLC", widget.Caption);
            Assert.DoesNotContain("Controller:", designer.ProblemText, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A lost PLC on the running screen: its Controller widget red, and an
    /// alarm across the top once it has been away past the grace that
    /// connecting at the start needs; both clear when it is back.
    /// </summary>
    [Fact]
    public void A_lost_plc_turns_its_widget_red_and_raises_the_alarm_until_it_is_back()
    {
        var project = LineWithController();

        WpfTestHost.OnStaThread(() =>
        {
            var screen = new Screen
            {
                Widgets =
                [
                    new Widget { Kind = WidgetKind.Controller, Controller = "Line 1 PLC", Width = 320, Height = 64 },
                    new Widget { Kind = WidgetKind.Controller, Controller = "Nowhere", Width = 320, Height = 64 },
                ],
            };

            using var runtime = new ProjectRuntime(project);
            using var view = new ScreenViewModel(screen, runtime, project.Folder);
            var controllers = ProjectControllers.Open(project, null);

            try
            {
                view.UseControllers(controllers.Runners);
                var widget = view.Widgets.OfType<ControllerWidgetViewModel>().First();
                var runner = controllers.Runners.Single();

                Assert.Contains("Controller widget: there is no controller 'Nowhere' running.", view.Problems);
                Assert.Equal((ControllerLook.Stopped, "Not running"), (widget.Look, widget.State));
                Assert.Null(controllers.Alarm(DateTime.UtcNow));

                controllers.StartAll();
                Until(() => runner.IsConnected);
                view.ShowControllers();
                Assert.Equal((ControllerLook.Connected, "Connected"), (widget.Look, widget.State));
                Assert.Null(controllers.Alarm(DateTime.UtcNow));

                // Lost: red at once; the alarm only past the grace.
                ((SimulatedPlc)runner.Driver).Unplug();
                Until(() => !runner.IsConnected);
                view.ShowControllers();
                Assert.Equal((ControllerLook.NotConnected, "Not connected"), (widget.Look, widget.State));

                var lost = DateTime.UtcNow;
                Assert.Null(controllers.Alarm(lost));

                var alarm = controllers.Alarm(lost + ProjectControllers.AlarmAfter);
                Assert.NotNull(alarm);
                Assert.StartsWith("Line 1 PLC is not connected", alarm, StringComparison.Ordinal);
                Assert.EndsWith("It starts no inspections until it is back.", alarm, StringComparison.Ordinal);

                // Back: both clear.
                ((SimulatedPlc)runner.Driver).PlugIn();
                Until(() => runner.IsConnected);
                view.ShowControllers();
                Assert.Equal(ControllerLook.Connected, widget.Look);
                Assert.Null(controllers.Alarm(lost + TimeSpan.FromMinutes(1)));
            }
            finally
            {
                controllers.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    /// <summary>The PLC window on the line: each running controller's, behind an engineer's login.</summary>
    [Fact]
    public void The_plc_window_opens_on_the_line_for_an_engineer()
    {
        var project = LineWithController();
        project.AddUser("Ann", UserLevel.Engineer, "1234");

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var window = RuntimeWindow.Create(project, fullScreen: false);
            var opened = new List<Sirius.UI.Plc.PlcViewModel>();
            window.OpenPlcWindow = (_, model) =>
            {
                opened.Add(model);
                return new System.Windows.Window();
            };

            try
            {
                Assert.Equal(System.Windows.Visibility.Visible, ((System.Windows.FrameworkElement)window.FindName("PlcButton")).Visibility);

                // No one logged in: asked who, and nothing opened when that is cancelled.
                var asked = window.OpenPlcWindowsAsync();
                Assert.NotNull(window.Screen.Login);
                window.Screen.Login!.Cancel();
                Assert.Equal(0, WpfTestHost.Wait(asked));
                Assert.Empty(opened);

                // The engineer logged in: the controller's window, on its running workflow.
                Assert.Null(window.Screen.Session.TryLogIn("Ann", "1234"));
                Assert.Equal(1, WpfTestHost.Wait(window.OpenPlcWindowsAsync()));
                Assert.Equal("Line 1 PLC", Assert.Single(opened).Controller.Name);
                Assert.True(opened[0].HasRunner);
            }
            finally
            {
                window.Runtime.Dispose();
            }
        });
    }

    private static void Until(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "Waited too long.");
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void A_counter_counts_a_controllers_parts_when_one_is_chosen_in_the_designer()
    {
        var project = LineWithController();

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();
            var designer = new ScreenDesignerViewModel(new StationRecipes(project));
            var counter = designer.Add(WidgetKind.Counter);

            // Each station's pictures, then each controller's parts.
            Assert.Equal(
                ["Top", "Line 1 PLC: parts"],
                counter.StationOptions.Select(option => option.Label));

            counter.StationValue = ScreenDesignerViewModel.ControllerPrefix + "Line 1 PLC";

            Assert.Equal("Line 1 PLC", counter.Widget.Controller);
            Assert.Null(counter.Widget.Station);
            Assert.Equal("Line 1 PLC: parts", counter.Caption);
            Assert.Equal(ScreenDesignerViewModel.ControllerPrefix + "Line 1 PLC", counter.StationValue);

            // No station is no problem for it.
            Assert.DoesNotContain(
                ScreenLayout.Problems(designer.ToScreen(), [new StationRecipe("Top", new VisionGraph())]),
                problem => problem.StartsWith("Counter", StringComparison.Ordinal));

            // Undone: the station's pictures again.
            designer.UndoCommand.Execute(null);
            Assert.Null(designer.Widgets.Last().Widget.Controller);
            Assert.Equal("Top", designer.Widgets.Last().Widget.Station);

            designer.RedoCommand.Execute(null);
            Assert.Equal("Line 1 PLC", designer.Widgets.Last().Widget.Controller);

            // A station again: the controller goes.
            designer.Widgets.Last().StationValue = "Top";
            Assert.Null(designer.Widgets.Last().Widget.Controller);
        });
    }

    [Fact]
    public void On_the_running_screen_a_counter_shows_the_parts_and_reset_all_starts_them_again()
    {
        var project = LineWithController();

        var countsPath = PartCountsFile.PathFor(project.WorkflowPath(project.Controllers[0]));
        Assert.Null(PartCountsFile.Save(countsPath, new PartCounts(10, 7, 1, 1, 1), 11));

        WpfTestHost.OnStaThread(() =>
        {
            var screen = new Screen
            {
                Widgets =
                [
                    new Widget { Kind = WidgetKind.Counter, Controller = "Line 1 PLC", Width = 300, Height = 60 },
                    new Widget { Kind = WidgetKind.Counter, Controller = "Nowhere", Width = 300, Height = 60 },
                    new Widget { Kind = WidgetKind.Counter, Station = "Top", Width = 300, Height = 60 },
                    new Widget { Kind = WidgetKind.Button, Action = WidgetAction.ResetCounts, Width = 140, Height = 36 },
                ],
            };

            using var runtime = new ProjectRuntime(project);
            using var view = new ScreenViewModel(screen, runtime, project.Folder);

            var controllers = ProjectControllers.Open(project, null);

            try
            {
                view.UseControllers(controllers.Runners);

                var counters = view.Widgets.OfType<CounterWidgetViewModel>().ToArray();
                var parts = counters[0];

                Assert.Equal("10", parts.Total);
                Assert.Equal("7", parts.Passed);
                Assert.Equal("3", parts.Failed);
                Assert.Equal("Reject", parts.FailLabel);
                Assert.Equal("70.0 %", parts.Yield.Replace(',', '.'));
                Assert.Equal("Late 1 · not inspected 1", parts.Timing);

                // A controller not running is said, once.
                Assert.Contains("Counter: there is no controller 'Nowhere' running.", view.Problems);

                // A station's counter is as it was.
                Assert.Equal("Fail", counters[2].FailLabel);

                // Reset Counts for every station: the parts too, kept.
                var reset = view.Widgets.OfType<ButtonWidgetViewModel>().Single();
                WpfTestHost.Wait(reset.Command.ExecuteAsync(null));

                Assert.Equal("0", parts.Total);
                Assert.Equal(PartCounts.Zero, PartCountsFile.Load(countsPath).Counts);
                Assert.Equal(11, PartCountsFile.Load(countsPath).NextPart);
            }
            finally
            {
                controllers.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void A_renamed_controller_takes_the_counters_of_its_parts_with_it()
    {
        var project = LineWithController();

        var screen = new Screen
        {
            Widgets = [new Widget { Kind = WidgetKind.Counter, Controller = "Line 1 PLC", Width = 300, Height = 60 }],
        };

        screen.Save(ScreenSet.PathFor(project, Screen.StartName));

        var changed = ScreenSet.RenameController(project, "Line 1 PLC", "Packer");

        Assert.Equal([Screen.StartName], changed);
        Assert.Equal("Packer", Screen.Load(ScreenSet.PathFor(project, Screen.StartName)).Widgets.Single().Controller);
    }
}
