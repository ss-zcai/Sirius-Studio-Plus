using System.Globalization;

namespace Sirius.Core.Shapes;

/// <summary>A point in image pixel coordinates.</summary>
/// <remarks>
/// Also what a node means by a position parameter. One type rather than a
/// second one for parameters: a place on the image is a place on the
/// image, whether a tool found it or an operator typed it, and two types
/// would mean converting between them at every boundary.
/// </remarks>
public readonly record struct Point2(double X, double Y)
{
    public static Point2 operator +(Point2 point, Point2 offset) =>
        new(point.X + offset.X, point.Y + offset.Y);

    public double DistanceTo(Point2 other)
    {
        var dx = other.X - X;
        var dy = other.Y - Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>Reads "100, 200", or "100 200".</summary>
    public static bool TryParse(string? text, out Point2 point)
    {
        point = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split([',', ' ', '	'], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2)
            return false;

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            return false;
        }

        point = new Point2(x, y);
        return true;
    }

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0:0.##}, {1:0.##}", X, Y);
}

/// <summary>An axis-aligned rectangle in image pixel coordinates.</summary>
public readonly record struct Bounds(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double Area => Width * Height;

    public Point2 Centre => new(X + (Width / 2), Y + (Height / 2));
}

/// <summary>
/// Geometry produced by a node, or drawn by an operator.
/// </summary>
/// <remarks>
/// <para>
/// Shapes are ordinary port data. A node that finds circles emits them, a
/// viewer that is wired to them draws them, and a node that needs a line can
/// take one from upstream instead of four typed numbers. "Annotation" is not a
/// separate mechanism - it is a shape connected to a viewer.
/// </para>
/// <para>
/// Measurements ride along in <see cref="Metrics"/> rather than as typed
/// fields on each subtype. A blob carries area, circularity and the rest; a
/// found line carries its length and angle. Keying them by name is what lets a
/// filter work on any producer's output without knowing what produced it.
/// </para>
/// <para>
/// All coordinates are pixels in the image the shape was found in, which is
/// why <see cref="ShapeSet"/> records that image's size.
/// </para>
/// </remarks>
public abstract record Shape
{
    /// <summary>Named measurements, as described by <see cref="ShapeMetric"/>.</summary>
    public IReadOnlyDictionary<string, double> Metrics { get; init; } =
        new Dictionary<string, double>();

    /// <summary>Drawn beside the shape when set.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// The label's height in image pixels, or null for a fixed size on
    /// screen.
    /// </summary>
    /// <remarks>
    /// Which of the two is wanted depends on what the label is for. A
    /// reading a person is squinting at while they pan around wants to stay
    /// the same size on screen, the way the measuring tools' readings do.
    /// A caption that belongs to the picture - written on a part, kept in a
    /// saved image, printed on a report - wants to sit at a fixed size in
    /// the image, so zooming out shrinks it along with everything else.
    /// Set this for the second; leave it null for the first.
    /// </remarks>
    public double? LabelSize { get; init; }

    /// <summary>
    /// The typeface to draw the label in, or null for the viewer's own.
    /// </summary>
    /// <remarks>
    /// A family name rather than a chosen font, because the viewer is the
    /// only part of this that knows what a font is; a name a machine does
    /// not have falls back to the default rather than failing.
    /// </remarks>
    public string? LabelFont { get; init; }

    /// <summary>
    /// Stroke width in image pixels, or null to let the viewer choose.
    /// </summary>
    /// <remarks>
    /// Set by a producer that wants one set of shapes to read louder than
    /// another - a failing feature over a passing one, say. The viewer still
    /// scales it by the zoom, so this is a weight rather than a size on
    /// screen.
    /// </remarks>
    public double? StrokeWidth { get; init; }

    /// <summary>
    /// An ARGB colour for the shape, or null to let the viewer choose.
    /// </summary>
    /// <remarks>
    /// Set by the producing node, so a pass/fail decision can be shown by
    /// colour without the viewer knowing anything about the decision.
    /// </remarks>
    public uint? Colour { get; init; }

    /// <summary>The axis-aligned extent, for picking and for fitting a view.</summary>
    public abstract Bounds Bounds { get; }

    /// <summary>Reads a measurement, or null when the producer did not take it.</summary>
    public double? Metric(string name) =>
        Metrics.TryGetValue(name, out var value) ? value : null;
}

/// <summary>A single position - a centre, a corner, a hit.</summary>
public sealed record PointShape(Point2 At) : Shape
{
    public override Bounds Bounds => new(At.X, At.Y, 0, 0);
}

/// <summary>A straight segment between two points.</summary>
public sealed record LineShape(Point2 Start, Point2 End) : Shape
{
    public double Length => Start.DistanceTo(End);

    public override Bounds Bounds => new(
        Math.Min(Start.X, End.X),
        Math.Min(Start.Y, End.Y),
        Math.Abs(End.X - Start.X),
        Math.Abs(End.Y - Start.Y));
}

/// <summary>An axis-aligned rectangle.</summary>
public sealed record RectShape(Bounds Rect) : Shape
{
    public override Bounds Bounds => Rect;
}

/// <summary>
/// A rectangle at an angle - the minimum-area box around a feature, or a
/// region of interest that follows a tilted part.
/// </summary>
/// <remarks>
/// <see cref="AngleDegrees"/> turns the rectangle clockwise as seen on the
/// image, which is the direction image coordinates run: y increases downward,
/// so the usual rotation matrix appears clockwise. It matches what OpenCV's
/// minimum-area rectangle reports.
/// </remarks>
public sealed record OrientedRectShape(
    Point2 Centre,
    double Width,
    double Height,
    double AngleDegrees) : Shape
{
    /// <summary>The four corners, clockwise from the top-left of the unrotated box.</summary>
    public Point2[] Corners()
    {
        var radians = AngleDegrees * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        var halfWidth = Width / 2;
        var halfHeight = Height / 2;

        Point2 Corner(double dx, double dy) => new(
            Centre.X + (dx * cos) - (dy * sin),
            Centre.Y + (dx * sin) + (dy * cos));

        return
        [
            Corner(-halfWidth, -halfHeight),
            Corner(halfWidth, -halfHeight),
            Corner(halfWidth, halfHeight),
            Corner(-halfWidth, halfHeight),
        ];
    }

    public override Bounds Bounds
    {
        get
        {
            var corners = Corners();

            var minX = corners.Min(corner => corner.X);
            var minY = corners.Min(corner => corner.Y);
            var maxX = corners.Max(corner => corner.X);
            var maxY = corners.Max(corner => corner.Y);

            return new Bounds(minX, minY, maxX - minX, maxY - minY);
        }
    }
}

/// <summary>A circle.</summary>
public sealed record CircleShape(Point2 Centre, double Radius) : Shape
{
    public override Bounds Bounds =>
        new(Centre.X - Radius, Centre.Y - Radius, Radius * 2, Radius * 2);
}

/// <summary>
/// A closed or open run of points - a blob outline, a contour, a fitted path.
/// </summary>
/// <summary>
/// An ellipse: a centre, two axis lengths and an angle.
/// </summary>
/// <remarks>
/// Its own shape rather than the oriented rectangle that bounds it, which
/// is what a fit returns. Drawn as a rectangle it would claim corners the
/// feature does not have, and a mask built from it would include them.
/// </remarks>
public sealed record EllipseShape(
    Point2 Centre,
    double MajorAxis,
    double MinorAxis,
    double Angle) : Shape
{
    /// <summary>
    /// The axis-aligned box the ellipse fits inside, at its angle.
    /// </summary>
    /// <remarks>
    /// The true extent rather than the bounding box of the un-rotated
    /// ellipse, which would be too small on the diagonal.
    /// </remarks>
    public override Bounds Bounds
    {
        get
        {
            var radians = Angle * Math.PI / 180.0;
            var a = MajorAxis / 2;
            var b = MinorAxis / 2;

            var halfWidth = Math.Sqrt(
                (a * a * Math.Cos(radians) * Math.Cos(radians)) +
                (b * b * Math.Sin(radians) * Math.Sin(radians)));

            var halfHeight = Math.Sqrt(
                (a * a * Math.Sin(radians) * Math.Sin(radians)) +
                (b * b * Math.Cos(radians) * Math.Cos(radians)));

            return new Bounds(
                Centre.X - halfWidth,
                Centre.Y - halfHeight,
                halfWidth * 2,
                halfHeight * 2);
        }
    }
}

public sealed record PolygonShape(IReadOnlyList<Point2> Points, bool Closed = true) : Shape
{
    public override Bounds Bounds
    {
        get
        {
            if (Points.Count == 0)
                return default;

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var point in Points)
            {
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }

            return new Bounds(minX, minY, maxX - minX, maxY - minY);
        }
    }
}

/// <summary>
/// Shapes produced by one node, with the frame they were measured in.
/// </summary>
/// <remarks>
/// The frame size is carried so a viewer can tell when it is being asked to
/// draw shapes found on a differently sized image - after a Crop or a Resize,
/// the coordinates refer to something else and would be drawn in the wrong
/// place. Remapping them through such a node is the real fix; reporting the
/// mismatch is what stops it being silent in the meantime.
/// </remarks>
public sealed class ShapeSet
{
    public static ShapeSet Empty { get; } = new([], 0, 0);

    public ShapeSet(IReadOnlyList<Shape> shapes, int frameWidth, int frameHeight)
    {
        Shapes = shapes;
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
    }

    public IReadOnlyList<Shape> Shapes { get; }

    /// <summary>Width of the image these shapes were found in, or 0 if unknown.</summary>
    public int FrameWidth { get; }

    /// <summary>Height of the image these shapes were found in, or 0 if unknown.</summary>
    public int FrameHeight { get; }

    public int Count => Shapes.Count;

    /// <summary>
    /// Whether these shapes belong to an image of the given size.
    /// </summary>
    /// <remarks>
    /// Unknown frames match anything: a shape set built by hand, or typed in,
    /// has nothing to disagree with.
    /// </remarks>
    public bool MatchesFrame(int width, int height) =>
        FrameWidth == 0 || FrameHeight == 0 || (FrameWidth == width && FrameHeight == height);
}

/// <summary>
/// The measurement names a shape may carry.
/// </summary>
/// <remarks>
/// Strings rather than an enum, so a node can publish a measurement this
/// library has never heard of and a filter can still select on it. These are
/// the well-known ones, spelled once.
/// </remarks>
public static class ShapeMetric
{
    public const string Area = "area";
    public const string Perimeter = "perimeter";
    public const string CentroidX = "centroidX";
    public const string CentroidY = "centroidY";
    public const string BoundsX = "boundsX";
    public const string BoundsY = "boundsY";
    public const string BoundsWidth = "boundsWidth";
    public const string BoundsHeight = "boundsHeight";

    /// <summary>Area divided by the area of the bounding box.</summary>
    public const string Extent = "extent";

    /// <summary>4 pi A / P squared. 1.0 is a perfect circle.</summary>
    public const string Circularity = "circularity";

    /// <summary>Area divided by the area of the convex hull. 1.0 is fully convex.</summary>
    public const string Solidity = "solidity";

    /// <summary>Long side divided by short side of the minimum-area rectangle.</summary>
    public const string AspectRatio = "aspectRatio";

    /// <summary>Short side of the minimum-area rectangle.</summary>
    public const string Width = "width";

    /// <summary>Long side of the minimum-area rectangle.</summary>
    public const string Length = "length";

    /// <summary>Orientation of the minimum-area rectangle, in degrees.</summary>
    public const string Angle = "angle";

    public const string HoleCount = "holeCount";

    /// <summary>1 when the shape reaches the edge of the frame, otherwise 0.</summary>
    public const string TouchesBorder = "touchesBorder";

    /// <summary>Radius of a fitted circle.</summary>
    public const string Radius = "radius";

    /// <summary>Longer axis of a fitted ellipse, end to end.</summary>
    public const string MajorAxis = "majorAxis";

    /// <summary>Shorter axis of a fitted ellipse, end to end.</summary>
    public const string MinorAxis = "minorAxis";

    /// <summary>
    /// How far the points sat from the fitted shape, as a root-mean-square
    /// distance in pixels.
    /// </summary>
    /// <remarks>
    /// The most useful number a fit produces, and the one most often left
    /// out. A circle fitted to a broken arc and a circle fitted to a clean
    /// one look identical until this is read.
    /// </remarks>
    public const string FitError = "fitError";

    /// <summary>How many points the fit was computed from.</summary>
    public const string FitPoints = "fitPoints";
}
