using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Sirius.Core.Graph;
using Sirius.Core.Projects;

namespace Sirius.Studio.App.ViewModels;

/// <summary>A station as the Project panel lists it.</summary>
public sealed class StationViewModel : ObservableObject
{
    private bool _isActive;

    public StationViewModel(Project project, Station station)
    {
        Station = station;
        RecipePath = project.RecipePath(station);
        station.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Station.Name))
                OnPropertyChanged(nameof(Name));
        };
    }

    public Station Station { get; }

    public string Name => Station.Name;

    /// <summary>The recipe file in full, for the tooltip.</summary>
    public string RecipePath { get; }

    public bool RecipeMissing => !File.Exists(RecipePath);

    /// <summary>Whether its recipe is the one in the editor.</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}

/// <summary>
/// The open project, for the Project panel: its stations, and opening,
/// adding, renaming, reordering and removing them.
/// </summary>
/// <remarks>
/// One station's recipe is in the editor at a time; opening another asks to
/// save the one there first, exactly as opening a recipe always has. The
/// project file itself is written on every change, so it has nothing to save.
/// </remarks>
public sealed class ProjectViewModel : ObservableObject
{
    private readonly MainViewModel _shell;
    private StationViewModel? _active;

    public ProjectViewModel(Project project, MainViewModel shell)
    {
        Project = project;
        _shell = shell;

        project.Changed += (_, _) => Rebuild();
        Rebuild();

        OpenStationCommand = new RelayCommand<StationViewModel>(station => Open(station));
        AddStationCommand = new RelayCommand(AddStation);
        ImportRecipeCommand = new RelayCommand(ImportRecipe);
        RenameStationCommand = new RelayCommand<StationViewModel>(Rename);
        RemoveStationCommand = new RelayCommand<StationViewModel>(Remove);
        MoveUpCommand = new RelayCommand<StationViewModel>(station => Move(station, -1));
        MoveDownCommand = new RelayCommand<StationViewModel>(station => Move(station, 1));
    }

    public Project Project { get; }

    public string Name => Project.Name;

    public string Folder => Project.Folder;

    public ObservableCollection<StationViewModel> Stations { get; } = [];

    /// <summary>The station whose recipe is in the editor, or null.</summary>
    public StationViewModel? ActiveStation
    {
        get => _active;
        private set
        {
            if (_active is not null)
                _active.IsActive = false;

            SetProperty(ref _active, value);

            if (value is not null)
                value.IsActive = true;
        }
    }

    public IRelayCommand<StationViewModel> OpenStationCommand { get; }

    public IRelayCommand AddStationCommand { get; }

    public IRelayCommand ImportRecipeCommand { get; }

    public IRelayCommand<StationViewModel> RenameStationCommand { get; }

    public IRelayCommand<StationViewModel> RemoveStationCommand { get; }

    public IRelayCommand<StationViewModel> MoveUpCommand { get; }

    public IRelayCommand<StationViewModel> MoveDownCommand { get; }

    /// <summary>Opens the first station whose recipe is there - what opening a project shows.</summary>
    internal void OpenFirst()
    {
        if (Stations.FirstOrDefault(station => !station.RecipeMissing) is { } first)
            Open(first, confirm: false);
    }

    /// <summary>The station whose recipe is at <paramref name="path"/>, made active - or none.</summary>
    internal void Follow(string? path) =>
        ActiveStation = path is null
            ? null
            : Stations.FirstOrDefault(station => string.Equals(station.RecipePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

    /// <summary>Puts a station's recipe in the editor, asking to save the one there first.</summary>
    internal bool Open(StationViewModel? station, bool confirm = true)
    {
        if (station is null || ReferenceEquals(station, ActiveStation))
            return false;

        if (station.RecipeMissing)
        {
            Dialog.Warn($"Station '{station.Name}' has no recipe: {station.RecipePath} is missing.", "Open Station");
            return false;
        }

        if (confirm && !_shell.ConfirmDiscard())
            return false;

        if (!_shell.OpenRecipeFile(station.RecipePath))
            return false;

        ActiveStation = station;
        return true;
    }

    private void AddStation()
    {
        var name = TextPromptWindow.Ask(
            null,
            "Add Station",
            "A new station with an empty recipe. What is it called?",
            Project.SuggestStationName(),
            text => Project.StationNameProblem(text));

        if (name is null)
            return;

        try
        {
            var station = Project.AddStation(name, new VisionGraph());
            Open(Find(station));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Dialog.Error(ex.Message, "Add Station");
        }
    }

    private void ImportRecipe()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Sirius Recipe (*.sgraph)|*.sgraph",
            Title = "Add a Station from a Recipe - it is copied into the project",
        };

        if (dialog.ShowDialog() != true)
            return;

        var suggested = Path.GetFileNameWithoutExtension(dialog.FileName);

        var name = TextPromptWindow.Ask(
            null,
            "Add Station",
            $"A new station running a copy of {Path.GetFileName(dialog.FileName)}. What is it called?",
            Project.StationNameProblem(suggested) is null ? suggested : Project.SuggestStationName(),
            text => Project.StationNameProblem(text));

        if (name is null)
            return;

        try
        {
            var station = Project.ImportStation(name, dialog.FileName);
            Open(Find(station));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Dialog.Error(ex.Message, "Add Station");
        }
    }

    private void Rename(StationViewModel? station)
    {
        if (station is null)
            return;

        var name = TextPromptWindow.Ask(
            null,
            "Rename Station",
            "Screens will show the station by this name.",
            station.Name,
            text => Project.StationNameProblem(text, station.Station));

        if (name is null || name == station.Name)
            return;

        Guard(() => Project.RenameStation(station.Station, name), "Rename Station");
        _shell.RefreshTitle();
    }

    private void Remove(StationViewModel? station)
    {
        if (station is null)
            return;

        var answer = Dialog.Confirm(
            $"Remove station '{station.Name}' from the project?\n\n" +
            $"Its recipe file stays in the project's recipes folder ({Path.GetFileName(station.RecipePath)}), " +
            "and can be added back with Add from Recipe.",
            "Remove Station");

        if (answer)
            RemoveConfirmed(station);
    }

    /// <summary>Removes a station once the operator has said so.</summary>
    internal void RemoveConfirmed(StationViewModel station)
    {
        // Its recipe goes from the editor with it - offered for saving
        // first - and the next station's takes its place.
        var wasOpen = ReferenceEquals(station, ActiveStation);

        if (wasOpen && !_shell.ConfirmDiscard())
            return;

        Guard(() => Project.RemoveStation(station.Station), "Remove Station");

        if (wasOpen)
        {
            ActiveStation = null;
            OpenFirst();

            if (ActiveStation is null)
                _shell.ClearRecipe();
        }

        _shell.RefreshTitle();
    }

    private void Move(StationViewModel? station, int offset)
    {
        if (station is not null)
            Guard(() => Project.MoveStation(station.Station, offset), "Move Station");
    }

    private static void Guard(Action action, string title)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Dialog.Error(ex.Message, title);
        }
    }

    private StationViewModel? Find(Station station) =>
        Stations.FirstOrDefault(candidate => ReferenceEquals(candidate.Station, station));

    private void Rebuild()
    {
        var active = ActiveStation?.Station;

        Stations.Clear();

        foreach (var station in Project.Stations)
            Stations.Add(new StationViewModel(Project, station));

        ActiveStation = active is null ? null : Find(active);
    }
}
