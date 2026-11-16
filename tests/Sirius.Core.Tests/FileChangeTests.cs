using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Plugins;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Sirius.Vision.Plugins;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// A file a node reads, saved over under the same name, makes it run again
/// (FileStamps) - the test team's template-matching custom node, which
/// kept matching against its old model until the model file was renamed.
/// </summary>
public sealed class FileChangeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-file-change-" + Guid.NewGuid().ToString("N"));

    public FileChangeTests()
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

    // ---- the test team's case, as custom nodes ------------------------------------------

    /// <summary>Matches against a model kept in a file - here, publishes what the file says.</summary>
    [CustomNode("Model Match", TypeKey = "tests.modelMatch")]
    public sealed class ModelMatchNode : ICustomNode
    {
        private OutPort<string> _model = null!;

        public static int Runs;

        [Setting("Model", Editor = SettingEditor.FilePath, Description = "The taught model.")]
        public string ModelPath { get; set; } = string.Empty;

        public void Declare(INodeShape shape) => _model = shape.Output<string>("Model");

        // Read in Run, as the guide says: never kept from a setter.
        public void Run(IRunContext context)
        {
            Interlocked.Increment(ref Runs);
            context.Publish(_model, File.Exists(ModelPath) ? File.ReadAllText(ModelPath) : "(no model)");
        }
    }

    /// <summary>Teaches: writes the model to the file it names.</summary>
    [CustomNode("Model Teach", TypeKey = "tests.modelTeach")]
    public sealed class ModelTeachNode : ICustomNode
    {
        public static int Runs;

        [Setting("Model", Editor = SettingEditor.FilePath)]
        public string ModelPath { get; set; } = string.Empty;

        [Setting]
        public string Feature { get; set; } = "circle";

        public void Declare(INodeShape shape)
        {
        }

        public void Run(IRunContext context)
        {
            Interlocked.Increment(ref Runs);
            File.WriteAllText(ModelPath, Feature);
        }
    }

    private static VisionNode Custom(Type type)
    {
        var problems = new List<string>();
        var described = CustomNodeType.TryCreate(type, "test library", problems);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        return described!.Descriptor.CreateInstance();
    }

    private static GraphScheduler Scheduler(VisionGraph graph)
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

    private static void Run(GraphScheduler scheduler) => scheduler.RunAsync().GetAwaiter().GetResult();

    /// <summary>Saved over, as a teaching tool does - and stamped later, as a later save would be.</summary>
    private static void SaveOver(string path, string text, int secondsLater)
    {
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(secondsLater));
    }

    [Fact]
    public void A_model_saved_over_under_the_same_name_is_matched_against_at_the_next_run()
    {
        var model = Path.Combine(_folder, "part.model");
        File.WriteAllText(model, "taught: v1");

        var graph = new VisionGraph();
        var match = Custom(typeof(ModelMatchNode));
        ((CustomNodeHost)match).Change(((CustomNodeHost)match).CustomType.Settings.Single(), model);
        graph.Add(match);

        using var scheduler = Scheduler(graph);
        var output = match.FindOutput("Model")!;

        Run(scheduler);
        Assert.Equal("taught: v1", scheduler.GetOutput(output));
        var runs = ModelMatchNode.Runs;

        // Nothing changed: not run again.
        Run(scheduler);
        Assert.Equal(runs, ModelMatchNode.Runs);

        // Taught again, saved over under the same name: run again, with the new model.
        SaveOver(model, "taught: v2", 5);
        Run(scheduler);
        Assert.Equal(runs + 1, ModelMatchNode.Runs);
        Assert.Equal("taught: v2", scheduler.GetOutput(output));

        // Removed, then made again: each a change.
        File.Delete(model);
        Run(scheduler);
        Assert.Equal("(no model)", scheduler.GetOutput(output));

        SaveOver(model, "taught: v3", 10);
        Run(scheduler);
        Assert.Equal("taught: v3", scheduler.GetOutput(output));
    }

    [Fact]
    public void A_teach_node_writing_its_file_does_not_run_itself_again_but_a_match_node_reading_it_does()
    {
        var model = Path.Combine(_folder, "taught.model");

        var graph = new VisionGraph();
        var teach = (CustomNodeHost)Custom(typeof(ModelTeachNode));
        var match = (CustomNodeHost)Custom(typeof(ModelMatchNode));
        teach.Change(teach.CustomType.Settings.Single(setting => setting.Name == "ModelPath"), model);
        match.Change(match.CustomType.Settings.Single(), model);
        graph.Add(teach);
        graph.Add(match);

        using var scheduler = Scheduler(graph);

        Run(scheduler);
        var teaches = ModelTeachNode.Runs;

        // The teach node wrote its file as it ran: that is not news to it.
        Run(scheduler);
        Assert.Equal(teaches, ModelTeachNode.Runs);

        // Taught again - a setting changed. The match node, not wired to it,
        // learns of the new file once it is written: at the next run.
        teach.Change(teach.CustomType.Settings.Single(setting => setting.Name == "Feature"), "square");
        Run(scheduler);
        Assert.Equal(teaches + 1, ModelTeachNode.Runs);

        Run(scheduler);
        Assert.Equal("square", scheduler.GetOutput(match.FindOutput("Model")!));
        Assert.Equal(teaches + 1, ModelTeachNode.Runs);
    }

    // ---- built-in nodes, too ----------------------------------------------------------

    [Fact]
    public void A_picture_saved_over_under_the_same_name_is_read_again()
    {
        var picture = Path.Combine(_folder, "part.png");

        using (var small = new Mat(30, 40, DepthType.Cv8U, 1))
            CvInvoke.Imwrite(picture, small);

        var graph = new VisionGraph();
        var file = new ImageFileSourceNode { FilePath = picture };
        graph.Add(file);

        using var scheduler = Scheduler(graph);
        Run(scheduler);
        Assert.Equal(40, ((ImageData)scheduler.GetOutput(file.ImageOut)!).Width);

        using (var large = new Mat(60, 80, DepthType.Cv8U, 1))
            CvInvoke.Imwrite(picture, large);

        File.SetLastWriteTimeUtc(picture, DateTime.UtcNow.AddSeconds(5));
        Run(scheduler);
        Assert.Equal(80, ((ImageData)scheduler.GetOutput(file.ImageOut)!).Width);
    }

    [Fact]
    public void A_file_a_node_writes_is_not_watched()
    {
        // Result Log's file is a SaveFile: what it writes is its own business.
        var log = new ResultLogNode();
        var parameter = log.Descriptor.Parameters.Single(candidate => candidate.Attribute.Editor == ParamEditor.FilePath);

        Assert.True(parameter.Attribute.SaveFile);
    }
}
