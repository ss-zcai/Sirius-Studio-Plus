using System.IO;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Serialization;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Projects: a folder of stations, each running a recipe kept inside it
/// (runtime design, section 0).
/// </summary>
public sealed class ProjectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sirius-project-" + Guid.NewGuid().ToString("N"));

    public ProjectTests()
    {
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

    private static VisionGraph OneNode()
    {
        var graph = new VisionGraph();
        graph.Add(new ThresholdNode { Threshold = 77 });
        return graph;
    }

    [Fact]
    public void A_new_project_is_a_folder_with_a_file_and_one_station()
    {
        var project = Project.Create(_root, "Line 3");

        Assert.Equal(Path.Combine(_root, "Line 3", "Line 3.sproj"), project.FilePath);
        Assert.True(File.Exists(project.FilePath));
        Assert.Equal("Line 3", project.Name);

        var station = Assert.Single(project.Stations);
        Assert.Equal("Station 1", station.Name);
        Assert.Equal("recipes/Station 1.sgraph", station.Recipe);
        Assert.True(File.Exists(project.RecipePath(station)));
    }

    /// <summary>The recipe open when the project was made becomes its first station's.</summary>
    [Fact]
    public void The_first_station_can_start_from_the_open_recipe()
    {
        var project = Project.Create(_root, "Line 3", OneNode(), "Top Camera");

        var station = Assert.Single(project.Stations);
        var recipe = GraphSerializer.Load(project.RecipePath(station)).Graph;

        Assert.Equal("Top Camera", station.Name);
        Assert.Equal(77, Assert.IsType<ThresholdNode>(Assert.Single(recipe.Nodes)).Threshold);
    }

    [Fact]
    public void A_project_is_not_made_over_a_folder_with_something_in_it()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Taken"));
        File.WriteAllText(Path.Combine(_root, "Taken", "notes.txt"), "mine");

        Assert.Throws<IOException>(() => Project.Create(_root, "Taken"));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_root, "Taken", "notes.txt")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("what?")]
    [InlineData("dot.")]
    public void Unusable_project_names_are_refused(string name) =>
        Assert.NotNull(Project.FileNameProblem(name));

    [Fact]
    public void Stations_survive_closing_and_opening()
    {
        var project = Project.Create(_root, "Line 3");
        project.AddStation("Bottom Camera", OneNode());
        project.RenameStation(project.Stations[0], "Top Camera");

        var loaded = Project.Load(project.FilePath);

        Assert.Empty(loaded.Warnings);
        Assert.Equal(
            ["Top Camera", "Bottom Camera"],
            loaded.Project.Stations.Select(station => station.Name).ToArray());
        Assert.Equal(
            ["recipes/Station 1.sgraph", "recipes/Bottom Camera.sgraph"],
            loaded.Project.Stations.Select(station => station.Recipe).ToArray());
    }

    [Fact]
    public void Station_names_are_unique_ignoring_case()
    {
        var project = Project.Create(_root, "Line 3");

        Assert.NotNull(project.StationNameProblem("station 1"));
        Assert.Null(project.StationNameProblem("station 1", project.Stations[0]));
        Assert.Throws<ArgumentException>(() => project.AddStation("STATION 1", new VisionGraph()));
        Assert.Equal("Station 2", project.SuggestStationName());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("A › B")]
    public void Unusable_station_names_are_refused(string name) =>
        Assert.NotNull(Project.Create(_root, "P" + Guid.NewGuid().ToString("N")[..6]).StationNameProblem(name));

    /// <summary>
    /// Two stations whose names make the same file - or a recipe left behind
    /// by a removed station - never share or overwrite one.
    /// </summary>
    [Fact]
    public void A_new_station_never_overwrites_a_recipe_file()
    {
        var project = Project.Create(_root, "Line 3");
        var first = project.Stations[0];
        File.WriteAllText(project.RecipePath(first), "kept");

        project.RemoveStation(first);
        var again = project.AddStation("Station 1", new VisionGraph());

        Assert.Equal("recipes/Station 1 (2).sgraph", again.Recipe);
        Assert.Equal("kept", File.ReadAllText(Path.Combine(project.Folder, "recipes", "Station 1.sgraph")));
    }

    /// <summary>Removing a station tidies the list and deletes no work.</summary>
    [Fact]
    public void Removing_a_station_keeps_its_recipe_file()
    {
        var project = Project.Create(_root, "Line 3");
        var station = project.Stations[0];

        project.RemoveStation(station);

        Assert.Empty(Project.Load(project.FilePath).Project.Stations);
        Assert.True(File.Exists(project.RecipePath(station)));
    }

    /// <summary>An imported recipe is copied in, so the folder is complete on its own.</summary>
    [Fact]
    public void An_imported_recipe_is_copied_into_the_project()
    {
        var outside = Path.Combine(_root, "cap check.sgraph");
        GraphSerializer.Save(OneNode(), outside);

        var project = Project.Create(_root, "Line 3");
        var station = project.ImportStation("Caps", outside);

        Assert.Equal("recipes/cap check.sgraph", station.Recipe);
        Assert.True(File.Exists(project.RecipePath(station)));

        File.Delete(outside);
        Assert.Single(GraphSerializer.Load(project.RecipePath(station)).Graph.Nodes);
    }

    [Fact]
    public void Stations_can_be_reordered()
    {
        var project = Project.Create(_root, "Line 3");
        var second = project.AddStation("Second", new VisionGraph());

        project.MoveStation(second, -1);

        Assert.Equal(
            ["Second", "Station 1"],
            Project.Load(project.FilePath).Project.Stations.Select(station => station.Name).ToArray());
    }

    [Fact]
    public void A_missing_recipe_is_reported_and_the_station_kept()
    {
        var project = Project.Create(_root, "Line 3");
        File.Delete(project.RecipePath(project.Stations[0]));

        var loaded = Project.Load(project.FilePath);

        Assert.Single(loaded.Project.Stations);
        Assert.Contains(loaded.Warnings, warning => warning.Contains("missing", StringComparison.Ordinal));
    }

    /// <summary>A hand-edited file cannot point a station outside the folder.</summary>
    [Fact]
    public void A_recipe_outside_the_folder_is_refused()
    {
        var project = Project.Create(_root, "Line 3");
        File.WriteAllText(
            project.FilePath,
            """{ "version": 1, "stations": [ { "name": "Away", "recipe": "../elsewhere.sgraph" } ] }""");

        var loaded = Project.Load(project.FilePath);

        Assert.Empty(loaded.Project.Stations);
        Assert.Contains(loaded.Warnings, warning => warning.Contains("outside", StringComparison.Ordinal));
    }

    [Fact]
    public void A_newer_format_is_refused()
    {
        var project = Project.Create(_root, "Line 3");
        File.WriteAllText(project.FilePath, """{ "version": 99, "stations": [] }""");

        Assert.Throws<InvalidDataException>(() => Project.Load(project.FilePath));
    }

    [Fact]
    public void Changes_are_announced()
    {
        var project = Project.Create(_root, "Line 3");
        var raised = 0;
        project.Changed += (_, _) => raised++;

        var station = project.AddStation("Second", new VisionGraph());
        project.RenameStation(station, "Bottom");
        project.MoveStation(station, -1);
        project.RemoveStation(station);

        Assert.Equal(4, raised);
    }
}
