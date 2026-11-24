using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sirius.Studio.App.Imaging;

/// <summary>
/// Writes a picture from a viewer to a file, in the format its name asks for.
/// </summary>
/// <remarks>
/// PNG unless the name says otherwise: lossless, and 16 bits deep where the
/// picture is, so a saved frame can be run through a recipe again and give
/// the same answer, which a JPEG's compression would not promise.
/// </remarks>
public static class ImageFileWriter
{
    /// <summary>The Save dialog's choices, PNG first.</summary>
    public const string Filter =
        "PNG image (*.png)|*.png|" +
        "JPEG image (*.jpg)|*.jpg;*.jpeg|" +
        "Bitmap (*.bmp)|*.bmp|" +
        "TIFF image (*.tif)|*.tif;*.tiff";

    /// <summary>Saves <paramref name="picture"/> to <paramref name="path"/>, replacing any file there.</summary>
    public static void Save(BitmapSource picture, string path)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var encoder = EncoderFor(path);
        encoder.Frames.Add(BitmapFrame.Create(Opaque(picture)));

        // Written whole to a temporary file first, so a failure part-way -
        // a full disk - does not leave a broken file in place of a good one.
        var temporary = path + ".saving";

        try
        {
            using (var stream = File.Create(temporary))
                encoder.Save(stream);

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    /// <summary>
    /// Whether a file at <paramref name="path"/> keeps pixels of
    /// <paramref name="depth"/> as they are: PNG and TIFF take 16 bits, TIFF
    /// alone takes float, and JPEG and BMP take 8 bits only.
    /// </summary>
    public static bool CanHold(DepthType depth, string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var tiff = extension is ".tif" or ".tiff";
        var png = extension is ".png";

        return depth switch
        {
            DepthType.Cv8U => true,
            DepthType.Cv16U => png || tiff,
            DepthType.Cv32F => tiff,
            _ => false,
        };
    }

    /// <summary>
    /// Saves an image's own pixels - 16-bit, float - through OpenCV, which
    /// writes them as they are where WPF cannot: its TIFF encoder turns
    /// float into 16-bit without a word.
    /// </summary>
    public static void Save(Mat pixels, string path)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // OpenCV picks the format by the extension, so the temporary file
        // keeps it.
        var temporary = Path.ChangeExtension(path, ".saving" + Path.GetExtension(path));

        try
        {
            if (!CvInvoke.Imwrite(temporary, pixels))
                throw new IOException($"OpenCV could not write {Path.GetFileName(path)}.");

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static BitmapEncoder EncoderFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            _ => new PngBitmapEncoder(),
        };

    /// <summary>
    /// Drops the alpha channel a drawing with overlays carries. The picture
    /// under the overlays covers every pixel, so it says nothing - and a
    /// JPEG cannot hold it, a bitmap with one opens oddly in half the
    /// programs that read it, and a PNG is a quarter larger for it.
    /// </summary>
    private static BitmapSource Opaque(BitmapSource picture)
    {
        if (picture.Format != PixelFormats.Pbgra32 && picture.Format != PixelFormats.Bgra32)
            return picture;

        var converted = new FormatConvertedBitmap(picture, PixelFormats.Bgr24, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>
    /// A name to offer: what the picture is of, and when, so a run of saves
    /// does not overwrite itself - "Image Viewer 1 2026-11-02 153012.png".
    /// </summary>
    public static string SuggestedName(string? title, DateTime when)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "Image" : title.Trim();

        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '-');

        return $"{name} {when:yyyy-MM-dd HHmmss}.png";
    }
}
