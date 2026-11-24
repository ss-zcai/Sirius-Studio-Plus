using System.Drawing;

namespace Sirius.Vision;

/// <summary>How intermediate pixel values are reconstructed between samples.</summary>
public enum ProfileInterpolation
{
    /// <summary>Whole pixels only. Fast, and the shape the old app produced.</summary>
    Nearest,

    /// <summary>Bilinear. A reasonable default, but flattens sharp edges.</summary>
    Linear,

    /// <summary>Bicubic. Better edge shape, which matters for locating one.</summary>
    Cubic,

    /// <summary>Lanczos over an 8x8 neighbourhood. Sharpest, and the slowest.</summary>
    Lanczos4,
}

/// <summary>
/// One channel's worth of a profile: the samples, and their statistics.
/// </summary>
/// <remarks>
/// Statistics ignore gaps. One sample off the edge of the image must not make
/// min, max and mean all <see cref="float.NaN"/>.
/// </remarks>
public sealed class ProfileChannel
{
    public ProfileChannel(string name, float[] samples)
    {
        Name = name;
        Samples = samples;

        var measured = samples.Where(float.IsFinite).ToArray();

        ValidCount = measured.Length;
        Min = measured.Length == 0 ? 0 : measured.Min();
        Max = measured.Length == 0 ? 0 : measured.Max();
        Mean = measured.Length == 0 ? 0 : measured.Average();
    }

    /// <summary>What this channel is called - "Gray", "R", "G", "B", "A".</summary>
    public string Name { get; }

    public float[] Samples { get; }

    /// <summary>How many samples fell inside the image.</summary>
    public int ValidCount { get; }

    public bool HasGaps => ValidCount != Samples.Length;

    public float Min { get; }

    public float Max { get; }

    public double Mean { get; }
}

/// <summary>
/// Intensity sampled along a line. Managed arrays, so no disposal needed.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="ProfileChannel"/> per channel of the image sampled: a
/// greyscale frame gives one, a colour frame gives one per component. The
/// channels are in the image's own order, which for anything loaded or
/// produced here is OpenCV's - blue first - and are named rather than
/// numbered so the chart can label them.
/// </para>
/// <para>
/// Samples that fall outside the image are <see cref="float.NaN"/> rather
/// than a replicated border value, so a line running off the edge reads as a
/// gap instead of a plateau of invented data. The geometry is shared, so a
/// gap falls at the same index in every channel.
/// </para>
/// </remarks>
public sealed class ProfileData
{
    public ProfileData(
        IReadOnlyList<ProfileChannel> channels,
        PointF start,
        PointF end,
        double spacing = 1.0,
        int thickness = 1)
    {
        Channels = channels;
        Start = start;
        End = end;
        Spacing = spacing;
        Thickness = thickness;
    }

    /// <summary>Convenience for the common single-channel case.</summary>
    public ProfileData(
        float[] samples,
        PointF start,
        PointF end,
        double spacing = 1.0,
        int thickness = 1)
        : this(
            new[] { new ProfileChannel(ProfileChannels.Monochrome, samples) },
            start,
            end,
            spacing,
            thickness)
    {
    }

    public IReadOnlyList<ProfileChannel> Channels { get; }

    /// <summary>
    /// Where the line starts, in image pixels.
    /// </summary>
    /// <remarks>
    /// Sub-pixel, because a line dragged on the viewer lands wherever the
    /// cursor was, not on a whole pixel. The node's own parameters are whole
    /// numbers, which is a property of that editor rather than of the line.
    /// </remarks>
    public PointF Start { get; }

    public PointF End { get; }

    /// <summary>Distance along the line between consecutive samples, in pixels.</summary>
    public double Spacing { get; }

    /// <summary>Width of the band averaged perpendicular to the line, in pixels.</summary>
    public int Thickness { get; }

    /// <summary>Samples taken along the line, the same for every channel.</summary>
    public int Length => Channels.Count == 0 ? 0 : Channels[0].Samples.Length;

    /// <summary>
    /// How many samples fell inside the image. Shared, because every channel
    /// is read at the same coordinates.
    /// </summary>
    public int ValidCount => Channels.Count == 0 ? 0 : Channels[0].ValidCount;

    public bool HasGaps => ValidCount != Length;

    /// <summary>Lowest value across every channel.</summary>
    public float Min => Channels.Count == 0 ? 0 : Channels.Min(channel => channel.Min);

    /// <summary>Highest value across every channel.</summary>
    public float Max => Channels.Count == 0 ? 0 : Channels.Max(channel => channel.Max);

    /// <summary>
    /// What the horizontal axis is measured in: "px" along a line, "°"
    /// round a ring.
    /// </summary>
    /// <remarks>
    /// A profile is not always intensity along a line. Ring Calipers gives
    /// a radius at each angle, which is the same picture - a value per
    /// evenly spaced step, with gaps - on different axes, so it is drawn by
    /// the same chart rather than a second one.
    /// </remarks>
    public string XUnit { get; init; } = "px";

    /// <summary>Where the horizontal axis starts: a start angle, say. Zero for a line.</summary>
    public double XOrigin { get; init; }

    /// <summary>
    /// How values are written: one decimal for intensity, more for a radius
    /// measured to a fraction of a pixel.
    /// </summary>
    public string ValueFormat { get; init; } = "0.#";

    /// <summary>
    /// What a missing sample is: off the image, along a line; a caliper that
    /// found nothing, round a ring.
    /// </summary>
    public string GapLabel { get; init; } = "outside";

    /// <summary>
    /// True for a profile taken along a line in the image, whose geometric
    /// length is worth reporting.
    /// </summary>
    public bool IsLine { get; init; } = true;

    /// <summary>Position on the horizontal axis at a sample index - pixels along a line from its start.</summary>
    public double DistanceAt(int index) => XOrigin + (index * Spacing);

    /// <summary>
    /// Distance actually covered by the samples, in pixels.
    /// </summary>
    /// <remarks>
    /// Not the same as <see cref="Distance"/> once the spacing does not divide
    /// the line exactly: the last sample lands on or before the end point.
    /// </remarks>
    public double SampledLength => Length == 0 ? 0 : (Length - 1) * Spacing;

    /// <summary>Geometric length of the sampled line, in pixels.</summary>
    public double Distance => Math.Sqrt(
        Math.Pow(End.X - Start.X, 2) + Math.Pow(End.Y - Start.Y, 2));
}
