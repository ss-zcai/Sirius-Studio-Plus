using System.Windows;

namespace Sirius.Studio.App.Layout;

/// <summary>
/// Where the graph editor should be looking.
/// </summary>
/// <remarks>
/// Node positions are stored in the recipe's own coordinates, and a graph
/// built by dragging nodes around drifts a long way from the origin - a
/// recipe here has its nodes past x=3000. The editor opens at the origin, so
/// such a recipe loads correctly onto an apparently empty canvas and has to
/// be hunted for by scrolling.
///
/// Separated from the window so the arithmetic can be tested without a
/// NodifyEditor, which needs realised item containers before it will report
/// an extent.
/// </remarks>
public static class GraphView
{
    /// <summary>Breathing room around the nodes, in graph units.</summary>
    public const double Padding = 48;

    /// <summary>
    /// The closest the view will get.
    /// </summary>
    /// <remarks>
    /// Fitting a two-node recipe to the window would blow it up to fill the
    /// screen, which reads as a bug rather than as a convenience. Opening at
    /// 1:1 and centred is what an operator expects.
    /// </remarks>
    public const double MaxZoom = 1.0;

    /// <summary>
    /// The viewport that frames <paramref name="extent"/> inside an editor of
    /// <paramref name="screen"/> pixels.
    /// </summary>
    /// <param name="extent">The nodes' bounding box, in graph units.</param>
    /// <param name="screen">The editor's size on screen, in pixels.</param>
    /// <param name="minZoom">The editor's own lower zoom limit.</param>
    /// <returns>Top-left corner in graph units, and the zoom to apply.</returns>
    public static (Point Location, double Zoom) Frame(
        Rect extent,
        Size screen,
        double minZoom = 0.1,
        double maxZoom = MaxZoom)
    {
        // Nothing to frame, or nowhere to frame it: the origin at 1:1 is the
        // same place a new recipe opens. An extent with no area counts as
        // nothing - an editor with no realised containers reports one, and
        // framing it would centre the view on empty space well off the
        // origin.
        if (extent.IsEmpty || extent.Width <= 0 || extent.Height <= 0 ||
            screen.Width <= 0 || screen.Height <= 0)
            return (new Point(0, 0), 1.0);

        var padded = new Rect(
            extent.X - Padding,
            extent.Y - Padding,
            extent.Width + (2 * Padding),
            extent.Height + (2 * Padding));

        var zoom = Math.Min(screen.Width / padded.Width, screen.Height / padded.Height);
        zoom = Math.Clamp(zoom, minZoom, maxZoom);

        // What the editor can show at that zoom, in graph units.
        var visible = new Size(screen.Width / zoom, screen.Height / zoom);

        return (
            new Point(
                extent.X + (extent.Width / 2) - (visible.Width / 2),
                extent.Y + (extent.Height / 2) - (visible.Height / 2)),
            zoom);
    }
}
