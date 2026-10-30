using System.Diagnostics;
using System.Globalization;
using System.Text;
using Sirius.Core.Nodes;

namespace Sirius.Core.Graph;

/// <summary>One image of a folder run, and what the recipe made of it.</summary>
/// <param name="Index">Its place in the folder, counting from one.</param>
/// <param name="Of">How many images the folder holds.</param>
/// <param name="Name">Its file name.</param>
/// <param name="Verdict">What the recipe decided.</param>
/// <param name="NodeErrors">How many nodes failed on it.</param>
public sealed record FolderRunItem(int Index, int Of, string? Name, Verdict Verdict, int NodeErrors);

/// <summary>A folder run, added up.</summary>
public sealed class FolderRunSummary
{
    private readonly List<FolderRunItem> _items = [];
    private readonly Dictionary<string, (int Rank, bool Passes, int Count)> _grades = new(StringComparer.Ordinal);

    /// <summary>Every image run, in order.</summary>
    public IReadOnlyList<FolderRunItem> Items => _items;

    /// <summary>How many images the folder holds.</summary>
    public int Total { get; internal set; }

    public int Passed { get; private set; }

    public int Failed { get; private set; }

    /// <summary>Images the recipe made no decision on, because nothing in it judges.</summary>
    public int Unjudged { get; private set; }

    /// <summary>Images on which at least one node failed.</summary>
    public int WithErrors { get; private set; }

    public TimeSpan Duration { get; internal set; }

    /// <summary>Stopped before the last image.</summary>
    public bool Cancelled { get; internal set; }

    /// <summary>Passed as a percentage of the images judged.</summary>
    public double Yield => Passed + Failed == 0 ? 0 : 100.0 * Passed / (Passed + Failed);

    internal void Add(FolderRunItem item)
    {
        _items.Add(item);

        switch (item.Verdict.Passed)
        {
            case true:
                Passed++;
                break;
            case false:
                Failed++;
                break;
            default:
                Unjudged++;
                break;
        }

        if (item.NodeErrors > 0)
            WithErrors++;

        if (item.Verdict.Grade is { } grade)
        {
            _grades[grade.Name] = _grades.TryGetValue(grade.Name, out var tally)
                ? tally with { Count = tally.Count + 1 }
                : (grade.Rank, grade.Passes, 1);
        }
    }

    /// <summary>"120 images in 14.2 s: 115 passed, 5 failed (95.8 % yield). Grades: A 80, B 35, Reject 5."</summary>
    public string Describe()
    {
        var text = new StringBuilder();

        text.Append(Cancelled
            ? string.Format(CultureInfo.CurrentCulture, "Stopped after {0} of {1} images", _items.Count, Total)
            : string.Format(CultureInfo.CurrentCulture, "{0} images", _items.Count));

        text.Append(string.Format(CultureInfo.CurrentCulture, " in {0:0.0} s", Duration.TotalSeconds));

        if (Passed + Failed > 0)
        {
            text.Append(string.Format(
                CultureInfo.CurrentCulture, ": {0} passed, {1} failed ({2:0.0} % yield)", Passed, Failed, Yield));
        }
        else
        {
            text.Append(". Nothing in the recipe judges, so none passed or failed");
        }

        text.Append('.');

        if (_grades.Count > 0)
        {
            text.Append(" Grades: ");
            text.Append(string.Join(
                ", ",
                _grades
                    .OrderBy(entry => !entry.Value.Passes)
                    .ThenBy(entry => entry.Value.Rank)
                    .Select(entry => $"{entry.Key} {entry.Value.Count}")));
            text.Append('.');
        }

        if (WithErrors > 0)
            text.Append(string.Format(CultureInfo.CurrentCulture, " {0} image(s) had a node error.", WithErrors));

        return text.ToString();
    }
}

/// <summary>
/// Runs a recipe over every image of its Image Sequence, each one an
/// inspection.
/// </summary>
/// <remarks>
/// <para>
/// What a recipe is for, once it is tuned: every part in a folder, judged,
/// counted and logged. Each image is an inspection
/// (<see cref="GraphScheduler.InspectAsync"/>), so Counter counts it and
/// Result Log writes it - which stepping through the folder by hand does
/// not.
/// </para>
/// <para>
/// Shared by the application's Run Folder and the command-line runner, so
/// a folder judged at the desk and one judged by a script come out the
/// same.
/// </para>
/// </remarks>
public static class FolderRun
{
    /// <summary>
    /// Steps <paramref name="sequence"/> through its images, inspecting each.
    /// </summary>
    /// <param name="afterEach">Called after each image, before the next - for progress.</param>
    public static async Task<FolderRunSummary> RunAsync(
        VisionGraph graph,
        GraphScheduler scheduler,
        ISequenceNode sequence,
        Func<FolderRunItem, Task>? afterEach,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(sequence);

        var summary = new FolderRunSummary { Total = sequence.Count };
        var clock = Stopwatch.StartNew();

        for (var index = 1; index <= sequence.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                summary.Cancelled = true;
                break;
            }

            // A parameter like any other: setting it marks the sequence and
            // everything after it stale, so the inspection reads the next
            // image.
            sequence.Index = index;

            var result = await scheduler.InspectAsync(cancellationToken).ConfigureAwait(true);

            if (result.Cancelled)
            {
                summary.Cancelled = true;
                break;
            }

            if (result.Error is not null)
                throw new InvalidOperationException(result.Error);

            var item = new FolderRunItem(
                index,
                sequence.Count,
                sequence.CurrentName,
                Verdicts.Of(graph, scheduler.GetOutput),
                result.Failed);

            summary.Add(item);

            if (afterEach is not null)
                await afterEach(item).ConfigureAwait(true);
        }

        summary.Duration = clock.Elapsed;
        return summary;
    }

    /// <summary>The sequence a folder run steps through: the first enabled one in the recipe.</summary>
    public static ISequenceNode? SequenceIn(VisionGraph graph) =>
        GraphTree.AllNodes(graph, enabledOnly: true).OfType<ISequenceNode>().FirstOrDefault();
}
