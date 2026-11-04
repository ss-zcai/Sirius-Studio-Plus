using System.Windows;

namespace NodeGraphKit.Controls;

/// <summary>
/// Where the canvas should look to show every node: the arithmetic behind
/// <see cref="GraphEditorView.FitToScreen"/>, kept apart so it can be
/// tested without a window.
/// </summary>
public static class ViewportFrame
{
    /// <summary>Room around the nodes, in graph units.</summary>
    public const double Padding = 48;

    /// <summary>
    /// The closest a fit will get. Fitting a two-node graph to the window
    /// would blow it up to fill the screen, which reads as a fault; 1:1 and
    /// centred is what anyone expects.
    /// </summary>
    public const double MaxZoom = 1.0;

    /// <param name="extent">The nodes' bounding box, in graph units.</param>
    /// <param name="screen">The canvas's size on screen, in pixels.</param>
    /// <returns>The viewport's top-left corner in graph units, and its zoom.</returns>
    public static (Point Location, double Zoom) Frame(Rect extent, Size screen, double minZoom = 0.1, double maxZoom = MaxZoom)
    {
        // Nothing to frame, or nowhere to frame it: the origin at 1:1, where
        // a new graph opens. An extent with no area counts as nothing - a
        // canvas whose nodes are not laid out yet reports one.
        if (extent.IsEmpty || extent.Width <= 0 || extent.Height <= 0 || screen.Width <= 0 || screen.Height <= 0)
            return (new Point(0, 0), 1.0);

        var padded = new Rect(
            extent.X - Padding,
            extent.Y - Padding,
            extent.Width + (2 * Padding),
            extent.Height + (2 * Padding));

        var zoom = Math.Clamp(Math.Min(screen.Width / padded.Width, screen.Height / padded.Height), minZoom, maxZoom);
        var visible = new Size(screen.Width / zoom, screen.Height / zoom);

        return (
            new Point(
                extent.X + (extent.Width / 2) - (visible.Width / 2),
                extent.Y + (extent.Height / 2) - (visible.Height / 2)),
            zoom);
    }
}
