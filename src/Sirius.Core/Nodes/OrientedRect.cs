using System.Globalization;

namespace Sirius.Core.Nodes;

/// <summary>
/// A rectangle with an angle, edited as one parameter.
/// </summary>
/// <remarks>
/// <para>
/// A region is one thing. Split into a centre, a width, a height and an
/// angle it becomes five rows in the inspector and - worse - five ports on
/// the node, four of which exist only because a number might be driven by a
/// wire. The node ends up looking like an arithmetic unit rather than a
/// tool with a region.
/// </para>
/// <para>
/// The centre rather than a corner, because that is what rotation is about
/// and what a minimum-area rectangle reports. Angle in degrees, positive
/// clockwise, matching <c>OrientedRectShape</c> and OpenCV's
/// <c>MinAreaRect</c>.
/// </para>
/// </remarks>
public readonly record struct OrientedRect
{
    public OrientedRect(double centreX, double centreY, double width, double height, double angle)
    {
        CentreX = centreX;
        CentreY = centreY;

        // A region with no area is not a region, and every consumer would
        // otherwise need the same guard.
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Angle = angle;
    }

    public double CentreX { get; init; }

    public double CentreY { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    /// <summary>Degrees, positive clockwise.</summary>
    public double Angle { get; init; }

    public double Left => CentreX - (Width / 2);

    public double Top => CentreY - (Height / 2);

    /// <summary>
    /// The upright rectangle of the same centre and size, ignoring the
    /// angle.
    /// </summary>
    /// <remarks>
    /// What an axis-aligned editor can offer: dragging sets where the region
    /// is and how big, and leaves the angle to whatever set it. Not the same
    /// as the bounding box of the rotated rectangle, which would grow every
    /// time it was read back and written out.
    /// </remarks>
    public (double X, double Y, double Width, double Height) Upright =>
        (Left, Top, Width, Height);

    /// <summary>
    /// The same region moved and resized, keeping its angle.
    /// </summary>
    public OrientedRect WithUpright(double x, double y, double width, double height) => new(
        x + (width / 2),
        y + (height / 2),
        width,
        height,
        Angle);

    /// <summary>
    /// Parses "x, y, w, h, angle". Brackets and spaces are ignored, and the
    /// angle may be left off.
    /// </summary>
    public static bool TryParse(string? text, out OrientedRect value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text
            .Trim()
            .Trim('(', ')', '[', ']')
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length is < 4 or > 5)
            return false;

        var numbers = new double[parts.Length];

        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.CurrentCulture, out numbers[i]))
                return false;
        }

        value = new OrientedRect(
            numbers[0],
            numbers[1],
            numbers[2],
            numbers[3],
            parts.Length == 5 ? numbers[4] : 0);

        return true;
    }

    public override string ToString() => string.Format(
        CultureInfo.CurrentCulture,
        "{0:0.##}, {1:0.##}, {2:0.##}, {3:0.##}, {4:0.##}",
        CentreX,
        CentreY,
        Width,
        Height,
        Angle);
}
