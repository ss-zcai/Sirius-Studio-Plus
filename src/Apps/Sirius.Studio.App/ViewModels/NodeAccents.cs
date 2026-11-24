using System.Windows.Media;
using Sirius.Core.Graph;

namespace Sirius.Studio.App.ViewModels;

/// <summary>
/// What one <see cref="NodeAccent"/> looks like on this theme.
/// </summary>
/// <remarks>
/// Three colours, not two. Background and stroke alone leave the title in
/// whatever the theme last set, and the whole reason for a fixed set rather
/// than a pair of colour pickers is that a background and the text on it
/// have to be chosen together or one of them loses.
/// </remarks>
public sealed class NodeAccentOption
{
    public NodeAccentOption(
        NodeAccent accent,
        string name,
        string background,
        string border,
        string text,
        string description)
    {
        Accent = accent;
        Name = name;
        BackgroundHex = background;
        BorderHex = border;
        TextHex = text;
        Description = description;

        Background = Frozen(background);
        Border = Frozen(border);
        Text = Frozen(text);
    }

    public NodeAccent Accent { get; }

    public string Name { get; }

    /// <summary>What it is for, shown as a tooltip on the swatch.</summary>
    public string Description { get; }

    public string BackgroundHex { get; }

    public string BorderHex { get; }

    public string TextHex { get; }

    public Brush Background { get; }

    public Brush Border { get; }

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
/// The colourings a node can be given, and what each one looks like.
/// </summary>
/// <remarks>
/// <para>
/// Built to one rule: <b>the background stays dark and the stroke carries
/// the colour.</b> On a dark canvas a saturated fill is what makes a node
/// unreadable - it fights the near-white title sitting on it and it shouts
/// loudly enough that two marked nodes are worse than none. A dark tinted
/// fill with a vivid border reads as colour from across a fifty-node graph
/// while the title stays as legible as it was, which is the whole point:
/// marking a node must not cost anything to look at it.
/// </para>
/// <para>
/// Every text-on-background pair here clears 7:1, and every accent's border
/// clears 3:1 against its own fill. <c>NodeAccentTests</c> does that
/// arithmetic, so a later hand-picked colour cannot quietly fall below it.
/// Default and Muted keep the theme's own quiet border - Default has to look
/// exactly as it did before accents existed, and Muted is dimmed on purpose.
/// </para>
/// <para>
/// Seven choices plus the default. Fewer would not cover "attention",
/// "good", "bad" and a couple of neutral groupings; more and they stop
/// being distinguishable at canvas zoom, which would defeat the purpose.
/// </para>
/// </remarks>
public static class NodeAccents
{
    public static IReadOnlyList<NodeAccentOption> All { get; } =
    [
        new(NodeAccent.Default, "Default", "#32343A", "#4A4D55", "#E6E6E6",
            "The ordinary node"),

        new(NodeAccent.Muted, "Muted", "#26282C", "#3A3D44", "#B2B8BE",
            "Dimmed - settled, or kept only for comparison"),

        new(NodeAccent.Blue, "Blue", "#2A3546", "#5B8DD6", "#DCE8F7",
            "Grouping, no fixed meaning"),

        // Pushed to a true cyan, and Green to a yellower green: the first
        // pair tried were a measured 60 apart in RGB and read as the same
        // colour at canvas zoom, which would have given the operator two
        // markers that mean different things and look alike.
        new(NodeAccent.Teal, "Teal", "#1E3A40", "#2BB3C9", "#D9F2F7",
            "Grouping, no fixed meaning"),

        new(NodeAccent.Green, "Green", "#2A3A24", "#6DBF4A", "#E7F3DC",
            "Known good - checked and working"),

        new(NodeAccent.Amber, "Amber", "#3E3420", "#D9A33A", "#F7EBD4",
            "Needs attention - being worked on"),

        new(NodeAccent.Red, "Red", "#42292A", "#DB5C5C", "#F8E2E2",
            "The problem, or the one not to touch"),

        new(NodeAccent.Purple, "Purple", "#352D45", "#A378D6", "#EEE4F8",
            "Grouping, no fixed meaning"),
    ];

    /// <summary>
    /// The styling for one accent, falling back to the default.
    /// </summary>
    /// <remarks>
    /// Never throws. A recipe can name an accent a build does not have, and
    /// a node drawn in the wrong colour beats a canvas that will not draw.
    /// </remarks>
    public static NodeAccentOption Of(NodeAccent accent) =>
        All.FirstOrDefault(option => option.Accent == accent) ?? All[0];
}

/// <summary>
/// One accent as offered to the operator, knowing whether it is the one in
/// use.
/// </summary>
/// <remarks>
/// A view-model rather than the option itself, because the same eight
/// options are offered for every node and only the mark differs. Baking
/// "this one is current" into the shared option would make the last node
/// looked at decide what every other node's swatches showed.
/// </remarks>
public sealed record NodeAccentChoice(NodeAccentOption Option, bool IsCurrent)
{
    public NodeAccent Accent => Option.Accent;

    public string Name => Option.Name;

    public string Description => Option.Description;

    public Brush Background => Option.Background;

    public Brush Border => Option.Border;
}
