using Emgu.CV;
using Emgu.CV.CvEnum;

namespace Sirius.Vision;

/// <summary>
/// An image flowing between nodes: an owned <see cref="Mat"/> plus cheap
/// metadata.
/// </summary>
/// <remarks>
/// <para>
/// Nodes pass <see cref="ImageData"/> rather than bare <see cref="Mat"/> so the
/// graph has one disposable unit to track, and so metadata can be read for the
/// status bar without touching native memory.
/// </para>
/// <para>
/// Lifetime follows the engine's ownership rule: whoever publishes an instance
/// to an output port hands ownership to the scheduler's cache. A node reading
/// this from an input must treat it as read-only and must not dispose it - the
/// same instance is handed to every other consumer of that port.
/// </para>
/// </remarks>
public sealed class ImageData : IDisposable
{
    private Mat? _mat;

    private ImageData(Mat mat, ColourSpace? space)
    {
        _mat = mat;
        Width = mat.Width;
        Height = mat.Height;
        Channels = mat.NumberOfChannels;
        Depth = mat.Depth;
        Space = space ?? ColourSpaces.FromChannelCount(Channels);
    }

    /// <summary>
    /// Wraps <paramref name="mat"/> and takes ownership of it. The caller must
    /// not dispose <paramref name="mat"/> afterwards.
    /// </summary>
    public static ImageData Adopt(Mat mat, ColourSpace? space = null)
    {
        ArgumentNullException.ThrowIfNull(mat);

        if (mat.IsEmpty)
        {
            mat.Dispose();
            throw new ArgumentException("Cannot wrap an empty Mat.", nameof(mat));
        }

        return new ImageData(mat, space);
    }

    /// <summary>Wraps a copy of <paramref name="mat"/>, leaving the original alone.</summary>
    public static ImageData CopyOf(Mat mat, ColourSpace? space = null)
    {
        ArgumentNullException.ThrowIfNull(mat);
        return Adopt(mat.Clone(), space);
    }

    /// <summary>
    /// The underlying matrix. Treat as read-only unless you own this instance.
    /// </summary>
    public Mat Mat => _mat ?? throw new ObjectDisposedException(nameof(ImageData));

    public int Width { get; }

    public int Height { get; }

    public int Channels { get; }

    public DepthType Depth { get; }

    /// <summary>
    /// What this frame's channels mean.
    /// </summary>
    /// <remarks>
    /// Defaults to what the channel count suggests, so a frame nobody has
    /// tagged behaves as it always did. Colour Convert sets it, and the
    /// filters pass it along: a blur of an HSV image is still HSV.
    /// </remarks>
    public ColourSpace Space { get; }

    public bool IsDisposed => _mat is null;

    /// <summary>True for a single-channel image.</summary>
    public bool IsGrayscale => Channels == 1;

    /// <summary>An independent copy, which the caller owns.</summary>
    public ImageData Clone() => CopyOf(Mat, Space);

    public void Dispose()
    {
        _mat?.Dispose();
        _mat = null;
    }

    public override string ToString() => $"{Width}x{Height}x{Channels} ({Depth}, {Space})";
}
