using System.Globalization;
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
/// A number is typed, or stepped with − and +; on or off is a switch; a
/// choice is a list. A value outside the range is refused with what the
/// range is, and the box goes back to what the setting holds. A setting
/// the screen has no control for, or one the designer made read-only, is
/// only shown.
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
    private string? _problem;

    public SettingWidgetViewModel(Widget widget, ProjectRuntime runtime)
        : base(widget)
    {
        _station = Station is null ? null : runtime.Find(Station);
        _setting = Widget.Name is { } name ? _station?.Setting(name) : null;

        LowerCommand = new RelayCommand(() => StepBy(-1), () => CanStep(-1));
        RaiseCommand = new RelayCommand(() => StepBy(+1), () => CanStep(+1));

        if (_station is not null)
            _station.SettingChanged += OnSettingChanged;
    }

    public string Label => string.IsNullOrWhiteSpace(Widget.Text) ? Widget.Name ?? string.Empty : Widget.Text!;

    /// <summary>The station has the setting, the screen may change it, and has a control for it.</summary>
    public bool CanChange => !Widget.ReadOnly && _setting is { IsChangeable: true } && _station is not null;

    public bool IsNumberEditor => CanChange && _setting!.IsNumeric;

    public bool IsToggleEditor => CanChange && _setting!.Kind == TunableKind.Toggle;

    public bool IsChoiceEditor => CanChange && _setting!.Kind == TunableKind.Choice;

    public bool IsDisplayOnly => !CanChange;

    private object? Current => _setting is null || _station is null ? null : _station.SettingValue(_setting.Name);

    /// <summary>What it is set to, as shown: "128 mm", "On", "Dark".</summary>
    public string DisplayText => Current switch
    {
        bool on => on ? "On" : "Off",
        Enum choice => choice.ToString(),
        var value => ValueWidgetViewModel.Format(value, Widget.Format, Widget.Unit),
    };

    /// <summary>The number in the box: typed, it is committed on Enter or on leaving the box.</summary>
    public string ValueText
    {
        get
        {
            if (Current is not { } value || _setting is null)
                return string.Empty;

            var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);

            return string.IsNullOrWhiteSpace(Widget.Format)
                ? _setting.Format(number)
                : number.ToString(Widget.Format, CultureInfo.CurrentCulture);
        }
        set
        {
            if (value == ValueText)
                return;

            Change(value);
        }
    }

    public string? Unit => Widget.Unit;

    public bool HasUnit => !string.IsNullOrWhiteSpace(Widget.Unit);

    public bool IsOn
    {
        get => Current is true;
        set
        {
            if (value != IsOn)
                Change(value);
        }
    }

    public IReadOnlyList<string> Choices => _setting?.Choices ?? [];

    public string? SelectedChoice
    {
        get => Current?.ToString();
        set
        {
            if (value is not null && value != SelectedChoice)
                Change(value);
        }
    }

    /// <summary>"Limit: 0 – 200", or what it is when it has no range.</summary>
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

    /// <summary>The setting may now hold another value, or have another range: the image's bit depth.</summary>
    internal override void Inspected(StationRunner station, InspectionSnapshot snapshot, bool drawPictures) => Refresh();

    internal override void Detach()
    {
        if (_station is not null)
            _station.SettingChanged -= OnSettingChanged;
    }

    private void OnSettingChanged(object? sender, string name)
    {
        if (string.Equals(name, _setting?.Name, StringComparison.OrdinalIgnoreCase))
            Refresh();
    }

    private void Change(object? value)
    {
        if (_station is null || _setting is null)
            return;

        Problem = _station.ChangeSetting(_setting.Name, value);

        // Refused: the box goes back to what the setting holds.
        Refresh();
    }

    private void StepBy(int direction)
    {
        if (_setting is null || Current is not { } value)
            return;

        var next = Convert.ToDouble(value, CultureInfo.InvariantCulture) + (direction * _setting.Step);
        Change(Math.Clamp(next, _setting.Minimum, _setting.Maximum));
    }

    private bool CanStep(int direction)
    {
        if (!IsNumberEditor || Current is not { } value)
            return false;

        var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return direction < 0 ? number > _setting!.Minimum : number < _setting!.Maximum;
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(SelectedChoice));
        OnPropertyChanged(nameof(Hint));
        LowerCommand.NotifyCanExecuteChanged();
        RaiseCommand.NotifyCanExecuteChanged();
    }
}
