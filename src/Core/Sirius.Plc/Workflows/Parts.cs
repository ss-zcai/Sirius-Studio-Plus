using System.Globalization;
using System.Text.Json;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Plc.Nodes;

namespace Sirius.Plc.Workflows;

/// <summary>How a part went, as the PLC is told it.</summary>
public enum PartVerdict
{
    /// <summary>Every inspection of it passed.</summary>
    Passed,

    /// <summary>An inspection of it failed, or could not judge it.</summary>
    Failed,

    /// <summary>A station gave no result before its deadline: a reject.</summary>
    Late,

    /// <summary>A station could not inspect it - busy or faulted: a reject.</summary>
    NotInspected,
}

/// <summary>
/// A part, as a controller saw it through one pass of its workflow: which
/// part, each inspection of it, and how it went (F25).
/// </summary>
/// <remarks>
/// A part of one picture is a part of one shot, so several shots (F26) add
/// to <see cref="Shots"/> rather than change the record.
/// </remarks>
/// <param name="Number">The controller's own count of parts, from 1, kept over a restart.</param>
/// <param name="Id">
/// The part's ID: a value called Part ID that Gather read from the PLC, or
/// else <paramref name="Number"/>.
/// </param>
/// <param name="Shots">Each inspection of it, in the order taken.</param>
/// <param name="Verdict">How it went.</param>
/// <param name="Started">When the pass that judged it began - the trigger.</param>
/// <param name="Duration">From the trigger to the end of the pass, the answer to the PLC written.</param>
/// <param name="Problem">Why it was late or not inspected; null otherwise.</param>
public sealed record PartRecord(
    long Number,
    string Id,
    IReadOnlyList<InspectionResult> Shots,
    PartVerdict Verdict,
    DateTime Started,
    TimeSpan Duration,
    string? Problem)
{
    /// <summary>
    /// The part's result (F33): the worst its stations gave - or, missed,
    /// what its station gives a late part or one not inspected. Null when
    /// nothing judged it.
    /// </summary>
    public ResultCode? Result { get; init; }

    /// <summary>"REVIEW", "FAIL (late)" - its result, and why when it was missed; else <see cref="VerdictWord"/>.</summary>
    public string Word => Result is not { } result ? VerdictWord
        : Verdict switch
        {
            PartVerdict.Late => $"{result.Name} (late)",
            PartVerdict.NotInspected => $"{result.Name} (not inspected)",
            _ => result.Name,
        };

    /// <summary>The name a part's ID goes to a station under, and to the PLC by Result Value.</summary>
    public const string PartIdName = "Part ID";

    /// <summary>The name of which inspection of the part a picture is, from 1.</summary>
    public const string ShotName = "Shot";

    /// <summary>PASS, FAIL, LATE or NOT INSPECTED.</summary>
    public string VerdictWord => WordFor(Verdict);

    public static string WordFor(PartVerdict verdict) => verdict switch
    {
        PartVerdict.Passed => "PASS",
        PartVerdict.Failed => "FAIL",
        PartVerdict.Late => "LATE",
        _ => "NOT INSPECTED",
    };

    /// <summary>A value of the part called Part ID, ignoring case, spaces and underscores.</summary>
    public static string? IdIn(PartValues? part)
    {
        if (part is null)
            return null;

        foreach (var (name, value) in part)
        {
            if (IsPartIdName(name) && value is not null && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text)
                return text;
        }

        return null;
    }

    /// <summary>Whether <paramref name="name"/> is Part ID however written: PartId, part_id, PART ID.</summary>
    public static bool IsPartIdName(string name) =>
        string.Equals(
            new string(name.Where(c => c is not (' ' or '_' or '-')).ToArray()),
            "PartID",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The verdict of a part that was inspected: missed, or else from its
    /// judged shots - a shot only received (F26) is judged with its set's
    /// last.
    /// </summary>
    internal static PartVerdict Judge(IReadOnlyList<InspectionResult> shots, PartVerdict? missed)
    {
        if (missed is { } verdict)
            return verdict;

        var judged = shots.Where(shot => !shot.Received).ToArray();
        return judged.Length > 0 && judged.All(shot => shot.Passed == true) ? PartVerdict.Passed : PartVerdict.Failed;
    }

    /// <summary>
    /// The result of a part judged from <paramref name="shots"/> - the worst
    /// any gave: a fail over anything not counted over a pass (F33) - or
    /// <paramref name="missed"/>'s when a station gave none in time. None
    /// when a shot was judged without one: a recipe that judges nothing.
    /// </summary>
    internal static ResultCode? ResultOf(IReadOnlyList<InspectionResult> shots, PartVerdict? missed, ResultCode? missedResult)
    {
        if (missed is not null)
            return missedResult;

        var judged = shots.Where(shot => !shot.Received).ToArray();
        return judged.Length == 0 || judged.Any(shot => shot.ResultCode is null)
            ? null
            : ResultCode.Worst(judged.Select(shot => shot.ResultCode));
    }
}

/// <summary>
/// A controller's parts: how many, and how they went. What a line is judged
/// by - beside, or instead of, each station's count of pictures.
/// </summary>
/// <remarks>
/// Late and not inspected are counted by what happened, whatever result
/// the line gives such a part. A part whose result is not counted (F33) -
/// NoLens, nothing to inspect - is counted in <see cref="NotCounted"/> and
/// left out of the yield; judged, it is not among the failed.
/// </remarks>
public sealed record PartCounts(long Total, long Passed, long Failed, long Late, long NotInspected)
{
    public static readonly PartCounts Zero = new(0, 0, 0, 0, 0);

    /// <summary>Given a result that is not counted: out of the yield.</summary>
    public long NotCounted { get; init; }

    /// <summary>Every part not accepted: failed, late, not inspected, and those given a result not counted.</summary>
    public long Rejected => Total - Passed;

    /// <summary>Passed as a percentage of the parts that count: all, less those given a result not counted.</summary>
    public double Yield => Total - NotCounted <= 0 ? 0 : 100.0 * Passed / (Total - NotCounted);

    internal PartCounts Add(PartVerdict verdict) => Add(verdict, null);

    internal PartCounts Add(PartVerdict verdict, ResultCode? result)
    {
        var notCounted = result?.CountsAs == ResultCounting.NotCounted;
        var passed = verdict == PartVerdict.Passed;

        return this with
        {
            Total = Total + 1,
            Passed = Passed + (passed ? 1 : 0),
            Failed = Failed + (verdict == PartVerdict.Failed && !passed && !notCounted ? 1 : 0),
            Late = Late + (verdict == PartVerdict.Late ? 1 : 0),
            NotInspected = NotInspected + (verdict == PartVerdict.NotInspected ? 1 : 0),
            NotCounted = NotCounted + (notCounted ? 1 : 0),
        };
    }
}

/// <summary>
/// A controller's part counts, and the number its next part takes, kept
/// beside its workflow so a restart carries on from them (F4).
/// </summary>
/// <remarks>
/// Written whole to a file beside it and moved into place, so a power cut
/// mid-write leaves the last counts rather than half of them.
/// </remarks>
public static class PartCountsFile
{
    /// <summary>What is added to the workflow's name: "Line 1 PLC.counts.json".</summary>
    public const string Suffix = ".counts.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>The counts file of the workflow at <paramref name="workflowPath"/>.</summary>
    public static string PathFor(string workflowPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowPath);

        var folder = Path.GetDirectoryName(workflowPath) ?? string.Empty;
        return Path.Combine(folder, Path.GetFileNameWithoutExtension(workflowPath) + Suffix);
    }

    /// <summary>The counts kept, and the next part's number; zero and 1 when there are none or they cannot be read.</summary>
    public static (PartCounts Counts, long NextPart) Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Options) is { } kept)
            {
                var counts = new PartCounts(
                    Math.Max(0, kept.Total),
                    Math.Max(0, kept.Passed),
                    Math.Max(0, kept.Failed),
                    Math.Max(0, kept.Late),
                    Math.Max(0, kept.NotInspected))
                {
                    NotCounted = Math.Max(0, kept.NotCounted),
                };

                return (counts, Math.Max(1, kept.NextPart));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Counts that cannot be read start again; the parts themselves are in the records.
        }

        return (PartCounts.Zero, 1);
    }

    /// <summary>How many parts had each result (F33); none when there are none or they cannot be read.</summary>
    public static ResultTally LoadResults(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Options) is { } kept)
                return ResultTally.Of(kept.Results);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Counts that cannot be read start again.
        }

        return ResultTally.Empty;
    }

    /// <summary>Keeps the counts, how many parts had each result, and the next part's number.</summary>
    /// <returns>Why they could not be kept, or null.</returns>
    public static string? Save(string path, PartCounts counts, long nextPart, ResultTally? results = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

            var document = new Document
            {
                Total = counts.Total,
                Passed = counts.Passed,
                Failed = counts.Failed,
                Late = counts.Late,
                NotInspected = counts.NotInspected,
                NotCounted = counts.NotCounted,
                Results = results is null || results.Counts.Count == 0 ? null : results.Counts.ToDictionary(StringComparer.OrdinalIgnoreCase),
                NextPart = nextPart,
                Saved = DateTime.Now,
            };

            var temporary = path + ".saving";
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options));
            File.Move(temporary, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The part counts could not be kept in {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    private sealed class Document
    {
        public long Total { get; set; }

        public long Passed { get; set; }

        public long Failed { get; set; }

        public long Late { get; set; }

        public long NotInspected { get; set; }

        public long NotCounted { get; set; }

        /// <summary>How many parts had each result, by name (F33).</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, long>? Results { get; set; }

        public long NextPart { get; set; } = 1;

        public DateTime Saved { get; set; }
    }
}
