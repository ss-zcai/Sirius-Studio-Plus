using System.Windows.Media;
using NodeGraphKit.Model;

namespace NodeGraphKit.ViewModels;

/// <summary>What one <see cref="NodeAccent"/> looks like on the dark theme.</summary>
/// <remarks>
/// Three colours, not two: a background and the text on it have to be chosen
/// together or one of them loses.
/// </remarks>
public sealed class NodeAccentOption
{
    public NodeAccentOption(NodeAccent accent, string name, string background, string border, string text, string description)
    {
        Accent = accent;
        Name = name;
        Description = description;
        Background = Frozen(background);
        Border = Frozen(border);
        Text = Frozen(text);
    }

    public NodeAccent Accent { get; }

    public string Name { get; }

    /// <summary>What it is for, shown as a tooltip.</summary>
    public string Description { get; }

    public Brush Background { get; }

    public Brush Border { get; }

    public Brush Text { get; }

    public override string ToString() => Name;

    internal static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}

/// <summary>The colourings a node can be given.</summary>
/// <remarks>
/// <para>
/// One rule: <b>the background stays dark and the border carries the
/// colour.</b> A saturated fill fights the near-white title on it, and two
/// marked nodes shout louder than none. A dark tinted fill with a vivid
/// border reads as colour from across a fifty-node graph while the title
/// stays as legible as before.
/// </para>
/// <para>
/// Every text-on-background pair clears 7:1 contrast, and every border clears
/// 3:1 against its own fill. Default and Muted keep the theme's quiet border.
/// </para>
/// </remarks>
public static class NodeAccents
{
    public static IReadOnlyList<NodeAccentOption> All { get; } =
    [
        new(NodeAccent.Default, "Default", "#32343A", "#4A4D55", "#E6E6E6", "The ordinary node"),
        new(NodeAccent.Muted, "Muted", "#26282C", "#3A3D44", "#B2B8BE", "Dimmed - settled, or kept only for comparison"),
        new(NodeAccent.Blue, "Blue", "#2A3546", "#5B8DD6", "#DCE8F7", "Grouping, no fixed meaning"),
        new(NodeAccent.Teal, "Teal", "#1E3A40", "#2BB3C9", "#D9F2F7", "Grouping, no fixed meaning"),
        new(NodeAccent.Green, "Green", "#2A3A24", "#6DBF4A", "#E7F3DC", "Known good - checked and working"),
        new(NodeAccent.Amber, "Amber", "#3E3420", "#D9A33A", "#F7EBD4", "Needs attention - being worked on"),
        new(NodeAccent.Red, "Red", "#42292A", "#DB5C5C", "#F8E2E2", "The problem, or the one not to touch"),
        new(NodeAccent.Purple, "Purple", "#352D45", "#A378D6", "#EEE4F8", "Grouping, no fixed meaning"),
    ];

    /// <summary>The styling for one accent, falling back to the default rather than throwing.</summary>
    public static NodeAccentOption Of(NodeAccent accent) =>
        All.FirstOrDefault(option => option.Accent == accent) ?? All[0];
}

/// <summary>One accent as offered for one node, knowing whether it is the one in use.</summary>
public sealed record NodeAccentChoice(NodeAccentOption Option, bool IsCurrent)
{
    public NodeAccent Accent => Option.Accent;

    public string Name => Option.Name;

    public string Description => Option.Description;

    public Brush Background => Option.Background;

    public Brush Border => Option.Border;
}
