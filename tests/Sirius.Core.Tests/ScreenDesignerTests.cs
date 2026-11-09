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

            var window = new BuilderWindow(model);

            Assert.Equal("*Line - Sirius Builder", model.Title);
            Assert.Equal(2, model.Stations.Count);
            Assert.NotEmpty(model.Designer!.Widgets);
            Assert.Same(model, window.Model);
        });
    }
}
