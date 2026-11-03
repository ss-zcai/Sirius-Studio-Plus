using System.Drawing;
using System.Globalization;
using Sirius.Core.Nodes;

namespace Sirius.Core.Shapes;

/// <summary>
/// Where a reference shape is, and which way it faces when it has a
/// direction - what a tool's regions follow from part to part.
/// </summary>
/// <remarks>
/// <para>
/// A region drawn on one part is meant for the same place on the next,
/// wherever that part has come to rest. Wire a found result into a tool's
/// Reference and the tool remembers where the result was when it was
/// wired; on every run after, its regions move by however far the result
/// has moved since, and turn by however far it has turned.
/// </para>
/// <para>
/// Only shapes with a direction of their own give an angle: a turned
/// rectangle (a blob's minimum-area box) and a line. A circle, a point, a
/// blob's outline give their centre and nothing more. An ellipse gives its
/// centre only, too: a fitted ellipse that is nearly round has an angle
/// that swings from part to part, and a region following it would swing
/// with it.
/// </para>
/// </remarks>
/// <param name="X">Where it is, in image pixels.</param>
/// <param name="Y">Where it is, in image pixels.</param>
/// <param name="Angle">Which way it faces, in degrees clockwise; null when it has no direction.</param>
public readonly record struct ReferencePose(double X, double Y, double? Angle)
{
    /// <summary>The pose of <paramref name="shape"/>: its centre, and its angle where it has one.</summary>
    public static ReferencePose Of(Shape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        switch (shape)
        {
            case OrientedRectShape box:
                return new ReferencePose(box.Centre.X, box.Centre.Y, box.AngleDegrees);

            case LineShape line:
                return new ReferencePose(
                    (line.Start.X + line.End.X) / 2,
                    (line.Start.Y + line.End.Y) / 2,
                    Math.Atan2(line.End.Y - line.Start.Y, line.End.X - line.Start.X) * 180 / Math.PI);

            default:
            {
                var centre = ShapeRegions.CentreOf(shape);
                return new ReferencePose(centre.X, centre.Y, null);
            }
        }
    }

    /// <summary>
    /// How far <paramref name="to"/> is turned from <paramref name="from"/>,
    /// in degrees - zero when either has no direction.
    /// </summary>
    /// <remarks>
    /// Taken as the smaller of the two ways of reading it. A line's ends can
    /// come out either way round, and a rectangle's angle can be reported
    /// half a turn on, so a direction is only known to within half a turn:
    /// the turn is the one between -90 and 90 degrees. A part turned further
    /// than that from where it was taught would be read the wrong way round,
    /// which on a fixture or a conveyor it is not.
    /// </remarks>
    public static double Turn(ReferencePose from, ReferencePose to)
    {
        if (from.Angle is not { } before || to.Angle is not { } after)
            return 0;

        var turn = (after - before) % 180;

        if (turn > 90)
            turn -= 180;
        else if (turn <= -90)
            turn += 180;

        return turn;
    }

    /// <summary>
    /// <paramref name="geometry"/> - a band, a region, a path - moved as a
    /// reference moved from <paramref name="from"/> to <paramref name="to"/>,
    /// turning <paramref name="degrees"/> about it.
    /// </summary>
    /// <remarks>
    /// An upright rectangle - Crop's - can follow where the reference went
    /// but not how it turned, having no angle to turn: its centre is carried
    /// round, and its sides stay upright.
    /// </remarks>
    /// <remarks>
    /// The turn is given rather than worked out from the two, so that moving
    /// back is exactly undoing: a part turned a quarter turn reads as 90
    /// degrees one way and, worked out afresh, 90 again the other - the half
    /// turn either side is the same reading - and the way back went on round
    /// instead of returning.
    /// </remarks>
    public static object Move(object geometry, ReferencePose from, ReferencePose to, double degrees)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        var radians = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));

        Point2 Carry(double x, double y)
        {
            var (dx, dy) = (x - from.X, y - from.Y);
            return new Point2(to.X + (dx * cos) - (dy * sin), to.Y + (dx * sin) + (dy * cos));
        }

        switch (geometry)
        {
            case OrientedRect rect:
            {
                var centre = Carry(rect.CentreX, rect.CentreY);
                return new OrientedRect(centre.X, centre.Y, rect.Width, rect.Height, rect.Angle + degrees);
            }

            case Ring ring:
            {
                var centre = Carry(ring.CentreX, ring.CentreY);
                return ring.WithCentre(centre.X, centre.Y);
            }

            case Rectangle box:
            {
                var centre = Carry(box.X + (box.Width / 2.0), box.Y + (box.Height / 2.0));
                return new Rectangle(
                    (int)Math.Round(centre.X - (box.Width / 2.0)),
                    (int)Math.Round(centre.Y - (box.Height / 2.0)),
                    box.Width,
                    box.Height);
            }

            case PathBand path:
                return new PathBand(path.Points.Select(point => Carry(point.X, point.Y)).ToArray(), path.Closed, path.Width);

            default:
                return geometry;
        }
    }

    /// <summary>Whether <paramref name="type"/> is geometry a reference can move.</summary>
    public static bool CanMove(Type type) =>
        type == typeof(OrientedRect) || type == typeof(Ring) || type == typeof(Rectangle) || type == typeof(PathBand);

    /// <summary>"x, y" or "x, y, angle", as a recipe keeps it.</summary>
    public override string ToString() => Angle is { } angle
        ? string.Create(CultureInfo.InvariantCulture, $"{X:0.###}, {Y:0.###}, {angle:0.####}")
        : string.Create(CultureInfo.InvariantCulture, $"{X:0.###}, {Y:0.###}");

    /// <summary>Reads what <see cref="ToString"/> wrote; false for anything else, empty included.</summary>
    public static bool TryParse(string? text, out ReferencePose pose)
    {
        pose = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        var numbers = new double[parts.Length];

        if (parts.Length is not (2 or 3))
            return false;

        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }

        pose = new ReferencePose(numbers[0], numbers[1], parts.Length == 3 ? numbers[2] : null);
        return true;
    }
}
