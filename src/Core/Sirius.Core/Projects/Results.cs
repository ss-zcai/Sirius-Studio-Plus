using System.Collections;
using System.Globalization;
using Sirius.Core.Graph;

namespace Sirius.Core.Projects;

/// <summary>What a result counts as in a station's tally and yield (F33).</summary>
public enum ResultCounting
{
    /// <summary>An accepted part: counted, and a pass in the yield.</summary>
    Pass,

    /// <summary>A rejected part: counted, and a fail in the yield.</summary>
    Fail,

    /// <summary>
    /// Counted on its own, out of the yield: no product - a pick-up problem,
    /// nothing in front of the camera - or a part still to be decided.
    /// </summary>
    [System.ComponentModel.Description("Not counted")]
    NotCounted,
}

/// <summary>
/// One result a station can give a part (F33): PASS, FAIL, REVIEW, NoLens.
/// What each one means for the part is the PLC's business; the station
/// gives the name, the colour it is shown in, the code the PLC is sent, and
/// how it counts.
/// </summary>
/// <param name="Name">What it is called - in pictures' names, on screens, to a workflow.</param>
/// <param name="Code">The number the PLC is sent for it.</param>
/// <param name="CountsAs">Pass, Fail or Not counted.</param>
/// <param name="Colour">How it is shown: "#RRGGBB".</param>
public sealed record ResultCode(string Name, int Code, ResultCounting CountsAs, string Colour)
{
    /// <summary>Its place in its list, from 0: later is worse among results that count alike.</summary>
    public int Rank { get; init; }

    /// <summary>Whether a part given it is accepted.</summary>
    public bool Accepts => CountsAs == ResultCounting.Pass;

    /// <summary>
    /// Whether this is worse than <paramref name="than"/>: a fail is worse
    /// than anything not counted, which is worse than a pass; between two
    /// that count alike, the one further down its list.
    /// </summary>
    public bool IsWorseThan(ResultCode than)
    {
        ArgumentNullException.ThrowIfNull(than);

        var tier = Tier(CountsAs);
        var other = Tier(than.CountsAs);
        return tier != other ? tier > other : Rank > than.Rank;
    }

    /// <summary>The worst of <paramref name="results"/>, the first on a tie; null when there are none.</summary>
    public static ResultCode? Worst(IEnumerable<ResultCode?> results)
    {
        ResultCode? worst = null;

        foreach (var result in results)
        {
            if (result is not null && (worst is null || result.IsWorseThan(worst)))
                worst = result;
        }

        return worst;
    }

    private static int Tier(ResultCounting counting) => counting switch
    {
        ResultCounting.Fail => 2,
        ResultCounting.NotCounted => 1,
        _ => 0,
    };

    public override string ToString() => Name;
}

/// <summary>
/// The results a station gives its parts (F33), best first, and which of
/// them a part gets when the station gives none in time.
/// </summary>
/// <remarks>
/// <para>
/// <b>As many as the line needs.</b> Most lines are PASS and FAIL - which is
/// what a station without a list of its own has, <see cref="Default"/>, so a
/// project made before result codes runs as it did. Others add REVIEW, a
/// second chance, or NoLens, nothing to inspect.
/// </para>
/// <para>
/// <b>From the recipe.</b> A recipe already grades its part (the Grade and
/// Worst Grade nodes): a grade with the name of a result is that result.
/// Otherwise a part the recipe passed is the list's first Pass result, and
/// one it failed the first Fail result. An inspection a node went wrong in
/// without failing is ERROR, if the list has one; a recipe that judges
/// nothing gives no result.
/// </para>
/// <para>
/// <b>Missed.</b> A station past its deadline, or one that could not
/// inspect - busy or faulted - gives the part <see cref="Late"/> or
/// <see cref="NotInspected"/>: FAIL, or REVIEW where the line chooses.
/// </para>
/// </remarks>
public sealed class ResultList : IEquatable<ResultList>, IReadOnlyList<ResultCode>
{
    /// <summary>The result an inspection a node went wrong in takes, when the list has it.</summary>
    public const string ErrorName = "ERROR";

    /// <summary>The longest a result's name may be.</summary>
    public const int MaxNameLength = 16;

    /// <summary>The most results a list may hold.</summary>
    public const int MostResults = 32;

    /// <summary>Words the line already uses for something else.</summary>
    private static readonly string[] Reserved = ["NONE", "RECEIVED", "LATE"];

    private readonly ResultCode[] _results;

    /// <param name="results">The results, best first; each is given its <see cref="ResultCode.Rank"/> here.</param>
    /// <param name="late">Which a part gets when the station is past its deadline; null for the first Fail result.</param>
    /// <param name="notInspected">Which a part gets when the station could not inspect it; null for the first Fail result.</param>
    public ResultList(IEnumerable<ResultCode> results, string? late = null, string? notInspected = null)
    {
        ArgumentNullException.ThrowIfNull(results);

        _results = results.Select((result, rank) => result with { Name = result.Name.Trim(), Rank = rank }).ToArray();
        Late = Blank(late);
        NotInspected = Blank(notInspected);
    }

    /// <summary>PASS and FAIL: what a station without a list of its own gives.</summary>
    public static ResultList Default { get; } = new(
    [
        new ResultCode("PASS", 1, ResultCounting.Pass, "#3FB950"),
        new ResultCode("FAIL", 2, ResultCounting.Fail, "#DB5C5C"),
    ]);

    /// <summary>The colours a new result is offered in, in turn.</summary>
    public static IReadOnlyList<string> Colours { get; } =
        ["#3FB950", "#DB5C5C", "#E3B341", "#58A6FF", "#A371F7", "#8B949E", "#F0883E", "#39C5CF"];

    /// <summary>Which result a part past the station's deadline gets, by name; null for the first Fail result.</summary>
    public string? Late { get; }

    /// <summary>Which result a part the station could not inspect gets, by name; null for the first Fail result.</summary>
    public string? NotInspected { get; }

    /// <summary>Whether this is PASS and FAIL as a station without a list has them.</summary>
    public bool IsDefault => Equals(Default);

    public int Count => _results.Length;

    public ResultCode this[int index] => _results[index];

    /// <summary>The result called <paramref name="name"/>, ignoring case; null when there is none.</summary>
    public ResultCode? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var wanted = name.Trim();
        return _results.FirstOrDefault(result => string.Equals(result.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The first result counting as a pass - what a part the recipe passed gets.</summary>
    public ResultCode? FirstPass => _results.FirstOrDefault(result => result.CountsAs == ResultCounting.Pass);

    /// <summary>The first result counting as a fail - what a part the recipe failed gets.</summary>
    public ResultCode? FirstFail => _results.FirstOrDefault(result => result.CountsAs == ResultCounting.Fail);

    /// <summary>What a part past the station's deadline gets.</summary>
    public ResultCode? LateResult => Find(Late) ?? FirstFail;

    /// <summary>What a part the station could not inspect gets.</summary>
    public ResultCode? NotInspectedResult => Find(NotInspected) ?? FirstFail;

    /// <summary>
    /// The result of an inspection the recipe judged <paramref name="verdict"/>,
    /// with <paramref name="nodeErrors"/> nodes gone wrong; null when nothing
    /// in the recipe judges.
    /// </summary>
    public ResultCode? For(Verdict verdict, int nodeErrors)
    {
        if (nodeErrors > 0 && verdict.Passed != false && Find(ErrorName) is { } error)
            return error;

        if (verdict.Grade is { } grade && Find(grade.Name) is { } graded)
            return graded;

        return verdict.Passed switch
        {
            true => FirstPass,
            false => FirstFail,
            null => null,
        };
    }

    /// <summary>
    /// Where the list and its recipe's <paramref name="grades"/> do not meet,
    /// or null: a grade no result is called, whose part is given the first
    /// pass or the first fail; a result no grade is called, never given -
    /// unless it is ERROR, the first pass or fail, or a missed part's.
    /// Results are found by name, so renaming one alone loses it.
    /// </summary>
    public string? GradeMismatch(IEnumerable<string> grades)
    {
        // PASS and FAIL alone: every grade is a pass or a fail, as meant.
        if (IsDefault)
            return null;

        var named = grades
            .Select(Blank)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var lines = new List<string>();

        var orphans = named.Where(grade => Find(grade) is null).ToArray();
        if (orphans.Length > 0)
        {
            lines.Add($"The recipe grades {Join(orphans)}, which no result is called: " +
                      $"{(orphans.Length == 1 ? "such a part is" : "those parts are")} given " +
                      $"{FirstPass?.Name ?? "the first pass"} or {FirstFail?.Name ?? "the first fail"} as the recipe passes or fails it.");
        }

        var given = new HashSet<ResultCode?>([FirstPass, FirstFail, LateResult, NotInspectedResult, Find(ErrorName)]);
        var unused = _results
            .Where(result => !given.Contains(result) && !named.Contains(result.Name, StringComparer.OrdinalIgnoreCase))
            .Select(result => result.Name)
            .ToArray();

        if (unused.Length > 0)
        {
            lines.Add(named.Length == 0
                ? $"The recipe gives no grades, so {Join(unused)} {(unused.Length == 1 ? "is" : "are")} never given."
                : $"No grade in the recipe is called {Join(unused, " or ")}, so {(unused.Length == 1 ? "it is" : "they are")} never given.");
        }

        return lines.Count == 0 ? null : string.Join(" ", lines);

        static string Join(IReadOnlyList<string> names, string last = " and ") =>
            names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + last + names[^1];
    }

    /// <summary>The same results, with the ones a missed part gets chosen again.</summary>
    public ResultList WithMissed(string? late, string? notInspected) => new(_results, late, notInspected);

    /// <summary>Why this list cannot be a station's, or null.</summary>
    public string? Problem()
    {
        if (_results.Length == 0)
            return "A station needs results: at least one that counts as a pass and one as a fail.";

        if (_results.Length > MostResults)
            return $"A station has at most {MostResults} results.";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codes = new HashSet<int>();

        foreach (var result in _results)
        {
            if (NameProblem(result.Name) is { } nameProblem)
                return nameProblem;

            if (!seen.Add(result.Name))
                return $"There are two results called {result.Name}.";

            if (!codes.Add(result.Code))
                return $"Two results have the code {result.Code.ToString(CultureInfo.InvariantCulture)}: the PLC could not tell them apart.";

            if (result.Code < 0)
                return $"{result.Name}'s code is below 0.";

            if (ColourProblem(result.Colour) is { } colourProblem)
                return $"{result.Name}: {colourProblem}";
        }

        if (FirstPass is null)
            return "At least one result must count as a pass.";

        if (FirstFail is null)
            return "At least one result must count as a fail.";

        if (Late is not null && Find(Late) is null)
            return $"A late part is to be {Late}, which is not a result.";

        if (LateResult is { Accepts: true })
            return $"A late part cannot be {LateResult.Name}: it is a reject, so not a result that counts as a pass.";

        if (NotInspected is not null && Find(NotInspected) is null)
            return $"A part not inspected is to be {NotInspected}, which is not a result.";

        if (NotInspectedResult is { Accepts: true })
            return $"A part not inspected cannot be {NotInspectedResult.Name}: it is a reject, so not a result that counts as a pass.";

        return null;
    }

    /// <summary>
    /// Why <paramref name="name"/> cannot be a result's, or null: it goes into
    /// pictures' names, so letters, digits and '-' only.
    /// </summary>
    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "A result needs a name.";

        var trimmed = name.Trim();

        if (trimmed.Length > MaxNameLength)
            return $"{trimmed} is too long: a result's name has at most {MaxNameLength} letters.";

        if (!trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            return $"{trimmed}: a result's name has letters, digits and '-' only - it goes into the pictures' names.";

        if (Reserved.Any(word => string.Equals(word, trimmed, StringComparison.OrdinalIgnoreCase)))
            return $"{trimmed} is a word the line already uses; choose another name.";

        return null;
    }

    /// <summary>Why <paramref name="colour"/> is not "#RRGGBB", or null.</summary>
    public static string? ColourProblem(string? colour) =>
        colour is { Length: 7 } && colour[0] == '#' && colour.Skip(1).All(char.IsAsciiHexDigit)
            ? null
            : "a colour is written #RRGGBB.";

    /// <summary>"PASS, FAIL, REVIEW" - for a station's line in Builder.</summary>
    public string Summary => string.Join(", ", _results.Select(result => result.Name));

    public bool Equals(ResultList? other) =>
        other is not null
        && string.Equals(Late, other.Late, StringComparison.OrdinalIgnoreCase)
        && string.Equals(NotInspected, other.NotInspected, StringComparison.OrdinalIgnoreCase)
        && _results.SequenceEqual(other._results);

    public override bool Equals(object? obj) => Equals(obj as ResultList);

    public override int GetHashCode() => HashCode.Combine(_results.Length, _results.FirstOrDefault()?.Name);

    public IEnumerator<ResultCode> GetEnumerator() => ((IEnumerable<ResultCode>)_results).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => Summary;

    private static string? Blank(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();
}

/// <summary>
/// How many of each result, by name, ignoring case: a station's
/// inspections, or a controller's parts (F33). Unchanging: adding gives a new
/// tally.
/// </summary>
public sealed class ResultTally : IEquatable<ResultTally>
{
    private readonly SortedDictionary<string, long> _counts;

    private ResultTally(SortedDictionary<string, long> counts)
    {
        _counts = counts;
    }

    public static ResultTally Empty { get; } = new(new SortedDictionary<string, long>(StringComparer.OrdinalIgnoreCase));

    /// <summary>A tally of these counts; those below 1, and blank names, left out.</summary>
    public static ResultTally Of(IEnumerable<KeyValuePair<string, long>>? counts)
    {
        var kept = new SortedDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, count) in counts ?? [])
        {
            if (!string.IsNullOrWhiteSpace(name) && count > 0)
                kept[name.Trim()] = kept.GetValueOrDefault(name.Trim()) + count;
        }

        return kept.Count == 0 ? Empty : new ResultTally(kept);
    }

    /// <summary>The results counted and how many of each, by name.</summary>
    public IReadOnlyDictionary<string, long> Counts => _counts;

    /// <summary>How many of <paramref name="name"/>; 0 for none.</summary>
    public long this[string name] => _counts.GetValueOrDefault(name);

    /// <summary>One more <paramref name="result"/>; the same tally for none.</summary>
    public ResultTally Add(ResultCode? result)
    {
        if (result is null)
            return this;

        var counts = new SortedDictionary<string, long>(_counts, StringComparer.OrdinalIgnoreCase);
        counts[result.Name] = counts.GetValueOrDefault(result.Name) + 1;
        return new ResultTally(counts);
    }

    public bool Equals(ResultTally? other) =>
        other is not null && _counts.Count == other._counts.Count
        && _counts.All(pair => other._counts.TryGetValue(pair.Key, out var count) && count == pair.Value);

    public override bool Equals(object? obj) => Equals(obj as ResultTally);

    public override int GetHashCode() => _counts.Count;

    /// <summary>"FAIL 3, PASS 12".</summary>
    public override string ToString() => string.Join(", ", _counts.Select(pair => $"{pair.Key} {pair.Value}"));
}
