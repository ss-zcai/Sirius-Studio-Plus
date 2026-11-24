using Sirius.Core.Shapes;

namespace Sirius.Vision;

/// <summary>The sides of a rectangle, in the order they are given and kept.</summary>
public enum RectangleSide
{
    Top,
    Right,
    Bottom,
    Left,
}

/// <summary>A rectangle fitted to the edge points along its four sides.</summary>
/// <param name="Corners">Top left, top right, bottom right, bottom left - in the frame of the region it was looked for in.</param>
/// <param name="Error">Root-mean-square distance of the points from the side they were found on.</param>
/// <param name="Used">The points fitted on each side, after any rejection, by <see cref="RectangleSide"/>.</param>
public readonly record struct RectangleFitResult(
    IReadOnlyList<Point2> Corners,
    double Error,
    IReadOnlyList<IReadOnlyList<Point2>> Used)
{
    public bool Success => Corners.Count == 4 && !double.IsNaN(Error);

    /// <summary>The middle: where the diagonals cross.</summary>
    public Point2 Centre => Success
        ? Meet(Corners[0], Corners[2], Corners[1], Corners[3]) ?? Middle(Corners[0], Corners[2])
        : default;

    /// <summary>How long its top is, and its bottom: their mean, for four lines that may not be parallel.</summary>
    public double Width => Success ? (Corners[0].DistanceTo(Corners[1]) + Corners[3].DistanceTo(Corners[2])) / 2 : 0;

    /// <summary>How long its sides are: their mean.</summary>
    public double Height => Success ? (Corners[1].DistanceTo(Corners[2]) + Corners[0].DistanceTo(Corners[3])) / 2 : 0;

    /// <summary>The turn of its top, left to right, in degrees clockwise.</summary>
    public double AngleDegrees => Success
        ? Math.Atan2(Corners[1].Y - Corners[0].Y, Corners[1].X - Corners[0].X) * 180 / Math.PI
        : 0;

    /// <summary>
    /// Each corner's angle, in degrees - 90 for a true rectangle; what Four
    /// Lines says of a part, or a view, that is not square.
    /// </summary>
    public IReadOnlyList<double> CornerAngles()
    {
        if (!Success)
            return [];

        var angles = new double[4];

        for (var i = 0; i < 4; i++)
        {
            var at = Corners[i];
            var previous = Corners[(i + 3) % 4];
            var next = Corners[(i + 1) % 4];
            var (ax, ay) = (previous.X - at.X, previous.Y - at.Y);
            var (bx, by) = (next.X - at.X, next.Y - at.Y);
            var cosine = ((ax * bx) + (ay * by)) / (Math.Sqrt((ax * ax) + (ay * ay)) * Math.Sqrt((bx * bx) + (by * by)));
            angles[i] = Math.Acos(Math.Clamp(cosine, -1, 1)) * 180 / Math.PI;
        }

        return angles;
    }

    internal static Point2 Middle(Point2 a, Point2 b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    /// <summary>Where the line a1-a2 meets the line b1-b2, or null when they are parallel.</summary>
    internal static Point2? Meet(Point2 a1, Point2 a2, Point2 b1, Point2 b2)
    {
        var (ux, uy) = (a2.X - a1.X, a2.Y - a1.Y);
        var (wx, wy) = (b2.X - b1.X, b2.Y - b1.Y);
        var denominator = (ux * wy) - (uy * wx);

        if (Math.Abs(denominator) < 1e-12)
            return null;

        var t = (((b1.X - a1.X) * wy) - ((b1.Y - a1.Y) * wx)) / denominator;
        return new Point2(a1.X + (ux * t), a1.Y + (uy * t));
    }

    internal static RectangleFitResult Failed(IReadOnlyList<IReadOnlyList<Point2>> used) => new([], double.NaN, used);
}

/// <summary>
/// Fits a rectangle to points found along its four sides.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rectangle</b> fits all the points at once with the sides held parallel
/// and square: one direction for the whole rectangle, found by total least
/// squares over every side's points - the top and bottom as they are, the
/// left and right turned a quarter so they run the same way - and then
/// where each side lies along it. Every point helps fix the angle, so it is
/// steadier than four lines fitted apart, and its length and width are
/// measured between parallel sides.
/// </para>
/// <para>
/// <b>Square</b> adds that its length and width are equal, by moving the
/// four sides the least - weighted by how many points each has - that
/// makes them so. The angle is the rectangle's: a square constraint moves
/// the sides, not the turn.
/// </para>
/// <para>
/// <b>Four lines</b> fits each side alone, with <see cref="LineFit"/>, and
/// takes the corners where neighbouring sides meet - for a part that is
/// not truly square, or a camera looking at it from an angle, where the
/// corner angles are the point.
/// </para>
/// <para>
/// Points that stray - a caliper that crossed a chip or a scratch - are
/// dropped as <see cref="LineFit"/> drops them: those further from their
/// side than the mean by more than <c>sigma</c> spreads, and fitted again.
/// </para>
/// </remarks>
public static class RectangleFit
{
    /// <summary>The fewest points a side needs.</summary>
    public const int MinimumPerSide = LineFit.MinimumPoints;

    /// <param name="sides">Each side's points, by <see cref="RectangleSide"/>.</param>
    /// <param name="angleDegrees">The turn of the region the sides were looked for in: the fit's top runs the same way.</param>
    public static RectangleFitResult Fit(
        IReadOnlyList<IReadOnlyList<Point2>> sides, double angleDegrees, bool square = false)
    {
        if (sides.Count != 4 || sides.Any(side => side.Count < MinimumPerSide))
            return RectangleFitResult.Failed(sides);

        var means = sides.Select(Mean).ToArray();

        // One scatter for the four sides: top and bottom as they lie, left
        // and right turned a quarter back, so all four run along the top.
        double xx = 0, yy = 0, xy = 0;

        for (var s = 0; s < 4; s++)
        {
            var turned = s is (int)RectangleSide.Right or (int)RectangleSide.Left;

            foreach (var point in sides[s])
            {
                var (dx, dy) = (point.X - means[s].X, point.Y - means[s].Y);

                if (turned)
                    (dx, dy) = (dy, -dx);

                xx += dx * dx;
                yy += dy * dy;
                xy += dx * dy;
            }
        }

        var theta = 0.5 * Math.Atan2(2 * xy, xx - yy);
        var (ux, uy) = (Math.Cos(theta), Math.Sin(theta));

        // Either way along the line is the same line; the region's way round
        // decides which side is the top.
        var radians = angleDegrees * Math.PI / 180;
        if ((ux * Math.Cos(radians)) + (uy * Math.Sin(radians)) < 0)
            (ux, uy) = (-ux, -uy);

        var (nx, ny) = (-uy, ux);

        // Where each side lies: across the rectangle for the top and bottom,
        // along it for the left and right.
        var offsets = new double[4];
        for (var s = 0; s < 4; s++)
        {
            offsets[s] = s is (int)RectangleSide.Top or (int)RectangleSide.Bottom
                ? (means[s].X * nx) + (means[s].Y * ny)
                : (means[s].X * ux) + (means[s].Y * uy);
        }

        if (square)
        {
            // Width = height, moving each side the least for its points:
            // (bottom - top) - (right - left) = 0, by a Lagrange multiplier.
            double[] signs = [-1, -1, 1, 1];
            var gap = 0.0;
            var weight = 0.0;

            for (var s = 0; s < 4; s++)
            {
                gap += signs[s] * offsets[s];
                weight += 1.0 / sides[s].Count;
            }

            var lambda = gap / weight;

            for (var s = 0; s < 4; s++)
                offsets[s] -= lambda * signs[s] / sides[s].Count;
        }

        var (top, right, bottom, left) = (offsets[0], offsets[1], offsets[2], offsets[3]);

        // Sides found the wrong way round - the top below the bottom - are
        // not a rectangle, whatever the arithmetic says.
        if (right - left <= 0 || bottom - top <= 0)
            return RectangleFitResult.Failed(sides);

        Point2 At(double along, double across) => new((along * ux) + (across * nx), (along * uy) + (across * ny));

        var corners = new[] { At(left, top), At(right, top), At(right, bottom), At(left, bottom) };

        var sum = 0.0;
        var count = 0;

        for (var s = 0; s < 4; s++)
        {
            foreach (var point in sides[s])
            {
                var residual = Residual(point, (RectangleSide)s, offsets[s], ux, uy, nx, ny);
                sum += residual * residual;
                count++;
            }
        }

        return new RectangleFitResult(corners, Math.Sqrt(sum / count), sides);
    }

    /// <summary>Fits, drops the points that stray furthest from their side, and fits again.</summary>
    public static RectangleFitResult FitRobust(
        IReadOnlyList<IReadOnlyList<Point2>> sides,
        double angleDegrees,
        bool square = false,
        double sigma = 2.5,
        int passes = 3)
    {
        var result = Fit(sides, angleDegrees, square);

        if (!result.Success || sigma <= 0)
            return result;

        var kept = sides;

        for (var pass = 0; pass < passes; pass++)
        {
            var deviations = Deviations(kept, result);
            var all = deviations.SelectMany(d => d).ToArray();
            var mean = all.Average();
            var spread = Math.Sqrt(all.Average(d => (d - mean) * (d - mean)));

            if (spread < 1e-9)
                break;

            var limit = mean + (sigma * spread);
            var survivors = kept
                .Select((side, s) => (IReadOnlyList<Point2>)side.Where((_, i) => deviations[s][i] <= limit).ToArray())
                .ToArray();

            if (survivors.Sum(side => side.Count) == kept.Sum(side => side.Count) ||
                survivors.Any(side => side.Count < MinimumPerSide))
            {
                break;
            }

            var next = Fit(survivors, angleDegrees, square);

            if (!next.Success)
                break;

            kept = survivors;
            result = next;
        }

        return result;
    }

    /// <summary>Each side fitted alone, and the corners where neighbouring sides meet.</summary>
    public static RectangleFitResult FitFourLines(
        IReadOnlyList<IReadOnlyList<Point2>> sides, double sigma = 2.5)
    {
        if (sides.Count != 4 || sides.Any(side => side.Count < MinimumPerSide))
            return RectangleFitResult.Failed(sides);

        var lines = sides.Select(side => LineFit.FitRobust(side, sigma)).ToArray();

        if (lines.Any(line => !line.Success))
            return RectangleFitResult.Failed(sides);

        Point2? Corner(RectangleSide a, RectangleSide b)
        {
            var (first, second) = (lines[(int)a], lines[(int)b]);

            return RectangleFitResult.Meet(
                first.Point,
                new Point2(first.Point.X + first.DirectionX, first.Point.Y + first.DirectionY),
                second.Point,
                new Point2(second.Point.X + second.DirectionX, second.Point.Y + second.DirectionY));
        }

        Point2?[] corners =
        [
            Corner(RectangleSide.Top, RectangleSide.Left),
            Corner(RectangleSide.Top, RectangleSide.Right),
            Corner(RectangleSide.Bottom, RectangleSide.Right),
            Corner(RectangleSide.Bottom, RectangleSide.Left),
        ];

        if (corners.Any(corner => corner is null))
            return RectangleFitResult.Failed(sides);

        var used = lines.Select(line => line.Used).ToArray();
        var count = used.Sum(side => side.Count);
        var sum = lines.Sum(line => line.Error * line.Error * line.Used.Count);

        return new RectangleFitResult(corners.Select(c => c!.Value).ToArray(), Math.Sqrt(sum / count), used);
    }

    /// <summary>Each point's distance from the side of <paramref name="fit"/> it was found on.</summary>
    public static double[][] Deviations(IReadOnlyList<IReadOnlyList<Point2>> sides, RectangleFitResult fit)
    {
        var corners = fit.Corners;
        (Point2 From, Point2 To)[] lines =
        [
            (corners[0], corners[1]),
            (corners[1], corners[2]),
            (corners[2], corners[3]),
            (corners[3], corners[0]),
        ];

        return sides
            .Select((side, s) => side.Select(point => DistanceToLine(point, lines[s].From, lines[s].To)).ToArray())
            .ToArray();
    }

    private static double DistanceToLine(Point2 point, Point2 a, Point2 b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var length = Math.Sqrt((dx * dx) + (dy * dy));

        return length < 1e-12
            ? point.DistanceTo(a)
            : Math.Abs(((point.X - a.X) * dy) - ((point.Y - a.Y) * dx)) / length;
    }

    private static double Residual(Point2 point, RectangleSide side, double offset, double ux, double uy, double nx, double ny) =>
        side is RectangleSide.Top or RectangleSide.Bottom
            ? (point.X * nx) + (point.Y * ny) - offset
            : (point.X * ux) + (point.Y * uy) - offset;

    private static Point2 Mean(IReadOnlyList<Point2> points) =>
        new(points.Average(p => p.X), points.Average(p => p.Y));
}
