using System.IO;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Screens;
using Sirius.Core.Serialization;
using Sirius.Builder;
using Sirius.Studio.App.Screens;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The screen designer: binding widgets, laying them out, undo, saving
/// (runtime MVP step 5).
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ScreenDesignerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-designer-" + Guid.NewGuid().ToString("N"));

    public ScreenDesignerTests(WpfTestHost host)
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

    /// <summary>A source, a threshold whose output is published as "Mask", region stats, a viewer.</summary>
    private static VisionGraph Recipe()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 64, Height = 48 };
        var threshold = new ThresholdNode();
        var stats = new RegionStatsNode();
        var viewer = new ViewerNode();

        foreach (var node in new VisionNode[] { source, threshold, stats, viewer })
            graph.Add(node);

        graph.TryConnect(source.ImageOut, threshold.ImageIn);
        graph.TryConnect(threshold.ImageOut, stats.ImageIn);
        graph.TryConnect(threshold.ImageOut, viewer.ImageIn);

        Publishing.Publish(graph, threshold, PublishedKind.Result, "Out", "Mask");
        return graph;
    }

    /// <summary>
    /// <see cref="Recipe"/> with more published, as Studio would leave it:
    /// region stats' mean as "Mean", the viewer, and the threshold level.
    /// </summary>
    private static VisionGraph PublishedRecipe()
    {
        var graph = Recipe();
        Publishing.Publish(graph, graph.Nodes.OfType<RegionStatsNode>().Single(), PublishedKind.Result, "Mean", "Mean");
        Publishing.Publish(graph, graph.Nodes.OfType<ViewerNode>().Single(), PublishedKind.View, string.Empty, "Image Viewer");
        Publishing.Publish(graph, graph.Nodes.OfType<ThresholdNode>().Single(), PublishedKind.Setting, nameof(ThresholdNode.Threshold), "Level");
        return graph;
    }

    /// <summary>A project with stations Top and Bottom, their recipes as Studio saved them.</summary>
    private (Project Project, StationRecipes Recipes) TwoStations()
    {
        var project = Project.Create(_folder, "Line", PublishedRecipe(), "Top");
        project.AddStation("Bottom", PublishedRecipe());

        return (project, new StationRecipes(project));
    }

    // ---- what a widget can show --------------------------------------------

    [Fact]
    public void A_picture_can_show_a_viewer_or_an_image_output_published_first()
    {
        var choices = ScreenBinding.Choices(Recipe(), WidgetKind.Image);

        Assert.Equal("Mask", choices[0].Name);
        Assert.Contains(choices, c => c.Kind == PublishedKind.View && !c.IsPublished);
        Assert.Contains(choices, c => c.Kind == PublishedKind.Result && c.Node is ConstantImageNode && !c.IsPublished);
        Assert.DoesNotContain(choices, c => c.Source.Contains("Mean", StringComparison.Ordinal));
    }

    [Fact]
    public void A_value_can_show_any_output_but_a_picture()
    {
        var choices = ScreenBinding.Choices(Recipe(), WidgetKind.Value);

        Assert.Contains(choices, c => c.Source == "Region Stats › Mean");
        Assert.DoesNotContain(choices, c => c.Name == "Mask");
        Assert.DoesNotContain(choices, c => c.Kind == PublishedKind.View);
    }

    [Fact]
    public void A_setting_widget_can_show_settings_but_not_hidden_ones()
    {
        var choices = ScreenBinding.Choices(Recipe(), WidgetKind.Setting);

        Assert.Contains(choices, c => c.Source == "Threshold › Threshold");
        Assert.DoesNotContain(choices, c => c.Member == nameof(ViewerNode.DisplayBits));
        Assert.All(choices, c => Assert.Equal(PublishedKind.Setting, c.Kind));
    }

    /// <summary>Choosing what is not published yet publishes it, under its own label.</summary>
    [Fact]
    public void Binding_publishes_what_is_not_published_yet()
    {
        var recipe = Recipe();
        var mean = ScreenBinding.Choices(recipe, WidgetKind.Value).Single(c => c.Source == "Region Stats › Mean");

        var (name, published) = ScreenBinding.Bind(recipe, mean);

        Assert.True(published);
        Assert.Equal("Mean", name);
        Assert.Equal("Mean", Publishing.Find(recipe, "Mean")!.Name);

        var again = ScreenBinding.Choices(recipe, WidgetKind.Value).Single(c => c.Name == "Mean");
        Assert.Equal(("Mean", false), ScreenBinding.Bind(recipe, again));
    }

    // ---- the designer ------------------------------------------------------

    [Fact]
    public void A_project_without_a_screen_starts_from_the_standard_one()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (_, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);

            Assert.Contains(designer.Widgets, w => w.Widget.Station == "Bottom");
            Assert.True(designer.IsModified, "a screen not yet saved is unsaved");
        });
    }

    [Fact]
    public void A_setting_widget_limits_its_setting_for_the_station_in_the_project()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (project, recipes) = TwoStations();
            var recipeFile = File.ReadAllText(project.RecipePath(project.Stations[1]));
            var designer = new ScreenDesignerViewModel(recipes);

            var widget = designer.Add(WidgetKind.Setting);
            widget.StationValue = "Bottom";
            widget.Choice = widget.Choices.Single(c => c.Name == "Level");

            Assert.True(widget.OperatorMayChange);
            Assert.True(widget.HasLimits);
            Assert.Contains("0 – 255", widget.SettingNote, StringComparison.Ordinal);

            widget.LowestText = "20";
            widget.HighestText = "90";

            // In the project, for Bottom, written at once - and the recipe untouched.
            Assert.Equal(new SettingLimit(20, 90), project.Stations[1].LimitFor("Level"));
            Assert.Null(project.Stations[0].LimitFor("Level"));
            Assert.Equal(new SettingLimit(20, 90), Project.Load(project.FilePath).Project.Stations[1].LimitFor("level"));
            Assert.Equal(recipeFile, File.ReadAllText(project.RecipePath(project.Stations[1])));
            Assert.Equal("20", widget.LowestText);

            widget.LowestText = "100";
            Assert.Contains("above", designer.StatusText, StringComparison.Ordinal);
            Assert.Equal("20", widget.LowestText);

            widget.LowestText = string.Empty;
            widget.HighestText = string.Empty;
            Assert.Empty(project.Stations[1].Limits);

            widget.OperatorMayChange = false;
            Assert.True(widget.Widget.ReadOnly);
            Assert.False(widget.HasLimits);

            designer.UndoCommand.Execute(null);
            Assert.False(designer.Widgets.Last().Widget.ReadOnly);
        });
    }

    [Fact]
    public void Saving_makes_the_screen_the_SIs_own()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (project, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);

            designer.Add(WidgetKind.Text);
            Assert.True(designer.Save());
            Assert.False(designer.IsModified);

            var saved = Screen.Load(ScreenLayout.MainScreenPath(project));
            Assert.False(saved.Generated);
            Assert.Contains(saved.Widgets, w => w.Kind == WidgetKind.Text && w.Text == "Text");

            // Opened again: as saved, nothing to save.
            Assert.False(new ScreenDesignerViewModel(recipes).IsModified);
        });
    }

    [Fact]
    public void An_added_widget_is_on_the_grid_selected_and_undoable()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (_, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);
            var count = designer.Widgets.Count;

            var added = designer.Add(WidgetKind.Value, 123, 457);

            Assert.Same(added, designer.Selected);
            Assert.True(added.IsSelected);
            Assert.Equal((120, 460), (added.X, added.Y));
            Assert.Equal("Top", added.Widget.Station);

            designer.UndoCommand.Execute(null);
            Assert.Equal(count, designer.Widgets.Count);

            designer.RedoCommand.Execute(null);
            Assert.Equal(count + 1, designer.Widgets.Count);
        });
    }

    /// <summary>A drag is one step, however many places it passes through.</summary>
    [Fact]
    public void A_drag_is_one_undo_step()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (_, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);
            var widget = designer.Add(WidgetKind.Verdict, 100, 100);

            designer.BeginGesture();
            widget.Place(110, 100, widget.Width, widget.Height);
            widget.Place(150, 130, widget.Width, widget.Height);
            widget.Place(200, 160, widget.Width, widget.Height);
            designer.EndGesture();

            designer.UndoCommand.Execute(null);

            var back = designer.Widgets.Last();
            Assert.Equal((100, 100), (back.X, back.Y));
        });
    }

    [Fact]
    public void A_widget_is_kept_on_the_canvas()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (_, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);
            var widget = designer.Add(WidgetKind.Button);

            widget.Place(5000, -50, 5, 5);

            Assert.Equal(designer.Width - DesignWidgetViewModel.MinimumSize, widget.X);
            Assert.Equal(0, widget.Y);
            Assert.Equal(DesignWidgetViewModel.MinimumSize, widget.Width);
        });
    }

    [Fact]
    public void Delete_duplicate_and_restack()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (_, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);
            var widget = designer.Add(WidgetKind.Counter, 100, 100);
            var count = designer.Widgets.Count;

            designer.DuplicateCommand.Execute(null);
            Assert.Equal(count + 1, designer.Widgets.Count);
            Assert.Equal((120, 120), (designer.Selected!.X, designer.Selected.Y));

            designer.SendToBackCommand.Execute(null);
            Assert.Same(designer.Selected, designer.Widgets[0]);

            designer.DeleteCommand.Execute(null);
            Assert.Equal(count, designer.Widgets.Count);
            Assert.Null(designer.Selected);
            Assert.Contains(widget, designer.Widgets);
        });
    }

    /// <summary>Builder binds only to what Studio published, and writes no recipe.</summary>
    [Fact]
    public void Binding_offers_only_published_names_and_writes_no_recipe()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (project, recipes) = TwoStations();
            var before = File.ReadAllText(project.RecipePath(project.Stations[1]));
            var designer = new ScreenDesignerViewModel(recipes);
            var value = designer.Add(WidgetKind.Value);

            value.StationValue = "Bottom";

            Assert.All(value.Choices, c => Assert.True(c.IsPublished));
            Assert.DoesNotContain(value.Choices, c => c.Source.Contains("Std Dev", StringComparison.Ordinal));

            value.Choice = value.Choices.Single(c => c.Name == "Mean");

            Assert.Equal("Mean", value.Widget.Name);
            Assert.Null(value.NameProblem);
            Assert.Equal(before, File.ReadAllText(project.RecipePath(project.Stations[1])));
        });
    }

    /// <summary>A name published and saved in Studio meanwhile appears once Builder looks again.</summary>
    [Fact]
    public void A_recipe_saved_in_Studio_is_read_again()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var project = Project.Create(_folder, "Line", Recipe(), "Top");
            var recipes = new StationRecipes(project);
            var designer = new ScreenDesignerViewModel(recipes);
            var value = designer.Add(WidgetKind.Value);

            Assert.True(value.HasNoChoices);
            Assert.Contains("Studio", value.NameProblem, StringComparison.Ordinal);

            // Studio publishes the mean and saves the recipe.
            var path = project.RecipePath(project.Stations[0]);
            var recipe = GraphSerializer.Load(path).Graph;
            Publishing.Publish(recipe, recipe.Nodes.OfType<RegionStatsNode>().Single(), PublishedKind.Result, "Mean", "Mean");
            GraphSerializer.Save(recipe, path);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

            designer.Refresh();

            Assert.False(value.HasNoChoices);
            Assert.Contains(value.Choices, c => c.Name == "Mean");
        });
    }

    [Fact]
    public void A_name_not_published_is_said_and_listed()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (_, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);
            designer.Save();
            Assert.False(designer.HasProblems, designer.ProblemText);

            var value = designer.Add(WidgetKind.Value);
            value.Widget.Name = "Diameter";
            value.RefreshChoices();
            designer.Changed();

            Assert.NotNull(value.NameProblem);
            Assert.True(designer.HasProblems);
            Assert.Contains("Diameter", designer.ProblemText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Properties_set_are_undoable()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (_, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);
            var button = designer.Add(WidgetKind.Button);

            button.Action = WidgetAction.RunStop;
            button.Text = "Go";
            button.FontSize = 22;

            Assert.Equal("Go  ·  all stations", button.Caption);

            designer.UndoCommand.Execute(null);
            designer.UndoCommand.Execute(null);

            var back = designer.Widgets.Last();
            Assert.Equal(WidgetAction.RunStop, back.Action);
            Assert.Null(back.Text);
        });
    }

    [Fact]
    public void Reset_to_standard_is_undoable()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (_, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);
            var count = designer.Widgets.Count;

            designer.Add(WidgetKind.Logo);
            designer.ResetToStandard();
            Assert.Equal(count, designer.Widgets.Count);

            designer.UndoCommand.Execute(null);
            Assert.Equal(count + 1, designer.Widgets.Count);
        });
    }

    /// <summary>The menu's Reset to Standard asks first: answered No, the design stays.</summary>
    [Fact]
    public void Reset_to_standard_from_the_menu_asks_first()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (_, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);
            var asked = 0;
            var answer = false;
            designer.Confirm = _ => { asked++; return answer; };

            designer.Add(WidgetKind.Logo);
            var count = designer.Widgets.Count;

            designer.ResetCommand.Execute(null);
            Assert.Equal(1, asked);
            Assert.Equal(count, designer.Widgets.Count);

            answer = true;
            designer.ResetCommand.Execute(null);
            Assert.Equal(count - 1, designer.Widgets.Count);
        });
    }

    /// <summary>Builder's window builds - its stations, the designer's templates, the palette, the properties - for a real project.</summary>
    [Fact]
    public void The_builder_window_builds()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var (project, _) = TwoStations();
            var model = new BuilderViewModel { RememberProjects = false };
            model.Show(project);

            var window = new BuilderWindow(model) { KeepsLayout = false, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = -20000, Top = -20000 };

            try
            {
                window.Show();
                WpfTestHost.Settle(window.Dispatcher);

                Assert.Equal("*Line - Sirius Builder", model.Title);
                Assert.Equal(2, model.Stations.Count);
                Assert.NotEmpty(model.Designer!.Widgets);
                Assert.Same(model, window.Model);

                // The property grid follows the selection.
                Assert.Equal("Screen", model.Designer.PropertyTitle);
                model.Designer.Selected = model.Designer.Widgets.First(widget => widget.Kind == WidgetKind.Value);
                WpfTestHost.Settle(window.Dispatcher);
                Assert.Equal("Value", model.Designer.PropertyTitle);
                Assert.Contains(model.Designer.PropertyCategories, category => category.Name == "Shows");

                window.ShowPanel("properties");
            }
            finally
            {
                model.Designer!.Save();
                window.Close();
                WpfTestHost.Settle(window.Dispatcher);
            }
        });
    }

    /// <summary>
    /// Builder's About is Builder's: its own name, logo and version, 1.0.1,
    /// not Studio's 1.0.x, which the programs no longer share.
    /// </summary>
    [Fact]
    public void Builder_has_an_about_of_its_own()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var window = new BuilderWindow(new BuilderViewModel { RememberProjects = false }) { KeepsLayout = false };
            var about = window.About();

            try
            {
                Assert.Equal("About Sirius Builder", about.Title);
                Assert.Equal("Sirius Builder", about.ProductText.Text);
                Assert.Equal("version 1.0.1", about.VersionText.Text);
                Assert.Same(System.Windows.Application.Current!.FindResource("Icon.SiriusBuilder"), about.LogoImage.Source);

                // Studio's own is unchanged.
                var studio = new Sirius.Studio.App.AboutWindow();
                Assert.Equal("About Sirius Studio Plus", studio.Title);
                Assert.Equal("Sirius Studio Plus", studio.ProductText.Text);
                Assert.NotEqual("version 1.0.1", studio.VersionText.Text);
                studio.Close();

                var menu = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Sirius.Builder", "BuilderWindow.xaml"));
                Assert.Contains("_About Sirius Builder…", menu, StringComparison.Ordinal);
            }
            finally
            {
                about.Close();
                window.Close();
            }
        });
    }

    // ---- the property grid ---------------------------------------------------

    [Fact]
    public void Cut_copy_and_paste_a_widget()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (_, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);
            var count = designer.Widgets.Count;
            var value = designer.Add(WidgetKind.Value, 100, 100);

            designer.CopyCommand.Execute(null);
            Assert.True(designer.PasteCommand.CanExecute(null));

            // Each paste a step further on.
            designer.PasteCommand.Execute(null);
            Assert.Equal((120, 120), (designer.Selected!.X, designer.Selected.Y));
            designer.PasteCommand.Execute(null);
            Assert.Equal((140, 140), (designer.Selected!.X, designer.Selected.Y));
            Assert.Equal(count + 3, designer.Widgets.Count);

            // Cut, then paste: back where it was.
            designer.Selected = value;
            designer.CutCommand.Execute(null);
            Assert.DoesNotContain(value, designer.Widgets);
            designer.PasteCommand.Execute(null);
            Assert.Equal((100, 100), (designer.Selected!.X, designer.Selected.Y));
            Assert.Equal(count + 3, designer.Widgets.Count);

            designer.UndoCommand.Execute(null);
            Assert.Equal(count + 2, designer.Widgets.Count);
        });
    }

    [Fact]
    public void Arrange_moves_a_widget_one_step_or_to_the_end()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (_, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);
            var widget = designer.Add(WidgetKind.Text);
            var last = designer.Widgets.Count - 1;

            Assert.False(designer.BringForwardCommand.CanExecute(null));
            Assert.False(designer.BringToFrontCommand.CanExecute(null));

            designer.SendBackwardCommand.Execute(null);
            Assert.Equal(last - 1, designer.Widgets.IndexOf(designer.Selected!));

            designer.SendToBackCommand.Execute(null);
            Assert.Equal(0, designer.Widgets.IndexOf(designer.Selected!));
            Assert.False(designer.SendBackwardCommand.CanExecute(null));

            designer.BringForwardCommand.Execute(null);
            Assert.Equal(1, designer.Widgets.IndexOf(designer.Selected!));

            designer.BringToFrontCommand.Execute(null);
            Assert.Equal(last, designer.Widgets.IndexOf(designer.Selected!));
            Assert.Same(widget, designer.Selected);
        });
    }

    [Fact]
    public void A_widget_lists_only_the_properties_of_its_kind()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (_, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);

            var value = designer.Add(WidgetKind.Value);
            Assert.Equal(["Widget", "Shows", "Text", "Layout"], value.Categories.Select(category => category.Name));

            var logo = designer.Add(WidgetKind.Logo);
            Assert.Equal(["Widget", "Logo", "Layout"], logo.Categories.Select(category => category.Name));
            Assert.Equal(PropertyEditor.File, logo.Categories[1].Rows.Single().Editor);

            var setting = designer.Add(WidgetKind.Setting);
            Assert.Contains(setting.Categories, category => category.Name == "On the line");
        });
    }

    /// <summary>
    /// A value's label goes beside it or above it, as a setting's does, and
    /// above makes the widget tall enough for two lines.
    /// </summary>
    [Fact]
    public void A_value_puts_its_label_beside_or_above()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (_, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);

            var value = designer.Add(WidgetKind.Value);
            var row = value.Categories.Single(c => c.Name == "Text").Rows.Single(r => r.Name == "Label position");
            Assert.Equal(LabelPlacement.Left, row.Value);

            row.Value = LabelPlacement.Top;
            Assert.Equal(LabelPlacement.Top, value.Widget.LabelPlacement);
            Assert.Equal(DesignWidgetViewModel.TwoLineHeight(value.Widget.FontSize), value.Height);

            // Only the kinds with a label have the choice.
            var text = designer.Add(WidgetKind.Text);
            Assert.DoesNotContain(text.Categories.SelectMany(c => c.Rows), r => r.Name == "Label position");
        });
    }

    /// <summary>
    /// A setting's control - − and +, slider, box, range - and its label's
    /// place are chosen in the property grid. A range takes a second setting
    /// for its high end, and its limits are the slider's two ends.
    /// </summary>
    [Fact]
    public void A_setting_chooses_its_control_and_where_its_label_goes()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var recipe = PublishedRecipe();
            Publishing.Publish(recipe, recipe.Nodes.OfType<ThresholdNode>().Single(), PublishedKind.Setting, nameof(ThresholdNode.MaxValue), "Ceiling");

            var project = Project.Create(_folder, "Line", recipe, "Top");
            var designer = new ScreenDesignerViewModel(new StationRecipes(project));

            var widget = designer.Add(WidgetKind.Setting);
            widget.StationValue = "Top";
            widget.Choice = widget.Choices.Single(c => c.Name == "Level");

            Row Find(string category, string name) =>
                new(widget.Categories.Single(c => c.Name == category).Rows.Where(row => row.Name == name && row.IsVisible).ToArray());

            var control = Find("Setting", "Control").Single;
            Assert.Equal(
                ["− value +", "Slider", "Box", "Range"],
                control.Options!.Cast<PropertyOption>().Select(option => option.Label));

            control.Value = SettingStyle.Slider;
            Assert.Equal(SettingStyle.Slider, widget.Widget.Style);
            Assert.Equal("Setting · Slider", widget.KindLabel);

            // Threshold's own 0 - 255 are both ends already: nothing to warn of.
            Assert.Empty(Find("On the line", "Control").Rows);

            // A range: the high setting is any other one the station publishes.
            control.Value = SettingStyle.Range;
            var high = Find("Setting", "High setting").Single;
            Assert.Equal(["Ceiling"], high.Options!.Cast<PropertyOption>().Select(option => (string)option.Value!));
            Assert.Contains("Choose the setting", widget.HighNameProblem, StringComparison.Ordinal);

            high.Value = "Ceiling";
            Assert.Equal("Ceiling", widget.Widget.HighName);
            Assert.Null(widget.HighNameProblem);
            Assert.Equal("Top › Level – Ceiling", widget.Caption);

            // Its ends: the low setting's lowest, the high setting's highest.
            var highest = Find("On the line", "Highest").Single;
            highest.Value = "200";
            Assert.Equal(new SettingLimit(null, 200), project.Stations[0].LimitFor("Ceiling"));
            Assert.Null(project.Stations[0].LimitFor("Level"));

            Find("On the line", "Lowest").Single.Value = "10";
            Assert.Equal(new SettingLimit(10, null), project.Stations[0].LimitFor("Level"));

            // The label above: the widget grows to two lines, on the grid.
            Assert.Equal(30, widget.Height);
            Find("Text", "Label position").Single.Value = LabelPlacement.Top;
            Assert.Equal(LabelPlacement.Top, widget.Widget.LabelPlacement);
            Assert.Equal(DesignWidgetViewModel.TwoLineHeight(widget.Widget.FontSize), widget.Height);
            Assert.Equal(0, widget.Height % ScreenLayout.Grid);

            // One undo puts both back.
            designer.UndoCommand.Execute(null);
            var undone = designer.Widgets.Last();
            Assert.Equal(LabelPlacement.Left, undone.Widget.LabelPlacement);
            Assert.Equal(30, undone.Height);
        });
    }

    /// <summary>A slider whose setting has no ends says so in the grid, and what to set.</summary>
    [Fact]
    public void A_slider_without_ends_says_what_to_set()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var recipe = PublishedRecipe();
            var check = new LimitCheckNode();
            recipe.Add(check);
            Publishing.Publish(recipe, check, PublishedKind.Setting, nameof(LimitCheckNode.Maximum), "Limit");

            var project = Project.Create(_folder, "Line", recipe, "Top");
            var designer = new ScreenDesignerViewModel(new StationRecipes(project));

            var widget = designer.Add(WidgetKind.Setting);
            widget.StationValue = "Top";
            widget.Choice = widget.Choices.Single(c => c.Name == "Limit");
            widget.Style = SettingStyle.Slider;

            Assert.Contains("A slider needs both ends", widget.StyleNote, StringComparison.Ordinal);

            widget.LowestText = "0";
            widget.HighestText = "500";
            Assert.Null(widget.StyleNote);
        });
    }

    /// <summary>The visible rows of one name in a category: one expected, or none.</summary>
    private sealed record Row(PropertyRow[] Rows)
    {
        public PropertyRow Single => Assert.Single(Rows);
    }

    [Fact]
    public void Property_rows_read_and_write_the_widget_undoably()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (_, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);
            var value = designer.Add(WidgetKind.Value, 100, 100);
            var rows = value.Categories.SelectMany(category => category.Rows).ToArray();

            var x = rows.Single(row => row.Name == "X");
            x.Value = "240";
            Assert.Equal(240, value.X);
            Assert.Equal("240", x.Value);

            // Refused: reads back as it was.
            x.Value = "left a bit";
            Assert.Equal("240", x.Value);

            var shows = rows.Single(row => row.Name == "Value");
            Assert.Contains(shows.Options!.Cast<PropertyOption>(), option => Equals(option.Value, "Mean"));
            shows.Value = "Mean";
            Assert.Equal("Mean", value.Widget.Name);

            var station = rows.Single(row => row.Name == "Station");
            station.Value = "Bottom";
            Assert.Equal("Bottom", value.Widget.Station);

            designer.UndoCommand.Execute(null);
            Assert.Equal("Top", designer.Widgets.Last().Widget.Station);
        });
    }

    [Fact]
    public void An_image_viewer_takes_a_background_colour()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (_, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);
            var viewer = designer.Add(WidgetKind.Image);
            var row = viewer.Categories.Single(category => category.Name == "Appearance").Rows.Single();

            Assert.Equal(PropertyEditor.Colour, row.Editor);
            Assert.Equal("Default", row.Value);
            Assert.Contains("White", row.Options!.Cast<string>());

            row.Value = "White";
            Assert.Equal("#FFFFFF", viewer.Widget.Background);
            Assert.Equal("White", row.Value);
            Assert.Equal(System.Windows.Media.Brushes.Black, viewer.PreviewForeground);

            row.Value = "#336699";
            Assert.Equal("#336699", viewer.Widget.Background);

            row.Value = "navy";
            Assert.Equal("#000080", viewer.Widget.Background);

            // Not a colour: refused, and said.
            row.Value = "sky-ish";
            Assert.Equal("#000080", viewer.Widget.Background);
            Assert.Contains("not a colour", designer.StatusText, StringComparison.Ordinal);

            row.Value = "Default";
            Assert.Null(viewer.Widget.Background);

            // No other kind has one.
            Assert.DoesNotContain(designer.Add(WidgetKind.Value).Categories, category => category.Name == "Appearance");
        });
    }

    [Fact]
    public void A_running_image_viewer_shows_its_background()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var plain = new ImageWidgetViewModel(new Widget { Kind = WidgetKind.Image });
            var white = new ImageWidgetViewModel(new Widget { Kind = WidgetKind.Image, Background = "#FFFFFF" });

            Assert.Equal(System.Windows.Media.Color.FromRgb(0x17, 0x18, 0x1A), ((System.Windows.Media.SolidColorBrush)plain.Background).Color);
            Assert.Equal(System.Windows.Media.Colors.White, ((System.Windows.Media.SolidColorBrush)white.Background).Color);

            var path = Path.Combine(_folder, "colour.screen");
            new Screen { Widgets = { new Widget { Kind = WidgetKind.Image, Background = "#FFFFFF" } } }.Save(path);
            Assert.Equal("#FFFFFF", Screen.Load(path).Widgets[0].Background);
        });
    }

    [Fact]
    public void With_nothing_selected_the_grid_shows_the_screen()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (_, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);

            Assert.Null(designer.Selected);
            Assert.Same(designer.ScreenCategories, designer.PropertyCategories);

            var width = designer.ScreenCategories.Single().Rows.Single(row => row.Name == "Width");
            width.Value = "1920";

            Assert.Equal(1920, designer.Width);
        });
    }
}
