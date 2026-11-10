using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Core.Screens;
using Sirius.Studio.App.Screens;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Screens: the layout file, the standard screen made from what stations
/// publish, and a screen running against them (runtime MVP step 4).
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ScreenTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-screen-" + Guid.NewGuid().ToString("N"));

    public ScreenTests(WpfTestHost host)
    {
        _ = host;
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

    /// <summary>
    /// A folder of a dark and a bright image; Region Stats' mean published
    /// as "Brightness"; a check passing up to 128, its Maximum published as
    /// "Limit"; a viewer published as "Main View".
    /// </summary>
    private VisionGraph Recipe(string images = "images")
    {
        var folder = Path.Combine(_folder, images);
        Directory.CreateDirectory(folder);

        foreach (var (name, value) in new[] { ("1.png", 50), ("2.png", 200) })
        {
            using var mat = new Mat(300, 400, DepthType.Cv8U, 1);
            mat.SetTo(new MCvScalar(value));
            CvInvoke.Imwrite(Path.Combine(folder, name), mat);
        }

        var graph = new VisionGraph();
        var source = new ImageSequenceNode { SourcePath = folder };
        var stats = new RegionStatsNode();
        var check = new LimitCheckNode { Minimum = 0, Maximum = 128 };
        var viewer = new ViewerNode();

        foreach (var node in new VisionNode[] { source, stats, check, viewer })
            graph.Add(node);

        graph.TryConnect(source.ImageOut, stats.ImageIn);
        graph.TryConnect(source.ImageOut, viewer.ImageIn);
        graph.TryConnect(stats.MeanOut, check.Inputs.Single(port => port.Name == nameof(LimitCheckNode.Value)));
        graph.TryConnect(stats.ShapesOut, viewer.ShapesIn);

        Publishing.Publish(graph, stats, PublishedKind.Result, "Mean", "Brightness");
        Publishing.Publish(graph, check, PublishedKind.Setting, nameof(LimitCheckNode.Maximum), "Limit");
        Publishing.Publish(graph, viewer, PublishedKind.View, string.Empty, "Main View");

        return graph;
    }

    private static StationRunner Runner(string name, VisionGraph recipe) => new(name, recipe);

    // ---- the file -----------------------------------------------------

    [Fact]
    public void A_screen_survives_saving_and_opening()
    {
        var screen = new Screen
        {
            Name = "Main",
            Widgets =
            {
                new Widget { Kind = WidgetKind.Value, X = 10, Y = 20, Width = 200, Height = 30, Station = "Top", Name = "Diameter", Format = "0.00", Unit = "mm" },
                new Widget { Kind = WidgetKind.Button, Action = WidgetAction.RunStop },
            },
        };

        var path = Path.Combine(_folder, "Main.screen");
        screen.Save(path);
        var loaded = Screen.Load(path);

        Assert.Equal(2, loaded.Widgets.Count);
        Assert.Equal("Diameter", loaded.Widgets[0].Name);
        Assert.Equal("mm", loaded.Widgets[0].Unit);
        Assert.Equal(WidgetAction.RunStop, loaded.Widgets[1].Action);
        Assert.Contains("\"RunStop\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void A_newer_screen_format_is_refused()
    {
        var path = Path.Combine(_folder, "Main.screen");
        File.WriteAllText(path, """{ "version": 99, "widgets": [] }""");

        Assert.Throws<InvalidDataException>(() => Screen.Load(path));
    }

    // ---- the standard screen -------------------------------------------

    [Fact]
    public void The_standard_screen_has_a_column_per_station()
    {
        using var top = Runner("Top", Recipe());
        using var bottom = Runner("Bottom", Recipe("other"));

        var screen = ScreenLayout.Standard("Line 3", [top, bottom]);

        Assert.True(screen.Generated);
        Assert.Contains(screen.Widgets, w => w.Kind == WidgetKind.Text && w.Text == "Line 3");

        foreach (var station in new[] { "Top", "Bottom" })
        {
            var mine = screen.Widgets.Where(w => w.Station == station).ToArray();

            Assert.Equal("Main View", Assert.Single(mine, w => w.Kind == WidgetKind.Image).Name);
            Assert.Single(mine, w => w.Kind == WidgetKind.Verdict);
            Assert.Single(mine, w => w.Kind == WidgetKind.Counter);
            Assert.Equal("Brightness", Assert.Single(mine, w => w.Kind == WidgetKind.Value).Name);
            Assert.Equal("Limit", Assert.Single(mine, w => w.Kind == WidgetKind.Setting).Name);
            Assert.Equal(
                [WidgetAction.Trigger, WidgetAction.RunStop],
                mine.Where(w => w.Kind == WidgetKind.Button).Select(w => w.Action!.Value).ToArray());
        }

        // Buttons for every station, along the top.
        Assert.Equal(
            [WidgetAction.Trigger, WidgetAction.RunStop, WidgetAction.ResetCounts],
            screen.Widgets.Where(w => w.Kind == WidgetKind.Button && w.Station is null).Select(w => w.Action!.Value).ToArray());

        // Side by side, inside the canvas.
        var left = screen.Widgets.Single(w => w.Station == "Top" && w.Kind == WidgetKind.Image);
        var right = screen.Widgets.Single(w => w.Station == "Bottom" && w.Kind == WidgetKind.Image);
        Assert.True(left.X + left.Width < right.X);
        Assert.All(screen.Widgets, w => Assert.True(w.X + w.Width <= screen.Width && w.Y + w.Height <= screen.Height, $"{w.Kind} is off the screen"));

        Assert.Empty(ScreenLayout.Problems(screen, [top, bottom]));
    }

    [Fact]
    public void A_station_publishing_nothing_still_gets_its_verdict_and_buttons()
    {
        using var bare = Runner("Bare", new VisionGraph());

        var mine = ScreenLayout.Standard("Line", [bare]).Widgets.Where(w => w.Station == "Bare").ToArray();

        Assert.DoesNotContain(mine, w => w.Kind == WidgetKind.Image);
        Assert.Single(mine, w => w.Kind == WidgetKind.Verdict);
        Assert.Equal(2, mine.Count(w => w.Kind == WidgetKind.Button));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_standard_screen_sits_on_the_designers_grid(int stations)
    {
        var recipe = Recipe();
        var screen = ScreenLayout.Standard("Line", Enumerable.Range(1, stations).Select(i => new StationRecipe($"Camera {i}", recipe)).ToArray());

        Assert.All(screen.Widgets, widget =>
        {
            foreach (var value in new[] { widget.X, widget.Y, widget.Width, widget.Height })
                Assert.Equal(0, value % ScreenLayout.Grid);

            Assert.True(widget.X + widget.Width <= screen.Width && widget.Y + widget.Height <= screen.Height);
        });
    }

    [Fact]
    public void A_project_with_no_stations_says_so()
    {
        var screen = ScreenLayout.Standard("Empty", Array.Empty<StationRecipe>());

        Assert.Contains(screen.Widgets, w => w.Text?.Contains("no stations", StringComparison.Ordinal) == true);
    }

    // ---- which screen is shown -------------------------------------------

    [Fact]
    public void A_generated_screen_is_saved_and_made_again_next_time()
    {
        var project = Project.Create(_folder, "Line", Recipe(), "Top");

        using (var first = StationRunner.Open(project, project.Stations[0]))
        {
            ScreenLayout.ScreenFor(project, [first], out var problem);
            Assert.Null(problem);
            Assert.True(File.Exists(ScreenLayout.MainScreenPath(project)));
        }

        project.AddStation("Bottom", new VisionGraph());

        using var top = StationRunner.Open(project, project.Stations[0]);
        using var bottom = StationRunner.Open(project, project.Stations[1]);

        var again = ScreenLayout.ScreenFor(project, [top, bottom], out _);

        Assert.Contains(again.Widgets, w => w.Station == "Bottom");
    }

    /// <summary>One edited by hand is the SI's: kept as it is.</summary>
    [Fact]
    public void A_screen_edited_by_hand_is_kept()
    {
        var project = Project.Create(_folder, "Line", Recipe(), "Top");
        var mine = new Screen { Generated = false, Widgets = { new Widget { Kind = WidgetKind.Text, Text = "Mine" } } };
        mine.Save(ScreenLayout.MainScreenPath(project));

        using var top = StationRunner.Open(project, project.Stations[0]);
        var shown = ScreenLayout.ScreenFor(project, [top], out _);

        Assert.Equal("Mine", Assert.Single(shown.Widgets).Text);
    }

    [Fact]
    public void An_unreadable_screen_is_reported_and_a_standard_one_shown()
    {
        var project = Project.Create(_folder, "Line", Recipe(), "Top");
        Directory.CreateDirectory(Path.GetDirectoryName(ScreenLayout.MainScreenPath(project))!);
        File.WriteAllText(ScreenLayout.MainScreenPath(project), "not json");

        using var top = StationRunner.Open(project, project.Stations[0]);
        var shown = ScreenLayout.ScreenFor(project, [top], out var problem);

        Assert.NotNull(problem);
        Assert.True(shown.Generated);
        Assert.Equal("not json", File.ReadAllText(ScreenLayout.MainScreenPath(project)));
    }

    // ---- checking a screen against the stations ---------------------------

    [Fact]
    public void Bindings_that_cannot_be_found_are_reported()
    {
        using var top = Runner("Top", Recipe());

        var screen = new Screen
        {
            Widgets =
            {
                new Widget { Kind = WidgetKind.Value, Station = "Nowhere", Name = "Brightness" },
                new Widget { Kind = WidgetKind.Value, Station = "Top", Name = "Diameter" },
                new Widget { Kind = WidgetKind.Setting, Station = "Top", Name = "Brightness" },
                new Widget { Kind = WidgetKind.Image, Station = "Top", Name = "Limit" },
                new Widget { Kind = WidgetKind.Verdict },
                new Widget { Kind = WidgetKind.Value, Station = "top", Name = "brightness" },
                new Widget { Kind = WidgetKind.Button, Action = WidgetAction.Trigger },
            },
        };

        var problems = ScreenLayout.Problems(screen, [top]);

        Assert.Equal(5, problems.Count);
        Assert.Contains(problems, p => p.Contains("no station 'Nowhere'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("publishes no 'Diameter'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("not a setting", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("not a picture", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("no station is named", StringComparison.Ordinal));
    }

    // ---- a screen running --------------------------------------------------

    [Theory]
    [InlineData(12.34567, null, null, "12.346")]
    [InlineData(12.34567, "0.0", "mm", "12.3 mm")]
    [InlineData(7, null, null, "7")]
    [InlineData(true, null, null, "Yes")]
    [InlineData("LOT 4471", null, null, "LOT 4471")]
    [InlineData(null, null, "mm", "—")]
    public void Values_are_written_as_a_screen_shows_them(object? value, string? format, string? unit, string expected) =>
        Assert.Equal(expected, ValueWidgetViewModel.Format(value, format, unit));

    [Fact]
    public void A_running_screen_follows_its_stations()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var project = Project.Create(_folder, "Line", Recipe(), "Top");
            using var runtime = new ProjectRuntime(project);

            var screen = ScreenLayout.Standard(project.Name, runtime.Stations);
            using var view = new ScreenViewModel(screen, runtime, project.Folder);

            Assert.False(view.HasProblems, view.ProblemText);

            var trigger = view.Widgets.OfType<ButtonWidgetViewModel>().First(b => b.Station == "Top" && b.Widget.Action == WidgetAction.Trigger);
            var image = view.Widgets.OfType<ImageWidgetViewModel>().Single();
            var verdict = view.Widgets.OfType<VerdictWidgetViewModel>().Single();
            var counter = view.Widgets.OfType<CounterWidgetViewModel>().Single();
            var brightness = view.Widgets.OfType<ValueWidgetViewModel>().Single(v => v.Widget.Name == "Brightness");
            var limit = view.Widgets.OfType<SettingWidgetViewModel>().Single(v => v.Widget.Name == "Limit");

            Assert.Equal("—", verdict.Text);
            Assert.Null(image.Picture);

            WpfTestHost.Wait(trigger.Command.ExecuteAsync(null));

            Assert.Equal("PASS", verdict.Text);
            Assert.Equal(VerdictLook.Pass, verdict.Look);
            Assert.Equal("50", brightness.Value);
            Assert.Equal("128", limit.ValueText);
            Assert.True(limit.IsNumberEditor);
            Assert.NotNull(image.Picture);
            Assert.Equal(400, image.Picture!.PixelWidth);
            Assert.Equal("1", counter.Total);

            WpfTestHost.Wait(trigger.Command.ExecuteAsync(null));

            Assert.Equal("FAIL", verdict.Text);
            Assert.Equal("200", brightness.Value);
            Assert.Equal("50.0 %", counter.Yield);

            var reset = view.Widgets.OfType<ButtonWidgetViewModel>().Single(b => b.Widget.Action == WidgetAction.ResetCounts);
            WpfTestHost.Wait(reset.Command.ExecuteAsync(null));
            Assert.Equal("0", counter.Total);
        });
    }

    [Fact]
    public void A_setting_is_changed_from_the_screen_within_its_limits()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var project = Project.Create(_folder, "Line", Recipe(), "Top");
            project.SetLimit(project.Stations[0], "Limit", 100, 150);

            using var runtime = new ProjectRuntime(project);
            using var view = new ScreenViewModel(ScreenLayout.Standard(project.Name, runtime.Stations), runtime, project.Folder);

            var limit = view.Widgets.OfType<SettingWidgetViewModel>().Single(v => v.Widget.Name == "Limit");
            var verdict = view.Widgets.OfType<VerdictWidgetViewModel>().Single();
            var trigger = view.Widgets.OfType<ButtonWidgetViewModel>().First(b => b.Station == "Top" && b.Widget.Action == WidgetAction.Trigger);

            Assert.Contains("100 – 150", limit.Hint, StringComparison.Ordinal);

            // Refused: said why, and the box goes back.
            limit.ValueText = "300";
            Assert.True(limit.HasProblem);
            Assert.Contains("between 100 and 150", limit.Problem, StringComparison.Ordinal);
            Assert.Equal("128", limit.ValueText);

            limit.RaiseCommand.Execute(null);
            Assert.False(limit.HasProblem);
            Assert.NotEqual("128", limit.ValueText);

            limit.ValueText = "100";
            Assert.Equal("100", limit.ValueText);
            Assert.False(limit.LowerCommand.CanExecute(null));

            // The first image is 50: under 100, a pass.
            WpfTestHost.Wait(trigger.Command.ExecuteAsync(null));
            Assert.Equal("PASS", verdict.Text);

            // The second is 200, a fail - and the change is kept for the next start.
            WpfTestHost.Wait(trigger.Command.ExecuteAsync(null));
            Assert.Equal("FAIL", verdict.Text);

            using var again = new ProjectRuntime(project);
            Assert.Equal(100.0, again.Stations[0].SettingValue("Limit"));
        });
    }

    [Fact]
    public void A_read_only_setting_is_only_shown()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var project = Project.Create(_folder, "Line", Recipe(), "Top");
            using var runtime = new ProjectRuntime(project);

            var screen = new Screen
            {
                Widgets =
                {
                    new Widget { Kind = WidgetKind.Setting, Station = "Top", Name = "Limit", ReadOnly = true, Width = 300, Height = 30 },
                    new Widget { Kind = WidgetKind.Setting, Station = "Top", Name = "Nothing", Width = 300, Height = 30 },
                },
            };

            using var view = new ScreenViewModel(screen, runtime, project.Folder);
            var shown = (SettingWidgetViewModel)view.Widgets[0];
            var missing = (SettingWidgetViewModel)view.Widgets[1];

            Assert.True(shown.IsDisplayOnly);
            Assert.False(shown.IsNumberEditor);
            Assert.Equal("128", shown.DisplayText);
            Assert.True(missing.IsDisplayOnly);
            Assert.Contains("publishes no setting", missing.Hint, StringComparison.Ordinal);

            var path = Path.Combine(_folder, "ro.screen");
            screen.Save(path);
            Assert.True(Screen.Load(path).Widgets[0].ReadOnly);
            Assert.DoesNotContain("readOnly\": false", File.ReadAllText(path), StringComparison.Ordinal);
        });
    }

    /// <summary><see cref="Recipe"/> with the check's Minimum published too, as "Floor": a low and a high for a range.</summary>
    private VisionGraph RangeRecipe()
    {
        var graph = Recipe();
        Publishing.Publish(graph, graph.Nodes.OfType<LimitCheckNode>().Single(), PublishedKind.Setting, nameof(LimitCheckNode.Minimum), "Floor");
        return graph;
    }

    [Fact]
    public void A_setting_keeps_its_control_and_label_in_the_screen_file()
    {
        var path = Path.Combine(_folder, "styles.screen");

        new Screen
        {
            Widgets =
            {
                new Widget { Kind = WidgetKind.Setting, Station = "Top", Name = "Limit" },
                new Widget { Kind = WidgetKind.Setting, Station = "Top", Name = "Floor", HighName = "Limit", Style = SettingStyle.Range, LabelPlacement = LabelPlacement.Top },
            },
        }.Save(path);

        var text = File.ReadAllText(path);
        var loaded = Screen.Load(path).Widgets;

        Assert.Equal(SettingStyle.Stepper, loaded[0].Style);
        Assert.Equal(LabelPlacement.Left, loaded[0].LabelPlacement);
        Assert.Equal(SettingStyle.Range, loaded[1].Style);
        Assert.Equal(LabelPlacement.Top, loaded[1].LabelPlacement);
        Assert.Equal("Limit", loaded[1].HighName);

        // The usual is not written, so older screens and newer ones read the same.
        Assert.Contains("\"style\": \"Range\"", text, StringComparison.Ordinal);
        Assert.Contains("\"labelPlacement\": \"Top\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Stepper", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Left\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_range_with_no_high_setting_is_a_problem_on_the_screen()
    {
        var recipe = RangeRecipe();
        var stations = new[] { new StationRecipe("Top", recipe) };

        var screen = new Screen
        {
            Widgets =
            {
                new Widget { Kind = WidgetKind.Setting, Station = "Top", Name = "Floor", Style = SettingStyle.Range },
                new Widget { Kind = WidgetKind.Setting, Station = "Top", Name = "Floor", Style = SettingStyle.Range, HighName = "Nothing" },
                new Widget { Kind = WidgetKind.Setting, Station = "Top", Name = "Floor", Style = SettingStyle.Range, HighName = "Brightness" },
                new Widget { Kind = WidgetKind.Setting, Station = "Top", Name = "Floor", Style = SettingStyle.Range, HighName = "Limit" },
            },
        };

        var problems = ScreenLayout.Problems(screen, stations);

        Assert.Equal(3, problems.Count);
        Assert.Contains("no high setting chosen", problems[0], StringComparison.Ordinal);
        Assert.Contains("publishes no 'Nothing'", problems[1], StringComparison.Ordinal);
        Assert.Contains("not a setting", problems[2], StringComparison.Ordinal);
    }

    /// <summary>
    /// A slider needs both ends. Without them it is − and +, and says why;
    /// with them a value let go is held to them.
    /// </summary>
    [Fact]
    public void A_slider_needs_both_ends_and_holds_to_them()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var project = Project.Create(_folder, "Line", Recipe(), "Top");
            var screen = new Screen { Widgets = { new Widget { Kind = WidgetKind.Setting, Station = "Top", Name = "Limit", Style = SettingStyle.Slider, Width = 300, Height = 30 } } };

            using (var runtime = new ProjectRuntime(project))
            using (var view = new ScreenViewModel(screen, runtime, project.Folder))
            {
                var open = (SettingWidgetViewModel)view.Widgets[0];

                Assert.True(open.IsStepperEditor);
                Assert.False(open.IsSliderEditor);
                Assert.Contains("a slider needs a lowest and a highest", open.Hint, StringComparison.Ordinal);
            }

            project.SetLimit(project.Stations[0], "Limit", 100, 150);

            using (var runtime = new ProjectRuntime(project))
            using (var view = new ScreenViewModel(screen, runtime, project.Folder))
            {
                var limit = (SettingWidgetViewModel)view.Widgets[0];

                Assert.True(limit.IsSliderEditor);
                Assert.False(limit.IsStepperEditor);
                Assert.Equal(SettingStyle.Slider, limit.Style);
                Assert.Equal(100, limit.SliderMinimum);
                Assert.Equal(150, limit.SliderMaximum);

                limit.SliderValue = 140;
                Assert.Equal(140.0, runtime.Stations[0].SettingValue("Limit"));
                Assert.Equal("140", limit.ValueText);

                // Dragged past the end: held there, not refused.
                limit.SliderValue = 500;
                Assert.Equal(150.0, runtime.Stations[0].SettingValue("Limit"));
                Assert.False(limit.HasProblem);
            }
        });
    }

    [Fact]
    public void A_range_is_two_settings_on_one_slider()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var project = Project.Create(_folder, "Line", RangeRecipe(), "Top");
            project.SetLimit(project.Stations[0], "Floor", 0, null);
            project.SetLimit(project.Stations[0], "Limit", null, 200);

            using var runtime = new ProjectRuntime(project);
            var screen = new Screen
            {
                Widgets =
                {
                    new Widget { Kind = WidgetKind.Setting, Station = "Top", Name = "Floor", HighName = "Limit", Style = SettingStyle.Range, Width = 400, Height = 60 },
                    new Widget { Kind = WidgetKind.Setting, Station = "Top", Name = "Floor", HighName = "Nothing", Style = SettingStyle.Range, Width = 400, Height = 60 },
                    new Widget { Kind = WidgetKind.Button, Station = "Top", Action = WidgetAction.Trigger, Width = 100, Height = 30 },
                },
            };

            using var view = new ScreenViewModel(screen, runtime, project.Folder);
            var range = (SettingWidgetViewModel)view.Widgets[0];
            var half = (SettingWidgetViewModel)view.Widgets[1];
            var trigger = (ButtonWidgetViewModel)view.Widgets[2];

            Assert.True(range.IsRangeEditor);
            Assert.Equal("Floor – Limit", range.Label);
            Assert.Equal(0, range.SliderMinimum);
            Assert.Equal(200, range.SliderMaximum);
            Assert.Equal(0, range.SliderValue);
            Assert.Equal(128, range.HighSliderValue);
            Assert.Equal("0 – 128", range.DisplayText);

            range.HighSliderValue = 150;
            range.SliderValue = 20;
            Assert.Equal(150.0, runtime.Stations[0].SettingValue("Limit"));
            Assert.Equal(20.0, runtime.Stations[0].SettingValue("Floor"));
            Assert.Equal("20", range.ValueText);
            Assert.Equal("150", range.HighValueText);

            // The high end typed in its own box, refused beyond its highest.
            range.HighValueText = "300";
            Assert.True(range.HasProblem);
            Assert.Equal("150", range.HighValueText);

            // No high setting: the low one alone, with − and +, and why.
            Assert.True(half.IsStepperEditor);
            Assert.Contains("publishes no setting 'Nothing'", half.Hint, StringComparison.Ordinal);

            // An inspection that changes nothing puts nothing back: a box
            // being typed in, a handle being dragged, are left alone.
            var raised = new List<string?>();
            range.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            WpfTestHost.Wait(trigger.Command.ExecuteAsync(null));

            Assert.DoesNotContain(nameof(SettingWidgetViewModel.ValueText), raised);
            Assert.DoesNotContain(nameof(SettingWidgetViewModel.SliderValue), raised);
        });
    }

    [Fact]
    public void A_label_on_top_has_the_control_under_it_across_the_widget()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var project = Project.Create(_folder, "Line", Recipe(), "Top");
            using var runtime = new ProjectRuntime(project);

            var screen = new Screen
            {
                Widgets =
                {
                    new Widget { Kind = WidgetKind.Setting, Station = "Top", Name = "Limit", LabelPlacement = LabelPlacement.Top, Width = 300, Height = 60 },
                    new Widget { Kind = WidgetKind.Setting, Station = "Top", Name = "Limit", Width = 300, Height = 30 },
                },
            };

            using var view = new ScreenViewModel(screen, runtime, project.Folder);
            var top = (SettingWidgetViewModel)view.Widgets[0];
            var left = (SettingWidgetViewModel)view.Widgets[1];

            Assert.True(top.IsLabelOnTop);
            Assert.Equal(1, top.EditorRow);
            Assert.Equal(2, top.EditorColumnSpan);
            Assert.Equal(System.Windows.HorizontalAlignment.Stretch, top.EditorAlignment);
            Assert.True(double.IsNaN(top.BoxWidth));

            Assert.False(left.IsLabelOnTop);
            Assert.Equal(0, left.EditorRow);
            Assert.Equal(1, left.EditorColumn);
            Assert.Equal(System.Windows.HorizontalAlignment.Right, left.EditorAlignment);
            Assert.Equal(90, left.BoxWidth);
        });
    }

    /// <summary>The running window draws each control: a one-handle slider, a two-handle range, − and + under a label above.</summary>
    [Fact]
    public void A_running_screen_draws_sliders_and_ranges()
    {
        var project = Project.Create(_folder, "Line", RangeRecipe(), "Top");
        project.SetLimit(project.Stations[0], "Floor", 0, 200);
        project.SetLimit(project.Stations[0], "Limit", 0, 200);

        new Screen
        {
            Widgets =
            {
                new Widget { Kind = WidgetKind.Setting, X = 0, Y = 0, Width = 400, Height = 40, Station = "Top", Name = "Limit", Style = SettingStyle.Slider },
                new Widget { Kind = WidgetKind.Setting, X = 0, Y = 50, Width = 400, Height = 60, Station = "Top", Name = "Floor", HighName = "Limit", Style = SettingStyle.Range, LabelPlacement = LabelPlacement.Top },
                new Widget { Kind = WidgetKind.Setting, X = 0, Y = 120, Width = 400, Height = 60, Station = "Top", Name = "Limit", LabelPlacement = LabelPlacement.Top },
            },
        }.Save(ScreenLayout.MainScreenPath(project));

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var window = RuntimeWindow.Create(project, fullScreen: false);
            window.ShowActivated = false;
            window.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;

            try
            {
                window.Show();
                WpfTestHost.Settle(window.Dispatcher);

                var sliders = Descendants(window).OfType<Sirius.Studio.App.Controls.RangeSlider>().Where(slider => slider.IsVisible).ToArray();

                Assert.Equal(2, sliders.Length);
                Assert.True(sliders[0].IsSingle);
                Assert.Equal(128, sliders[0].High);
                Assert.False(sliders[1].IsSingle);
                Assert.Equal(0, sliders[1].Low);
                Assert.Equal(128, sliders[1].High);
                Assert.Equal(200, sliders[1].Maximum);

                // Let go: the setting takes the handle's value.
                sliders[0].High = 60;
                sliders[0].RaiseEvent(new System.Windows.RoutedEventArgs(Sirius.Studio.App.Controls.RangeSlider.ValueCommittedEvent, sliders[0]));
                WpfTestHost.Settle(window.Dispatcher);

                var model = (ScreenViewModel)window.DataContext;
                Assert.Equal(60.0, model.Widgets.OfType<SettingWidgetViewModel>().First().SliderValue);
            }
            finally
            {
                window.Close();
                WpfTestHost.Settle(window.Dispatcher);
            }
        });
    }

    [Fact]
    public void A_run_button_says_stop_while_running()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var project = Project.Create(_folder, "Line", Recipe(), "Top");
            using var runtime = new ProjectRuntime(project);
            using var view = new ScreenViewModel(ScreenLayout.Standard(project.Name, runtime.Stations), runtime, project.Folder);

            var run = view.Widgets.OfType<ButtonWidgetViewModel>().Single(b => b.Station == "Top" && b.Widget.Action == WidgetAction.RunStop);
            var all = view.Widgets.OfType<ButtonWidgetViewModel>().Single(b => b.Station is null && b.Widget.Action == WidgetAction.RunStop);

            Assert.Equal("Run", run.Caption);

            WpfTestHost.Wait(run.Command.ExecuteAsync(null));

            Assert.Equal("Stop", run.Caption);
            WpfTestHost.PumpUntil(() => runtime.Stations[0].Counts.Total >= 3);
            Assert.Equal("Stop All", all.Caption);

            WpfTestHost.Wait(all.Command.ExecuteAsync(null));

            Assert.Equal("Run", run.Caption);
            Assert.True(runtime.Stations[0].Counts.Total > 0);

            // Stopped: the picture of the inspection it stopped on is drawn.
            Assert.NotNull(view.Widgets.OfType<ImageWidgetViewModel>().Single().Picture);
        });
    }

    [Fact]
    public void A_station_that_would_not_load_shows_a_fault()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var project = Project.Create(_folder, "Line", Recipe(), "Top");
            File.WriteAllText(project.RecipePath(project.Stations[0]), "not json");

            using var runtime = new ProjectRuntime(project);
            using var view = new ScreenViewModel(ScreenLayout.Standard(project.Name, runtime.Stations), runtime, project.Folder);

            var verdict = view.Widgets.OfType<VerdictWidgetViewModel>().Single();

            Assert.Equal("FAULT", verdict.Text);
            Assert.Equal(VerdictLook.Fault, verdict.Look);
            Assert.True(view.HasProblems);
            Assert.Contains("could not be opened", view.ProblemText, StringComparison.Ordinal);
        });
    }

    /// <summary>The runtime program's theme, read from Studio's assembly by name, is all there.</summary>
    [Fact]
    public void The_runtime_finds_studios_theme()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var app = System.Windows.Application.Current!;
            var before = app.Resources.MergedDictionaries.Count;

            Sirius.Studio.App.Hosting.SiriusHost.LoadTheme(app);

            try
            {
                Assert.NotNull(app.TryFindResource("Surface.Panel"));
                Assert.NotNull(app.TryFindResource("Icon.ph_run"));
            }
            finally
            {
                while (app.Resources.MergedDictionaries.Count > before)
                    app.Resources.MergedDictionaries.RemoveAt(app.Resources.MergedDictionaries.Count - 1);
            }
        });
    }

    /// <summary>
    /// Exit with nothing running closes the window. It had thrown: the
    /// stop finished at once and the window was closed again from inside
    /// its own closing.
    /// </summary>
    [Fact]
    public void Exit_with_nothing_running_closes()
    {
        var project = Project.Create(_folder, "Line", Recipe(), "Top");

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var window = RuntimeWindow.Create(project, fullScreen: false);
            var asked = 0;
            window.Confirm = _ => { asked++; return true; };
            var closed = false;
            window.Closed += (_, _) => closed = true;

            window.Show();
            window.Close();
            WpfTestHost.PumpUntil(() => closed);

            Assert.Equal(0, asked);
        });
    }

    /// <summary>
    /// An Image Viewer the screen colours is that colour when running. The
    /// viewer's own dark grey was set in its markup, which outranked the
    /// screen's template, so the designer showed the colour and the running
    /// screen did not.
    /// </summary>
    [Fact]
    public void A_running_image_viewer_is_the_colour_the_screen_gives_it()
    {
        var project = Project.Create(_folder, "Line", Recipe(), "Top");
        new Screen
        {
            Widgets =
            {
                new Widget { Kind = WidgetKind.Image, X = 0, Y = 0, Width = 300, Height = 200, Station = "Top", Name = "Main View", Background = "#000000" },
                new Widget { Kind = WidgetKind.Image, X = 300, Y = 0, Width = 300, Height = 200, Station = "Top", Name = "Main View" },
            },
        }.Save(ScreenLayout.MainScreenPath(project));

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var window = RuntimeWindow.Create(project, fullScreen: false);
            window.ShowActivated = false;
            window.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;

            try
            {
                window.Show();
                WpfTestHost.Settle(window.Dispatcher);

                var viewers = Descendants(window).OfType<Sirius.Studio.App.Controls.ImageViewer>().ToArray();
                Assert.Equal(2, viewers.Length);

                Assert.Equal(System.Windows.Media.Colors.Black, ((System.Windows.Media.SolidColorBrush)viewers[0].Background).Color);
                Assert.Equal(System.Windows.Media.Color.FromRgb(0x17, 0x18, 0x1A), ((System.Windows.Media.SolidColorBrush)viewers[1].Background).Color);
            }
            finally
            {
                window.Close();
                WpfTestHost.Settle(window.Dispatcher);
            }
        });
    }

    /// <summary>Studio's viewers, which set no colour, keep the dark grey.</summary>
    [Fact]
    public void A_viewer_with_no_colour_set_is_dark_grey() =>
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var viewer = new Sirius.Studio.App.Controls.ImageViewer();

            Assert.Equal(System.Windows.Media.Color.FromRgb(0x17, 0x18, 0x1A), ((System.Windows.Media.SolidColorBrush)viewer.Background).Color);
        });

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;

            foreach (var below in Descendants(child))
                yield return below;
        }
    }

    /// <summary>With a station running, Exit asks, stops it - finishing the inspection in hand - and closes.</summary>
    [Fact]
    public void Exit_while_running_asks_stops_and_closes()
    {
        var project = Project.Create(_folder, "Line", Recipe(), "Top");

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var window = RuntimeWindow.Create(project, fullScreen: false);
            var asked = new List<string>();
            window.Confirm = question => { asked.Add(question); return true; };
            var closed = false;
            window.Closed += (_, _) => closed = true;

            window.Show();
            var station = window.Runtime.Stations[0];
            station.Start();
            WpfTestHost.PumpUntil(() => station.Counts.Total >= 2);

            window.Close();
            WpfTestHost.PumpUntil(() => closed);

            Assert.Equal(["Stop every station and close?"], asked);
            Assert.Equal(StationState.Ready, station.State);
        });
    }

    /// <summary>
    /// A running station leaves the window time for its own work - even the
    /// least urgent, which a bare yield between inspections starved.
    /// </summary>
    [Fact]
    public void A_running_station_does_not_starve_the_window()
    {
        var project = Project.Create(_folder, "Line", Recipe(), "Top");

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var window = RuntimeWindow.Create(project, fullScreen: false);
            window.Confirm = _ => true;
            var closed = false;
            window.Closed += (_, _) => closed = true;

            window.Show();
            var station = window.Runtime.Stations[0];
            station.Start();
            WpfTestHost.PumpUntil(() => station.Counts.Total >= 3);

            var ran = 0;

            for (var i = 0; i < 5; i++)
            {
                var before = ran;
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => ran++);
                WpfTestHost.PumpUntil(() => ran > before, timeoutSeconds: 5);
            }

            Assert.True(station.State == StationState.Running);

            window.Close();
            WpfTestHost.PumpUntil(() => closed);
        });
    }

    /// <summary>Answering no leaves it running and open.</summary>
    [Fact]
    public void Exit_answered_no_keeps_running()
    {
        var project = Project.Create(_folder, "Line", Recipe(), "Top");

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var window = RuntimeWindow.Create(project, fullScreen: false);
            window.Confirm = _ => false;
            var closed = false;
            window.Closed += (_, _) => closed = true;

            window.Show();
            var station = window.Runtime.Stations[0];
            station.Start();

            window.Close();
            WpfTestHost.Settle(window.Dispatcher);

            Assert.False(closed);
            Assert.Equal(StationState.Running, station.State);

            window.Confirm = _ => true;
            window.Close();
            WpfTestHost.PumpUntil(() => closed);
        });
    }

    /// <summary>The window builds - its templates and the screen in it - for a real project.</summary>
    [Fact]
    public void The_runtime_window_opens_a_project()
    {
        var project = Project.Create(_folder, "Line", Recipe(), "Top");

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var window = RuntimeWindow.Create(project, fullScreen: false);

            Assert.Equal("Line - Sirius Edge", window.Title);
            Assert.Single(window.Runtime.Stations);
            Assert.NotEmpty(window.Screen.Widgets);
            Assert.True(File.Exists(ScreenLayout.MainScreenPath(project)));

            window.Runtime.Dispose();
        });
    }
}
