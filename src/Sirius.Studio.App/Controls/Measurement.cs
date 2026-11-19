using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Sirius.Studio.App.Controls;

/// <summary>What a click on the image does.</summary>
public enum ViewerTool
{
    /// <summary>Pan, select measurements, and edit the active region.</summary>
    None,

    /// <summary>Distance between two points.</summary>
    PointToPoint,

    /// <summary>Perpendicular distance from a point to a line.</summary>
    PointToLine,

    /// <summary>Separation of two parallel lines, measured like a caliper.</summary>
    LineToLineParallel,

    /// <summary>Closest approach of two independently drawn lines.</summary>
    LineToLineAngled,

    /// <summary>Angle at a vertex, between two arms.</summary>
    Angle,

    /// <summary>Circle from a centre and a radius.</summary>
    CircleCentre,

    /// <summary>Circle through three points on its circumference.</summary>
    CircleThreePoint,

    /// <summary>
    /// Draws an annular search band: centre, then the edge, then the
    /// width either side of it.
    /// </summary>
    /// <remarks>
    /// Not a measurement - it writes a parameter rather than leaving an
    /// annotation - but it shares the tool strip and the click handling,
    /// because to the operator it is the same kind of act: pick the tool,
    /// click on the image, something appears.
    /// </remarks>
    Ring,

    /// <summary>
    /// Draws a search path corner by corner: a double-click finishes it,
    /// a click on the first corner closes it. Like the band, it writes a
    /// parameter rather than leaving an annotation.
    /// </summary>
    Path,

    /// <summary>
    /// Intensity sampled along a line, charted beneath the image.
    /// </summary>
    /// <remarks>
    /// Not a measurement in the sense the others are - it reports a curve
    /// rather than a number - but it is placed, selected, recoloured and
    /// deleted identically, so it belongs with them rather than in a mode of
    /// its own.
    /// </remarks>
    LineProfile,

    /// <summary>
    /// A rectangle, turned to lie along a part: drag along one side, then
    /// click across for the other. Its length and width are the reading;
    /// its crop can be saved, so it is also the viewer's ROI.
    /// </summary>
    /// <remarks>
    /// Kept as three corners - top left, top right, bottom right - with
    /// the top meaning the side its knob is on, which is the side a saved
    /// crop is turned to have at the top. The fourth corner follows.
    /// </remarks>
    Rectangle,
}

/// <summary>How a tool collects each of its points.</summary>
public enum GestureKind
{
    /// <summary>One point, placed on click.</summary>
    Click,

    /// <summary>Two points: press places the first, release the second.</summary>
    Drag,
}

/// <summary>
/// A measurement placed on the image.
/// </summary>
/// <remarks>
/// Measurements persist and can be selected, so several can be compared on one
/// image. They do not take part in evaluation - drawing a line says nothing
/// about what the graph computes - but they are saved with the recipe, as
/// <see cref="Sirius.Core.Graph.MeasurementRecord"/> on the node whose viewer
/// they were drawn on.
///
/// All coordinates are image pixels, like the region of interest, so a
/// measurement stays put under pan and zoom.
/// </remarks>
public sealed class Measurement
{
    public Measurement(ViewerTool tool, IEnumerable<Point> points, Color colour)
    {
        Tool = tool;
        Points = points.ToList();
        Colour = colour;
    }

    public ViewerTool Tool { get; }

    public List<Point> Points { get; }

    public Color Colour { get; set; }

    public bool IsSelected { get; set; }

    /// <summary>
    /// What this measurement is called, for the ones that need naming.
    /// </summary>
    /// <remarks>
    /// A profile line is identified by its name rather than by its reading: the
    /// number that matters is the chart, which lives in a window of its own, and
    /// the name is what ties the two together. Other measurements leave this
    /// empty and are labelled with their reading instead.
    /// </remarks>
    public string Name { get; set; } = string.Empty;

    /// <summary>The gestures each tool needs, in order.</summary>
    public static GestureKind[] GesturesFor(ViewerTool tool) => tool switch
    {
        // Press to place the first point, drag to see the line, release to fix
        // the second - one motion rather than two separate clicks.
        ViewerTool.PointToPoint => new[] { GestureKind.Drag },

        ViewerTool.LineProfile => new[] { GestureKind.Drag },

        // The point first, then the line drawn the same way as a P2P.
        ViewerTool.PointToLine => new[] { GestureKind.Click, GestureKind.Drag },

        ViewerTool.LineToLineParallel => new[] { GestureKind.Drag },

        ViewerTool.LineToLineAngled => new[] { GestureKind.Drag, GestureKind.Drag },

        ViewerTool.Angle => new[] { GestureKind.Click, GestureKind.Click, GestureKind.Click },

        ViewerTool.CircleCentre => new[] { GestureKind.Drag },

        ViewerTool.CircleThreePoint =>
            new[] { GestureKind.Click, GestureKind.Click, GestureKind.Click },

        // A side, drawn like a point-to-point; then how far across.
        ViewerTool.Rectangle => new[] { GestureKind.Drag, GestureKind.Click },

        _ => Array.Empty<GestureKind>(),
    };

    /// <summary>Total points a tool collects before it can report.</summary>
    public static int PointsRequired(ViewerTool tool) =>
        GesturesFor(tool).Sum(g => g == GestureKind.Drag ? 2 : 1);

    public static string DisplayName(ViewerTool tool) => tool switch
    {
        ViewerTool.PointToPoint => "Point to point",
        ViewerTool.PointToLine => "Point to line",
        ViewerTool.LineToLineParallel => "Parallel lines",
        ViewerTool.LineToLineAngled => "Line to line",
        ViewerTool.Angle => "Angle",
        ViewerTool.CircleCentre => "Circle (centre)",
        ViewerTool.CircleThreePoint => "Circle (3 point)",
        ViewerTool.LineProfile => "Line profile",
        ViewerTool.Ring => "Search band",
        ViewerTool.Path => "Search path",
        ViewerTool.Rectangle => "Rectangle",
        _ => "Pointer",
    };

    /// <summary>The reading, as shown on the image and in the status bar.</summary>
    public string Text => Describe(Tool, Points);

    /// <summary>
    /// Computes the reading for a set of points.
    /// </summary>
    /// <remarks>
    /// Results are in pixels. Converting to millimetres needs a calibration
    /// factor, which belongs to the recipe rather than the viewer.
    /// </remarks>
    public static string Describe(ViewerTool tool, IReadOnlyList<Point> points)
    {
        if (points.Count < PointsRequired(tool) || points.Count == 0)
            return string.Empty;

        return tool switch
        {
            ViewerTool.PointToPoint =>
                Format(Distance(points[0], points[1])),

            // The reading is the chart; the length is what is worth saying in
            // a line of text beside it.
            ViewerTool.LineProfile =>
                Format(Distance(points[0], points[1])),

            ViewerTool.PointToLine =>
                Format(PointToLineDistance(points[0], points[1], points[2])),

            // The drag is the caliper: its length is the separation, and the
            // reference lines show what it was taken across.
            ViewerTool.LineToLineParallel =>
                Format(Distance(points[0], points[1])),

            // Two lines drawn independently will rarely be parallel, so the
            // reading is their closest approach, with the angle alongside -
            // a separation between non-parallel lines means little without it.
            ViewerTool.LineToLineAngled =>
                $"{Format(SegmentDistance(points[0], points[1], points[2], points[3]))}  " +
                $"∠ {LineAngle(points[0], points[1], points[2], points[3]):0.##}°",

            ViewerTool.Angle =>
                $"{AngleAt(points[0], points[1], points[2]):0.##}°",

            ViewerTool.CircleCentre =>
                RadiusReading(Distance(points[0], points[1])),

            ViewerTool.CircleThreePoint =>
                Circumcircle(points[0], points[1], points[2]) is { } circle
                    ? RadiusReading(circle.Radius)
                    : "no circle (points are in line)",

            ViewerTool.Rectangle => RectangleReading(points),

            _ => string.Empty,
        };
    }

    private static string Format(double pixels) =>
        string.Create(CultureInfo.CurrentCulture, $"{pixels:0.##} px");

    private static string RadiusReading(double radius) =>
        string.Create(CultureInfo.CurrentCulture, $"R {radius:0.##}  ⌀ {radius * 2:0.##} px");

    /// <summary>
    /// "L 245.3 × W 120.1 px", the longer side first whichever way it was
    /// drawn, and the angle of its top when it is turned.
    /// </summary>
    private static string RectangleReading(IReadOnlyList<Point> points)
    {
        var corners = RectangleCorners(points[0], points[1], points[2]);
        var top = Distance(corners[0], corners[1]);
        var side = Distance(corners[1], corners[2]);
        var reading = string.Create(
            CultureInfo.CurrentCulture,
            $"L {Math.Max(top, side):0.##} × W {Math.Min(top, side):0.##} px");

        var angle = RectangleAngle(corners);

        return Math.Abs(angle) < 0.005
            ? reading
            : string.Create(CultureInfo.CurrentCulture, $"{reading}  ∠ {angle:0.##}°");
    }

    /// <summary>Where the label sits, in image coordinates.</summary>
    public Point LabelAnchor
    {
        get
        {
            if (Points.Count == 0)
                return default;

            return Tool switch
            {
                ViewerTool.CircleCentre => Points[0],

                ViewerTool.CircleThreePoint =>
                    Circumcircle(Points[0], Points[1], Points[2])?.Centre ?? Points[0],

                ViewerTool.Angle => Points[0],

                ViewerTool.PointToLine => Midpoint(Points[0], FootOnLine(Points[0], Points[1], Points[2])),

                ViewerTool.Rectangle when Points.Count >= 3 =>
                    Midpoint(Points[0], RectangleCorners(Points[0], Points[1], Points[2])[2]),

                _ => Points.Count >= 2 ? Midpoint(Points[0], Points[1]) : Points[0],
            };
        }
    }

    // ---- geometry --------------------------------------------------------

    public static double Distance(Point a, Point b) => (b - a).Length;

    private static Point Midpoint(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    /// <summary>Perpendicular distance from <paramref name="p"/> to the line a-b.</summary>
    public static double PointToLineDistance(Point p, Point a, Point b)
    {
        var line = b - a;
        var length = line.Length;

        // A degenerate line collapses to a point, which is still a sensible
        // answer rather than a divide by zero.
        if (length < 1e-9)
            return Distance(p, a);

        var cross = (line.X * (p.Y - a.Y)) - (line.Y * (p.X - a.X));
        return Math.Abs(cross) / length;
    }

    /// <summary>Foot of the perpendicular from a point onto an infinite line.</summary>
    public static Point FootOnLine(Point p, Point a, Point b)
    {
        var line = b - a;
        if (line.LengthSquared < 1e-18)
            return a;

        var t = (((p.X - a.X) * line.X) + ((p.Y - a.Y) * line.Y)) / line.LengthSquared;
        return new Point(a.X + (line.X * t), a.Y + (line.Y * t));
    }

    /// <summary>Distance from a point to a line <b>segment</b>, not its extension.</summary>
    public static double PointToSegmentDistance(Point p, Point a, Point b)
    {
        var line = b - a;
        if (line.LengthSquared < 1e-18)
            return Distance(p, a);

        var t = Math.Clamp(
            (((p.X - a.X) * line.X) + ((p.Y - a.Y) * line.Y)) / line.LengthSquared,
            0,
            1);

        return Distance(p, new Point(a.X + (line.X * t), a.Y + (line.Y * t)));
    }

    /// <summary>
    /// Closest approach of two segments, which is zero where they cross.
    /// </summary>
    public static double SegmentDistance(Point a1, Point a2, Point b1, Point b2)
    {
        if (SegmentsIntersect(a1, a2, b1, b2))
            return 0;

        return Math.Min(
            Math.Min(PointToSegmentDistance(a1, b1, b2), PointToSegmentDistance(a2, b1, b2)),
            Math.Min(PointToSegmentDistance(b1, a1, a2), PointToSegmentDistance(b2, a1, a2)));
    }

    private static bool SegmentsIntersect(Point a1, Point a2, Point b1, Point b2)
    {
        static double Cross(Point o, Point p, Point q) =>
            ((p.X - o.X) * (q.Y - o.Y)) - ((p.Y - o.Y) * (q.X - o.X));

        var d1 = Cross(b1, b2, a1);
        var d2 = Cross(b1, b2, a2);
        var d3 = Cross(a1, a2, b1);
        var d4 = Cross(a1, a2, b2);

        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
               ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    public static double AngleAt(Point vertex, Point armA, Point armB)
    {
        var u = armA - vertex;
        var w = armB - vertex;

        if (u.Length < 1e-9 || w.Length < 1e-9)
            return 0;

        var cosine = ((u.X * w.X) + (u.Y * w.Y)) / (u.Length * w.Length);
        return Math.Acos(Math.Clamp(cosine, -1, 1)) * 180 / Math.PI;
    }

    /// <summary>Smallest angle between two lines, in degrees.</summary>
    public static double LineAngle(Point a1, Point a2, Point b1, Point b2)
    {
        var u = a2 - a1;
        var w = b2 - b1;

        if (u.Length < 1e-9 || w.Length < 1e-9)
            return 0;

        var cosine = Math.Abs(((u.X * w.X) + (u.Y * w.Y)) / (u.Length * w.Length));
        return Math.Acos(Math.Clamp(cosine, 0, 1)) * 180 / Math.PI;
    }

    /// <summary>
    /// Centre and radius of the circle through three points, or null when they
    /// are collinear.
    /// </summary>
    public static (Point Centre, double Radius)? Circumcircle(Point a, Point b, Point c)
    {
        var d = 2 * ((a.X * (b.Y - c.Y)) + (b.X * (c.Y - a.Y)) + (c.X * (a.Y - b.Y)));

        if (Math.Abs(d) < 1e-9)
            return null;

        var aSq = (a.X * a.X) + (a.Y * a.Y);
        var bSq = (b.X * b.X) + (b.Y * b.Y);
        var cSq = (c.X * c.X) + (c.Y * c.Y);

        var centre = new Point(
            ((aSq * (b.Y - c.Y)) + (bSq * (c.Y - a.Y)) + (cSq * (a.Y - b.Y))) / d,
            ((aSq * (c.X - b.X)) + (bSq * (a.X - c.X)) + (cSq * (b.X - a.X))) / d);

        return (centre, Distance(centre, a));
    }

    /// <summary>
    /// How far a point is from this measurement's drawn geometry, for picking.
    /// </summary>
    public double DistanceTo(Point p)
    {
        if (Points.Count < PointsRequired(Tool))
            return double.MaxValue;

        switch (Tool)
        {
            case ViewerTool.PointToPoint:
            case ViewerTool.LineToLineParallel:
            case ViewerTool.LineProfile:
                return PointToSegmentDistance(p, Points[0], Points[1]);

            case ViewerTool.PointToLine:
                return Math.Min(
                    PointToSegmentDistance(p, Points[1], Points[2]),
                    PointToSegmentDistance(p, Points[0], FootOnLine(Points[0], Points[1], Points[2])));

            case ViewerTool.LineToLineAngled:
                return Math.Min(
                    PointToSegmentDistance(p, Points[0], Points[1]),
                    PointToSegmentDistance(p, Points[2], Points[3]));

            case ViewerTool.Angle:
                return Math.Min(
                    PointToSegmentDistance(p, Points[0], Points[1]),
                    PointToSegmentDistance(p, Points[0], Points[2]));

            case ViewerTool.CircleCentre:
            {
                var radius = Distance(Points[0], Points[1]);
                return Math.Abs(Distance(p, Points[0]) - radius);
            }

            case ViewerTool.CircleThreePoint:
            {
                var circle = Circumcircle(Points[0], Points[1], Points[2]);
                return circle is null
                    ? double.MaxValue
                    : Math.Abs(Distance(p, circle.Value.Centre) - circle.Value.Radius);
            }

            // Its outline, as a circle's is: a large rectangle round a whole
            // part would otherwise take every click made inside it.
            case ViewerTool.Rectangle:
            {
                var corners = RectangleCorners(Points[0], Points[1], Points[2]);
                var nearest = double.MaxValue;

                for (var i = 0; i < 4; i++)
                    nearest = Math.Min(nearest, PointToSegmentDistance(p, corners[i], corners[(i + 1) % 4]));

                return nearest;
            }

            default:
                return double.MaxValue;
        }
    }

    /// <summary>
    /// The pair of closest points on two segments, so the reading can be drawn
    /// where it was actually taken.
    /// </summary>
    public static (Point OnFirst, Point OnSecond) ClosestPointsBetween(
        Point a1, Point a2, Point b1, Point b2)
    {
        if (SegmentsIntersect(a1, a2, b1, b2))
        {
            var crossing = Intersection(a1, a2, b1, b2);
            if (crossing is { } point)
                return (point, point);
        }

        // Four point-to-segment candidates cover it: for segments, the closest
        // approach always involves at least one endpoint unless they cross.
        var candidates = new[]
        {
            (First: a1, Second: FootOnSegment(a1, b1, b2)),
            (First: a2, Second: FootOnSegment(a2, b1, b2)),
            (First: FootOnSegment(b1, a1, a2), Second: b1),
            (First: FootOnSegment(b2, a1, a2), Second: b2),
        };

        var best = candidates[0];
        var bestDistance = Distance(best.First, best.Second);

        foreach (var candidate in candidates.Skip(1))
        {
            var distance = Distance(candidate.First, candidate.Second);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>Nearest point on a segment, clamped to its ends.</summary>
    private static Point FootOnSegment(Point p, Point a, Point b)
    {
        var line = b - a;
        if (line.LengthSquared < 1e-18)
            return a;

        var t = Math.Clamp(
            (((p.X - a.X) * line.X) + ((p.Y - a.Y) * line.Y)) / line.LengthSquared,
            0,
            1);

        return new Point(a.X + (line.X * t), a.Y + (line.Y * t));
    }

    /// <summary>Where two infinite lines meet, or null when they are parallel.</summary>
    public static Point? Intersection(Point a1, Point a2, Point b1, Point b2)
    {
        var u = a2 - a1;
        var w = b2 - b1;

        var denominator = (u.X * w.Y) - (u.Y * w.X);
        if (Math.Abs(denominator) < 1e-12)
            return null;

        var t = (((b1.X - a1.X) * w.Y) - ((b1.Y - a1.Y) * w.X)) / denominator;
        return new Point(a1.X + (u.X * t), a1.Y + (u.Y * t));
    }

    // ---- the rectangle -----------------------------------------------------

    /// <summary>
    /// The four corners of the rectangle with a side from <paramref name="a"/>
    /// to <paramref name="b"/> and its opposite side through
    /// <paramref name="across"/>, in order round it: a, b, then the two
    /// across.
    /// </summary>
    /// <remarks>
    /// Serves both the three points of a rectangle being drawn and the
    /// three corners it is kept as: a kept rectangle's third corner is
    /// square to its first side, so it is its own foot across.
    /// </remarks>
    public static Point[] RectangleCorners(Point a, Point b, Point across)
    {
        var normal = Normal(a, b);
        var width = ((across.X - a.X) * normal.X) + ((across.Y - a.Y) * normal.Y);
        var offset = normal * width;

        return [a, b, b + offset, a + offset];
    }

    /// <summary>
    /// The three corners a rectangle is kept as - top left, top right,
    /// bottom right - with its top the side facing most nearly up the
    /// image, so a crop of it is turned by as little as it can be.
    /// </summary>
    public static Point[] UprightCorners(Point a, Point b, Point across)
    {
        var corners = RectangleCorners(a, b, across);
        var centre = Midpoint(corners[0], corners[2]);

        // Of the four sides, the one facing most nearly up: its middle
        // furthest above the centre for its distance from it. On a tie -
        // a rectangle turned exactly 45 degrees - the first found stays.
        var best = 0;
        var bestUp = double.MinValue;

        for (var i = 0; i < 4; i++)
        {
            var outward = Midpoint(corners[i], corners[(i + 1) % 4]) - centre;
            if (outward.Length < 1e-12)
                continue;

            var up = -outward.Y / outward.Length;
            if (up > bestUp + 1e-9)
            {
                best = i;
                bestUp = up;
            }
        }

        return WithTop(corners, best);
    }

    /// <summary>
    /// The same rectangle with the next side clockwise as its top - the
    /// way to say which way up a crop should be without moving it.
    /// </summary>
    public static Point[] NextTop(IReadOnlyList<Point> kept) =>
        WithTop(RectangleCorners(kept[0], kept[1], kept[2]), 1);

    /// <summary>
    /// Top left, top right and bottom right of <paramref name="corners"/>
    /// with the side starting at corner <paramref name="side"/> as the top:
    /// its left the end that keeps a crop unmirrored, whichever way round
    /// the corners were given.
    /// </summary>
    private static Point[] WithTop(Point[] corners, int side)
    {
        var p = corners[side];
        var q = corners[(side + 1) % 4];
        var centre = Midpoint(corners[0], corners[2]);
        var middle = Midpoint(p, q);

        // Down is from the top's middle towards the centre; right is down
        // turned a quarter anticlockwise on screen, as +x is from +y.
        var down = centre - middle;
        var height = 2 * down.Length;

        if (height < 1e-12)
            return [p, q, q];

        down.Normalize();
        var right = new Vector(down.Y, -down.X);

        var (left, rightEnd) = Vector.Multiply(q - p, right) >= 0 ? (p, q) : (q, p);

        return [left, rightEnd, rightEnd + (down * height)];
    }

    /// <summary>
    /// How far a rectangle's top is turned from level, in degrees, clockwise
    /// on screen and between -180 and 180.
    /// </summary>
    public static double RectangleAngle(IReadOnlyList<Point> corners)
    {
        var top = corners[1] - corners[0];
        return Math.Atan2(top.Y, top.X) * 180 / Math.PI;
    }

    /// <summary>
    /// Resizes a kept rectangle by one corner (0 to 3, round from top left),
    /// the opposite corner staying where it is and its turn and top kept.
    /// </summary>
    public static Point[] ResizeRectangle(IReadOnlyList<Point> kept, int corner, Point to)
    {
        var corners = RectangleCorners(kept[0], kept[1], kept[2]);
        var right = corners[1] - corners[0];
        var down = corners[3] - corners[0];

        if (right.Length < 1e-12 || down.Length < 1e-12)
            return [.. kept];

        right.Normalize();
        down.Normalize();

        var origin = corners[0];
        var fixedCorner = corners[(corner + 2) % 4];

        double Along(Point point, Vector axis) => Vector.Multiply(point - origin, axis);

        var (x1, x2) = (Along(fixedCorner, right), Along(to, right));
        var (y1, y2) = (Along(fixedCorner, down), Along(to, down));

        // At least a pixel each way, so a corner dragged over its opposite
        // stops there rather than turning the rectangle inside out.
        var width = Math.Max(1, Math.Abs(x2 - x1));
        var height = Math.Max(1, Math.Abs(y2 - y1));
        var left = x2 < x1 ? x1 - width : x1;
        var top = y2 < y1 ? y1 - height : y1;

        var topLeft = origin + (right * left) + (down * top);
        return [topLeft, topLeft + (right * width), topLeft + (right * width) + (down * height)];
    }

    /// <summary>
    /// A kept rectangle turned about its centre by <paramref name="degrees"/>,
    /// clockwise on screen.
    /// </summary>
    public static Point[] TurnRectangle(IReadOnlyList<Point> kept, double degrees)
    {
        var centre = Midpoint(kept[0], kept[2]);
        var (sin, cos) = Math.SinCos(degrees * Math.PI / 180);

        Point Turn(Point p)
        {
            var v = p - centre;
            return new Point(centre.X + (v.X * cos) - (v.Y * sin), centre.Y + (v.X * sin) + (v.Y * cos));
        }

        return [Turn(kept[0]), Turn(kept[1]), Turn(kept[2])];
    }

    /// <summary>
    /// Where a kept rectangle's knob is: beyond the middle of its top,
    /// <paramref name="reach"/> image pixels out.
    /// </summary>
    public static Point RectangleKnob(IReadOnlyList<Point> kept, double reach)
    {
        var middle = Midpoint(kept[0], kept[1]);
        var down = kept[2] - kept[1];

        if (down.Length < 1e-12)
            return middle;

        down.Normalize();
        return middle - (down * reach);
    }

    /// <summary>
    /// A unit vector perpendicular to a-b, for the reference lines a caliper
    /// measurement draws across.
    /// </summary>
    public static Vector Normal(Point a, Point b)
    {
        var line = b - a;
        if (line.Length < 1e-9)
            return new Vector(0, 0);

        line.Normalize();
        return new Vector(-line.Y, line.X);
    }
}
