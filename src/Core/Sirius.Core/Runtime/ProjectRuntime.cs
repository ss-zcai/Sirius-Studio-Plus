using Sirius.Core.Projects;

namespace Sirius.Core.Runtime;

/// <summary>
/// Every station of a project, running side by side: what the runtime
/// program and Studio's Run as Runtime hold while a project runs.
/// </summary>
/// <remarks>
/// Each station has its own recipe, scheduler and camera, and inspects on
/// its own worker, so one station's slow inspection never holds another's.
/// A station whose recipe would not load is kept, faulted, so a screen can
/// say which and why rather than the station silently not being there.
/// </remarks>
public sealed class ProjectRuntime : IDisposable
{
    private readonly List<StationRunner> _stations;

    public ProjectRuntime(Project project)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        _stations = project.Stations.Select(station => StationRunner.Open(project, station)).ToList();
    }

    /// <summary>For stations made some other way - tests, a recipe run on its own.</summary>
    public ProjectRuntime(Project project, IEnumerable<StationRunner> stations)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        _stations = stations.ToList();
    }

    public Project Project { get; }

    public IReadOnlyList<StationRunner> Stations => _stations;

    /// <summary>The station called <paramref name="name"/>, ignoring case, or null.</summary>
    public StationRunner? Find(string name) =>
        _stations.FirstOrDefault(station => string.Equals(station.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The value <paramref name="station"/> last published under
    /// <paramref name="name"/> - what a screen widget bound to
    /// "station › name" shows - or null.
    /// </summary>
    public object? Read(string station, string name) => Find(station)?.Last?[name];

    /// <summary>Every problem any station has, "Station: problem".</summary>
    public IEnumerable<string> Problems =>
        _stations.SelectMany(station => station.Problems.Select(problem => $"{station.Name}: {problem}"));

    /// <summary>Inspects once on every station that is free, all at the same time.</summary>
    public async Task<IReadOnlyList<InspectionSnapshot>> TriggerAllAsync(CancellationToken cancellationToken = default)
    {
        var inspections = await Task.WhenAll(_stations.Select(station => station.TriggerAsync(cancellationToken)))
            .ConfigureAwait(true);

        return inspections.OfType<InspectionSnapshot>().ToArray();
    }

    /// <summary>Starts every station that is free running.</summary>
    public void StartAll()
    {
        foreach (var station in _stations)
            station.Start();
    }

    /// <summary>Stops every running station, waiting for each to finish the inspection in hand.</summary>
    public Task StopAllAsync() => Task.WhenAll(_stations.Select(station => station.StopAsync()));

    public void ResetAllCounts()
    {
        foreach (var station in _stations)
            station.ResetCounts();
    }

    public void Dispose()
    {
        foreach (var station in _stations)
            station.Dispose();
    }
}
