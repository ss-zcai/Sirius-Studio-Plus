using Emgu.CV.CvEnum;

namespace Sirius.UI.Imaging;

/// <summary>
/// The picture a viewer shows, with its own values - for saving a frame
/// deeper than the 8 bits it is displayed in.
/// </summary>
public interface IOriginalPicture
{
    /// <summary>How deep it is, or null when it is 8-bit and shown as it is.</summary>
    DepthType? OriginalDepth { get; }

    /// <summary>Writes it to <paramref name="path"/>, in the format the name asks for.</summary>
    void SaveOriginal(string path);

    /// <summary>
    /// Its own value at a pixel, as the viewer's readout shows it - "I 4095"
    /// on a 12-bit frame - or null when it is 8-bit and shown as it is.
    /// </summary>
    string? Reading(int x, int y);

    /// <summary>
    /// A copy of it with its own values, for cutting ROIs from - or null
    /// when it is 8-bit and the displayed picture is its own values.
    /// </summary>
    Emgu.CV.Mat? CopyOriginal() => null;

    /// <summary>How many bits carry it: 12 for a 12-bit camera's frame.</summary>
    int OriginalBits => 16;
}
