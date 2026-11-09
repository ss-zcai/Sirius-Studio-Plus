using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sirius.Core.Screens;

namespace Sirius.Studio.App.Screens;

/// <summary>A station a widget can be bound to, as the designer lists it - or every station, for a button.</summary>
public sealed record StationOption(string Label, string Value);

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
        BringToFrontCommand = new RelayCommand(() => Restack(toFront: true), () => Selected is not null);
        SendToBackCommand = new RelayCommand(() => Restack(toFront: false), () => Selected is not null);
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

    private void Restack(bool toFront)
    {
        if (Selected is not { } widget)
            return;

        var from = Widgets.IndexOf(widget);
        var to = toFront ? Widgets.Count - 1 : 0;

        if (from != to)
            Edit(() => Widgets.Move(from, to));
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
        BringToFrontCommand.NotifyCanExecuteChanged();
        SendToBackCommand.NotifyCanExecuteChanged();
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

    /// <summary>Re-reads the station recipes and checks the screen against them again.</summary>
    public void Refresh()
    {
        Recipes.Refresh();

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
