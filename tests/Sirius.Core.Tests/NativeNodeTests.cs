using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
/// Builds the C++ libraries the tests load, with the Visual C++ compiler
/// this machine has - and builds nothing, so the tests pass without
/// checking anything, on a machine with none.
/// </summary>
internal static class NativeTestLibrary
{
    private static readonly string Sources = Path.Combine(AppContext.BaseDirectory, "cpp-tests", "Native");
    private static readonly string Headers = Path.Combine(AppContext.BaseDirectory, "cpp");

    private static readonly Lazy<string?> Compiler = new(FindCompiler);
    private static readonly Dictionary<string, string?> Built = new(StringComparer.Ordinal);

    /// <summary>The batch file that puts cl.exe on PATH, or null with no compiler.</summary>
    private static string? FindCompiler()
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");

        if (!File.Exists(vswhere))
            return null;

        var start = new ProcessStartInfo(vswhere)
        {
            Arguments = "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(start)!;
        var installation = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        var vcvars = Path.Combine(installation, "VC", "Auxiliary", "Build", "vcvars64.bat");
        return installation.Length > 0 && File.Exists(vcvars) ? vcvars : null;
    }

    /// <summary>
    /// Builds <paramref name="source"/> into a DLL, once for each version of
    /// the source and the headers, and returns it - or null with no compiler.
    /// A source that does not compile fails the test with the compiler's words.
    /// </summary>
    public static string? Build(string source, string output, string options = "")
    {
        lock (Built)
        {
            var key = $"{source}|{output}|{options}";

            if (!Built.TryGetValue(key, out var dll))
                Built[key] = dll = Compile(source, output, options);

            return dll;
        }
    }

    private static string? Compile(string source, string output, string options)
    {
        if (Compiler.Value is not { } vcvars)
            return null;

        var sourcePath = Path.Combine(Sources, source);

        var stamp = SHA256.HashData(Encoding.UTF8.GetBytes(
            File.ReadAllText(sourcePath) +
            File.ReadAllText(Path.Combine(Headers, "sirius_node.h")) +
            File.ReadAllText(Path.Combine(Headers, "sirius_node.hpp")) +
            options));

        var folder = Path.Combine(
            Path.GetTempPath(), "sirius-native-tests", Convert.ToHexString(stamp)[..16], Path.GetFileNameWithoutExtension(output));
        var dll = Path.Combine(folder, output);

        if (File.Exists(dll))
            return dll;

        Directory.CreateDirectory(folder);

        var language = source.EndsWith(".c", StringComparison.Ordinal) ? "/TC" : "/EHsc /std:c++14";
        var script = Path.Combine(folder, "build.cmd");

        File.WriteAllText(script,
            $"@echo off\r\ncall \"{vcvars}\" >nul 2>nul\r\n" +
            $"cl /nologo /LD /O2 /W4 /WX {language} {options} /I \"{Headers}\" \"{sourcePath}\" /Fe:\"{dll}\"\r\n");

        var start = new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(start)!;
        var said = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || !File.Exists(dll))
        {
            File.Delete(dll);
            throw new InvalidOperationException($"{source} did not compile:{Environment.NewLine}{said}");
        }

        return dll;
    }

    /// <summary>
    /// A copy of a built DLL in a folder of its own. Windows loads each copy
    /// as a library of its own, so a test that crashes one leaves the rest.
    /// </summary>
    public static string Copy(string dll, string? folder = null)
    {
        folder ??= CustomNodeTests.Scratch();
        var copy = Path.Combine(folder, Path.GetFileName(dll));
        File.Copy(dll, copy, overwrite: true);
        return copy;
    }

    public static string? TestNodes => Build("test_nodes.cpp", "CppTestNodes.dll");

    /// <summary>
    /// A library that needs a DLL of its own beside it, and that DLL - or
    /// null with no compiler.
    /// </summary>
    /// <remarks>
    /// One pair for each test that uses one, under names of their own:
    /// Windows gives a library a DLL already loaded under the name it asks
    /// for, wherever that was loaded from, so a helper one test has loaded
    /// is never missing for the next.
    /// </remarks>
    public static (string Helper, string Library)? Needs(string which)
    {
        if (Build("helper.cpp", $"sirius_test_helper_{which}.dll") is not { } helper)
            return null;

        return (helper, Build("needs_helper.cpp", $"CppNeeds_{which}.dll", $"\"{Path.ChangeExtension(helper, ".lib")}\"")!);
    }
}

/// <summary>
/// Custom nodes written in C++: a library is found by its export, loaded
/// into this process, described, and run in a graph - and what goes wrong
/// in it, up to a hardware fault, is the node's error and not the end of
/// the process running these tests.
/// </summary>
public class NativeNodeTests
{
    public NativeNodeTests() => TestCatalog.Ensure();

    // ---- finding ------------------------------------------------------------------

    [Fact]
    public void A_library_is_found_by_what_it_exports_and_other_DLLs_are_passed_over()
    {
        if (NativeTestLibrary.TestNodes is not { } built)
            return;

        var folder = CustomNodeTests.Scratch();
        var library = NativeTestLibrary.Copy(built, folder);

        // Its own dependencies sit beside it: a native DLL that is not a
        // library, and a .NET one that is not a custom node library.
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Path.Combine(folder, "version.dll"));
        File.Copy(typeof(VisionNode).Assembly.Location, Path.Combine(folder, "Sirius.Core.dll"));

        Assert.True(NativeNodeLibraries.IsLibrary(library));
        Assert.False(NativeNodeLibraries.IsLibrary(Path.Combine(folder, "version.dll")));
        Assert.False(NativeNodeLibraries.IsLibrary(Path.Combine(folder, "Sirius.Core.dll")));

        Assert.Equal(new[] { library }, PluginLoader.FindLibraries(folder));
    }

    [Fact]
    public void A_DLL_that_is_neither_kind_of_library_says_so()
    {
        var folder = CustomNodeTests.Scratch();
        var other = Path.Combine(folder, "version.dll");
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), other);

        var problem = Assert.Single(PluginLoader.Load(other).Problems);

        Assert.Contains("neither a .NET library nor a C++ node library", problem);
        Assert.Contains("sirius_node_library", problem);
    }

    // ---- loading ------------------------------------------------------------------

    [Fact]
    public void Loading_describes_the_nodes_and_reports_the_ones_that_are_wrong()
    {
        if (Load(out var library) is not { } nodes)
            return;

        Assert.Equal(new[] { "Cpp Invert", "Cpp Count" }, nodes.Select(node => node.DisplayName));
        Assert.Contains(library.Problems, problem => problem.StartsWith("Bad: has the key \"nodots\"", StringComparison.Ordinal));
        Assert.Contains("Twice: uses the name 'value' for more than one port or setting.", library.Problems);
        Assert.Equal("CppTestNodes.dll", library.Name);

        var invert = nodes[0];
        Assert.Equal("custom/cpptest.invert", invert.TypeKey);
        Assert.Equal(NodeCategory.Filtering, invert.Category);
        Assert.Equal("Inverts.", invert.Description);
        Assert.Equal("CppTestNodes.dll", invert.Origin);
        Assert.Equal(new[] { "level", "model", "mode", "gain", "weights" }, invert.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(typeof(int), invert.Parameters[0].ValueType);
        Assert.Equal(255, invert.Parameters[0].Attribute.Max);
        Assert.Equal(ParamEditor.ComboBox, invert.Parameters[2].Attribute.Editor);
        Assert.Equal(ParamEditor.FilePath, invert.Parameters[4].Attribute.Editor);
        Assert.Equal("Models|*.onnx", invert.Parameters[4].Attribute.Filter);

        var node = invert.CreateInstance();
        Assert.Equal(60, invert.Parameters[0].GetValue(node));
        Assert.Equal("fast", invert.Parameters[2].GetValue(node));
        Assert.Equal(1.5, invert.Parameters[3].GetValue(node));
        Assert.Equal(new[] { "fast", "accurate" }, ((IParameterChoices)node).ChoicesFor("mode"));
        Assert.Equal(typeof(ImageData), node.FindInput("image")!.DataType);
        Assert.Equal(typeof(ShapeSet), node.FindOutput("spots")!.DataType);

        // The connectable setting's socket, after the declared inputs.
        Assert.Equal(new[] { "image", "gain" }, node.Inputs.Select(port => port.Name));

        // An input declared optional may be left unwired.
        Assert.False(nodes[1].CreateInstance().FindInput("scale")!.IsRequired);
    }

    [Fact]
    public void A_library_in_plain_C_loads_and_runs_on_the_header_alone()
    {
        if (NativeTestLibrary.Build("c_library.c", "CNodes.dll") is not { } built)
            return;

        var library = NativeNodeLibraries.Load(NativeTestLibrary.Copy(built));
        Assert.Empty(library.Problems);

        var (graph, node) = Fed(Assert.Single(library.Nodes), "value", 21.0);
        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.True(node.Status == NodeStatus.Ready, node.StatusMessage);
        Assert.Equal(42.0, scheduler.GetOutput(node.FindOutput("out")!));
    }

    [Fact]
    public void A_library_built_for_another_interface_version_says_which()
    {
        if (NativeTestLibrary.Build("c_library.c", "CNodesWrongVersion.dll", "/DWRONG_VERSION") is not { } built)
            return;

        var library = NativeNodeLibraries.Load(NativeTestLibrary.Copy(built));

        Assert.Empty(library.Nodes);
        var problem = Assert.Single(library.Problems);
        Assert.Contains("built for version 99 of the C++ node interface", problem);
        Assert.Contains("provides version 1", problem);
    }

    [Fact]
    public void A_32_bit_DLL_is_refused_by_name_before_Windows_refuses_it_without_one()
    {
        var folder = CustomNodeTests.Scratch();
        var narrow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "version.dll");

        if (!File.Exists(narrow))
            return;

        var copy = Path.Combine(folder, "narrow.dll");
        File.Copy(narrow, copy);

        Assert.Null(NativeNodeLibrary.Open(copy, out var problem));
        Assert.Contains("built for 32-bit Windows (x86)", problem);
        Assert.Contains("Build the library for x64", problem);
    }

    /// <summary>
    /// The commonest failure of all: the library is there, and something it
    /// needs is not. Windows says "the specified module could not be found"
    /// and does not say which.
    /// </summary>
    [Fact]
    public void A_library_whose_own_DLL_is_missing_names_it_and_loads_once_it_is_back()
    {
        if (NativeTestLibrary.Needs("a") is not var (helper, built))
            return;

        var folder = CustomNodeTests.Scratch();
        var library = NativeTestLibrary.Copy(built, folder);

        var failed = PluginLoader.Install(library);

        Assert.Empty(failed.Nodes);
        var problem = Assert.Single(failed.Problems);
        Assert.Contains("it needs sirius_test_helper_a.dll, which Windows could not find", problem);
        Assert.Contains("dllFolders", problem);

        // Put beside it, and installed again: no restart.
        NativeTestLibrary.Copy(helper, folder);
        var retried = PluginLoader.Install(library);

        Assert.Empty(retried.Problems);
        var needs = Assert.Single(retried.Nodes);
        Assert.Same(retried, PluginLoader.Install(library));

        var graph = new VisionGraph();
        var node = needs.CreateInstance();
        graph.Add(node);
        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(42, scheduler.GetOutput(node.FindOutput("out")!));
    }

    [Fact]
    public void A_sirius_json_that_names_a_folder_that_is_not_there_says_so()
    {
        if (NativeTestLibrary.TestNodes is not { } built)
            return;

        var folder = CustomNodeTests.Scratch();
        var library = NativeTestLibrary.Copy(built, folder);
        File.WriteAllText(Path.Combine(folder, "sirius.json"), """{ "dllFolders": ["no-such-folder"] }""");

        var problem = Assert.Single(NativeNodeLibraries.Load(library).Problems);

        Assert.Contains("sirius.json names", problem);
        Assert.Contains("no-such-folder", problem);
    }

    [Fact]
    public void A_folder_named_in_sirius_json_is_where_Windows_then_looks()
    {
        if (NativeTestLibrary.Needs("b") is not var (helper, built))
            return;

        var folder = CustomNodeTests.Scratch();
        var library = NativeTestLibrary.Copy(built, folder);
        NativeTestLibrary.Copy(helper, Path.Combine(folder, "bin").Also(bin => Directory.CreateDirectory(bin)));
        File.WriteAllText(Path.Combine(folder, "sirius.json"), """{ "dllFolders": ["bin"] }""");

        var loaded = NativeNodeLibraries.Load(library);

        Assert.Empty(loaded.Problems);
        Assert.Single(loaded.Nodes);
    }

    // ---- running ------------------------------------------------------------------

    [Fact]
    public void A_cpp_node_runs_in_a_graph_and_its_outputs_are_ours()
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
        Assert.Equal(8, image.Bits);

        var spots = scheduler.GetOutput<ShapeSet>(node.FindOutput("spots")!)!;
        Assert.Equal((4, 3), (spots.FrameWidth, spots.FrameHeight));
        var spot = Assert.IsType<RectShape>(spots.Shapes[0]);
        Assert.Equal(new Bounds(1, 2, 3, 4), spot.Rect);
        Assert.Equal("spot", spot.Label);
        Assert.Equal(0.75, spot.Metric("score"));
        Assert.Equal(0xFFFF0000u, spot.Colour);

        var circle = Assert.IsType<CircleShape>(spots.Shapes[1]);
        Assert.Equal((new Point2(5, 6), 2.0), (circle.Centre, circle.Radius));
        Assert.Null(circle.Label);
        Assert.Null(circle.Colour);

        var polygon = Assert.IsType<PolygonShape>(spots.Shapes[2]);
        Assert.Equal(new[] { new Point2(0, 0), new Point2(4, 0), new Point2(4, 3) }, polygon.Points);
        Assert.False(polygon.Closed);

        Assert.Equal("a/fast/1.50/", scheduler.GetOutput<string>(node.FindOutput("note")!));
        Assert.False((bool)scheduler.GetOutput(node.FindOutput("bright")!)!);
        Assert.Equal(255.0, scheduler.GetOutput(node.FindOutput("top")!));
        Assert.Equal(1, scheduler.GetOutput(node.FindOutput("loads")!));
    }

    /// <summary>
    /// A 12-bit frame arrives saying so, and a result of the same type is
    /// still 12-bit without the node saying anything - as a built-in tool's is.
    /// </summary>
    [Fact]
    public void A_twelve_bit_image_arrives_with_its_bits_and_the_result_keeps_them()
    {
        if (Load(out _) is not { } nodes)
            return;

        var (graph, node) = Wired(nodes[0], () =>
        {
            var mat = new Mat(2, 3, DepthType.Cv16U, 1);
            mat.SetTo(new MCvScalar(1000));
            return ImageData.Adopt(mat, bits: 12);
        });

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.True(node.Status == NodeStatus.Ready, node.StatusMessage);
        Assert.Equal(4095.0, scheduler.GetOutput(node.FindOutput("top")!));

        var image = scheduler.GetOutput<ImageData>(node.FindOutput("result")!)!;
        Assert.Equal(DepthType.Cv16U, image.Depth);
        Assert.Equal(12, image.Bits);
        Assert.Equal(3095, CvInvoke.Mean(image.Mat).V0);
    }

    /// <summary>
    /// A region cut from a larger image is rows of that image, not adjacent
    /// to each other. It goes to the library where it is, with the larger
    /// image's stride - no copy to tidy it first.
    /// </summary>
    [Fact]
    public void A_slice_of_a_larger_image_is_read_in_place_by_its_stride()
    {
        if (Load(out _) is not { } nodes)
            return;

        using var whole = new Mat(8, 10, DepthType.Cv8U, 3);
        whole.SetTo(new MCvScalar(9, 9, 9));

        var (graph, node) = Wired(nodes[0], () =>
        {
            var slice = new Mat(whole, new System.Drawing.Rectangle(2, 1, 4, 3));
            slice.SetTo(new MCvScalar(10, 20, 30));
            Assert.False(slice.IsContinuous);
            return ImageData.Adopt(slice, ColourSpace.Bgr);
        });

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.True(node.Status == NodeStatus.Ready, node.StatusMessage);

        var image = scheduler.GetOutput<ImageData>(node.FindOutput("result")!)!;
        var mean = CvInvoke.Mean(image.Mat);

        Assert.Equal(new System.Drawing.Size(4, 3), image.Mat.Size);
        Assert.Equal((245.0, 235.0, 225.0), (mean.V0, mean.V1, mean.V2));
        Assert.Equal(ColourSpace.Bgr, image.Space);
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

        // A load that fails is the node's error, and is tried again.
        parameters.Single(parameter => parameter.Name == "model").SetValue(node, "missing");
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Error, node.Status);
        Assert.Contains("load() failed: The model 'missing' is not in ", node.StatusMessage);
        Assert.Contains(Path.GetDirectoryName(Loaded.Value!.Path)!, node.StatusMessage);

        parameters.Single(parameter => parameter.Name == "model").SetValue(node, "c");
        Assert.Equal(4, Loads());
    }

    [Fact]
    public void Each_node_on_the_canvas_has_an_object_of_its_own()
    {
        if (Load(out _) is not { } nodes)
            return;

        var graph = new VisionGraph();
        var source = new FrameNode(() => Gray(2, 2, 1));
        var first = nodes[0].CreateInstance();
        var second = nodes[0].CreateInstance();

        graph.Add(source);
        graph.Add(first);
        graph.Add(second);
        Assert.True(graph.TryConnect(source.Out, first.FindInput("image")!).Success);
        Assert.True(graph.TryConnect(source.Out, second.FindInput("image")!).Success);

        nodes[0].Parameters.Single(parameter => parameter.Name == "model").SetValue(second, "other");

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.StartsWith("a/", scheduler.GetOutput<string>(first.FindOutput("note")!));
        Assert.StartsWith("other/", scheduler.GetOutput<string>(second.FindOutput("note")!));
        Assert.Equal(1, scheduler.GetOutput(first.FindOutput("loads")!));
        Assert.Equal(1, scheduler.GetOutput(second.FindOutput("loads")!));
    }

    [Fact]
    public void Shapes_go_in_with_their_labels_and_measurements_and_come_back_the_same()
    {
        if (Load(out _) is not { } nodes)
            return;

        var shapes = new ShapeSet(
            [
                new RectShape(new Bounds(1, 2, 3, 4)) { Label = "a", Metrics = new Dictionary<string, double> { ["area"] = 12 } },
                new OrientedRectShape(new Point2(5, 6), 7, 8, 30),
                new EllipseShape(new Point2(1, 1), 4, 2, 15) { Colour = 0xFF00FF00 },
                new LineShape(new Point2(0, 0), new Point2(3, 4)) { Label = "b" },
                new PointShape(new Point2(9, 9)),
                new PolygonShape([new Point2(0, 0), new Point2(2, 0), new Point2(2, 2)]),
            ],
            640, 480);

        var (graph, node) = Fed(nodes[1], "shapes", shapes);
        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.True(node.Status == NodeStatus.Ready, node.StatusMessage);
        Assert.Equal(6, scheduler.GetOutput(node.FindOutput("count")!));
        Assert.Equal("a:area,-,-,b,-,-", scheduler.GetOutput<string>(node.FindOutput("labels")!));

        // Nothing wired to the optional input: the node's own fallback.
        Assert.Equal(12.0, scheduler.GetOutput(node.FindOutput("scaled")!));

        var echo = scheduler.GetOutput<ShapeSet>(node.FindOutput("echo")!)!;
        Assert.Equal(6, echo.Count);

        for (var i = 0; i < shapes.Count; i++)
        {
            var (sent, back) = (shapes.Shapes[i], echo.Shapes[i]);
            Assert.Equal(sent.GetType(), back.GetType());
            Assert.Equal(sent.Label, back.Label);
            Assert.Equal(sent.Colour, back.Colour);
            Assert.Equal(sent.Metrics, back.Metrics);
        }

        Assert.Equal(((RectShape)shapes.Shapes[0]).Rect, ((RectShape)echo.Shapes[0]).Rect);
        Assert.Equal(30, ((OrientedRectShape)echo.Shapes[1]).AngleDegrees);
        Assert.Equal(((PolygonShape)shapes.Shapes[5]).Points, ((PolygonShape)echo.Shapes[5]).Points);
        Assert.True(((PolygonShape)echo.Shapes[5]).Closed);

        // A shapes output nothing was added to is an empty set, not nothing.
        Assert.Equal(0, scheduler.GetOutput<ShapeSet>(node.FindOutput("none")!)!.Count);
    }

    // ---- going wrong ----------------------------------------------------------------

    [Theory]
    [InlineData(13, "unlucky level")]
    [InlineData(14, "It threw an exception that says nothing about itself.")]
    [InlineData(15, "Vendor: the licence dongle is missing")]
    [InlineData(21, "It has no setting or input called 'no_such_setting'. Its settings: level, model, mode, gain, weights; its inputs: image.")]
    [InlineData(22, "'note' is a text output; set it with set_text.")]
    [InlineData(23, "'gain' is a number; read it with number().")]
    [InlineData(24, "'result' was given an image whose pixels cannot be read - freed already, or smaller than it says.")]
    [InlineData(25, "'result' was given an image whose stride, 2, is less than one row of it, 4 bytes.")]
    public void What_a_node_gets_wrong_is_its_error_and_the_next_run_is_fine(int level, string message)
    {
        if (Load(out _) is not { } nodes)
            return;

        var (graph, node) = Wired(nodes[0], () => Gray(2, 2, 1));
        using var scheduler = CreateScheduler(graph);
        var setting = node.Descriptor.Parameters.Single(parameter => parameter.Name == "level");

        setting.SetValue(node, level);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Error, node.Status);
        Assert.Equal(message, node.StatusMessage);

        setting.SetValue(node, 60);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.True(node.Status == NodeStatus.Ready, node.StatusMessage);
    }

    [Fact]
    public void A_warning_shows_as_a_warning_and_the_outputs_are_still_there()
    {
        if (Load(out _) is not { } nodes)
            return;

        var (graph, node) = Wired(nodes[0], () => Gray(2, 2, 1));
        using var scheduler = CreateScheduler(graph);

        node.Descriptor.Parameters.Single(parameter => parameter.Name == "level").SetValue(node, 7);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Warning, node.Status);
        Assert.Equal("seven is low", node.StatusMessage);
        Assert.NotNull(scheduler.GetOutput<ImageData>(node.FindOutput("result")!));
    }

    /// <summary>
    /// The reason the guard exists. Each of these ends an ordinary program;
    /// here each is one node's error, the process carries on - these tests
    /// are running in it - and the library is not called again, because
    /// nothing says what the fault did to its memory first.
    /// </summary>
    [Theory]
    [InlineData(99, "it wrote through a null pointer, in its own code (CppTestNodes.dll+0x")]
    [InlineData(98, "it divided an integer by zero, in its own code (CppTestNodes.dll+0x")]
    [InlineData(97, "it ran out of stack - recursion that does not end, or a very large local array, in its own code (CppTestNodes.dll+0x")]
    public void A_hardware_fault_is_the_nodes_error_and_stops_its_library_not_the_application(int level, string what)
    {
        if (NativeTestLibrary.TestNodes is not { } built)
            return;

        // Its own copy: stopping it must not stop the library the other tests share.
        var nodes = NativeNodeLibraries.Load(NativeTestLibrary.Copy(built)).Nodes;

        var (graph, node) = Wired(nodes[0], () => Gray(2, 2, 1));
        using var scheduler = CreateScheduler(graph);
        var setting = node.Descriptor.Parameters.Single(parameter => parameter.Name == "level");

        setting.SetValue(node, level);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Error, node.Status);
        Assert.StartsWith("crashed while running: " + what, node.StatusMessage);
        Assert.EndsWith(
            "CppTestNodes.dll has been stopped, because its memory can no longer be trusted - save your work and " +
            "restart Sirius Studio Plus.",
            node.StatusMessage);

        // It stays stopped: a good setting does not bring it back...
        setting.SetValue(node, 60);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Error, node.Status);
        Assert.Equal(
            "CppTestNodes.dll was stopped earlier, when Cpp Invert crashed, and is not used again until Sirius Studio " +
            "Plus is restarted.",
            node.StatusMessage);

        // ...and neither does another of its nodes.
        var (other, count) = Fed(nodes[1], "shapes", ShapeSet.Empty);
        using var second = CreateScheduler(other);
        second.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Error, count.Status);

        // Another library is untouched.
        if (Load(out _) is { } shared)
        {
            var (fine, working) = Wired(shared[0], () => Gray(2, 2, 1));
            using var third = CreateScheduler(fine);
            third.RunAsync().GetAwaiter().GetResult();

            Assert.True(working.Status == NodeStatus.Ready, working.StatusMessage);
        }
    }

    [Theory]
    [InlineData("fault code=C0000005 at=0000000000001234 access=0 target=0000000000000008", "it read through a null pointer, at 0x1234")]
    [InlineData("fault code=C0000005 at=0000000000001234 access=1 target=00007FF812345678", "it wrote at 0x7FF812345678, which is not memory it can use - a pointer to something already freed, or past the end of an image, at 0x1234")]
    [InlineData("fault code=C0000005 at=0000000000001234 access=8 target=0000000000000000", "it ran code through a null pointer, at 0x1234")]
    [InlineData("fault code=C000001D at=0000000000001234", "it ran an instruction this processor does not have - built for a newer processor, with AVX-512 perhaps, at 0x1234")]
    [InlineData("fault code=C0000096 at=0000000000001234", "Windows stopped it with exception 0xC0000096, at 0x1234")]
    [InlineData("", "a hardware fault")]
    [InlineData("something else entirely", "something else entirely")]
    public void A_fault_is_worded_for_whoever_has_to_find_it(string report, string expected) =>
        Assert.Equal(expected, NativeNodeLibrary.DescribeFault(report, @"C:\nodes\mine.dll"));

    /// <summary>
    /// A library built with newer tools than the runtime it runs on is
    /// cautioned about when it loads - the one way to hear of it before the
    /// fault it leads to.
    /// </summary>
    [Theory]
    [InlineData("14.51", "14.44", true)]
    [InlineData("14.44", "14.44", false)]
    [InlineData("14.29", "14.44", false)]
    [InlineData("2.41", "14.44", false)]
    [InlineData("14.51", null, false)]
    public void A_library_built_with_newer_tools_than_the_runtime_is_cautioned_about(string built, string? runtime, bool cautioned)
    {
        var note = NativeNodeLibraries.RuntimeNote(Version.Parse(built), runtime is null ? null : Version.Parse(runtime));

        Assert.Equal(cautioned, note is not null);

        if (cautioned)
            Assert.Contains("built with Visual C++ 14.51 tools, and the C++ runtime it runs on here is 14.44", note);
    }

    /// <summary>
    /// A line PC with no Visual C++ runtime installed still has the one the
    /// application brings; a library is lent it. Where one is installed, or
    /// already loaded, or beside the library, that one is left to win.
    /// </summary>
    [Fact]
    public void A_library_is_lent_the_applications_runtime_only_where_there_is_no_other()
    {
        var imports = new[] { "KERNEL32.dll", "MSVCP140.dll", "VCRUNTIME140.dll", "VCRUNTIME140_1.dll", "opencv_world4100.dll" };
        var system = Environment.SystemDirectory;

        IReadOnlyList<string> Lent(Func<string, bool> isLoaded, params string[] files) =>
            NativeNodeLibrary.RuntimeToLend(
                imports, @"C:\nodes", @"C:\app", isLoaded,
                path => files.Contains(path, StringComparer.OrdinalIgnoreCase));

        // Nothing installed, nothing loaded: the application's, wherever it keeps them.
        Assert.Equal(
            new[] { @"C:\app\MSVCP140.dll", @"C:\app\runtimes\win-x64\native\VCRUNTIME140.dll" },
            Lent(_ => false, @"C:\app\MSVCP140.dll", @"C:\app\runtimes\win-x64\native\VCRUNTIME140.dll"));

        // Installed in Windows, already loaded, or beside the library: left alone.
        Assert.Empty(Lent(_ => false, @"C:\app\MSVCP140.dll", Path.Combine(system, "MSVCP140.dll")));
        Assert.Empty(Lent(name => name == "MSVCP140.dll", @"C:\app\MSVCP140.dll"));
        Assert.Empty(Lent(_ => false, @"C:\app\MSVCP140.dll", @"C:\nodes\MSVCP140.dll"));

        // Only the runtime is lent - never a library's own DLLs.
        Assert.Empty(Lent(_ => false, @"C:\app\opencv_world4100.dll", @"C:\app\KERNEL32.dll"));
    }

    // ---- OpenCV, and the sample -----------------------------------------------------

    /// <summary>
    /// The two functions that wrap an image as a cv::Mat and back, compiled
    /// against a stand-in for OpenCV's header - this machine has no OpenCV -
    /// and an image passed through both.
    /// </summary>
    [Fact]
    public void An_image_goes_through_a_Mat_and_back_without_a_copy_of_its_own()
    {
        var fake = Path.Combine(AppContext.BaseDirectory, "cpp-tests", "Native", "fake_opencv");

        if (NativeTestLibrary.Build("opencv_glue.cpp", "CppThrough.dll", $"/I \"{fake}\"") is not { } built)
            return;

        var library = NativeNodeLibraries.Load(NativeTestLibrary.Copy(built));
        Assert.Empty(library.Problems);

        var (graph, node) = Wired(library.Nodes[0], () =>
        {
            var mat = new Mat(3, 5, DepthType.Cv16U, 3);
            mat.SetTo(new MCvScalar(100, 200, 300));
            return ImageData.Adopt(mat, ColourSpace.Bgr, bits: 10);
        });

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.True(node.Status == NodeStatus.Ready, node.StatusMessage);

        // Depth 2 is CV_16U, three channels.
        Assert.Equal(23, scheduler.GetOutput(node.FindOutput("type")!));

        var image = scheduler.GetOutput<ImageData>(node.FindOutput("result")!)!;
        var mean = CvInvoke.Mean(image.Mat);

        Assert.Equal((100.0, 200.0, 300.0), (mean.V0, mean.V1, mean.V2));
        Assert.Equal((DepthType.Cv16U, 10, ColourSpace.Bgr), (image.Depth, image.Bits, image.Space));
    }

    [Fact]
    public void The_sample_library_builds_and_finds_the_spots()
    {
        if (NativeTestLibrary.Build("sample_nodes.cpp", "SampleCppNodes.dll") is not { } built)
            return;

        var library = NativeNodeLibraries.Load(NativeTestLibrary.Copy(built));

        Assert.Empty(library.Problems);
        Assert.Equal(new[] { "Dark Spots (C++)", "Sharpness (C++)" }, library.Nodes.Select(node => node.DisplayName));
        Assert.Equal(NodeCategory.Blob, library.Nodes[0].Category);

        // Two dark spots on a light ground, and a speck too small to count.
        ImageData Frame()
        {
            var mat = new Mat(60, 80, DepthType.Cv8U, 3);
            mat.SetTo(new MCvScalar(200, 200, 200));
            Fill(mat, new System.Drawing.Rectangle(10, 10, 8, 6), 20);
            Fill(mat, new System.Drawing.Rectangle(40, 30, 10, 10), 0);
            Fill(mat, new System.Drawing.Rectangle(70, 50, 2, 2), 0);
            return ImageData.Adopt(mat, ColourSpace.Bgr);

            static void Fill(Mat image, System.Drawing.Rectangle where, byte value)
            {
                using var region = new Mat(image, where);
                region.SetTo(new MCvScalar(value, value, value));
            }
        }

        var (graph, spots) = Wired(library.Nodes[0], Frame);
        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.True(spots.Status == NodeStatus.Ready, spots.StatusMessage);
        Assert.Equal(2, scheduler.GetOutput(spots.FindOutput("count")!));

        var found = scheduler.GetOutput<ShapeSet>(spots.FindOutput("spots")!)!;
        Assert.Equal((80, 60), (found.FrameWidth, found.FrameHeight));
        Assert.Equal(new Bounds(10, 10, 8, 6), ((RectShape)found.Shapes[0]).Rect);
        Assert.Equal("48 px", found.Shapes[0].Label);
        Assert.Equal(100, found.Shapes[1].Metric("area"));

        var mask = scheduler.GetOutput<ImageData>(spots.FindOutput("mask")!)!;
        Assert.Equal((80, 60, 1), (mask.Width, mask.Height, mask.Channels));
        Assert.Equal(48 + 100 + 4, CvInvoke.CountNonZero(mask.Mat));

        // The level raised above everything: nothing found, said as a warning.
        spots.Descriptor.Parameters.Single(parameter => parameter.Name == "level").SetValue(spots, 0.0);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Warning, spots.Status);
        Assert.Equal("Nothing is darker than the level.", spots.StatusMessage);

        var (focus, sharpness) = Wired(library.Nodes[1], Frame);
        using var second = CreateScheduler(focus);
        second.RunAsync().GetAwaiter().GetResult();

        Assert.True(sharpness.Status == NodeStatus.Ready, sharpness.StatusMessage);
        Assert.True((double)second.GetOutput(sharpness.FindOutput("sharpness")!)! > 100);
        Assert.True((bool)second.GetOutput(sharpness.FindOutput("sharp")!)!);
    }

    /// <summary>
    /// Installed, a C++ node is in the catalogue under its key like any
    /// other, which is all a recipe needs to save it and find it again.
    /// </summary>
    [Fact]
    public void A_recipe_saves_and_reopens_a_cpp_node_with_its_settings()
    {
        if (NativeTestLibrary.Build("sample_nodes.cpp", "SampleCppNodes.dll") is not { } built)
            return;

        var library = PluginLoader.Install(NativeTestLibrary.Copy(built));
        Assert.Equal(2, library.Nodes.Count);
        Assert.Contains(library, PluginLoader.Libraries);

        var graph = new VisionGraph();
        var node = NodeCatalog.FindByTypeKey("custom/sample.dark-spots-cpp")!.CreateInstance();
        node.Descriptor.Parameters.Single(parameter => parameter.Name == "level").SetValue(node, 77.5);
        node.Descriptor.Parameters.Single(parameter => parameter.Name == "min_area").SetValue(node, 5);
        graph.Add(node);

        var loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));

        Assert.False(loaded.HasWarnings, string.Join(Environment.NewLine, loaded.Warnings));
        var reopened = Assert.IsType<NativeNode>(Assert.Single(loaded.Graph.Nodes));
        Assert.Equal(77.5, reopened.Descriptor.Parameters.Single(parameter => parameter.Name == "level").GetValue(reopened));
        Assert.Equal(5, reopened.Descriptor.Parameters.Single(parameter => parameter.Name == "min_area").GetValue(reopened));

        // Where it came from, for the toolbox's tooltip and its search.
        Assert.Equal("SampleCppNodes.dll", reopened.Descriptor.Origin);
        Assert.True(Sirius.Studio.App.ViewModels.MainViewModel.MatchesToolboxSearch(reopened.Descriptor, "samplecpp"));
    }

    // ---- helpers ------------------------------------------------------------------

    private static IReadOnlyList<NodeDescriptor>? Load(out PluginLibrary library)
    {
        library = Loaded.Value!;
        return library is null ? null : library.Nodes;
    }

    private static readonly Lazy<PluginLibrary?> Loaded = new(
        () => NativeTestLibrary.TestNodes is { } built ? NativeNodeLibraries.Load(NativeTestLibrary.Copy(built)) : null,
        LazyThreadSafetyMode.ExecutionAndPublication);

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

    /// <summary>A node with one value fed to one of its inputs.</summary>
    private static (VisionGraph Graph, VisionNode Node) Fed(NodeDescriptor descriptor, string input, object value)
    {
        var graph = new VisionGraph();
        var source = new ValueNode(value);
        var node = descriptor.CreateInstance();

        graph.Add(source);
        graph.Add(node);
        Assert.True(graph.TryConnect(source.Out, node.FindInput(input)!).Success);

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

internal static class NativeTestExtensions
{
    /// <summary>Does something with a value on the way past.</summary>
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
