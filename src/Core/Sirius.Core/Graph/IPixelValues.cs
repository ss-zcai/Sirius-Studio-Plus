namespace Sirius.Core.Graph;

/// <summary>
/// A value whose pixels have a top level - an image: 255 for 8 bits, 4095
/// for a 12-bit camera's frame.
/// </summary>
/// <remarks>
/// Here rather than on the image type, which lives with OpenCV, so the
/// engine can tell a node what depth it last ran on (see
/// <see cref="VisionNode.ImageMaxValue"/>) without knowing what an image is.
/// </remarks>
public interface IPixelValues
{
    /// <summary>The largest value a pixel can hold.</summary>
    double MaxValue { get; }
}
