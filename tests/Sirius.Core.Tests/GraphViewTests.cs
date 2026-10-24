using System.Windows;
using Sirius.Studio.App.Layout;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Framing the graph when a recipe is opened.
/// </summary>
/// <remarks>
/// The case that prompted it: a saved recipe whose nodes sit past x=3000,
/// opened into an editor that starts at the origin. Everything loads, and the
/// canvas looks empty.
/// </remarks>
public class GraphViewTests
{
    private static readonly Size Screen = new(1200, 800);

    /// <summary>The graph-unit rectangle the returned viewport can show.</summary>
    private static Rect Visible((Point Location, double Zoom) frame, Size screen) =>
        new(frame.Location,
            new Size(screen.Width / frame.Zoom, screen.Height / frame.Zoom));

    [Fact]
    public void A_distant_graph_ends_up_on_screen()
    {
        var nodes = new Rect(3340, 980, 960, 280);

        var frame = GraphView.Frame(nodes, Screen);

        Assert.True(Visible(frame, Screen).Contains(nodes), "the nodes are still off screen");
    }

    [Fact]
    public void The_graph_ends_up_centred()
    {
        var nodes = new Rect(3340, 980, 960, 280);

        var frame = GraphView.Frame(nodes, Screen);
        var visible = Visible(frame, Screen);

        Assert.Equal(nodes.X + (nodes.Width / 2), visible.X + (visible.Width / 2), 3);
        Assert.Equal(nodes.Y + (nodes.Height / 2), visible.Y + (visible.Height / 2), 3);
    }

    /// <summary>
    /// A graph wider than the window has to be zoomed out to fit, padding
    /// included - otherwise the outermost nodes sit on the very edge.
    /// </summary>
    [Fact]
    public void A_graph_larger_than_the_window_is_zoomed_out_to_fit()
    {
        var nodes = new Rect(0, 0, 6000, 400);

        var frame = GraphView.Frame(nodes, Screen);

        Assert.True(frame.Zoom < 1, "a graph five times the window width was not zoomed out");
        Assert.True(Visible(frame, Screen).Contains(nodes));
    }

    /// <summary>
    /// Fitting a small recipe would magnify two nodes to fill the screen,
    /// which looks like a fault rather than a convenience.
    /// </summary>
    [Fact]
    public void A_small_graph_is_not_magnified()
    {
        var frame = GraphView.Frame(new Rect(100, 100, 180, 90), Screen);

        Assert.Equal(1.0, frame.Zoom);
    }

    [Fact]
    public void The_editors_own_zoom_floor_is_respected()
    {
        var frame = GraphView.Frame(new Rect(0, 0, 500_000, 500_000), Screen, minZoom: 0.25);

        Assert.Equal(0.25, frame.Zoom);
    }

    /// <summary>An empty recipe opens where a new one does.</summary>
    [Fact]
    public void An_empty_graph_opens_at_the_origin()
    {
        var frame = GraphView.Frame(Rect.Empty, Screen);

        Assert.Equal(new Point(0, 0), frame.Location);
        Assert.Equal(1.0, frame.Zoom);
    }

    /// <summary>
    /// An editor that has realised no containers reports a zero-area extent
    /// rather than an empty one. Framing that would centre the view on a
    /// point, leaving the viewport half a screen away from the origin.
    /// </summary>
    [Fact]
    public void An_extent_with_no_area_opens_at_the_origin()
    {
        var frame = GraphView.Frame(new Rect(0, 0, 0, 0), Screen);

        Assert.Equal(new Point(0, 0), frame.Location);
        Assert.Equal(1.0, frame.Zoom);
    }

    /// <summary>
    /// The editor has no size before its first layout pass, and dividing by it
    /// would put the viewport at infinity.
    /// </summary>
    [Fact]
    public void An_unmeasured_editor_is_left_alone()
    {
        var frame = GraphView.Frame(new Rect(3340, 980, 960, 280), new Size(0, 0));

        Assert.Equal(new Point(0, 0), frame.Location);
        Assert.Equal(1.0, frame.Zoom);
    }
}
