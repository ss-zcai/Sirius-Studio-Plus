using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Core.Screens;
using Sirius.Core.Serialization;
using Sirius.Plc;
using Sirius.Plc.Workflows;
using Sirius.Studio.App;
using Sirius.Studio.App.Hosting;
using Sirius.Studio.App.Screens;

namespace Sirius.Builder;

/// <summary>
/// Sirius Builder's window: the open project, its stations, controllers
/// and screens, each screen in a designer of its own (decisions 71, 79).
/// </summary>
/// <remarks>
/// <para>
/// A project is a folder of stations, each running a recipe made in Studio,
/// and the screens that show them. Builder adds, renames, reorders and
/// removes stations, lays out the screen, sets how far the line may change
/// each setting, and test-runs the whole as Sirius Edge will - but never
/// edits a recipe: Open in Studio does that.
/// </para>
/// <para>
/// The project file is written on every change, so only the screen has
/// anything to save.
/// </para>
/// </remarks>
public sealed partial class BuilderViewModel : ObservableObject
{
    public const string AppTitle = "Sirius Builder";

    private Project? _project;
    private StationRecipes? _recipes;
    private ScreenDesignerViewModel? _designer;
    private ScreenDesignerViewModel? _mainDesigner;

    [ObservableProperty]
    private StationViewModel? _selectedStation;

    [ObservableProperty]
    private ControllerViewModel? _selectedController;

    [ObservableProperty]
    private CameraUnitViewModel? _selectedCamera;

    [ObservableProperty]
    private string _statusText = "Open a project, or make a new one.";

    public BuilderViewModel()
    {
        NewProjectCommand = new RelayCommand(NewProject);
        OpenProjectCommand = new RelayCommand(OpenProject);
        CloseProjectCommand = new RelayCommand(() => CloseProject(), () => HasProject);
        AddStationCommand = new RelayCommand(AddStation, () => HasProject);
        AddFromRecipeCommand = new RelayCommand(AddFromRecipe, () => HasProject);
        RenameStationCommand = new RelayCommand<StationViewModel>(Rename);
        RemoveStationCommand = new RelayCommand<StationViewModel>(Remove);
        MoveUpCommand = new RelayCommand<StationViewModel>(station => Move(station, -1));
        MoveDownCommand = new RelayCommand<StationViewModel>(station => Move(station, 1));
        OpenInStudioCommand = new RelayCommand<StationViewModel>(station => OpenInStudio(station));
        LineSettingsCommand = new RelayCommand<StationViewModel>(ShowLineSettings);
        ImageSavingCommand = new RelayCommand<StationViewModel>(ShowImageSaving);
        BrowseImagesCommand = new RelayCommand<StationViewModel>(station => BrowseImages(station));
        TestRunCommand = new RelayCommand(() => TestRun(), () => HasProject);

        AddControllerCommand = new RelayCommand(AddController, () => HasProject);
        OpenWorkflowCommand = new RelayCommand<ControllerViewModel>(controller => OpenWorkflow(controller));
        PlcCommand = new RelayCommand<ControllerViewModel>(controller => ShowPlc(controller));
        RenameControllerCommand = new RelayCommand<ControllerViewModel>(RenameController);
        RemoveControllerCommand = new RelayCommand<ControllerViewModel>(RemoveController);
        OpenSelectedCommand = new RelayCommand(() => OpenSelected(SelectedItem), () => SelectedItem is not null);

        AddCameraCommand = new RelayCommand(AskThenAddCamera, () => HasProject);
        UsersCommand = new RelayCommand(ShowUsers, () => HasProject);
        OpenCameraCommand = new RelayCommand<CameraUnitViewModel>(camera => OpenCamera(camera));
        RenameCameraCommand = new RelayCommand<CameraUnitViewModel>(AskThenRenameCamera);
        RemoveCameraCommand = new RelayCommand<CameraUnitViewModel>(AskThenRemoveCamera);

        AddPageCommand = new RelayCommand(() => AskThenAddScreen(ScreenKind.Page), () => HasProject);
        AddPopupCommand = new RelayCommand(() => AskThenAddScreen(ScreenKind.Popup), () => HasProject);
        OpenScreenCommand = new RelayCommand<ScreenItemViewModel>(screen => OpenScreen(screen));
        RenameScreenCommand = new RelayCommand<ScreenItemViewModel>(AskThenRenameScreen, screen => screen?.CanChange == true);
        RemoveScreenCommand = new RelayCommand<ScreenItemViewModel>(AskThenRemoveScreen, screen => screen?.CanChange == true);

        // The toolbar's and the Edit menu's: whatever is in front does them.
        SaveCommand = Active(() => ActiveWorkflow?.SaveCommand ?? Designer?.SaveCommand);
        SaveAllCommand = new RelayCommand(() => SaveAll(), () => HasProject);
        UndoCommand = Active(() => ActiveWorkflow?.UndoCommand ?? Designer?.UndoCommand);
        RedoCommand = Active(() => ActiveWorkflow?.RedoCommand ?? Designer?.RedoCommand);
        CutCommand = Active(() => ActiveWorkflow?.CutCommand ?? Designer?.CutCommand);
        CopyCommand = Active(() => ActiveWorkflow?.CopyCommand ?? Designer?.CopyCommand);
        PasteCommand = Active(() => ActiveWorkflow?.PasteCommand ?? Designer?.PasteCommand);
        DuplicateCommand = Active(() => ActiveWorkflow?.DuplicateCommand ?? Designer?.DuplicateCommand);
        DeleteCommand = Active(() => ActiveWorkflow?.DeleteCommand ?? Designer?.DeleteCommand);

        // Arranging what overlaps is the screen's alone.
        BringForwardCommand = Active(() => ActiveWorkflow is null ? Designer?.BringForwardCommand : null);
        SendBackwardCommand = Active(() => ActiveWorkflow is null ? Designer?.SendBackwardCommand : null);
        BringToFrontCommand = Active(() => ActiveWorkflow is null ? Designer?.BringToFrontCommand : null);
        SendToBackCommand = Active(() => ActiveWorkflow is null ? Designer?.SendToBackCommand : null);
    }

    // ---- the document in front --------------------------------------------------

    private readonly List<ActiveCommand> _activeCommands = [];
    private WorkflowDocumentViewModel? _activeWorkflow;

    private ActiveCommand Active(Func<System.Windows.Input.ICommand?> target)
    {
        var command = new ActiveCommand(target);
        _activeCommands.Add(command);
        return command;
    }

    /// <summary>
    /// The workflow whose tab is in front, or null while the screen is - set
    /// by the window as tabs are chosen. The toolbar, the Edit menu, the
    /// tools panel and Properties all follow it.
    /// </summary>
    public WorkflowDocumentViewModel? ActiveWorkflow
    {
        get => _activeWorkflow;
        set
        {
            if (value is not null && !Workflows.Contains(value))
                value = null;

            if (ReferenceEquals(_activeWorkflow, value))
                return;

            if (_activeWorkflow is { } old)
                old.PropertyChanged -= OnActiveWorkflowChanged;

            _activeWorkflow = value;

            if (value is not null)
                value.PropertyChanged += OnActiveWorkflowChanged;

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWorkflowActive));
            OnPropertyChanged(nameof(ToolsTitle));
            ActiveChanged();
        }
    }

    public bool IsWorkflowActive => _activeWorkflow is not null;

    /// <summary>The tools panel's title: the screen's widgets, or a workflow's nodes.</summary>
    public string ToolsTitle => IsWorkflowActive ? "Nodes" : "Widgets";

    public ActiveCommand SaveCommand { get; }

    /// <summary>The screen and every open workflow.</summary>
    public IRelayCommand SaveAllCommand { get; }

    public ActiveCommand UndoCommand { get; }

    public ActiveCommand RedoCommand { get; }

    public ActiveCommand CutCommand { get; }

    public ActiveCommand CopyCommand { get; }

    public ActiveCommand PasteCommand { get; }

    public ActiveCommand DuplicateCommand { get; }

    public ActiveCommand DeleteCommand { get; }

    public ActiveCommand BringForwardCommand { get; }

    public ActiveCommand SendBackwardCommand { get; }

    public ActiveCommand BringToFrontCommand { get; }

    public ActiveCommand SendToBackCommand { get; }

    /// <summary>The Properties panel: the chosen widget or the screen, or the chosen node of the workflow in front.</summary>
    public IReadOnlyList<PropertyCategory> PropertyCategories =>
        ActiveWorkflow?.Properties ?? Designer?.PropertyCategories ?? [];

    public string? PropertyTitle => ActiveWorkflow?.PropertyTitle ?? Designer?.PropertyTitle;

    public string? PropertySubtitle => ActiveWorkflow?.PropertySubtitle ?? Designer?.PropertySubtitle;

    private void ActiveChanged()
    {
        foreach (var command in _activeCommands)
            command.Retarget();

        OnPropertyChanged(nameof(PropertyCategories));
        OnPropertyChanged(nameof(PropertyTitle));
        OnPropertyChanged(nameof(PropertySubtitle));
    }

    private void OnActiveWorkflowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WorkflowDocumentViewModel.Properties):
                OnPropertyChanged(nameof(PropertyCategories));
                break;

            case nameof(WorkflowDocumentViewModel.PropertyTitle):
                OnPropertyChanged(nameof(PropertyTitle));
                break;

            case nameof(WorkflowDocumentViewModel.PropertySubtitle):
                OnPropertyChanged(nameof(PropertySubtitle));
                break;

            case nameof(WorkflowDocumentViewModel.StatusText) when sender is WorkflowDocumentViewModel { StatusText.Length: > 0 } workflow:
                StatusText = workflow.StatusText;
                break;
        }
    }

    private void OnWorkflowModified(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkflowDocumentViewModel.IsModified))
            OnPropertyChanged(nameof(Title));
    }

    /// <summary>Saves every screen and every workflow open with changes.</summary>
    /// <returns>Whether all were saved.</returns>
    internal bool SaveAll()
    {
        var saved = true;

        foreach (var designer in Designers.Where(designer => designer.IsModified))
            saved &= designer.Save();

        foreach (var workflow in Workflows.Where(workflow => workflow.IsModified))
            saved &= workflow.Save();

        if (saved)
            StatusText = "Saved the screens and the workflows.";

        return saved;
    }

    /// <summary>Asks about everything unsaved - each screen, each workflow - before Builder closes.</summary>
    /// <returns>False when the operator chose to stay.</returns>
    internal bool ConfirmLeaving(string title)
    {
        if (!ConfirmScreens(title))
            return false;

        foreach (var workflow in Workflows.Where(workflow => workflow.IsModified).ToArray())
        {
            var answer = Ask($"Save {workflow.Controller.Name}'s workflow first?", title);

            if (answer is MessageBoxResult.Cancel or MessageBoxResult.None)
                return false;

            if (answer == MessageBoxResult.Yes && !workflow.Save())
                return false;
        }

        return true;
    }

    /// <summary>The open project, or null.</summary>
    public Project? Project => _project;

    public bool HasProject => _project is not null;

    public ObservableCollection<StationViewModel> Stations { get; } = [];

    public bool HasNoStations => _project is not null && Stations.Count == 0;

    /// <summary>The project's cameras, which stations take their pictures from.</summary>
    public ObservableCollection<CameraUnitViewModel> Cameras { get; } = [];

    /// <summary>The project's PLCs, each with its workflow.</summary>
    public ObservableCollection<ControllerViewModel> Controllers { get; } = [];

    public bool HasNoControllers => _project is not null && Controllers.Count == 0;

    /// <summary>
    /// What the Project panel lists: the stations, then the controllers -
    /// one list, as a line is one machine, each told apart by its icon.
    /// </summary>
    public ObservableCollection<object> Items { get; } = [];

    /// <summary>A project with no stations, cameras or controllers - its start screen is always listed.</summary>
    public bool IsEmpty => _project is not null && Stations.Count == 0 && Cameras.Count == 0 && Controllers.Count == 0;

    /// <summary>The project's screens: the start screen, then its pages and popups.</summary>
    public ObservableCollection<ScreenItemViewModel> Screens { get; } = [];

    private object? _selectedItem;

    /// <summary>
    /// What is selected in the Project panel - a station, a controller or a
    /// screen. The Station and Controller menus act on it.
    /// </summary>
    public object? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value))
                return;

            SelectedStation = value as StationViewModel;
            SelectedController = value as ControllerViewModel;
            SelectedCamera = value as CameraUnitViewModel;
            OpenSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnSelectedStationChanged(StationViewModel? value)
    {
        if (value is not null)
            SelectedItem = value;
    }

    partial void OnSelectedControllerChanged(ControllerViewModel? value)
    {
        if (value is not null)
            SelectedItem = value;
    }

    partial void OnSelectedCameraChanged(CameraUnitViewModel? value)
    {
        if (value is not null)
            SelectedItem = value;
    }

    /// <summary>The Project panel's open button: a station's recipe in Studio, a controller's workflow here.</summary>
    public IRelayCommand OpenSelectedCommand { get; }

    /// <summary>A screen's designer is to be brought to the front - opened, or already open.</summary>
    public event EventHandler<ScreenDesignerViewModel>? ScreenShown;

    /// <summary>
    /// What a double-click does - open it here, in Builder: a station shows
    /// the start screen, a controller its workflow, a screen its designer.
    /// A station's recipe opens in Studio from its right-click menu, or the
    /// panel's open button.
    /// </summary>
    internal void Open(object? item)
    {
        switch (item)
        {
            case StationViewModel station:
                ShowScreen(station);
                break;

            case ControllerViewModel controller:
                OpenWorkflowCommand.Execute(controller);
                break;

            case ScreenItemViewModel screen:
                OpenScreen(screen);
                break;

            case CameraUnitViewModel camera:
                OpenCamera(camera);
                break;
        }
    }

    private void OpenSelected(object? item)
    {
        switch (item)
        {
            case StationViewModel station:
                OpenInStudioCommand.Execute(station);
                break;

            case ControllerViewModel controller:
                OpenWorkflowCommand.Execute(controller);
                break;

            case ScreenItemViewModel screen:
                OpenScreen(screen);
                break;

            case CameraUnitViewModel camera:
                OpenCamera(camera);
                break;
        }
    }

    /// <summary>Brings the start screen to the front, saying how much of it shows the station.</summary>
    internal void ShowScreen(StationViewModel? station = null)
    {
        if (_mainDesigner is not { } main)
            return;

        ShowDesigner(main);

        if (station is not null)
        {
            var designer = main;
            var showing = designer.Widgets.Count(widget => string.Equals(widget.Widget.Station, station.Name, StringComparison.OrdinalIgnoreCase));

            StatusText = showing == 0
                ? $"The screen shows nothing of {station.Name} yet: add widgets for it. Right-click it to open its recipe in Studio."
                : $"{showing} widget{(showing == 1 ? string.Empty : "s")} on the screen show{(showing == 1 ? "s" : string.Empty)} {station.Name}. Right-click it to open its recipe in Studio.";
        }
    }

    /// <summary>The workflows open in tabs.</summary>
    public ObservableCollection<WorkflowDocumentViewModel> Workflows { get; } = [];

    /// <summary>A workflow is to be shown - opened, or brought to the front.</summary>
    public event EventHandler<WorkflowDocumentViewModel>? WorkflowShown;

    /// <summary>
    /// The screen being designed: the one whose tab is in front, or was last
    /// - the start screen's to begin with. Null with no project open.
    /// </summary>
    public ScreenDesignerViewModel? Designer
    {
        get => _designer;
        private set
        {
            if (SetProperty(ref _designer, value))
                ActiveChanged();
        }
    }

    /// <summary>The start screen's designer, in the tab that is always there.</summary>
    public ScreenDesignerViewModel? MainDesigner
    {
        get => _mainDesigner;
        private set => SetProperty(ref _mainDesigner, value);
    }

    /// <summary>The screens open in designers, the start screen's first.</summary>
    public ObservableCollection<ScreenDesignerViewModel> Designers { get; } = [];

    /// <summary>
    /// A screen's tab came to the front: its designer is the one the
    /// toolbar, the Widgets panel and Properties act on.
    /// </summary>
    internal void ShowingDesigner(ScreenDesignerViewModel designer)
    {
        if (!Designers.Contains(designer))
            return;

        ActiveWorkflow = null;
        Designer = designer;
    }

    /// <summary>Brings a screen's designer to the front.</summary>
    private void ShowDesigner(ScreenDesignerViewModel designer)
    {
        ShowingDesigner(designer);
        ScreenShown?.Invoke(this, designer);
    }

    public string Title => _project is null
        ? AppTitle
        : $"{(Designers.Any(designer => designer.IsModified) || Workflows.Any(workflow => workflow.IsModified) ? "*" : string.Empty)}{_project.Name} - {AppTitle}";

    /// <summary>Asks the operator a question; replaced in tests.</summary>
    internal Func<string, string, MessageBoxResult> Ask { get; set; } = (message, title) => Dialog.Ask(message, title, Application.Current?.MainWindow);

    /// <summary>Starts a program; replaced in tests.</summary>
    internal Func<string, string, bool> Launch { get; set; } = LaunchProcess;

    public IRelayCommand NewProjectCommand { get; }

    public IRelayCommand OpenProjectCommand { get; }

    public IRelayCommand CloseProjectCommand { get; }

    public IRelayCommand AddStationCommand { get; }

    public IRelayCommand AddFromRecipeCommand { get; }

    public IRelayCommand<StationViewModel> RenameStationCommand { get; }

    public IRelayCommand<StationViewModel> RemoveStationCommand { get; }

    public IRelayCommand<StationViewModel> MoveUpCommand { get; }

    public IRelayCommand<StationViewModel> MoveDownCommand { get; }

    public IRelayCommand<StationViewModel> OpenInStudioCommand { get; }

    public IRelayCommand<StationViewModel> LineSettingsCommand { get; }

    public IRelayCommand<StationViewModel> ImageSavingCommand { get; }

    public IRelayCommand<StationViewModel> BrowseImagesCommand { get; }

    public IRelayCommand TestRunCommand { get; }

    public IRelayCommand AddControllerCommand { get; }

    public IRelayCommand<ControllerViewModel> OpenWorkflowCommand { get; }

    public IRelayCommand<ControllerViewModel> PlcCommand { get; }

    public IRelayCommand<ControllerViewModel> RenameControllerCommand { get; }

    public IRelayCommand<ControllerViewModel> RemoveControllerCommand { get; }

    /// <summary>A new page: fills the window in place of the one shown.</summary>
    public IRelayCommand AddPageCommand { get; }

    /// <summary>A new popup: opens over the page.</summary>
    public IRelayCommand AddPopupCommand { get; }

    public IRelayCommand<ScreenItemViewModel> OpenScreenCommand { get; }

    public IRelayCommand<ScreenItemViewModel> RenameScreenCommand { get; }

    public IRelayCommand<ScreenItemViewModel> RemoveScreenCommand { get; }

    public IRelayCommand AddCameraCommand { get; }

    /// <summary>Who may log in on the line (F2).</summary>
    public IRelayCommand UsersCommand { get; }

    /// <summary>Shows the Users window; a test swaps it for its own.</summary>
    internal Action<Window?, UsersViewModel> OpenUsersWindow { get; set; } = (owner, model) => UsersWindow.Show(owner, model);

    private void ShowUsers()
    {
        if (_project is { } project)
            OpenUsersWindow(Application.Current?.MainWindow, new UsersViewModel(project));
    }

    public IRelayCommand<CameraUnitViewModel> OpenCameraCommand { get; }

    public IRelayCommand<CameraUnitViewModel> RenameCameraCommand { get; }

    public IRelayCommand<CameraUnitViewModel> RemoveCameraCommand { get; }

    /// <summary>Shows a camera's window and returns once it is closed; replaced in tests.</summary>
    internal Action<Window?, CameraViewModel> OpenCameraWindow { get; set; } = (owner, model) => CameraWindow.Show(owner, model);

    /// <summary>Opens a PLC window; replaced in tests.</summary>
    internal Func<Window?, PlcViewModel, Window?> OpenPlcWindow { get; set; } = (owner, model) => PlcWindow.Open(owner, model);

    // ---- the project ------------------------------------------------------

    private void NewProject()
    {
        if (!CloseProject())
            return;

        var dialog = new SaveFileDialog
        {
            Filter = "Sirius Project (*.sproj)|*.sproj",
            DefaultExt = Sirius.Core.Projects.Project.Extension,
            Title = "New Project - a folder of this name is made here",
            FileName = "New Project",
            OverwritePrompt = false,
        };

        if (dialog.ShowDialog() != true)
            return;

        var parent = Path.GetDirectoryName(dialog.FileName)!;
        var name = Path.GetFileNameWithoutExtension(dialog.FileName);

        if (Sirius.Core.Projects.Project.FileNameProblem(name) is { } problem)
        {
            Dialog.Warn(problem, "New Project");
            return;
        }

        try
        {
            var project = Sirius.Core.Projects.Project.Create(parent, name, firstRecipe: null, firstStation: null);
            Show(project);
            Remember(project.FilePath);
            StatusText = $"Made project {name}. Add a station from a recipe made in Studio.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Dialog.Error(ex.Message, "Could not make the project");
        }
    }

    private void OpenProject()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Sirius Project (*.sproj)|*.sproj",
            Title = "Open Project",
        };

        if (dialog.ShowDialog() == true)
            OpenProjectFile(dialog.FileName);
    }

    /// <summary>Opens the project at <paramref name="path"/>, closing the one open first.</summary>
    /// <returns>Whether it opened.</returns>
    public bool OpenProjectFile(string path)
    {
        if (!CloseProject())
            return false;

        try
        {
            var loaded = Sirius.Core.Projects.Project.Load(path);
            Show(loaded.Project);
            Remember(loaded.Project.FilePath);

            if (loaded.Warnings.Count > 0)
                Dialog.Warn(string.Join(Environment.NewLine, loaded.Warnings), "Project opened with warnings");

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            Dialog.Error($"{path} could not be opened: {ex.Message}", "Open Project");
            return false;
        }
    }

    /// <summary>Puts <paramref name="project"/> in the window.</summary>
    internal void Show(Project project)
    {
        _project = project;
        _recipes = new StationRecipes(project);
        project.Changed += OnProjectChanged;

        Rebuild();

        var main = Designed(new ScreenDesignerViewModel(_recipes, Screen.StartName, ScreenInfos));
        MainDesigner = main;
        Designer = main;
        ActiveChanged();
        SelectedStation = Stations.FirstOrDefault();

        StatusText = $"{project.Name}: {Stations.Count} station{(Stations.Count == 1 ? string.Empty : "s")}, in {project.Folder}.";
        Changed();
    }

    /// <summary>Closes the project, offering to save the screen first.</summary>
    /// <returns>False when the operator chose to keep it open.</returns>
    public bool CloseProject()
    {
        if (_project is null)
            return true;

        if (!ConfirmScreens("Close Project"))
            return false;

        foreach (var workflow in Workflows.ToArray())
        {
            if (!CloseWorkflow(workflow))
                return false;
        }

        _project.Changed -= OnProjectChanged;
        _project = null;
        _recipes = null;
        Designers.Clear();
        MainDesigner = null;
        Designer = null;
        ActiveChanged();
        Stations.Clear();
        Cameras.Clear();
        Controllers.Clear();
        Screens.Clear();
        Items.Clear();
        SelectedItem = null;
        StatusText = "Open a project, or make a new one.";
        Changed();
        return true;
    }

    /// <summary>Asks to save each screen that has changes.</summary>
    /// <returns>False when the operator cancelled, or saving failed.</returns>
    internal bool ConfirmScreens(string title)
    {
        foreach (var designer in Designers.Where(designer => designer.IsModified).ToArray())
        {
            if (!ConfirmScreen(designer, title))
                return false;
        }

        return true;
    }

    /// <summary>Asks to save one screen, if it has changes.</summary>
    private bool ConfirmScreen(ScreenDesignerViewModel designer, string title)
    {
        if (!designer.IsModified)
            return true;

        var answer = Ask(designer.IsStart && Designers.Count == 1 ? "Save the screen first?" : $"Save the screen {designer.Name} first?", title);

        return answer switch
        {
            MessageBoxResult.Yes => designer.Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    /// <summary>
    /// Back in front: recipes may have been saved in Studio, and settings
    /// changed on the line, meanwhile.
    /// </summary>
    public void Refresh()
    {
        if (_project is null)
            return;

        foreach (var station in Stations)
            station.RefreshLine();

        foreach (var designer in Designers)
            designer.Refresh();
    }

    // ---- stations ---------------------------------------------------------

    private void AddStation()
    {
        if (_project is not { } project)
            return;

        var name = TextPromptWindow.Ask(
            Application.Current?.MainWindow,
            "Add Station",
            "A new station with an empty recipe, to build in Studio. What is it called?",
            project.SuggestStationName(),
            text => project.StationNameProblem(text));

        if (name is null)
            return;

        Guard(() =>
        {
            var station = project.AddStation(name, new VisionGraph());
            SelectedStation = Find(station);
            StatusText = $"Added {station.Name}. Open it in Studio to build its recipe.";
        }, "Add Station");
    }

    private void AddFromRecipe()
    {
        if (_project is not { } project)
            return;

        var dialog = new OpenFileDialog
        {
            Filter = "Sirius Recipe (*.sgraph)|*.sgraph",
            Title = "Add a Station from a Recipe - it is copied into the project",
        };

        if (dialog.ShowDialog() != true)
            return;

        var suggested = Path.GetFileNameWithoutExtension(dialog.FileName);

        var name = TextPromptWindow.Ask(
            Application.Current?.MainWindow,
            "Add Station",
            $"A new station running a copy of {Path.GetFileName(dialog.FileName)}. What is it called?",
            project.StationNameProblem(suggested) is null ? suggested : project.SuggestStationName(),
            text => project.StationNameProblem(text));

        if (name is null)
            return;

        AddFromRecipe(name, dialog.FileName);
    }

    /// <summary>Adds a station running a copy of the recipe at <paramref name="recipeFile"/>.</summary>
    internal Station? AddFromRecipe(string name, string recipeFile)
    {
        if (_project is not { } project)
            return null;

        Station? added = null;

        Guard(() =>
        {
            added = project.ImportStation(name, recipeFile);
            SelectedStation = Find(added);
            StatusText = $"Added {added.Name}, running a copy of {Path.GetFileName(recipeFile)}.";
        }, "Add Station");

        if (added is not null)
            OfferCamera(added);

        return added;
    }

    private void Rename(StationViewModel? station)
    {
        if (station is null || _project is not { } project)
            return;

        var name = TextPromptWindow.Ask(
            Application.Current?.MainWindow,
            "Rename Station",
            "Screens show the station by this name.",
            station.Name,
            text => project.StationNameProblem(text, station.Station));

        if (name is not null && name != station.Name)
            RenameStation(station, name);
    }

    /// <summary>Renames a station, and the screen's widgets that show it.</summary>
    internal void RenameStation(StationViewModel station, string name)
    {
        if (_project is not { } project)
            return;

        var old = station.Name;

        Guard(() =>
        {
            project.RenameStation(station.Station, name);

            // A screen or workflow open in a tab follows as a change to save; one not open, in its file at once.
            var open = Designers.Where(designer => designer.RenameStation(old, station.Name)).Select(designer => designer.Name)
                .Concat(Workflows.Where(workflow => workflow.RenameStation(old, station.Name)).Select(workflow => $"{workflow.Controller.Name}'s workflow"))
                .ToArray();

            var written = ScreenSet.RenameStation(project, old, station.Name, Designers.Select(designer => designer.Name).ToArray())
                .Concat(RenameInWorkflowFiles(project, old, station.Name))
                .ToArray();

            var all = open.Concat(written).ToArray();

            StatusText = all.Length == 0
                ? $"Renamed {old} to {station.Name}."
                : open.Length == 0
                    ? $"Renamed {old} to {station.Name}, on {string.Join(", ", all)} too."
                    : $"Renamed {old} to {station.Name}, on {string.Join(", ", all)} too. Save {string.Join(", ", open)} to keep it.";
        }, "Rename Station");
    }

    /// <summary>
    /// The workflows not open in a tab whose nodes name the station renamed,
    /// changed in their files. One that cannot be read is left; its check
    /// says the station is not there.
    /// </summary>
    private IEnumerable<string> RenameInWorkflowFiles(Project project, string from, string to)
    {
        var changed = new List<string>();

        foreach (var controller in project.Controllers)
        {
            var path = project.WorkflowPath(controller);

            if (Workflows.Any(workflow => ReferenceEquals(workflow.Controller, controller)) || !File.Exists(path))
                continue;

            try
            {
                if (WorkflowFile.RenameStation(path, from, to))
                    changed.Add($"{controller.Name}'s workflow");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                // Left as it is.
            }
        }

        return changed;
    }

    private void Remove(StationViewModel? station)
    {
        if (station is null)
            return;

        var answer = Dialog.Confirm(
            $"Remove station '{station.Name}' from the project?\n\n" +
            $"Its recipe file stays in the project's recipes folder ({station.RecipeFile}), " +
            "and can be added back with Add from Recipe. Widgets showing it are reported missing.",
            "Remove Station");

        if (answer)
            RemoveStation(station);
    }

    internal void RemoveStation(StationViewModel station)
    {
        if (_project is { } project)
            Guard(() => project.RemoveStation(station.Station), "Remove Station");
    }

    private void Move(StationViewModel? station, int offset)
    {
        if (station is not null && _project is { } project)
            Guard(() => project.MoveStation(station.Station, offset), "Move Station");
    }

    // ---- recipes, Studio and the line ---------------------------------------

    /// <summary>
    /// Opens a station's recipe in Sirius Studio, beside Builder - where its
    /// names are published. Back here, Builder reads it again.
    /// </summary>
    /// <returns>Whether Studio was started.</returns>
    internal bool OpenInStudio(StationViewModel? station)
    {
        if (station is null)
            return false;

        if (station.RecipeMissing)
        {
            Dialog.Warn($"Station '{station.Name}' has no recipe: {station.RecipePath} is missing.", "Open in Studio");
            return false;
        }

        var studio = Path.Combine(AppContext.BaseDirectory, "SiriusStudioPlus.exe");

        if (!File.Exists(studio))
        {
            Dialog.Warn($"Sirius Studio is not beside Sirius Builder ({studio}). Open {station.RecipePath} in it yourself.", "Open in Studio");
            return false;
        }

        if (!Launch(studio, station.RecipePath))
        {
            Dialog.Error("Sirius Studio could not be started.", "Open in Studio");
            return false;
        }

        StatusText = $"Opened {station.RecipeFile} in Studio. Save it there; Builder picks the changes up when you come back.";
        return true;
    }

    private void ShowLineSettings(StationViewModel? station)
    {
        if (station is null || _project is not { } project)
            return;

        if (station.RecipeMissing)
        {
            Dialog.Warn($"Station '{station.Name}' has no recipe: {station.RecipePath} is missing.", "Line Settings");
            return;
        }

        LineSettingsWindow.Show(Application.Current?.MainWindow, new LineSettingsViewModel(project, station.Station));
        station.RefreshLine();

        foreach (var designer in Designers)
            designer.Refresh();
    }

    private void ShowImageSaving(StationViewModel? station)
    {
        if (station is null || _project is not { } project)
            return;

        if (ImageSavingWindow.Show(Application.Current?.MainWindow, new ImageSavingViewModel(project, station.Station)))
            StatusText = $"{station.Name}: {station.SavingText.ToLowerInvariant()}.";
    }

    /// <summary>
    /// Opens a station's saved pictures in Sirius IV - the folder it saves
    /// into, or the project's images folder when it has saved none yet.
    /// </summary>
    /// <returns>Whether Sirius IV was started.</returns>
    internal bool BrowseImages(StationViewModel? station)
    {
        if (station is null || _project is not { } project)
            return false;

        var folder = Directory.Exists(station.ImagesFolder) ? station.ImagesFolder : station.Station.Saving.RootFor(project);

        if (!Directory.Exists(folder))
        {
            Dialog.Inform(
                $"{station.Name} has saved no pictures yet. Choose which it keeps in Image Saving, then run it.",
                "Browse Saved Images");
            return false;
        }

        var viewer = Path.Combine(AppContext.BaseDirectory, "SiriusIV.exe");

        if (!File.Exists(viewer))
        {
            Dialog.Warn($"Sirius IV is not beside Sirius Builder ({viewer}). Open {folder} in it yourself.", "Browse Saved Images");
            return false;
        }

        if (!Launch(viewer, folder))
        {
            Dialog.Error("Sirius IV could not be started.", "Browse Saved Images");
            return false;
        }

        StatusText = $"Opened {station.Name}'s pictures in Sirius IV.";
        return true;
    }

    /// <summary>
    /// Shows the project running on its saved screen, in a window, exactly as
    /// Sirius Edge will run it on the machine.
    /// </summary>
    internal void TestRun(Window? owner = null)
    {
        if (_project is not { } project)
            return;

        if (Designers.Where(designer => designer.IsModified).ToArray() is { Length: > 0 } unsaved)
        {
            var answer = Ask(
                unsaved.Length == 1 && unsaved[0].IsStart && Designers.Count == 1
                    ? "The test run shows the screen as saved. Save it first?"
                    : $"The test run shows the screens as saved. Save {string.Join(", ", unsaved.Select(designer => designer.Name))} first?",
                "Test Run");

            if (answer is MessageBoxResult.Cancel or MessageBoxResult.None)
                return;

            if (answer == MessageBoxResult.Yes && !unsaved.All(designer => designer.Save()))
                return;
        }

        if (Workflows.Where(workflow => workflow.IsModified).ToArray() is { Length: > 0 } changed)
        {
            var answer = Ask(
                $"The test run runs the workflows as saved. Save {string.Join(", ", changed.Select(workflow => workflow.Controller.Name))} first?",
                "Test Run");

            if (answer is MessageBoxResult.Cancel or MessageBoxResult.None)
                return;

            if (answer == MessageBoxResult.Yes && !changed.All(workflow => workflow.Save()))
                return;
        }

        StatusText = $"Running {project.Name}…";
        RuntimeWindow.ShowWindowed(owner ?? Application.Current?.MainWindow, project, running => OpenSimulators(running));

        // Let the cameras go, so Sirius Edge or Studio on this PC can open them.
        Sirius.Vision.Cameras.CameraDrivers.CloseAll();

        foreach (var station in Stations)
            station.RefreshLine();

        var lineChanged = Stations.Where(station => station.HasLineChanges).Select(station => station.Name).ToArray();

        StatusText = lineChanged.Length == 0
            ? "Test run closed."
            : $"Test run closed. Settings changed on the line for {string.Join(", ", lineChanged)} - right-click a station for Line Settings.";
    }

    /// <summary>
    /// The running project's simulated PLCs, each in a PLC window beside it
    /// - shown after the test run's window, so they can be used while it is
    /// up: flip the trigger bit there, and the station inspects.
    /// </summary>
    internal IReadOnlyList<Window> OpenSimulators(RuntimeWindow running)
    {
        var opened = new List<Window>();

        if (_project is not { } project)
            return opened;

        foreach (var runner in running.Controllers.Runners.Where(runner => runner.Driver is SimulatedPlc))
        {
            if (project.FindController(runner.Name) is not { } controller)
                continue;

            if (OpenPlcWindow(running, new PlcViewModel(project, controller, runner)) is { } window)
            {
                // Beside the screen rather than over it.
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = running.Left + Math.Max(0, running.ActualWidth - window.Width) + (opened.Count * 30);
                window.Top = running.Top + 40 + (opened.Count * 30);
                opened.Add(window);
            }
        }

        return opened;
    }

    // ---- cameras -------------------------------------------------------------

    /// <summary>
    /// A station added from a recipe with a Camera node: offered a project
    /// camera made from it - the node's camera, every setting as the recipe -
    /// so the camera is changed here from now on. A camera already in the
    /// project with that camera is simply used.
    /// </summary>
    private void OfferCamera(Station station)
    {
        if (_project is not { } project)
            return;

        CameraSetup? found;

        try
        {
            found = StationCamera.From(GraphSerializer.Load(project.RecipePath(station)).Graph);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return;
        }

        if (found is not { HasDevice: true } setup)
            return;

        var serial = Sirius.Vision.Cameras.CameraInfo.SerialIn(setup.Device);

        if (project.Cameras.FirstOrDefault(camera => serial is not null &&
                string.Equals(Sirius.Vision.Cameras.CameraInfo.SerialIn(camera.Setup.Device), serial, StringComparison.OrdinalIgnoreCase)) is { } same)
        {
            Guard(() => project.SetStationCamera(station, same), "Add Station");
            StatusText = $"Added {station.Name}; it takes its pictures from {same.Name}, the project's camera for {setup.Device}.";
            return;
        }

        var answer = Ask(
            $"{station.Name}'s recipe takes its pictures from {setup.Device}.\n\n" +
            "Make it a camera of the project? Then the camera - which one, its exposure and gain - is set here in " +
            "Builder, and changed without opening the recipe in Studio. Its settings start as the recipe's.",
            "Add Station");

        if (answer != MessageBoxResult.Yes)
            return;

        Guard(() =>
        {
            var camera = project.AddCamera(project.CameraNameProblem($"{station.Name} camera") is null ? $"{station.Name} camera" : project.SuggestCameraName(), new CameraSetup(setup.Device));
            project.SetStationCamera(station, camera);
            SelectedStation = Find(station);
            StatusText = $"Added {station.Name}, taking its pictures from the camera {camera.Name}: double-click it to set it up.";
        }, "Add Station");
    }

    private void AskThenAddCamera()
    {
        if (_project is not { } project)
            return;

        var name = TextPromptWindow.Ask(
            Application.Current?.MainWindow,
            "Add Camera",
            "A camera stations take their pictures from - chosen and set up here, without opening a recipe. What is it called?",
            project.SuggestCameraName(),
            text => project.CameraNameProblem(text));

        if (name is not null && AddCamera(name) is { } added)
            OpenCamera(Find(added));
    }

    /// <summary>Adds a camera, none chosen yet.</summary>
    internal CameraUnit? AddCamera(string name, CameraSetup? setup = null)
    {
        if (_project is not { } project)
            return null;

        CameraUnit? added = null;

        Guard(() =>
        {
            added = project.AddCamera(name, setup);
            SelectedCamera = Find(added);
            StatusText = $"Added the camera {added.Name}. Choose which camera it is, then have stations use it: right-click a station ▸ Camera.";
        }, "Add Camera");

        return added;
    }

    /// <summary>A camera's window: find, choose, set, look through it.</summary>
    internal void OpenCamera(CameraUnitViewModel? camera)
    {
        if (camera is null || _project is not { } project)
            return;

        var unit = camera.Camera;
        OpenCameraWindow(Application.Current?.MainWindow, new CameraViewModel(project, unit));
        Rebuild();

        StatusText = unit.Setup.HasDevice
            ? $"{unit.Name} is {unit.Setup.Device}."
            : $"{unit.Name} has no camera chosen yet: double-click it, Find, then Use.";
    }

    /// <summary>Has a station take its pictures from <paramref name="camera"/>, or - null - from its recipe's own.</summary>
    internal void SetStationCamera(StationViewModel station, CameraUnit? camera)
    {
        if (_project is not { } project)
            return;

        Guard(() =>
        {
            if (project.SetStationCamera(station.Station, camera))
            {
                StatusText = camera is null
                    ? $"{station.Name} takes its pictures from its recipe's own Camera node."
                    : $"{station.Name} takes its pictures from {camera.Name}" +
                      (CameraProblem(station.Station, camera) is { } problem ? $" - but {problem}" : ".");
            }
        }, "Camera");
    }

    /// <summary>Why a station's recipe cannot take <paramref name="camera"/>, lower-cased to follow a "but"; or null.</summary>
    private string? CameraProblem(Station station, CameraUnit camera)
    {
        if (_project is not { } project)
            return null;

        try
        {
            var recipe = GraphSerializer.Load(project.RecipePath(station)).Graph;
            return StationCamera.NodeFor(recipe, camera, out var problem) is null && problem is not null
                ? char.ToLowerInvariant(problem[0]) + problem[1..]
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    private void AskThenRenameCamera(CameraUnitViewModel? camera)
    {
        if (camera is null || _project is not { } project)
            return;

        var name = TextPromptWindow.Ask(
            Application.Current?.MainWindow,
            "Rename Camera",
            "Stations taking their pictures from it follow the new name.",
            camera.Name,
            text => project.CameraNameProblem(text, camera.Camera));

        if (name is not null && name.Trim() != camera.Name)
            Guard(() => project.RenameCamera(camera.Camera, name), "Rename Camera");
    }

    private void AskThenRemoveCamera(CameraUnitViewModel? camera)
    {
        if (camera is null || _project is null)
            return;

        var users = _project.StationsUsing(camera.Camera);

        var answer = Dialog.Confirm(
            $"Remove the camera '{camera.Name}' from the project?\n\n" +
            (users.Count == 0
                ? "No station uses it."
                : $"{string.Join(", ", users.Select(station => station.Name))} go back to taking pictures as their recipes' own Camera nodes say."),
            "Remove Camera");

        if (answer)
            RemoveCamera(camera);
    }

    internal void RemoveCamera(CameraUnitViewModel camera)
    {
        if (_project is { } project)
            Guard(() => project.RemoveCamera(camera.Camera), "Remove Camera");
    }

    private CameraUnitViewModel? Find(CameraUnit camera) =>
        Cameras.FirstOrDefault(candidate => ReferenceEquals(candidate.Camera, camera));

    // ---- controllers -------------------------------------------------------

    private void AddController()
    {
        if (_project is not { } project)
            return;

        var name = TextPromptWindow.Ask(
            Application.Current?.MainWindow,
            "Add Controller",
            "A PLC and its workflow - simulated to begin with, so it can be tried at the desk. What is it called?",
            project.SuggestControllerName(),
            text => project.ControllerNameProblem(text));

        if (name is null)
            return;

        if (AddController(name) is { } added)
            OpenWorkflow(Find(added));
    }

    /// <summary>
    /// Adds a controller with a simulated PLC of its own name and the starter
    /// workflow: M0.0 rising has the first station inspect.
    /// </summary>
    internal Controller? AddController(string name)
    {
        if (_project is not { } project)
            return null;

        Controller? added = null;
        var station = project.Stations.FirstOrDefault()?.Name;

        Guard(() =>
        {
            added = project.AddController(
                name,
                SimulatedPlcDriver.Key,
                name.Trim(),
                path => WorkflowFile.Save(StarterWorkflow.For(station), path, name.Trim()));

            SelectedController = Find(added);
            StatusText = station is null
                ? $"Added {added.Name}, a simulated PLC with a starter workflow. Add a station for it to inspect."
                : $"Added {added.Name}, a simulated PLC: M0.0 rising has {station} inspect. Open its PLC window to try it, or Test Run.";
        }, "Add Controller");

        return added;
    }

    /// <summary>Opens a controller's workflow in a tab - or brings its tab to the front.</summary>
    internal WorkflowDocumentViewModel? OpenWorkflow(ControllerViewModel? controller)
    {
        if (controller is null || _project is not { } project)
            return null;

        var open = Workflows.FirstOrDefault(workflow => ReferenceEquals(workflow.Controller, controller.Controller));

        if (open is null)
        {
            try
            {
                open = new WorkflowDocumentViewModel(project, controller.Controller);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                Dialog.Error($"{controller.WorkflowFile} could not be opened: {ex.Message}", "Open Workflow");
                return null;
            }

            if (open.LoadWarnings.Count > 0)
                Dialog.Warn(string.Join(Environment.NewLine, open.LoadWarnings), "Workflow opened with warnings");

            open.PropertyChanged += OnWorkflowModified;
            Workflows.Add(open);
        }

        WorkflowShown?.Invoke(this, open);
        StatusText = $"{controller.Name}'s workflow: {open.ProblemText}";
        return open;
    }

    /// <summary>Closes a workflow's tab, offering to save it first.</summary>
    /// <returns>False when the operator chose to keep it open, or saving failed.</returns>
    internal bool CloseWorkflow(WorkflowDocumentViewModel workflow)
    {
        if (workflow.IsModified)
        {
            var answer = Ask($"Save {workflow.Controller.Name}'s workflow first?", "Close Workflow");

            if (answer is MessageBoxResult.Cancel or MessageBoxResult.None)
                return false;

            if (answer == MessageBoxResult.Yes && !workflow.Save())
                return false;
        }

        Forget(workflow);
        return true;
    }

    private void Forget(WorkflowDocumentViewModel workflow)
    {
        if (ReferenceEquals(ActiveWorkflow, workflow))
            ActiveWorkflow = null;

        workflow.PropertyChanged -= OnWorkflowModified;
        Workflows.Remove(workflow);
        workflow.Dispose();
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>The controller's PLC window: try the connection, read and write, drive a simulated PLC.</summary>
    internal Window? ShowPlc(ControllerViewModel? controller)
    {
        if (controller is null || _project is not { } project)
            return null;

        return OpenPlcWindow(Application.Current?.MainWindow, new PlcViewModel(project, controller.Controller));
    }

    private void RenameController(ControllerViewModel? controller)
    {
        if (controller is null || _project is not { } project)
            return;

        var name = TextPromptWindow.Ask(
            Application.Current?.MainWindow,
            "Rename Controller",
            "What the controller is called. Its workflow keeps its file.",
            controller.Name,
            text => project.ControllerNameProblem(text, controller.Controller));

        if (name is null || name == controller.Name)
            return;

        var old = controller.Name;

        Guard(() =>
        {
            project.RenameController(controller.Controller, name);

            // Counters of its parts follow: in a screen open in a tab as a
            // change to save, in a screen's file at once.
            var open = Designers.Where(designer => designer.RenameController(old, name)).Select(designer => designer.Name).ToArray();
            var written = ScreenSet.RenameController(project, old, name, Designers.Select(designer => designer.Name).ToArray());
            var all = open.Concat(written).ToArray();

            StatusText = all.Length == 0
                ? $"Renamed {old} to {name}."
                : open.Length == 0
                    ? $"Renamed {old} to {name}; the counters of its parts on {string.Join(", ", all)} follow."
                    : $"Renamed {old} to {name}; the counters of its parts on {string.Join(", ", all)} follow. Save {string.Join(", ", open)} to keep it.";
        }, "Rename Controller");
    }

    private void RemoveController(ControllerViewModel? controller)
    {
        if (controller is null)
            return;

        var answer = Dialog.Confirm(
            $"Remove controller '{controller.Name}' from the project?\n\n" +
            $"Its workflow file stays in the project's controllers folder ({controller.WorkflowFile}).",
            "Remove Controller");

        if (answer)
            RemoveController(controller, ask: true);
    }

    internal bool RemoveController(ControllerViewModel controller, bool ask)
    {
        if (_project is not { } project)
            return false;

        if (Workflows.FirstOrDefault(workflow => ReferenceEquals(workflow.Controller, controller.Controller)) is { } open)
        {
            if (ask && !CloseWorkflow(open))
                return false;

            if (Workflows.Contains(open))
                Forget(open);
        }

        Guard(() => project.RemoveController(controller.Controller), "Remove Controller");
        return true;
    }

    private ControllerViewModel? Find(Controller controller) =>
        Controllers.FirstOrDefault(candidate => ReferenceEquals(candidate.Controller, controller));

    // ---- screens ----------------------------------------------------------

    /// <summary>A designer, listened to: its changes on the title, its news on the status line, its properties when in front.</summary>
    private ScreenDesignerViewModel Designed(ScreenDesignerViewModel designer)
    {
        designer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScreenDesignerViewModel.IsModified))
                OnPropertyChanged(nameof(Title));

            if (ActiveWorkflow is null && ReferenceEquals(designer, _designer) &&
                e.PropertyName is nameof(ScreenDesignerViewModel.PropertyCategories)
                    or nameof(ScreenDesignerViewModel.PropertyTitle) or nameof(ScreenDesignerViewModel.PropertySubtitle))
            {
                OnPropertyChanged(e.PropertyName);
            }

            // The designer's news - saved, limited, reset - on Builder's status line.
            if (e.PropertyName == nameof(ScreenDesignerViewModel.StatusText) && !string.IsNullOrEmpty(designer.StatusText))
                StatusText = designer.StatusText;
        };

        Designers.Add(designer);
        return designer;
    }

    /// <summary>
    /// Every screen of the project, as a designer sees them: each open one
    /// as it is now, saved or not, the rest as saved.
    /// </summary>
    private IReadOnlyList<ScreenInfo> ScreenInfos() =>
        Screens.Select(screen => Designers.FirstOrDefault(designer => string.Equals(designer.Name, screen.Name, StringComparison.OrdinalIgnoreCase))?.Info ?? screen.Info)
            .ToArray();

    /// <summary>The screen called <paramref name="name"/> in the Project panel, or null.</summary>
    internal ScreenItemViewModel? FindScreen(string name) =>
        Screens.FirstOrDefault(screen => string.Equals(screen.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Opens a screen in its designer's tab - or brings the tab to the front.</summary>
    internal ScreenDesignerViewModel? OpenScreen(ScreenItemViewModel? screen)
    {
        if (screen is null || _recipes is not { } recipes)
            return null;

        var open = Designers.FirstOrDefault(designer => string.Equals(designer.Name, screen.Name, StringComparison.OrdinalIgnoreCase))
                   ?? Designed(new ScreenDesignerViewModel(recipes, screen.Name, ScreenInfos));

        ShowDesigner(open);
        StatusText = $"{open.Name}: {open.ProblemText}";
        return open;
    }

    /// <summary>Closes a screen's tab, offering to save it first. The start screen's tab stays.</summary>
    /// <returns>False when the operator chose to keep it open, or saving failed.</returns>
    internal bool CloseScreen(ScreenDesignerViewModel designer)
    {
        if (designer.IsStart)
            return false;

        if (!Designers.Contains(designer))
            return true;

        if (!ConfirmScreen(designer, "Close Screen"))
            return false;

        Forget(designer);
        return true;
    }

    private void Forget(ScreenDesignerViewModel designer)
    {
        Designers.Remove(designer);

        if (ReferenceEquals(_designer, designer))
            Designer = _mainDesigner;

        OnPropertyChanged(nameof(Title));
    }

    private void AskThenAddScreen(ScreenKind kind)
    {
        if (_project is not { } project)
            return;

        var popup = kind == ScreenKind.Popup;

        var name = TextPromptWindow.Ask(
            Application.Current?.MainWindow,
            popup ? "Add Popup" : "Add Page",
            popup
                ? "A popup opens over the page from a button, the page dimmed behind it and still running - for settings, a login, a part's details. What is it called?"
                : "A page fills the window in place of the one shown, opened from a button; Back returns. What is it called?",
            ScreenSet.Suggest(project, popup ? "Settings" : "Page"),
            text => ScreenSet.NameProblem(project, text));

        if (name is not null)
            AddScreen(name, kind);
    }

    /// <summary>
    /// Adds a page or popup - a title and its way out, to build on - writes
    /// it, and opens it in a tab.
    /// </summary>
    internal ScreenDesignerViewModel? AddScreen(string name, ScreenKind kind)
    {
        if (_project is not { } project)
            return null;

        ScreenDesignerViewModel? opened = null;

        Guard(() =>
        {
            if (ScreenSet.NameProblem(project, name) is { } problem)
                throw new ArgumentException(problem);

            var start = _mainDesigner?.Info ?? new ScreenInfo(Screen.StartName, ScreenKind.Page, 1280, 800);
            ScreenSet.NewScreen(name, kind, start.Width, start.Height).Save(ScreenSet.PathFor(project, name));

            RebuildScreens();
            SelectedItem = FindScreen(name);
            opened = OpenScreen(FindScreen(name));

            StatusText = kind == ScreenKind.Popup
                ? $"Added the popup {name.Trim()}. Put a button on another screen to open it: Does - Open Screen, Opens - {name.Trim()}."
                : $"Added the page {name.Trim()}. Put a button on another screen to open it: Does - Open Screen, Opens - {name.Trim()}.";
        }, kind == ScreenKind.Popup ? "Add Popup" : "Add Page");

        return opened;
    }

    private void AskThenRenameScreen(ScreenItemViewModel? screen)
    {
        if (screen is null || _project is not { } project || !screen.CanChange)
            return;

        var name = TextPromptWindow.Ask(
            Application.Current?.MainWindow,
            "Rename Screen",
            "Buttons opening it follow the new name.",
            screen.Name,
            text => ScreenSet.NameProblem(project, text, screen.Name));

        if (name is not null && name.Trim() != screen.Name)
            RenameScreen(screen, name);
    }

    /// <summary>
    /// Renames a screen: its file, and the buttons opening it - in screens
    /// open in a designer as an undoable change to save, in the others at
    /// once.
    /// </summary>
    internal void RenameScreen(ScreenItemViewModel screen, string name)
    {
        if (_project is not { } project)
            return;

        var from = screen.Name;

        Guard(() =>
        {
            var open = Designers.ToArray();
            var renamed = Designers.FirstOrDefault(designer => string.Equals(designer.Name, from, StringComparison.OrdinalIgnoreCase));

            // Open screens are left to their designers, so changes not yet saved are not lost.
            var leave = open.Where(designer => !ReferenceEquals(designer, renamed)).Select(designer => designer.Name).ToArray();
            ScreenSet.Rename(project, from, name, leave);

            renamed?.Renamed(name);

            var pointing = open
                .Where(designer => !ReferenceEquals(designer, renamed))
                .Where(designer => designer.RetargetScreen(from, name.Trim()))
                .Select(designer => designer.Name)
                .ToArray();

            RebuildScreens();
            SelectedItem = FindScreen(name.Trim());

            StatusText = pointing.Length == 0
                ? $"Renamed the screen {from} to {name.Trim()}."
                : $"Renamed the screen {from} to {name.Trim()}; buttons on {string.Join(", ", pointing)} follow - save to keep them.";
        }, "Rename Screen");
    }

    private void AskThenRemoveScreen(ScreenItemViewModel? screen)
    {
        if (screen is null || !screen.CanChange)
            return;

        var answer = Dialog.Confirm(
            $"Remove the screen '{screen.Name}' from the project?\n\n" +
            $"Its file ({Path.GetFileName(screen.FilePath)}) goes to the Recycle Bin, where it can be restored from. " +
            "Buttons opening it are reported.",
            "Remove Screen");

        if (answer)
            RemoveScreen(screen);
    }

    /// <summary>Removes a screen: its tab closes, unsaved changes and all, and its file goes to the Recycle Bin.</summary>
    internal bool RemoveScreen(ScreenItemViewModel screen)
    {
        if (_project is null || !screen.CanChange)
            return false;

        if (Designers.FirstOrDefault(designer => string.Equals(designer.Name, screen.Name, StringComparison.OrdinalIgnoreCase)) is { } open)
            Forget(open);

        var removed = false;

        Guard(() =>
        {
            if (File.Exists(screen.FilePath))
                Recycle(screen.FilePath);

            removed = true;
            StatusText = $"Removed the screen {screen.Name}; its file is in the Recycle Bin.";
        }, "Remove Screen");

        RebuildScreens();

        foreach (var designer in Designers)
            designer.Refresh();

        return removed;
    }

    /// <summary>Sends a file to the Recycle Bin; replaced in tests.</summary>
    internal Action<string> Recycle { get; set; } = path =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
            path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    /// <summary>The screens as they are in the folder now - after one was added, renamed or removed.</summary>
    private void RebuildScreens()
    {
        var selected = SelectedItem as ScreenItemViewModel;
        Rebuild();

        if (selected is not null)
            SelectedItem = FindScreen(selected.Name);

        foreach (var designer in Designers)
            designer.Refresh();
    }

    // ---- housekeeping -----------------------------------------------------

    private void OnProjectChanged(object? sender, EventArgs e)
    {
        Rebuild();

        foreach (var designer in Designers)
            designer.Refresh();
    }

    private void Rebuild()
    {
        // What was selected, found again among the new items.
        var selected = SelectedItem switch
        {
            StationViewModel station => (object)station.Station,
            ControllerViewModel controller => controller.Controller,
            CameraUnitViewModel camera => camera.Camera,
            ScreenItemViewModel screen => screen.Name,
            _ => null,
        };

        Stations.Clear();
        Cameras.Clear();
        Controllers.Clear();
        Screens.Clear();
        Items.Clear();

        if (_project is { } project)
        {
            foreach (var station in project.Stations)
                Stations.Add(new StationViewModel(project, station));

            foreach (var camera in project.Cameras)
                Cameras.Add(new CameraUnitViewModel(project, camera));

            foreach (var controller in project.Controllers)
                Controllers.Add(new ControllerViewModel(project, controller));

            foreach (var name in ScreenSet.Names(project))
                Screens.Add(new ScreenItemViewModel(project, name));
        }

        foreach (var item in Stations.Cast<object>().Concat(Cameras).Concat(Controllers).Concat(Screens))
            Items.Add(item);

        SelectedItem = selected switch
        {
            Station station => Find(station),
            CameraUnit camera => Find(camera),
            Controller controller => Find(controller),
            string screen => FindScreen(screen),
            _ => null,
        };

        OnPropertyChanged(nameof(HasNoStations));
        OnPropertyChanged(nameof(HasNoControllers));
        OnPropertyChanged(nameof(IsEmpty));

        foreach (var workflow in Workflows)
            workflow.ControllerChanged();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(Title));
        CloseProjectCommand.NotifyCanExecuteChanged();
        AddStationCommand.NotifyCanExecuteChanged();
        AddFromRecipeCommand.NotifyCanExecuteChanged();
        TestRunCommand.NotifyCanExecuteChanged();
        AddControllerCommand.NotifyCanExecuteChanged();
        AddCameraCommand.NotifyCanExecuteChanged();
        UsersCommand.NotifyCanExecuteChanged();
        AddPageCommand.NotifyCanExecuteChanged();
        AddPopupCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasNoControllers));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private StationViewModel? Find(Station station) =>
        Stations.FirstOrDefault(candidate => ReferenceEquals(candidate.Station, station));

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

    private static bool LaunchProcess(string program, string file)
    {
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false };
            start.ArgumentList.Add(file);
            return Process.Start(start) is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    // ---- the last project -------------------------------------------------

    private static string RememberedFile => Path.Combine(SiriusHost.SettingsFolder, "builder.json");

    /// <summary>The project open when Builder last closed, if it is still there.</summary>
    public static string? LastProject()
    {
        try
        {
            var path = JsonSerializer.Deserialize<Remembered>(File.ReadAllText(RememberedFile))?.LastProject;
            return path is not null && File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>For tests: not remembered between runs.</summary>
    internal bool RememberProjects { get; set; } = true;

    private void Remember(string path)
    {
        if (!RememberProjects)
            return;

        try
        {
            Directory.CreateDirectory(SiriusHost.SettingsFolder);
            File.WriteAllText(RememberedFile, JsonSerializer.Serialize(new Remembered { LastProject = path }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a convenience.
        }
    }

    private sealed class Remembered
    {
        public string? LastProject { get; set; }
    }
}
