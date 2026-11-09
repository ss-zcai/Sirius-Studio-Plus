using System.IO;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Core.Serialization;
using Sirius.Studio.App.ViewModels;
using Xunit;

namespace Sirius.Core.Tests;

public enum TunableTestMode
{
    Dark,
    Bright,
}

/// <summary>
/// A node with one of each kind of setting, which says what its Level was
/// when it ran - and can be held mid-inspection.
/// </summary>
[VisionNode("Test Tunable", TypeKey = "test.tunable", Category = NodeCategory.Source)]
public sealed class TestTunableNode : VisionNode
{
    private double _level = 50;
    private int _count = 3;
    private bool _enabled = true;
    private TunableTestMode _mode = TunableTestMode.Dark;
    private string _label = "x";

    public TestTunableNode()
    {
        LevelOut = AddOutput<double>("Seen");
    }

    public OutputPort LevelOut { get; }

    /// <summary>When set, an evaluation waits for <see cref="Release"/>.</summary>
    public ManualResetEventSlim? Hold { get; set; }

    public ManualResetEventSlim Started { get; } = new(false);

    public ManualResetEventSlim Release { get; } = new(false);

    [Param("Level", Min = 0, Max = 100)]
    public double Level
    {
        get => _level;
        set => SetParam(ref _level, value);
    }

    [Param("Count", Min = 1, Max = 9, OddOnly = true)]
    public int Count
    {
        get => _count;
        set => SetParam(ref _count, value);
    }

    [Param("Use It")]
    public bool UseIt
    {
        get => _enabled;
        set => SetParam(ref _enabled, value);
    }

    [Param("Mode")]
    public TunableTestMode Mode
    {
        get => _mode;
        set => SetParam(ref _mode, value);
    }

    [Param("Label")]
    public string Label
    {
        get => _label;
        set => SetParam(ref _label, value);
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var level = _level;

        if (Hold is not null)
        {
            Started.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
        }

        context.Set(LevelOut, level);
    }
}

/// <summary>
/// Settings changed on the line: their range, the file of changed values,
/// and a station taking a change between inspections (runtime MVP step 6).
/// </summary>
public sealed class LineSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-line-" + Guid.NewGuid().ToString("N"));

    public LineSettingsTests()
    {
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

    /// <summary>The tunable node, each setting published under its own name, Level limited to 10 - 60.</summary>
    private static (VisionGraph Recipe, TestTunableNode Node) Recipe()
    {
        var graph = new VisionGraph();
        var node = new TestTunableNode();
        graph.Add(node);

        Publishing.Publish(graph, node, PublishedKind.Setting, nameof(TestTunableNode.Level), "Level", 10, 60);
        Publishing.Publish(graph, node, PublishedKind.Setting, nameof(TestTunableNode.Count), "Count");
        Publishing.Publish(graph, node, PublishedKind.Setting, nameof(TestTunableNode.UseIt), "Use It");
        Publishing.Publish(graph, node, PublishedKind.Setting, nameof(TestTunableNode.Mode), "Mode");
        Publishing.Publish(graph, node, PublishedKind.Setting, nameof(TestTunableNode.Label), "Label");
        Publishing.Publish(graph, node, PublishedKind.Result, "Seen", "Seen");

        return (graph, node);
    }

    /// <summary>Made with no synchronisation context, so the passes run where the test is.</summary>
    private static StationRunner Open(Project project)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return StationRunner.Open(project, project.Stations[0]);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static TestTunableNode NodeOf(StationRunner runner) => runner.Recipe.Nodes.OfType<TestTunableNode>().Single();

    // ---- the range ---------------------------------------------------------

    [Fact]
    public void A_setting_ranges_between_the_nodes_own_limits_and_its_published_ones()
    {
        var (recipe, node) = Recipe();
        var level = Tunable.Find(recipe, "Level")!;

        Assert.Equal(TunableKind.Number, level.Kind);
        Assert.Equal(0, level.NodeMinimum);
        Assert.Equal(100, level.NodeMaximum);
        Assert.Equal(10, level.Minimum);
        Assert.Equal(60, level.Maximum);

        // Limits wider than the node's own do not widen it.
        Publishing.Limit(node, nameof(TestTunableNode.Level), -5, 500);
        level = Tunable.Find(recipe, "Level")!;
        Assert.Equal(0, level.Minimum);
        Assert.Equal(100, level.Maximum);
        Assert.Equal("0 – 100", level.RangeText.Replace(" ", " ", StringComparison.Ordinal));
    }

    [Fact]
    public void Each_kind_of_setting_reads_what_a_screen_or_a_file_gives_it()
    {
        var (recipe, _) = Recipe();

        Assert.Null(Tunable.Find(recipe, "Level")!.Convert("42", out var level));
        Assert.Equal(42.0, level);
        Assert.Contains("between 10 and 60", Tunable.Find(recipe, "Level")!.Convert(75, out _), StringComparison.Ordinal);
        Assert.NotNull(Tunable.Find(recipe, "Level")!.Convert("lots", out _));

        var count = Tunable.Find(recipe, "Count")!;
        Assert.Equal(TunableKind.WholeNumber, count.Kind);
        Assert.Equal(2, count.Step);
        Assert.Null(count.Convert(4.6, out var five));
        Assert.Equal(5, five);
        Assert.Contains("odd", count.Convert(4, out _), StringComparison.Ordinal);

        Assert.Null(Tunable.Find(recipe, "Use It")!.Convert(false, out var off));
        Assert.Equal(false, off);

        Assert.Null(Tunable.Find(recipe, "Mode")!.Convert("bright", out var mode));
        Assert.Equal(TunableTestMode.Bright, mode);
        Assert.Contains("Dark, Bright", Tunable.Find(recipe, "Mode")!.Convert("Grey", out _), StringComparison.Ordinal);

        var label = Tunable.Find(recipe, "Label")!;
        Assert.False(label.IsChangeable);
        Assert.NotNull(label.Convert("y", out _));
    }

    [Fact]
    public void Limits_survive_saving_and_renaming()
    {
        var (recipe, node) = Recipe();

        var loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(recipe)).Graph;
        var entry = Publishing.Find(loaded, "Level")!.Entry;
        Assert.Equal(10, entry.Lowest);
        Assert.Equal(60, entry.Highest);

        Publishing.Publish(recipe, node, PublishedKind.Setting, nameof(TestTunableNode.Level), "Brightness");
        var renamed = Publishing.Find(recipe, "Brightness")!.Entry;
        Assert.Equal(10, renamed.Lowest);
        Assert.Equal(60, renamed.Highest);

        Assert.NotNull(Publishing.LimitProblem(70, 60));
        Assert.Throws<ArgumentException>(() => Publishing.Limit(node, nameof(TestTunableNode.Level), 70, 60));
    }

    // ---- a station changing them -------------------------------------------

    [Fact]
    public void A_change_while_ready_is_applied_and_kept_for_the_next_start()
    {
        var project = Project.Create(_folder, "Line", Recipe().Recipe, "Top");

        using (var runner = Open(project))
        {
            Assert.Null(runner.ChangeSetting("Level", 42));
            Assert.Null(runner.ChangeSetting("mode", "Bright"));
            Assert.Equal(42, NodeOf(runner).Level);

            var snapshot = runner.TriggerAsync().GetAwaiter().GetResult()!;
            Assert.Equal(42.0, snapshot["Seen"]);
            Assert.Equal(42.0, snapshot["Level"]);
        }

        var file = LineSettings.PathFor(project.RecipePath(project.Stations[0]));
        Assert.True(File.Exists(file));
        Assert.EndsWith("Top.line.json", file, StringComparison.OrdinalIgnoreCase);

        using (var again = Open(project))
        {
            Assert.Empty(again.Problems);
            Assert.Equal(42, NodeOf(again).Level);
            Assert.Equal(TunableTestMode.Bright, NodeOf(again).Mode);
        }

        // The recipe itself is not rewritten.
        var saved = GraphSerializer.Load(project.RecipePath(project.Stations[0])).Graph;
        Assert.Equal(50, saved.Nodes.OfType<TestTunableNode>().Single().Level);
    }

    [Fact]
    public void A_value_outside_the_range_is_refused_and_not_kept()
    {
        var project = Project.Create(_folder, "Line", Recipe().Recipe, "Top");
        using var runner = Open(project);

        var problem = runner.ChangeSetting("Level", 90);

        Assert.Contains("between 10 and 60", problem, StringComparison.Ordinal);
        Assert.Equal(50, NodeOf(runner).Level);
        Assert.False(File.Exists(LineSettings.PathFor(project.RecipePath(project.Stations[0]))));
        Assert.Contains("no setting", runner.ChangeSetting("Seen", 1), StringComparison.Ordinal);
    }

    [Fact]
    public void A_change_during_an_inspection_waits_for_it_to_finish()
    {
        var project = Project.Create(_folder, "Line", Recipe().Recipe, "Top");
        using var runner = Open(project);
        var node = NodeOf(runner);
        node.Hold = node.Release;

        var changed = new List<string>();
        runner.SettingChanged += (_, name) => changed.Add(name);

        var first = runner.TriggerAsync();
        Assert.True(node.Started.Wait(TimeSpan.FromSeconds(10)));

        Assert.Null(runner.ChangeSetting("Level", 20));

        // Not yet in the node - the recipe is running - but what it is set to.
        Assert.Equal(50, node.Level);
        Assert.Equal(20.0, runner.SettingValue("Level"));
        Assert.Equal(["Level"], changed);

        node.Release.Set();
        var snapshot = first.GetAwaiter().GetResult()!;

        Assert.Equal(50.0, snapshot["Seen"]);
        Assert.Equal(20, node.Level);

        node.Hold = null;
        Assert.Equal(20.0, runner.TriggerAsync().GetAwaiter().GetResult()!["Seen"]);
    }

    [Fact]
    public void A_line_value_that_no_longer_fits_the_recipe_is_reported_not_applied()
    {
        var project = Project.Create(_folder, "Line", Recipe().Recipe, "Top");
        var line = LineSettings.ForRecipe(project.RecipePath(project.Stations[0]), out _);
        line.Set("Level", 95.0);
        line.Set("Gone", 3.0);
        line.Set("Count", 7.0);
        line.Save();

        using var runner = Open(project);

        Assert.Contains(runner.Problems, p => p.Contains("'Gone'", StringComparison.Ordinal) && p.Contains("no longer publishes", StringComparison.Ordinal));
        Assert.Contains(runner.Problems, p => p.Contains("'Level'", StringComparison.Ordinal) && p.Contains("not applied", StringComparison.Ordinal));
        Assert.Equal(50, NodeOf(runner).Level);
        Assert.Equal(7, NodeOf(runner).Count);
    }

    [Fact]
    public void An_unreadable_line_file_leaves_the_recipe_as_saved()
    {
        var project = Project.Create(_folder, "Line", Recipe().Recipe, "Top");
        File.WriteAllText(LineSettings.PathFor(project.RecipePath(project.Stations[0])), "{ not json");

        using var runner = Open(project);

        Assert.Contains(runner.Problems, p => p.Contains("could not be read", StringComparison.Ordinal));
        Assert.Equal(StationState.Ready, runner.State);
        Assert.Null(runner.ChangeSetting("Level", 30));
    }

    [Fact]
    public void A_wired_setting_is_shown_but_not_changed()
    {
        var graph = new VisionGraph();
        var stats = new Sirius.Vision.Nodes.RegionStatsNode();
        var check = new Sirius.Vision.Nodes.LimitCheckNode();
        var source = new Sirius.Vision.Nodes.ConstantImageNode();
        graph.Add(source);
        graph.Add(stats);
        graph.Add(check);
        graph.TryConnect(source.ImageOut, stats.ImageIn);
        graph.TryConnect(stats.MeanOut, check.Inputs.Single(port => port.Name == nameof(Sirius.Vision.Nodes.LimitCheckNode.Maximum)));
        Publishing.Publish(graph, check, PublishedKind.Setting, nameof(Sirius.Vision.Nodes.LimitCheckNode.Maximum), "Limit");

        var limit = Tunable.Find(graph, "Limit")!;

        Assert.True(limit.IsWired);
        Assert.False(limit.IsChangeable);
        Assert.Contains("wire", limit.Convert(10, out _), StringComparison.Ordinal);
    }

    // ---- Studio: taking them into the recipe -------------------------------

    [Fact]
    public void Line_values_can_be_taken_into_a_recipe_file()
    {
        var project = Project.Create(_folder, "Line", Recipe().Recipe, "Top");

        using (var runner = Open(project))
        {
            runner.ChangeSetting("Level", 33);
            runner.ChangeSetting("Use It", false);
        }

        var station = project.Stations[0];
        Assert.Equal(2, LineSettingsViewModel.CountFor(project, station));

        var model = new LineSettingsViewModel(project, station, editor: null);
        var level = model.Rows.Single(row => row.Name == "Level");
        Assert.Equal("50", level.InRecipe);
        Assert.Equal("33", level.OnLine);

        Assert.Empty(model.TakeIntoRecipe([level]));

        var saved = GraphSerializer.Load(project.RecipePath(station)).Graph.Nodes.OfType<TestTunableNode>().Single();
        Assert.Equal(33, saved.Level);
        Assert.Equal(1, LineSettingsViewModel.CountFor(project, station));

        model.Forget(model.Rows);
        Assert.Empty(model.Rows);
        Assert.False(File.Exists(LineSettings.PathFor(project.RecipePath(station))));
    }

    [Fact]
    public void Taking_line_values_into_the_open_recipe_changes_the_editor_not_the_file()
    {
        var project = Project.Create(_folder, "Line", Recipe().Recipe, "Top");
        var station = project.Stations[0];

        using (var runner = Open(project))
            runner.ChangeSetting("Level", 12);

        var editor = GraphSerializer.Load(project.RecipePath(station)).Graph;
        var model = new LineSettingsViewModel(project, station, editor);

        Assert.True(model.ChangesEditor);
        Assert.Empty(model.TakeIntoRecipe(model.Rows));

        Assert.Equal(12, editor.Nodes.OfType<TestTunableNode>().Single().Level);
        Assert.Equal(50, GraphSerializer.Load(project.RecipePath(station)).Graph.Nodes.OfType<TestTunableNode>().Single().Level);
        Assert.Equal(0, LineSettingsViewModel.CountFor(project, station));
    }
}
