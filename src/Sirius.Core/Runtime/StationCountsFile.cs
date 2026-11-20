using System.Text.Json;

namespace Sirius.Core.Runtime;

/// <summary>
/// How long a station's inspections take: what a line is sized by, and
/// what a deadline has to allow for.
/// </summary>
/// <param name="Count">Inspections timed.</param>
/// <param name="Total">Their time added up.</param>
/// <param name="Worst">The longest.</param>
/// <param name="Latest">The last one's.</param>
public sealed record StationTiming(long Count, TimeSpan Total, TimeSpan Worst, TimeSpan Latest)
{
    public static readonly StationTiming Zero = new(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>The average, or zero before any.</summary>
    public TimeSpan Average => Count == 0 ? TimeSpan.Zero : Total / Count;

    public StationTiming Add(TimeSpan duration) => new(
        Count + 1,
        Total + duration,
        duration > Worst ? duration : Worst,
        duration);
}

/// <summary>
/// A station's counts and timing, kept beside its recipe - as its line
/// settings are - so a restart carries on from them (F4).
/// </summary>
/// <remarks>
/// Written whole to a file beside it and moved into place, so a power cut
/// mid-write leaves the last counts rather than half of them.
/// </remarks>
public static class StationCountsFile
{
    /// <summary>What is added to the recipe's name: "Top.counts.json".</summary>
    public const string Suffix = ".counts.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>The counts file of the recipe at <paramref name="recipePath"/>.</summary>
    public static string PathFor(string recipePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipePath);

        var folder = Path.GetDirectoryName(recipePath) ?? string.Empty;
        return Path.Combine(folder, Path.GetFileNameWithoutExtension(recipePath) + Suffix);
    }

    /// <summary>The counts and timing kept; zero when there are none or they cannot be read.</summary>
    public static (StationCounts Counts, StationTiming Timing) Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Options) is { } kept)
            {
                var counts = new StationCounts(
                    Math.Max(0, kept.Total),
                    Math.Max(0, kept.Passed),
                    Math.Max(0, kept.Failed),
                    Math.Max(0, kept.Unjudged),
                    Math.Max(0, kept.WithErrors));

                var timing = new StationTiming(
                    Math.Max(0, kept.Timed),
                    TimeSpan.FromMilliseconds(Math.Max(0, kept.TotalMs)),
                    TimeSpan.FromMilliseconds(Math.Max(0, kept.WorstMs)),
                    TimeSpan.FromMilliseconds(Math.Max(0, kept.LatestMs)));

                return (counts, timing);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Counts that cannot be read start again.
        }

        return (StationCounts.Zero, StationTiming.Zero);
    }

    /// <summary>Keeps the counts and timing.</summary>
    /// <returns>Why they could not be kept, or null.</returns>
    public static string? Save(string path, StationCounts counts, StationTiming timing)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

            var document = new Document
            {
                Total = counts.Total,
                Passed = counts.Passed,
                Failed = counts.Failed,
                Unjudged = counts.Unjudged,
                WithErrors = counts.WithErrors,
                Timed = timing.Count,
                TotalMs = timing.Total.TotalMilliseconds,
                WorstMs = timing.Worst.TotalMilliseconds,
                LatestMs = timing.Latest.TotalMilliseconds,
                Saved = DateTime.Now,
            };

            var temporary = path + ".saving";
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options));
            File.Move(temporary, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The counts could not be kept in {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    private sealed class Document
    {
        public long Total { get; set; }

        public long Passed { get; set; }

        public long Failed { get; set; }

        public long Unjudged { get; set; }

        public long WithErrors { get; set; }

        public long Timed { get; set; }

        public double TotalMs { get; set; }

        public double WorstMs { get; set; }

        public double LatestMs { get; set; }

        public DateTime Saved { get; set; }
    }
}
