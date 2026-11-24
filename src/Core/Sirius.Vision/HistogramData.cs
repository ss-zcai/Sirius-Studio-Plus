namespace Sirius.Vision;

/// <summary>
/// Per-channel intensity histogram. Plain managed arrays, so unlike
/// <see cref="ImageData"/> this needs no disposal.
/// </summary>
public sealed class HistogramData
{
    /// <param name="levels">
    /// How many levels the bins span: 256 for 8-bit, 4096 for 12-bit. A
    /// bin covers <c>levels / binCount</c> of them.
    /// </param>
    public HistogramData(IReadOnlyList<float[]> channels, int binCount, int levels = 256)
    {
        Channels = channels;
        BinCount = binCount;
        Levels = Math.Max(binCount, levels);
        Max = channels.Count == 0 ? 0 : channels.Max(c => c.Length == 0 ? 0 : c.Max());
    }

    /// <summary>How many levels the bins span: 256 for 8-bit, 4096 for 12-bit.</summary>
    public int Levels { get; }

    /// <summary>The levels one bin counts, first and last.</summary>
    public (int First, int Last) LevelsOf(int bin)
    {
        var first = (int)Math.Floor((double)bin * Levels / BinCount);
        var next = (int)Math.Floor((double)(bin + 1) * Levels / BinCount);
        return (first, Math.Max(first, next - 1));
    }

    /// <summary>One array of <see cref="BinCount"/> counts per channel.</summary>
    public IReadOnlyList<float[]> Channels { get; }

    public int BinCount { get; }

    /// <summary>Largest bin count across all channels, for axis scaling.</summary>
    public float Max { get; }

    public int ChannelCount => Channels.Count;
}
