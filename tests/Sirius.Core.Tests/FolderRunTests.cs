using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Serialization;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// A recipe over a folder of images: Run Folder, and sirius-run.
/// </summary>
/// <remarks>
/// The folder holds two good parts (a disc of radius 80), one with nothing
/// on it, and one undersize (radius 60). The recipe finds the circle and
/// passes a radius between 75 and 85.
/// </remarks>
public class FolderRunTests
{
    public FolderRunTests() => TestCatalog.Ensure();

    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sirius-folder-run", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        Write(Path.Combine(folder, "part_1.png"), 80);
        Write(Path.Combine(folder, "part_2.png"), 80);
        Write(Path.Combine(folder, "part_3.png"), 0);
        Write(Path.Combine(folder, "part_4.png"), 60);

        return folder;
    }

    private static void Write(string path, int radius)
    {
        using var mat = new Mat(400, 400, DepthType.Cv8U, 1);
        mat.SetTo(new MCvScalar(0));

        if (radius > 0)
            CvInvoke.Circle(mat, new System.Drawing.Point(200, 200), radius, new MCvScalar(255), -1);

        CvInvoke.Imwrite(path, mat);
    }

    private static (VisionGraph Graph, ImageSequenceNode Sequence, CounterNode Counter, ResultLogNode Log) Recipe(
        string folder)
    {
        var graph = new VisionGraph();
        var sequence = new ImageSequenceNode { SourcePath = folder };
        var find = new FindCircleNode { Band = new Ring(200, 200, 40, 110), CaliperCount = 36 };
        var check = new LimitCheckNode { Kind = LimitKind.Between, Minimum = 75, Maximum = 85 };
        var counter = new CounterNode();
        var log = new ResultLogNode { LogFile = Path.Combine(folder, "log", "results.csv"), NewFileEachDay = false };

        graph.Add(sequence);
        graph.Add(find);
        graph.Add(check);
        graph.Add(counter);
        graph.Add(log);

        graph.TryConnect(sequence.ImageOut, find.ImageIn);
        graph.TryConnect(find.RadiusOut, check.FindInput(nameof(LimitCheckNode.Value))!);
        graph.TryConnect(check.PassOut, counter.ResultIn);
        graph.TryConnect(find.RadiusOut, log.Sockets[0]);

        return (graph, sequence, counter, log);
    }

    private static GraphScheduler CreateScheduler(VisionGraph graph)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return new GraphScheduler(graph);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void A_folder_run_inspects_every_image_and_adds_them_up()
    {
        var folder = Folder();
        var (graph, sequence, counter, log) = Recipe(folder);

        using var scheduler = CreateScheduler(graph);

        var seen = new List<FolderRunItem>();
        var summary = FolderRun.RunAsync(graph, scheduler, sequence, item =>
        {
            seen.Add(item);
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(4, summary.Items.Count);
        Assert.Equal(2, summary.Passed);
        Assert.Equal(2, summary.Failed);
        Assert.Equal(50, summary.Yield, 6);
        Assert.False(summary.Cancelled);

        Assert.Equal(["part_1.png", "part_2.png", "part_3.png", "part_4.png"], seen.Select(item => item.Name));
        Assert.Equal([true, true, false, false], seen.Select(item => item.Verdict.Passed));

        // Each image an inspection: counted, and a row in the log.
        Assert.Equal(4, scheduler.GetOutput(counter.TotalOut));
        Assert.Equal(2, scheduler.GetOutput(counter.PassedOut));
        Assert.Equal(5, File.ReadAllLines(log.LogFile).Length);

        Assert.StartsWith("4 images in", summary.Describe(), StringComparison.Ordinal);
        Assert.Contains("2 passed, 2 failed (50.0 % yield)", summary.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_stopped_run_says_how_far_it_got()
    {
        var folder = Folder();
        var (graph, sequence, _, _) = Recipe(folder);

        using var scheduler = CreateScheduler(graph);
        using var stop = new CancellationTokenSource();

        var summary = FolderRun.RunAsync(graph, scheduler, sequence, item =>
        {
            if (item.Index == 2)
                stop.Cancel();

            return Task.CompletedTask;
        }, stop.Token).GetAwaiter().GetResult();

        Assert.True(summary.Cancelled);
        Assert.Equal(2, summary.Items.Count);
        Assert.StartsWith("Stopped after 2 of 4 images", summary.Describe(), StringComparison.Ordinal);
    }

    // ---- sirius-run ------------------------------------------------------------------

    /// <summary>
    /// The saved recipe run from the command line over a folder named there:
    /// exit code 1, because two parts failed, and the log written.
    /// </summary>
    [Fact]
    public void The_runner_runs_a_saved_recipe_over_a_folder_and_says_some_failed()
    {
        var folder = Folder();
        var (graph, _, _, log) = Recipe(Path.Combine(folder, "elsewhere"));

        var recipe = Path.Combine(folder, "recipe.sgraph");
        File.WriteAllText(recipe, GraphSerializer.Serialize(graph));

        var code = Sirius.Runner.Program.Main([recipe, "--folder", folder, "--quiet"]).GetAwaiter().GetResult();

        Assert.Equal(1, code);
        Assert.Equal(5, File.ReadAllLines(log.LogFile).Length);
    }

    [Fact]
    public void The_runner_says_what_is_wrong_with_its_arguments()
    {
        Assert.False(Sirius.Runner.Program.TryParse([], out _, out _, out _, out var none));
        Assert.Null(none);

        Assert.False(Sirius.Runner.Program.TryParse(["missing.sgraph"], out _, out _, out _, out var missing));
        Assert.Contains("No recipe", missing, StringComparison.Ordinal);

        Assert.False(Sirius.Runner.Program.TryParse(["--folder"], out _, out _, out _, out var bare));
        Assert.Contains("needs a folder", bare, StringComparison.Ordinal);

        Assert.Equal(2, Sirius.Runner.Program.Main(["--nonsense"]).GetAwaiter().GetResult());
    }

    [Fact]
    public void Each_image_is_one_line()
    {
        var item = new FolderRunItem(7, 120, "part_007.png", new Verdict(false, new Grade("Reject", 3, false), 2, 1), 1);

        Assert.Equal("  7/120  part_007.png  FAIL  Reject  (1 node error)", Sirius.Runner.Program.Line(item));
    }
}
