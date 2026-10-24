using System.Windows;
using System.Windows.Media;
using Sirius.Studio.App.Controls;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The viewer's measurement geometry.
/// </summary>
/// <remarks>
/// Pure arithmetic over placed points, so it is worth pinning: a sign error
/// or a swapped argument produces a plausible-looking number rather than a
/// visible failure.
/// </remarks>
public class MeasurementTests
{
    private static string Measure(ViewerTool tool, params (double X, double Y)[] points) =>
        Measurement.Describe(tool, points.Select(p => new Point(p.X, p.Y)).ToArray());

    private static Measurement Place(ViewerTool tool, params (double X, double Y)[] points) =>
        new(tool, points.Select(p => new Point(p.X, p.Y)), Colors.White);

    // ---- readings --------------------------------------------------------

    [Fact]
    public void Point_to_point_measures_the_straight_line_distance()
    {
        // 3-4-5 triangle.
        Assert.Contains("5", Measure(ViewerTool.PointToPoint, (0, 0), (3, 4)));
    }

    [Fact]
    public void Point_to_point_of_coincident_points_is_zero()
    {
        Assert.Contains("0", Measure(ViewerTool.PointToPoint, (7, 7), (7, 7)));
    }

    /// <summary>
    /// Perpendicular distance, not distance to the nearest endpoint - the two
    /// differ whenever the foot falls outside the placed span.
    /// </summary>
    [Fact]
    public void Point_to_line_measures_perpendicular_distance()
    {
        // Horizontal line along y = 0; the point sits 5 above it.
        Assert.Contains("5", Measure(ViewerTool.PointToLine, (50, 5), (0, 0), (10, 0)));
    }

    [Fact]
    public void Point_to_line_handles_a_line_of_zero_length()
    {
        // Both line points identical: falls back to point-to-point rather than
        // dividing by zero.
        Assert.Contains("5", Measure(ViewerTool.PointToLine, (3, 4), (0, 0), (0, 0)));
    }

    /// <summary>
    /// The parallel tool works like a caliper: the drag is taken across the gap,
    /// so its length <i>is</i> the separation.
    /// </summary>
    [Fact]
    public void Parallel_lines_measure_the_span_of_the_caliper()
    {
        Assert.Contains("12", Measure(ViewerTool.LineToLineParallel, (5, 0), (5, 12)));
    }

    [Fact]
    public void Angled_lines_measure_their_closest_approach()
    {
        // y = 0 and y = 12, so the nearest points are 12 apart.
        var reading = Measure(ViewerTool.LineToLineAngled, (0, 0), (10, 0), (0, 12), (10, 12));

        Assert.Contains("12", reading);
        Assert.Contains("0", reading); // parallel, so zero degrees apart
    }

    /// <summary>
    /// A separation taken off two lines that are not parallel is meaningless
    /// without knowing by how much they diverge, so the angle is reported
    /// alongside it.
    /// </summary>
    [Fact]
    public void Angled_lines_report_how_far_from_parallel_they_are()
    {
        // Second line at 45 degrees to the first.
        var reading = Measure(ViewerTool.LineToLineAngled, (0, 0), (10, 0), (0, 10), (10, 20));

        Assert.Contains("45", reading);
    }

    /// <summary>Lines that cross are zero apart, not "nearest endpoints apart".</summary>
    [Fact]
    public void Crossing_lines_are_zero_apart()
    {
        Assert.Equal(
            0,
            Measurement.SegmentDistance(
                new Point(0, 0), new Point(10, 0), new Point(5, -5), new Point(5, 5)));
    }

    [Theory]
    [InlineData(0, 10, 10, 0, 90)]   // right angle
    [InlineData(10, 0, -10, 0, 180)] // straight through the vertex
    [InlineData(10, 0, 10, 0, 0)]    // both arms the same way
    public void Angle_measures_between_the_two_arms(
        double ax, double ay, double bx, double by, double expected)
    {
        var reading = Measure(ViewerTool.Angle, (0, 0), (ax, ay), (bx, by));

        Assert.Contains(expected.ToString("0.##"), reading);
    }

    [Fact]
    public void Circle_from_a_centre_takes_the_radius_from_the_drag()
    {
        var reading = Measure(ViewerTool.CircleCentre, (100, 100), (100, 107));

        Assert.Contains("R 7", reading);
        Assert.Contains("14", reading); // diameter
    }

    [Fact]
    public void Circle_fits_three_points_on_its_circumference()
    {
        var circle = Measurement.Circumcircle(new Point(5, 0), new Point(0, 5), new Point(-5, 0));

        Assert.NotNull(circle);
        Assert.Equal(0, circle!.Value.Centre.X, 6);
        Assert.Equal(0, circle.Value.Centre.Y, 6);
        Assert.Equal(5, circle.Value.Radius, 6);
    }

    [Fact]
    public void Circle_reports_radius_and_diameter()
    {
        var reading = Measure(ViewerTool.CircleThreePoint, (5, 0), (0, 5), (-5, 0));

        Assert.Contains("R 5", reading);
        Assert.Contains("10", reading);
    }

    /// <summary>
    /// Three points in a line have no circle through them. Saying so beats
    /// reporting an enormous radius from a near-zero determinant.
    /// </summary>
    [Fact]
    public void Collinear_points_are_reported_rather_than_fitted()
    {
        Assert.Null(Measurement.Circumcircle(new Point(0, 0), new Point(5, 5), new Point(10, 10)));

        Assert.Contains("no circle", Measure(ViewerTool.CircleThreePoint, (0, 0), (5, 5), (10, 10)));
    }

    [Fact]
    public void The_pointer_tool_measures_nothing()
    {
        Assert.Equal(string.Empty, Measure(ViewerTool.None, (0, 0), (1, 1)));
    }

    /// <summary>
    /// A half-placed measurement reports nothing rather than a reading taken
    /// from the points that happen to be there.
    /// </summary>
    [Fact]
    public void An_incomplete_measurement_has_no_reading()
    {
        Assert.Equal(string.Empty, Measure(ViewerTool.LineToLineAngled, (0, 0), (10, 0)));
        Assert.Equal(string.Empty, Measure(ViewerTool.Angle, (0, 0), (10, 0)));
    }

    // ---- gesture tables --------------------------------------------------

    /// <summary>
    /// Two tables describe each tool - the gestures it collects and the points
    /// it needs - and the capture loop reads both. They have to agree, or a
    /// measurement commits early or never.
    /// </summary>
    [Theory]
    [InlineData(ViewerTool.PointToPoint, 2)]
    [InlineData(ViewerTool.PointToLine, 3)]
    [InlineData(ViewerTool.LineToLineParallel, 2)]
    [InlineData(ViewerTool.LineToLineAngled, 4)]
    [InlineData(ViewerTool.Angle, 3)]
    [InlineData(ViewerTool.CircleCentre, 2)]
    [InlineData(ViewerTool.CircleThreePoint, 3)]
    [InlineData(ViewerTool.LineProfile, 2)]
    public void Each_tool_collects_the_points_its_gestures_add_up_to(ViewerTool tool, int expected)
    {
        var fromGestures = Measurement.GesturesFor(tool)
            .Sum(gesture => gesture == GestureKind.Drag ? 2 : 1);

        Assert.Equal(expected, Measurement.PointsRequired(tool));
        Assert.Equal(expected, fromGestures);
    }

    [Fact]
    public void The_pointer_collects_nothing()
    {
        Assert.Empty(Measurement.GesturesFor(ViewerTool.None));
        Assert.Equal(0, Measurement.PointsRequired(ViewerTool.None));
    }

    /// <summary>
    /// Every tool needs a name for the toolbar and the prompt, and they must be
    /// distinguishable - two tools reading "Circle" would be a coin toss.
    /// </summary>
    [Fact]
    public void Every_tool_has_a_distinct_name()
    {
        var names = Enum.GetValues<ViewerTool>()
            .Select(Measurement.DisplayName)
            .ToList();

        Assert.DoesNotContain(string.Empty, names);
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    // ---- picking ---------------------------------------------------------

    /// <summary>
    /// Selection is by proximity to what was drawn, so the distance has to be
    /// measured to the geometry rather than to the placed points.
    /// </summary>
    /// <summary>
    /// A profile line is picked like any other span. It reports a curve rather
    /// than a number, but it is placed, selected and deleted identically.
    /// </summary>
    [Fact]
    public void A_profile_line_is_picked_by_its_line()
    {
        var measurement = Place(ViewerTool.LineProfile, (0, 0), (100, 0));

        Assert.Equal(2, measurement.DistanceTo(new Point(50, 2)), 6);
        Assert.Contains("100", measurement.Text);
    }

    [Fact]
    public void A_point_on_the_line_picks_a_point_to_point()
    {
        var measurement = Place(ViewerTool.PointToPoint, (0, 0), (100, 0));

        // Mid-span, 2 away: nowhere near either end, but on the line.
        Assert.Equal(2, measurement.DistanceTo(new Point(50, 2)), 6);
    }

    [Fact]
    public void Picking_does_not_run_off_the_end_of_a_line()
    {
        var measurement = Place(ViewerTool.PointToPoint, (0, 0), (10, 0));

        // Far off the end: distance to the segment, not to its extension, so
        // clicking along the line beyond its end does not select it.
        Assert.Equal(90, measurement.DistanceTo(new Point(100, 0)), 6);
    }

    [Fact]
    public void A_circle_is_picked_by_its_rim_not_its_middle()
    {
        var measurement = Place(ViewerTool.CircleCentre, (0, 0), (10, 0));

        Assert.Equal(0, measurement.DistanceTo(new Point(0, 10)), 6); // on the rim
        Assert.Equal(10, measurement.DistanceTo(new Point(0, 0)), 6); // at the centre
    }

    [Fact]
    public void A_half_placed_measurement_cannot_be_picked()
    {
        var measurement = Place(ViewerTool.LineToLineAngled, (0, 0), (10, 0));

        Assert.Equal(double.MaxValue, measurement.DistanceTo(new Point(5, 0)));
    }

    // ---- editing ---------------------------------------------------------

    /// <summary>
    /// Dragging a handle moves a point in place, so the reading and the label
    /// have to be derived on read rather than captured when it was placed.
    /// </summary>
    [Fact]
    public void Moving_a_point_changes_the_reading()
    {
        var measurement = Place(ViewerTool.PointToPoint, (0, 0), (3, 4));
        Assert.Contains("5", measurement.Text);

        measurement.Points[1] = new Point(6, 8);

        Assert.Contains("10", measurement.Text);
        Assert.Equal(3, measurement.LabelAnchor.X, 6);
    }

    [Fact]
    public void Moving_a_point_can_make_a_circle_unfittable()
    {
        var measurement = Place(ViewerTool.CircleThreePoint, (5, 0), (0, 5), (-5, 0));
        Assert.Contains("R 5", measurement.Text);

        // Dragged onto the line through the other two.
        measurement.Points[1] = new Point(0, 0);

        Assert.Contains("no circle", measurement.Text);
    }

    // ---- labels ----------------------------------------------------------

    /// <summary>
    /// The label is drawn on the measurement, so its anchor has to sit on the
    /// geometry rather than at the origin or the first click.
    /// </summary>
    [Fact]
    public void A_distance_label_sits_at_the_middle_of_the_span()
    {
        var anchor = Place(ViewerTool.PointToPoint, (0, 0), (100, 50)).LabelAnchor;

        Assert.Equal(50, anchor.X, 6);
        Assert.Equal(25, anchor.Y, 6);
    }

    [Fact]
    public void A_circle_label_sits_at_the_centre()
    {
        var anchor = Place(ViewerTool.CircleThreePoint, (5, 0), (0, 5), (-5, 0)).LabelAnchor;

        Assert.Equal(0, anchor.X, 6);
        Assert.Equal(0, anchor.Y, 6);
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>
    /// The closest-approach pair is what the dashed connector is drawn between,
    /// so it has to land on both segments.
    /// </summary>
    [Fact]
    public void Closest_approach_lands_on_both_lines()
    {
        var (onFirst, onSecond) = Measurement.ClosestPointsBetween(
            new Point(0, 0), new Point(10, 0), new Point(3, 8), new Point(7, 8));

        Assert.Equal(0, onFirst.Y, 6);
        Assert.Equal(8, onSecond.Y, 6);
        Assert.Equal(onFirst.X, onSecond.X, 6);
    }

    [Fact]
    public void A_normal_is_perpendicular_to_the_span_and_unit_length()
    {
        var normal = Measurement.Normal(new Point(0, 0), new Point(10, 0));

        Assert.Equal(1, normal.Length, 6);
        Assert.Equal(0, (normal.X * 10) + (normal.Y * 0), 6); // dot product with the span
    }

    [Fact]
    public void A_degenerate_span_has_no_normal()
    {
        Assert.Equal(0, Measurement.Normal(new Point(4, 4), new Point(4, 4)).Length);
    }
}
