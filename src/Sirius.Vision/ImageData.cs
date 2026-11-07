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
public sealed class ImageData : IDisposable, Sirius.Core.Graph.IPixelValues
{
    private Mat? _mat;

    private ImageData(Mat mat, ColourSpace? space, int? bits)
    {
        _mat = mat;
        Width = mat.Width;
        Height = mat.Height;
        Channels = mat.NumberOfChannels;
        Depth = mat.Depth;
        Space = space ?? ColourSpaces.FromChannelCount(Channels);

        var container = ContainerBits(Depth);
        Bits = container == 0 ? 0 : Math.Clamp(bits ?? container, 1, container);
    }

    /// <summary>
    /// Wraps <paramref name="mat"/> and takes ownership of it. The caller must
    /// not dispose <paramref name="mat"/> afterwards.
    /// </summary>
    /// <param name="bits">
    /// How many bits of each value are used, when fewer than its type holds -
    /// 12 for a 12-bit camera's 16-bit frame. Null for all of them.
    /// </param>
    public static ImageData Adopt(Mat mat, ColourSpace? space = null, int? bits = null)
    {
        ArgumentNullException.ThrowIfNull(mat);

        if (mat.IsEmpty)
        {
            mat.Dispose();
            throw new ArgumentException("Cannot wrap an empty Mat.", nameof(mat));
        }

        return new ImageData(mat, space, bits);
    }

    /// <summary>Wraps a copy of <paramref name="mat"/>, leaving the original alone.</summary>
    public static ImageData CopyOf(Mat mat, ColourSpace? space = null, int? bits = null)
    {
        ArgumentNullException.ThrowIfNull(mat);
        return Adopt(mat.Clone(), space, bits);
    }

    /// <summary>
    /// A result made from <paramref name="source"/>, keeping its colour
    /// space and bits where they still hold: the same channel count for the
    /// space, the same pixel type for the bits.
    /// </summary>
    /// <remarks>
    /// A blur of a 12-bit image is still 12-bit; one converted to 8-bit, or
    /// to float, is not.
    /// </remarks>
    public static ImageData From(ImageData source, Mat result)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(result);

        return Adopt(
            result,
            result.NumberOfChannels == source.Channels ? source.Space : null,
            source.BitsFor(result.Depth));
    }

    /// <summary>
    /// This image's bits, for a result of <paramref name="depth"/>: its
    /// own when the pixel type is the same, otherwise all the new type's.
    /// </summary>
    public int? BitsFor(DepthType depth) => depth == Depth ? Bits : null;

    /// <summary>
    /// The underlying matrix. Treat as read-only unless you own this instance.
    /// </summary>
    public Mat Mat => _mat ?? throw new ObjectDisposedException(nameof(ImageData));

    public int Width { get; }

    public int Height { get; }

    public int Channels { get; }

    public DepthType Depth { get; }

    /// <summary>
    /// How many bits of each value carry the picture: 8 for an 8-bit image,
    /// 12 for a 12-bit camera's frame in a 16-bit image, 16 for a full
    /// 16-bit one. 0 for floating point, which has no such limit.
    /// </summary>
    /// <remarks>
    /// Nothing in a 16-bit frame says that its values stop at 4095, so the
    /// source that knows - a Mono12 camera, an Image File told its bits -
    /// says so, and the tools carry it along. Settings are in the image's
    /// own values, so this is what their ranges run to (see
    /// <see cref="MaxValue"/>), and what a conversion to 8 bits scales by.
    /// </remarks>
    public int Bits { get; }

    /// <summary>
    /// The largest value a pixel can hold here: 255 for 8-bit, 4095 for
    /// 12-bit; 1 for floating point, by convention.
    /// </summary>
    public double MaxValue => Bits == 0 ? 1.0 : Math.Pow(2, Bits) - 1;

    /// <summary>The bits a pixel type holds: 8, 16, or 0 for floating point.</summary>
    public static int ContainerBits(DepthType depth) => depth switch
    {
        DepthType.Cv8U or DepthType.Cv8S => 8,
        DepthType.Cv16U or DepthType.Cv16S => 16,
        DepthType.Cv32S => 32,
        _ => 0,
    };

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

    /// <summary>
    /// An 8-bit copy, which the caller owns: scaled by this image's own top
    /// level, so a 12-bit picture's 4095 becomes 255.
    /// </summary>
    /// <remarks>
    /// For what only works on 8 bits - decoding a code, reading text, an
    /// automatic threshold. Scaling by the type's top instead - 65535 - left
    /// a 12-bit picture at most 16 out of 255: nearly black, and unreadable.
    /// Floating point is taken as 0 to 1.
    /// </remarks>
    public Mat ToEightBit()
    {
        var narrowed = new Mat();

        try
        {
            if (Depth == DepthType.Cv8U)
                Mat.CopyTo(narrowed);
            else
                Mat.ConvertTo(narrowed, DepthType.Cv8U, 255.0 / MaxValue);

            return narrowed;
        }
        catch
        {
            narrowed.Dispose();
            throw;
        }
    }

    /// <summary>An independent copy, which the caller owns.</summary>
    public ImageData Clone() => CopyOf(Mat, Space, Bits == 0 ? null : Bits);

    public void Dispose()
    {
        _mat?.Dispose();
        _mat = null;
    }

    public override string ToString() =>
        Bits != 0 && Bits != ContainerBits(Depth)
            ? $"{Width}x{Height}x{Channels} ({Depth}, {Bits}-bit, {Space})"
            : $"{Width}x{Height}x{Channels} ({Depth}, {Space})";
}
