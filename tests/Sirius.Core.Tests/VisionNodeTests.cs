using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// End-to-end tests over the real OpenCV-backed nodes, driven through the
/// scheduler exactly as the application drives them.
/// </summary>
public class VisionNodeTests
{
    public VisionNodeTests() => TestCatalog.Ensure();

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

    /// <summary>A left-to-right 0..255 ramp, so sampling results are predictable.</summary>
    private sealed class RampSourceNode : VisionNode
    {
        public RampSourceNode()
        {
            ImageOut = AddOutput<ImageData>("Image");
        }

        public OutputPort ImageOut { get; }

        public int Width { get; init; } = 256;

        public int Height { get; init; } = 16;

        protected override void OnEvaluate(EvalContext context)
        {
            var mat = new Mat(Height, Width, DepthType.Cv8U, 1);
            var data = new byte[Height, Width];

            for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                data[y, x] = (byte)(x * 255 / Math.Max(1, Width - 1));

            using var managed = new Matrix<byte>(data);
            managed.Mat.CopyTo(mat);

            context.Set(ImageOut, ImageData.Adopt(mat));
        }
    }

    /// <summary>
    /// A hard vertical edge: 0 to the left of <see cref="EdgeAt"/>, 200 from it
    /// on. A ramp cannot tell interpolators apart, because every scheme
    /// reproduces a straight line exactly.
    /// </summary>
    private sealed class StepSourceNode : VisionNode
    {
        public StepSourceNode()
        {
            ImageOut = AddOutput<ImageData>("Image");
        }

        public OutputPort ImageOut { get; }

        public int Width { get; init; } = 64;

        public int Height { get; init; } = 16;

        public int EdgeAt { get; init; } = 32;

        protected override void OnEvaluate(EvalContext context)
        {
            var mat = new Mat(Height, Width, DepthType.Cv8U, 1);
            var data = new byte[Height, Width];

            for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                data[y, x] = x < EdgeAt ? (byte)0 : (byte)200;

            using var managed = new Matrix<byte>(data);
            managed.Mat.CopyTo(mat);

            context.Set(ImageOut, ImageData.Adopt(mat));
        }
    }

    private static async Task<ProfileData> RunProfileAsync(
        VisionNode source, OutputPort imageOut, LineProfileNode profile)
    {
        var graph = new VisionGraph();
        graph.Add(source);
        graph.Add(profile);
        graph.TryConnect(imageOut, profile.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        return scheduler.GetOutput<ProfileData>(profile.ProfileOut)!;
    }

    [Fact]
    public async Task Constant_image_source_produces_the_requested_size()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 320, Height = 240, Value = 77 };
        graph.Add(source);

        using var scheduler = CreateScheduler(graph);
        var result = await scheduler.RunAsync();

        Assert.True(result.Success);
        var image = scheduler.GetOutput<ImageData>(source.ImageOut)!;
        Assert.Equal(320, image.Width);
        Assert.Equal(240, image.Height);
        Assert.True(image.IsGrayscale);
    }

    [Fact]
    public async Task Threshold_splits_a_ramp_at_the_configured_level()
    {
        var graph = new VisionGraph();
        var source = new RampSourceNode();
        var threshold = new ThresholdNode { Threshold = 127, MaxValue = 255 };
        graph.Add(source);
        graph.Add(threshold);
        graph.TryConnect(source.ImageOut, threshold.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        var image = scheduler.GetOutput<ImageData>(threshold.ImageOut)!;
        var mean = CvInvoke.Mean(image.Mat).V0;

        // Half the ramp sits above the level, so the mean lands near 255/2.
        Assert.InRange(mean, 110, 145);
    }

    [Fact]
    public async Task Crop_produces_an_image_of_the_region_size()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 200, Height = 100 };
        var crop = new CropNode { Roi = new Rectangle(10, 20, 50, 30) };
        graph.Add(source);
        graph.Add(crop);
        graph.TryConnect(source.ImageOut, crop.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        var image = scheduler.GetOutput<ImageData>(crop.ImageOut)!;
        Assert.Equal(50, image.Width);
        Assert.Equal(30, image.Height);
        Assert.Equal(NodeStatus.Ready, crop.Status);
    }

    /// <summary>
    /// The old CropTool only checked for a zero-area ROI; a region partly past
    /// the edge reached OpenCV and threw.
    /// </summary>
    [Fact]
    public async Task Crop_clips_a_region_that_overhangs_the_image()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 100, Height = 100 };
        var crop = new CropNode { Roi = new Rectangle(80, 80, 50, 50) };
        graph.Add(source);
        graph.Add(crop);
        graph.TryConnect(source.ImageOut, crop.ImageIn);

        using var scheduler = CreateScheduler(graph);
        var result = await scheduler.RunAsync();

        Assert.Equal(0, result.Failed);
        Assert.Equal(NodeStatus.Warning, crop.Status);

        var image = scheduler.GetOutput<ImageData>(crop.ImageOut)!;
        Assert.Equal(20, image.Width);
        Assert.Equal(20, image.Height);
    }

    [Fact]
    public async Task Crop_with_an_empty_region_warns_and_passes_through()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 64, Height = 64 };
        var crop = new CropNode { Roi = Rectangle.Empty };
        graph.Add(source);
        graph.Add(crop);
        graph.TryConnect(source.ImageOut, crop.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Equal(NodeStatus.Warning, crop.Status);
        var image = scheduler.GetOutput<ImageData>(crop.ImageOut)!;
        Assert.Equal(64, image.Width);
    }

    [Fact]
    public async Task Colour_convert_reduces_a_colour_image_to_one_channel()
    {
        var graph = new VisionGraph();
        var source = new ColourSourceNode();
        var convert = new ColourConvertNode { Conversion = ColorConversion.Bgr2Gray };
        graph.Add(source);
        graph.Add(convert);
        graph.TryConnect(source.ImageOut, convert.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        var image = scheduler.GetOutput<ImageData>(convert.ImageOut)!;
        Assert.Equal(1, image.Channels);
    }

    /// <summary>
    /// OpenCV's own error for this is opaque; the node checks up front and
    /// names the fix.
    /// </summary>
    [Fact]
    public async Task Adaptive_threshold_explains_what_to_do_with_a_colour_input()
    {
        var graph = new VisionGraph();
        var source = new ColourSourceNode();
        var adaptive = new AdaptiveThresholdNode();
        graph.Add(source);
        graph.Add(adaptive);
        graph.TryConnect(source.ImageOut, adaptive.ImageIn);

        using var scheduler = CreateScheduler(graph);
        var result = await scheduler.RunAsync();

        Assert.Equal(1, result.Failed);
        Assert.Equal(NodeStatus.Error, adaptive.Status);
        Assert.Contains("Colour Convert", adaptive.StatusMessage!);
    }

    [Fact]
    public async Task Block_size_snaps_to_an_odd_value()
    {
        var node = new AdaptiveThresholdNode { BlockSize = 19 };

        node.BlockSize = 20;

        Assert.Equal(21, node.BlockSize);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Line_profile_follows_a_horizontal_ramp()
    {
        var graph = new VisionGraph();
        var source = new RampSourceNode { Width = 256, Height = 8 };
        var profile = new LineProfileNode { StartX = 0, StartY = 4, EndX = 255, EndY = 4 };
        graph.Add(source);
        graph.Add(profile);
        graph.TryConnect(source.ImageOut, profile.ImageIn);

        using var scheduler = CreateScheduler(graph);
        var result = await scheduler.RunAsync();

        Assert.True(result.Success);
        var data = scheduler.GetOutput<ProfileData>(profile.ProfileOut)!;

        Assert.Equal(256, data.Length);
        Assert.InRange(data.Min, 0, 2);
        Assert.InRange(data.Max, 253, 255);

        // Monotonic, because the source is a strictly increasing ramp.
        for (var i = 1; i < data.Length; i++)
            Assert.True(data.Gray().Samples[i] >= data.Gray().Samples[i - 1], $"sample {i} decreased");
    }

    [Fact]
    public async Task Line_profile_warns_on_a_zero_length_line()
    {
        var graph = new VisionGraph();
        var source = new RampSourceNode();
        var profile = new LineProfileNode { StartX = 10, StartY = 10, EndX = 10, EndY = 10 };
        graph.Add(source);
        graph.Add(profile);
        graph.TryConnect(source.ImageOut, profile.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Equal(NodeStatus.Warning, profile.Status);
        Assert.Empty(scheduler.GetOutput<ProfileData>(profile.ProfileOut)!.Gray().Samples);
    }

    /// <summary>
    /// Spacing sets the step along the line, so halving it roughly doubles the
    /// sample count. The old Bresenham walk had no equivalent - the count fell
    /// out of the line's angle.
    /// </summary>
    [Fact]
    public async Task Sample_spacing_sets_how_many_samples_are_taken()
    {
        var source = new RampSourceNode { Width = 256, Height = 8 };
        var profile = new LineProfileNode
        {
            StartX = 0, StartY = 4, EndX = 255, EndY = 4, Spacing = 0.5,
        };

        var data = await RunProfileAsync(source, source.ImageOut, profile);

        // 255 px of travel at half-pixel steps, plus the sample at zero.
        Assert.Equal(511, data.Length);
        Assert.Equal(0.5, data.Spacing);
        Assert.Equal(255, data.SampledLength);
    }

    /// <summary>
    /// The point of sub-pixel sampling: between two pixels either side of an
    /// edge, a linear reconstruction reads the value in between. Nearest
    /// cannot - it can only ever return one of the two.
    /// </summary>
    [Fact]
    public async Task Linear_interpolation_reads_between_pixels_at_an_edge()
    {
        var source = new StepSourceNode { Width = 64, Height = 16, EdgeAt = 32 };
        var profile = new LineProfileNode
        {
            StartX = 0,
            StartY = 8,
            EndX = 63,
            EndY = 8,
            Spacing = 0.5,
            Interpolation = ProfileInterpolation.Linear,
        };

        var data = await RunProfileAsync(source, source.ImageOut, profile);

        // Index 63 is 31.5 px along: midway between pixel 31 (0) and 32 (200).
        Assert.InRange(data.Gray().Samples[63], 80, 120);
    }

    [Fact]
    public async Task Nearest_interpolation_only_ever_returns_a_real_pixel()
    {
        var source = new StepSourceNode { Width = 64, Height = 16, EdgeAt = 32 };
        var profile = new LineProfileNode
        {
            StartX = 0,
            StartY = 8,
            EndX = 63,
            EndY = 8,
            Spacing = 0.5,
            Interpolation = ProfileInterpolation.Nearest,
        };

        var data = await RunProfileAsync(source, source.ImageOut, profile);

        foreach (var sample in data.Gray().Samples)
            Assert.True(sample < 1 || sample > 199, $"{sample} is not a pixel value");
    }

    /// <summary>
    /// Averaging across the line must not shift what it reads. The ramp is
    /// constant down each column, so a wide band has to agree exactly with a
    /// single-pixel one.
    /// </summary>
    [Fact]
    public async Task Thickness_averages_across_the_line_without_moving_it()
    {
        var thinSource = new RampSourceNode { Width = 256, Height = 32 };
        var thin = await RunProfileAsync(
            thinSource,
            thinSource.ImageOut,
            new LineProfileNode { StartX = 0, StartY = 16, EndX = 255, EndY = 16 });

        var wideSource = new RampSourceNode { Width = 256, Height = 32 };
        var wide = await RunProfileAsync(
            wideSource,
            wideSource.ImageOut,
            new LineProfileNode
            {
                StartX = 0, StartY = 16, EndX = 255, EndY = 16, Thickness = 9,
            });

        Assert.Equal(9, wide.Thickness);
        Assert.Equal(thin.Length, wide.Length);

        for (var i = 0; i < thin.Length; i++)
            Assert.Equal(thin.Gray().Samples[i], wide.Gray().Samples[i], 3);
    }

    /// <summary>
    /// A line running off the image reports gaps. The old code skipped those
    /// points, silently shortening the profile so the chart's x axis no longer
    /// matched the line that was drawn.
    /// </summary>
    [Fact]
    public async Task Samples_off_the_image_become_gaps_rather_than_border_values()
    {
        var source = new RampSourceNode { Width = 256, Height = 8 };
        var profile = new LineProfileNode { StartX = 200, StartY = 4, EndX = 400, EndY = 4 };

        var data = await RunProfileAsync(source, source.ImageOut, profile);

        Assert.Equal(201, data.Length);
        Assert.True(data.HasGaps);

        // x runs 200..400; only through 255 is on the image.
        Assert.Equal(56, data.ValidCount);
        Assert.True(float.IsNaN(data.Gray().Samples[^1]));

        // The statistics still come from the samples that were measured.
        Assert.True(float.IsFinite(data.Min));
        Assert.True(float.IsFinite(data.Max));
    }

    [Fact]
    public async Task A_line_that_leaves_the_image_warns()
    {
        var graph = new VisionGraph();
        var source = new RampSourceNode { Width = 256, Height = 8 };
        var profile = new LineProfileNode { StartX = 200, StartY = 4, EndX = 400, EndY = 4 };
        graph.Add(source);
        graph.Add(profile);
        graph.TryConnect(source.ImageOut, profile.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Equal(NodeStatus.Warning, profile.Status);
        Assert.Contains("outside the image", profile.StatusMessage!);
    }

    /// <summary>
    /// A spacing of zero would ask for infinitely many samples, and a saved
    /// graph can carry any number at all, so the clamp lives on the property.
    /// </summary>
    [Theory]
    [InlineData(0, 0.05)]
    [InlineData(-5, 0.05)]
    [InlineData(1000, 50)]
    [InlineData(0.25, 0.25)]
    public void Sample_spacing_is_clamped_to_a_usable_range(double set, double expected)
    {
        Assert.Equal(expected, new LineProfileNode { Spacing = set }.Spacing);
    }

    /// <summary>A ramp as a bare image, without a graph around it.</summary>
    private static ImageData RampImage(int width, int height)
    {
        var mat = new Mat(height, width, DepthType.Cv8U, 1);
        var data = new byte[height, width];

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            data[y, x] = (byte)(x * 255 / Math.Max(1, width - 1));

        using var managed = new Matrix<byte>(data);
        managed.Mat.CopyTo(mat);

        return ImageData.Adopt(mat);
    }

    /// <summary>
    /// The viewer holds one of these across evaluations, while the graph
    /// reclaims the buffer it was built from at the end of every pass. If the
    /// preparation did not copy, that would be a use-after-free into native
    /// memory - uncatchable, and exactly the fault this engine was built to
    /// avoid.
    /// </summary>
    [Fact]
    public void A_prepared_source_outlives_the_image_it_came_from()
    {
        var image = RampImage(64, 16);
        var prepared = LineProfileSource.From(image);

        image.Dispose();

        using (prepared)
        {
            var data = prepared.Sample(new PointF(0, 8), new PointF(63, 8));

            Assert.Equal(64, data.Length);
            Assert.InRange(data.Max, 250, 255);
        }
    }

    /// <summary>
    /// The viewer's profile tool and the node share one sampler precisely so
    /// that a line drawn on the image and the same line typed into the node
    /// cannot disagree. Two implementations would drift, and the drift would
    /// be invisible.
    /// </summary>
    [Fact]
    public async Task The_node_and_a_direct_sample_agree()
    {
        var source = new RampSourceNode { Width = 256, Height = 16 };
        var profile = new LineProfileNode
        {
            StartX = 0,
            StartY = 8,
            EndX = 255,
            EndY = 8,
            Spacing = 0.5,
            Thickness = 3,
            Interpolation = ProfileInterpolation.Cubic,
        };

        var viaNode = await RunProfileAsync(source, source.ImageOut, profile);

        using var image = RampImage(256, 16);
        using var prepared = LineProfileSource.From(image);

        var direct = prepared.Sample(
            new PointF(0, 8),
            new PointF(255, 8),
            spacing: 0.5,
            thickness: 3,
            interpolation: ProfileInterpolation.Cubic);

        Assert.Equal(viaNode.Length, direct.Length);

        for (var i = 0; i < direct.Length; i++)
            Assert.Equal(viaNode.Gray().Samples[i], direct.Gray().Samples[i], 4);
    }

    [Fact]
    public void A_disposed_source_refuses_to_sample()
    {
        using var image = RampImage(32, 8);
        var prepared = LineProfileSource.From(image);
        prepared.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => prepared.Sample(new PointF(0, 4), new PointF(31, 4)));
    }

    [Theory]
    [InlineData(4, 5)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(7, 7)]
    public void Thickness_snaps_to_an_odd_value(int set, int expected)
    {
        // An even band has no centre pixel, so it would sit half a pixel off
        // the line it is meant to be measuring.
        Assert.Equal(expected, new LineProfileNode { Thickness = set }.Thickness);
    }

    [Fact]
    public async Task Histogram_reports_one_array_per_channel()
    {
        var graph = new VisionGraph();
        var source = new RampSourceNode();
        var histogram = new HistogramNode();
        graph.Add(source);
        graph.Add(histogram);
        graph.TryConnect(source.ImageOut, histogram.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        var data = scheduler.GetOutput<HistogramData>(histogram.HistogramOut)!;
        Assert.Equal(1, data.ChannelCount);
        Assert.Equal(256, data.BinCount);
        Assert.True(data.Max > 0);
    }

    /// <summary>
    /// The leak the old tools had: every re-run allocated a Mat and dropped it.
    /// </summary>
    [Fact]
    public async Task Re_running_a_filter_disposes_the_previous_image()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode();
        var blur = new GaussianBlurNode { KernelSize = 3 };
        graph.Add(source);
        graph.Add(blur);
        graph.TryConnect(source.ImageOut, blur.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        var first = scheduler.GetOutput<ImageData>(blur.ImageOut)!;
        Assert.False(first.IsDisposed);

        blur.KernelSize = 7;
        await scheduler.RunAsync();

        Assert.True(first.IsDisposed);
        Assert.False(scheduler.GetOutput<ImageData>(blur.ImageOut)!.IsDisposed);
    }

    [Fact]
    public async Task A_bypassed_filter_forwards_the_upstream_image_untouched()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Value = 200 };
        var blur = new GaussianBlurNode { Enabled = false };
        graph.Add(source);
        graph.Add(blur);
        graph.TryConnect(source.ImageOut, blur.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Same(
            scheduler.GetOutput<ImageData>(source.ImageOut),
            scheduler.GetOutput<ImageData>(blur.ImageOut));
    }

    /// <summary>A three-channel source, for colour-specific paths.</summary>
    private sealed class ColourSourceNode : VisionNode
    {
        public ColourSourceNode()
        {
            ImageOut = AddOutput<ImageData>("Image");
        }

        public OutputPort ImageOut { get; }

        protected override void OnEvaluate(EvalContext context)
        {
            var mat = new Mat(32, 32, DepthType.Cv8U, 3);
            mat.SetTo(new MCvScalar(30, 120, 210));
            context.Set(ImageOut, ImageData.Adopt(mat));
        }
    }
}
