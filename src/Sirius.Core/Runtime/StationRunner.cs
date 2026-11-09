using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Projects;
using Sirius.Core.Serialization;

namespace Sirius.Core.Runtime;

/// <summary>What a station is doing.</summary>
public enum StationState
{
    /// <summary>Waiting for a trigger.</summary>
    Ready,

    /// <summary>Inspecting once, for a trigger.</summary>
    Inspecting,

    /// <summary>Inspecting part after part until stopped.</summary>
    Running,

    /// <summary>Finishing the inspection in hand before stopping.</summary>
    Stopping,

    /// <summary>Cannot inspect - its recipe would not load or will not run. <see cref="StationRunner.Problem"/> says why.</summary>
    Faulted,
}

/// <summary>A station's tally since it started or was last reset.</summary>
public sealed record StationCounts(long Total, long Passed, long Failed, long Unjudged, long WithErrors)
{
    public static readonly StationCounts Zero = new(0, 0, 0, 0, 0);

    /// <summary>Passed as a percentage of those judged.</summary>
    public double Yield => Passed + Failed == 0 ? 0 : 100.0 * Passed / (Passed + Failed);

    internal StationCounts Add(Verdict verdict, bool hadErrors) => this with
    {
        Total = Total + 1,
        Passed = Passed + (verdict.Passed == true ? 1 : 0),
        Failed = Failed + (verdict.Passed == false ? 1 : 0),
        Unjudged = Unjudged + (verdict.Passed is null ? 1 : 0),
        WithErrors = WithErrors + (hadErrors ? 1 : 0),
    };
}

/// <summary>
/// A view's picture and what is drawn on it, as an inspection left them.
/// </summary>
/// <param name="Inputs">Each of the viewer's inputs by name - its image first, then its shapes - in order.</param>
public sealed record ViewFrame(IReadOnlyList<KeyValuePair<string, object?>> Inputs)
{
    /// <summary>The viewer's first input: its picture.</summary>
    public object? Image => Inputs.Count > 0 ? Inputs[0].Value : null;

    /// <summary>Everything after the picture: what is drawn on it.</summary>
    public IEnumerable<object?> Overlays => Inputs.Skip(1).Select(input => input.Value);
}

/// <summary>
/// One inspection of a station, and what its recipe published.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Values"/> holds every published name: a result's output, a
/// setting's value, a view's <see cref="ViewFrame"/>.
/// </para>
/// <para>
/// <b>Images belong to the station.</b> A picture - an image result, or a
/// view's - is the station's own buffer, valid until its next inspection
/// starts, and must not be disposed. The station waits for
/// <see cref="StationRunner.Inspected"/> before inspecting again, so a
/// screen draws or copies in there. Numbers, text, pass/fail and shapes can
/// be kept.
/// </para>
/// </remarks>
public sealed record InspectionSnapshot(
    string Station,
    long Serial,
    DateTime Time,
    Verdict Verdict,
    TimeSpan Duration,
    int NodeErrors,
    IReadOnlyDictionary<string, object?> Values)
{
    /// <summary>The value published under <paramref name="name"/>, ignoring case, or null.</summary>
    public object? this[string name] => Values.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// Runs one station's recipe: a trigger inspects once, Start inspects part
/// after part until Stop. Runtime MVP step 3 - acquire, inspect, publish -
/// with no screen of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Acquire</b> is the recipe's own source. Its Camera grabs on each
/// inspection, as it does when Studio runs continuously. A recipe reading
/// an Image Sequence instead steps to the next image on each inspection,
/// round to the first after the last, so a folder of images stands in for a
/// camera at the desk.
/// </para>
/// <para>
/// <b>Inspect</b> is <see cref="GraphScheduler.InspectAsync"/>, as Run Folder
/// and Run Continuously use: counters count and logs log. <b>Publish</b> is
/// an <see cref="InspectionSnapshot"/> of every published name, and the
/// station's own counts.
/// </para>
/// <para>
/// Call it from one thread - the screen's - as the scheduler requires.
/// Several stations run side by side, each inspecting on its own worker.
/// </para>
/// </remarks>
public sealed class StationRunner : ObservableObject, IDisposable
{
    private readonly GraphScheduler? _scheduler;
    private readonly ISequenceNode? _sequence;
    private readonly List<string> _problems = [];

    private StationState _state;
    private StationCounts _counts = StationCounts.Zero;
    private InspectionSnapshot? _last;
    private string? _lastError;
    private long _serial;
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private bool _disposed;

    /// <summary>A station running <paramref name="recipe"/>.</summary>
    /// <param name="problems">What loading the recipe found, reported with the station's own.</param>
    public StationRunner(string name, VisionGraph recipe, IEnumerable<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        Name = name;
        Recipe = recipe;
        _problems.AddRange(problems ?? []);

        foreach (var item in Publishing.In(recipe).Where(item => !item.Found))
            _problems.Add($"'{item.Name}' names {item.Source}, which is no longer there.");

        _sequence = FolderRun.SequenceIn(recipe);
        _scheduler = new GraphScheduler(recipe);
        _state = StationState.Ready;
    }

    private StationRunner(string name, string problem)
    {
        Name = name;
        Recipe = new VisionGraph();
        Problem = problem;
        _problems.Add(problem);
        _state = StationState.Faulted;
    }

    /// <summary>
    /// The station's recipe, loaded from its project - or a faulted station
    /// saying why it could not be.
    /// </summary>
    /// <remarks>Needs the node library registered, and any custom node libraries loaded, first.</remarks>
    public static StationRunner Open(Project project, Station station)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(station);

        var path = project.RecipePath(station);

        try
        {
            var loaded = GraphSerializer.Load(path);
            return new StationRunner(station.Name, loaded.Graph, loaded.Warnings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return new StationRunner(station.Name, $"The recipe {station.Recipe} could not be opened: {ex.Message}");
        }
    }

    public string Name { get; }

    public VisionGraph Recipe { get; }

    public StationState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
                OnPropertyChanged(nameof(IsBusy));
        }
    }

    /// <summary>Inspecting, or running, or stopping: a trigger now would be refused.</summary>
    public bool IsBusy => State is StationState.Inspecting or StationState.Running or StationState.Stopping;

    /// <summary>Why the station is faulted, or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// What loading found - unknown nodes, published names whose output has
    /// gone - none of which stops it running.
    /// </summary>
    public IReadOnlyList<string> Problems => _problems;

    public StationCounts Counts
    {
        get => _counts;
        private set => SetProperty(ref _counts, value);
    }

    /// <summary>The latest inspection. Its pictures are valid only until the next starts; see <see cref="InspectionSnapshot"/>.</summary>
    public InspectionSnapshot? Last
    {
        get => _last;
        private set => SetProperty(ref _last, value);
    }

    /// <summary>The latest inspection's node failures, or why a run stopped; null when it went cleanly.</summary>
    public string? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    /// <summary>
    /// Called after every inspection, and awaited before the next starts -
    /// the one moment a screen may draw the station's pictures.
    /// </summary>
    public Func<InspectionSnapshot, Task>? Inspected { get; set; }

    /// <summary>
    /// Awaited between one inspection and the next while running - the
    /// screen's chance to catch up. Null for a bare yield.
    /// </summary>
    /// <remarks>
    /// A fast recipe finishes inspections back to back, and each resumes on
    /// the screen's thread ahead of anything less urgent there: a bare yield
    /// let a running station starve the window's own work. A screen gives
    /// one that waits until it has drawn and answered input.
    /// </remarks>
    public Func<Task>? Breathe { get; set; }

    /// <summary>Inspects once.</summary>
    /// <returns>The inspection, or null when the station was busy or faulted and did nothing.</returns>
    public async Task<InspectionSnapshot?> TriggerAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsBusy || State == StationState.Faulted)
            return null;

        State = StationState.Inspecting;

        try
        {
            return await InspectOnceAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            if (State == StationState.Inspecting)
                State = StationState.Ready;
        }
    }

    /// <summary>Inspects part after part until <see cref="StopAsync"/>.</summary>
    /// <returns>False when it was already running, or faulted.</returns>
    public bool Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsBusy || State == StationState.Faulted)
            return false;

        _run = new CancellationTokenSource();
        State = StationState.Running;
        _loop = RunAsync(_run);
        return true;
    }

    /// <summary>Stops after the inspection in hand, and waits for it.</summary>
    public async Task StopAsync()
    {
        if (_run is { } run && State == StationState.Running)
        {
            State = StationState.Stopping;
            run.Cancel();
        }

        await _loop.ConfigureAwait(true);
    }

    /// <summary>Starts the station's counts again from zero. Counter nodes in the recipe keep their own.</summary>
    public void ResetCounts() => Counts = StationCounts.Zero;

    private async Task RunAsync(CancellationTokenSource run)
    {
        var failingInARow = 0;

        try
        {
            while (!run.IsCancellationRequested)
            {
                // Not given the stop token: Stop finishes the inspection in
                // hand rather than abandoning it half way - which would also
                // have freed the last inspection's pictures, with nothing new
                // in their place.
                var snapshot = await InspectOnceAsync(CancellationToken.None).ConfigureAwait(true);

                if (snapshot is null || State == StationState.Faulted)
                    break;

                // A camera unplugged fails every inspection at once; going
                // round again at full speed would only fill the screen with
                // the same failure. Back off a little, and more, while it lasts.
                failingInARow = snapshot.NodeErrors > 0 ? failingInARow + 1 : 0;

                if (failingInARow > 1)
                    await Task.Delay(Math.Min(1000, 100 * failingInARow), run.Token).ConfigureAwait(true);
                else if (Breathe is { } breathe)
                    await breathe().ConfigureAwait(true);
                else
                    await Task.Yield();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        finally
        {
            run.Dispose();
            _run = null;

            if (State != StationState.Faulted)
                State = StationState.Ready;
        }
    }

    private async Task<InspectionSnapshot?> InspectOnceAsync(CancellationToken cancellationToken)
    {
        var scheduler = _scheduler!;

        // The next image of a folder standing in for a camera; the first
        // inspection reads the one the recipe was saved on.
        if (_sequence is { Count: > 0 } sequence && _serial > 0)
            sequence.Index = sequence.Index >= sequence.Count ? 1 : sequence.Index + 1;

        var clock = Stopwatch.StartNew();
        GraphRunResult result;

        try
        {
            result = await scheduler.InspectAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Abandoned();
            return null;
        }

        if (result.Cancelled)
        {
            Abandoned();
            return null;
        }

        if (result.Error is not null)
        {
            Fault($"The recipe cannot run: {result.Error}");
            return null;
        }

        var verdict = Verdicts.Of(Recipe, scheduler.GetOutput);

        var snapshot = new InspectionSnapshot(
            Name,
            ++_serial,
            DateTime.Now,
            verdict,
            clock.Elapsed,
            result.Failed,
            ReadPublished(scheduler));

        Counts = Counts.Add(verdict, result.Failed > 0);
        LastError = result.Failed > 0 ? FailureText() : null;
        Last = snapshot;

        if (Inspected is { } inspected)
            await inspected(snapshot).ConfigureAwait(true);

        return snapshot;
    }

    /// <summary>Every published name's value, as the inspection just left it.</summary>
    private Dictionary<string, object?> ReadPublished(GraphScheduler scheduler)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in Publishing.In(Recipe).Where(item => item.Found))
        {
            values[item.Name] = item.Kind switch
            {
                PublishedKind.Result => item.Node.FindOutput(item.Entry.Member) is { } port ? scheduler.GetOutput(port) : null,
                PublishedKind.Setting => item.Node.Descriptor.Parameters.First(p => p.Name == item.Entry.Member).GetValue(item.Node),
                _ => new ViewFrame(item.Node.Inputs
                    .Where(input => !item.Node.IsParameterPort(input))
                    .Select(input => new KeyValuePair<string, object?>(input.Name, scheduler.GetInput(input)))
                    .ToArray()),
            };
        }

        return values;
    }

    /// <summary>"Find Circle 1: no circle found" - the failed nodes and their messages.</summary>
    private string FailureText() =>
        string.Join(
            "; ",
            GraphTree.AllNodes(Recipe, enabledOnly: true)
                .Where(node => node.Status == NodeStatus.Error)
                .Select(node => string.IsNullOrWhiteSpace(node.StatusMessage) ? node.Name : $"{node.Name}: {node.StatusMessage}")
                .DefaultIfEmpty("A node failed."));

    /// <summary>
    /// An inspection cut short - a trigger's token cancelled. It may have
    /// replaced what the last one left, so the last one's pictures can no
    /// longer be trusted, and it is forgotten.
    /// </summary>
    private void Abandoned() => Last = null;

    private void Fault(string problem)
    {
        Problem = problem;
        LastError = problem;
        _problems.Add(problem);
        State = StationState.Faulted;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _run?.Cancel();
        _scheduler?.Dispose();
    }
}
