using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Vision;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Profiling a colour image.
/// </summary>
/// <remarks>
/// A colour profile is one trace per channel rather than one trace of
/// luminance. Reducing to grey first loses exactly what a colour inspection
/// is looking for: a red fiducial on green solder resist is a strong signal
/// in two channels and nearly nothing in luminance.
/// </remarks>
public class ProfileChannelTests
{
    /// <summary>A flat frame in OpenCV's channel order: blue, green, red.</summary>
    private static ImageData Colour(byte blue, byte green, byte red, int channels = 3)
    {
        var mat = new Mat(32, 64, DepthType.Cv8U, channels);
        mat.SetTo(new MCvScalar(blue, green, red, 255));
        return ImageData.Adopt(mat);
    }

    private static ImageData Grey(byte value)
    {
        var mat = new Mat(32, 64, DepthType.Cv8U, 1);
        mat.SetTo(new MCvScalar(value));
        return ImageData.Adopt(mat);
    }

    private static ProfileData Across(ImageData image)
    {
        using var source = LineProfileSource.From(image);
        return source.Sample(new PointF(2, 16), new PointF(60, 16));
    }

    [Fact]
    public void A_colour_image_profiles_every_channel()
    {
        using var image = Colour(blue: 20, green: 90, red: 200);

        var profile = Across(image);

        Assert.Equal(3, profile.Channels.Count);
        Assert.Equal(new[] { "B", "G", "R" }, profile.Channels.Select(c => c.Name));

        // Each trace reads its own channel, not a shared luminance.
        Assert.Equal(20, profile.Channels[0].Mean, 1);
        Assert.Equal(90, profile.Channels[1].Mean, 1);
        Assert.Equal(200, profile.Channels[2].Mean, 1);
    }

    /// <summary>
    /// The regression this replaces: a colour frame was reduced with
    /// Bgr2Gray, so three very different channels arrived as one middling
    /// trace and nothing could be told apart.
    /// </summary>
    [Fact]
    public void A_colour_profile_is_not_flattened_to_luminance()
    {
        using var image = Colour(blue: 0, green: 0, red: 255);

        var profile = Across(image);

        Assert.Equal(0, profile.Channels[0].Max, 1);
        Assert.Equal(255, profile.Channels[2].Min, 1);

        // The range spans every channel, so the chart shows all of them.
        Assert.Equal(0, profile.Min, 1);
        Assert.Equal(255, profile.Max, 1);
    }

    [Fact]
    public void A_greyscale_image_still_profiles_as_one_channel()
    {
        using var image = Grey(140);

        var profile = Across(image);

        Assert.Equal(140, profile.Gray().Mean, 1);
    }

    [Fact]
    public void An_image_with_alpha_profiles_four_channels()
    {
        using var image = Colour(blue: 10, green: 20, red: 30, channels: 4);

        var profile = Across(image);

        Assert.Equal(new[] { "B", "G", "R", "A" }, profile.Channels.Select(c => c.Name));
    }

    /// <summary>
    /// Every channel is read at the same coordinates, so a line running off
    /// the image has to leave the gap at the same index in all of them -
    /// otherwise the traces would drift apart where it matters most.
    /// </summary>
    [Fact]
    public void Gaps_fall_at_the_same_place_in_every_channel()
    {
        using var image = Colour(blue: 20, green: 90, red: 200);
        using var source = LineProfileSource.From(image);

        var profile = source.Sample(new PointF(40, 16), new PointF(100, 16));

        Assert.True(profile.HasGaps);
        Assert.All(
            profile.Channels,
            channel => Assert.Equal(profile.ValidCount, channel.ValidCount));

        for (var i = 0; i < profile.Length; i++)
        {
            var missing = profile.Channels.Count(
                channel => !float.IsFinite(channel.Samples[i]));

            Assert.True(
                missing == 0 || missing == profile.Channels.Count,
                $"sample {i} is a gap in {missing} of {profile.Channels.Count} channels");
        }
    }

    [Fact]
    public void The_sampled_length_is_the_same_in_every_channel()
    {
        using var image = Colour(blue: 20, green: 90, red: 200);

        var profile = Across(image);

        Assert.All(
            profile.Channels,
            channel => Assert.Equal(profile.Length, channel.Samples.Length));
    }

    /// <summary>
    /// A degenerate line still has to come back with the right shape, or the
    /// chart would index a channel that is not there.
    /// </summary>
    [Fact]
    public void A_zero_length_line_returns_an_empty_trace_per_channel()
    {
        using var image = Colour(blue: 20, green: 90, red: 200);
        using var source = LineProfileSource.From(image);

        var profile = source.Sample(new PointF(10, 10), new PointF(10, 10));

        Assert.Equal(3, profile.Channels.Count);
        Assert.All(profile.Channels, channel => Assert.Empty(channel.Samples));
        Assert.Equal(0, profile.Length);
    }

    [Theory]
    [InlineData(1, new[] { "Gray" })]
    [InlineData(3, new[] { "B", "G", "R" })]
    [InlineData(4, new[] { "B", "G", "R", "A" })]
    public void Channels_are_named_by_the_engines_own_convention(int count, string[] expected) =>
        Assert.Equal(expected, ProfileChannels.NamesFor(count));

    /// <summary>
    /// An unusual channel count is numbered rather than given invented
    /// colour names.
    /// </summary>
    [Fact]
    public void An_unexpected_channel_count_is_numbered()
    {
        Assert.Equal(new[] { "Ch0", "Ch1" }, ProfileChannels.NamesFor(2));
        Assert.Empty(ProfileChannels.NamesFor(0));
    }
}
