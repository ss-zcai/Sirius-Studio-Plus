using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace Sirius.UI.Imaging;

/// <summary>
/// Cuts a rectangle drawn on a viewer out of its picture, turned upright,
/// for saving as an ROI.
/// </summary>
/// <remarks>
/// <para>
/// The rectangle is its three kept corners - top left, top right, bottom
/// right, in image coordinates where pixel (i, j) covers i to i + 1 - and
/// the crop is as wide as its top and as tall as its side, its top the
/// rectangle's top. Turned rather than boxed: a board lying at 12 degrees
/// is saved as the board, not as the board and the corners of the bench
/// around it.
/// </para>
/// <para>
/// Through OpenCV and on the picture's own values, so a 12-bit frame
/// gives a 12-bit crop. A rectangle square to the image is copied pixel
/// for pixel; a turned one is resampled, bilinearly. What lies outside
/// the picture is black.
/// </para>
/// </remarks>
public static class RoiCrop
{
    /// <summary>The crop of <paramref name="source"/> inside a kept rectangle.</summary>
    public static Mat Cut(Mat source, Point topLeft, Point topRight, Point bottomRight)
    {
        ArgumentNullException.ThrowIfNull(source);

        var right = topRight - topLeft;
        var down = bottomRight - topRight;

        var width = Math.Max(1, (int)Math.Round(right.Length));
        var height = Math.Max(1, (int)Math.Round(down.Length));

        if (right.Length < 1e-9 || down.Length < 1e-9)
            return new Mat(height, width, source.Depth, source.NumberOfChannels);

        right.Normalize();
        down.Normalize();

        // Square to the image: snapped to whole pixels and copied as they
        // are, so a crop of an untouched part is the part, value for value.
        var square = IsUnit(right) && IsUnit(down);

        if (square)
            topLeft = new Point(Math.Round(topLeft.X), Math.Round(topLeft.Y));

        // Where each crop pixel's centre falls in the picture, as OpenCV
        // counts it - pixel centres on whole numbers, half a pixel in from
        // the edge the corners are measured to.
        var origin = topLeft - new Vector(0.5, 0.5) + (right * 0.5) + (down * 0.5);

        using var map = new Matrix<double>(2, 3);
        map[0, 0] = right.X;
        map[0, 1] = down.X;
        map[0, 2] = origin.X;
        map[1, 0] = right.Y;
        map[1, 1] = down.Y;
        map[1, 2] = origin.Y;

        var crop = new Mat();

        CvInvoke.WarpAffine(
            source,
            crop,
            map,
            new System.Drawing.Size(width, height),
            square ? Inter.Nearest : Inter.Linear,
            Warp.InverseMap,
            BorderType.Constant,
            new MCvScalar(0));

        return crop;
    }

    private static bool IsUnit(Vector axis) =>
        (Math.Abs(Math.Abs(axis.X) - 1) < 1e-9 && Math.Abs(axis.Y) < 1e-9) ||
        (Math.Abs(Math.Abs(axis.Y) - 1) < 1e-9 && Math.Abs(axis.X) < 1e-9);

    /// <summary>
    /// The picture a viewer shows as an OpenCV image, for one with no
    /// deeper original: grey stays grey, anything else becomes BGR.
    /// </summary>
    public static Mat FromBitmap(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var grey = bitmap.Format == PixelFormats.Gray8;
        var format = grey ? PixelFormats.Gray8 : PixelFormats.Bgr24;

        BitmapSource pixels = bitmap.Format == format
            ? bitmap
            : new FormatConvertedBitmap(bitmap, format, null, 0);

        var mat = new Mat(pixels.PixelHeight, pixels.PixelWidth, DepthType.Cv8U, grey ? 1 : 3);
        pixels.CopyPixels(Int32Rect.Empty, mat.DataPointer, mat.Step * mat.Rows, mat.Step);
        return mat;
    }

    /// <summary>
    /// "board-3.png" from "board.png" and 3: one file of a numbered set, as
    /// artboards are exported.
    /// </summary>
    public static string Numbered(string path, int number)
    {
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        return Path.Combine(folder, $"{name}-{number}{extension}");
    }

    /// <summary>
    /// Writes a crop to <paramref name="path"/> - with its own values where
    /// the format holds them, else brought to 8 bits.
    /// </summary>
    /// <remarks>
    /// To 8 bits by the picture's bit count, so a 12-bit crop saved as a
    /// JPEG looks as it did on screen rather than nearly black.
    /// </remarks>
    public static void Save(Mat crop, string path, int bits)
    {
        ArgumentNullException.ThrowIfNull(crop);

        if (ImageFileWriter.CanHold(crop.Depth, path))
        {
            ImageFileWriter.Save(crop, path);
            return;
        }

        using var eight = new Mat();
        var full = crop.Depth == DepthType.Cv16U ? (1 << Math.Clamp(bits, 1, 16)) - 1 : 1.0;
        crop.ConvertTo(eight, DepthType.Cv8U, 255.0 / full);
        ImageFileWriter.Save(eight, path);
    }
}
