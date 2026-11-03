using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Vision;

namespace Sirius.Studio.App.Imaging;

/// <summary>
/// Converts an OpenCV <see cref="Mat"/> into a WPF <see cref="BitmapSource"/>.
/// </summary>
/// <remarks>
/// The old app called <c>Mat.ToBitmap()</c> and handed a
/// <see cref="System.Drawing.Bitmap"/> to a WinForms control. WPF wants a
/// <see cref="BitmapSource"/>, and going via <c>System.Drawing</c> would mean an
/// extra copy plus a GDI handle to release on every frame. Writing straight from
/// the Mat's buffer avoids both.
/// </remarks>
public static class MatImageConverter
{
    /// <summary>
    /// Produces a frozen, independent bitmap. The returned image does not
    /// reference <paramref name="image"/>, so it stays valid after the graph
    /// disposes the underlying buffer.
    /// </summary>
    public static BitmapSource? ToBitmapSource(ImageData? image, int displayBits = FullRange) =>
        image is null || image.IsDisposed ? null : ToBitmapSource(image.Mat, displayBits);

    /// <summary>A 16-bit image shown across its whole range, 0 to 65535.</summary>
    public const int FullRange = 16;

    /// <summary>A 16-bit image shown from its darkest pixel to its brightest.</summary>
    public const int Stretch = 0;

    /// <param name="mat">The image.</param>
    /// <param name="displayBits">
    /// For a 16-bit image, how many of its bits carry the picture: 12 for a
    /// 12-bit camera's frame, whose values stop at 4095, so 4095 is shown
    /// white. <see cref="Stretch"/> shows it from its darkest pixel to its
    /// brightest. Other depths ignore it.
    /// </param>
    public static BitmapSource? ToBitmapSource(Mat? mat, int displayBits = FullRange)
    {
        if (mat is null || mat.IsEmpty)
            return null;

        // Displayable formats are 8-bit; anything else is scaled down first.
        var source = ToDisplayDepth(mat, displayBits, out var owned);
        try
        {
            var format = ResolvePixelFormat(source);
            if (format is null)
                return null;

            var bitmap = new WriteableBitmap(source.Width, source.Height, 96, 96, format.Value, null);
            var stride = (int)source.Step;

            bitmap.WritePixels(
                new Int32Rect(0, 0, source.Width, source.Height),
                source.DataPointer,
                stride * source.Height,
                stride);

            // Frozen so it can be handed to the UI thread from a worker and
            // rendered without further synchronisation.
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            // Only dispose when a conversion actually allocated: otherwise
            // `source` is the caller's Mat, owned by the graph's cache.
            if (owned)
                source.Dispose();
        }
    }

    /// <summary>
    /// Returns an 8-bit view of <paramref name="mat"/>, converting if needed.
    /// <paramref name="owned"/> indicates whether the result is a new Mat.
    /// </summary>
    private static Mat ToDisplayDepth(Mat mat, int displayBits, out bool owned)
    {
        if (mat.Depth == DepthType.Cv8U)
        {
            owned = false;
            return mat;
        }

        owned = true;
        var converted = new Mat();

        switch (mat.Depth)
        {
            case DepthType.Cv16U when displayBits == Stretch:
                NormaliseToByte(mat, converted);
                break;

            case DepthType.Cv16U:
                // A 10- or 12-bit camera's frame comes in a 16-bit container
                // with its values stopping at 1023 or 4095, and nothing in it
                // says so - divided by 257 it is all but black. Anything
                // brighter than the bits allow saturates at white.
                var top = (1 << Math.Clamp(displayBits, 8, 16)) - 1;
                mat.ConvertTo(converted, DepthType.Cv8U, 255.0 / top);
                break;

            case DepthType.Cv8S:
                mat.ConvertTo(converted, DepthType.Cv8U, 1.0, 128.0);
                break;

            default:
                // Float and signed types have no inherent display range, so
                // stretch the actual min/max across 0-255.
                NormaliseToByte(mat, converted);
                break;
        }

        return converted;
    }

    private static void NormaliseToByte(Mat source, Mat destination)
    {
        double min = 0, max = 0;
        var minLocation = new System.Drawing.Point();
        var maxLocation = new System.Drawing.Point();

        if (source.NumberOfChannels == 1)
        {
            CvInvoke.MinMaxLoc(source, ref min, ref max, ref minLocation, ref maxLocation);
        }
        else
        {
            using var gray = new Mat();
            CvInvoke.CvtColor(source, gray, ColorConversion.Bgr2Gray);
            CvInvoke.MinMaxLoc(gray, ref min, ref max, ref minLocation, ref maxLocation);
        }

        var range = max - min;
        var scale = range > double.Epsilon ? 255.0 / range : 1.0;
        source.ConvertTo(destination, DepthType.Cv8U, scale, -min * scale);
    }

    private static PixelFormat? ResolvePixelFormat(Mat mat) => mat.NumberOfChannels switch
    {
        1 => PixelFormats.Gray8,
        3 => PixelFormats.Bgr24,
        4 => PixelFormats.Bgra32,
        _ => null,
    };
}
