using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sirius.Core.Nodes;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Core.Screens;
using Sirius.Core.Shapes;
using Sirius.Plc.Workflows;
using Sirius.UI.Imaging;
using Sirius.Vision;

namespace Sirius.UI.Screens;

/// <summary>A widget on a running screen.</summary>
public abstract class WidgetViewModel : ObservableObject
{
    protected WidgetViewModel(Widget widget)
    {
        Widget = widget;
    }

    public Widget Widget { get; }

    public double X => Widget.X;

    public double Y => Widget.Y;

    public double Width => Math.Max(1, Widget.Width);

    public double Height => Math.Max(1, Widget.Height);

    public double FontSize => Widget.FontSize > 0 ? Widget.FontSize : 16;

    /// <summary>The station it is bound to, or null for one bound to none - or to all.</summary>
    public string? Station => Widget.Station;

    /// <summary>Who is logged in, and the login popup: the screen this is on.</summary>
    internal IScreenAccess? Access { get; set; }

    /// <summary>Someone logged in or out: what is locked may have changed.</summary>
    internal virtual void AccessChanged()
    {
    }

    /// <summary>Whether it follows <paramref name="station"/>.</summary>
    internal bool Follows(StationRunner station) =>
        Station is not null && string.Equals(Station, station.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// After an inspection of its station. Pictures may be read only when
    /// <paramref name="drawPictures"/> - they are the station's own, alive
    /// only until its next inspection.
    /// </summary>
    internal virtual void Inspected(StationRunner station, InspectionSnapshot snapshot, bool drawPictures)
    {
    }

    /// <summary>After its station's counts, state or error changed.</summary>
    internal virtual void StationChanged(StationRunner station)
    {
    }

    /// <summary>The screen is closing: let go of the stations.</summary>
    internal virtual void Detach()
    {
    }
}

/// <summary>Fixed text.</summary>
public sealed class TextWidgetViewModel(Widget widget) : WidgetViewModel(widget)
{
    public string Text => Widget.Text ?? string.Empty;
}

/// <summary>A picture file from the project.</summary>
public sealed class LogoWidgetViewModel : WidgetViewModel
{
    public LogoWidgetViewModel(Widget widget, string projectFolder)
        : base(widget)
    {
        if (string.IsNullOrWhiteSpace(widget.File))
            return;

        var path = Path.GetFullPath(Path.Combine(projectFolder, widget.File));

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            Picture = bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or UnauthorizedAccessException)
        {
            Missing = $"No picture at {widget.File}";
        }
    }

    public BitmapSource? Picture { get; }

    public string? Missing { get; }
}

/// <summary>A station's picture, with what is drawn on it.</summary>
public sealed class ImageWidgetViewModel(Widget widget) : WidgetViewModel(widget)
{
    private BitmapSource? _picture;
    private ShapeSet? _shapes;

    /// <summary>Behind the image: the colour the screen gives it, or the viewer's own.</summary>
    public System.Windows.Media.Brush Background { get; } = ScreenColours.Brush(widget.Background);

    public BitmapSource? Picture
    {
        get => _picture;
        private set => SetProperty(ref _picture, value);
    }

    public ShapeSet? Shapes
    {
        get => _shapes;
        private set => SetProperty(ref _shapes, value);
    }

    internal override void Inspected(StationRunner station, InspectionSnapshot snapshot, bool drawPictures)
    {
        if (!drawPictures || Widget.Name is not { } name)
            return;

        switch (snapshot[name])
        {
            case ViewFrame view:
                Picture = MatImageConverter.ToBitmapSource(view.Image as ImageData);
                Shapes = Merge(view.Overlays.OfType<ShapeSet>());
                break;

            case ImageData image:
                Picture = MatImageConverter.ToBitmapSource(image);
                Shapes = null;
                break;

            default:
                Picture = null;
                Shapes = null;
                break;
        }
    }

    /// <summary>
    /// Every set drawn as one, in socket order, leaving out a set measured
    /// in another frame - as a viewer panel does.
    /// </summary>
    internal static ShapeSet? Merge(IEnumerable<ShapeSet> sets)
    {
        var drawn = sets.Where(set => set.Count > 0).ToArray();

        if (drawn.Length <= 1)
            return drawn.FirstOrDefault();

        var frame = drawn.FirstOrDefault(set => set.FrameWidth > 0 && set.FrameHeight > 0);
        var width = frame?.FrameWidth ?? 0;
        var height = frame?.FrameHeight ?? 0;

        return new ShapeSet(
            drawn.Where(set => set.MatchesFrame(width, height)).SelectMany(set => set.Shapes).ToArray(),
            width,
            height);
    }
}

/// <summary>How a verdict looks.</summary>
public enum VerdictLook
{
    None,
    Pass,
    Fail,
    Fault,
}

/// <summary>A station's PASS or FAIL.</summary>
public sealed class VerdictWidgetViewModel(Widget widget) : WidgetViewModel(widget)
{
    private string _text = "—";
    private string? _detail;
    private VerdictLook _look;

    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    /// <summary>The grade, or what went wrong.</summary>
    public string? Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public VerdictLook Look
    {
        get => _look;
        private set => SetProperty(ref _look, value);
    }

    internal override void Inspected(StationRunner station, InspectionSnapshot snapshot, bool drawPictures)
    {
        var verdict = snapshot.Verdict;

        (Text, Look) = verdict.Passed switch
        {
            true => ("PASS", VerdictLook.Pass),
            false => ("FAIL", VerdictLook.Fail),
            null => ("—", VerdictLook.None),
        };

        Detail = station.LastError ?? (verdict.Grade is { } grade && grade != Grade.Pass && grade != Grade.Fail ? grade.Name : null);
    }

    internal override void StationChanged(StationRunner station)
    {
        if (station.State != StationState.Faulted)
            return;

        Text = "FAULT";
        Look = VerdictLook.Fault;
        Detail = station.Problem;
    }
}

/// <summary>
/// A station's counts of pictures - or, given a controller, its counts of
/// parts (F25): rejects in place of fails, and the late and not inspected
/// said under them.
/// </summary>
public sealed class CounterWidgetViewModel(Widget widget) : WidgetViewModel(widget)
{
    private StationCounts _counts = StationCounts.Zero;
    private StationTiming _timing = StationTiming.Zero;
    private PartCounts? _parts;
    private PartRecord? _lastPart;

    /// <summary>The controller whose parts it counts, or null for a station's pictures.</summary>
    public string? Controller => string.IsNullOrWhiteSpace(Widget.Controller) ? null : Widget.Controller;

    public string Total => _parts is { } parts
        ? parts.Total.ToString(CultureInfo.CurrentCulture)
        : _counts.Total.ToString(CultureInfo.CurrentCulture);

    /// <summary>"Fail", or "Reject" for parts - failed, late and not inspected together.</summary>
    public string FailLabel => Controller is null ? "Fail" : "Reject";

    /// <summary>
    /// For a station, "Average 120 ms · worst 340 ms"; for parts, the late and
    /// not inspected and the last part; empty before any.
    /// </summary>
    public string Timing
    {
        get
        {
            if (Controller is not null)
                return PartLine();

            return _timing.Count == 0
                ? string.Empty
                : $"Average {Milliseconds(_timing.Average)} · worst {Milliseconds(_timing.Worst)}";
        }
    }

    private string PartLine()
    {
        if (_parts is not { Total: > 0 } parts)
            return string.Empty;

        var line = $"Late {parts.Late} · not inspected {parts.NotInspected}";

        return _lastPart is { } last
            ? $"{line} · last {last.Id}: {last.VerdictWord}"
            : line;
    }

    /// <summary>The controller's counts, as it has them now.</summary>
    internal void PartsChanged(PartCounts counts, PartRecord? last)
    {
        if (_parts == counts && ReferenceEquals(_lastPart, last))
            return;

        _parts = counts;
        _lastPart = last;
        OnPropertyChanged(string.Empty);
    }

    internal static string Milliseconds(TimeSpan time) =>
        time.TotalMilliseconds.ToString(time.TotalMilliseconds < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " ms";

    public string Passed => _parts is { } parts
        ? parts.Passed.ToString(CultureInfo.CurrentCulture)
        : _counts.Passed.ToString(CultureInfo.CurrentCulture);

    public string Failed => _parts is { } parts
        ? parts.Rejected.ToString(CultureInfo.CurrentCulture)
        : _counts.Failed.ToString(CultureInfo.CurrentCulture);

    public string Yield => _parts is { } parts
        ? parts.Total == 0 ? "—" : parts.Yield.ToString("0.0", CultureInfo.CurrentCulture) + " %"
        : _counts.Passed + _counts.Failed == 0
            ? "—"
            : _counts.Yield.ToString("0.0", CultureInfo.CurrentCulture) + " %";

    internal override void StationChanged(StationRunner station)
    {
        if (Controller is not null || (_counts == station.Counts && _timing == station.Timing))
            return;

        _counts = station.Counts;
        _timing = station.Timing;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>How a controller's PLC is, for the Controller widget's colour.</summary>
public enum ControllerLook
{
    Stopped,
    Connected,
    NotConnected,
}

/// <summary>A controller's PLC on the screen: connected or not, what last started a pass, what last went wrong (F23c).</summary>
public sealed class ControllerWidgetViewModel(Widget widget) : WidgetViewModel(widget)
{
    private ControllerLook _look;
    private string _state = "Not running";
    private string _detail = string.Empty;
    private string? _error;

    /// <summary>The controller it shows.</summary>
    public string? Controller => string.IsNullOrWhiteSpace(Widget.Controller) ? null : Widget.Controller;

    public string Name => Controller ?? "No controller";

    public ControllerLook Look
    {
        get => _look;
        private set => SetProperty(ref _look, value);
    }

    /// <summary>"Connected", "Not connected", "Not running".</summary>
    public string State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    /// <summary>What last started a pass, and how many passes: "Trigger: rising edge on M0.0 · 12 passes".</summary>
    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    /// <summary>What last went wrong, or null.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _error is not null;

    /// <summary>The controller as it is now; null when it is not running here.</summary>
    internal void Show(Sirius.Plc.Workflows.WorkflowRunner? runner)
    {
        if (runner is null || runner.State != Sirius.Plc.Workflows.ControllerState.Running)
        {
            Look = ControllerLook.Stopped;
            State = "Not running";
            Detail = string.Empty;
            Error = null;
            return;
        }

        Look = runner.IsConnected ? ControllerLook.Connected : ControllerLook.NotConnected;
        State = runner.IsConnected ? "Connected" : "Not connected";

        var passes = runner.Passes == 1 ? "1 pass" : $"{runner.Passes.ToString(CultureInfo.CurrentCulture)} passes";
        Detail = runner.LastEvent is { } last ? $"{last} · {passes}" : passes;
        Error = runner.LastError;
    }
}

/// <summary>One published result.</summary>
public sealed class ValueWidgetViewModel(Widget widget) : WidgetViewModel(widget)
{
    private string _value = "—";

    public string Label => string.IsNullOrWhiteSpace(Widget.Text) ? Widget.Name ?? string.Empty : Widget.Text!;

    /// <summary>The label above the value rather than beside it, so a long name has the width to itself.</summary>
    public bool IsLabelOnTop => Widget.LabelPlacement == LabelPlacement.Top;

    public string Value
    {
        get => _value;
        private set => SetProperty(ref _value, value);
    }

    internal override void Inspected(StationRunner station, InspectionSnapshot snapshot, bool drawPictures)
    {
        if (Widget.Name is { } name)
            Value = Format(snapshot[name], Widget.Format, Widget.Unit);
    }

    /// <summary>"12.346 mm", "Yes", "7 shapes", "B" - a value as a screen writes it.</summary>
    internal static string Format(object? value, string? format = null, string? unit = null)
    {
        var text = value switch
        {
            null => "—",
            double number => number.ToString(string.IsNullOrWhiteSpace(format) ? "0.###" : format, CultureInfo.CurrentCulture),
            float number => number.ToString(string.IsNullOrWhiteSpace(format) ? "0.###" : format, CultureInfo.CurrentCulture),
            int or long => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(string.IsNullOrWhiteSpace(format) ? "0" : format, CultureInfo.CurrentCulture),
            bool yes => yes ? "Yes" : "No",
            Grade grade => grade.Name,
            ShapeSet shapes => shapes.Count == 1 ? "1 shape" : $"{shapes.Count} shapes",
            ImageData or ViewFrame => "picture",
            string words => words,
            _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? "—",
        };

        return value is null || string.IsNullOrWhiteSpace(unit) ? text : $"{text} {unit}";
    }
}

/// <summary>
/// A button: trigger, run or stop, reset counts - for one station or all;
/// or open another screen, or close the one it is on.
/// </summary>
public sealed class ButtonWidgetViewModel : WidgetViewModel
{
    private readonly ProjectRuntime _runtime;
    private readonly IScreenNavigator? _screens;
    private readonly ScreenKind _on;
    private bool _running;

    /// <param name="screens">What opens and closes screens; none, and Open Screen and Close do nothing.</param>
    /// <param name="on">The kind of screen it is on: Close is "Close" on a popup, "Back" on a page.</param>
    public ButtonWidgetViewModel(Widget widget, ProjectRuntime runtime, IScreenNavigator? screens = null, ScreenKind on = ScreenKind.Page)
        : base(widget)
    {
        _runtime = runtime;
        _screens = screens;
        _on = on;
        Command = new AsyncRelayCommand(PressAsync);
    }

    public IAsyncRelayCommand Command { get; }

    public string Caption => Widget.Action switch
    {
        WidgetAction.RunStop when _running => Station is null ? "Stop All" : "Stop",
        _ when !string.IsNullOrWhiteSpace(Widget.Text) => Widget.Text!,
        WidgetAction.Trigger => Station is null ? "Trigger All" : "Trigger",
        WidgetAction.RunStop => Station is null ? "Run All" : "Run",
        WidgetAction.ResetCounts => "Reset Counts",
        WidgetAction.LogInOut => Access?.Session.ActionText ?? "Log In",
        WidgetAction.OpenScreen => string.IsNullOrWhiteSpace(Widget.Screen) ? "Open" : Widget.Screen!,
        WidgetAction.Close => ScreenSet.CloseCaption(_on),
        _ => "?",
    };

    /// <summary>A Run button showing Stop.</summary>
    public bool IsRunning => _running;

    private IReadOnlyList<StationRunner> Targets =>
        Station is null ? _runtime.Stations : _runtime.Find(Station) is { } one ? [one] : [];

    internal override void AccessChanged() => OnPropertyChanged(nameof(Caption));

    private async Task PressAsync()
    {
        if (Widget.Action == WidgetAction.LogInOut)
        {
            if (Access is not null)
                await Access.LogInOutAsync().ConfigureAwait(true);

            return;
        }

        // Locked: logged in at the level it needs - and, to open a screen, that screen's - or not done.
        var needed = ScreenLayout.NeedsFor(Widget);

        if (Widget.Action == WidgetAction.OpenScreen && Access?.NeedsToOpen(Widget.Screen) is { } opens && opens > needed)
            needed = opens;

        if (Access is not null && !await Access.EnsureAsync(needed, Caption).ConfigureAwait(true))
            return;

        switch (Widget.Action)
        {
            case WidgetAction.OpenScreen:
                _screens?.Open(Widget.Screen);
                return;

            case WidgetAction.Close:
                _screens?.Close();
                return;
        }

        var targets = Targets;

        switch (Widget.Action)
        {
            case WidgetAction.Trigger:
                await Task.WhenAll(targets.Select(station => station.TriggerAsync())).ConfigureAwait(true);
                break;

            case WidgetAction.RunStop when targets.Any(station => station.State == StationState.Running):
                await Task.WhenAll(targets.Select(station => station.StopAsync())).ConfigureAwait(true);
                break;

            case WidgetAction.RunStop:
                foreach (var station in targets)
                    station.Start();
                break;

            case WidgetAction.ResetCounts:
                foreach (var station in targets)
                    station.ResetCounts();

                // For every station: the controllers' parts too.
                if (Station is null && _screens is ScreenViewModel screen)
                    screen.ResetPartCounts();

                break;
        }
    }

    /// <summary>Any station change: a Run button follows whether its stations are running.</summary>
    internal void Refresh()
    {
        var running = Targets.Any(station => station.State is StationState.Running or StationState.Stopping);

        if (running == _running)
            return;

        _running = running;
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(IsRunning));
    }
}
