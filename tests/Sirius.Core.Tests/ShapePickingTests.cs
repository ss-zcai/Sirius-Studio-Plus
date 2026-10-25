using Sirius.Core.Shapes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Picking a shape by its outline rather than by its bounding box.
/// </summary>
/// <remarks>
/// <para>
/// The box was right for a small compact blob and wrong for everything
/// else. A fitted circle three thousand pixels across claimed every click
/// inside it - the whole of the part it was measured on, and any
/// measurement or annotation sitting there. The shapes are drawn as
/// outlines, so the outline is what should be clickable.
/// </para>
/// <para>
/// Distance rather than a boolean, so the caller can pick the nearest of
/// several overlapping shapes rather than the first one it tests.
/// </para>
/// </remarks>
public class ShapePickingTests
{
    private const double Tolerance = 1e-6;

    // ---- the case that prompted it -------------------------------------------

    /// <summary>
    /// The middle of a large circle is far from it. This is the bug: it
    /// used to be a hit, because the centre is inside the bounding box.
    /// </summary>
    [Fact]
    public void The_middle_of_a_circle_is_not_on_it()
    {
        var circle = new CircleShape(new Point2(1000, 1000), 900);

        Assert.Equal(900, ShapeHitTest.DistanceToOutline(circle, new Point2(1000, 1000)), 6);
    }

    [Fact]
    public void The_edge_of_a_circle_is_on_it()
    {
        var circle = new CircleShape(new Point2(1000, 1000), 900);

        Assert.Equal(0, ShapeHitTest.DistanceToOutline(circle, new Point2(1900, 1000)), 6);
        Assert.Equal(2, ShapeHitTest.DistanceToOutline(circle, new Point2(1902, 1000)), 6);
        Assert.Equal(3, ShapeHitTest.DistanceToOutline(circle, new Point2(1897, 1000)), 6);
    }

    /// <summary>
    /// A small blob is still easy to hit: every point of it is within a
    /// few pixels of its own outline, so nothing was lost for the case the
    /// bounding box was right for.
    /// </summary>
    [Fact]
    public void A_small_blob_is_still_easy_to_hit()
    {
        var blob = new CircleShape(new Point2(50, 50), 3);

        Assert.True(ShapeHitTest.DistanceToOutline(blob, new Point2(50, 50)) <= 3);
    }

    // ---- each kind of shape ---------------------------------------------------

    [Fact]
    public void A_point_measures_to_itself()
    {
        var point = new PointShape(new Point2(10, 20));

        Assert.Equal(0, ShapeHitTest.DistanceToOutline(point, new Point2(10, 20)), 6);
        Assert.Equal(5, ShapeHitTest.DistanceToOutline(point, new Point2(10, 25)), 6);
    }

    /// <summary>
    /// To the segment, not to the infinite line it lies on - a point well
    /// past the end of a line is not on the line.
    /// </summary>
    [Fact]
    public void A_line_is_a_segment()
    {
        var line = new LineShape(new Point2(0, 0), new Point2(100, 0));

        Assert.Equal(0, ShapeHitTest.DistanceToOutline(line, new Point2(50, 0)), 6);
        Assert.Equal(4, ShapeHitTest.DistanceToOutline(line, new Point2(50, 4)), 6);

        // 40 past the end, not 0.
        Assert.Equal(40, ShapeHitTest.DistanceToOutline(line, new Point2(140, 0)), 6);
    }

    [Fact]
    public void A_rectangle_is_its_four_sides()
    {
        var rect = new RectShape(new Bounds(0, 0, 100, 60));

        Assert.Equal(0, ShapeHitTest.DistanceToOutline(rect, new Point2(50, 0)), 6);
        Assert.Equal(0, ShapeHitTest.DistanceToOutline(rect, new Point2(100, 30)), 6);

        // The middle is 30 from the nearest side, not inside-and-therefore-hit.
        Assert.Equal(30, ShapeHitTest.DistanceToOutline(rect, new Point2(50, 30)), 6);
    }

    [Fact]
    public void A_turned_rectangle_is_measured_where_it_actually_is()
    {
        var rect = new OrientedRectShape(new Point2(0, 0), 200, 100, 90);

        // Turned a quarter turn, so the long side now runs vertically: the
        // point 50 out along X is on the edge.
        Assert.Equal(0, ShapeHitTest.DistanceToOutline(rect, new Point2(50, 0)), 6);
        Assert.Equal(50, ShapeHitTest.DistanceToOutline(rect, new Point2(0, 0)), 6);
    }

    [Fact]
    public void An_ellipse_is_its_outline()
    {
        var ellipse = new EllipseShape(new Point2(0, 0), 200, 100, 0);

        // Semi-axes of 100 and 50.
        Assert.True(ShapeHitTest.DistanceToOutline(ellipse, new Point2(100, 0)) < 0.1);
        Assert.True(ShapeHitTest.DistanceToOutline(ellipse, new Point2(0, 50)) < 0.1);

        // The centre is 50 from the nearest point of it.
        Assert.True(Math.Abs(ShapeHitTest.DistanceToOutline(ellipse, new Point2(0, 0)) - 50) < 0.1);
    }

    [Fact]
    public void A_polygon_is_its_edges()
    {
        var square = new PolygonShape(
        [
            new Point2(0, 0), new Point2(100, 0), new Point2(100, 100), new Point2(0, 100),
        ]);

        Assert.Equal(0, ShapeHitTest.DistanceToOutline(square, new Point2(50, 0)), 6);

        // Closed, so the last-to-first edge counts too.
        Assert.Equal(0, ShapeHitTest.DistanceToOutline(square, new Point2(0, 50)), 6);

        Assert.Equal(50, ShapeHitTest.DistanceToOutline(square, new Point2(50, 50)), 6);
    }

    /// <summary>
    /// An open polygon has no closing edge, so the gap between its ends is
    /// not part of it.
    /// </summary>
    [Fact]
    public void An_open_polygon_has_no_closing_edge()
    {
        var open = new PolygonShape(
            [new Point2(0, 0), new Point2(100, 0), new Point2(100, 100)],
            Closed: false);

        Assert.Equal(0, ShapeHitTest.DistanceToOutline(open, new Point2(50, 0)), 6);

        // Where a closing edge would have run, and does not.
        Assert.Equal(50, ShapeHitTest.DistanceToOutline(open, new Point2(0, 50)), 6);
    }

    // ---- what the picker does with the distances --------------------------------

    /// <summary>
    /// Two circles sharing a centre: a click near the inner one picks the
    /// inner one, even though both bounding boxes contain the point and
    /// the outer box is larger.
    /// </summary>
    [Fact]
    public void The_nearest_outline_wins()
    {
        var inner = new CircleShape(new Point2(0, 0), 100);
        var outer = new CircleShape(new Point2(0, 0), 400);

        var at = new Point2(102, 0);

        Assert.True(
            ShapeHitTest.DistanceToOutline(inner, at) <
            ShapeHitTest.DistanceToOutline(outer, at));
    }

    /// <summary>
    /// And a click in the middle of both is near neither, so nothing is
    /// picked and whatever is underneath stays reachable - which is the
    /// whole point.
    /// </summary>
    [Fact]
    public void A_click_in_the_middle_reaches_neither()
    {
        var inner = new CircleShape(new Point2(0, 0), 100);
        var outer = new CircleShape(new Point2(0, 0), 400);

        const double tolerance = 6;
        var at = new Point2(0, 0);

        Assert.True(ShapeHitTest.DistanceToOutline(inner, at) > tolerance);
        Assert.True(ShapeHitTest.DistanceToOutline(outer, at) > tolerance);
    }

    [Fact]
    public void A_shape_is_never_measured_as_negative()
    {
        Shape[] shapes =
        [
            new PointShape(new Point2(1, 1)),
            new LineShape(new Point2(0, 0), new Point2(10, 10)),
            new CircleShape(new Point2(0, 0), 5),
            new RectShape(new Bounds(0, 0, 10, 10)),
            new OrientedRectShape(new Point2(0, 0), 10, 5, 30),
            new EllipseShape(new Point2(0, 0), 10, 5, 15),
            new PolygonShape([new Point2(0, 0), new Point2(5, 0), new Point2(5, 5)]),
        ];

        foreach (var shape in shapes)
        {
            foreach (var at in new[] { new Point2(0, 0), new Point2(100, 100), new Point2(-7, 3) })
                Assert.True(ShapeHitTest.DistanceToOutline(shape, at) >= 0);
        }
    }

    [Fact]
    public void An_empty_polygon_is_unreachable_rather_than_a_crash()
    {
        var empty = new PolygonShape([]);

        Assert.True(ShapeHitTest.DistanceToOutline(empty, new Point2(0, 0)) > 1e9);
    }
}
