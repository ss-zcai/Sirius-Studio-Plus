using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Sirius.Core.Runtime;
using Sirius.Core.Screens;
using Sirius.Studio.App;

namespace Sirius.Builder;

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
    private Sirius.Core.Graph.VisionGraph? _choicesFrom;

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

    /// <summary>"Image Viewer", "PASS / FAIL" - what kind of widget it is.</summary>
    public string KindLabel => Kind switch
    {
        WidgetKind.Image => "Image Viewer",
        WidgetKind.Verdict => "PASS / FAIL",
        WidgetKind.Counter => "Total · Pass · Fail · Yield",
        WidgetKind.Button => "Button",
        WidgetKind.Setting => Widget.Style == SettingStyle.Stepper ? "Setting" : $"Setting · {StyleLabel(Widget.Style)}",
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
        WidgetKind.Setting when Widget.Style == SettingStyle.Range => $"{Widget.Station ?? "?"} › {Widget.Name ?? "?"} – {Widget.HighName ?? "?"}",
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

    /// <summary>An Image Viewer has a colour behind its image.</summary>
    public bool HasBackground => Kind == WidgetKind.Image;

    /// <summary>"Default", "Black", or the "#RRGGBB" kept - as the Background box shows it.</summary>
    public string BackgroundText
    {
        get => Sirius.Studio.App.Screens.ScreenColours.Describe(Widget.Background);
        set
        {
            var colour = Sirius.Studio.App.Screens.ScreenColours.Normalise(value, out var valid);

            if (!valid)
            {
                _owner.StatusText = $"'{value}' is not a colour. Type a name such as Black, or #RRGGBB.";
                Raise();
                return;
            }

            Set(() => Widget.Background = colour, Widget.Background, colour);
        }
    }

    /// <summary>The widget's colour on the designer's canvas, as the running screen will show it.</summary>
    public System.Windows.Media.Brush PreviewBackground => Sirius.Studio.App.Screens.ScreenColours.Brush(Widget.Background);

    /// <summary>The canvas caption's colour: dark over a light background, light over a dark one.</summary>
    public System.Windows.Media.Brush PreviewForeground =>
        Sirius.Studio.App.Screens.ScreenColours.Parse(Widget.Background) is { } colour &&
        (0.299 * colour.R) + (0.587 * colour.G) + (0.114 * colour.B) > 150
            ? System.Windows.Media.Brushes.Black
            : System.Windows.Media.Brushes.Gainsboro;

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

    /// <summary>What the widget can show from its station's recipe: the names it publishes, of the right kind.</summary>
    public IReadOnlyList<BindingChoice> Choices => _choices;

    /// <summary>The station's recipe publishes nothing this widget could show.</summary>
    public bool HasNoChoices => HasName && _choicesFrom is not null && _choices.Count == 0;

    /// <summary>The choice it is bound to, or null when its name is not found.</summary>
    public BindingChoice? Choice
    {
        get => _choices.FirstOrDefault(choice =>
            choice.IsPublished && string.Equals(choice.Name, Widget.Name, StringComparison.OrdinalIgnoreCase));
        set
        {
            // Only what the recipe publishes: Studio publishes, Builder binds.
            if (value is not { Name: { } name } || string.Equals(name, Widget.Name, StringComparison.OrdinalIgnoreCase))
                return;

            _owner.Edit(() => Widget.Name = name);
            RefreshChoices();
            Raise();
        }
    }

    /// <summary>Said under the name when it is not among the station's published names, or there are none.</summary>
    public string? NameProblem
    {
        get
        {
            if (!HasName)
                return null;

            if (Widget.Station is { } station && _choicesFrom is null)
                return $"{station}'s recipe could not be read.";

            if (!string.IsNullOrWhiteSpace(Widget.Name) && Choice is null)
                return $"'{Widget.Name}' is not published by {Widget.Station ?? "any station"}. Publish it in Studio, or choose another.";

            if (HasNoChoices)
                return $"{Widget.Station}'s recipe publishes nothing of this kind yet. In Studio, right-click an output, a setting or a viewer, choose Publish, and save the recipe.";

            return null;
        }
    }

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

    /// <summary>The setting the widget shows, in its station's recipe, with the station's limit for it - or null.</summary>
    private Tunable? BoundSetting =>
        HasSettingOptions && _choicesFrom is { } recipe && !string.IsNullOrWhiteSpace(Widget.Name)
            ? Tunable.Find(recipe, Widget.Name!, _owner.Recipes.Station(Widget.Station)?.LimitFor(Widget.Name!))
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

    /// <summary>A number, or nothing bound yet: it can be shown with − and +, a slider, a box or as a range.</summary>
    public bool HasStyle => HasSettingOptions && BoundSetting is not { IsNumeric: false };

    public SettingStyle Style
    {
        get => Widget.Style;
        set => Set(() => Widget.Style = value, Widget.Style, value);
    }

    public bool IsRange => HasSettingOptions && Widget.Style == SettingStyle.Range;

    /// <summary>A range's high end: another setting the station publishes.</summary>
    public string? HighName
    {
        get => Widget.HighName;
        set
        {
            var name = string.IsNullOrWhiteSpace(value) ? null : value;

            if (string.Equals(name, Widget.HighName, StringComparison.OrdinalIgnoreCase))
                return;

            _owner.Edit(() => Widget.HighName = name);
            Raise();
        }
    }

    /// <summary>The range's high setting, with the station's limit for it - or null.</summary>
    private Tunable? BoundHighSetting =>
        IsRange && _choicesFrom is { } recipe && !string.IsNullOrWhiteSpace(Widget.HighName)
            ? Tunable.Find(recipe, Widget.HighName!, _owner.Recipes.Station(Widget.Station)?.LimitFor(Widget.HighName!))
            : null;

    /// <summary>What the range's high end can be: the other settings the station publishes.</summary>
    private IEnumerable<BindingChoice> HighChoices =>
        _choices.Where(choice => !string.Equals(choice.Name, Widget.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Said under the high setting when it is missing or not published.</summary>
    public string? HighNameProblem
    {
        get
        {
            if (!IsRange || _choicesFrom is null)
                return null;

            if (string.IsNullOrWhiteSpace(Widget.HighName))
                return "Choose the setting for the range's high end.";

            if (!HighChoices.Any(choice => string.Equals(choice.Name, Widget.HighName, StringComparison.OrdinalIgnoreCase)))
                return $"'{Widget.HighName}' is not published by {Widget.Station}. Publish it in Studio, or choose another.";

            return null;
        }
    }

    public LabelPlacement LabelPlacement
    {
        get => Widget.LabelPlacement;
        set
        {
            if (value == Widget.LabelPlacement)
                return;

            // Above the control takes two lines: grown to fit, to the grid.
            _owner.Edit(() =>
            {
                Widget.LabelPlacement = value;

                var needed = TwoLineHeight(Widget.FontSize);

                if (value == LabelPlacement.Top && Widget.Height < needed)
                    Place(X, Y, Width, needed);
            });

            Raise();
        }
    }

    /// <summary>A label above a control: two lines of the font, the control's frame, the widget's edge - on the grid.</summary>
    internal static double TwoLineHeight(double fontSize) =>
        Math.Ceiling(((fontSize * 3) + 14) / ScreenLayout.Grid) * ScreenLayout.Grid;

    /// <summary>The lowest it may be set to on the line; blank for as low as the node allows.</summary>
    public string LowestText
    {
        get => LimitText(BoundSetting?.Limit?.Lowest);
        set => SetLimit(BoundSetting, value, lowest: true);
    }

    public string HighestText
    {
        get => LimitText(BoundSetting?.Limit?.Highest);
        set => SetLimit(BoundSetting, value, lowest: false);
    }

    /// <summary>A range's right end: the high setting's highest.</summary>
    public string HighHighestText
    {
        get => LimitText(BoundHighSetting?.Limit?.Highest);
        set => SetLimit(BoundHighSetting, value, lowest: false);
    }

    /// <summary>The range's high setting can have its highest set.</summary>
    public bool HasHighLimits => BoundHighSetting is { IsNumeric: true } && !Widget.ReadOnly;

    /// <summary>A slider, or a range, that cannot be one yet - and what to set so it can.</summary>
    public string? StyleNote
    {
        get
        {
            if (Widget.ReadOnly || BoundSetting is not { IsNumeric: true, IsChangeable: true } setting)
                return null;

            if (Widget.Style == SettingStyle.Slider && !setting.IsBounded)
                return "A slider needs both ends: set Lowest and Highest. Until then the line shows − and +.";

            if (Widget.Style == SettingStyle.Range && BoundHighSetting is { } high &&
                Tunable.Track(setting, high) is var (low, top) && (!double.IsFinite(low) || !double.IsFinite(top)))
            {
                return $"The slider runs from {setting.Name}'s lowest to {high.Name}'s highest: set both. Until then the line only shows them.";
            }

            return null;
        }
    }

    internal static string StyleLabel(SettingStyle style) => style switch
    {
        SettingStyle.Slider => "Slider",
        SettingStyle.Box => "Box",
        SettingStyle.Range => "Range",
        _ => "− value +",
    };

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

            return $"{setting.Source} itself allows {own}. Limits are kept in the project, for this station.";
        }
    }

    private static string LimitText(double? limit) =>
        limit is { } value ? value.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>
    /// Sets one end of the setting's range on the line - for this station,
    /// in the project, written at once. Every screen showing the setting,
    /// and later a PLC writing it, is held to the same.
    /// </summary>
    private void SetLimit(Tunable? bound, string? text, bool lowest)
    {
        if (bound is not { } setting || _owner.Recipes.Station(Widget.Station) is not { } station)
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

        var low = lowest ? limit : setting.Limit?.Lowest;
        var high = lowest ? setting.Limit?.Highest : limit;

        if (Sirius.Core.Projects.SettingLimit.Problem(low, high) is { } problem)
        {
            _owner.StatusText = problem;
            Raise();
            return;
        }

        try
        {
            if (!_owner.Recipes.Project.SetLimit(station, setting.Name, low, high))
                return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _owner.StatusText = $"Could not save the project: {ex.Message}";
            Raise();
            return;
        }

        var range = (ReferenceEquals(bound, BoundSetting) ? BoundSetting : BoundHighSetting)?.RangeText ?? string.Empty;
        _owner.StatusText = $"{station.Name} › {setting.Name} may be set to {range} on the line.";

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

        RefreshRows();
        _owner.Changed();
    }

    /// <summary>Reads the station's recipe again for what the widget can show.</summary>
    internal void RefreshChoices()
    {
        _choicesFrom = HasName ? _owner.Recipes.Recipe(Widget.Station) : null;
        _choices = _choicesFrom is null ? [] : ScreenBinding.Choices(_choicesFrom, Kind).Where(choice => choice.IsPublished).ToArray();

        OnPropertyChanged(nameof(StationOptions));
        OnPropertyChanged(nameof(StationValue));
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(Choice));
        OnPropertyChanged(nameof(NameProblem));
        OnPropertyChanged(nameof(HasNoChoices));
        RefreshRows();
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
        RefreshRows();
    }

    // ---- the property grid -------------------------------------------------

    private IReadOnlyList<PropertyCategory>? _categories;

    /// <summary>The widget's properties, by category, for the property grid - only those its kind has.</summary>
    public IReadOnlyList<PropertyCategory> Categories => _categories ??= PropertyCategory.From(Rows());

    private void RefreshRows()
    {
        if (_categories is null)
            return;

        foreach (var category in _categories)
            category.Refresh();
    }

    private IEnumerable<PropertyRow> Rows()
    {
        yield return new PropertyRow("Widget", "Kind", PropertyEditor.ReadOnly, () => KindLabel);

        if (HasStation)
        {
            yield return new PropertyRow(
                "Shows", "Station", PropertyEditor.Choice,
                () => StationValue,
                value => StationValue = value as string ?? string.Empty,
                Kind == WidgetKind.Button ? "The station the button acts on, or every station." : "The station it shows.",
                options: () => StationOptions.Select(option => new PropertyOption(option.Label, option.Value)).ToArray());
        }

        if (HasName)
        {
            yield return new PropertyRow(
                "Shows", Kind switch { WidgetKind.Image => "Image", WidgetKind.Setting => "Setting", _ => "Value" },
                PropertyEditor.Choice,
                () => Widget.Name,
                value => Choice = _choices.FirstOrDefault(choice => string.Equals(choice.Name, value as string, StringComparison.OrdinalIgnoreCase)),
                "What the station's recipe publishes. Publish more in Studio, then save the recipe there.",
                options: () => _choices.Select(choice => new PropertyOption($"{choice.Display}   ({choice.Source})", choice.Name)).ToArray());

            yield return new PropertyRow(
                "Shows", "Problem", PropertyEditor.ReadOnly,
                () => NameProblem,
                visible: () => NameProblem is not null);
        }

        if (HasAction)
        {
            yield return new PropertyRow(
                "Shows", "Does", PropertyEditor.Choice,
                () => Action,
                value => { if (value is WidgetAction action) Action = action; },
                "Trigger inspects once; Run / Stop runs part after part; Reset Counts starts the counts from zero.",
                options: () => _owner.Actions.Select(action => new PropertyOption(ActionLabel(action), action)).ToArray());
        }

        if (HasText)
        {
            yield return new PropertyRow(
                "Text", Kind == WidgetKind.Text ? "Text" : Kind == WidgetKind.Button ? "Caption" : "Label",
                PropertyEditor.Text,
                () => Text ?? string.Empty,
                value => Text = value as string,
                TextLabel);
        }

        if (HasNumberFormat)
        {
            yield return new PropertyRow(
                "Text", "Number format", PropertyEditor.Text,
                () => Format ?? string.Empty,
                value => Format = value as string,
                "How a number is written: 0.00 for two decimals, 0 for none. Blank for up to three.");

            yield return new PropertyRow(
                "Text", "Unit", PropertyEditor.Text,
                () => Unit ?? string.Empty,
                value => Unit = value as string,
                "Written after the number: mm, px.");
        }

        if (HasFontSize)
        {
            yield return new PropertyRow(
                "Text", "Font size", PropertyEditor.Text,
                () => Number(FontSize),
                value => { if (Parse(value) is { } size) FontSize = size; },
                "In the screen's own units; the whole screen is scaled to the display.");
        }

        if (HasBackground)
        {
            yield return new PropertyRow(
                "Appearance", "Background", PropertyEditor.Colour,
                () => BackgroundText,
                value => BackgroundText = value as string ?? string.Empty,
                "The colour behind the image, where it does not fill the viewer. Pick one, or type a name or #RRGGBB.",
                options: () => Sirius.Studio.App.Screens.ScreenColours.Presets.Select(preset => preset.Name).ToArray());
        }

        if (HasFile)
        {
            yield return new PropertyRow(
                "Logo", "Picture", PropertyEditor.File,
                () => File ?? string.Empty,
                value => File = value as string,
                "A picture in the project folder. One chosen from outside is copied into its screens folder.",
                browse: BrowseCommand);
        }

        if (HasSettingOptions)
        {
            yield return new PropertyRow(
                "Setting", "Control", PropertyEditor.Choice,
                () => Style,
                value => { if (value is SettingStyle style) Style = style; },
                "How the operator changes a number: − and + either side of a box; a slider (needs Lowest and Highest); a box to type in; or a range - this setting and a second one, low and high, on one slider with two handles.",
                options: () => Enum.GetValues<SettingStyle>().Select(style => new PropertyOption(StyleLabel(style), style)).ToArray(),
                visible: () => HasStyle);

            yield return new PropertyRow(
                "Setting", "High setting", PropertyEditor.Choice,
                () => HighName,
                value => HighName = value as string,
                "The range's high end: another setting the station publishes. This widget's Setting is the low end.",
                options: () => HighChoices.Select(choice => new PropertyOption($"{choice.Display}   ({choice.Source})", choice.Name)).ToArray(),
                visible: () => IsRange);

            yield return new PropertyRow(
                "Setting", "Problem", PropertyEditor.ReadOnly,
                () => HighNameProblem,
                visible: () => HighNameProblem is not null);

            yield return new PropertyRow(
                "Setting", "Label", PropertyEditor.Choice,
                () => LabelPlacement,
                value => { if (value is LabelPlacement placement) LabelPlacement = placement; },
                "Beside the control, on one line; or above it, so a long name and a wide control both fit. Above makes the widget taller if it needs to be.",
                options: () => new[]
                {
                    new PropertyOption("Left", LabelPlacement.Left),
                    new PropertyOption("Top", LabelPlacement.Top),
                });

            yield return new PropertyRow(
                "On the line", "Operator may change", PropertyEditor.Check,
                () => OperatorMayChange,
                value => { if (value is bool may) OperatorMayChange = may; },
                "Off: the screen only shows the setting.",
                enabled: () => IsChangeableKind);

            yield return new PropertyRow(
                "On the line", "Lowest", PropertyEditor.Text,
                () => LowestText,
                value => LowestText = value as string ?? string.Empty,
                "The lowest the operator may set it to - for a range, the low setting's, where the slider starts. Blank for as low as the node allows. Kept in the project, for this station.",
                visible: () => HasLimits);

            yield return new PropertyRow(
                "On the line", "Highest", PropertyEditor.Text,
                () => HighestText,
                value => HighestText = value as string ?? string.Empty,
                "The highest the operator may set it to. Blank for as high as the node allows.",
                visible: () => HasLimits && !IsRange);

            yield return new PropertyRow(
                "On the line", "Highest", PropertyEditor.Text,
                () => HighHighestText,
                value => HighHighestText = value as string ?? string.Empty,
                "The highest the high setting may be set to, where the slider ends. Blank for as high as its node allows.",
                visible: () => HasHighLimits);

            yield return new PropertyRow(
                "On the line", "Control", PropertyEditor.ReadOnly,
                () => StyleNote,
                visible: () => StyleNote is not null);

            yield return new PropertyRow(
                "On the line", "Note", PropertyEditor.ReadOnly,
                () => SettingNote,
                visible: () => SettingNote is not null);
        }

        yield return new PropertyRow("Layout", "X", PropertyEditor.Text, () => Number(X), value => { if (Parse(value) is { } x) EditX = x; }, "From the screen's left edge.");
        yield return new PropertyRow("Layout", "Y", PropertyEditor.Text, () => Number(Y), value => { if (Parse(value) is { } y) EditY = y; }, "From the screen's top edge.");
        yield return new PropertyRow("Layout", "Width", PropertyEditor.Text, () => Number(Width), value => { if (Parse(value) is { } width) EditWidth = width; });
        yield return new PropertyRow("Layout", "Height", PropertyEditor.Text, () => Number(Height), value => { if (Parse(value) is { } height) EditHeight = height; });
    }

    internal static string ActionLabel(WidgetAction action) => action switch
    {
        WidgetAction.RunStop => "Run / Stop",
        WidgetAction.ResetCounts => "Reset Counts",
        _ => "Trigger",
    };

    internal static string Number(double value) => value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);

    internal static double? Parse(object? value) =>
        value is string text &&
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var number) &&
        double.IsFinite(number)
            ? number
            : null;
}
