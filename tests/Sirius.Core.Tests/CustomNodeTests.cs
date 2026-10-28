using System.IO;
using System.Runtime.Loader;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Graph.Undo;
using Sirius.Core.Nodes;
using Sirius.Core.Serialization;
using Sirius.Core.Shapes;
using Sirius.Plugins;
using Sirius.Plugins.Testing;
using Sirius.Samples;
using Sirius.Vision;
using Sirius.Vision.Plugins;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The custom node interface: what a plugin author sees, and what the
/// application does with what they wrote.
/// </summary>
/// <remarks>
/// This is a contract handed to other teams, so the tests are mostly about
/// its edges - what is refused, and with what message - because the person
/// meeting those edges cannot read this repository to find out why.
/// </remarks>
public class CustomNodeTests
{
    public CustomNodeTests() => TestCatalog.Ensure();

    // ---- Picture --------------------------------------------------------------

    [Fact]
    public void An_allocated_picture_is_contiguous_and_writable()
    {
        var picture = Picture.Allocate(7, 3, 3, PixelType.UInt16, ColourLayout.Bgr);

        Assert.Equal(7 * 3 * 2, picture.Stride);
        Assert.Equal(21, picture.RowLength);
        Assert.False(picture.IsReadOnly);
        Assert.Equal(21 * 3, picture.WritableSamples<ushort>().Length);
    }

    /// <summary>
    /// Reading 16-bit samples as bytes would silently halve every value and
    /// double the row length. Refused, with the type it should have been.
    /// </summary>
    [Fact]
    public void A_row_has_to_be_read_as_the_type_it_holds()
    {
        var picture = Picture.Allocate(4, 4, pixels: PixelType.UInt16);

        var error = Assert.Throws<InvalidOperationException>(() => picture.Row<byte>(0).Length);

        Assert.Contains("ushort", error.Message);
    }

    [Fact]
    public void A_layout_has_to_agree_with_the_channel_count()
    {
        var error = Assert.Throws<ArgumentException>(() => Picture.Allocate(4, 4, 1, layout: ColourLayout.Bgr));

        Assert.Contains("3 channels", error.Message);
    }

    /// <summary>
    /// An input is the same buffer every other consumer is reading, so a
    /// node writing into it would corrupt nodes it has never heard of.
    /// </summary>
    [Fact]
    public void An_input_picture_cannot_be_written()
    {
        var input = Picture.Allocate(4, 4).AsInput();

        var error = Assert.Throws<InvalidOperationException>(() => input.WritableRow<byte>(0).Length);

        Assert.Contains("Picture.Allocate", error.Message);
    }

    [Fact]
    public void A_row_outside_the_picture_is_refused()
    {
        var picture = Picture.Allocate(4, 4);

        Assert.Throws<ArgumentOutOfRangeException>(() => picture.Row<byte>(4).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => picture.Row<byte>(-1).Length);
    }

    [Fact]
    public void A_copy_is_independent_and_writable()
    {
        var original = Filled(4, 4, 10).AsInput();
        var copy = original.Copy();

        copy.WritableRow<byte>(0)[0] = 99;

        Assert.False(copy.IsReadOnly);
        Assert.Equal(10, original.Row<byte>(0)[0]);
    }

    // ---- the sample nodes, run without the application -----------------------

    [Fact]
    public void Gamma_of_one_hands_the_input_back()
    {
        var run = TestRun.For(new GammaNode { Gamma = 1.0 })
            .Supply("In", Filled(4, 4, 77))
            .Execute();

        Assert.Equal(77, run.Published<Picture>("Out").Row<byte>(0)[0]);
    }

    [Theory]
    [InlineData(PixelType.Byte8, 128, 64)]
    [InlineData(PixelType.UInt16, 32768, 16384)]
    public void Gamma_two_squares_the_mid_tones(PixelType pixels, int value, int expected)
    {
        var picture = Picture.Allocate(4, 4, pixels: pixels);

        if (pixels == PixelType.Byte8)
            picture.WritableSamples<byte>().Fill((byte)value);
        else
            picture.WritableSamples<ushort>().Fill((ushort)value);

        var output = TestRun.For(new GammaNode { Gamma = 2.0 })
            .Supply("In", picture)
            .Execute()
            .Published<Picture>("Out");

        var actual = pixels == PixelType.Byte8
            ? output.Row<byte>(0)[0]
            : output.Row<ushort>(0)[0];

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void A_wire_into_a_setting_overrides_what_was_typed()
    {
        var output = TestRun.For(new GammaNode { Gamma = 1.0 })
            .Supply("In", Filled(4, 4, 128))
            .Wire("Gamma", 2.0)
            .Execute()
            .Published<Picture>("Out");

        Assert.Equal(64, output.Row<byte>(0)[0]);
    }

    [Fact]
    public void Bright_region_boxes_what_is_above_the_level()
    {
        var picture = Picture.Allocate(10, 10);
        for (var y = 3; y < 5; y++)
            picture.WritableRow<byte>(y).Slice(2, 3).Fill(255);

        var run = TestRun.For(new BrightRegionNode { Level = 0.5 })
            .Supply("In", picture)
            .Execute();

        var box = Assert.IsType<RectShape>(Assert.Single(run.Published<ShapeSet>("Region").Shapes));

        Assert.Equal(new Bounds(2, 3, 3, 2), box.Rect);
        Assert.Equal(0.06, run.Published<double>("Fraction"), 6);
        Assert.True(run.Published<bool>("Found"));
    }

    [Fact]
    public void Ratio_reads_numbers_and_warns_about_dividing_by_zero()
    {
        var run = TestRun.For(new RatioNode())
            .Supply("A", 3.0)
            .Supply("B", 0.0)
            .Execute();

        Assert.True(double.IsNaN(run.Published<double>("Ratio")));
        Assert.Contains("zero", Assert.Single(run.Warnings));
    }

    /// <summary>
    /// An integer output feeding a double input is how a Count reaches a
    /// division. The application converts; so must the test harness.
    /// </summary>
    [Fact]
    public void An_integer_can_feed_a_number_input()
    {
        var run = TestRun.For(new RatioNode { Decimals = 2 })
            .Supply("A", 1)
            .Supply("B", 3)
            .Execute();

        Assert.Equal(0.33, run.Published<double>("Ratio"));
    }

    [Fact]
    public void A_required_input_left_out_is_a_test_failure_not_a_null()
    {
        var error = Assert.Throws<InvalidOperationException>(() => TestRun.For(new GammaNode()).Execute());

        Assert.Contains("In", error.Message);
    }

    [Fact]
    public void Supplying_the_wrong_type_says_which_type_is_wanted()
    {
        var error = Assert.Throws<ArgumentException>(() => TestRun.For(new RatioNode()).Supply("A", "three"));

        Assert.Contains("Double", error.Message);
    }

    // ---- the guide's examples ---------------------------------------------------

    /// <summary>
    /// The test in docs/custom-nodes.md section 7, against the node in
    /// section 3. The guide is what a team learns the interface from, so an
    /// example in it that no longer compiles or no longer passes is a bug.
    /// </summary>
    [Fact]
    public void The_guides_example_passes_its_own_test()
    {
        var input = Picture.Allocate(16, 16);
        input.WritableSamples<byte>().Fill(100);

        var run = TestRun.For(new LiftNode { Amount = 20 })
            .Supply("In", input)
            .Execute();

        Assert.Equal(120, run.Published<Picture>("Out").Row<byte>(0)[0]);
        Assert.Equal(120.0, run.Published<double>("Mean"));
    }

    [Fact]
    public void The_guides_example_passes_other_sample_types_through_with_a_warning()
    {
        var run = TestRun.For(new LiftNode())
            .Supply("In", Picture.Allocate(4, 4, pixels: PixelType.UInt16))
            .Execute();

        Assert.True(run.WasPublished("Out"));
        Assert.Contains("8-bit", Assert.Single(run.Warnings));
    }

    [Fact]
    public void The_guides_example_loads_as_the_application_would() =>
        Assert.Equal(NodeCategory.Basic, Describe(typeof(LiftNode)).Category);

    // ---- what is refused when a library loads ---------------------------------

    [Theory]
    [InlineData("acme.flatfield", true)]
    [InlineData("acme.vision.flatField-2", true)]
    [InlineData("flatfield", false)]
    [InlineData("acme flat.field", false)]
    [InlineData("acme/flat.field", false)]
    [InlineData("", false)]
    public void A_key_is_written_vendor_dot_name(string key, bool acceptable) =>
        Assert.Equal(acceptable, CustomNodeType.KeyProblem(key) is null);

    [Fact]
    public void A_node_without_the_attribute_is_named_rather_than_ignored() =>
        Assert.Contains(Problems(typeof(NoAttributeNode)), problem => problem.Contains("[CustomNode]"));

    [Fact]
    public void A_node_without_a_key_is_refused() =>
        Assert.Contains(Problems(typeof(NoKeyNode)), problem => problem.Contains("needs a TypeKey"));

    [Fact]
    public void A_port_type_nothing_can_wire_is_refused_with_the_list_of_types()
    {
        var problem = Assert.Single(Problems(typeof(BadPortNode)));

        Assert.Contains("DateTime", problem);
        Assert.Contains("Picture", problem);
    }

    [Fact]
    public void A_setting_nothing_can_edit_is_refused()
    {
        var problems = Problems(typeof(BadSettingNode));

        Assert.Contains(problems, problem => problem.Contains("'When'") && problem.Contains("DateTime"));
        Assert.Contains(problems, problem => problem.Contains("'Maybe'") && problem.Contains("nullable"));
        Assert.Contains(problems, problem => problem.Contains("'Fixed'") && problem.Contains("get and set"));
    }

    [Fact]
    public void A_constructor_that_throws_is_reported_with_its_message() =>
        Assert.Contains(Problems(typeof(ThrowingNode)), problem => problem.Contains("no camera"));

    [Fact]
    public void An_enum_cannot_be_connectable() =>
        Assert.Contains(Problems(typeof(ConnectableEnumNode)), problem => problem.Contains("enum"));

    [Fact]
    public void A_connectable_setting_cannot_share_an_input_name() =>
        Assert.Contains(Problems(typeof(ClashingNode)), problem => problem.Contains("'Level'"));

    [Fact]
    public void Every_toolbox_group_is_a_category()
    {
        foreach (var group in Enum.GetValues<NodeGroup>())
            Assert.True(Enum.TryParse<NodeCategory>(group.ToString(), out _), $"{group} has no category");
    }

    // ---- inside the application ----------------------------------------------

    [Fact]
    public void Settings_become_parameters_with_the_editors_they_ask_for()
    {
        var descriptor = Describe(typeof(BrightRegionNode));

        Assert.Equal("custom/samples.brightRegion", descriptor.TypeKey);
        Assert.Equal(NodeCategory.Analysis, descriptor.Category);
        Assert.Equal("test library", descriptor.Origin);

        var level = descriptor.Parameters.Single(parameter => parameter.Name == "Level");
        Assert.Equal(ParamEditor.Slider, level.Editor);
        Assert.True(level.Attribute.Connectable);

        var colour = descriptor.Parameters.Single(parameter => parameter.Name == "Colour");
        Assert.Equal(ParamEditor.Colour, colour.Editor);
        Assert.Equal("Display", colour.Group);
    }

    [Fact]
    public void A_node_with_no_icon_gets_the_custom_node_icon() =>
        Assert.Equal(CustomNodeType.DefaultIcon, Describe(typeof(BrightRegionNode)).Icon);

    [Fact]
    public void Ports_carrying_pictures_wire_to_built_in_images()
    {
        var node = Describe(typeof(GammaNode)).CreateInstance();

        Assert.IsType<CustomNodeHost>(node);
        Assert.Equal(typeof(ImageData), node.FindInput("In")!.DataType);
        Assert.Equal(typeof(ImageData), node.FindOutput("Out")!.DataType);

        // The connectable setting's socket, below the node's own.
        Assert.Equal(new[] { "In", "Gamma" }, node.Inputs.Select(port => port.Name));
    }

    [Fact]
    public void A_custom_node_runs_in_a_graph_beside_built_in_ones()
    {
        var (graph, source, node) = Wired(typeof(GammaNode), () => Gray(8, 8, 128));
        ((GammaNode)((CustomNodeHost)node).Plugin).Gamma = 2.0;

        using var scheduler = CreateScheduler(graph);
        var result = scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(0, result.Failed);
        Assert.Equal(64, CvInvoke.Mean(scheduler.GetOutput<ImageData>(node.FindOutput("Out")!)!.Mat).V0);
    }

    /// <summary>
    /// The Gamma sample hands the input back at 1.0. That should cost
    /// nothing - the same frame, not a copy of it.
    /// </summary>
    [Fact]
    public void Publishing_an_input_passes_it_through_without_copying()
    {
        var (graph, source, node) = Wired(typeof(GammaNode), () => Gray(8, 8, 50));

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Same(
            scheduler.GetOutput<ImageData>(source.Out),
            scheduler.GetOutput<ImageData>(node.FindOutput("Out")!));
    }

    /// <summary>
    /// A slice of a larger frame has rows that are not next to each other;
    /// a 16-bit signed edge image has a sample type a picture does not. Both
    /// are converted rather than refused, so a plugin sees one shape of data.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frames_a_picture_cannot_hold_are_converted_on_the_way_in(bool signed)
    {
        var (graph, _, node) = Wired(typeof(EchoNode), () =>
        {
            using var whole = new Mat(20, 20, signed ? DepthType.Cv16S : DepthType.Cv8U, 1);
            whole.SetTo(new MCvScalar(signed ? -5 : 9));

            using var slice = new Mat(whole, new System.Drawing.Rectangle(3, 4, 6, 5));
            return ImageData.CopyOf(signed ? whole : slice);
        });

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Ready, node.Status);
        Assert.Equal(signed ? PixelType.Float32 : PixelType.Byte8, EchoNode.LastPixels);
        Assert.Equal(signed ? -5 : 9, EchoNode.LastFirstSample);
    }

    [Fact]
    public void A_picture_kept_past_its_run_fails_loudly()
    {
        var (graph, _, _) = Wired(typeof(KeeperNode), () => Gray(4, 4, 1));

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var error = Assert.Throws<ObjectDisposedException>(() => KeeperNode.Kept!.Row<byte>(0).Length);
        Assert.Contains("Copy", error.Message);
    }

    [Fact]
    public void A_wired_setting_the_node_never_resolves_is_a_warning()
    {
        var graph = new VisionGraph();
        var number = new NumberSourceNode(5);
        var node = Describe(typeof(DeafNode)).CreateInstance();

        graph.Add(number);
        graph.Add(node);
        Assert.True(graph.TryConnect(number.Out, node.FindInput("Level")!).Success);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Warning, node.Status);
        Assert.Contains("never read it with Resolve", node.StatusMessage);
    }

    [Fact]
    public void A_node_that_throws_fails_with_its_message_and_nothing_else_breaks()
    {
        var (graph, source, node) = Wired(typeof(GammaNode), () => Gray(4, 4, 1));
        ((GammaNode)((CustomNodeHost)node).Plugin).Gamma = -1;

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Error, node.Status);
        Assert.Contains("above zero", node.StatusMessage);
        Assert.Equal(NodeStatus.Ready, source.Status);
    }

    /// <summary>
    /// The properties panel and undo both work through the parameter's
    /// descriptor, and a custom node's setting is not a property of the
    /// node class - so this is the test that the indirection holds.
    /// </summary>
    [Fact]
    public void Changing_a_setting_invalidates_and_can_be_undone()
    {
        var graph = new VisionGraph();
        var stack = new UndoStack(graph);
        _ = new UndoRecorder(graph, stack);

        var node = Describe(typeof(GammaNode)).CreateInstance();
        graph.Add(node);

        var invalidated = 0;
        node.Invalidated += (_, _) => invalidated++;

        var gamma = node.Descriptor.Parameters.Single(parameter => parameter.Name == "Gamma");
        gamma.SetValue(node, 2.5);

        Assert.Equal(1, invalidated);
        Assert.Equal(2.5, gamma.GetValue(node));

        Assert.True(stack.Undo());
        Assert.Equal(1.0, gamma.GetValue(node));
    }

    /// <summary>The editor hands a slider's value over as a double, whatever the setting is.</summary>
    [Fact]
    public void A_whole_number_setting_takes_a_value_from_a_slider()
    {
        var node = Describe(typeof(RatioNode)).CreateInstance();
        var decimals = node.Descriptor.Parameters.Single(parameter => parameter.Name == "Decimals");

        decimals.SetValue(node, 4.0);

        Assert.Equal(4, ((RatioNode)((CustomNodeHost)node).Plugin).Decimals);
    }

    [Fact]
    public void A_recipe_saves_and_reopens_a_custom_node_with_its_settings()
    {
        RegisteredEcho.Ensure();

        var graph = new VisionGraph();
        var node = NodeCatalog.FindByTypeKey("custom/tests.echo")!.CreateInstance();
        node.Descriptor.Parameters.Single(parameter => parameter.Name == "Amount").SetValue(node, 7.5);
        graph.Add(node);

        var loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));

        Assert.False(loaded.HasWarnings, string.Join(Environment.NewLine, loaded.Warnings));

        var reopened = Assert.IsType<CustomNodeHost>(Assert.Single(loaded.Graph.Nodes));
        Assert.Equal(7.5, ((EchoNode)reopened.Plugin).Amount);
    }

    [Fact]
    public void A_recipe_using_a_missing_custom_node_says_where_to_get_it()
    {
        const string json = """
            {
              "version": 1,
              "nodes": [ { "id": "a", "type": "custom/acme.flatfield", "name": "Flat Field" } ],
              "connections": []
            }
            """;

        var warning = Assert.Single(GraphSerializer.Deserialize(json).Warnings);

        Assert.Contains("custom node 'acme.flatfield'", warning);
        Assert.Contains("Tools > Custom Nodes", warning);
    }

    // ---- loading a library ----------------------------------------------------

    [Fact]
    public void The_sample_library_loads_into_a_context_of_its_own()
    {
        var library = PluginLoader.Load(SampleCopy());

        Assert.Empty(library.Problems);
        Assert.Equal("Sirius.SampleNodes", library.Name);
        Assert.Equal(
            new[] { "custom/samples.brightRegion", "custom/samples.gamma", "custom/samples.ratio" },
            library.Nodes.Select(node => node.TypeKey).Order(StringComparer.Ordinal));

        var context = AssemblyLoadContext.GetLoadContext(library.Nodes[0].NodeType.Assembly);
        Assert.NotSame(AssemblyLoadContext.Default, context);

        // The contract itself is shared, or the host could not recognise
        // the library's nodes as nodes at all.
        Assert.Same(
            AssemblyLoadContext.Default,
            AssemblyLoadContext.GetLoadContext(typeof(ICustomNode).Assembly));
    }

    /// <summary>
    /// Somebody developing a node rebuilds it with the application open. A
    /// loaded DLL that stayed locked would make every rebuild fail.
    /// </summary>
    [Fact]
    public void A_loaded_library_does_not_lock_its_file()
    {
        var path = SampleCopy();
        PluginLoader.Load(path);

        File.Delete(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_library_from_a_loaded_context_runs()
    {
        var library = PluginLoader.Load(SampleCopy());
        var ratio = library.Nodes.Single(node => node.TypeKey == "custom/samples.ratio").CreateInstance();

        var graph = new VisionGraph();
        var a = new NumberSourceNode(6);
        var b = new NumberSourceNode(4);
        graph.Add(a);
        graph.Add(b);
        graph.Add(ratio);
        graph.TryConnect(a.Out, ratio.FindInput("A")!);
        graph.TryConnect(b.Out, ratio.FindInput("B")!);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(1.5, scheduler.GetOutput(ratio.FindOutput("Ratio")!));
    }

    [Fact]
    public void A_dotnet_library_without_the_sdk_is_refused_by_name()
    {
        var library = PluginLoader.Load(CopyOf(typeof(VisionNode).Assembly.Location));

        Assert.Empty(library.Nodes);
        Assert.Contains("does not reference Sirius.Plugins", Assert.Single(library.Problems));
    }

    [Fact]
    public void A_file_that_is_not_dotnet_is_refused()
    {
        var path = Path.Combine(Scratch(), "native.dll");
        File.WriteAllBytes(path, [0x4D, 0x5A, 1, 2, 3, 4, 5, 6]);

        Assert.Contains("not a .NET library", Assert.Single(PluginLoader.Load(path).Problems));
    }

    /// <summary>
    /// A library sits in a folder with whatever it depends on. Only the
    /// library itself is a library.
    /// </summary>
    [Fact]
    public void Finding_libraries_passes_over_their_dependencies()
    {
        var folder = Scratch();
        var sample = SampleCopy(folder);
        CopyOf(typeof(VisionNode).Assembly.Location, folder);
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "not a library");

        Assert.Equal(new[] { sample }, PluginLoader.FindLibraries(folder));
    }

    [Fact]
    public void A_second_copy_of_a_library_adds_nothing_and_says_why()
    {
        // Whichever copy got there first - tests in other classes install
        // copies too, in parallel - the samples' keys are now taken.
        _ = InstalledSample.Library;
        Assert.NotNull(NodeCatalog.FindByTypeKey("custom/samples.gamma"));

        var second = PluginLoader.Install(SampleCopy());

        Assert.Empty(second.Nodes);
        Assert.All(second.Problems, problem => Assert.Contains("already used by", problem));
        Assert.Equal(3, second.Problems.Count);
    }

    [Fact]
    public void Installing_the_same_file_twice_is_harmless() =>
        Assert.Same(InstalledSample.Library, PluginLoader.Install(InstalledSample.Library.Path));

    [Theory]
    [InlineData("1.0.0", "1.0.0", null)]
    [InlineData("1.0.0", "1.2.0", null)]
    [InlineData("2.0.0", "1.0.0", "Rebuild it against Sirius.Plugins 1.x")]
    [InlineData("1.3.0", "1.2.0", "Update the application")]
    public void The_interface_version_is_checked_before_anything_loads(string wanted, string host, string? problem)
    {
        var found = PluginLoader.VersionProblem(Version.Parse(wanted), Version.Parse(host), null, new Version(1, 0, 2));

        if (problem is null)
            Assert.Null(found);
        else
            Assert.Contains(problem, found);
    }

    [Fact]
    public void A_library_built_for_a_newer_application_is_refused() =>
        Assert.Contains(
            "newer Sirius Studio Plus",
            PluginLoader.VersionProblem(new Version(1, 0, 0), new Version(1, 0, 0), new Version(1, 0, 9), new Version(1, 0, 2)));

    // ---- helpers ----------------------------------------------------------------

    private static Picture Filled(int width, int height, byte value)
    {
        var picture = Picture.Allocate(width, height);
        picture.WritableSamples<byte>().Fill(value);
        return picture;
    }

    private static ImageData Gray(int width, int height, byte value)
    {
        var mat = new Mat(height, width, DepthType.Cv8U, 1);
        mat.SetTo(new MCvScalar(value));
        return ImageData.Adopt(mat);
    }

    private static List<string> Problems(Type type)
    {
        var problems = new List<string>();
        Assert.Null(CustomNodeType.TryCreate(type, "test library", problems));
        Assert.NotEmpty(problems);
        return problems;
    }

    private static NodeDescriptor Describe(Type type)
    {
        var problems = new List<string>();
        var described = CustomNodeType.TryCreate(type, "test library", problems);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        return described!.Descriptor;
    }

    private static (VisionGraph Graph, FrameNode Source, VisionNode Node) Wired(Type type, Func<ImageData> frame)
    {
        var graph = new VisionGraph();
        var source = new FrameNode(frame);
        var node = Describe(type).CreateInstance();

        graph.Add(source);
        graph.Add(node);
        Assert.True(graph.TryConnect(source.Out, node.FindInput("In")!).Success);

        return (graph, source, node);
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

    internal static string Scratch()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sirius-custom-node-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>The sample library, copied somewhere of its own as an author would deploy it.</summary>
    internal static string SampleCopy(string? folder = null)
    {
        folder ??= Scratch();
        var source = Path.Combine(AppContext.BaseDirectory, "Sirius.SampleNodes.dll");

        foreach (var extension in new[] { ".dll", ".pdb", ".deps.json" })
        {
            var file = Path.ChangeExtension(source, extension);
            if (File.Exists(file))
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        }

        return Path.Combine(folder, "Sirius.SampleNodes.dll");
    }

    private static string CopyOf(string path, string? folder = null)
    {
        var target = Path.Combine(folder ?? Scratch(), Path.GetFileName(path));
        File.Copy(path, target);
        return target;
    }

    // ---- fixtures ---------------------------------------------------------------

    /// <summary>
    /// The sample library, installed once for the whole run - the catalogue
    /// is process-wide and refuses a key twice, so every test that needs the
    /// samples in it shares this one.
    /// </summary>
    internal static class InstalledSample
    {
        private static readonly Lazy<PluginLibrary> Installed =
            new(() => PluginLoader.Install(SampleCopy()), LazyThreadSafetyMode.ExecutionAndPublication);

        public static PluginLibrary Library => Installed.Value;
    }

    private static class RegisteredEcho
    {
        private static readonly Lazy<bool> Registered = new(() =>
        {
            NodeCatalog.Register(Describe(typeof(EchoNode)));
            return true;
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        public static void Ensure() => _ = Registered.Value;
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

    private sealed class NumberSourceNode : VisionNode
    {
        private readonly double _value;

        public NumberSourceNode(double value)
        {
            _value = value;
            Out = AddOutput<double>("Out");
        }

        public OutputPort Out { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(Out, _value);
    }

    // ---- custom nodes for the tests --------------------------------------------

    /// <summary>The node in docs/custom-nodes.md section 3, verbatim. Keep the two in step.</summary>
    [CustomNode("Lift",
        TypeKey     = "acme.lift",
        Group       = NodeGroup.Basic,
        Icon        = "ph_brightness",
        Description = "Adds a constant to every pixel of an 8-bit image.")]
    public sealed class LiftNode : ICustomNode
    {
        private InPort<Picture>  _in    = null!;
        private OutPort<Picture> _out   = null!;
        private OutPort<double>  _mean  = null!;

        [Setting(Min = -255, Max = 255, Step = 1, Connectable = true,
                 Description = "How much to add. Results stop at 0 and 255.")]
        public int Amount { get; set; } = 20;

        public void Declare(INodeShape shape)
        {
            _in   = shape.Input<Picture>("In");
            _out  = shape.Output<Picture>("Out");
            _mean = shape.Output<double>("Mean");
        }

        public void Run(IRunContext context)
        {
            var source = context.Read(_in);
            if (source is null)
                return;

            if (source.Pixels != PixelType.Byte8)
            {
                context.Warn("Lift handles 8-bit images; this one passed through unchanged.");
                context.Publish(_out, source);
                return;
            }

            // A connectable setting is read through Resolve, so a wire wins.
            var amount = context.Resolve(nameof(Amount), Amount);

            var result = Picture.Allocate(source);
            long total = 0;

            for (var y = 0; y < source.Height; y++)
            {
                context.ThrowIfCancelled();

                var from = source.Row<byte>(y);
                var to   = result.WritableRow<byte>(y);

                for (var x = 0; x < from.Length; x++)
                {
                    to[x] = (byte)Math.Clamp(from[x] + amount, 0, 255);
                    total += to[x];
                }
            }

            context.Publish(_out, result);
            context.Publish(_mean, (double)total / (source.Width * source.Height * source.Channels));
        }
    }

    public enum EchoMode
    {
        Plain,
        Loud,
    }

    [CustomNode("Echo", TypeKey = "tests.echo")]
    public sealed class EchoNode : ICustomNode
    {
        private InPort<Picture> _in = null!;
        private OutPort<double> _out = null!;

        public static PixelType? LastPixels;
        public static double LastFirstSample;

        [Setting(Min = 0, Max = 10, Connectable = true)]
        public double Amount { get; set; } = 1;

        [Setting]
        public EchoMode Mode { get; set; }

        public void Declare(INodeShape shape)
        {
            _in = shape.Input<Picture>("In", required: false);
            _out = shape.Output<double>("Amount");
        }

        public void Run(IRunContext context)
        {
            if (context.Read(_in) is { } picture)
            {
                LastPixels = picture.Pixels;
                LastFirstSample = picture.Pixels == PixelType.Float32
                    ? picture.Row<float>(0)[0]
                    : picture.Row<byte>(0)[0];
            }

            context.Publish(_out, context.Resolve(nameof(Amount), Amount));
        }
    }

    [CustomNode("Deaf", TypeKey = "tests.deaf")]
    public sealed class DeafNode : ICustomNode
    {
        private OutPort<double> _out = null!;

        [Setting(Connectable = true)]
        public double Level { get; set; }

        public void Declare(INodeShape shape) => _out = shape.Output<double>("Out");

        // Reads the property, never Resolve - the mistake the warning is for.
        public void Run(IRunContext context) => context.Publish(_out, Level);
    }

    [CustomNode("Keeper", TypeKey = "tests.keeper")]
    public sealed class KeeperNode : ICustomNode
    {
        private InPort<Picture> _in = null!;

        public static Picture? Kept;

        public void Declare(INodeShape shape) => _in = shape.Input<Picture>("In");

        public void Run(IRunContext context) => Kept = context.Read(_in);
    }

    public sealed class NoAttributeNode : ICustomNode
    {
        public void Declare(INodeShape shape)
        {
        }

        public void Run(IRunContext context)
        {
        }
    }

    [CustomNode("No Key")]
    public sealed class NoKeyNode : ICustomNode
    {
        public void Declare(INodeShape shape)
        {
        }

        public void Run(IRunContext context)
        {
        }
    }

    [CustomNode("Bad Port", TypeKey = "tests.badPort")]
    public sealed class BadPortNode : ICustomNode
    {
        public void Declare(INodeShape shape) => shape.Input<DateTime>("When");

        public void Run(IRunContext context)
        {
        }
    }

    [CustomNode("Bad Setting", TypeKey = "tests.badSetting")]
    public sealed class BadSettingNode : ICustomNode
    {
        [Setting]
        public DateTime When { get; set; }

        [Setting]
        public int? Maybe { get; set; }

        [Setting]
        public double Fixed => 1;

        public void Declare(INodeShape shape)
        {
        }

        public void Run(IRunContext context)
        {
        }
    }

    [CustomNode("Throws", TypeKey = "tests.throws")]
    public sealed class ThrowingNode : ICustomNode
    {
        public ThrowingNode() => throw new InvalidOperationException("no camera attached");

        public void Declare(INodeShape shape)
        {
        }

        public void Run(IRunContext context)
        {
        }
    }

    [CustomNode("Enum Wire", TypeKey = "tests.enumWire")]
    public sealed class ConnectableEnumNode : ICustomNode
    {
        [Setting(Connectable = true)]
        public EchoMode Mode { get; set; }

        public void Declare(INodeShape shape)
        {
        }

        public void Run(IRunContext context)
        {
        }
    }

    [CustomNode("Clash", TypeKey = "tests.clash")]
    public sealed class ClashingNode : ICustomNode
    {
        [Setting(Connectable = true)]
        public double Level { get; set; }

        public void Declare(INodeShape shape) => shape.Input<double>("Level");

        public void Run(IRunContext context)
        {
        }
    }
}
