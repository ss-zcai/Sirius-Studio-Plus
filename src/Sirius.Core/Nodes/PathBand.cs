using System.Globalization;
using Sirius.Core.Shapes;

namespace Sirius.Core.Nodes;

/// <summary>
/// A search band along a path: the line an edge is expected to follow, and
/// how wide a strip either side of it to search.
/// </summary>
/// <remarks>
/// <para>
/// What <see cref="Ring"/> is to a circle and <see cref="OrientedRect"/> to
/// a straight edge, for an edge that is neither - a fillet, a moulded
/// profile, a contour taken from a good part. One value, drawn in one
/// gesture, for the same reason those are: the points and the width are
/// one decision, and a band that is drawn has to be drawn with its width
/// or nobody can see what will be searched.
/// </para>
/// <para>
/// A class rather than a record struct because it holds a list, and value
/// equality is written out so that two paths through the same points are
/// the same parameter value - which is what tells an edit from a no-op.
/// </para>
/// </remarks>
public sealed class PathBand : IEquatable<PathBand>
{
    /// <summary>The band a node starts with: a gentle arc, 40 wide.</summary>
    public static PathBand Default { get; } = new(
        [new Point2(200, 300), new Point2(300, 250), new Point2(400, 250), new Point2(500, 300)],
        closed: false,
        width: 40);

    public PathBand(IReadOnlyList<Point2> points, bool closed, double width)
    {
        Points = points.ToArray();

        // A closed path needs a triangle's worth of corners to enclose
        // anything; two points closed is the same segment twice.
        Closed = closed && Points.Count >= 3;

        // A band of no width can be typed but not searched, as with a ring.
        Width = double.IsFinite(width) && width >= 1 ? width : 1;
    }

    /// <summary>The corners of the path, in order.</summary>
    public IReadOnlyList<Point2> Points { get; }

    /// <summary>Whether the last point joins back to the first.</summary>
    public bool Closed { get; }

    /// <summary>The band's full width, centred on the path.</summary>
    public double Width { get; }

    /// <summary>How far the band reaches either side of the path.</summary>
    public double HalfWidth => Width / 2;

    /// <summary>Whether there is a path to search along: two points at least, apart.</summary>
    public bool IsUsable => Length > 0;

    /// <summary>The path's length, including the closing side when closed.</summary>
    public double Length
    {
        get
        {
            var length = 0.0;

            for (var i = 1; i < Points.Count; i++)
                length += Points[i - 1].DistanceTo(Points[i]);

            if (Closed)
                length += Points[^1].DistanceTo(Points[0]);

            return length;
        }
    }

    public PathBand WithPoints(IReadOnlyList<Point2> points) => new(points, Closed, Width);

    public PathBand WithWidth(double width) => new(Points, Closed, width);

    /// <summary>The same path moved by an offset.</summary>
    public PathBand Moved(double dx, double dy) =>
        new(Points.Select(point => new Point2(point.X + dx, point.Y + dy)).ToArray(), Closed, Width);

    /// <summary>The path as a shape, for drawing it or handing it on.</summary>
    public PolygonShape ToShape() => new(Points, Closed);

    /// <summary>
    /// Reads "100, 200; 150, 220; 200, 260 | 40", with "| closed" after the
    /// width for a closed path. The width may be left off, keeping
    /// <paramref name="width"/>.
    /// </summary>
    public static bool TryParse(string? text, double width, out PathBand path)
    {
        path = Default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var sections = text.Split('|', StringSplitOptions.TrimEntries);
        var closed = false;

        for (var i = 1; i < sections.Length; i++)
        {
            if (sections[i].Equals("closed", StringComparison.OrdinalIgnoreCase))
                closed = true;
            else if (sections[i].Equals("open", StringComparison.OrdinalIgnoreCase))
                closed = false;
            else if (!double.TryParse(sections[i], NumberStyles.Float, CultureInfo.InvariantCulture, out width))
                return false;
        }

        var points = new List<Point2>();

        foreach (var corner in sections[0].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Point2.TryParse(corner, out var point))
                return false;

            points.Add(point);
        }

        if (points.Count < 2)
            return false;

        path = new PathBand(points, closed, width);
        return true;
    }

    public override string ToString()
    {
        var corners = string.Join("; ", Points.Select(point => point.ToString()));
        var width = Width.ToString("0.##", CultureInfo.InvariantCulture);

        return Closed ? $"{corners} | {width} | closed" : $"{corners} | {width}";
    }

    public bool Equals(PathBand? other) =>
        other is not null &&
        Closed == other.Closed &&
        Width.Equals(other.Width) &&
        Points.SequenceEqual(other.Points);

    public override bool Equals(object? obj) => Equals(obj as PathBand);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Closed);
        hash.Add(Width);

        foreach (var point in Points)
            hash.Add(point);

        return hash.ToHashCode();
    }

    public static bool operator ==(PathBand? left, PathBand? right) => Equals(left, right);

    public static bool operator !=(PathBand? left, PathBand? right) => !Equals(left, right);
}
