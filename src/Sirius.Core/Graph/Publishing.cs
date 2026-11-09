using System.Globalization;

namespace Sirius.Core.Graph;

/// <summary>What a published name stands for.</summary>
public enum PublishedKind
{
    /// <summary>An output the outside world reads - a count, a diameter, a code read.</summary>
    Result,

    /// <summary>A setting that may be changed on the line.</summary>
    Setting,

    /// <summary>An image viewer: its picture and everything drawn on it.</summary>
    View,
}

/// <summary>
/// A name a recipe gives one of its outputs, settings or viewers, by which a
/// screen finds it.
/// </summary>
/// <param name="Kind">What the name stands for.</param>
/// <param name="Member">
/// The output's port name for a result, the setting's property name for a
/// setting, and empty for a view - which is the node itself.
/// </param>
/// <param name="Name">The name, unique in the recipe.</param>
public sealed record PublishedName(PublishedKind Kind, string Member, string Name);

/// <summary>One published name in a recipe, and the node it is on.</summary>
/// <param name="Node">The node carrying it.</param>
/// <param name="Entry">The name.</param>
/// <param name="Found">
/// Whether the node still has the output or setting it names. A growing
/// node's spare socket can go, and a custom node's setting can be renamed by
/// its library.
/// </param>
public sealed record PublishedItem(VisionNode Node, PublishedName Entry, bool Found)
{
    public string Name => Entry.Name;

    public PublishedKind Kind => Entry.Kind;

    /// <summary>"Measure 1 › Distance", or "Image Viewer 1" for a view.</summary>
    public string Source
    {
        get
        {
            if (Entry.Kind == PublishedKind.View)
                return Node.Name;

            var member = Entry.Kind == PublishedKind.Result
                ? Node.FindOutput(Entry.Member)?.DisplayName
                : Node.Descriptor.Parameters.FirstOrDefault(p => p.Name == Entry.Member)?.DisplayName;

            return $"{Node.Name} › {member ?? Entry.Member}";
        }
    }
}

/// <summary>
/// The names a recipe publishes, for screens to bind to.
/// </summary>
/// <remarks>
/// <para>
/// A screen finds what it shows by name, never by node: a recipe can be
/// reworked - a node deleted and another put in its place - and the screen
/// still works once the new output is given the old name. A name no longer
/// found is something the project can report when it opens, rather than a
/// readout that quietly goes blank on the line.
/// </para>
/// <para>
/// The names are kept on the nodes (<see cref="VisionNode.Published"/>), as
/// colours and measurements are, so saving, undo and deleting a node need
/// nothing of their own. Names are unique across the whole recipe and every
/// kind, ignoring case, so "station › name" always means one thing.
/// </para>
/// <para>
/// Only the recipe's own graph publishes; a sub-graph's nodes do not, until
/// promoted parameters (backlog F5) say how a name inside one is reached.
/// </para>
/// </remarks>
public static class Publishing
{
    /// <summary>The longest name allowed.</summary>
    public const int MaxLength = 64;

    /// <summary>Every published name in <paramref name="graph"/>, in node order.</summary>
    public static IReadOnlyList<PublishedItem> In(VisionGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        return graph.Nodes
            .SelectMany(node => node.Published.Select(entry => new PublishedItem(node, entry, Exists(node, entry))))
            .ToArray();
    }

    /// <summary>The item published under <paramref name="name"/>, ignoring case, or null.</summary>
    public static PublishedItem? Find(VisionGraph graph, string name) =>
        In(graph).FirstOrDefault(item => Same(item.Name, name));

    /// <summary>The name <paramref name="node"/> gives one of its members, or null.</summary>
    public static PublishedName? NameOf(VisionNode node, PublishedKind kind, string member)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node.Published.FirstOrDefault(entry => entry.Kind == kind && entry.Member == member);
    }

    /// <summary>
    /// Why <paramref name="name"/> cannot be used for a member of
    /// <paramref name="node"/>, or null when it can.
    /// </summary>
    /// <param name="kind">What it would stand for - its own current name is not a clash.</param>
    /// <param name="member">Which output or setting.</param>
    public static string? Problem(VisionGraph graph, string? name, VisionNode node, PublishedKind kind, string member)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);

        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            return "A name is needed.";

        if (trimmed.Length > MaxLength)
            return $"A name can be at most {MaxLength} characters.";

        if (trimmed.Any(char.IsControl) || trimmed.Contains('›'))
            return "A name cannot contain '›' or control characters.";

        var clash = In(graph).FirstOrDefault(item =>
            Same(item.Name, trimmed) &&
            !(ReferenceEquals(item.Node, node) && item.Kind == kind && item.Entry.Member == member));

        return clash is null ? null : $"'{clash.Name}' is already the name of {clash.Source}.";
    }

    /// <summary>
    /// <paramref name="wanted"/>, or "<paramref name="wanted"/> 2", "3"... -
    /// the first not already taken in <paramref name="graph"/>.
    /// </summary>
    public static string Suggest(VisionGraph graph, string wanted)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var stem = string.IsNullOrWhiteSpace(wanted) ? "Value" : wanted.Trim();

        if (stem.Length > MaxLength - 4)
            stem = stem[..(MaxLength - 4)].TrimEnd();

        var taken = In(graph).Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(stem))
            return stem;

        for (var i = 2; ; i++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{stem} {i}");

            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Gives a member of <paramref name="node"/> a published name, replacing
    /// any it had. Throws when <see cref="Problem"/> would object.
    /// </summary>
    public static void Publish(VisionGraph graph, VisionNode node, PublishedKind kind, string member, string name)
    {
        if (Problem(graph, name, node, kind, member) is { } problem)
            throw new ArgumentException(problem, nameof(name));

        var entry = new PublishedName(kind, member, name.Trim());

        node.Published =
        [
            .. node.Published.Where(existing => !(existing.Kind == kind && existing.Member == member)),
            entry,
        ];
    }

    /// <summary>Takes a member's published name off it, if it has one.</summary>
    public static void Unpublish(VisionNode node, PublishedKind kind, string member)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (NameOf(node, kind, member) is null)
            return;

        node.Published = node.Published.Where(existing => !(existing.Kind == kind && existing.Member == member)).ToArray();
    }

    internal static bool Exists(VisionNode node, PublishedName entry) => entry.Kind switch
    {
        PublishedKind.Result => node.FindOutput(entry.Member) is not null,
        PublishedKind.Setting => node.Descriptor.Parameters.Any(p => p.Name == entry.Member),
        _ => true,
    };

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
