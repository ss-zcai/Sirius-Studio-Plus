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
using Sirius.Plc;
using Sirius.Plc.Workflows;
using Sirius.Studio.App;
using Sirius.Studio.App.Hosting;
using Sirius.Studio.App.Screens;

namespace Sirius.Builder;

/// <summary>
/// Sirius Builder's window: the open project, its stations, and its screen
/// in the designer (decision 71).
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
    private ScreenDesignerViewModel? _designer;

    [ObservableProperty]
    private StationViewModel? _selectedStation;

    [ObservableProperty]
    private ControllerViewModel? _selectedController;

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
    }

    /// <summary>The open project, or null.</summary>
    public Project? Project => _project;

    public bool HasProject => _project is not null;

    public ObservableCollection<StationViewModel> Stations { get; } = [];

    public bool HasNoStations => _project is not null && Stations.Count == 0;

    /// <summary>The project's PLCs, each with its workflow.</summary>
    public ObservableCollection<ControllerViewModel> Controllers { get; } = [];

    public bool HasNoControllers => _project is not null && Controllers.Count == 0;

    /// <summary>The workflows open in tabs.</summary>
    public ObservableCollection<WorkflowDocumentViewModel> Workflows { get; } = [];

    /// <summary>A workflow is to be shown - opened, or brought to the front.</summary>
    public event EventHandler<WorkflowDocumentViewModel>? WorkflowShown;

    /// <summary>The project's screen in the designer; null with no project open.</summary>
    public ScreenDesignerViewModel? Designer
    {
        get => _designer;
        private set => SetProperty(ref _designer, value);
    }

    public string Title => _project is null ? AppTitle : $"{(_designer?.IsModified == true ? "*" : string.Empty)}{_project.Name} - {AppTitle}";

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
        project.Changed += OnProjectChanged;

        Rebuild();

        var designer = new ScreenDesignerViewModel(new StationRecipes(project));
        designer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScreenDesignerViewModel.IsModified))
                OnPropertyChanged(nameof(Title));

            // The designer's news - saved, limited, reset - on Builder's status line.
            if (e.PropertyName == nameof(ScreenDesignerViewModel.StatusText) && !string.IsNullOrEmpty(designer.StatusText))
                StatusText = designer.StatusText;
        };

        Designer = designer;
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

        if (!ConfirmScreen("Close Project"))
            return false;

        foreach (var workflow in Workflows.ToArray())
        {
            if (!CloseWorkflow(workflow))
                return false;
        }

        _project.Changed -= OnProjectChanged;
        _project = null;
        Designer = null;
        Stations.Clear();
        SelectedStation = null;
        Controllers.Clear();
        SelectedController = null;
        StatusText = "Open a project, or make a new one.";
        Changed();
        return true;
    }

    /// <summary>Asks to save the screen when it has changes.</summary>
    /// <returns>False when the operator cancelled, or saving failed.</returns>
    internal bool ConfirmScreen(string title)
    {
        if (_designer is not { IsModified: true } designer)
            return true;

        var answer = Ask("Save the screen first?", title);

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

        _designer?.Refresh();
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

            if (_designer?.RenameStation(old, station.Name) == true)
                StatusText = $"Renamed {old} to {station.Name}, on the screen too. Save the screen to keep it.";
            else
                StatusText = $"Renamed {old} to {station.Name}.";
        }, "Rename Station");
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
        _designer?.Refresh();
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

        if (_designer is { IsModified: true } designer)
        {
            var answer = Ask("The test run shows the screen as saved. Save it first?", "Test Run");

            if (answer is MessageBoxResult.Cancel or MessageBoxResult.None)
                return;

            if (answer == MessageBoxResult.Yes && !designer.Save())
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

        Workflows.Remove(workflow);
        workflow.Dispose();
        return true;
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

        if (name is not null && name != controller.Name)
            Guard(() => project.RenameController(controller.Controller, name), "Rename Controller");
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
            {
                Workflows.Remove(open);
                open.Dispose();
            }
        }

        Guard(() => project.RemoveController(controller.Controller), "Remove Controller");
        return true;
    }

    private ControllerViewModel? Find(Controller controller) =>
        Controllers.FirstOrDefault(candidate => ReferenceEquals(candidate.Controller, controller));

    // ---- housekeeping -----------------------------------------------------

    private void OnProjectChanged(object? sender, EventArgs e)
    {
        Rebuild();
        _designer?.Refresh();
    }

    private void Rebuild()
    {
        var selected = SelectedStation?.Station;
        var selectedController = SelectedController?.Controller;

        Stations.Clear();
        Controllers.Clear();

        if (_project is { } project)
        {
            foreach (var station in project.Stations)
                Stations.Add(new StationViewModel(project, station));

            foreach (var controller in project.Controllers)
                Controllers.Add(new ControllerViewModel(project, controller));
        }

        SelectedStation = selected is null ? null : Find(selected);
        SelectedController = selectedController is null ? null : Find(selectedController);
        OnPropertyChanged(nameof(HasNoStations));
        OnPropertyChanged(nameof(HasNoControllers));

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
        OnPropertyChanged(nameof(HasNoControllers));
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
