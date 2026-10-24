using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Walking a folder of images.
/// </summary>
/// <remarks>
/// Driven against real files, because the behaviour worth testing is the
/// listing: which files are picked up, and in what order.
/// </remarks>
public class ImageSequenceTests : IDisposable
{
    private readonly string _folder;

    public ImageSequenceTests()
    {
        TestCatalog.Ensure();

        _folder = Path.Combine(Path.GetTempPath(), "sirius-seq-" + Guid.NewGuid().ToString("N"));
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
            // A leftover temp folder is not worth failing a test run over.
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>Writes a small real image, so Imread has something to decode.</summary>
    private string WriteImage(string name, byte value = 128)
    {
        var path = Path.Combine(_folder, name);

        using var mat = new Mat(4, 4, DepthType.Cv8U, 1);
        mat.SetTo(new Emgu.CV.Structure.MCvScalar(value));
        CvInvoke.Imwrite(path, mat);

        return path;
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

    /// <summary>
    /// The reason a natural order is needed at all: ordinal sorting puts
    /// "frame10" between "frame1" and "frame2", which scrambles exactly the
    /// numbered sequences this node exists to walk.
    /// </summary>
    [Fact]
    public void Frames_are_ordered_the_way_they_are_numbered()
    {
        WriteImage("frame1.png");
        WriteImage("frame2.png");
        WriteImage("frame10.png");

        var node = new ImageSequenceNode { SourcePath = _folder };

        Assert.Equal(3, node.Count);

        Assert.Equal("frame1.png", NameAt(node, 1));
        Assert.Equal("frame2.png", NameAt(node, 2));
        Assert.Equal("frame10.png", NameAt(node, 3));
    }

    [Fact]
    public void Zero_padding_does_not_change_the_order()
    {
        WriteImage("shot-007.png");
        WriteImage("shot-8.png");
        WriteImage("shot-0010.png");

        var node = new ImageSequenceNode { SourcePath = _folder };

        Assert.Equal("shot-007.png", NameAt(node, 1));
        Assert.Equal("shot-8.png", NameAt(node, 2));
        Assert.Equal("shot-0010.png", NameAt(node, 3));
    }

    [Fact]
    public void Only_images_are_listed()
    {
        WriteImage("a.png");
        WriteImage("b.bmp");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not an image");
        File.WriteAllText(Path.Combine(_folder, "recipe.sirius"), "{}");

        Assert.Equal(2, new ImageSequenceNode { SourcePath = _folder }.Count);
    }

    /// <summary>
    /// Choosing a file rather than a folder is how an operator picks "the first
    /// image", so the sequence has to start where they pointed.
    /// </summary>
    [Fact]
    public void Choosing_a_file_starts_the_sequence_on_it()
    {
        WriteImage("a.png");
        var second = WriteImage("b.png");
        WriteImage("c.png");

        var node = new ImageSequenceNode { SourcePath = second };

        Assert.Equal(3, node.Count);
        Assert.Equal(2, node.Index);
        Assert.Equal("b.png", node.CurrentName);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(99, 3)]
    public void The_frame_number_stays_inside_the_sequence(int set, int expected)
    {
        WriteImage("a.png");
        WriteImage("b.png");
        WriteImage("c.png");

        var node = new ImageSequenceNode { SourcePath = _folder, Index = set };

        Assert.Equal(expected, node.Index);
    }

    [Fact]
    public void An_empty_folder_has_nothing_to_show()
    {
        var node = new ImageSequenceNode { SourcePath = _folder };

        Assert.Equal(0, node.Count);
        Assert.Null(node.CurrentName);
    }

    /// <summary>
    /// A path that cannot be listed must not throw while it is being typed -
    /// the inspector reads Count on every keystroke.
    /// </summary>
    [Fact]
    public void A_path_that_does_not_exist_is_an_empty_sequence()
    {
        var node = new ImageSequenceNode
        {
            SourcePath = Path.Combine(_folder, "no-such-folder", "no-such-file.png"),
        };

        Assert.Equal(0, node.Count);
    }

    [Fact]
    public async Task Evaluating_reads_the_frame_the_index_points_at()
    {
        WriteImage("a.png", 40);
        WriteImage("b.png", 200);

        var graph = new VisionGraph();
        var node = new ImageSequenceNode { SourcePath = _folder, Index = 2 };
        graph.Add(node);

        using var scheduler = CreateScheduler(graph);
        var result = await scheduler.RunAsync();

        Assert.True(result.Success);

        var image = scheduler.GetOutput<ImageData>(node.ImageOut)!;
        Assert.Equal(4, image.Width);

        // The second frame, not the first.
        using var sample = new Mat();
        image.Mat.ConvertTo(sample, DepthType.Cv8U);
        Assert.Equal(200, sample.GetRawData()[0]);
    }

    [Fact]
    public async Task An_empty_folder_fails_the_node_rather_than_the_run()
    {
        var graph = new VisionGraph();
        var node = new ImageSequenceNode { SourcePath = _folder };
        graph.Add(node);

        using var scheduler = CreateScheduler(graph);
        var result = await scheduler.RunAsync();

        Assert.Equal(1, result.Failed);
        Assert.Equal(NodeStatus.Error, node.Status);
        Assert.Contains("No images found", node.StatusMessage!);
    }

    private static string? NameAt(ImageSequenceNode node, int index)
    {
        node.Index = index;
        return node.CurrentName;
    }
}
