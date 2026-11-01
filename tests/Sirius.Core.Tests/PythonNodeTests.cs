using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Serialization;
using Sirius.Core.Shapes;
using Sirius.Vision;
using Sirius.Vision.Plugins;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Custom nodes written in Python: a library is found, its own Python is
/// started, its nodes are described, and they run in a graph.
/// </summary>
/// <remarks>
/// These start real Python processes, with whatever Python this machine has
/// on PATH, and pass without checking anything on a machine with none -
/// xunit 2 has no way to skip at run time. They use no package beyond the
/// standard library, so they pass with or without numpy.
/// </remarks>
public class PythonNodeTests
{
    private const string Library = """
        import os
        from sirius_nodes import node, Input, Output, Setting, Rect, Circle, Image

        def invert(image):
            if hasattr(image, "to_numpy"):
                return Image(image.width, image.height, image.channels, "uint8",
                             bytearray(255 - b for b in image.data))
            return 255 - image

        @node("Py Invert", key="pytest.invert", group="Filtering", description="Inverts.")
        class PyInvert:
            image = Input.image("Image")
            result = Output.image("Result")
            spots = Output.shapes("Spots")
            loads = Output.integer("Loads")
            note = Output.text("Note")
            bright = Output.bool("Bright")

            level = Setting.integer("Level", 60, min=0, max=255)
            model = Setting.text("Model", "a", reload=True)
            mode = Setting.choice("Mode", choices=["fast", "accurate"])
            gain = Setting.number("Gain", 1.5, connectable=True)

            def load(self):
                self.count = getattr(self, "count", 0) + 1

            def run(self, image):
                print("this goes to stderr, not into the protocol")
                if self.level == 13:
                    raise ValueError("unlucky level")
                if self.level == 99:
                    os._exit(3)
                if self.level == 7:
                    self.warn("seven is low")
                return {
                    "result": invert(image),
                    "spots": [Rect(1, 2, 3, 4, label="spot", score=0.75, colour="red"), Circle(5, 6, 2)],
                    "loads": self.count,
                    "note": "%s/%s/%g" % (self.model, self.mode, self.gain),
                    "bright": self.level > 100,
                }

        @node("Py Count", key="pytest.count", group="Analysis")
        class PyCount:
            shapes = Input.shapes("Shapes")
            count = Output.integer("Count")
            labels = Output.text("Labels")

            def run(self, shapes):
                return {"count": len(shapes), "labels": ",".join(s.label or "-" for s in shapes)}

        @node("Bad", key="nodots", group="Analysis")
        class Bad:
            out = Output.number("Out")

            def run(self):
                return 1
        """;

    // ---- finding ------------------------------------------------------------------

    [Fact]
    public void Libraries_are_found_and_environments_tests_and_helpers_are_passed_over()
    {
        var folder = CustomNodeTests.Scratch();
        var library = Write(folder, "vision_nodes.py", Library);
        Write(folder, "helpers.py", "import sirius_nodes\n\ndef area(r):\n    return r.width * r.height\n");
        Write(folder, "test_vision_nodes.py", Library);
        Write(Path.Combine(folder, ".venv", "Lib", "site-packages"), "other.py", Library);
        Write(Path.Combine(folder, "sub"), "more_nodes.py", Library);

        Assert.Equal(
            new[] { Path.Combine(folder, "sub", "more_nodes.py"), library }.Order(StringComparer.OrdinalIgnoreCase),
            PythonLibraries.Find(folder));

        Assert.Contains(library, PluginLoader.FindLibraries(folder));
    }

    [Fact]
    public void A_venv_beside_the_library_is_preferred_to_PATH()
    {
        var folder = CustomNodeTests.Scratch();
        var script = Write(folder, "nodes.py", Library);
        var python = Write(Path.Combine(folder, ".venv", "Scripts"), "python.exe", "");

        var found = PythonInterpreter.Find(script, out var problem);

        Assert.Null(problem);
        Assert.Equal(python, found!.Executable);
    }

    [Fact]
    public void A_sirius_json_naming_a_missing_python_says_so()
    {
        var folder = CustomNodeTests.Scratch();
        var script = Write(folder, "nodes.py", Library);
        Write(folder, "sirius.json", """{ "python": "C:\\no\\such\\env" }""");

        Assert.Null(PythonInterpreter.Find(script, out var problem));
        Assert.Contains(@"C:\no\such\env", problem);

        var library = PythonLibraries.Load(script);
        Assert.Empty(library.Nodes);
        Assert.Contains("does not exist", Assert.Single(library.Problems));
    }

    // ---- loading ------------------------------------------------------------------

    [Fact]
    public void Loading_describes_the_nodes_and_reports_the_ones_that_are_wrong()
    {
        if (Load(out var library) is not { } nodes)
            return;

        Assert.Equal(new[] { "Py Invert", "Py Count" }, nodes.Select(node => node.DisplayName));
        Assert.Contains(library.Problems, problem => problem.StartsWith("Bad: has the key 'nodots'", StringComparison.Ordinal));
        Assert.StartsWith("Python ", library.Version);

        var invert = nodes[0];
        Assert.Equal("custom/pytest.invert", invert.TypeKey);
        Assert.Equal(NodeCategory.Filtering, invert.Category);
        Assert.Equal(new[] { "level", "model", "mode", "gain" }, invert.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(typeof(int), invert.Parameters[0].ValueType);
        Assert.Equal(255, invert.Parameters[0].Attribute.Max);
        Assert.Equal(ParamEditor.ComboBox, invert.Parameters[2].Attribute.Editor);

        var node = invert.CreateInstance();
        Assert.Equal(60, invert.Parameters[0].GetValue(node));
        Assert.Equal("fast", invert.Parameters[2].GetValue(node));
        Assert.Equal(new[] { "fast", "accurate" }, ((IParameterChoices)node).ChoicesFor("mode"));
        Assert.Equal(typeof(ImageData), node.FindInput("image")!.DataType);
        Assert.Equal(typeof(ShapeSet), node.FindOutput("spots")!.DataType);

        // The connectable setting's socket, after the declared inputs.
        Assert.Equal(new[] { "image", "gain" }, node.Inputs.Select(port => port.Name));
    }

    [Fact]
    public void A_library_whose_imports_fail_says_what_is_missing_and_where()
    {
        var folder = CustomNodeTests.Scratch();
        var script = Write(folder, "needs_torch.py",
            "from sirius_nodes import node, Output\nimport no_such_package_here\n\n" +
            "@node('X', key='pytest.x')\nclass X:\n    out = Output.number()\n    def run(self):\n        return 1\n");

        if (PythonInterpreter.Find(script, out _) is null)
            return;

        var library = PythonLibraries.Load(script);

        Assert.Empty(library.Nodes);
        var problem = Assert.Single(library.Problems);
        Assert.Contains("No module named 'no_such_package_here'", problem);
        Assert.Contains("(needs_torch.py, line 2", problem);
        Assert.Contains("Install it in that environment", problem);
    }

    // ---- running ------------------------------------------------------------------

    [Fact]
    public void A_python_node_runs_in_a_graph_and_its_outputs_are_ours()
    {
        if (Load(out _) is not { } nodes)
            return;

        var (graph, node) = Wired(nodes[0], () => Gray(4, 3, 200));
        using var scheduler = CreateScheduler(graph);
        var result = scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.True(result.Failed == 0, node.StatusMessage);
        Assert.Equal(NodeStatus.Ready, node.Status);

        var image = scheduler.GetOutput<ImageData>(node.FindOutput("result")!)!;
        Assert.Equal(new System.Drawing.Size(4, 3), image.Mat.Size);
        Assert.Equal(55, CvInvoke.Mean(image.Mat).V0);

        var spots = scheduler.GetOutput<ShapeSet>(node.FindOutput("spots")!)!;
        Assert.Equal((4, 3), (spots.FrameWidth, spots.FrameHeight));
        var spot = Assert.IsType<RectShape>(spots.Shapes[0]);
        Assert.Equal(new Bounds(1, 2, 3, 4), spot.Rect);
        Assert.Equal("spot", spot.Label);
        Assert.Equal(0.75, spot.Metric("score"));
        Assert.Equal(0xFFFF0000u, spot.Colour);
        Assert.Equal(new CircleShape(new Point2(5, 6), 2) with { Metrics = spots.Shapes[1].Metrics }, spots.Shapes[1]);

        Assert.Equal("a/fast/1.5", scheduler.GetOutput<string>(node.FindOutput("note")!));
        Assert.False((bool)scheduler.GetOutput(node.FindOutput("bright")!)!);
    }

    [Fact]
    public void Load_runs_once_and_again_only_when_a_reload_setting_changes()
    {
        if (Load(out _) is not { } nodes)
            return;

        var (graph, node) = Wired(nodes[0], () => Gray(2, 2, 1));
        using var scheduler = CreateScheduler(graph);
        var parameters = node.Descriptor.Parameters;

        int Loads()
        {
            scheduler.RunAsync().GetAwaiter().GetResult();
            Assert.True(node.Status == NodeStatus.Ready, node.StatusMessage);
            return (int)scheduler.GetOutput(node.FindOutput("loads")!)!;
        }

        Assert.Equal(1, Loads());
        parameters.Single(parameter => parameter.Name == "level").SetValue(node, 61);
        Assert.Equal(1, Loads());
        parameters.Single(parameter => parameter.Name == "model").SetValue(node, "b");
        Assert.Equal(2, Loads());
        Assert.StartsWith("b/", scheduler.GetOutput<string>(node.FindOutput("note")!));
    }

    [Fact]
    public void A_python_error_shows_on_the_node_with_its_file_and_line_and_a_warning_as_a_warning()
    {
        if (Load(out _) is not { } nodes)
            return;

        var (graph, node) = Wired(nodes[0], () => Gray(2, 2, 1));
        using var scheduler = CreateScheduler(graph);
        var level = node.Descriptor.Parameters.Single(parameter => parameter.Name == "level");

        level.SetValue(node, 13);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Error, node.Status);
        Assert.Contains("ValueError: unlucky level", node.StatusMessage);
        Assert.Matches(@"\(pytest_nodes\.py, line \d+, in run\)", node.StatusMessage);

        level.SetValue(node, 7);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Warning, node.Status);
        Assert.Equal("seven is low", node.StatusMessage);
    }

    [Fact]
    public void A_python_crash_is_reported_and_the_next_run_starts_it_again()
    {
        if (Load(out _) is not { } nodes)
            return;

        var (graph, node) = Wired(nodes[0], () => Gray(2, 2, 1));
        using var scheduler = CreateScheduler(graph);
        var level = node.Descriptor.Parameters.Single(parameter => parameter.Name == "level");

        level.SetValue(node, 99);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Error, node.Status);
        Assert.Contains("stopped while running Py Invert (exit code 3)", node.StatusMessage);
        Assert.Contains("started again on the next run", node.StatusMessage);

        level.SetValue(node, 60);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.True(node.Status == NodeStatus.Ready, node.StatusMessage);
    }

    [Fact]
    public void A_connected_setting_is_driven_by_its_wire()
    {
        if (Load(out _) is not { } nodes)
            return;

        var (graph, node) = Wired(nodes[0], () => Gray(2, 2, 1));
        var number = new ValueNode(2.25);
        graph.Add(number);
        Assert.True(graph.TryConnect(number.Out, node.FindInput("gain")!).Success);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal("a/fast/2.25", scheduler.GetOutput<string>(node.FindOutput("note")!));
    }

    [Fact]
    public void Shapes_go_into_python_with_their_labels()
    {
        if (Load(out _) is not { } nodes)
            return;

        var graph = new VisionGraph();
        var source = new ValueNode(new ShapeSet(
            [new PointShape(new Point2(1, 1)) { Label = "a" }, new PolygonShape([new(0, 0), new(4, 0), new(0, 4)]) { Label = "b" }],
            10, 10));
        var node = nodes[1].CreateInstance();
        graph.Add(source);
        graph.Add(node);
        Assert.True(graph.TryConnect(source.Out, node.FindInput("shapes")!).Success);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.True(node.Status == NodeStatus.Ready, node.StatusMessage);
        Assert.Equal(2, (int)scheduler.GetOutput(node.FindOutput("count")!)!);
        Assert.Equal("a,b", scheduler.GetOutput<string>(node.FindOutput("labels")!));
    }

    [Fact]
    public void A_recipe_saves_and_reopens_a_python_node_with_its_settings()
    {
        var folder = CustomNodeTests.Scratch();
        var script = Write(folder, "pytest_saved.py", Library.Replace("pytest.", "pytest-saved.", StringComparison.Ordinal));

        if (PythonInterpreter.Find(script, out _) is null)
            return;

        var library = PluginLoader.Install(script);
        Assert.Equal(2, library.Nodes.Count);

        var graph = new VisionGraph();
        var node = NodeCatalog.FindByTypeKey("custom/pytest-saved.invert")!.CreateInstance();
        node.Descriptor.Parameters.Single(parameter => parameter.Name == "mode").SetValue(node, "accurate");
        node.Descriptor.Parameters.Single(parameter => parameter.Name == "gain").SetValue(node, 3.0);
        graph.Add(node);

        var loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));

        Assert.False(loaded.HasWarnings, string.Join(Environment.NewLine, loaded.Warnings));
        var reopened = Assert.IsType<PythonNode>(Assert.Single(loaded.Graph.Nodes));
        Assert.Equal("accurate", reopened.Descriptor.Parameters.Single(parameter => parameter.Name == "mode").GetValue(reopened));
        Assert.Equal(3.0, reopened.Descriptor.Parameters.Single(parameter => parameter.Name == "gain").GetValue(reopened));
    }

    // ---- helpers ------------------------------------------------------------------

    /// <summary>The test library's nodes, or null when this machine has no Python.</summary>
    private static IReadOnlyList<NodeDescriptor>? Load(out PluginLibrary library)
    {
        library = Loaded.Value!;
        return library is null ? null : library.Nodes;
    }

    private static readonly Lazy<PluginLibrary?> Loaded = new(() =>
    {
        var script = Write(CustomNodeTests.Scratch(), "pytest_nodes.py", Library);
        return PythonInterpreter.Find(script, out _) is null ? null : PythonLibraries.Load(script);
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static string Write(string folder, string name, string text)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static ImageData Gray(int width, int height, byte value)
    {
        var mat = new Mat(height, width, DepthType.Cv8U, 1);
        mat.SetTo(new MCvScalar(value));
        return ImageData.Adopt(mat);
    }

    private static (VisionGraph Graph, VisionNode Node) Wired(NodeDescriptor descriptor, Func<ImageData> frame)
    {
        var graph = new VisionGraph();
        var source = new FrameNode(frame);
        var node = descriptor.CreateInstance();

        graph.Add(source);
        graph.Add(node);
        Assert.True(graph.TryConnect(source.Out, node.FindInput("image")!).Success);

        return (graph, node);
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

    private sealed class FrameNode : VisionNode
    {
        private readonly Func<ImageData> _frame;

        public FrameNode(Func<ImageData> frame)
        {
            _frame = frame;
            Out = AddOutput<ImageData>("Out");
        }

        public OutputPort Out { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(Out, _frame());
    }

    private sealed class ValueNode : VisionNode
    {
        private readonly object _value;

        public ValueNode(object value)
        {
            _value = value;
            Out = AddOutput(value.GetType(), "Out");
        }

        public OutputPort Out { get; }

        protected override void OnEvaluate(EvalContext context) => context.Forward(Out, _value);
    }
}
