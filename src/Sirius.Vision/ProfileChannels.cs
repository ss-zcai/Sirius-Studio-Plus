namespace Sirius.Vision;

/// <summary>
/// What to call each channel of an image being profiled.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ImageData"/> records how many channels a frame has but not what
/// they mean, so the names come from the one convention the whole engine
/// already follows: OpenCV's, in which a three-channel frame is blue, green,
/// red in that order and a fourth channel is alpha. Every image here is
/// loaded or produced through OpenCV, so the convention holds.
/// </para>
/// <para>
/// This is the single place that assumption lives. Supporting HSV, Lab or
/// anything else properly means tagging <see cref="ImageData"/> with a colour
/// space - a Colour Convert node knows exactly what it produced, and
/// currently throws that away - and then naming from the tag instead of from
/// the count. Until then an HSV image profiles correctly and is labelled
/// B/G/R, which is wrong but at least wrong in one findable place.
/// </para>
/// </remarks>
public static class ProfileChannels
{
    /// <summary>The name a single-channel profile carries.</summary>
    public const string Monochrome = "Gray";

    /// <summary>
    /// Names for a frame with <paramref name="channels"/> channels, in the
    /// image's own channel order.
    /// </summary>
    public static IReadOnlyList<string> NamesFor(int channels) => channels switch
    {
        1 => new[] { Monochrome },
        3 => new[] { "B", "G", "R" },
        4 => new[] { "B", "G", "R", "A" },

        // Something unusual - a split-off multi-band image, say. Numbered is
        // honest; inventing colour names for it would not be.
        _ => Enumerable.Range(0, Math.Max(0, channels))
            .Select(index => $"Ch{index}")
            .ToArray(),
    };
}
