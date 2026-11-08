using System.IO;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Serialization;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>A project open in Studio: its stations, one recipe in the editor at a time.</summary>
[Collection(WpfCollection.Name)]
public sealed class ProjectStudioTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sirius-studio-project-" + Guid.NewGuid().ToString("N"));

    public ProjectStudioTests(WpfTestHost host)
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

    /// <summary>Two stations, the first with one Threshold, the second with two.</summary>
    private Project TwoStations()
    {
        var first = new VisionGraph();
        first.Add(new ThresholdNode());

        var project = Project.Create(_root, "Line 3", first, "Top");

        var second = new VisionGraph();
        second.Add(new ThresholdNode());
        second.Add(new ThresholdNode());
        project.AddStation("Bottom", second);

        return project;
    }

    [Fact]
    public void Opening_a_project_opens_its_first_station()
    {
        var project = TwoStations();

        WpfTestHost.OnStaThread(() =>
        {
            var model = new MainViewModel();

            Assert.True(model.OpenProjectFile(project.FilePath));

            Assert.NotNull(model.Project);
            Assert.Equal("Top", model.Project!.ActiveStation?.Name);
            Assert.Single(model.MainDocument.Graph.Nodes);
            Assert.Equal("Sirius Studio Plus - Line 3 › Top", model.Title);
            Assert.True(model.CloseProjectCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Opening_another_station_puts_its_recipe_in_the_editor()
    {
        var project = TwoStations();

        WpfTestHost.OnStaThread(() =>
        {
            var model = new MainViewModel();
            model.OpenProjectFile(project.FilePath);

            var bottom = model.Project!.Stations.Single(station => station.Name == "Bottom");
            model.Project.OpenStationCommand.Execute(bottom);

            Assert.Same(bottom, model.Project.ActiveStation);
            Assert.True(bottom.IsActive);
            Assert.False(model.Project.Stations[0].IsActive);
            Assert.Equal(2, model.MainDocument.Graph.Nodes.Count);
            Assert.EndsWith("› Bottom", model.Title, StringComparison.Ordinal);
        });
    }

    /// <summary>Saving writes the station's own recipe inside the project.</summary>
    [Fact]
    public void Saving_writes_the_stations_recipe()
    {
        var project = TwoStations();

        WpfTestHost.OnStaThread(() =>
        {
            var model = new MainViewModel();
            model.OpenProjectFile(project.FilePath);

            model.MainDocument.Graph.Add(new ThresholdNode());
            model.SaveGraphCommand.Execute(null);

            Assert.Equal(2, GraphSerializer.Load(project.RecipePath(project.Stations[0])).Graph.Nodes.Count);
            Assert.False(model.IsModified);
        });
    }

    /// <summary>A recipe saved under another name is no longer the station's.</summary>
    [Fact]
    public void Saving_elsewhere_leaves_the_station()
    {
        var project = TwoStations();

        WpfTestHost.OnStaThread(() =>
        {
            var model = new MainViewModel();
            model.OpenProjectFile(project.FilePath);

            model.Write(Path.Combine(_root, "copy.sgraph"));

            Assert.NotNull(model.Project);
            Assert.Null(model.Project!.ActiveStation);
            Assert.Equal("Sirius Studio Plus - copy.sgraph", model.Title);
        });
    }

    [Fact]
    public void A_renamed_station_is_shown_by_its_new_name()
    {
        var project = TwoStations();

        WpfTestHost.OnStaThread(() =>
        {
            var model = new MainViewModel();
            model.OpenProjectFile(project.FilePath);

            model.Project!.Project.RenameStation(model.Project.Project.Stations[0], "Lid");
            model.RefreshTitle();

            Assert.Equal("Lid", model.Project.Stations[0].Name);
            Assert.Same(model.Project.Stations[0], model.Project.ActiveStation);
            Assert.EndsWith("› Lid", model.Title, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Removing the station that is open takes its recipe out of the editor
    /// too, and opens the next one.
    /// </summary>
    [Fact]
    public void Removing_the_open_station_opens_the_next()
    {
        var project = TwoStations();

        WpfTestHost.OnStaThread(() =>
        {
            var model = new MainViewModel();
            model.OpenProjectFile(project.FilePath);

            model.Project!.RemoveConfirmed(model.Project.Stations[0]);

            var left = Assert.Single(model.Project.Stations);
            Assert.Same(left, model.Project.ActiveStation);
            Assert.Equal(2, model.MainDocument.Graph.Nodes.Count);
            Assert.EndsWith("› Bottom", model.Title, StringComparison.Ordinal);
        });
    }

    /// <summary>With no station left, the editor is left empty.</summary>
    [Fact]
    public void Removing_the_last_station_empties_the_editor()
    {
        var project = Project.Create(_root, "Solo", null, "Only");

        WpfTestHost.OnStaThread(() =>
        {
            var model = new MainViewModel();
            model.OpenProjectFile(project.FilePath);
            model.MainDocument.Graph.Add(new ThresholdNode());
            model.SaveGraphCommand.Execute(null);

            model.Project!.RemoveConfirmed(model.Project.Stations[0]);

            Assert.Empty(model.Project.Stations);
            Assert.Null(model.Project.ActiveStation);
            Assert.Empty(model.MainDocument.Graph.Nodes);
            Assert.Equal("Sirius Studio Plus - Solo", model.Title);
        });
    }

    /// <summary>Removing another station leaves the open one where it is.</summary>
    [Fact]
    public void Removing_another_station_keeps_the_open_one()
    {
        var project = TwoStations();

        WpfTestHost.OnStaThread(() =>
        {
            var model = new MainViewModel();
            model.OpenProjectFile(project.FilePath);
            var top = model.Project!.ActiveStation!;

            model.Project.RemoveConfirmed(model.Project.Stations[1]);

            Assert.Equal(top.Station, model.Project.ActiveStation?.Station);
            Assert.Single(model.MainDocument.Graph.Nodes);
        });
    }

    /// <summary>A station whose recipe has gone is listed, marked, and not opened first.</summary>
    [Fact]
    public void A_station_with_a_missing_recipe_is_marked_and_skipped()
    {
        var project = TwoStations();
        File.Delete(project.RecipePath(project.Stations[0]));

        WpfTestHost.OnStaThread(() =>
        {
            var model = new MainViewModel();
            var loaded = Project.Load(project.FilePath);

            Assert.NotEmpty(loaded.Warnings);

            // Opened through the view-model the shell uses, without the
            // warning dialog that OpenProjectFile would show.
            var view = new ProjectViewModel(loaded.Project, model);
            view.OpenFirst();

            Assert.True(view.Stations[0].RecipeMissing);
            Assert.Equal("Bottom", view.ActiveStation?.Name);
        });
    }
}
