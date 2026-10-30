using Sirius.Core.Graph;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>
/// The Overlay socket of a node that makes a new image from an old one:
/// shapes measured on the old image in, the same shapes placed on the new
/// one out.
/// </summary>
/// <remarks>
/// Shared by Crop, Resize, Region of Interest and Polar to Linear, which
/// differ only in where each point goes (<see cref="ShapeMap"/>).
/// </remarks>
internal static class Overlays
{
    /// <summary>The Overlay input's description, the same on every node that has one.</summary>
    public const string InputHelp =
        "Shapes found on the input image - a fitted circle, blobs, edge points - to be placed on the output.";

    /// <summary>
    /// Carries what arrived on <paramref name="input"/> to
    /// <paramref name="output"/>, onto an output image of the given size.
    /// </summary>
    public static void Carry(
        EvalContext context,
        InputPort input,
        OutputPort output,
        ShapeMap map,
        ImageData source,
        int width,
        int height)
    {
        if (context.Get<ShapeSet>(input) is not { } shapes)
        {
            context.Set(output, new ShapeSet([], width, height));
            return;
        }

        // Measured on another image, its points mean somewhere else; it is
        // still carried, but said, as a viewer says it.
        if (shapes.Count > 0 && !shapes.MatchesFrame(source.Width, source.Height))
        {
            context.Warn(
                $"The overlay was measured on a {shapes.FrameWidth} x {shapes.FrameHeight} image, " +
                $"not this {source.Width} x {source.Height} one, so it will not sit where it was found.");
        }

        context.Set(output, map.Map(shapes, width, height));
    }
}
