using System.IO;
using Sirius.Core.Nodes;
using Sirius.Studio.App.ViewModels;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Where custom node libraries come from, and what the application
/// remembers about them between sessions.
/// </summary>
public class CustomNodesViewModelTests
{
    public CustomNodesViewModelTests() => TestCatalog.Ensure();

    private sealed record Folders(string Settings, string Shipped, string User)
    {
        public CustomNodesViewModel Model() => new(Settings, Shipped, User);
    }

    private static Folders Create()
    {
        var root = CustomNodeTests.Scratch();
        var shipped = Path.Combine(root, "shipped");
        var user = Path.Combine(root, "user");

        Directory.CreateDirectory(shipped);
        Directory.CreateDirectory(user);

        return new Folders(Path.Combine(root, "custom-nodes.json"), shipped, user);
    }

    [Fact]
    public void A_library_in_a_plugins_folder_loads_without_being_remembered()
    {
        var folders = Create();
        CustomNodeTests.SampleCopy(folders.Shipped);

        var model = folders.Model();
        model.LoadAll();

        var row = Assert.Single(model.Libraries);
        Assert.Equal(CustomNodeSource.Shipped, row.Source);
        Assert.False(row.CanRemove);
        Assert.Empty(model.Remembered);
    }

    /// <summary>
    /// A library chosen by hand is the one the operator would have to go
    /// and find again, so it is the one worth remembering.
    /// </summary>
    [Fact]
    public void A_library_added_by_hand_is_there_next_time()
    {
        var folders = Create();
        var path = CustomNodeTests.SampleCopy();

        var row = folders.Model().Add(path);

        Assert.Equal(CustomNodeSource.Added, row.Source);
        Assert.True(row.CanRemove);

        var next = folders.Model();
        Assert.Equal(new[] { path }, next.Remembered);

        next.LoadAll();
        Assert.Equal(path, Assert.Single(next.Libraries).Path);
    }

    [Fact]
    public void Adding_a_file_from_a_plugins_folder_does_not_remember_it_twice()
    {
        var folders = Create();
        var path = CustomNodeTests.SampleCopy(folders.User);

        var row = folders.Model().Add(path);

        Assert.Equal(CustomNodeSource.PerUser, row.Source);
        Assert.Empty(folders.Model().Remembered);
    }

    [Fact]
    public void Removing_forgets_it_and_waits_for_a_restart()
    {
        var folders = Create();
        var model = folders.Model();
        var row = model.Add(CustomNodeTests.SampleCopy());

        model.Remove(row);

        Assert.True(row.IsRemoved);
        Assert.False(row.CanRemove);
        Assert.True(model.NeedsRestart);
        Assert.Empty(folders.Model().Remembered);
    }

    /// <summary>
    /// A library that moved should be noticed, not quietly dropped along
    /// with every node it provided.
    /// </summary>
    [Fact]
    public void A_remembered_library_that_has_gone_is_listed_with_the_reason()
    {
        // As the last session left it: a library remembered, and since moved.
        var folders = Create();
        var gone = Path.Combine(CustomNodeTests.Scratch(), "Moved.dll");
        File.WriteAllText(
            folders.Settings,
            System.Text.Json.JsonSerializer.Serialize(new { Libraries = new[] { gone } }));

        var model = folders.Model();
        model.LoadAll();

        var row = Assert.Single(model.Libraries);
        Assert.True(row.Failed);
        Assert.Contains("does not exist", Assert.Single(row.Problems));
        Assert.NotNull(model.ProblemSummary);
    }

    [Fact]
    public void A_damaged_settings_file_starts_with_nothing_remembered()
    {
        var folders = Create();
        File.WriteAllText(folders.Settings, "{ not json");

        Assert.Empty(folders.Model().Remembered);
    }

    [Fact]
    public void A_librarys_nodes_reach_the_toolbox()
    {
        _ = CustomNodeTests.InstalledSample.Library;

        var folders = Create();
        var model = new MainViewModel(folders.Model());

        var custom = model.Palette.Single(group => group.Category == NodeCategory.Custom);
        Assert.Contains(custom.Items, item => item.DisplayName == "Gamma");

        var analysis = model.Palette.Single(group => group.Category == NodeCategory.Analysis);
        Assert.Contains(analysis.Items, item => item.DisplayName == "Bright Region");
    }

    /// <summary>
    /// Adding a library rebuilds the toolbox, which must not reopen every
    /// group the operator had closed.
    /// </summary>
    [Fact]
    public void Rebuilding_the_toolbox_keeps_closed_groups_closed()
    {
        var model = new MainViewModel(Create().Model());

        model.Palette.Single(group => group.Category == NodeCategory.Filtering).IsExpanded = false;

        // Called directly: whether adding a library adds anything depends on
        // what this test run has installed already, and the catalogue is
        // process-wide.
        model.RebuildPalette();

        Assert.False(model.Palette.Single(group => group.Category == NodeCategory.Filtering).IsExpanded);
        Assert.True(model.Palette.Single(group => group.Category == NodeCategory.Basic).IsExpanded);
    }
}
