using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sirius.Core.Projects;
using Sirius.Core.Screens;

namespace Sirius.Builder;

/// <summary>A station a widget can be bound to, as the designer lists it - or every station, for a button.</summary>
public sealed record StationOption(string Label, string Value);

/// <summary>A kind of widget, as the Widgets panel offers it.</summary>
public sealed record WidgetTool(WidgetKind Kind, string Name, string Description, string Icon);

/// <summary>
/// The screen designer: one of a project's screens laid out on a canvas,
/// its widgets placed, sized and bound (runtime MVP step 5) - the start
/// screen, or a page or popup beside it (decision 79).
/// </summary>
/// <remarks>
/// <para>
/// Works on a copy of the screen and writes it only on Save, as the
/// recipe editor does; a screen saved from here is the SI's, and is no
/// longer made again from the project (<see cref="Screen.Generated"/>).
/// Reset to Standard puts the generated layout back to start from.
/// </para>
/// <para>
/// Undo keeps the whole screen as it was before each change: a screen is a
/// few dozen widgets, and a snapshot cannot miss a kind of change the way
/// a list of edit types can.
/// </para>
/// </remarks>
public sealed partial class ScreenDesignerViewModel : ObservableObject
{
    /// <summary>The grid widgets snap to, in screen units.</summary>
    public const double GridSize = 10;

    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private readonly Func<IReadOnlyList<ScreenInfo>> _screens;
    private string _path;
    private string _name;
    private ScreenKind _kind;
    private UserLevel? _needs;

    private string _saved;
    private string? _gesture;
    private double _width;
    private double _height;
    private bool _generated;
    private int _added;

    [ObservableProperty]
    private DesignWidgetViewModel? _selected;

    [ObservableProperty]
    private string _problemText = string.Empty;

    [ObservableProperty]
    private bool _hasProblems;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <param name="name">The screen: the start screen, or another the project has.</param>
    /// <param name="screens">
    /// Every screen of the project - what an Open Screen button can open,
    /// and the start screen's size a popup must fit in. None: this one alone.
    /// </param>
    public ScreenDesignerViewModel(StationRecipes recipes, string name = Screen.StartName, Func<IReadOnlyList<ScreenInfo>>? screens = null)
    {
        Recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));
        _name = ScreenSet.IsStart(name) ? Screen.StartName : name.Trim();
        _path = ScreenSet.PathFor(recipes.Project, _name);
        _screens = screens ?? (() => [Info]);

        var screen = Load(out var problem);
        Apply(screen);

        _saved = File.Exists(_path) && problem is null ? Serialize() : string.Empty;
        StatusText = problem ?? (File.Exists(_path)
            ? $"Editing {ScreenLayout.ScreensFolder}/{Path.GetFileName(_path)}."
            : "A standard screen, made from what the stations publish.");

        AddCommand = new RelayCommand<WidgetKind>(kind => Add(kind));
        DeleteCommand = new RelayCommand(Delete, () => Selected is not null);
        DuplicateCommand = new RelayCommand(Duplicate, () => Selected is not null);
        CutCommand = new RelayCommand(Cut, () => Selected is not null);
        CopyCommand = new RelayCommand(Copy, () => Selected is not null);
        PasteCommand = new RelayCommand(Paste, () => _clipboard is not null);
        BringForwardCommand = new RelayCommand(() => Restack(+1), () => CanRestack(+1));
        SendBackwardCommand = new RelayCommand(() => Restack(-1), () => CanRestack(-1));
        BringToFrontCommand = new RelayCommand(() => Restack(int.MaxValue), () => CanRestack(+1));
        SendToBackCommand = new RelayCommand(() => Restack(int.MinValue), () => CanRestack(-1));
        UndoCommand = new RelayCommand(Undo, () => _undo.Count > 0);
        RedoCommand = new RelayCommand(Redo, () => _redo.Count > 0);
        SaveCommand = new RelayCommand(() => Save());
        ResetCommand = new RelayCommand(AskThenReset, () => IsStart);

        Check();
    }

    public StationRecipes Recipes { get; }

    /// <summary>The screen's name, and its file's: Main for the start screen.</summary>
    public string Name => _name;

    /// <summary>The start screen: shown first, made from the stations until edited, always a page.</summary>
    public bool IsStart => ScreenSet.IsStart(_name);

    /// <summary>A page, or a popup over one. The start screen is always a page.</summary>
    public ScreenKind Kind
    {
        get => IsStart ? ScreenKind.Page : _kind;
        set
        {
            if (IsStart || value == _kind)
                return;

            Edit(() => _kind = value);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The level needed to open it, when the project has users (F2); null
    /// for anyone. The start screen is always open.
    /// </summary>
    public UserLevel? Needs
    {
        get => IsStart ? null : _needs;
        set
        {
            if (IsStart || value == _needs)
                return;

            Edit(() => _needs = value);
            OnPropertyChanged();
        }
    }

    /// <summary>The screen's file.</summary>
    public string FilePath => _path;

    /// <summary>Its name, kind and size, as it is now - saved or not.</summary>
    public ScreenInfo Info => new(_name, Kind, _width, _height);

    /// <summary>The tab's title: its name, and a star until it is saved.</summary>
    public string TabTitle => _name + (IsModified ? " *" : string.Empty);

    public ObservableCollection<DesignWidgetViewModel> Widgets { get; } = [];

    /// <summary>Every widget kind, for the palette.</summary>
    public IReadOnlyList<WidgetKind> Kinds { get; } = Enum.GetValues<WidgetKind>();

    /// <summary>The Widgets panel: each kind, with what it is for.</summary>
    public static IReadOnlyList<WidgetTool> Tools { get; } =
    [
        new(WidgetKind.Image, "Image Viewer", "A station's image, with what its recipe draws on it - as Studio's Image Viewer shows it.", "ph_image"),
        new(WidgetKind.Verdict, "PASS / FAIL", "A station's verdict, and its grade or what went wrong.", "ph_limit_check"),
        new(WidgetKind.Value, "Value", "One published result: a measurement, a count, a code read.", "ph_number"),
        new(WidgetKind.Setting, "Setting", "A published setting, changed by the operator within its limits - or only shown.", "ph_sliders"),
        new(WidgetKind.Counter, "Counts", "A station's total, passed, failed and yield.", "ph_counter"),
        new(WidgetKind.Button, "Button", "Trigger, Run / Stop or Reset Counts - for one station or all.", "ph_pointer"),
        new(WidgetKind.Text, "Text", "Fixed text: a title, a label, an instruction.", "ph_text"),
        new(WidgetKind.Logo, "Logo", "A picture from the project folder: the customer's or yours.", "ph_trademark"),
        new(WidgetKind.Controller, "Controller", "A controller's PLC: connected or not, what last started a pass, what last went wrong.", "ph_controller"),
    ];

    // ---- the property grid ------------------------------------------------

    private IReadOnlyList<PropertyCategory>? _screenCategories;

    /// <summary>What the property grid shows: the selected widget's properties, or the screen's.</summary>
    public IReadOnlyList<PropertyCategory> PropertyCategories => Selected?.Categories ?? ScreenCategories;

    /// <summary>"Picture", or "Screen" with nothing selected.</summary>
    public string PropertyTitle => Selected?.KindLabel ?? "Screen";

    /// <summary>"Top › Main View", or the screen file.</summary>
    public string PropertySubtitle => Selected?.Caption ?? $"{ScreenLayout.ScreensFolder}/{Path.GetFileName(_path)} · {Widgets.Count} widgets";

    /// <summary>"Page - the start screen, shown first", "Popup".</summary>
    private string KindText => IsStart ? "Page - the start screen, shown first" : Kind == ScreenKind.Popup ? "Popup" : "Page";

    /// <summary>The screen's own properties, shown with no widget selected.</summary>
    public IReadOnlyList<PropertyCategory> ScreenCategories => _screenCategories ??= PropertyCategory.From(
    [
        new PropertyRow("Screen", "Name", PropertyEditor.ReadOnly, () => _name),
        IsStart
            ? new PropertyRow("Screen", "Opens as", PropertyEditor.ReadOnly, () => KindText)
            : new PropertyRow(
                "Screen", "Opens as", PropertyEditor.Choice,
                () => Kind,
                value => { if (value is ScreenKind kind) Kind = kind; },
                "A page fills the window in place of the one shown; Close goes back. A popup opens over the page, which is dimmed and goes on running, until it is closed.",
                options: () => new[]
                {
                    new PropertyOption("Page", ScreenKind.Page),
                    new PropertyOption("Popup", ScreenKind.Popup),
                }),
        new PropertyRow(
            "Screen", "Needs", PropertyEditor.Choice,
            () => Needs?.ToString() ?? string.Empty,
            value => Needs = Enum.TryParse<UserLevel>(value as string, out var level) && level != UserLevel.Operator ? level : null,
            "Who may open it on the line, when the project has users (Project ▸ Users). A button opening it asks " +
            "someone below this level to log in first; logged out, it closes.",
            options: () => new[]
            {
                new PropertyOption("Anyone", string.Empty),
                new PropertyOption("Technician", nameof(UserLevel.Technician)),
                new PropertyOption("Engineer", nameof(UserLevel.Engineer)),
            },
            visible: () => !IsStart),
        new PropertyRow(
            "Screen", "Width", PropertyEditor.Text,
            () => DesignWidgetViewModel.Number(Width),
            value => { if (DesignWidgetViewModel.Parse(value) is { } width) Width = width; },
            "In the screen's own units. It is scaled as a whole to whatever display shows it."),
        new PropertyRow(
            "Screen", "Height", PropertyEditor.Text,
            () => DesignWidgetViewModel.Number(Height),
            value => { if (DesignWidgetViewModel.Parse(value) is { } height) Height = height; }),
        new PropertyRow("Screen", "Grid", PropertyEditor.ReadOnly, () => $"{GridSize:0} units"),
        new PropertyRow(
            "Screen", "Made", PropertyEditor.ReadOnly,
            () => _generated ? "From the stations, each time it is shown, until saved" : "By hand",
            visible: () => IsStart),
    ]);

    private void RefreshProperties()
    {
        if (_screenCategories is not null)
        {
            foreach (var category in _screenCategories)
                category.Refresh();
        }

        OnPropertyChanged(nameof(PropertyCategories));
        OnPropertyChanged(nameof(PropertyTitle));
        OnPropertyChanged(nameof(PropertySubtitle));
    }

    public IReadOnlyList<WidgetAction> Actions { get; } = Enum.GetValues<WidgetAction>();

    /// <summary>Every station, for a widget showing one.</summary>
    public IReadOnlyList<StationOption> StationOptions =>
        Recipes.StationNames.Select(name => new StationOption(name, name)).ToArray();

    /// <summary>Every station, and "all", for a button.</summary>
    public IReadOnlyList<StationOption> ButtonStationOptions =>
        [new StationOption("All stations", string.Empty), .. StationOptions];

    /// <summary>What a counter's station list holds value starting with, for a controller's parts.</summary>
    public const string ControllerPrefix = "controller:";

    /// <summary>A counter's: each station's pictures, then each controller's parts (F25).</summary>
    /// <summary>The project's controllers, for a Controller widget.</summary>
    public IReadOnlyList<StationOption> ControllerOptions =>
        Recipes.ControllerNames.Select(name => new StationOption(name, ControllerPrefix + name)).ToArray();

    public IReadOnlyList<StationOption> CounterOptions =>
    [
        .. StationOptions,
        .. Recipes.ControllerNames.Select(name => new StationOption($"{name}: parts", ControllerPrefix + name)),
    ];

    /// <summary>The project's other screens, for an Open Screen button: "Settings (popup)".</summary>
    public IReadOnlyList<StationOption> ScreenOptions =>
        _screens()
            .Where(screen => !string.Equals(screen.Name, _name, StringComparison.OrdinalIgnoreCase))
            .Select(screen => new StationOption(
                screen.Kind == ScreenKind.Popup ? $"{screen.Name} (popup)" : ScreenSet.IsStart(screen.Name) ? $"{screen.Name} (start)" : screen.Name,
                screen.Name))
            .ToArray();

    public double Width
    {
        get => _width;
        set => Edit(() => SetProperty(ref _width, Math.Clamp(Math.Round(value), 320, 7680)));
    }

    public double Height
    {
        get => _height;
        set => Edit(() => SetProperty(ref _height, Math.Clamp(Math.Round(value), 240, 4320)));
    }

    public bool IsModified => Serialize() != _saved;

    public string Title => (IsModified ? "*" : string.Empty) + $"{_name} - {Recipes.Project.Name}";

    public IRelayCommand<WidgetKind> AddCommand { get; }

    public IRelayCommand DeleteCommand { get; }

    public IRelayCommand DuplicateCommand { get; }

    public IRelayCommand CutCommand { get; }

    public IRelayCommand CopyCommand { get; }

    public IRelayCommand PasteCommand { get; }

    /// <summary>One step nearer the front: in front of the widget just above it.</summary>
    public IRelayCommand BringForwardCommand { get; }

    /// <summary>One step further back.</summary>
    public IRelayCommand SendBackwardCommand { get; }

    public IRelayCommand BringToFrontCommand { get; }

    public IRelayCommand SendToBackCommand { get; }

    public IRelayCommand UndoCommand { get; }

    public IRelayCommand RedoCommand { get; }

    public IRelayCommand SaveCommand { get; }

    public IRelayCommand ResetCommand { get; }

    /// <summary>A position on the grid.</summary>
    public static double Snap(double value) => Math.Round(value / GridSize) * GridSize;

    // ---- editing -------------------------------------------------------

    /// <summary>Adds a widget - where it was dropped, or stepped down from the top left.</summary>
    public DesignWidgetViewModel Add(WidgetKind kind, double? x = null, double? y = null)
    {
        var step = _added++ % 8 * 20;
        var widget = ScreenLayout.NewWidget(
            kind,
            Snap(x ?? 40 + step),
            Snap(y ?? 80 + step),
            Recipes.StationNames.FirstOrDefault());

        if (kind == WidgetKind.Button)
            widget.Station = null;

        DesignWidgetViewModel added = null!;

        Edit(() =>
        {
            added = new DesignWidgetViewModel(widget, this);
            Keep(added);
            Widgets.Add(added);
        });

        Selected = added;
        return added;
    }

    private void Delete()
    {
        if (Selected is not { } doomed)
            return;

        Edit(() => Widgets.Remove(doomed));
        Selected = null;
    }

    private void Duplicate()
    {
        if (Selected is not { } original)
            return;

        var copy = ScreenLayout.Copy(original.Widget);
        copy.X = Snap(copy.X + 20);
        copy.Y = Snap(copy.Y + 20);

        DesignWidgetViewModel added = null!;

        Edit(() =>
        {
            added = new DesignWidgetViewModel(copy, this);
            Keep(added);
            Widgets.Add(added);
        });

        Selected = added;
    }

    // ---- the clipboard -------------------------------------------------------

    /// <summary>
    /// The widget cut or copied, as the screen file writes it - shared by
    /// every designer in Builder, so a widget can go from one project's
    /// screen to another's.
    /// </summary>
    private static string? _clipboard;

    /// <summary>How many times what is on the clipboard has been pasted; each paste lands a step further on.</summary>
    private static int _pastes;

    private void Copy()
    {
        if (Selected is not { } widget)
            return;

        _clipboard = JsonSerializer.Serialize(widget.Widget, Screen.Options);
        _pastes = 1;
        PasteCommand.NotifyCanExecuteChanged();
        StatusText = $"Copied the {widget.KindLabel}.";
    }

    /// <summary>Copies the selected widget and removes it; pasting puts it back where it was.</summary>
    private void Cut()
    {
        if (Selected is not { } widget)
            return;

        Copy();
        _pastes = 0;
        Delete();
        StatusText = $"Cut the {widget.KindLabel}.";
    }

    private void Paste()
    {
        if (_clipboard is null || JsonSerializer.Deserialize<Widget>(_clipboard, Screen.Options) is not { } pasted)
            return;

        var offset = 20 * _pastes++;
        pasted.X = Snap(pasted.X + offset);
        pasted.Y = Snap(pasted.Y + offset);

        DesignWidgetViewModel added = null!;

        Edit(() =>
        {
            added = new DesignWidgetViewModel(pasted, this);
            Keep(added);
            Widgets.Add(added);
        });

        Selected = added;
    }

    // ---- arranging ---------------------------------------------------------

    private bool CanRestack(int direction)
    {
        if (Selected is not { } widget)
            return false;

        var index = Widgets.IndexOf(widget);
        return direction > 0 ? index < Widgets.Count - 1 : index > 0;
    }

    /// <summary>Moves the selected widget forward (positive) or back in the stack; past either end stops there.</summary>
    private void Restack(int steps)
    {
        if (Selected is not { } widget)
            return;

        var from = Widgets.IndexOf(widget);
        var to = (int)Math.Clamp((long)from + steps, 0, Widgets.Count - 1);

        if (from != to)
        {
            Edit(() => Widgets.Move(from, to));
            NotifyArrange();
        }
    }

    private void NotifyArrange()
    {
        BringForwardCommand.NotifyCanExecuteChanged();
        SendBackwardCommand.NotifyCanExecuteChanged();
        BringToFrontCommand.NotifyCanExecuteChanged();
        SendToBackCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Moves the selected widget by a number of grid steps - the arrow keys.</summary>
    public void Nudge(double dx, double dy)
    {
        if (Selected is { } widget)
            Edit(() => widget.Place(widget.X + (dx * GridSize), widget.Y + (dy * GridSize), widget.Width, widget.Height));
    }

    /// <summary>A change made in one step - a property set, a widget added - undoable as one.</summary>
    internal void Edit(Action change)
    {
        var before = Serialize();
        change();

        if (Serialize() != before)
            Recorded(before);
    }

    /// <summary>Starts a drag: a move or a resize is one undo step however many places it passes through.</summary>
    public void BeginGesture() => _gesture = Serialize();

    public void EndGesture()
    {
        if (_gesture is { } before && Serialize() != before)
            Recorded(before);

        _gesture = null;
    }

    private void Recorded(string before)
    {
        _undo.Push(before);
        _redo.Clear();
        _generated = false;
        Changed();
    }

    /// <summary>Something changed: the title, the commands, the problems.</summary>
    internal void Changed()
    {
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(Info));

        if (_gesture is null)
            RefreshProperties();

        // Null while the constructor is still laying the screen out.
        UndoCommand?.NotifyCanExecuteChanged();
        RedoCommand?.NotifyCanExecuteChanged();

        if (_gesture is null)
            Check();
    }

    private void Undo()
    {
        if (_undo.Count == 0)
            return;

        _redo.Push(Serialize());
        Restore(_undo.Pop());
    }

    private void Redo()
    {
        if (_redo.Count == 0)
            return;

        _undo.Push(Serialize());
        Restore(_redo.Pop());
    }

    private void Restore(string snapshot)
    {
        var index = Selected is null ? -1 : Widgets.IndexOf(Selected);

        Apply(JsonSerializer.Deserialize<Screen>(snapshot, Screen.Options)!);
        Selected = index >= 0 && index < Widgets.Count ? Widgets[index] : null;

        Changed();
    }

    partial void OnSelectedChanged(DesignWidgetViewModel? oldValue, DesignWidgetViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;

        if (newValue is not null)
            newValue.IsSelected = true;

        DeleteCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        NotifyArrange();
        RefreshProperties();
    }

    // ---- the file ----------------------------------------------------------

    /// <summary>Writes the screen; from now on it is kept as it is rather than made again.</summary>
    public bool Save()
    {
        try
        {
            var screen = ToScreen();
            screen.Generated = false;
            screen.Save(_path);

            _generated = false;
            _saved = Serialize();
            StatusText = $"Saved {ScreenLayout.ScreensFolder}/{Path.GetFileName(_path)}.";
            Changed();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not save the screen {_name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Asks the operator a yes-or-no question; replaced in tests.</summary>
    internal Func<string, bool> Confirm { get; set; } =
        question => Sirius.UI.Dialog.Confirm(question, "Reset to Standard", System.Windows.Application.Current?.MainWindow);

    /// <summary>
    /// Screen - Reset to Standard: asks first, since it replaces every
    /// widget placed by hand. Undo still brings the design back.
    /// </summary>
    private void AskThenReset()
    {
        if (Confirm("Replace this screen with the standard one made from the stations?\n\n" +
                    "Every widget placed by hand is replaced. Undo (Ctrl+Z) brings the design back; " +
                    "nothing is written until the screen is saved."))
        {
            ResetToStandard();
        }
    }

    /// <summary>The standard screen made from the stations again, to start from - undoable.</summary>
    public void ResetToStandard()
    {
        if (!IsStart)
            return;

        var before = Serialize();
        Apply(ScreenLayout.Standard(Recipes.Project.Name, Recipes.All()));
        Selected = null;

        if (Serialize() != before)
            Recorded(before);

        StatusText = "Reset to the standard screen. Save to keep it.";
    }

    /// <summary>The screen as laid out now.</summary>
    public Screen ToScreen() => new()
    {
        Name = _name,
        Kind = Kind,
        Needs = Needs,
        Width = _width,
        Height = _height,
        Generated = _generated,
        Widgets = Widgets.Select(widget => ScreenLayout.Copy(widget.Widget)).ToList(),
    };

    /// <summary>
    /// After a controller was renamed: the counters of its parts follow, as
    /// one undoable change.
    /// </summary>
    /// <returns>Whether any counter counted its parts.</returns>
    public bool RenameController(string from, string to)
    {
        var following = Widgets
            .Where(widget => string.Equals(widget.Widget.Controller, from, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (following.Length == 0)
            return false;

        Edit(() =>
        {
            foreach (var widget in following)
                widget.Widget.Controller = to;
        });

        Refresh();
        return true;
    }

    /// <summary>
    /// After a station was renamed: the widgets showing it follow, as one
    /// undoable change.
    /// </summary>
    /// <returns>Whether any widget showed it.</returns>
    public bool RenameStation(string from, string to)
    {
        var following = Widgets
            .Where(widget => string.Equals(widget.Widget.Station, from, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (following.Length == 0)
            return false;

        Edit(() =>
        {
            foreach (var widget in following)
                widget.Widget.Station = to;
        });

        Refresh();
        return true;
    }

    /// <summary>
    /// After a screen was renamed: the buttons opening it follow, as one
    /// undoable change.
    /// </summary>
    /// <returns>Whether any button opened it.</returns>
    public bool RetargetScreen(string from, string to)
    {
        var opening = Widgets
            .Where(widget => string.Equals(widget.Widget.Screen, from, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (opening.Length == 0)
            return false;

        Edit(() =>
        {
            foreach (var widget in opening)
                widget.Widget.Screen = to;
        });

        Refresh();
        return true;
    }

    /// <summary>
    /// This screen was renamed and its file moved: from now on it is saved
    /// there, under its new name. Changes not yet saved stay so.
    /// </summary>
    public void Renamed(string name)
    {
        var saved = _saved.Length > 0 ? JsonSerializer.Deserialize<Screen>(_saved, Screen.Options) : null;

        _name = name.Trim();
        _path = ScreenSet.PathFor(Recipes.Project, _name);

        if (saved is not null)
        {
            saved.Name = _name;
            _saved = JsonSerializer.Serialize(saved, Screen.Options);
        }

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(FilePath));
        StatusText = $"Renamed to {_name}: {ScreenLayout.ScreensFolder}/{Path.GetFileName(_path)}.";
        Changed();
    }

    /// <summary>Re-reads the station recipes and checks the screen against them again.</summary>
    public void Refresh()
    {
        Recipes.Refresh();
        OnPropertyChanged(nameof(StationOptions));
        OnPropertyChanged(nameof(ButtonStationOptions));
        OnPropertyChanged(nameof(CounterOptions));
        OnPropertyChanged(nameof(ControllerOptions));
        OnPropertyChanged(nameof(ScreenOptions));

        foreach (var widget in Widgets)
            widget.RefreshChoices();

        Check();
    }

    private Screen Load(out string? problem)
    {
        problem = null;

        if (File.Exists(_path))
        {
            try
            {
                return Screen.Load(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                problem = IsStart
                    ? $"The screen file could not be read ({ex.Message}); a standard one is shown. Saving replaces the file."
                    : $"The screen file could not be read ({ex.Message}); a new one is shown. Saving replaces the file.";
            }
        }

        if (!IsStart)
            return ScreenSet.NewScreen(_name, ScreenKind.Page);

        return ScreenLayout.Standard(Recipes.Project.Name, Recipes.All());
    }

    private void Apply(Screen screen)
    {
        _width = screen.Width;
        _height = screen.Height;
        _generated = screen.Generated;
        _kind = screen.Kind;
        _needs = screen.Needs;

        Widgets.Clear();

        foreach (var widget in screen.Widgets)
        {
            var view = new DesignWidgetViewModel(widget, this);
            Keep(view);
            Widgets.Add(view);
        }

        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
    }

    /// <summary>Within the canvas, and on the grid.</summary>
    private void Keep(DesignWidgetViewModel widget) =>
        widget.Place(widget.X, widget.Y, widget.Width, widget.Height);

    private string Serialize() => JsonSerializer.Serialize(ToScreen(), Screen.Options);

    /// <summary>The project's screens, with this one as it is now rather than as saved.</summary>
    private ScreenInfo[] Others(Screen screen) =>
        [
            ScreenSet.Info(screen),
            .. _screens().Where(other => !string.Equals(other.Name, _name, StringComparison.OrdinalIgnoreCase)),
        ];

    private void Check()
    {
        var screen = ToScreen();
        var problems = ScreenLayout.Problems(screen, Recipes.All())
            .Concat(ScreenSet.NavigationProblems(screen, Others(screen)))
            .ToArray();
        HasProblems = problems.Length > 0;

        ProblemText = problems.Length == 0
            ? "Every widget finds what it shows."
            : string.Join("   ·   ", problems);
    }
}
