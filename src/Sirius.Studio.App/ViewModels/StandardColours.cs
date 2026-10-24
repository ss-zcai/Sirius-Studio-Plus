using System.Windows.Media;

namespace Sirius.Studio.App.ViewModels;

/// <summary>One colour offered by a picker.</summary>
/// <remarks>
/// Carries two brushes. <see cref="Swatch"/> is the colour itself, shown as a
/// block. <see cref="Text"/> is the colour the *name* is written in, which is
/// the same thing except where that would be unreadable - black on a dark
/// panel disappears, so it is written in grey while its swatch stays black.
/// </remarks>
public sealed class ColourOption
{
    public ColourOption(string name, string hex, string? textHex = null)
    {
        Name = name;
        Hex = hex;
        Swatch = Frozen(hex);
        Text = Frozen(textHex ?? hex);
    }

    public string Name { get; }

    /// <summary>The value stored on the parameter, as #RRGGBB.</summary>
    public string Hex { get; }

    public Brush Swatch { get; }

    public Brush Text { get; }

    public override string ToString() => Name;

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// The colours offered wherever something is drawn over an image.
/// </summary>
/// <remarks>
/// <para>
/// One list, used by the measurement picker in the viewer and by any Colour
/// parameter, so a measurement and an annotation can be set to the same
/// green and actually match.
/// </para>
/// <para>
/// Chosen to stay visible on both dark and bright parts of a greyscale image,
/// which is why the set is saturated rather than pastel, and why it includes
/// both white and black - the two that work when everything else is lost
/// against the subject.
/// </para>
/// </remarks>
public static class StandardColours
{
    public static IReadOnlyList<ColourOption> All { get; } =
    [
        new("Amber", "#FBBF24"),
        new("Orange", "#FB923C"),
        new("Red", "#EF4444"),
        new("Pink", "#F472B6"),
        new("Magenta", "#E879F9"),
        new("Violet", "#A78BFA"),
        new("Blue", "#38BDF8"),
        new("Cyan", "#22D3EE"),
        new("Green", "#22C55E"),
        new("Lime", "#A3E635"),
        new("White", "#FFFFFF"),
        new("Grey", "#9CA3AF"),

        // Its swatch is black; its name is not, or it would be invisible here.
        new("Black", "#111111", "#8A8F98"),
    ];

    /// <summary>The option matching a hex value, or null when it is a custom one.</summary>
    public static ColourOption? Find(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;

        var wanted = hex.Trim();

        return All.FirstOrDefault(
            option => string.Equals(option.Hex, wanted, StringComparison.OrdinalIgnoreCase));
    }
}
