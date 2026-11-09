using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sirius.Core.Nodes;
using Sirius.Core.Runtime;
using Sirius.Core.Screens;
using Sirius.Core.Shapes;
using Sirius.Studio.App.Imaging;
using Sirius.Vision;

namespace Sirius.Studio.App.Screens;

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

/// <summary>A station's counts.</summary>
public sealed class CounterWidgetViewModel(Widget widget) : WidgetViewModel(widget)
{
    private StationCounts _counts = StationCounts.Zero;

    public string Total => _counts.Total.ToString(CultureInfo.CurrentCulture);

    public string Passed => _counts.Passed.ToString(CultureInfo.CurrentCulture);

    public string Failed => _counts.Failed.ToString(CultureInfo.CurrentCulture);

    public string Yield => _counts.Passed + _counts.Failed == 0
        ? "—"
        : _counts.Yield.ToString("0.0", CultureInfo.CurrentCulture) + " %";

    internal override void StationChanged(StationRunner station)
    {
        if (_counts == station.Counts)
            return;

        _counts = station.Counts;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>One published result.</summary>
public sealed class ValueWidgetViewModel(Widget widget) : WidgetViewModel(widget)
{
    private string _value = "—";

    public string Label => string.IsNullOrWhiteSpace(Widget.Text) ? Widget.Name ?? string.Empty : Widget.Text!;

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

/// <summary>A button: trigger, run or stop, reset counts - for one station or all.</summary>
public sealed class ButtonWidgetViewModel : WidgetViewModel
{
    private readonly ProjectRuntime _runtime;
    private bool _running;

    public ButtonWidgetViewModel(Widget widget, ProjectRuntime runtime)
        : base(widget)
    {
        _runtime = runtime;
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
        _ => "?",
    };

    /// <summary>A Run button showing Stop.</summary>
    public bool IsRunning => _running;

    private IReadOnlyList<StationRunner> Targets =>
        Station is null ? _runtime.Stations : _runtime.Find(Station) is { } one ? [one] : [];

    private async Task PressAsync()
    {
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
