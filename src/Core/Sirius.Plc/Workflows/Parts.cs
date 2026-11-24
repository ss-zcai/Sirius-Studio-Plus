using System.Globalization;
using System.Text.Json;
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
}

/// <summary>
/// A controller's parts: how many, and how they went. What a line is judged
/// by - beside, or instead of, each station's count of pictures.
/// </summary>
public sealed record PartCounts(long Total, long Passed, long Failed, long Late, long NotInspected)
{
    public static readonly PartCounts Zero = new(0, 0, 0, 0, 0);

    /// <summary>Every reject: failed, late and not inspected.</summary>
    public long Rejected => Failed + Late + NotInspected;

    /// <summary>Passed as a percentage of all parts.</summary>
    public double Yield => Total == 0 ? 0 : 100.0 * Passed / Total;

    internal PartCounts Add(PartVerdict verdict) => this with
    {
        Total = Total + 1,
        Passed = Passed + (verdict == PartVerdict.Passed ? 1 : 0),
        Failed = Failed + (verdict == PartVerdict.Failed ? 1 : 0),
        Late = Late + (verdict == PartVerdict.Late ? 1 : 0),
        NotInspected = NotInspected + (verdict == PartVerdict.NotInspected ? 1 : 0),
    };
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
                    Math.Max(0, kept.NotInspected));

                return (counts, Math.Max(1, kept.NextPart));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Counts that cannot be read start again; the parts themselves are in the records.
        }

        return (PartCounts.Zero, 1);
    }

    /// <summary>Keeps the counts and the next part's number.</summary>
    /// <returns>Why they could not be kept, or null.</returns>
    public static string? Save(string path, PartCounts counts, long nextPart)
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

        public long NextPart { get; set; } = 1;

        public DateTime Saved { get; set; }
    }
}
