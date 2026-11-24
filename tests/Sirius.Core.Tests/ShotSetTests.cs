using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Several shots per part (F26a): the recipe runs on each shot, Collect
/// keeps them, and what follows it waits - not fails - until the set's
/// last, then judges the part once.
/// </summary>
public sealed class ShotSetTests
{
    public ShotSetTests() => TestCatalog.Ensure();

    /// <summary>A picture of one grey level per shot: 10 for shot 1, 20 for shot 2 ...</summary>
    private sealed class ShotPictureNode : VisionNode, IInspectionNode
    {
        public ShotPictureNode() => ImageOut = AddOutput<ImageData>("Image");

        public OutputPort ImageOut { get; }

        protected override void OnEvaluate(EvalContext context)
        {
            var mat = new Mat(8, 8, DepthType.Cv8U, 1);
            mat.SetTo(new MCvScalar(10 * (context.Shot?.Number ?? 1)));
            context.Set(ImageOut, ImageData.Adopt(mat));
        }
    }

    /// <summary>Shot number, summed over the set, checked at most 5.</summary>
    private sealed class Recipe
    {
        public Recipe(int deskShots = 1)
        {
            Shot = new ShotNode { ShotsPerPart = deskShots };
            Numbers = new CollectNumbersNode();
            Check = new LimitCheckNode { Kind = LimitKind.AtMost, Maximum = 5 };

            Graph.Add(Shot);
            Graph.Add(Numbers);
            Graph.Add(Check);
            Assert.True(Graph.TryConnect(Shot.ShotOut, Numbers.FindInput(nameof(CollectNumbersNode.Value))!).Success);
            Assert.True(Graph.TryConnect(Numbers.SumOut, Check.FindInput(nameof(LimitCheckNode.Value))!).Success);

            Scheduler = new GraphScheduler(Graph);
        }

        public VisionGraph Graph { get; } = new();

        public ShotNode Shot { get; }

        public CollectNumbersNode Numbers { get; }

        public LimitCheckNode Check { get; }

        public GraphScheduler Scheduler { get; }

        public GraphRunResult Inspect(ShotInfo? shot = null)
        {
            Scheduler.Shot = shot;
            return Scheduler.InspectAsync().GetAwaiter().GetResult();
        }

        public object? Sum => Scheduler.GetOutput(Numbers.SumOut);
    }

    [Fact]
    public void Shots_before_the_last_wait_and_the_last_judges_the_part()
    {
        var recipe = new Recipe();

        var first = recipe.Inspect(new ShotInfo("A", 1, IsLast: false));
        var second = recipe.Inspect(new ShotInfo("A", 2, IsLast: false));

        // Kept, waiting - and nothing after the Collect failed for want of a value.
        Assert.True(first.IsWaiting);
        Assert.True(second.IsWaiting);
        Assert.Equal(0, second.Failed);
        Assert.Contains("waiting for the set's last", second.WaitingFor, StringComparison.Ordinal);
        Assert.Null(recipe.Sum);
        Assert.Null(recipe.Scheduler.GetOutput(recipe.Check.PassOut));
        Assert.Equal([1, 2], recipe.Numbers.KeptShots);

        var last = recipe.Inspect(new ShotInfo("A", 3, IsLast: true));

        Assert.False(last.IsWaiting);
        Assert.Equal(0, last.Failed);
        Assert.Equal(6d, recipe.Sum);
        Assert.Equal(false, recipe.Scheduler.GetOutput(recipe.Check.PassOut));
        Assert.Equal(3, recipe.Scheduler.GetOutput(recipe.Numbers.CountOut));
    }

    [Fact]
    public void A_new_item_starts_a_new_set_and_a_missing_shot_is_said()
    {
        var recipe = new Recipe();

        recipe.Inspect(new ShotInfo("A", 1, IsLast: false));
        recipe.Inspect(new ShotInfo("B", 1, IsLast: false));
        recipe.Inspect(new ShotInfo("B", 2, IsLast: true));

        // A's shot 1 thrown away when B began.
        Assert.Equal(3d, recipe.Sum);
        Assert.Equal(true, recipe.Scheduler.GetOutput(recipe.Check.PassOut));

        recipe.Inspect(new ShotInfo("C", 1, IsLast: false));
        recipe.Inspect(new ShotInfo("C", 3, IsLast: true));

        Assert.Equal(4d, recipe.Sum);
        Assert.Equal(NodeStatus.Warning, recipe.Numbers.Status);
        Assert.Equal("Shot 2 of 3 is missing: judged on the rest.", recipe.Numbers.StatusMessage);
    }

    /// <summary>At the desk, with no station: the Shot node's Shots per Part numbers the inspections.</summary>
    [Fact]
    public void At_the_desk_inspections_are_numbered_into_sets_by_the_shot_node()
    {
        var recipe = new Recipe(deskShots: 2);

        Assert.True(recipe.Inspect().IsWaiting);
        Assert.Equal(1, recipe.Scheduler.GetOutput(recipe.Shot.ShotOut));
        Assert.Equal("Part 1", recipe.Scheduler.GetOutput(recipe.Shot.ItemOut));

        Assert.False(recipe.Inspect().IsWaiting);
        Assert.Equal(3d, recipe.Sum);

        // The next part, from shot 1.
        Assert.True(recipe.Inspect().IsWaiting);
        Assert.Equal("Part 2", recipe.Scheduler.GetOutput(recipe.Shot.ItemOut));
        Assert.False(recipe.Inspect().IsWaiting);

        // Restarted - a new folder run - from shot 1 of a new part.
        recipe.Scheduler.RestartShots();
        Assert.True(recipe.Inspect().IsWaiting);
        Assert.Equal(1, recipe.Scheduler.GetOutput(recipe.Shot.ShotOut));

        // One shot a part: no set at all.
        var single = new Recipe();
        Assert.False(single.Inspect().IsWaiting);
        Assert.Equal(1d, single.Sum);
    }

    [Fact]
    public void Collect_images_gives_the_average_the_brightest_and_the_darkest()
    {
        var graph = new VisionGraph();
        var picture = new ShotPictureNode();
        var collect = new CollectImagesNode();
        graph.Add(picture);
        graph.Add(collect);
        Assert.True(graph.TryConnect(picture.ImageOut, collect.ImageIn).Success);

        using var scheduler = new GraphScheduler(graph);

        foreach (var shot in new[] { 1, 2, 3 })
        {
            scheduler.Shot = new ShotInfo("A", shot, IsLast: shot == 3);
            scheduler.InspectAsync().GetAwaiter().GetResult();
        }

        double Level(OutputPort port) => CvInvoke.Mean(((ImageData)scheduler.GetOutput(port)!).Mat).V0;

        Assert.Equal(20, Level(collect.AverageOut));
        Assert.Equal(30, Level(collect.BrightestOut));
        Assert.Equal(10, Level(collect.DarkestOut));
        Assert.Equal(3, scheduler.GetOutput(collect.CountOut));
    }

    /// <summary>
    /// The part's verdict is Collect Verdicts', not a per-shot check's: a
    /// check wired into it is not a final one.
    /// </summary>
    [Fact]
    public void Collect_verdicts_is_the_parts_check()
    {
        var graph = new VisionGraph();
        var shot = new ShotNode();
        var perShot = new LimitCheckNode { Kind = LimitKind.AtMost, Maximum = 2 };
        var verdicts = new CollectVerdictsNode();
        graph.Add(shot);
        graph.Add(perShot);
        graph.Add(verdicts);
        Assert.True(graph.TryConnect(shot.ShotOut, perShot.FindInput(nameof(LimitCheckNode.Value))!).Success);
        Assert.True(graph.TryConnect(perShot.PassOut, verdicts.FindInput(nameof(CollectVerdictsNode.ShotPassed))!).Success);

        Assert.Equal([verdicts], Verdicts.Final(graph.Nodes.OfType<IJudgingNode>()));

        using var scheduler = new GraphScheduler(graph);

        foreach (var number in new[] { 1, 2, 3, 4 })
        {
            scheduler.Shot = new ShotInfo("A", number, IsLast: number == 4);
            scheduler.InspectAsync().GetAwaiter().GetResult();
        }

        Assert.Equal(false, scheduler.GetOutput(verdicts.PassedOut));
        Assert.Equal(2, scheduler.GetOutput(verdicts.FailedCountOut));
        Assert.Equal("3, 4", scheduler.GetOutput(verdicts.FailedShotsOut));
    }
}
