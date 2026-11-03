using Sirius.Core.Shapes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// HALCON's circularity and roundness, against shapes whose answers are
/// known exactly - and against the one that started it: an outline a little
/// out of round, which the 4 pi A / P squared circularity calls perfect.
/// </summary>
public class RadialMeasureTests
{
    private static Point2[] Ellipse(double a, double b, int corners = 720) =>
        Enumerable.Range(0, corners)
            .Select(i => new Point2(500 + (a * Math.Cos(2 * Math.PI * i / corners)), 500 + (b * Math.Sin(2 * Math.PI * i / corners))))
            .ToArray();

    private static double Area(IReadOnlyList<Point2> points)
    {
        var twice = 0.0;

        for (var i = 0; i < points.Count; i++)
            twice += (points[i].X * points[(i + 1) % points.Count].Y) - (points[(i + 1) % points.Count].X * points[i].Y);

        return Math.Abs(twice) / 2;
    }

    [Fact]
    public void A_circle_reads_one_both_ways()
    {
        var circle = Ellipse(300, 300);
        var (radial, roundness) = RadialMeasures.Of(circle, new Point2(500, 500), Area(circle));

        Assert.Equal(1, radial, 3);
        Assert.Equal(1, roundness, 4);
    }

    /// <summary>
    /// Two per cent long: the isoperimetric circularity reads 0.9999, and
    /// this reads b / a, as a circle through the ends of the long axis says.
    /// </summary>
    [Fact]
    public void An_oval_two_per_cent_long_is_not_round()
    {
        var oval = Ellipse(306, 300);
        var area = Area(oval);
        var (radial, roundness) = RadialMeasures.Of(oval, new Point2(500, 500), area);

        Assert.Equal(300 / 306.0, radial, 3);
        Assert.InRange(roundness, 0.98, 0.995);

        var perimeter = oval.Select((p, i) => p.DistanceTo(oval[(i + 1) % oval.Length])).Sum();
        Assert.InRange(4 * Math.PI * area / (perimeter * perimeter), 0.9995, 1);
    }

    /// <summary>A square: area s squared over pi (s / root 2) squared, which is 2 / pi.</summary>
    [Fact]
    public void A_square_reads_two_over_pi()
    {
        Point2[] square = [new(0, 0), new(100, 0), new(100, 100), new(0, 100)];
        var (radial, roundness) = RadialMeasures.Of(square, new Point2(50, 50), 10_000);

        Assert.Equal(2 / Math.PI, radial, 6);

        // Walked a pixel at a time, the sides count as well as the corners:
        // from 50 at their middles to 70.7 at the corners. Worked out along
        // a side, the mean is 57.39 and the spread 6.31: 1 - 6.31 / 57.39.
        Assert.Equal(0.8901, roundness, 3);
    }

    [Fact]
    public void The_centroid_is_the_areas_not_the_corners_mean()
    {
        // A square with three extra corners crowded along its top edge.
        Point2[] square = [new(0, 0), new(10, 0), new(20, 0), new(30, 0), new(100, 0), new(100, 100), new(0, 100)];

        Assert.Equal(new Point2(50, 50), RadialMeasures.AreaCentroid(square));
    }
}
