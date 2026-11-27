namespace Sirius.Core.Runtime;

/// <summary>
/// The stations of a running project, as something outside them - a PLC
/// controller's workflow - sees them: ask one to inspect, and hear when
/// any has inspected.
/// </summary>
/// <remarks>
/// Safe to use from any thread: an implementation takes each call to the
/// thread the stations live on.
/// </remarks>
public interface IStationBus
{
    /// <summary>The stations' names.</summary>
    IReadOnlyList<string> Stations { get; }

    /// <summary>
    /// Has <paramref name="station"/> inspect once, with what is known about
    /// the part.
    /// </summary>
    /// <returns>The inspection; null when the station was busy or faulted and did nothing.</returns>
    /// <exception cref="ArgumentException">There is no station of that name.</exception>
    Task<InspectionSnapshot?> InspectAsync(string station, PartValues? part, CancellationToken cancellationToken);

    /// <summary>How <paramref name="station"/> is - ready, inspecting, faulted - or null when not known.</summary>
    StationState? StateOf(string station) => null;

    /// <summary>
    /// The results <paramref name="station"/> gives (F33) - for a part it
    /// gave none in time; PASS and FAIL when not known.
    /// </summary>
    Projects.ResultList ResultsOf(string station) => Projects.ResultList.Default;

    /// <summary>
    /// Raised after every inspection of every station, however it was
    /// started - on the stations' thread, so a handler copies what it needs
    /// and returns. The snapshot's pictures are valid only during the call.
    /// </summary>
    event EventHandler<InspectionSnapshot>? Inspected;
}

/// <summary>
/// A running project's stations as an <see cref="IStationBus"/>: each call
/// is taken to the thread the stations are driven from - the screen's - as
/// <see cref="StationRunner"/> requires.
/// </summary>
public sealed class ProjectStationBus : IStationBus, IDisposable
{
    private readonly ProjectRuntime _runtime;
    private readonly SynchronizationContext? _context;

    /// <param name="runtime">The project's stations.</param>
    /// <param name="context">
    /// The stations' thread; null to call them on whichever thread asks,
    /// for tests and a program with no screen.
    /// </param>
    public ProjectStationBus(ProjectRuntime runtime, SynchronizationContext? context = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _context = context;

        foreach (var station in runtime.Stations)
            station.Finished += OnFinished;
    }

    public IReadOnlyList<string> Stations => _runtime.Stations.Select(station => station.Name).ToArray();

    /// <remarks>Read as it is, from whichever thread asks: a state is one value, and a moment old is fine.</remarks>
    public StationState? StateOf(string station) => _runtime.Find(station)?.State;

    /// <remarks>Read as it is: a station's list is set when it is opened, and does not change while it runs.</remarks>
    public Projects.ResultList ResultsOf(string station) => _runtime.Find(station)?.Results ?? Projects.ResultList.Default;

    public event EventHandler<InspectionSnapshot>? Inspected;

    public Task<InspectionSnapshot?> InspectAsync(string station, PartValues? part, CancellationToken cancellationToken)
    {
        var runner = _runtime.Find(station)
                     ?? throw new ArgumentException($"There is no station called '{station}'.", nameof(station));

        return OnStationsThread(() => runner.TriggerAsync(part, cancellationToken));
    }

    private Task<T> OnStationsThread<T>(Func<Task<T>> work)
    {
        if (_context is null || SynchronizationContext.Current == _context)
            return work();

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        _context.Post(
            async _ =>
            {
                try
                {
                    done.SetResult(await work().ConfigureAwait(true));
                }
                catch (OperationCanceledException ex)
                {
                    done.TrySetCanceled(ex.CancellationToken);
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            },
            null);

        return done.Task;
    }

    private void OnFinished(object? sender, InspectionSnapshot snapshot) => Inspected?.Invoke(this, snapshot);

    public void Dispose()
    {
        foreach (var station in _runtime.Stations)
            station.Finished -= OnFinished;
    }
}
