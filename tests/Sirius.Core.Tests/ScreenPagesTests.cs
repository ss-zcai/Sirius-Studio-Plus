using System.IO;
using System.Windows;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Builder;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Core.Screens;
using Sirius.UI.Screens;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Several screens in a project (decision 79): pages and popups, opened by
/// buttons; their files, their check, running in the runtime window, and
/// designed in Builder.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ScreenPagesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sirius-pages-" + Guid.NewGuid().ToString("N"));

    public ScreenPagesTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
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

    /// <summary>
    /// A folder of a dark and a bright picture; Region Stats' mean published
    /// as "Brightness", a check's Maximum as "Limit", a viewer as "Main View".
    /// </summary>
    private VisionGraph Recipe()
    {
        var folder = Path.Combine(_root, "images");
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

        Publishing.Publish(graph, stats, PublishedKind.Result, "Mean", "Brightness");
        Publishing.Publish(graph, check, PublishedKind.Setting, nameof(LimitCheckNode.Maximum), "Limit");
        Publishing.Publish(graph, viewer, PublishedKind.View, string.Empty, "Main View");

        return graph;
    }

    private static Widget OpenButton(string screen, string? caption = null) => new()
    {
        Kind = WidgetKind.Button,
        X = 20,
        Y = 700,
        Width = 140,
        Height = 40,
        Action = WidgetAction.OpenScreen,
        Screen = screen,
        Text = caption,
    };

    /// <summary>
    /// Main - the picture, a Settings button and a Details button; Settings,
    /// a popup with the Limit and no Close button; Details, a page with the
    /// picture again and Back.
    /// </summary>
    private Project PagedProject()
    {
        var project = Project.Create(_root, "Line", Recipe(), "Top");

        new Screen
        {
            Widgets =
            [
                new Widget { Kind = WidgetKind.Image, X = 20, Y = 20, Width = 400, Height = 300, Station = "Top", Name = "Main View" },
                new Widget { Kind = WidgetKind.Value, X = 440, Y = 20, Width = 300, Height = 30, Station = "Top", Name = "Brightness" },
                OpenButton("Settings"),
                OpenButton("Details", "More"),
            ],
        }.Save(ScreenLayout.MainScreenPath(project));

        new Screen
        {
            Name = "Settings",
            Kind = ScreenKind.Popup,
            Width = 600,
            Height = 400,
            Widgets =
            [
                new Widget { Kind = WidgetKind.Setting, X = 20, Y = 60, Width = 400, Height = 30, Station = "Top", Name = "Limit" },
                new Widget { Kind = WidgetKind.Value, X = 20, Y = 100, Width = 400, Height = 30, Station = "Top", Name = "Brightness" },
            ],
        }.Save(ScreenSet.PathFor(project, "Settings"));

        new Screen
        {
            Name = "Details",
            Widgets =
            [
                new Widget { Kind = WidgetKind.Image, X = 20, Y = 20, Width = 800, Height = 600, Station = "Top", Name = "Main View" },
                new Widget { Kind = WidgetKind.Button, X = 1100, Y = 10, Width = 140, Height = 40, Action = WidgetAction.Close },
            ],
        }.Save(ScreenSet.PathFor(project, "Details"));

        return project;
    }

    // ---- the files --------------------------------------------------------------------

    [Fact]
    public void A_screen_is_saved_in_the_oldest_format_that_holds_it()
    {
        var path = Path.Combine(_root, "a.screen");

        // Nothing new: format 1, which a build from before pages still reads.
        new Screen { Widgets = [new Widget { Kind = WidgetKind.Text, Text = "Hello" }] }.Save(path);
        Assert.Equal(1, Screen.Load(path).Version);
        Assert.DoesNotContain("\"kind\": \"Page\"", File.ReadAllText(path), StringComparison.Ordinal);

        // A popup, or a button opening a screen: format 2.
        new Screen { Name = "Settings", Kind = ScreenKind.Popup }.Save(path);
        var popup = Screen.Load(path);
        Assert.Equal(2, popup.Version);
        Assert.Equal(ScreenKind.Popup, popup.Kind);

        new Screen { Widgets = [OpenButton("Settings")] }.Save(path);
        var opening = Screen.Load(path);
        Assert.Equal(2, opening.Version);
        Assert.Equal("Settings", opening.Widgets[0].Screen);

        // A newer format than this build reads is refused, saying so.
        File.WriteAllText(path, "{ \"version\": 4, \"widgets\": [] }");
        Assert.Contains("newer version", Assert.Throws<InvalidDataException>(() => Screen.Load(path)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_lists_its_screens_with_the_start_screen_first()
    {
        var project = Project.Create(_root, "Line", new VisionGraph(), "Top");

        // Main is there before its file is.
        Assert.Equal([Screen.StartName], ScreenSet.Names(project));

        new Screen { Name = "Zoom", Kind = ScreenKind.Popup }.Save(ScreenSet.PathFor(project, "Zoom"));
        new Screen { Name = "Alarms" }.Save(ScreenSet.PathFor(project, "Alarms"));
        new Screen().Save(ScreenLayout.MainScreenPath(project));

        Assert.Equal(["Main", "Alarms", "Zoom"], ScreenSet.Names(project));

        Assert.Equal("A name is needed.", ScreenSet.NameProblem(project, " "));
        Assert.Contains("start screen", ScreenSet.NameProblem(project, "main"), StringComparison.Ordinal);
        Assert.Contains("already", ScreenSet.NameProblem(project, "zoom"), StringComparison.Ordinal);
        Assert.NotNull(ScreenSet.NameProblem(project, "a/b"));
        Assert.Null(ScreenSet.NameProblem(project, "Settings"));

        // Renaming to itself, or to another case of itself, is no clash.
        Assert.Null(ScreenSet.NameProblem(project, "Zoom", "Zoom"));
        Assert.Null(ScreenSet.NameProblem(project, "ZOOM", "Zoom"));
        Assert.NotNull(ScreenSet.NameProblem(project, "Alarms", "Zoom"));

        Assert.Equal("Settings", ScreenSet.Suggest(project, "Settings"));
        Assert.Equal("Zoom 2", ScreenSet.Suggest(project, "Zoom"));
    }

    [Fact]
    public void Every_screen_loads_named_after_its_file_and_one_unreadable_is_said()
    {
        var project = PagedProject();
        File.WriteAllText(ScreenSet.PathFor(project, "Broken"), "not json");

        // A screen file copied in under another name is that name.
        File.Copy(ScreenSet.PathFor(project, "Details"), ScreenSet.PathFor(project, "Copy"));

        using var runtime = new ProjectRuntime(project);
        var screens = ScreenSet.Load(project, runtime.Stations, out var problems);

        Assert.Equal(["Main", "Copy", "Details", "Settings"], screens.Select(screen => screen.Name));
        Assert.Contains(problems, problem => problem.Contains("Broken.screen", StringComparison.Ordinal));
        Assert.Equal(ScreenKind.Popup, screens.Single(screen => screen.Name == "Settings").Kind);
        Assert.Empty(ScreenSet.Problems(screens, runtime.Stations));
    }

    [Fact]
    public void A_button_opening_nothing_or_a_screen_not_there_and_a_popup_too_big_are_problems()
    {
        var start = new ScreenInfo("Main", ScreenKind.Page, 1280, 800);
        var screen = new Screen { Name = "Main", Widgets = [OpenButton("Nowhere"), OpenButton(string.Empty, "Go")] };

        var problems = ScreenSet.NavigationProblems(screen, [start]);
        Assert.Equal(["Button: there is no screen 'Nowhere'.", "Button 'Go': no screen to open is chosen."], problems);

        var big = new Screen { Name = "Big", Kind = ScreenKind.Popup, Width = 1400, Height = 600 };
        Assert.Contains("larger than Main", Assert.Single(ScreenSet.NavigationProblems(big, [start, ScreenSet.Info(big)])), StringComparison.Ordinal);

        // With several screens, each line says which.
        using var runtime = new ProjectRuntime(Project.Create(_root, "Line", new VisionGraph(), "Top"));
        var all = ScreenSet.Problems([screen, big], runtime.Stations);
        Assert.Contains("Main: Button: there is no screen 'Nowhere'.", all);
        Assert.Contains(all, line => line.StartsWith("Big: The popup is larger", StringComparison.Ordinal));
    }

    [Fact]
    public void Renaming_a_screen_moves_its_file_and_the_buttons_opening_it_follow()
    {
        var project = PagedProject();

        Assert.Throws<ArgumentException>(() => ScreenSet.Rename(project, "Main", "Home"));
        Assert.Throws<ArgumentException>(() => ScreenSet.Rename(project, "Settings", "Details"));

        var pointed = ScreenSet.Rename(project, "Settings", "Tuning");

        Assert.Equal(["Main"], pointed);
        Assert.False(File.Exists(ScreenSet.PathFor(project, "Settings")));
        Assert.Equal("Tuning", Screen.Load(ScreenSet.PathFor(project, "Tuning")).Name);
        Assert.Contains(Screen.Load(ScreenLayout.MainScreenPath(project)).Widgets, widget => widget.Screen == "Tuning");

        // A screen the caller changes itself - open in a designer - is left alone.
        Assert.Empty(ScreenSet.Rename(project, "Tuning", "Settings", leave: ["Main"]));
        Assert.Contains(Screen.Load(ScreenLayout.MainScreenPath(project)).Widgets, widget => widget.Screen == "Tuning");

        // A change of case is a rename too.
        ScreenSet.Rename(project, "Details", "DETAILS");
        Assert.Contains("DETAILS", ScreenSet.Names(project));
    }

    // ---- running ----------------------------------------------------------------------

    [Fact]
    public void A_popup_opens_over_the_page_and_a_page_in_place_of_it_with_a_way_back()
    {
        var project = PagedProject();

        WpfTestHost.OnStaThread(() =>
        {
            using var runtime = new ProjectRuntime(project);
            var screens = ScreenSet.Load(project, runtime.Stations, out _);
            using var view = new ScreenViewModel(screens, runtime, project.Folder);

            Assert.False(view.HasProblems, view.ProblemText);
            Assert.Equal("Main", view.Page.Name);
            Assert.False(view.HasPopup);

            var buttons = view.Widgets.OfType<ButtonWidgetViewModel>().ToArray();
            var settings = buttons.Single(button => button.Widget.Screen == "Settings");
            var details = buttons.Single(button => button.Widget.Screen == "Details");

            // Captions: the screen's name unless given one.
            Assert.Equal("Settings", settings.Caption);
            Assert.Equal("More", details.Caption);

            // A popup: over the page, which stays and takes no input.
            WpfTestHost.Wait(settings.Command.ExecuteAsync(null));
            Assert.Equal("Settings", view.Popup?.Name);
            Assert.Equal("Main", view.Page.Name);
            Assert.False(view.IsPageEnabled);
            Assert.True(view.Page.IsShown);
            Assert.True(view.Popup!.IsShown);

            // It has no Close button, so it is given one; Close closes it.
            Assert.True(view.Popup.NeedsCloseCorner);
            Assert.True(view.ClosePopupCommand.CanExecute(null));
            view.ClosePopupCommand.Execute(null);
            Assert.Null(view.Popup);
            Assert.True(view.IsPageEnabled);
            Assert.False(view.ClosePopupCommand.CanExecute(null));

            // A page: in place of Main, which is no longer on view; Back returns.
            WpfTestHost.Wait(details.Command.ExecuteAsync(null));
            Assert.Equal("Details", view.Page.Name);
            Assert.False(view.Screens[0].IsShown);
            Assert.True(view.CanGoBack);

            var back = view.Widgets.OfType<ButtonWidgetViewModel>().Single();
            Assert.Equal("Back", back.Caption);
            Assert.Equal(WidgetAction.Close, back.Widget.Action);

            WpfTestHost.Wait(back.Command.ExecuteAsync(null));
            Assert.Equal("Main", view.Page.Name);
            Assert.False(view.CanGoBack);

            // Opening the start screen starts the history again; a page opened over a popup closes it.
            view.Open("Details");
            view.Open("Settings");
            view.Open("Main");
            Assert.Null(view.Popup);
            Assert.Equal("Main", view.Page.Name);
            Assert.False(view.CanGoBack);

            // Close on the start page, with nothing to go back to, does nothing.
            view.Close();
            Assert.Equal("Main", view.Page.Name);
            Assert.False(view.Open("Nowhere"));
        });
    }

    [Fact]
    public void Values_follow_on_every_screen_and_pictures_only_on_the_ones_on_view()
    {
        var project = PagedProject();

        WpfTestHost.OnStaThread(() =>
        {
            using var runtime = new ProjectRuntime(project);
            using var view = new ScreenViewModel(ScreenSet.Load(project, runtime.Stations, out _), runtime, project.Folder);

            var main = view.Screens.Single(screen => screen.Name == "Main");
            var details = view.Screens.Single(screen => screen.Name == "Details");
            var mainPicture = main.Widgets.OfType<ImageWidgetViewModel>().Single();
            var detailPicture = details.Widgets.OfType<ImageWidgetViewModel>().Single();
            var hiddenValue = view.Find("Settings")!.Widgets.OfType<ValueWidgetViewModel>().Single();

            WpfTestHost.Wait(runtime.Stations[0].TriggerAsync());

            // On view: drawn. Not on view: not drawn, but its values follow.
            Assert.NotNull(mainPicture.Picture);
            Assert.Null(detailPicture.Picture);
            var shownValue = main.Widgets.OfType<ValueWidgetViewModel>().Single();
            Assert.Equal(shownValue.Value, hiddenValue.Value);
            Assert.Equal("50", hiddenValue.Value);

            // Brought on view with the station stopped: drawn at once, from its last inspection.
            view.Open("Details");
            Assert.NotNull(detailPicture.Picture);
            Assert.Equal(400, detailPicture.Picture!.PixelWidth);
        });
    }

    [Fact]
    public void The_runtime_window_shows_a_popup_over_the_page_and_Escape_closes_it()
    {
        var project = PagedProject();

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var window = RuntimeWindow.Create(project, fullScreen: true);

            try
            {
                Assert.Equal(3, window.Screen.Screens.Count);

                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1296, 860));
                content.Arrange(new Rect(0, 0, 1296, 860));
                WpfTestHost.Settle(window.Dispatcher);

                var layer = (FrameworkElement)window.FindName("PopupLayer");
                var frame = (System.Windows.Controls.Border)window.FindName("PopupFrame");
                Assert.Equal(Visibility.Collapsed, layer.Visibility);

                window.Screen.Open("Settings");
                WpfTestHost.Settle(window.Dispatcher);
                Assert.Equal(Visibility.Visible, layer.Visibility);
                content.Measure(new Size(1296, 860));
                content.Arrange(new Rect(0, 0, 1296, 860));
                Assert.Equal(602, frame.ActualWidth, 1);

                // Escape closes the popup, even full screen - and does not close the window.
                using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters());
                var escape = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Escape)
                {
                    RoutedEvent = UIElement.KeyDownEvent,
                };

                window.RaiseEvent(escape);
                Assert.True(escape.Handled);
                Assert.False(window.Screen.HasPopup);
                WpfTestHost.Settle(window.Dispatcher);
                Assert.Equal(Visibility.Collapsed, layer.Visibility);
            }
            finally
            {
                window.Runtime.Dispose();
            }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;

            foreach (var below in Descendants(child))
                yield return below;
        }
    }

    // ---- Builder --------------------------------------------------------------------

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

    [Fact]
    public void Builder_adds_a_popup_in_a_tab_of_its_own_which_the_toolbar_and_Properties_follow()
    {
        var project = Project.Create(_root, "Line", Recipe(), "Top");

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var model = Builder(project);
            var main = model.MainDesigner!;
            Assert.Equal(["Main"], model.Screens.Select(screen => screen.Name));

            var popup = model.AddScreen("Settings", ScreenKind.Popup)!;

            // Written, listed, open and in front.
            Assert.True(File.Exists(ScreenSet.PathFor(project, "Settings")));
            Assert.Equal(["Main", "Settings"], model.Screens.Select(screen => screen.Name));
            Assert.True(model.FindScreen("Settings")!.IsPopup);
            Assert.Equal("Popup · 640 × 420", model.FindScreen("Settings")!.KindText);
            Assert.Same(popup, model.Designer);
            Assert.Equal([main, popup], model.Designers);
            Assert.Same(popup.SaveCommand, model.SaveCommand.Target);
            Assert.Same(popup.PropertyCategories, model.PropertyCategories);

            // It starts with a title and its Close.
            Assert.Equal(ScreenKind.Popup, popup.Kind);
            Assert.Contains(popup.Widgets, widget => widget.Widget.Action == WidgetAction.Close);
            Assert.False(popup.ResetCommand.CanExecute(null));
            Assert.True(main.ResetCommand.CanExecute(null));

            // A double-click on a station brings Main back to the front.
            model.Open(model.Stations[0]);
            Assert.Same(main, model.Designer);
            Assert.Same(main.SaveCommand, model.SaveCommand.Target);

            // Opening it again brings the same designer.
            Assert.Same(popup, model.OpenScreen(model.FindScreen("Settings")));
            Assert.Equal(2, model.Designers.Count);

            // In the window: a tab of its own; coming to the front makes it the one acted on.
            var window = new BuilderWindow(model) { KeepsLayout = false };

            try
            {
                window.ShowScreen(popup);
                var tab = window.TabForTests(popup)!;
                Assert.Equal("Settings", tab.Title);
                Assert.Equal("screen:Settings", tab.ContentId);

                popup.Add(WidgetKind.Text);
                Assert.Equal("Settings *", tab.Title);

                window.ShowScreen(main);
                Assert.Same(main, model.Designer);
                Assert.Equal("Main", window.ScreenTabForTests!.Title);

                // Closing the tab of a changed screen asks; No closes it without saving.
                Assert.True(model.CloseScreen(popup));
                Assert.Equal([main], model.Designers);
                Assert.Null(window.TabForTests(popup));
                Assert.Same(main, model.Designer);
                Assert.False(model.CloseScreen(main));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void A_button_set_to_Open_Screen_opens_the_first_other_screen_and_says_when_it_is_gone()
    {
        var project = Project.Create(_root, "Line", Recipe(), "Top");

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var model = Builder(project);
            model.AddScreen("Settings", ScreenKind.Popup);
            model.AddScreen("Details", ScreenKind.Page);

            var main = model.MainDesigner!;
            model.ShowScreen();

            var button = main.Add(WidgetKind.Button);
            Assert.Equal(["Details", "Settings (popup)"], main.ScreenOptions.Select(option => option.Label));

            button.Action = WidgetAction.OpenScreen;
            Assert.Equal("Details", button.Widget.Screen);
            Assert.Equal("Details  ·  opens Details", button.Caption);
            Assert.Null(button.ScreenProblem);

            // The Opens row shows only for Open Screen; the Station row not for it.
            var rows = button.Categories.SelectMany(category => category.Rows).ToArray();
            Assert.True(rows.Single(row => row.Name == "Opens").IsVisible);
            Assert.False(rows.Single(row => row.Name == "Station").IsVisible);

            button.ScreenValue = "Settings";
            Assert.DoesNotContain(main.ProblemText, "Settings", StringComparison.Ordinal);

            // Removed: the button says so, as does the screen's check.
            var recycled = new List<string>();
            model.Recycle = path =>
            {
                recycled.Add(path);
                File.Delete(path);
            };
            Assert.True(model.RemoveScreen(model.FindScreen("Settings")!));

            Assert.Equal(ScreenSet.PathFor(project, "Settings"), Assert.Single(recycled));
            Assert.Equal(["Main", "Details"], model.Screens.Select(screen => screen.Name));
            Assert.Contains("no screen 'Settings'", button.ScreenProblem, StringComparison.Ordinal);
            Assert.True(main.HasProblems);
            Assert.Contains("there is no screen 'Settings'", main.ProblemText, StringComparison.Ordinal);

            // The start screen cannot be renamed or removed.
            var start = model.FindScreen("Main")!;
            Assert.False(model.RenameScreenCommand.CanExecute(start));
            Assert.False(model.RemoveScreen(start));

            // A Close button's caption follows the screen it is on.
            var close = main.Add(WidgetKind.Button);
            close.Action = WidgetAction.Close;
            Assert.Equal("Back", close.Caption);
        });
    }

    [Fact]
    public void A_station_renamed_is_followed_by_screens_open_and_not()
    {
        var project = PagedProject();

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var model = Builder(project);
            var settings = model.OpenScreen(model.FindScreen("Settings"))!;

            model.RenameStation(model.Stations[0], "Camera");

            // Open: as changes to save. Not open: in its file at once.
            Assert.All(settings.Widgets, widget => Assert.Equal("Camera", widget.Widget.Station));
            Assert.True(settings.IsModified);
            Assert.All(
                Screen.Load(ScreenSet.PathFor(project, "Details")).Widgets.Where(widget => widget.Kind == WidgetKind.Image),
                widget => Assert.Equal("Camera", widget.Station));
            Assert.Contains(Screen.Load(ScreenSet.PathFor(project, "Settings")).Widgets, widget => widget.Station == "Top");
            Assert.Contains("Details", model.StatusText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Renaming_a_screen_in_Builder_takes_its_tab_and_the_buttons_opening_it_along()
    {
        var project = PagedProject();

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var model = Builder(project);
            var main = model.MainDesigner!;
            var settings = model.OpenScreen(model.FindScreen("Settings"))!;
            settings.Add(WidgetKind.Text);

            model.RenameScreen(model.FindScreen("Settings")!, "Tuning");

            // The file moved; the open designer saves there, its changes kept.
            Assert.False(File.Exists(ScreenSet.PathFor(project, "Settings")));
            Assert.True(File.Exists(ScreenSet.PathFor(project, "Tuning")));
            Assert.Equal("Tuning", settings.Name);
            Assert.Equal("Tuning *", settings.TabTitle);

            // Main is open: its button follows as a change to save, its file not yet.
            Assert.Contains(main.Widgets, widget => widget.Widget.Screen == "Tuning");
            Assert.True(main.IsModified);
            Assert.Contains(Screen.Load(ScreenLayout.MainScreenPath(project)).Widgets, widget => widget.Screen == "Settings");
            Assert.Contains("Main", model.StatusText, StringComparison.Ordinal);

            Assert.True(model.SaveAll());
            Assert.Contains(Screen.Load(ScreenLayout.MainScreenPath(project)).Widgets, widget => widget.Screen == "Tuning");
            Assert.Equal(3, Screen.Load(ScreenSet.PathFor(project, "Tuning")).Widgets.Count);
            Assert.Equal("Tuning", Screen.Load(ScreenSet.PathFor(project, "Tuning")).Name);
            Assert.False(main.HasProblems, main.ProblemText);

            // The kind is a property of the screen; changed, it is saved.
            settings.Kind = ScreenKind.Page;
            Assert.True(settings.Save());
            Assert.Equal(ScreenKind.Page, Screen.Load(ScreenSet.PathFor(project, "Tuning")).Kind);
        });
    }
}
