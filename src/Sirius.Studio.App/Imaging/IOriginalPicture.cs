using Emgu.CV.CvEnum;

namespace Sirius.Studio.App.Imaging;

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
}
