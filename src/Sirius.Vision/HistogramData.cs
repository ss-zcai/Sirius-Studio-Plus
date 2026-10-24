namespace Sirius.Vision;

/// <summary>
/// Per-channel intensity histogram. Plain managed arrays, so unlike
/// <see cref="ImageData"/> this needs no disposal.
/// </summary>
public sealed class HistogramData
{
    public HistogramData(IReadOnlyList<float[]> channels, int binCount)
    {
        Channels = channels;
        BinCount = binCount;
        Max = channels.Count == 0 ? 0 : channels.Max(c => c.Length == 0 ? 0 : c.Max());
    }

    /// <summary>One array of <see cref="BinCount"/> counts per channel.</summary>
    public IReadOnlyList<float[]> Channels { get; }

    public int BinCount { get; }

    /// <summary>Largest bin count across all channels, for axis scaling.</summary>
    public float Max { get; }

    public int ChannelCount => Channels.Count;
}
