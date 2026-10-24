using System.Globalization;

namespace Sirius.Vision;

/// <summary>
/// A two-component value — a size, an offset, a kernel extent.
/// </summary>
/// <remarks>
/// Deliberately its own type rather than two separate number ports: a size is
/// one decision, and splitting it means two wires that can disagree. Kept as a
/// readonly struct so it carries no lifetime concerns, unlike the image types.
/// </remarks>
public readonly record struct Vector2(double X, double Y)
{
    public static Vector2 Zero => new(0, 0);

    /// <summary>Rounded to whole numbers, for APIs that take pixel counts.</summary>
    public (int X, int Y) ToInt() => ((int)Math.Round(X), (int)Math.Round(Y));

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X:0.###}, {Y:0.###})");
}
