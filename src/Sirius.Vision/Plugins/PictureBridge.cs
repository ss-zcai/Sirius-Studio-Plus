using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Plugins;

namespace Sirius.Vision.Plugins;

/// <summary>
/// Converts between the application's images and a custom node's pictures.
/// </summary>
/// <remarks>
/// The one place OpenCV meets the plugin contract. Everything a plugin sees
/// is a plain buffer; everything the graph carries is an
/// <see cref="ImageData"/>. Keeping the conversion here, in code this
/// repository owns, is what keeps OpenCV out of every plugin.
/// </remarks>
internal static class PictureBridge
{
    /// <summary>
    /// A read-only view of <paramref name="image"/>, without copying when
    /// the memory can be shown as it is.
    /// </summary>
    /// <remarks>
    /// A copy is made only when the frame's layout is one a plugin is not
    /// promised. A sample type other than 8-bit, 16-bit unsigned or float is
    /// converted to float - exact for the signed 8- and 16-bit types an edge
    /// filter produces, approximate for 32-bit integers past 2^24 and for
    /// 64-bit floats. A slice of a larger matrix, whose rows are not
    /// adjacent, is made contiguous. Copies go in
    /// <paramref name="temporaries"/>, to be released when the run ends.
    /// </remarks>
    public static Picture View(ImageData image, ICollection<IDisposable> temporaries)
    {
        var mat = image.Mat;

        if (PixelsOf(mat.Depth) is not { } pixels)
        {
            var converted = new Mat();
            temporaries.Add(converted);
            mat.ConvertTo(converted, DepthType.Cv32F);
            mat = converted;
            pixels = PixelType.Float32;
        }

        if (!mat.IsContinuous)
        {
            var copy = mat.Clone();
            temporaries.Add(copy);
            mat = copy;
        }

        return Picture.View(
            mat.DataPointer,
            mat.Width,
            mat.Height,
            mat.NumberOfChannels,
            pixels,
            LayoutOf(image.Space, mat.NumberOfChannels),
            source: image);
    }

    /// <summary>A new image holding a copy of <paramref name="picture"/>.</summary>
    public static ImageData ToImage(Picture picture) =>
        ToImage(picture.Bytes, picture.Width, picture.Height, picture.Channels, picture.Pixels, picture.Layout);

    /// <summary>
    /// A new image holding a copy of rows of samples, top to bottom with no
    /// padding - what a Python node sends back.
    /// </summary>
    /// <param name="bits">How many bits of the values carry the picture, or null for all of them.</param>
    public static unsafe ImageData ToImage(
        ReadOnlySpan<byte> source, int width, int height, int channels, PixelType pixels, ColourLayout layout,
        int? bits = null)
    {
        if (width < 1 || height < 1 || channels is < 1 or > 4)
            throw new ArgumentException($"A {width}x{height} image with {channels} channels cannot be made.");

        var stride = width * channels * SampleSize(pixels);
        if (source.Length != (long)stride * height)
        {
            throw new ArgumentException(
                $"A {width}x{height}x{channels} image of {pixels} needs {(long)stride * height} bytes, not {source.Length}.");
        }

        if (LayoutOf(SpaceOf(layout) ?? ColourSpace.Gray, channels) != layout)
            layout = ColourLayout.Unknown;

        var mat = new Mat(height, width, DepthOf(pixels), channels);

        try
        {
            if (mat.IsContinuous && mat.Step == stride)
            {
                source.CopyTo(new Span<byte>((void*)mat.DataPointer, source.Length));
            }
            else
            {
                for (var y = 0; y < height; y++)
                {
                    var row = source.Slice(y * stride, stride);
                    row.CopyTo(new Span<byte>((byte*)mat.DataPointer + ((long)y * mat.Step), stride));
                }
            }

            return ImageData.Adopt(mat, SpaceOf(layout), bits);
        }
        catch
        {
            mat.Dispose();
            throw;
        }
    }

    private static int SampleSize(PixelType pixels) => pixels switch
    {
        PixelType.UInt16 => 2,
        PixelType.Float32 => 4,
        _ => 1,
    };

    internal static PixelType? PixelsOf(DepthType depth) => depth switch
    {
        DepthType.Cv8U => PixelType.Byte8,
        DepthType.Cv16U => PixelType.UInt16,
        DepthType.Cv32F => PixelType.Float32,
        _ => null,
    };

    internal static DepthType DepthOf(PixelType pixels) => pixels switch
    {
        PixelType.Byte8 => DepthType.Cv8U,
        PixelType.UInt16 => DepthType.Cv16U,
        PixelType.Float32 => DepthType.Cv32F,
        _ => throw new ArgumentOutOfRangeException(nameof(pixels), pixels, null),
    };

    /// <summary>
    /// The layout of a frame, or unknown when its colour space does not
    /// match its channel count - a picture refuses a contradiction, and the
    /// channel count is the one of the two that is certainly right.
    /// </summary>
    internal static ColourLayout LayoutOf(ColourSpace space, int channels)
    {
        var layout = space switch
        {
            ColourSpace.Gray => ColourLayout.Gray,
            ColourSpace.Bgr => ColourLayout.Bgr,
            ColourSpace.Bgra => ColourLayout.Bgra,
            ColourSpace.Hsv => ColourLayout.Hsv,
            ColourSpace.Hls => ColourLayout.Hls,
            ColourSpace.Lab => ColourLayout.Lab,
            ColourSpace.Luv => ColourLayout.Luv,
            ColourSpace.YCrCb => ColourLayout.YCrCb,
            ColourSpace.Xyz => ColourLayout.Xyz,
            _ => ColourLayout.Unknown,
        };

        var expected = layout switch
        {
            ColourLayout.Unknown => channels,
            ColourLayout.Gray => 1,
            ColourLayout.Bgra => 4,
            _ => 3,
        };

        return expected == channels ? layout : ColourLayout.Unknown;
    }

    /// <summary>The colour space for a layout; null leaves it to the channel count.</summary>
    internal static ColourSpace? SpaceOf(ColourLayout layout) => layout switch
    {
        ColourLayout.Gray => ColourSpace.Gray,
        ColourLayout.Bgr => ColourSpace.Bgr,
        ColourLayout.Bgra => ColourSpace.Bgra,
        ColourLayout.Hsv => ColourSpace.Hsv,
        ColourLayout.Hls => ColourSpace.Hls,
        ColourLayout.Lab => ColourSpace.Lab,
        ColourLayout.Luv => ColourSpace.Luv,
        ColourLayout.YCrCb => ColourSpace.YCrCb,
        ColourLayout.Xyz => ColourSpace.Xyz,
        _ => null,
    };
}
