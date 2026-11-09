using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sirius.Core.Screens;

namespace Sirius.Builder;

/// <summary>A station a widget can be bound to, as the designer lists it - or every station, for a button.</summary>
public sealed record StationOption(string Label, string Value);

/// <summary>A kind of widget, as the Widgets panel offers it.</summary>
public sealed record WidgetTool(WidgetKind Kind, string Name, string Description, string Icon);

/// <summary>
/// The screen designer: a project's screen laid out on a canvas, its
/// widgets placed, sized and bound (runtime MVP step 5).
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
    private readonly string _path;

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

    public ScreenDesignerViewModel(StationRecipes recipes)
    {
        Recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));
        _path = ScreenLayout.MainScreenPath(recipes.Project);

        var screen = Load(out var problem);
        Apply(screen);

        _saved = File.Exists(_path) && problem is null ? Serialize() : string.Empty;
        StatusText = problem ?? (File.Exists(_path) ? $"Editing {ScreenLayout.ScreensFolder}/{Path.GetFileName(_path)}." : "A standard screen, made from what the stations publish.");

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
        ResetCommand = new RelayCommand(ResetToStandard);

        Check();
    }

    public StationRecipes Recipes { get; }

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
    ];

    // ---- the property grid ------------------------------------------------

    private IReadOnlyList<PropertyCategory>? _screenCategories;

    /// <summary>What the property grid shows: the selected widget's properties, or the screen's.</summary>
    public IReadOnlyList<PropertyCategory> PropertyCategories => Selected?.Categories ?? ScreenCategories;

    /// <summary>"Picture", or "Screen" with nothing selected.</summary>
    public string PropertyTitle => Selected?.KindLabel ?? "Screen";

    /// <summary>"Top › Main View", or the screen file.</summary>
    public string PropertySubtitle => Selected?.Caption ?? $"{ScreenLayout.ScreensFolder}/{Path.GetFileName(_path)} · {Widgets.Count} widgets";

    /// <summary>The screen's own properties, shown with no widget selected.</summary>
    public IReadOnlyList<PropertyCategory> ScreenCategories => _screenCategories ??= PropertyCategory.From(
    [
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
            () => _generated ? "From the stations, each time it is shown, until saved" : "By hand"),
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

    public string Title => (IsModified ? "*" : string.Empty) + $"Screen - {Recipes.Project.Name}";

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
            StatusText = $"Could not save the screen: {ex.Message}";
            return false;
        }
    }

    /// <summary>The standard screen made from the stations again, to start from - undoable.</summary>
    public void ResetToStandard()
    {
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
        Name = "Main",
        Width = _width,
        Height = _height,
        Generated = _generated,
        Widgets = Widgets.Select(widget => ScreenLayout.Copy(widget.Widget)).ToList(),
    };

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

    /// <summary>Re-reads the station recipes and checks the screen against them again.</summary>
    public void Refresh()
    {
        Recipes.Refresh();
        OnPropertyChanged(nameof(StationOptions));
        OnPropertyChanged(nameof(ButtonStationOptions));

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
                problem = $"The screen file could not be read ({ex.Message}); a standard one is shown. Saving replaces the file.";
            }
        }

        return ScreenLayout.Standard(Recipes.Project.Name, Recipes.All());
    }

    private void Apply(Screen screen)
    {
        _width = screen.Width;
        _height = screen.Height;
        _generated = screen.Generated;

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

    private void Check()
    {
        var problems = ScreenLayout.Problems(ToScreen(), Recipes.All());
        HasProblems = problems.Count > 0;

        ProblemText = problems.Count == 0
            ? "Every widget finds what it shows."
            : string.Join("   ·   ", problems);
    }
}
