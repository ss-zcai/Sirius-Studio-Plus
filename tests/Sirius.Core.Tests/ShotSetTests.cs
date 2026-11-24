using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using System.IO;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Runtime;
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

    // ---- a station of several shots per part -----------------------------------------------

    private static StationRunner Station(VisionGraph recipe, int shotsPerPart)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);

        try
        {
            return new StationRunner("Lens", recipe) { ShotsPerPart = shotsPerPart };
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static PartValues Part(string item, int shot, bool? last = null) =>
        new([
            new("Part ID", item),
            new(PartValues.ShotName, shot),
            .. last is { } said ? new[] { new KeyValuePair<string, object?>(PartValues.LastName, said) } : [],
        ]);

    /// <summary>
    /// Shots per Part 3: shots 1 and 2 received - no verdict, not counted -
    /// shot 3 judges the part, counted once. The PLC's Last ends a set
    /// sooner.
    /// </summary>
    [Fact]
    public void A_station_answers_received_until_the_last_and_counts_the_part_once()
    {
        var recipe = new Recipe();
        using var station = Station(recipe.Graph, shotsPerPart: 3);

        InspectionSnapshot Shoot(PartValues part) => station.TriggerAsync(part).GetAwaiter().GetResult()!;

        var first = Shoot(Part("A", 1));
        var second = Shoot(Part("A", 2));

        Assert.True(first.Received);
        Assert.True(second.Received);
        Assert.Null(second.Verdict.Passed);
        Assert.Equal(new ShotInfo("A", 2, IsLast: false), second.Shot);
        Assert.Equal(0, station.Counts.Total);

        var last = Shoot(Part("A", 3));

        Assert.False(last.Received);
        Assert.Equal(false, last.Verdict.Passed);
        Assert.Equal((1L, 1L), (station.Counts.Total, station.Counts.Failed));

        // The PLC says Last at shot 2: judged there.
        var early = Shoot(Part("B", 1));
        var said = Shoot(Part("B", 2, last: true));

        Assert.True(early.Received);
        Assert.False(said.Received);
        Assert.Equal(true, said.Verdict.Passed);
        Assert.Equal((2L, 1L), (station.Counts.Total, station.Counts.Passed));
    }

    /// <summary>
    /// Saving failures only: a set's early pictures are held until the part
    /// is judged, then written - every one - when it failed, carrying the
    /// part's verdict; let go when it passed; written as not judged when
    /// the set was given up on.
    /// </summary>
    [Fact]
    public void A_sets_early_pictures_are_kept_as_its_part_went()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sirius-shots-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var mat = new Mat(10, 10, DepthType.Cv8U, 1);
            using var image = ImageData.Adopt(mat.Clone());

            InspectionRecord Shot(int serial, bool? passed, bool received) => new(
                new InspectionSnapshot("Top", serial, DateTime.Now, new Verdict(passed, null, passed is null ? 0 : 1, passed == false ? 1 : 0), TimeSpan.Zero, 0, new Dictionary<string, object?>())
                {
                    Received = received,
                },
                null,
                image,
                []);

            string[] Saved() => Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*.tif", SearchOption.AllDirectories).Select(path => Path.GetFileNameWithoutExtension(path)!).Order(StringComparer.Ordinal).ToArray()
                : [];

            using (var recorder = new Sirius.Vision.Runtime.ImageRecorder(new Sirius.Core.Projects.ImageSaving(Sirius.Core.Projects.ImageSavePolicy.Failed), folder, "Top", freeSpace: _ => long.MaxValue))
            {
                // A part that passed: its two held shots let go.
                recorder.Hold(Shot(1, null, received: true));
                recorder.Hold(Shot(2, null, received: true));
                Assert.Equal(2, recorder.Held);
                recorder.Release(new Verdict(true, null, 1, 0), 0);
                Assert.Equal(0, recorder.Held);

                // A part that failed: its held shots written as failed, and its last.
                recorder.Hold(Shot(3, null, received: true));
                recorder.Hold(Shot(4, null, received: true));
                recorder.Release(new Verdict(false, null, 1, 1), 0);
                recorder.Record(Shot(5, false, received: false));

                // A set given up on: kept as not judged.
                recorder.Hold(Shot(6, null, received: true));
                recorder.Release(new Verdict(null, null, 0, 0), nodeErrors: 1);
            }

            var saved = Saved();
            Assert.Equal(4, saved.Length);
            Assert.All(saved.Take(3), name => Assert.EndsWith("_FAIL", name, StringComparison.Ordinal));
            Assert.EndsWith("_000006_ERROR", saved[3], StringComparison.Ordinal);

            var annotation = Sirius.Core.Annotations.AnnotationFile.TryLoad(
                Directory.GetFiles(folder, "*_000003_FAIL.sann", SearchOption.AllDirectories).Single(), out _)!;
            Assert.Equal(false, annotation.Passed);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not worth failing a run over.
            }
        }
    }

    /// <summary>The station's Shots per Part is kept in the project, and a station opened from it takes it.</summary>
    [Fact]
    public void Shots_per_part_is_kept_with_the_station()
    {
        var root = Path.Combine(Path.GetTempPath(), "sirius-shots-project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var project = Sirius.Core.Projects.Project.Create(root, "Line", new Recipe().Graph, "Lens");
            var station = project.Stations[0];

            Assert.Equal(1, station.ShotsPerPart);
            Assert.True(project.SetShotsPerPart(station, 4));
            Assert.False(project.SetShotsPerPart(station, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => project.SetShotsPerPart(station, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => project.SetShotsPerPart(station, Sirius.Core.Projects.Station.MostShots + 1));

            var again = Sirius.Core.Projects.Project.Load(project.FilePath).Project;
            Assert.Equal(4, again.Stations[0].ShotsPerPart);

            // One a part is not written at all: a project from before is unchanged.
            project.SetShotsPerPart(station, 1);
            Assert.DoesNotContain("\"shots\"", File.ReadAllText(project.FilePath), StringComparison.Ordinal);
            project.SetShotsPerPart(station, 4);

            using var runner = StationRunner.Open(again, again.Stations[0]);
            Assert.Equal(4, runner.ShotsPerPart);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not worth failing a run over.
            }
        }
    }

    /// <summary>
    /// A folder of sets, run in Studio: its pictures shots 1 and 2 of each
    /// part in turn, the parts counted, not the pictures.
    /// </summary>
    [Fact]
    public void A_folder_run_of_sets_counts_its_parts()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sirius-shots-folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            foreach (var name in new[] { "a1", "a2", "b1", "b2", "c1", "c2" })
            {
                using var mat = new Mat(8, 8, DepthType.Cv8U, 1);
                mat.SetTo(new MCvScalar(50));
                CvInvoke.Imwrite(Path.Combine(folder, name + ".png"), mat);
            }

            var recipe = new Recipe(deskShots: 2);
            var sequence = new ImageSequenceNode { SourcePath = folder };
            recipe.Graph.Add(sequence);

            var summary = FolderRun.RunAsync(recipe.Graph, recipe.Scheduler, sequence, null, CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(6, summary.Items.Count);
            Assert.Equal((3, 3), (summary.Received, summary.Parts));
            Assert.Equal([true, false, true, false, true, false], summary.Items.Select(item => item.Received));

            // Shot 1 + shot 2 = 3, at most 5: each part passes.
            Assert.Equal(3, summary.Passed);
            Assert.StartsWith("6 images, 3 parts in", summary.Describe(), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not worth failing a run over.
            }
        }
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
