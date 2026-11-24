using System.Runtime.InteropServices;

namespace Sirius.Plugins;

/// <summary>How each sample of a <see cref="Picture"/> is stored.</summary>
public enum PixelType
{
    /// <summary>Unsigned 8-bit, 0 to 255. Read with <c>Row&lt;byte&gt;</c>.</summary>
    Byte8,

    /// <summary>
    /// Unsigned 16-bit, 0 to 65535. Read with <c>Row&lt;ushort&gt;</c>. A
    /// 10- or 12-bit camera delivers its frames like this, with the values
    /// stopping at 1023 or 4095.
    /// </summary>
    UInt16,

    /// <summary>32-bit floating point. Read with <c>Row&lt;float&gt;</c>.</summary>
    Float32,
}

/// <summary>What a <see cref="Picture"/>'s channels mean.</summary>
/// <remarks>
/// Three channels are not automatically blue, green and red: after a Colour
/// Convert they may be hue, saturation and value, where hue runs only to 179
/// in eight bits. A node that assumes one layout should check.
/// </remarks>
public enum ColourLayout
{
    /// <summary>Not stated; taken to be what the channel count suggests.</summary>
    Unknown,

    /// <summary>One channel: intensity, a mask, or one separated band.</summary>
    Gray,

    /// <summary>Blue, green, red - the order cameras and image files load in.</summary>
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
/// An image, as a custom node sees one: dimensions, a sample type, and rows
/// of samples.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not an OpenCV matrix. A plugin built on one would link
/// OpenCV's .NET binding, and be tied to the version the application
/// happened to use; a plain buffer is neither, and is what a bridge to
/// Python or C++ needs in any case.
/// </para>
/// <para>
/// Samples are interleaved - a three-channel row runs B, G, R, B, G, R - and
/// rows are contiguous, so <see cref="Stride"/> is always the row length in
/// bytes. Read a row with <see cref="Row{T}(int)"/>, where <c>T</c> matches
/// <see cref="Pixels"/>: <c>byte</c>, <c>ushort</c> or <c>float</c>.
/// </para>
/// <para>
/// A picture read from an input belongs to the application. It is
/// read-only, and it stops working when <see cref="ICustomNode.Run"/>
/// returns - keep one with <see cref="Copy"/>. One made with
/// <see cref="Allocate(int, int, int, PixelType, ColourLayout)"/> belongs to
/// the node until it is published.
/// </para>
/// </remarks>
public sealed unsafe class Picture
{
    private readonly byte[]? _owned;
    private readonly byte* _view;
    private bool _expired;

    private Picture(
        int width, int height, int channels, PixelType pixels, ColourLayout layout,
        byte[]? owned, byte* view, bool isReadOnly)
    {
        Width = width;
        Height = height;
        Channels = channels;
        Pixels = pixels;
        Layout = layout;
        Stride = width * channels * SizeOf(pixels);
        _owned = owned;
        _view = view;
        IsReadOnly = isReadOnly;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Samples per pixel: 1 to 4.</summary>
    public int Channels { get; }

    /// <summary>How each sample is stored.</summary>
    public PixelType Pixels { get; }

    /// <summary>What the channels mean.</summary>
    public ColourLayout Layout { get; }

    /// <summary>Bytes per row. Always <c>Width * Channels * </c> the sample size.</summary>
    public int Stride { get; }

    /// <summary>Samples per row: <c>Width * Channels</c>.</summary>
    public int RowLength => Width * Channels;

    /// <summary>
    /// True for a picture read from an input, which every other node wired
    /// to the same output is also reading.
    /// </summary>
    public bool IsReadOnly { get; }

    /// <summary>
    /// A new, blank picture the node owns and may write.
    /// </summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="channels">Samples per pixel, 1 to 4.</param>
    /// <param name="pixels">How each sample is stored.</param>
    /// <param name="layout">
    /// What the channels mean. <see cref="ColourLayout.Unknown"/> leaves it to
    /// the channel count.
    /// </param>
    public static Picture Allocate(
        int width,
        int height,
        int channels = 1,
        PixelType pixels = PixelType.Byte8,
        ColourLayout layout = ColourLayout.Unknown)
    {
        Validate(width, height, channels, pixels, layout);

        var length = (long)width * height * channels * SizeOf(pixels);
        if (length > Array.MaxLength)
            throw new ArgumentException($"A {width}x{height}x{channels} picture is too large to allocate.");

        return new Picture(width, height, channels, pixels, layout, new byte[length], null, isReadOnly: false);
    }

    /// <summary>A new, blank picture of the same size, sample type and layout as <paramref name="like"/>.</summary>
    public static Picture Allocate(Picture like)
    {
        ArgumentNullException.ThrowIfNull(like);
        return Allocate(like.Width, like.Height, like.Channels, like.Pixels, like.Layout);
    }

    /// <summary>An independent, writable copy the node owns.</summary>
    public Picture Copy()
    {
        var copy = Allocate(this);
        Bytes.CopyTo(copy.WritableBytes);
        return copy;
    }

    /// <summary>One row of samples, read-only.</summary>
    /// <typeparam name="T"><c>byte</c>, <c>ushort</c> or <c>float</c>, matching <see cref="Pixels"/>.</typeparam>
    public ReadOnlySpan<T> Row<T>(int y) where T : unmanaged => Rows<T>(y, 1);

    /// <summary>One row of samples, to write.</summary>
    /// <typeparam name="T"><c>byte</c>, <c>ushort</c> or <c>float</c>, matching <see cref="Pixels"/>.</typeparam>
    /// <exception cref="InvalidOperationException">The picture is an input, and read-only.</exception>
    public Span<T> WritableRow<T>(int y) where T : unmanaged
    {
        RequireWritable();
        return Rows<T>(y, 1);
    }

    /// <summary>Every sample, row after row, read-only.</summary>
    public ReadOnlySpan<T> Samples<T>() where T : unmanaged => Rows<T>(0, Height);

    /// <summary>Every sample, row after row, to write.</summary>
    /// <exception cref="InvalidOperationException">The picture is an input, and read-only.</exception>
    public Span<T> WritableSamples<T>() where T : unmanaged
    {
        RequireWritable();
        return Rows<T>(0, Height);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Width}x{Height}x{Channels} ({Pixels}, {Layout})";

    // ---- for the host -------------------------------------------------------

    /// <summary>
    /// A read-only window onto memory the host owns. Valid until
    /// <see cref="Expire"/>.
    /// </summary>
    /// <remarks>
    /// <c>source</c> is what the host passes straight through if the node
    /// publishes this view unchanged.
    /// </remarks>
    internal static Picture View(
        IntPtr data, int width, int height, int channels, PixelType pixels, ColourLayout layout,
        object? source = null)
    {
        if (data == IntPtr.Zero)
            throw new ArgumentException("A view needs memory to look at.", nameof(data));

        Validate(width, height, channels, pixels, layout);
        return new Picture(width, height, channels, pixels, layout, null, (byte*)data, isReadOnly: true)
        {
            Source = source,
        };
    }

    /// <summary>A read-only copy, standing in for an input in a test.</summary>
    internal Picture AsInput()
    {
        var bytes = Bytes.ToArray();
        return new Picture(Width, Height, Channels, Pixels, Layout, bytes, null, isReadOnly: true);
    }

    /// <summary>
    /// Ends a view's life, so a node that kept one past its run fails
    /// loudly instead of reading memory that has been reused.
    /// </summary>
    internal void Expire() => _expired = true;

    /// <summary>What the host passes straight through when this is published.</summary>
    internal object? Source { get; init; }

    internal ReadOnlySpan<byte> Bytes => Rows<byte>(0, Height, checkType: false);

    private Span<byte> WritableBytes => Rows<byte>(0, Height, checkType: false);

    // ---- internals ----------------------------------------------------------

    private Span<T> Rows<T>(int y, int count, bool checkType = true) where T : unmanaged
    {
        if (_expired)
        {
            throw new ObjectDisposedException(
                nameof(Picture),
                "An input picture is only valid while Run is executing. Keep a copy with Picture.Copy().");
        }

        if (checkType)
            CheckSampleType<T>();

        if ((uint)y > (uint)Height || count < 0 || y + count > Height)
            throw new ArgumentOutOfRangeException(nameof(y), y, $"Row must be from 0 to {Height - 1}.");

        var offset = y * Stride;
        var length = count * Stride;

        var bytes = _owned is not null
            ? _owned.AsSpan(offset, length)
            : new Span<byte>(_view + offset, length);

        return MemoryMarshal.Cast<byte, T>(bytes);
    }

    private void CheckSampleType<T>() where T : unmanaged
    {
        var expected = Pixels switch
        {
            PixelType.Byte8 => typeof(byte),
            PixelType.UInt16 => typeof(ushort),
            _ => typeof(float),
        };

        if (typeof(T) != expected)
        {
            throw new InvalidOperationException(
                $"This picture holds {Pixels} samples; read it as {Keyword(expected)}, not {Keyword(typeof(T))}.");
        }
    }

    private void RequireWritable()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException(
                "This picture is an input, which other nodes are also reading. " +
                "Write into Picture.Allocate(input) or input.Copy() instead.");
        }
    }

    private static void Validate(int width, int height, int channels, PixelType pixels, ColourLayout layout)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"A picture needs a positive size, not {width}x{height}.");

        if (channels is < 1 or > 4)
            throw new ArgumentException($"A picture has 1 to 4 channels, not {channels}.", nameof(channels));

        if (!Enum.IsDefined(pixels))
            throw new ArgumentException($"Unknown pixel type {pixels}.", nameof(pixels));

        var needed = layout switch
        {
            ColourLayout.Unknown => channels,
            ColourLayout.Gray => 1,
            ColourLayout.Bgra => 4,
            _ when Enum.IsDefined(layout) => 3,
            _ => throw new ArgumentException($"Unknown colour layout {layout}.", nameof(layout)),
        };

        if (needed != channels)
        {
            throw new ArgumentException(
                $"{layout} has {needed} channel{(needed == 1 ? "" : "s")}, not {channels}.", nameof(layout));
        }
    }

    private static int SizeOf(PixelType pixels) => pixels switch
    {
        PixelType.Byte8 => sizeof(byte),
        PixelType.UInt16 => sizeof(ushort),
        _ => sizeof(float),
    };

    private static string Keyword(Type type) =>
        type == typeof(byte) ? "byte"
        : type == typeof(ushort) ? "ushort"
        : type == typeof(float) ? "float"
        : type.Name;
}
