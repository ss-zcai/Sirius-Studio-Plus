using Emgu.CV.CvEnum;

namespace Sirius.Vision;

/// <summary>
/// What the channels of an image mean.
/// </summary>
/// <remarks>
/// <para>
/// An image used to record only how many channels it had, so everything
/// downstream guessed from the count: three meant blue, green, red. That is
/// right for a camera frame and wrong the moment a Colour Convert has been
/// through - an HSV image profiled correctly and was labelled B/G/R, and a
/// Colour Range offered a 0-255 band for a hue that only runs to 179.
/// </para>
/// <para>
/// The information already existed and was thrown away: Colour Convert
/// knows exactly what it produced. This is where it now says so.
/// </para>
/// </remarks>
public enum ColourSpace
{
    /// <summary>Not known. Treated as the channel count suggests.</summary>
    Unknown,

    /// <summary>Single channel: intensity, a mask, or one separated band.</summary>
    Gray,

    /// <summary>Blue, green, red - OpenCV's order, and what a loader produces.</summary>
    Bgr,

    /// <summary>Blue, green, red, alpha.</summary>
    Bgra,

    /// <summary>Hue, saturation, value. Hue runs to 179 in eight bits.</summary>
    Hsv,

    /// <summary>Hue, lightness, saturation. Hue runs to 179 in eight bits.</summary>
    Hls,

    /// <summary>CIE L*a*b*.</summary>
    Lab,

    /// <summary>CIE L*u*v*.</summary>
    Luv,

    /// <summary>Luma and two chroma differences.</summary>
    YCrCb,

    /// <summary>CIE XYZ.</summary>
    Xyz,
}

/// <summary>
/// What each colour space's channels are called, and how far they run.
/// </summary>
public static class ColourSpaces
{
    /// <summary>
    /// The space a frame is in when nothing has said otherwise.
    /// </summary>
    /// <remarks>
    /// By channel count, which is the convention the whole engine followed
    /// before the tag existed - so an untagged image behaves exactly as it
    /// always did.
    /// </remarks>
    public static ColourSpace FromChannelCount(int channels) => channels switch
    {
        1 => ColourSpace.Gray,
        3 => ColourSpace.Bgr,
        4 => ColourSpace.Bgra,
        _ => ColourSpace.Unknown,
    };

    /// <summary>Channel names, in the image's own channel order.</summary>
    public static IReadOnlyList<string> NamesOf(ColourSpace space, int channels) => space switch
    {
        ColourSpace.Gray => ["Gray"],
        ColourSpace.Bgr => ["B", "G", "R"],
        ColourSpace.Bgra => ["B", "G", "R", "A"],
        ColourSpace.Hsv => ["H", "S", "V"],
        ColourSpace.Hls => ["H", "L", "S"],
        ColourSpace.Lab => ["L", "a", "b"],
        ColourSpace.Luv => ["L", "u", "v"],
        ColourSpace.YCrCb => ["Y", "Cr", "Cb"],
        ColourSpace.Xyz => ["X", "Y", "Z"],
        _ => Numbered(channels),
    };

    /// <summary>
    /// The highest value a channel takes, which is not always full scale.
    /// </summary>
    /// <remarks>
    /// Hue is the one that matters: OpenCV packs 0-360 degrees into 0-179 so
    /// it fits in a byte. A range control offering 0-255 for it wastes a
    /// third of its travel on values that cannot occur, and a band set near
    /// the top matches nothing at all.
    /// </remarks>
    public static double MaximumOf(ColourSpace space, int channel, DepthType depth)
    {
        if (depth is not (DepthType.Cv8U or DepthType.Cv8S))
            return space is ColourSpace.Hsv or ColourSpace.Hls && channel == 0 ? 360 : 255;

        return space switch
        {
            ColourSpace.Hsv or ColourSpace.Hls when channel == 0 => 179,
            _ => 255,
        };
    }

    /// <summary>What a conversion produces, or null when it is not one we track.</summary>
    public static ColourSpace? ResultOf(ColorConversion conversion) => conversion switch
    {
        ColorConversion.Bgr2Gray or ColorConversion.Rgb2Gray or
        ColorConversion.Bgra2Gray or ColorConversion.Rgba2Gray => ColourSpace.Gray,

        ColorConversion.Gray2Bgr or ColorConversion.Hsv2Bgr or ColorConversion.Hls2Bgr or
        ColorConversion.Lab2Bgr or ColorConversion.Luv2Bgr or ColorConversion.YCrCb2Bgr or
        ColorConversion.Xyz2Bgr or ColorConversion.Bgra2Bgr => ColourSpace.Bgr,

        ColorConversion.Bgr2Bgra or ColorConversion.Gray2Bgra => ColourSpace.Bgra,

        ColorConversion.Bgr2Hsv or ColorConversion.Rgb2Hsv or
        ColorConversion.Bgr2HsvFull or ColorConversion.Rgb2HsvFull => ColourSpace.Hsv,

        ColorConversion.Bgr2Hls or ColorConversion.Rgb2Hls or
        ColorConversion.Bgr2HlsFull or ColorConversion.Rgb2HlsFull => ColourSpace.Hls,

        ColorConversion.Bgr2Lab or ColorConversion.Rgb2Lab => ColourSpace.Lab,
        ColorConversion.Bgr2Luv or ColorConversion.Rgb2Luv => ColourSpace.Luv,
        ColorConversion.Bgr2YCrCb or ColorConversion.Rgb2YCrCb => ColourSpace.YCrCb,
        ColorConversion.Bgr2Xyz or ColorConversion.Rgb2Xyz => ColourSpace.Xyz,

        // Everything else - and there are hundreds, most of them Bayer
        // patterns - is left unsaid rather than guessed at.
        _ => null,
    };

    private static string[] Numbered(int channels) =>
        Enumerable.Range(0, Math.Max(0, channels)).Select(i => $"Ch{i}").ToArray();
}
