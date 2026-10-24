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
    /// Intensity sampled along a line, charted beneath the image.
    /// </summary>
    /// <remarks>
    /// Not a measurement in the sense the others are - it reports a curve
    /// rather than a number - but it is placed, selected, recoloured and
    /// deleted identically, so it belongs with them rather than in a mode of
    /// its own.
    /// </remarks>
    LineProfile,
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

            _ => string.Empty,
        };
    }

    private static string Format(double pixels) =>
        string.Create(CultureInfo.CurrentCulture, $"{pixels:0.##} px");

    private static string RadiusReading(double radius) =>
        string.Create(CultureInfo.CurrentCulture, $"R {radius:0.##}  ⌀ {radius * 2:0.##} px");

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
