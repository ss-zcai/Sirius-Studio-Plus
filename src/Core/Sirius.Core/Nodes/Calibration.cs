using System.Globalization;

namespace Sirius.Core.Nodes;

/// <summary>
/// What a pixel is worth, and in what.
/// </summary>
/// <remarks>
/// <para>
/// Every reading this product takes is in pixels, and no drawing is
/// written in pixels. This is the conversion, and it travels on a wire
/// rather than sitting in a setting on the recipe.
/// </para>
/// <para>
/// On a wire because a scale is not a property of a recipe. Two cameras
/// looking at two parts have two scales; a Resize halves one; a Polar
/// unwrap has a different scale along each axis. A value that flows can
/// say all of that, and a single global number can only be right about
/// the simplest case and silently wrong about the rest.
/// </para>
/// <para>
/// Stored as units per pixel rather than pixels per unit because that is
/// the direction every reading is converted in - a measurement is taken
/// in pixels and reported in millimetres, never the other way round.
/// </para>
/// </remarks>
public readonly record struct Calibration
{
    /// <summary>Pixels, reported as pixels: the absence of a calibration.</summary>
    public static readonly Calibration None = new(1, "px");

    public Calibration(double unitsPerPixel, string? unit)
    {
        // A scale of zero or less collapses every reading to nothing or
        // turns it inside out, and neither is a scale anybody meant.
        UnitsPerPixel = unitsPerPixel > 0 && !double.IsInfinity(unitsPerPixel)
            ? unitsPerPixel
            : 1;

        Unit = string.IsNullOrWhiteSpace(unit) ? "px" : unit.Trim();
    }

    /// <summary>How much of <see cref="Unit"/> one pixel covers.</summary>
    public double UnitsPerPixel { get; init; }

    /// <summary>What the readings are in - "mm", "in", "um".</summary>
    public string Unit { get; init; }

    /// <summary>Whether this is anything other than pixels at one to one.</summary>
    public bool IsCalibrated =>
        Math.Abs(UnitsPerPixel - 1) > 1e-12 ||
        !string.Equals(Unit, "px", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A scale from a length that is known in both: so many pixels across
    /// is so many millimetres.
    /// </summary>
    /// <remarks>
    /// How anybody actually calibrates - put a gauge block or a printed
    /// scale in the field of view, measure it on the image, and type what
    /// it really is.
    /// </remarks>
    public static Calibration FromKnownLength(double pixels, double length, string? unit) =>
        pixels > 0 && length > 0
            ? new Calibration(length / pixels, unit)
            : new Calibration(1, unit);

    /// <summary>A length in pixels, in units.</summary>
    public double ToUnits(double pixels) => pixels * UnitsPerPixel;

    /// <summary>A length in units, back in pixels.</summary>
    public double ToPixels(double units) => units / UnitsPerPixel;

    /// <summary>
    /// An area in square pixels, in square units.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ToUnits"/> and not a special case of it:
    /// an area scales by the square, and a blob's area put through the
    /// length conversion would be wrong by the scale factor itself -
    /// which on a typical lens is a factor of twenty.
    /// </remarks>
    public double AreaToUnits(double squarePixels) =>
        squarePixels * UnitsPerPixel * UnitsPerPixel;

    /// <summary>Reads "0.0412 mm", or a bare number as pixels.</summary>
    public static bool TryParse(string? text, out Calibration calibration)
    {
        calibration = None;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length is < 1 or > 2)
            return false;

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var scale))
            return false;

        calibration = new Calibration(scale, parts.Length == 2 ? parts[1] : "px");
        return true;
    }

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0:0.######} {1}", UnitsPerPixel, Unit);
}
