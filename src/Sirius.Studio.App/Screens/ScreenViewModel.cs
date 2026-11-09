using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Sirius.Core.Runtime;
using Sirius.Core.Screens;
using Sirius.Studio.App.ViewModels;

namespace Sirius.Studio.App.Screens;

/// <summary>
/// A screen, running: its widgets bound to a project's stations as they
/// inspect (runtime MVP step 4).
/// </summary>
/// <remarks>
/// <para>
/// Values follow every inspection. Pictures follow at up to the screen's
/// rate, paced per station as Studio's live display is
/// (<see cref="LiveDisplay"/>): a station inspecting four hundred parts a
/// second must not spend its time drawing them. They are drawn in the
/// station's <see cref="StationRunner.Inspected"/> handler, the one moment
/// its pictures are alive; one skipped while running is drawn when the
/// station stops, from the inspection it stopped on.
/// </para>
/// </remarks>
public sealed class ScreenViewModel : ObservableObject, IDisposable
{
    private readonly ProjectRuntime _runtime;
    private readonly Dictionary<StationRunner, (LiveDisplay Pacing, bool Undrawn)> _pacing = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _problems = [];

    public ScreenViewModel(Screen screen, ProjectRuntime runtime, string projectFolder, IEnumerable<string>? problems = null)
    {
        Screen = screen ?? throw new ArgumentNullException(nameof(screen));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

        Widgets = screen.Widgets.Select(widget => Create(widget, projectFolder)).ToArray();

        _problems.AddRange(problems ?? []);
        _problems.AddRange(ScreenLayout.Problems(screen, runtime.Stations));
        _problems.AddRange(runtime.Problems);

        foreach (var station in runtime.Stations)
        {
            _pacing[station] = (new LiveDisplay(0), false);
            station.Inspected = snapshot => OnInspected(station, snapshot);
            station.PropertyChanged += OnStationChanged;
            Refresh(station);
        }
    }

    public Screen Screen { get; }

    public double Width => Screen.Width;

    public double Height => Screen.Height;

    public IReadOnlyList<WidgetViewModel> Widgets { get; }

    /// <summary>Stations that would not load, names the screen cannot find - shown along the bottom.</summary>
    public IReadOnlyList<string> Problems => _problems;

    public bool HasProblems => _problems.Count > 0;

    public string ProblemText => string.Join("   ·   ", _problems);

    private WidgetViewModel Create(Widget widget, string projectFolder) => widget.Kind switch
    {
        WidgetKind.Image => new ImageWidgetViewModel(widget),
        WidgetKind.Verdict => new VerdictWidgetViewModel(widget),
        WidgetKind.Value or WidgetKind.Setting => new ValueWidgetViewModel(widget),
        WidgetKind.Counter => new CounterWidgetViewModel(widget),
        WidgetKind.Button => new ButtonWidgetViewModel(widget, _runtime),
        WidgetKind.Logo => new LogoWidgetViewModel(widget, projectFolder),
        _ => new TextWidgetViewModel(widget),
    };

    private Task OnInspected(StationRunner station, InspectionSnapshot snapshot)
    {
        var (pacing, _) = _pacing[station];
        var now = _clock.Elapsed.TotalSeconds;

        pacing.Inspected();

        // A single trigger is always drawn; a run, at the screen's pace.
        var draw = station.State != StationState.Running || pacing.IsDue(now);

        foreach (var widget in Widgets.Where(widget => widget.Follows(station)))
            widget.Inspected(station, snapshot, draw);

        if (draw)
            pacing.Shown(now, _clock.Elapsed.TotalSeconds);

        _pacing[station] = (pacing, !draw);
        return Task.CompletedTask;
    }

    private void OnStationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not StationRunner station)
            return;

        Refresh(station);

        // Stopped on a frame that was not drawn: draw it now. It is still
        // the station's latest, so still alive.
        if (e.PropertyName == nameof(StationRunner.State) &&
            station.State == StationState.Ready &&
            _pacing[station] is { Undrawn: true } paced &&
            station.Last is { } last)
        {
            foreach (var widget in Widgets.OfType<ImageWidgetViewModel>().Where(widget => widget.Follows(station)))
                widget.Inspected(station, last, drawPictures: true);

            _pacing[station] = (paced.Pacing, false);
        }
    }

    private void Refresh(StationRunner station)
    {
        foreach (var widget in Widgets.Where(widget => widget.Follows(station)))
            widget.StationChanged(station);

        foreach (var button in Widgets.OfType<ButtonWidgetViewModel>())
            button.Refresh();
    }

    public void Dispose()
    {
        foreach (var station in _runtime.Stations)
        {
            station.PropertyChanged -= OnStationChanged;
            station.Inspected = null;
        }
    }
}
