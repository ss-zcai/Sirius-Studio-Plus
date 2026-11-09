using System.IO;
using System.Windows;
using Sirius.Builder;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Core.Screens;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Sirius Builder: a project's stations and screen, apart from Studio
/// (decision 71).
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class BuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sirius-builder-" + Guid.NewGuid().ToString("N"));

    public BuilderTests(WpfTestHost host)
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

    /// <summary>A threshold with its level published as "Level".</summary>
    private static VisionGraph Recipe()
    {
        var graph = new VisionGraph();
        var threshold = new ThresholdNode();
        graph.Add(threshold);
        Publishing.Publish(graph, threshold, PublishedKind.Setting, nameof(ThresholdNode.Threshold), "Level");
        return graph;
    }

    private Project TwoStations()
    {
        var project = Project.Create(_root, "Line 3", Recipe(), "Top");
        project.AddStation("Bottom", Recipe());
        return project;
    }

    private static BuilderViewModel Builder(Project project)
    {
        var model = new BuilderViewModel
        {
            RememberProjects = false,
            Ask = (_, _) => MessageBoxResult.No,
        };

        model.Show(project);
        return model;
    }

    [Fact]
    public void A_project_shows_its_stations_and_its_screen()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Builder(TwoStations());

            Assert.True(model.HasProject);
            Assert.Equal(["Top", "Bottom"], model.Stations.Select(station => station.Name));
            Assert.Equal("Top.sgraph", model.Stations[0].RecipeFile);
            Assert.Equal("Top", model.SelectedStation?.Name);
            Assert.NotNull(model.Designer);
            Assert.True(model.TestRunCommand.CanExecute(null));

            Assert.True(model.CloseProject());
            Assert.False(model.HasProject);
            Assert.Null(model.Designer);
            Assert.Empty(model.Stations);
            Assert.Equal("Sirius Builder", model.Title);
        });
    }

    [Fact]
    public void A_new_project_has_no_station_until_one_is_added_from_a_recipe()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var project = Project.Create(_root, "Empty", firstRecipe: null, firstStation: null);
            Assert.Empty(project.Stations);
            Assert.True(File.Exists(project.FilePath));

            var model = Builder(project);
            Assert.True(model.HasNoStations);

            var recipe = Path.Combine(_root, "made in studio.sgraph");
            Sirius.Core.Serialization.GraphSerializer.Save(Recipe(), recipe);

            var added = model.AddFromRecipe("Camera 1", recipe);

            Assert.NotNull(added);
            Assert.False(model.HasNoStations);
            Assert.Equal("Camera 1", model.SelectedStation?.Name);
            Assert.True(File.Exists(project.RecipePath(added!)));
        });
    }

    [Fact]
    public void Renaming_a_station_renames_it_on_the_screen()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Builder(TwoStations());
            var designer = model.Designer!;
            Assert.Contains(designer.Widgets, widget => widget.Widget.Station == "Bottom");

            model.RenameStation(model.Stations[1], "Underside");

            Assert.Equal("Underside", model.Stations[1].Name);
            Assert.DoesNotContain(designer.Widgets, widget => widget.Widget.Station == "Bottom");
            Assert.Contains(designer.Widgets, widget => widget.Widget.Station == "Underside");
            Assert.False(designer.HasProblems, designer.ProblemText);

            // One step, undone as one.
            designer.UndoCommand.Execute(null);
            Assert.Contains(designer.Widgets, widget => widget.Widget.Station == "Bottom");
        });
    }

    [Fact]
    public void Removing_a_station_keeps_its_recipe_and_reports_the_screen()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var project = TwoStations();
            var model = Builder(project);
            var bottom = model.Stations[1];

            model.Designer!.Save();
            model.RemoveStation(bottom);

            Assert.Single(model.Stations);
            Assert.True(File.Exists(bottom.RecipePath));
            Assert.True(model.Designer.HasProblems);
            Assert.Contains("Bottom", model.Designer.ProblemText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Open_in_Studio_starts_Studio_on_the_recipe_or_says_why_not()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var model = Builder(TwoStations());
            var launched = new List<(string Program, string File)>();
            model.Launch = (program, file) =>
            {
                launched.Add((program, file));
                return true;
            };

            var studio = Path.Combine(AppContext.BaseDirectory, "SiriusStudioPlus.exe");

            if (File.Exists(studio))
            {
                Assert.True(model.OpenInStudio(model.Stations[1]));
                Assert.Equal((studio, model.Stations[1].RecipePath), launched.Single());
            }
            else
            {
                // A test host without Studio's program beside it: said, not started.
                var warned = Capture(() => Assert.False(model.OpenInStudio(model.Stations[1])));
                Assert.Contains("not beside", warned, StringComparison.Ordinal);
                Assert.Empty(launched);
            }
        });
    }

    [Fact]
    public void Line_changes_are_counted_per_station()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var project = TwoStations();

            using (var runner = StationRunner.Open(project, project.Stations[1]))
                Assert.Null(runner.ChangeSetting("Level", 140));

            var model = Builder(project);

            Assert.False(model.Stations[0].HasLineChanges);
            Assert.True(model.Stations[1].HasLineChanges);
            Assert.Equal("1 changed on the line", model.Stations[1].LineChangesText);
        });
    }

    [Fact]
    public void A_limit_from_the_project_holds_the_station_on_the_line()
    {
        var project = TwoStations();
        project.SetLimit(project.Stations[0], "Level", 100, 150);

        using var top = StationRunner.Open(Project.Load(project.FilePath).Project, project.Stations[0]);
        using var bottom = StationRunner.Open(project, project.Stations[1]);

        Assert.Contains("between 100 and 150", top.ChangeSetting("Level", 200), StringComparison.Ordinal);
        Assert.Null(top.ChangeSetting("Level", 120));

        // Bottom has no limit of its own: the node's 0 - 255.
        Assert.Null(bottom.ChangeSetting("Level", 200));
    }

    [Fact]
    public void Studio_says_when_a_recipe_has_settings_changed_on_the_line()
    {
        var project = TwoStations();
        var path = project.RecipePath(project.Stations[0]);

        Assert.Null(MainViewModel.LineChangesNote(path));

        using (var runner = StationRunner.Open(project, project.Stations[0]))
            runner.ChangeSetting("Level", 140);

        var note = MainViewModel.LineChangesNote(path);

        Assert.NotNull(note);
        Assert.Contains("Level 140", note, StringComparison.Ordinal);
        Assert.Contains("Sirius Builder", note, StringComparison.Ordinal);
    }

    /// <summary>Runs <paramref name="action"/>, returning what it said in a message box instead of showing one.</summary>
    private static string Capture(Action action)
    {
        var said = string.Empty;
        var previous = Sirius.Studio.App.Dialog.Present;
        Sirius.Studio.App.Dialog.Present = (_, message, _, _, _) =>
        {
            said = message;
            return MessageBoxResult.OK;
        };

        try
        {
            action();
        }
        finally
        {
            Sirius.Studio.App.Dialog.Present = previous;
        }

        return said;
    }
}
