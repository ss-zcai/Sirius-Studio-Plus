using Sirius.Vision;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Helpers for reading a profile in tests.
/// </summary>
internal static class ProfileAssertions
{
    /// <summary>
    /// The only channel of a greyscale profile, asserting that it is the
    /// only one and that it is the monochrome channel.
    /// </summary>
    /// <remarks>
    /// Most of these tests sample a single-channel fixture, where a second
    /// channel appearing would itself be the bug.
    /// </remarks>
    public static ProfileChannel Gray(this ProfileData data)
    {
        var channel = Assert.Single(data.Channels);
        Assert.Equal(ProfileChannels.Monochrome, channel.Name);
        return channel;
    }
}
