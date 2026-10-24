using System.Windows;
using Sirius.Studio.App.Controls;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Resizing a region that has been turned.
/// </summary>
/// <remarks>
/// The property that matters, and the one that was broken: dragging one
/// corner must leave the opposite corner exactly where it is, on screen,
/// at any angle. Upright it worked; turned, the far corner slid away,
/// because the pointer was read in screen axes against an edge that is an
/// edge of the region.
/// </remarks>
public class RoiResizeTests
{
    private static readonly Rect Start = new(80, 90, 40, 20);

    /// <summary>A point in the region's own frame, put into image coordinates.</summary>
    private static Point World(Rect rect, double degrees, Point local)
    {
        var centre = new Point(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
        var radians = degrees * Math.PI / 180.0;

        var dx = local.X - centre.X;
        var dy = local.Y - centre.Y;

        return new Point(
            centre.X + (dx * Math.Cos(radians)) - (dy * Math.Sin(radians)),
            centre.Y + (dx * Math.Sin(radians)) + (dy * Math.Cos(radians)));
    }

    /// <summary>A corner of a turned region, in image coordinates.</summary>
    private static Point Corner(Rect rect, double degrees, bool right, bool bottom) =>
        World(rect, degrees, new Point(
            right ? rect.Right : rect.Left,
            bottom ? rect.Bottom : rect.Top));

    /// <summary>
    /// Where to put the pointer to grow the region by a given amount.
    /// </summary>
    /// <remarks>
    /// Offset along the region's own axes, not the screen's. Offsetting in
    /// screen axes can land the pointer past the opposite edge, which flips
    /// the region - correct behaviour, but not what these cases are about.
    /// </remarks>
    private static Point Grow(Rect rect, double degrees, double by) =>
        World(rect, degrees, new Point(rect.Right + by, rect.Bottom + by));

    /// <summary>
    /// Drag the bottom-right corner somewhere, and check the top-left one
    /// has not moved.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(-30)]
    [InlineData(187)]
    public void Dragging_a_corner_leaves_the_opposite_corner_where_it_was(double degrees)
    {
        var anchor = Corner(Start, degrees, right: false, bottom: false);

        var pointer = Grow(Start, degrees, 25);

        var resized = ImageViewer.ResizeTurned(
            Start, ImageViewer.Edge.Right | ImageViewer.Edge.Bottom, degrees, pointer);

        var moved = Corner(resized, degrees, right: false, bottom: false);

        Assert.Equal(anchor.X, moved.X, 6);
        Assert.Equal(anchor.Y, moved.Y, 6);
    }

    /// <summary>
    /// And the corner being dragged ends up under the pointer, or the
    /// region does not follow the hand.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(-30)]
    public void The_dragged_corner_follows_the_pointer(double degrees)
    {
        var pointer = Grow(Start, degrees, 40);

        var resized = ImageViewer.ResizeTurned(
            Start, ImageViewer.Edge.Right | ImageViewer.Edge.Bottom, degrees, pointer);

        var dragged = Corner(resized, degrees, right: true, bottom: true);

        Assert.Equal(pointer.X, dragged.X, 6);
        Assert.Equal(pointer.Y, dragged.Y, 6);
    }

    /// <summary>One edge alone moves only that edge; the other three stay.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    public void Dragging_one_edge_leaves_the_other_three(double degrees)
    {
        var topLeft = Corner(Start, degrees, right: false, bottom: false);
        var topRight = Corner(Start, degrees, right: true, bottom: false);

        var pointer = Grow(Start, degrees, 60);

        var resized = ImageViewer.ResizeTurned(
            Start, ImageViewer.Edge.Bottom, degrees, pointer);

        Assert.Equal(Start.Width, resized.Width, 6);

        Assert.Equal(topLeft.X, Corner(resized, degrees, false, false).X, 6);
        Assert.Equal(topLeft.Y, Corner(resized, degrees, false, false).Y, 6);
        Assert.Equal(topRight.X, Corner(resized, degrees, true, false).X, 6);
        Assert.Equal(topRight.Y, Corner(resized, degrees, true, false).Y, 6);
    }

    /// <summary>
    /// Dragged past its opposite, an edge flips the region rather than
    /// giving it a negative extent.
    /// </summary>
    [Fact]
    public void Dragging_an_edge_past_its_opposite_flips_rather_than_inverts()
    {
        var resized = ImageViewer.ResizeTurned(
            Start, ImageViewer.Edge.Right, 30, new Point(-200, -200));

        Assert.True(resized.Width > 0);
        Assert.True(resized.Height > 0);
    }

    /// <summary>Upright, it must behave exactly as it always did.</summary>
    [Fact]
    public void An_upright_region_resizes_in_plain_coordinates()
    {
        var resized = ImageViewer.ResizeTurned(
            Start, ImageViewer.Edge.Right | ImageViewer.Edge.Bottom, 0, new Point(150, 140));

        Assert.Equal(new Rect(80, 90, 70, 50), resized);
    }
}
