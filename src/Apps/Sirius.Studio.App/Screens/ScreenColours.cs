using System.Windows.Media;

namespace Sirius.Studio.App.Screens;

/// <summary>
/// Colours a screen's widgets can be given - an Image Viewer's background -
/// as the screen file keeps them: "#RRGGBB", or blank for the widget's own.
/// </summary>
public static class ScreenColours
{
    /// <summary>The Image Viewer's own background, as in Studio's viewers.</summary>
    public const string ViewerDefault = "#17181A";

    /// <summary>What the property grid offers, by name; any other colour can be typed.</summary>
    public static IReadOnlyList<(string Name, string? Value)> Presets { get; } =
    [
        ("Default", null),
        ("Black", "#000000"),
        ("Charcoal", "#262A30"),
        ("Grey", "#808080"),
        ("Light grey", "#D0D0D0"),
        ("White", "#FFFFFF"),
    ];

    /// <summary>
    /// <paramref name="text"/> as the file keeps it: a preset's value, a
    /// colour name or hex normalised to "#RRGGBB" ("#AARRGGBB" when not
    /// opaque), or null for blank or "Default".
    /// </summary>
    /// <param name="valid">False when it is not a colour at all.</param>
    public static string? Normalise(string? text, out bool valid)
    {
        valid = true;
        var trimmed = text?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            return null;

        foreach (var (name, value) in Presets)
        {
            if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        if (Parse(trimmed) is not { } colour)
        {
            valid = false;
            return null;
        }

        return colour.A == 255
            ? $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}"
            : $"#{colour.A:X2}{colour.R:X2}{colour.G:X2}{colour.B:X2}";
    }

    /// <summary>"Default", a preset's name, or the hex the file holds.</summary>
    public static string Describe(string? stored)
    {
        foreach (var (name, value) in Presets)
        {
            if (string.Equals(value, stored, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        return stored ?? "Default";
    }

    /// <summary>The colour <paramref name="text"/> names, or null.</summary>
    public static Color? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            return ColorConverter.ConvertFromString(text.Trim()) as Color?;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A frozen brush of the stored colour, or of <paramref name="fallback"/> when there is none.</summary>
    public static Brush Brush(string? stored, string fallback = ViewerDefault)
    {
        var colour = Parse(stored) ?? Parse(fallback) ?? Colors.Black;
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    /// <summary>Whether text in a colour box is one: for the property grid's "not a colour".</summary>
    public static bool IsColour(string? text)
    {
        Normalise(text, out var valid);
        return valid;
    }
}
