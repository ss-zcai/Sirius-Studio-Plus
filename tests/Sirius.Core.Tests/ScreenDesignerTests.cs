using System.IO;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Screens;
using Sirius.Core.Serialization;
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

    /// <summary>A project with stations Top and Bottom, Top open in the "editor".</summary>
    private (Project Project, VisionGraph Editor, StationRecipes Recipes) TwoStations()
    {
        var project = Project.Create(_folder, "Line", Recipe(), "Top");
        project.AddStation("Bottom", Recipe());

        var editor = GraphSerializer.Load(project.RecipePath(project.Stations[0])).Graph;
        var recipes = new StationRecipes(project, () => project.Stations[0], () => editor);

        return (project, editor, recipes);
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
            var (_, _, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);

            Assert.Contains(designer.Widgets, w => w.Widget.Station == "Bottom");
            Assert.True(designer.IsModified, "a screen not yet saved is unsaved");
        });
    }

    [Fact]
    public void Saving_makes_the_screen_the_SIs_own()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (project, _, recipes) = TwoStations();
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
            var (_, _, recipes) = TwoStations();
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
            var (_, _, recipes) = TwoStations();
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
            var (_, _, recipes) = TwoStations();
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
            var (_, _, recipes) = TwoStations();
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

    /// <summary>
    /// Binding on a station not in the editor publishes in its recipe file,
    /// which nothing else has open.
    /// </summary>
    [Fact]
    public void Binding_on_another_station_publishes_in_its_file()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (project, _, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);
            var value = designer.Add(WidgetKind.Value);

            value.StationValue = "Bottom";
            value.Choice = value.Choices.Single(c => c.Source == "Region Stats › Mean");

            Assert.Equal("Mean", value.Widget.Name);
            Assert.Null(value.NameProblem);

            var file = GraphSerializer.Load(project.RecipePath(project.Stations[1])).Graph;
            Assert.NotNull(Publishing.Find(file, "Mean"));
        });
    }

    /// <summary>On the station in the editor, it publishes there, to be saved with the rest.</summary>
    [Fact]
    public void Binding_on_the_open_station_publishes_in_the_editor()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (project, editor, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);
            var picture = designer.Add(WidgetKind.Image);

            picture.Choice = picture.Choices.Single(c => c.Kind == PublishedKind.View);

            Assert.Equal("Image Viewer", picture.Widget.Name);
            Assert.NotNull(Publishing.Find(editor, "Image Viewer"));

            var file = GraphSerializer.Load(project.RecipePath(project.Stations[0])).Graph;
            Assert.Null(Publishing.Find(file, "Image Viewer"));
        });
    }

    [Fact]
    public void A_name_not_published_is_said_and_listed()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var (_, _, recipes) = TwoStations();
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
            var (_, _, recipes) = TwoStations();
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
            var (_, _, recipes) = TwoStations();
            var designer = new ScreenDesignerViewModel(recipes);
            var count = designer.Widgets.Count;

            designer.Add(WidgetKind.Logo);
            designer.ResetToStandard();
            Assert.Equal(count, designer.Widgets.Count);

            designer.UndoCommand.Execute(null);
            Assert.Equal(count + 1, designer.Widgets.Count);
        });
    }

    /// <summary>The window builds - its templates, the palette, the properties - for a real project.</summary>
    [Fact]
    public void The_designer_window_builds()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var (_, _, recipes) = TwoStations();
            var window = ScreenDesignerWindow.Unshown(new ScreenDesignerViewModel(recipes));

            Assert.Equal("*Screen - Line", window.Model.Title);
            Assert.NotEmpty(window.Model.Widgets);
        });
    }
}
