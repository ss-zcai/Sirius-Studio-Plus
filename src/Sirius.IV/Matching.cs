using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Sirius.IV;

/// <summary>How pictures that belong together are found.</summary>
public enum MatchMode
{
    /// <summary>Each picture on its own.</summary>
    [Description("Off")]
    None,

    /// <summary>Every N pictures down the list, as sorted: for names that already sort a part's pictures together.</summary>
    [Description("In groups of N")]
    Groups,

    /// <summary>Names alike for their first N characters - as the old ICB matched, on 20.</summary>
    [Description("Same start of name")]
    NameStart,

    /// <summary>Names alike in the part a pattern picks out.</summary>
    [Description("Name pattern")]
    Pattern,
}

/// <summary>
/// A rule for matching pictures into sets - two cameras' views of one part,
/// a part lit three ways - which Sirius IV shows side by side and steps
/// through together.
/// </summary>
/// <remarks>
/// <para>
/// Names are compared without their folder or extension, ignoring case, so
/// <c>cam1\part0042.png</c> and <c>cam2\part0042.png</c> - two cameras each
/// saving into a folder of its own - are alike by name.
/// </para>
/// <para>
/// <b>A pattern</b> is a regular expression. The part of the name it finds
/// is what must be alike - or, when it has a group in brackets, the group:
/// <c>^(.+)_cam\d+$</c> matches <c>part0042_cam1</c> with <c>part0042_cam2</c>.
/// A group named <c>key</c> wins over the others. A picture whose name the
/// pattern does not find is a set of its own.
/// </para>
/// <para>
/// Within a set, pictures are in folder-and-name order, so each camera
/// keeps its place across the viewers; sets are in the list's order, by
/// where their first picture comes.
/// </para>
/// </remarks>
public sealed record MatchRule
{
    public const int MaxGroupSize = 9;

    public static readonly MatchRule Off = new();

    [JsonPropertyName("mode")]
    public MatchMode Mode { get; init; }

    /// <summary>How many pictures in each group, for <see cref="MatchMode.Groups"/>.</summary>
    [JsonPropertyName("groupSize")]
    public int GroupSize { get; init; } = 2;

    /// <summary>How many characters of the names must be alike, for <see cref="MatchMode.NameStart"/>.</summary>
    [JsonPropertyName("characters")]
    public int Characters { get; init; } = 20;

    /// <summary>The regular expression, for <see cref="MatchMode.Pattern"/>.</summary>
    [JsonPropertyName("pattern")]
    public string Pattern { get; init; } = string.Empty;

    [JsonIgnore]
    public bool IsOn => Mode != MatchMode.None;

    /// <summary>Why the rule cannot be used, or null.</summary>
    public string? Problem() => Mode switch
    {
        MatchMode.Groups when GroupSize is < 2 or > MaxGroupSize => $"A group is 2 to {MaxGroupSize} pictures.",
        MatchMode.NameStart when Characters < 1 => "At least one character of the names must be alike.",
        MatchMode.Pattern when string.IsNullOrWhiteSpace(Pattern) => "Type a pattern: the part of the name that must be alike.",
        MatchMode.Pattern => PatternProblem(Pattern),
        _ => null,
    };

    /// <summary>"In groups of 3", "First 20 characters alike", "Pattern ^(.+)_cam\d+$".</summary>
    [JsonIgnore]
    public string Summary => Mode switch
    {
        MatchMode.Groups => $"In groups of {GroupSize}",
        MatchMode.NameStart => $"First {Characters} characters of the names alike",
        MatchMode.Pattern => $"Names alike in {Pattern}",
        _ => "Off",
    };

    /// <summary>
    /// The pictures, in the list's order, made into sets. Every picture is in
    /// exactly one; a rule that is off, or cannot be used, makes each its own.
    /// </summary>
    /// <param name="sorted">The pictures, as the list is sorted.</param>
    /// <param name="listed">The folder-and-name order, which orders each set's pictures.</param>
    public List<IReadOnlyList<ImageFileItem>> Sets(IReadOnlyList<ImageFileItem> sorted, IReadOnlyList<ImageFileItem> listed)
    {
        if (!IsOn || Problem() is not null)
            return sorted.Select(item => (IReadOnlyList<ImageFileItem>)[item]).ToList();

        if (Mode == MatchMode.Groups)
            return sorted.Chunk(GroupSize).Select(chunk => (IReadOnlyList<ImageFileItem>)chunk).ToList();

        var place = new Dictionary<ImageFileItem, int>(listed.Count);

        for (var i = 0; i < listed.Count; i++)
            place[listed[i]] = i;

        var regex = Mode == MatchMode.Pattern ? Compile(Pattern) : null;
        var byKey = new Dictionary<string, List<ImageFileItem>>(StringComparer.OrdinalIgnoreCase);
        var sets = new List<List<ImageFileItem>>();

        foreach (var item in sorted)
        {
            if (KeyOf(item, regex) is not { } key)
            {
                sets.Add([item]);
                continue;
            }

            if (!byKey.TryGetValue(key, out var set))
            {
                set = [];
                byKey[key] = set;
                sets.Add(set);
            }

            set.Add(item);
        }

        return sets
            .Select(set => (IReadOnlyList<ImageFileItem>)set.OrderBy(item => place.GetValueOrDefault(item, int.MaxValue)).ToArray())
            .ToList();
    }

    /// <summary>What must be alike in this picture's name; null when the rule finds nothing in it.</summary>
    internal string? KeyOf(ImageFileItem item, Regex? regex = null)
    {
        var name = Path.GetFileNameWithoutExtension(item.Name);

        switch (Mode)
        {
            case MatchMode.NameStart:
                return name.Length <= Characters ? name : name[..Characters];

            case MatchMode.Pattern:
                regex ??= Compile(Pattern);

                Match found;

                try
                {
                    found = regex.Match(name);
                }
                catch (RegexMatchTimeoutException)
                {
                    return null;
                }

                if (!found.Success)
                    return null;

                var group = found.Groups["key"] is { Success: true } named
                    ? named
                    : found.Groups.Count > 1 ? found.Groups[1] : found.Groups[0];

                return group.Success && group.Length > 0 ? group.Value : null;

            default:
                return null;
        }
    }

    private static Regex Compile(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private static string? PatternProblem(string pattern)
    {
        try
        {
            _ = Compile(pattern);
            return null;
        }
        catch (ArgumentException ex)
        {
            return $"The pattern cannot be read: {ex.Message}";
        }
    }

    /// <summary>The layout with room for a set this size: one, two or three side by side, 2 × 2, 3 × 3.</summary>
    public static ViewerLayout LayoutFor(int setSize) => setSize switch
    {
        <= 1 => ViewerLayout.Single,
        2 => ViewerLayout.Two,
        3 => ViewerLayout.Three,
        4 => ViewerLayout.Four,
        _ => ViewerLayout.Nine,
    };
}
