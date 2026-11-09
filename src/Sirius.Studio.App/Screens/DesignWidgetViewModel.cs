using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Sirius.Core.Graph;
using Sirius.Core.Runtime;
using Sirius.Core.Screens;

namespace Sirius.Studio.App.Screens;

/// <summary>
/// A widget on the designer's canvas: where it is, and what it is bound to.
/// </summary>
/// <remarks>
/// Each property set from the properties panel is one undoable step, made
/// through <see cref="ScreenDesignerViewModel.Edit"/>. Moving and resizing
/// on the canvas go through <see cref="Place"/>, inside a gesture the
/// window opens and closes, so a drag is one step too.
/// </remarks>
public sealed class DesignWidgetViewModel : ObservableObject
{
    /// <summary>The smallest a widget can be made, in screen units.</summary>
    public const double MinimumSize = 20;

    private readonly ScreenDesignerViewModel _owner;
    private bool _isSelected;
    private IReadOnlyList<BindingChoice> _choices = [];
    private VisionGraph? _choicesFrom;

    public DesignWidgetViewModel(Widget widget, ScreenDesignerViewModel owner)
    {
        Widget = widget;
        _owner = owner;
        BrowseCommand = new RelayCommand(Browse);
        RefreshChoices();
    }

    public Widget Widget { get; }

    public WidgetKind Kind => Widget.Kind;

    public double X => Widget.X;

    public double Y => Widget.Y;

    public double Width => Widget.Width;

    public double Height => Widget.Height;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    // ---- what the canvas shows -----------------------------------------

    /// <summary>"Picture", "PASS / FAIL" - what kind of widget it is.</summary>
    public string KindLabel => Kind switch
    {
        WidgetKind.Image => "Picture",
        WidgetKind.Verdict => "PASS / FAIL",
        WidgetKind.Counter => "Total · Pass · Fail · Yield",
        WidgetKind.Button => "Button",
        WidgetKind.Setting => "Setting",
        WidgetKind.Logo => "Logo",
        WidgetKind.Value => "Value",
        _ => "Text",
    };

    /// <summary>What it shows: "Top › Diameter", "Trigger · all stations", the text itself.</summary>
    public string Caption => Kind switch
    {
        WidgetKind.Text => Widget.Text ?? string.Empty,
        WidgetKind.Button => $"{ButtonCaption}  ·  {(Widget.Station ?? "all stations")}",
        WidgetKind.Logo => Widget.File ?? "no picture chosen",
        _ when ScreenLayout.NeedsName(Kind) => $"{Widget.Station ?? "?"} › {Widget.Name ?? "?"}",
        _ => Widget.Station ?? "?",
    };

    private string ButtonCaption => !string.IsNullOrWhiteSpace(Widget.Text)
        ? Widget.Text!
        : Widget.Action switch
        {
            WidgetAction.RunStop => "Run / Stop",
            WidgetAction.ResetCounts => "Reset Counts",
            _ => "Trigger",
        };

    /// <summary>The logo itself, for a Logo on the canvas; null for anything else or a picture not found.</summary>
    public string? LogoPath =>
        Kind == WidgetKind.Logo && !string.IsNullOrWhiteSpace(Widget.File) &&
        System.IO.File.Exists(Path.Combine(_owner.Recipes.Project.Folder, Widget.File!))
            ? Path.GetFullPath(Path.Combine(_owner.Recipes.Project.Folder, Widget.File!))
            : null;

    // ---- which properties it has ----------------------------------------

    public bool HasStation => ScreenLayout.NeedsStation(Kind) || Kind == WidgetKind.Button;

    public bool HasName => ScreenLayout.NeedsName(Kind);

    public bool HasText => Kind is WidgetKind.Text or WidgetKind.Button or WidgetKind.Value or WidgetKind.Setting;

    public bool HasAction => Kind == WidgetKind.Button;

    public bool HasNumberFormat => Kind is WidgetKind.Value or WidgetKind.Setting;

    public bool HasFile => Kind == WidgetKind.Logo;

    public bool HasFontSize => Kind is not (WidgetKind.Image or WidgetKind.Logo);

    /// <summary>What the Text box is called for this kind.</summary>
    public string TextLabel => Kind switch
    {
        WidgetKind.Button => "Caption (blank for the usual)",
        WidgetKind.Value or WidgetKind.Setting => "Label (blank for the name)",
        _ => "Text",
    };

    public IReadOnlyList<StationOption> StationOptions =>
        Kind == WidgetKind.Button ? _owner.ButtonStationOptions : _owner.StationOptions;

    // ---- the properties ---------------------------------------------------

    /// <summary>The station, as the station list's value: empty is every station, for a button.</summary>
    public string StationValue
    {
        get => Widget.Station ?? string.Empty;
        set
        {
            var station = string.IsNullOrEmpty(value) ? null : value;

            if (station == Widget.Station)
                return;

            _owner.Edit(() => Widget.Station = station);
            RefreshChoices();
            Raise();
        }
    }

    /// <summary>What the widget can show from its station's recipe: published names, then what would be published.</summary>
    public IReadOnlyList<BindingChoice> Choices => _choices;

    /// <summary>The choice it is bound to, or null when its name is not found.</summary>
    public BindingChoice? Choice
    {
        get => _choices.FirstOrDefault(choice =>
            choice.IsPublished && string.Equals(choice.Name, Widget.Name, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null || _choicesFrom is not { } recipe || Widget.Station is not { } station)
                return;

            if (value.IsPublished && string.Equals(value.Name, Widget.Name, StringComparison.OrdinalIgnoreCase))
                return;

            // Binding publishes when it has to: the output is labelled in
            // its recipe, and nothing is wired.
            var (name, published) = ScreenBinding.Bind(recipe, value);

            if (published)
            {
                var written = _owner.Recipes.Published(station, recipe);

                _owner.StatusText = written
                    ? $"Published {value.Source} as '{name}' in {station}'s recipe, and saved it."
                    : $"Published {value.Source} as '{name}'. Save the recipe in Studio to keep it.";
            }

            _owner.Edit(() => Widget.Name = name);
            RefreshChoices();
            Raise();
        }
    }

    /// <summary>Said under the name when it is not among the station's published names.</summary>
    public string? NameProblem =>
        HasName && !string.IsNullOrWhiteSpace(Widget.Name) && Choice is null
            ? $"'{Widget.Name}' is not published by {Widget.Station ?? "any station"}."
            : null;

    public string? Text
    {
        get => Widget.Text;
        set => Set(() => Widget.Text = string.IsNullOrEmpty(value) ? null : value, Widget.Text, value);
    }

    public WidgetAction Action
    {
        get => Widget.Action ?? WidgetAction.Trigger;
        set => Set(() => Widget.Action = value, Widget.Action, value);
    }

    public double FontSize
    {
        get => Widget.FontSize;
        set => Set(() => Widget.FontSize = Math.Clamp(value, 6, 200), Widget.FontSize, value);
    }

    public string? Format
    {
        get => Widget.Format;
        set => Set(() => Widget.Format = string.IsNullOrWhiteSpace(value) ? null : value, Widget.Format, value);
    }

    public string? Unit
    {
        get => Widget.Unit;
        set => Set(() => Widget.Unit = string.IsNullOrWhiteSpace(value) ? null : value, Widget.Unit, value);
    }

    public string? File
    {
        get => Widget.File;
        set => Set(() => Widget.File = string.IsNullOrWhiteSpace(value) ? null : value, Widget.File, value);
    }

    // ---- a setting on the line ---------------------------------------------

    public bool HasSettingOptions => Kind == WidgetKind.Setting;

    /// <summary>The setting the widget shows, in its station's recipe, or null.</summary>
    private Tunable? BoundSetting =>
        HasSettingOptions && _choicesFrom is { } recipe && !string.IsNullOrWhiteSpace(Widget.Name)
            ? Tunable.Find(recipe, Widget.Name!)
            : null;

    /// <summary>A setting a screen has a control for - or none bound yet, which may become one.</summary>
    public bool IsChangeableKind => BoundSetting is not { IsChangeable: false };

    /// <summary>Unticked, the screen shows the setting without a control.</summary>
    public bool OperatorMayChange
    {
        get => !Widget.ReadOnly && IsChangeableKind;
        set => Set(() => Widget.ReadOnly = !value, !Widget.ReadOnly, value);
    }

    /// <summary>A number the operator may change: its limits on the line can be set.</summary>
    public bool HasLimits => BoundSetting is { IsNumeric: true } && !Widget.ReadOnly;

    /// <summary>The lowest it may be set to on the line; blank for as low as the node allows.</summary>
    public string LowestText
    {
        get => LimitText(BoundSetting?.Entry.Lowest);
        set => SetLimit(value, lowest: true);
    }

    public string HighestText
    {
        get => LimitText(BoundSetting?.Entry.Highest);
        set => SetLimit(value, lowest: false);
    }

    /// <summary>What the node itself allows, and anything else worth knowing about the setting.</summary>
    public string? SettingNote
    {
        get
        {
            if (BoundSetting is not { } setting)
                return null;

            if (setting.IsWired)
                return "A wire in the recipe sets this, so the screen only shows it.";

            if (!setting.IsChangeable)
                return "A screen has no control for this kind of setting yet; it is shown only.";

            if (Widget.ReadOnly)
                return null;

            if (!setting.IsNumeric)
                return setting.Kind == TunableKind.Toggle ? "Shown as an On / Off switch." : "Shown as a list to choose from.";

            var low = setting.NodeMinimum;
            var high = setting.NodeMaximum;
            var own = (double.IsFinite(low), double.IsFinite(high)) switch
            {
                (true, true) => $"{setting.Format(low)} – {setting.Format(high)}",
                (true, false) => $"{setting.Format(low)} or more",
                (false, true) => $"{setting.Format(high)} or less",
                _ => "any value",
            };

            return $"{setting.Source} itself allows {own}. Limits are kept in the recipe, with the setting's name.";
        }
    }

    private static string LimitText(double? limit) =>
        limit is { } value ? value.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>
    /// Sets one end of the setting's range on the line, in its recipe - as
    /// binding publishes there: the editor's recipe for the station open in
    /// Studio, saved with it; any other station's file at once.
    /// </summary>
    private void SetLimit(string? text, bool lowest)
    {
        if (BoundSetting is not { } setting || _choicesFrom is not { } recipe || Widget.Station is not { } station)
            return;

        double? limit = null;

        if (!string.IsNullOrWhiteSpace(text))
        {
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var parsed) ||
                !double.IsFinite(parsed))
            {
                _owner.StatusText = $"'{text}' is not a number.";
                Raise();
                return;
            }

            limit = parsed;
        }

        var low = lowest ? limit : setting.Entry.Lowest;
        var high = lowest ? setting.Entry.Highest : limit;

        if (Publishing.LimitProblem(low, high) is { } problem)
        {
            _owner.StatusText = problem;
            Raise();
            return;
        }

        if (!Publishing.Limit(setting.Node, setting.Entry.Member, low, high))
            return;

        var range = Tunable.For(setting.Node, Publishing.NameOf(setting.Node, PublishedKind.Setting, setting.Entry.Member)!)!.RangeText;
        var written = _owner.Recipes.Published(station, recipe);

        _owner.StatusText = written
            ? $"{setting.Name} may be set to {range} on the line; saved in {station}'s recipe."
            : $"{setting.Name} may be set to {range} on the line. Save the recipe in Studio to keep it.";

        _owner.Changed();
        Raise();
    }

    /// <summary>Position and size, typed in the properties panel.</summary>
    public double EditX
    {
        get => Widget.X;
        set => _owner.Edit(() => Place(value, Y, Width, Height));
    }

    public double EditY
    {
        get => Widget.Y;
        set => _owner.Edit(() => Place(X, value, Width, Height));
    }

    public double EditWidth
    {
        get => Widget.Width;
        set => _owner.Edit(() => Place(X, Y, value, Height));
    }

    public double EditHeight
    {
        get => Widget.Height;
        set => _owner.Edit(() => Place(X, Y, Width, value));
    }

    public IRelayCommand BrowseCommand { get; }

    /// <summary>
    /// Puts the widget at a place and size, kept on the canvas and no
    /// smaller than <see cref="MinimumSize"/>. Not undoable on its own: the
    /// caller is inside an edit or a gesture.
    /// </summary>
    public void Place(double x, double y, double width, double height)
    {
        width = Math.Clamp(width, MinimumSize, _owner.Width);
        height = Math.Clamp(height, MinimumSize, _owner.Height);
        x = Math.Clamp(x, 0, _owner.Width - width);
        y = Math.Clamp(y, 0, _owner.Height - height);

        if (x == Widget.X && y == Widget.Y && width == Widget.Width && height == Widget.Height)
            return;

        Widget.X = x;
        Widget.Y = y;
        Widget.Width = width;
        Widget.Height = height;

        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
        OnPropertyChanged(nameof(EditX));
        OnPropertyChanged(nameof(EditY));
        OnPropertyChanged(nameof(EditWidth));
        OnPropertyChanged(nameof(EditHeight));

        _owner.Changed();
    }

    /// <summary>Reads the station's recipe again for what the widget can show.</summary>
    internal void RefreshChoices()
    {
        _choicesFrom = HasName ? _owner.Recipes.Recipe(Widget.Station) : null;
        _choices = _choicesFrom is null ? [] : ScreenBinding.Choices(_choicesFrom, Kind);

        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(Choice));
        OnPropertyChanged(nameof(NameProblem));
    }

    /// <summary>
    /// Picks a picture for a logo. One from outside the project is copied
    /// into its screens folder, so the project folder stays complete.
    /// </summary>
    private void Browse()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            Title = "Logo",
        };

        if (dialog.ShowDialog() != true)
            return;

        var folder = _owner.Recipes.Project.Folder;
        var chosen = Path.GetFullPath(dialog.FileName);
        string relative;

        if (chosen.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            relative = Path.GetRelativePath(folder, chosen);
        }
        else
        {
            var target = Path.Combine(folder, ScreenLayout.ScreensFolder, Path.GetFileName(chosen));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                System.IO.File.Copy(chosen, target, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _owner.StatusText = $"Could not copy the picture into the project: {ex.Message}";
                return;
            }

            relative = Path.GetRelativePath(folder, target);
        }

        File = relative.Replace('\\', '/');
    }

    private void Set<T>(Action change, T? current, T? wanted)
    {
        if (EqualityComparer<T?>.Default.Equals(current, wanted))
            return;

        _owner.Edit(change);
        Raise();
    }

    /// <summary>Everything shown from the widget, after any change to it.</summary>
    private void Raise()
    {
        OnPropertyChanged(string.Empty);
    }
}
