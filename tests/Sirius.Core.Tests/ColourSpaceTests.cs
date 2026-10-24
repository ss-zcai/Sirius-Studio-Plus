using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// What an image's channels mean, and who gets told.
/// </summary>
/// <remarks>
/// An image recorded only how many channels it had, so everything
/// downstream guessed: three meant blue, green, red. After a Colour Convert
/// that guess is wrong, and wrong in a way that looks like a tuning problem
/// rather than a bug - a hue band offering 0-255 for a channel that stops
/// at 179.
/// </remarks>
public class ColourSpaceTests
{
    public ColourSpaceTests() => TestCatalog.Ensure();

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

    private sealed class ColourSourceNode : VisionNode
    {
        public ColourSourceNode() => ImageOut = AddOutput<ImageData>("Out");

        public OutputPort ImageOut { get; }

        protected override void OnEvaluate(EvalContext context)
        {
            var mat = new Mat(16, 16, DepthType.Cv8U, 3);
            mat.SetTo(new MCvScalar(40, 90, 200));
            context.Set(ImageOut, ImageData.Adopt(mat));
        }
    }

    // ---- the tag ------------------------------------------------------------

    [Theory]
    [InlineData(1, ColourSpace.Gray)]
    [InlineData(3, ColourSpace.Bgr)]
    [InlineData(4, ColourSpace.Bgra)]
    public void An_untagged_frame_reads_as_its_channel_count_always_did(
        int channels, ColourSpace expected)
    {
        using var mat = new Mat(4, 4, DepthType.Cv8U, channels);
        using var image = ImageData.Adopt(mat.Clone());

        Assert.Equal(expected, image.Space);
    }

    [Fact]
    public async Task Colour_convert_says_what_it_produced()
    {
        var graph = new VisionGraph();
        var source = new ColourSourceNode();
        var convert = new ColourConvertNode { Conversion = ColorConversion.Bgr2Hsv };
        graph.Add(source);
        graph.Add(convert);
        graph.TryConnect(source.ImageOut, convert.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Equal(ColourSpace.Hsv, scheduler.GetOutput<ImageData>(convert.ImageOut)!.Space);
    }

    /// <summary>
    /// A blur does not reinterpret the channels, so the tag has to survive
    /// everything between the conversion and whoever reads it.
    /// </summary>
    [Fact]
    public async Task A_filter_passes_the_space_along()
    {
        var graph = new VisionGraph();
        var source = new ColourSourceNode();
        var convert = new ColourConvertNode { Conversion = ColorConversion.Bgr2Hsv };
        var blur = new GaussianBlurNode();
        graph.Add(source);
        graph.Add(convert);
        graph.Add(blur);
        graph.TryConnect(source.ImageOut, convert.ImageIn);
        graph.TryConnect(convert.ImageOut, blur.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Equal(ColourSpace.Hsv, scheduler.GetOutput<ImageData>(blur.ImageOut)!.Space);
    }

    [Fact]
    public async Task Converting_back_to_grey_says_so()
    {
        var graph = new VisionGraph();
        var source = new ColourSourceNode();
        var convert = new ColourConvertNode { Conversion = ColorConversion.Bgr2Gray };
        graph.Add(source);
        graph.Add(convert);
        graph.TryConnect(source.ImageOut, convert.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Equal(ColourSpace.Gray, scheduler.GetOutput<ImageData>(convert.ImageOut)!.Space);
    }

    // ---- what it is used for ------------------------------------------------

    [Theory]
    [InlineData(ColourSpace.Bgr, new[] { "B", "G", "R" })]
    [InlineData(ColourSpace.Hsv, new[] { "H", "S", "V" })]
    [InlineData(ColourSpace.Lab, new[] { "L", "a", "b" })]
    public void Channels_are_named_for_the_space(ColourSpace space, string[] expected) =>
        Assert.Equal(expected, ColourSpaces.NamesOf(space, 3));

    /// <summary>
    /// The one that matters. OpenCV packs 0-360 degrees of hue into 0-179 so
    /// it fits in a byte; a band offering 0-255 wastes a third of its travel
    /// on values that cannot occur.
    /// </summary>
    [Fact]
    public void Hue_stops_at_179_in_eight_bits()
    {
        Assert.Equal(179, ColourSpaces.MaximumOf(ColourSpace.Hsv, 0, DepthType.Cv8U));
        Assert.Equal(255, ColourSpaces.MaximumOf(ColourSpace.Hsv, 1, DepthType.Cv8U));
        Assert.Equal(255, ColourSpaces.MaximumOf(ColourSpace.Bgr, 0, DepthType.Cv8U));
    }

    [Fact]
    public async Task A_colour_range_relabels_itself_from_the_frame_it_is_given()
    {
        var graph = new VisionGraph();
        var source = new ColourSourceNode();
        var convert = new ColourConvertNode { Conversion = ColorConversion.Bgr2Hsv };
        var range = new ColourRangeNode();
        graph.Add(source);
        graph.Add(convert);
        graph.Add(range);
        graph.TryConnect(source.ImageOut, convert.ImageIn);
        graph.TryConnect(convert.ImageOut, range.ImageIn);

        var first = new ParameterViewModel(
            range, range.Descriptor.Parameters.Single(p => p.Name == "Blue"));

        Assert.Equal("B", first.DisplayName);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Equal("H", first.DisplayName);
        Assert.Equal(179, first.Maximum);
    }

    /// <summary>
    /// And an ordinary camera frame is unchanged, which is most recipes.
    /// </summary>
    [Fact]
    public async Task A_colour_range_on_an_unconverted_frame_still_says_blue()
    {
        var graph = new VisionGraph();
        var source = new ColourSourceNode();
        var range = new ColourRangeNode();
        graph.Add(source);
        graph.Add(range);
        graph.TryConnect(source.ImageOut, range.ImageIn);

        var first = new ParameterViewModel(
            range, range.Descriptor.Parameters.Single(p => p.Name == "Blue"));

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Equal("B", first.DisplayName);
        Assert.Equal(255, first.Maximum);
    }

    [Fact]
    public async Task A_profile_names_its_channels_from_the_space()
    {
        var graph = new VisionGraph();
        var source = new ColourSourceNode();
        var convert = new ColourConvertNode { Conversion = ColorConversion.Bgr2Hsv };
        graph.Add(source);
        graph.Add(convert);
        graph.TryConnect(source.ImageOut, convert.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        using var prepared = LineProfileSource.From(
            scheduler.GetOutput<ImageData>(convert.ImageOut)!);

        Assert.Equal(new[] { "H", "S", "V" }, prepared.ChannelNames);
    }
}
