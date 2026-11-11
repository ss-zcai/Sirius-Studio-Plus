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
/// <para>
/// <b>Settings on the line</b> (MVP step 6): a published setting can be
/// changed while the station is ready or running, within its range
/// (<see cref="Tunable"/>). A change made during an inspection waits for it
/// to finish - the scheduler reads the nodes from its worker - and takes
/// effect from the next. Each is kept in the recipe's
/// <see cref="LineSettings"/> file and applied whenever it is loaded again.
/// </para>
/// </remarks>
public sealed class StationRunner : ObservableObject, IDisposable
{
    private readonly GraphScheduler? _scheduler;
    private readonly ISequenceNode? _sequence;
    private readonly List<string> _problems = [];
    private readonly Dictionary<string, Tunable> _settings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Tunable> _settingOrder = [];
    private readonly Dictionary<string, (Tunable Setting, object? Value)> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly LineSettings? _line;

    private bool _inPass;
    private StationState _state;
    private StationCounts _counts = StationCounts.Zero;
    private InspectionSnapshot? _last;
    private string? _lastError;
    private long _serial;
    private string? _savingProblem;
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private bool _disposed;

    /// <summary>A station running <paramref name="recipe"/>.</summary>
    /// <param name="problems">What loading the recipe found, reported with the station's own.</param>
    /// <param name="line">
    /// The settings changed on the line for this recipe, applied now and
    /// added to as they are changed; null keeps changes for this run only.
    /// </param>
    /// <param name="limits">The station's limits on its settings, by published name; null for the nodes' own.</param>
    public StationRunner(
        string name,
        VisionGraph recipe,
        IEnumerable<string>? problems = null,
        LineSettings? line = null,
        IReadOnlyDictionary<string, SettingLimit>? limits = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        Name = name;
        Recipe = recipe;
        _line = line;
        _problems.AddRange(problems ?? []);

        foreach (var item in Publishing.In(recipe).Where(item => !item.Found))
            _problems.Add($"'{item.Name}' names {item.Source}, which is no longer there.");

        foreach (var setting in Tunable.In(recipe, limits))
        {
            _settings[setting.Name] = setting;
            _settingOrder.Add(setting);
        }

        ApplyLine();

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
            var line = LineSettings.ForRecipe(path, out var lineProblem);

            var runner = new StationRunner(
                station.Name,
                loaded.Graph,
                lineProblem is null ? loaded.Warnings : [.. loaded.Warnings, lineProblem],
                line,
                station.Limits)
            {
                RecipeName = Path.GetFileName(path),
            };

            if (station.Saving.IsOn)
            {
                if (InspectionRecorders.Factory?.Invoke(project, station) is { } recorder)
                    runner.Recorder = recorder;
                else
                    runner._problems.Add("Images are not saved: this program has nothing that writes pictures.");
            }

            return runner;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return new StationRunner(station.Name, $"The recipe {station.Recipe} could not be opened: {ex.Message}");
        }
    }

    public string Name { get; }

    public VisionGraph Recipe { get; }

    /// <summary>The recipe's file name, for what is saved: "cap.sgraph". Null for a recipe from nowhere.</summary>
    public string? RecipeName { get; init; }

    /// <summary>
    /// Keeps the inspections the station's image saving asks for; null to
    /// keep none. Disposed with the station.
    /// </summary>
    public IInspectionRecorder? Recorder { get; set; }

    /// <summary>What is stopping pictures being saved, or making it skip some; null while all is well.</summary>
    public string? SavingProblem
    {
        get => _savingProblem;
        private set => SetProperty(ref _savingProblem, value);
    }

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

    // ---- settings on the line ----------------------------------------------

    /// <summary>The settings the recipe publishes, in node order.</summary>
    public IReadOnlyList<Tunable> Settings => _settingOrder;

    /// <summary>The setting published as <paramref name="name"/>, or null.</summary>
    public Tunable? Setting(string name) => _settings.GetValueOrDefault(name);

    /// <summary>The settings changed on the line, kept for the next start; null when not kept.</summary>
    public LineSettings? Line => _line;

    /// <summary>After a setting was changed: its published name.</summary>
    public event EventHandler<string>? SettingChanged;

    /// <summary>
    /// What <paramref name="name"/> is set to: a change waiting for the
    /// inspection in hand to finish, or the node's value.
    /// </summary>
    public object? SettingValue(string name)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(name, out var waiting))
                return waiting.Value;
        }

        return _settings.TryGetValue(name, out var setting) ? setting.Value : null;
    }

    /// <summary>
    /// Changes a published setting: now, or - during an inspection - as soon
    /// as it is finished; and keeps it for the next start.
    /// </summary>
    /// <param name="value">A number, on or off, a choice's name, or typed text.</param>
    /// <returns>Why it was not changed, or not kept; null when all is well.</returns>
    public string? ChangeSetting(string name, object? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_scheduler is null)
            return $"{Name} has no recipe loaded.";

        if (!_settings.TryGetValue(name, out var setting))
            return $"{Name} publishes no setting called '{name}'.";

        if (setting.Convert(value, out var converted) is { } problem)
            return problem;

        bool now;

        lock (_gate)
        {
            now = !_inPass;

            if (now)
                _pending.Remove(setting.Name);
            else
                _pending[setting.Name] = (setting, converted);
        }

        if (now)
            setting.Apply(converted);

        SettingChanged?.Invoke(this, setting.Name);

        if (_line is null)
            return null;

        try
        {
            _line.Set(setting.Name, Tunable.ToStored(converted));
            _line.Save();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{setting.Name} is changed, but could not be kept for the next start: {ex.Message}";
        }
    }

    /// <summary>The values changed on the line, put into the recipe as it loads.</summary>
    private void ApplyLine()
    {
        if (_line is null)
            return;

        foreach (var entry in _line.Entries)
        {
            if (!_settings.TryGetValue(entry.Name, out var setting))
            {
                _problems.Add($"'{entry.Name}' was changed on the line to {entry.ValueText}, but the recipe no longer publishes it as a setting; not applied.");
                continue;
            }

            if (setting.Convert(entry.Value, out var converted) is { } problem)
            {
                _problems.Add($"'{entry.Name}' was changed on the line to {entry.ValueText}, which is not applied: {problem}");
                continue;
            }

            setting.Apply(converted);
        }
    }

    /// <summary>Changes made during the inspection just finished, applied now it has.</summary>
    private void ApplyPending()
    {
        (Tunable Setting, object? Value)[] waiting;

        lock (_gate)
        {
            if (_pending.Count == 0)
                return;

            waiting = _pending.Values.ToArray();
            _pending.Clear();
        }

        foreach (var (setting, value) in waiting)
            setting.Apply(value);
    }

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

        try
        {
            return await InspectAndPublishAsync(scheduler, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            // A setting changed while the recipe ran, in time for the next -
            // after this inspection is published, so its snapshot shows the
            // values it was judged with.
            ApplyPending();
        }
    }

    private async Task<InspectionSnapshot?> InspectAndPublishAsync(GraphScheduler scheduler, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        GraphRunResult result;

        lock (_gate)
            _inPass = true;

        try
        {
            result = await scheduler.InspectAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Abandoned();
            return null;
        }
        finally
        {
            lock (_gate)
                _inPass = false;
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

        Keep(scheduler, snapshot);

        if (Inspected is { } inspected)
            await inspected(snapshot).ConfigureAwait(true);

        return snapshot;
    }

    /// <summary>
    /// Hands the inspection to the recorder when it wants it - while the
    /// pictures are still the station's, before anything else can run.
    /// Nothing it does can stop the line: a recorder that throws is reported
    /// and the inspection carries on.
    /// </summary>
    private void Keep(GraphScheduler scheduler, InspectionSnapshot snapshot)
    {
        if (Recorder is not { } recorder)
            return;

        try
        {
            if (recorder.Wants(snapshot.Verdict, snapshot.NodeErrors))
                recorder.Record(new InspectionRecord(snapshot, RecipeName, Acquired(scheduler), Viewers(scheduler)));

            SavingProblem = recorder.Problem;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SavingProblem = $"Images could not be saved: {ex.Message}";
        }
    }

    /// <summary>
    /// The picture the recipe took: its camera's, else its image sequence's,
    /// else any source node's - the first output carrying pixels.
    /// </summary>
    private object? Acquired(GraphScheduler scheduler)
    {
        var nodes = Recipe.Nodes.Where(node => node.Enabled).ToArray();

        var sources = nodes.Where(node => node is IInspectionNode)
            .Concat(nodes.Where(node => node is ISequenceNode))
            .Concat(nodes.Where(node => node.Descriptor.Category == NodeCategory.Source));

        foreach (var node in sources)
        {
            foreach (var output in node.Outputs)
            {
                if (scheduler.GetOutput(output) is IPixelValues picture)
                    return picture;
            }
        }

        return null;
    }

    /// <summary>Each image viewer in the recipe: its picture and what is drawn on it.</summary>
    private List<ViewerRecord> Viewers(GraphScheduler scheduler)
    {
        var viewers = new List<ViewerRecord>();

        foreach (var node in Recipe.Nodes.Where(node => node.Enabled && node.Descriptor.TypeKey == "output.viewer"))
        {
            var inputs = node.Inputs.Where(input => !node.IsParameterPort(input)).ToArray();

            if (inputs.Length == 0)
                continue;

            viewers.Add(new ViewerRecord(
                node.Name,
                scheduler.GetInput(inputs[0]),
                inputs.Skip(1).Select(scheduler.GetInput).OfType<Shapes.ShapeSet>().ToArray()));
        }

        return viewers;
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

        // Last: it writes out what it was given, which no longer needs the station.
        Recorder?.Dispose();
    }
}
