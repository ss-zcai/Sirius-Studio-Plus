using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Annotations;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Serialization;
using Sirius.Core.Shapes;
using Sirius.Vision.Nodes;
using Sirius.Vision.Runtime;
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

        // A .sann beside each image, naming the recipe; none when told not to.
        var written = Directory.GetFiles(folder, "*.sann");
        Assert.Equal(4, written.Length);
        Assert.Equal("recipe.sgraph", AnnotationFile.TryLoad(written[0], out _)!.Recipe);

        var other = Folder();
        Sirius.Runner.Program.Main([recipe, "--folder", other, "--quiet", "--no-annotations"]).GetAwaiter().GetResult();
        Assert.Empty(Directory.GetFiles(other, "*.sann"));

        Assert.True(Sirius.Runner.Program.TryParse([recipe, "--no-annotations"], out _, out _, out _, out _, out var no));
        Assert.True(no);
    }

    /// <summary>The recipe with an image viewer drawing the circle found, and its radius published.</summary>
    private static (VisionGraph Graph, ImageSequenceNode Sequence) Viewed(string folder)
    {
        var (graph, sequence, _, _) = Recipe(folder);
        var find = graph.Nodes.OfType<FindCircleNode>().Single();
        var viewer = new ViewerNode { Name = "Main View" };

        graph.Add(viewer);
        graph.TryConnect(sequence.ImageOut, viewer.ImageIn);
        graph.TryConnect(find.CircleOut, viewer.ShapesIn);

        Publishing.Publish(graph, find, PublishedKind.Result, "Radius", "Radius");
        Publishing.Publish(graph, viewer, PublishedKind.View, string.Empty, "Main View");

        return (graph, sequence);
    }

    /// <summary>
    /// A folder run writes a .sann beside each picture (F20a), as a station
    /// does beside each it saves: the verdict, the results, the shapes each
    /// viewer drew - for Sirius IV to show.
    /// </summary>
    [Fact]
    public void A_folder_run_writes_a_sann_beside_each_picture()
    {
        var folder = Folder();
        var (graph, sequence) = Viewed(folder);

        using var scheduler = CreateScheduler(graph);
        var annotations = new FolderAnnotations("Discs.sgraph");

        FolderRun.RunAsync(graph, scheduler, sequence, null, CancellationToken.None, annotations.Write).GetAwaiter().GetResult();

        Assert.Equal(4, annotations.Written);
        Assert.Empty(annotations.Problems);
        Assert.Equal("Annotations: 4 .sann files written beside the pictures.", annotations.Describe());

        var good = AnnotationFile.TryLoad(Path.Combine(folder, "part_1.sann"), out var problem);
        Assert.Null(problem);
        Assert.Equal(("part_1.png", "Discs.sgraph", true), (good!.Image, good.Recipe, good.Passed));
        Assert.Equal((400, 400), (good.Width, good.Height));
        Assert.Equal(80, Convert.ToDouble(good.Results.Single(result => result.Key == "Radius").Value, System.Globalization.CultureInfo.InvariantCulture), 0);

        var layer = Assert.Single(good.Layers);
        Assert.Equal("Main View", layer.Name);
        Assert.IsType<CircleShape>(Assert.Single(layer.Shapes));

        Assert.False(AnnotationFile.TryLoad(Path.Combine(folder, "part_4.sann"), out _)!.Passed);
    }

    /// <summary>
    /// A station's picture run again: what is about the picture - station,
    /// inspection, time, part, bits, measurements drawn by hand - is kept;
    /// what this run found replaces the rest.
    /// </summary>
    [Fact]
    public void A_station_picture_run_again_keeps_what_is_about_the_picture()
    {
        var folder = Folder();
        var taken = new DateTime(2026, 11, 21, 9, 30, 0);

        new AnnotationFile
        {
            Image = "part_1.png",
            Width = 400,
            Height = 400,
            Station = "Top",
            Recipe = "Old.sgraph",
            Serial = 42,
            Time = taken,
            Passed = false,
            Results = [new("Radius", 1.0)],
            Part = [new("PalletId", 12L)],
            Measurements = [new MeasurementRecord("PointToPoint", [new Point2(10, 10), new Point2(50, 10)], "#F59E0B", null)],
        }.Save(Path.Combine(folder, "part_1.sann"));

        var (graph, sequence) = Viewed(folder);
        using var scheduler = CreateScheduler(graph);
        var annotations = new FolderAnnotations("New.sgraph");

        FolderRun.RunAsync(graph, scheduler, sequence, null, CancellationToken.None, annotations.Write).GetAwaiter().GetResult();

        var again = AnnotationFile.TryLoad(Path.Combine(folder, "part_1.sann"), out _)!;

        Assert.Equal(("Top", 42L, taken), (again.Station, again.Serial, again.Time));
        Assert.Equal(12L, again.Part.Single().Value);
        Assert.Single(again.Measurements);

        Assert.Equal(("New.sgraph", true), (again.Recipe, again.Passed));
        Assert.Equal(80, Convert.ToDouble(again.Results.Single().Value, System.Globalization.CultureInfo.InvariantCulture), 0);
    }

    /// <summary>A folder that cannot be written to is said, not thrown: the run goes on.</summary>
    [Fact]
    public void An_annotation_file_that_cannot_be_written_is_said_and_the_run_goes_on()
    {
        var folder = Folder();
        var blocked = Path.Combine(folder, "part_2.sann");
        Directory.CreateDirectory(blocked);

        var (graph, sequence) = Viewed(folder);
        using var scheduler = CreateScheduler(graph);
        var annotations = new FolderAnnotations("Discs.sgraph");

        var summary = FolderRun.RunAsync(graph, scheduler, sequence, null, CancellationToken.None, annotations.Write).GetAwaiter().GetResult();

        Assert.Equal(4, summary.Items.Count);
        Assert.Equal(3, annotations.Written);
        Assert.StartsWith("part_2.png", Assert.Single(annotations.Problems), StringComparison.Ordinal);
        Assert.StartsWith("Annotations: 3 written; 1 could not be", annotations.Describe(), StringComparison.Ordinal);
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
