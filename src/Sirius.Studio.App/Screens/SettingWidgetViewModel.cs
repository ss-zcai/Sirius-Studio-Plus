using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sirius.Core.Runtime;
using Sirius.Core.Screens;

namespace Sirius.Studio.App.Screens;

/// <summary>
/// A published setting on a running screen, changed there within its limits
/// (runtime MVP step 6).
/// </summary>
/// <remarks>
/// <para>
/// A number has the control the designer chose (<see cref="SettingStyle"/>):
/// − and + either side of a box, a slider, a box alone, or - for a low and a
/// high setting together - one slider with two handles. On or off is a
/// switch; a choice is a list. A value outside the range is refused with
/// what the range is, and the box goes back to what the setting holds. A
/// setting the screen has no control for, or one the designer made
/// read-only, is only shown.
/// </para>
/// <para>
/// A slider needs both ends. One whose setting has no lowest or no highest
/// is shown with − and + instead, and the hint says why, so a screen never
/// shows a control that cannot work.
/// </para>
/// <para>
/// The station takes the change now, or after the inspection in hand, and
/// keeps it for the next start (<see cref="StationRunner.ChangeSetting"/>).
/// </para>
/// </remarks>
public sealed class SettingWidgetViewModel : WidgetViewModel
{
    private readonly StationRunner? _station;
    private readonly Tunable? _setting;
    private readonly Tunable? _high;
    private string? _problem;
    private string? _shown;

    public SettingWidgetViewModel(Widget widget, ProjectRuntime runtime)
        : base(widget)
    {
        _station = Station is null ? null : runtime.Find(Station);
        _setting = Widget.Name is { } name ? _station?.Setting(name) : null;
        _high = Widget.Style == SettingStyle.Range && Widget.HighName is { } high ? _station?.Setting(high) : null;

        LowerCommand = new RelayCommand(() => StepBy(-1), () => CanStep(-1));
        RaiseCommand = new RelayCommand(() => StepBy(+1), () => CanStep(+1));

        if (_station is not null)
            _station.SettingChanged += OnSettingChanged;

        _shown = Shown();
    }

    /// <summary>The label: what the designer typed, or the setting's name - "Low – High" for a range.</summary>
    public string Label
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Widget.Text))
                return Widget.Text!;

            return Widget.Style == SettingStyle.Range && !string.IsNullOrWhiteSpace(Widget.HighName)
                ? $"{Widget.Name} – {Widget.HighName}"
                : Widget.Name ?? string.Empty;
        }
    }

    // ---- which control --------------------------------------------------------

    /// <summary>The station has the setting, the screen may change it, and has a control for it.</summary>
    public bool CanChange => !Widget.ReadOnly && _setting is { IsChangeable: true } && _station is not null;

    /// <summary>A number the operator may change - with whichever control <see cref="Style"/> says.</summary>
    public bool IsNumberEditor => CanChange && _setting!.IsNumeric && !IsRangeBlocked;

    /// <summary>The control a number is shown with: the one chosen, or − and + when a slider has no ends.</summary>
    public SettingStyle Style => !IsNumberEditor
        ? SettingStyle.Stepper
        : Widget.Style switch
        {
            SettingStyle.Slider when _setting!.IsBounded => SettingStyle.Slider,
            SettingStyle.Range when IsRangeReady => SettingStyle.Range,
            SettingStyle.Box => SettingStyle.Box,
            _ => SettingStyle.Stepper,
        };

    public bool IsStepperEditor => IsNumberEditor && Style == SettingStyle.Stepper;

    public bool IsSliderEditor => IsNumberEditor && Style == SettingStyle.Slider;

    public bool IsBoxEditor => IsNumberEditor && Style == SettingStyle.Box;

    public bool IsRangeEditor => IsNumberEditor && Style == SettingStyle.Range;

    public bool IsToggleEditor => CanChange && _setting!.Kind == TunableKind.Toggle;

    public bool IsChoiceEditor => CanChange && _setting!.Kind == TunableKind.Choice;

    public bool IsDisplayOnly => !CanChange || IsRangeBlocked;

    /// <summary>
    /// A range whose high setting is there but cannot be a slider: no ends,
    /// or not changeable. Both are shown, neither changed - − and + on the
    /// low end alone would read as the whole range.
    /// </summary>
    private bool IsRangeBlocked => Widget.Style == SettingStyle.Range && _high is not null && !IsRangeReady;

    /// <summary>The high setting is there, a number the screen may change, and the two have a track between them.</summary>
    private bool IsRangeReady =>
        _high is { IsChangeable: true, IsNumeric: true } &&
        double.IsFinite(RangeMinimum) && double.IsFinite(RangeMaximum) && RangeMaximum > RangeMinimum;

    // ---- where the label goes ---------------------------------------------------

    public bool IsLabelOnTop => Widget.LabelPlacement == LabelPlacement.Top;

    /// <summary>A slider takes what room there is; − and +, a box, a switch or a list take what they need.</summary>
    private bool IsWide => IsSliderEditor || IsRangeEditor;

    /// <summary>Above: the label's own row. Beside: one row, label then control.</summary>
    public int EditorRow => IsLabelOnTop ? 1 : 0;

    public int EditorColumn => IsLabelOnTop ? 0 : 1;

    public int EditorColumnSpan => IsLabelOnTop ? 2 : 1;

    /// <summary>Beside a slider the label takes only what it needs, so the slider has the rest.</summary>
    public GridLength LabelWidth => !IsLabelOnTop && IsWide ? GridLength.Auto : new GridLength(1, GridUnitType.Star);

    public GridLength EditorWidth => !IsLabelOnTop && IsWide ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;

    /// <summary>Above, the control spans the widget; beside, a narrow one sits on the right.</summary>
    public HorizontalAlignment EditorAlignment => IsLabelOnTop || IsWide ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;

    /// <summary>The number box: a fixed width beside the label, the room between − and + above it.</summary>
    public double BoxWidth => IsLabelOnTop && IsStepperEditor ? double.NaN : 90;

    /// <summary>The gap between a label above and its control, in the screen's units.</summary>
    public const double LabelGap = 8;

    public Thickness LabelMargin => IsLabelOnTop ? new Thickness(0, 0, 0, LabelGap) : new Thickness(0, 0, 8, 0);

    // ---- the values ------------------------------------------------------------------

    private object? Current => ValueOf(_setting);

    private object? ValueOf(Tunable? setting) =>
        setting is null || _station is null ? null : _station.SettingValue(setting.Name);

    /// <summary>What it is set to, as shown: "128 mm", "On", "Dark" - "40 – 60 mm" for a range.</summary>
    public string DisplayText
    {
        get
        {
            if (Widget.Style == SettingStyle.Range && _high is not null)
                return $"{ValueWidgetViewModel.Format(Current, Widget.Format, null)} – {ValueWidgetViewModel.Format(ValueOf(_high), Widget.Format, Widget.Unit)}";

            return Current switch
            {
                bool on => on ? "On" : "Off",
                Enum choice => choice.ToString(),
                var value => ValueWidgetViewModel.Format(value, Widget.Format, Widget.Unit),
            };
        }
    }

    /// <summary>The number in the box: typed, it is committed on Enter or on leaving the box.</summary>
    public string ValueText
    {
        get => NumberText(_setting);
        set
        {
            if (value != ValueText)
                Change(_setting, value);
        }
    }

    /// <summary>A range's high end, in its own box.</summary>
    public string HighValueText
    {
        get => NumberText(_high);
        set
        {
            if (value != HighValueText)
                Change(_high, value);
        }
    }

    private string NumberText(Tunable? setting)
    {
        if (ValueOf(setting) is not { } value || setting is null)
            return string.Empty;

        var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);

        return string.IsNullOrWhiteSpace(Widget.Format)
            ? setting.Format(number)
            : number.ToString(Widget.Format, CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// A slider's value; a range's low end. Set when a drag ends - the
    /// slider is bound with an explicit update - held to the setting's own
    /// range, as a slider cannot be dragged outside it.
    /// </summary>
    public double SliderValue
    {
        get => NumberOf(_setting);
        set => Slide(_setting, value);
    }

    /// <summary>A range's high end.</summary>
    public double HighSliderValue
    {
        get => NumberOf(_high);
        set => Slide(_high, value);
    }

    /// <summary>A slider's left end: the setting's lowest - for a range, the lower of the two settings' lowest.</summary>
    public double SliderMinimum => IsRangeEditor ? RangeMinimum : _setting?.Minimum is { } low && double.IsFinite(low) ? low : 0;

    public double SliderMaximum => IsRangeEditor ? RangeMaximum : _setting?.Maximum is { } high && double.IsFinite(high) ? high : 0;

    public double SliderStep => _setting?.Step ?? 1;

    private double RangeMinimum => _setting is null || _high is null ? double.NaN : Tunable.Track(_setting, _high).Minimum;

    private double RangeMaximum => _setting is null || _high is null ? double.NaN : Tunable.Track(_setting, _high).Maximum;

    private double NumberOf(Tunable? setting) =>
        ValueOf(setting) is { } value ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : 0;

    public string? Unit => Widget.Unit;

    public bool HasUnit => !string.IsNullOrWhiteSpace(Widget.Unit);

    public bool IsOn
    {
        get => Current is true;
        set
        {
            if (value != IsOn)
                Change(_setting, value);
        }
    }

    public IReadOnlyList<string> Choices => _setting?.Choices ?? [];

    public string? SelectedChoice
    {
        get => Current?.ToString();
        set
        {
            if (value is not null && value != SelectedChoice)
                Change(_setting, value);
        }
    }

    /// <summary>"Limit: 0 – 200", or what it is when it has no range, or why the chosen control is not shown.</summary>
    public string Hint
    {
        get
        {
            if (_station is null)
                return $"No station '{Widget.Station}'.";

            if (_setting is null)
                return $"{_station.Name} publishes no setting '{Widget.Name}'.";

            if (Widget.ReadOnly)
                return $"{_setting.Source} - shown here, not changed.";

            if (_setting.IsWired)
                return $"{_setting.Source} - set by a wire in the recipe, so shown only.";

            if (!_setting.IsChangeable)
                return $"{_setting.Source} - a screen has no control for this kind of setting.";

            if (Widget.Style == SettingStyle.Range && _setting.IsNumeric)
            {
                if (_high is null)
                    return $"{_station.Name} publishes no setting '{Widget.HighName}' for the high end, so {_setting.Name} is shown with − and +.";

                if (!_high.IsChangeable || !_high.IsNumeric)
                    return $"{_high.Source} cannot be changed from a screen, so the range is shown only.";

                if (!IsRangeReady)
                    return $"A range slider needs a lowest for {_setting.Name} and a highest for {_high.Name}; set them in Sirius Builder. Shown only until then.";

                return $"{_setting.Source}: {_setting.RangeText}\n{_high.Source}: {_high.RangeText}";
            }

            if (Widget.Style == SettingStyle.Slider && _setting.IsNumeric && !_setting.IsBounded)
                return $"{_setting.Source}: a slider needs a lowest and a highest; set them in Sirius Builder. Shown with − and + until then.";

            return _setting.RangeText.Length > 0
                ? $"{_setting.Source}: {_setting.RangeText}"
                : _setting.Source;
        }
    }

    /// <summary>Why the last change was refused, or null.</summary>
    public string? Problem
    {
        get => _problem;
        private set
        {
            if (SetProperty(ref _problem, value))
                OnPropertyChanged(nameof(HasProblem));
        }
    }

    public bool HasProblem => _problem is not null;

    public IRelayCommand LowerCommand { get; }

    public IRelayCommand RaiseCommand { get; }

    /// <summary>
    /// The setting may now hold another value, or have another range: the
    /// image's bit depth. Only what changed is shown again - an inspection
    /// every part must not put back a box being typed in, or a handle being
    /// dragged.
    /// </summary>
    internal override void Inspected(StationRunner station, InspectionSnapshot snapshot, bool drawPictures) => Refresh(force: false);

    internal override void Detach()
    {
        if (_station is not null)
            _station.SettingChanged -= OnSettingChanged;
    }

    private void OnSettingChanged(object? sender, string name)
    {
        if (string.Equals(name, _setting?.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, _high?.Name, StringComparison.OrdinalIgnoreCase))
        {
            Refresh(force: true);
        }
    }

    private void Change(Tunable? setting, object? value)
    {
        if (_station is null || setting is null)
            return;

        Problem = _station.ChangeSetting(setting.Name, value);

        // Refused: the box goes back to what the setting holds.
        Refresh(force: true);
    }

    /// <summary>A slider's handle let go: within the setting's own range, and only if it moved.</summary>
    private void Slide(Tunable? setting, double value)
    {
        if (setting is null || !double.IsFinite(value))
            return;

        var held = Math.Clamp(value, setting.Minimum, setting.Maximum);

        if (held == NumberOf(setting))
        {
            // Pushed past the end and held there: the handle goes back. Not
            // moved at all - the other end of a range was - nothing is shown
            // again, which would put that other handle back before it is sent.
            if (held != value)
                Refresh(force: true);

            return;
        }

        Change(setting, held);
    }

    private void StepBy(int direction)
    {
        if (_setting is null || Current is not { } value)
            return;

        var next = Convert.ToDouble(value, CultureInfo.InvariantCulture) + (direction * _setting.Step);
        Change(_setting, Math.Clamp(next, _setting.Minimum, _setting.Maximum));
    }

    private bool CanStep(int direction)
    {
        if (!IsNumberEditor || Current is not { } value)
            return false;

        var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return direction < 0 ? number > _setting!.Minimum : number < _setting!.Maximum;
    }

    /// <summary>Everything a change could alter, as one string: unchanged, nothing need be shown again.</summary>
    private string Shown() =>
        string.Join('|', DisplayText, ValueText, HighValueText, SliderMinimum, SliderMaximum, Style, Hint);

    private void Refresh(bool force)
    {
        var shown = Shown();

        if (!force && shown == _shown)
            return;

        _shown = shown;

        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(HighValueText));
        OnPropertyChanged(nameof(SliderValue));
        OnPropertyChanged(nameof(HighSliderValue));
        OnPropertyChanged(nameof(SliderMinimum));
        OnPropertyChanged(nameof(SliderMaximum));
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(SelectedChoice));
        OnPropertyChanged(nameof(Hint));
        LowerCommand.NotifyCanExecuteChanged();
        RaiseCommand.NotifyCanExecuteChanged();
    }
}
