using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Sirius.Core.Graph;
using Sirius.Core.Serialization;

namespace Sirius.Core.Projects;

/// <summary>
/// A station: one inspection running on its own - in the MVP, a named
/// recipe whose camera is inside it.
/// </summary>
/// <remarks>
/// Its own trigger, outputs and cameras come later (runtime design, sections
/// 5 and 9); the project file keeps a list of stations from the start so
/// that adds to a station rather than reshaping the file.
/// </remarks>
public sealed class Station : ObservableObject
{
    private readonly Dictionary<string, SettingLimit> _limits = new(StringComparer.OrdinalIgnoreCase);
    private string _name;
    private ImageSaving _saving = ImageSaving.Off;
    private string? _camera;
    private int _shotsPerPart = 1;

    public Station(string name, string recipe)
    {
        _name = name;
        Recipe = recipe;
    }

    /// <summary>
    /// How far the line may take each published setting, by its published
    /// name - chosen in Sirius Builder for this deployment. A setting not
    /// listed is limited only by its node.
    /// </summary>
    public IReadOnlyDictionary<string, SettingLimit> Limits => _limits;

    /// <summary>The limit set for <paramref name="setting"/>, or null.</summary>
    public SettingLimit? LimitFor(string setting) => _limits.GetValueOrDefault(setting);

    internal void SetLimit(string setting, SettingLimit? limit)
    {
        if (limit is null || limit.IsNone)
            _limits.Remove(setting);
        else
            _limits[setting] = limit;
    }

    /// <summary>Which pictures the station keeps when it runs, and where. Off until chosen in Builder.</summary>
    public ImageSaving Saving
    {
        get => _saving;
        internal set => SetProperty(ref _saving, value ?? ImageSaving.Off);
    }

    /// <summary>What screens and the operator call it: "Camera 1", "Top".</summary>
    public string Name
    {
        get => _name;
        internal set => SetProperty(ref _name, value);
    }

    /// <summary>Its recipe, relative to the project folder, with forward slashes.</summary>
    public string Recipe { get; }

    /// <summary>
    /// How many pictures make a part (F26): the set's last is that shot,
    /// unless the PLC says Last itself. 1 - a picture a part - unless set.
    /// </summary>
    public int ShotsPerPart
    {
        get => _shotsPerPart;
        internal set => SetProperty(ref _shotsPerPart, Math.Clamp(value, 1, MostShots));
    }

    /// <summary>The most pictures a part may take: 2 cameras under 4 lights is 8; room beyond.</summary>
    public const int MostShots = 16;

    /// <summary>
    /// The project camera it takes its pictures from, by name; null for the
    /// recipe's own Camera node as it is (backlog F29).
    /// </summary>
    public string? Camera
    {
        get => _camera;
        internal set => SetProperty(ref _camera, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public override string ToString() => Name;
}

/// <summary>
/// The lowest and highest a published setting may be set to on the line;
/// either null for as far as its node allows. Never wider than the node's
/// own range: the two are combined (<see cref="Runtime.Tunable"/>).
/// </summary>
public sealed record SettingLimit(double? Lowest, double? Highest)
{
    /// <summary>Neither end set: no limit of its own.</summary>
    public bool IsNone => Lowest is null && Highest is null;

    /// <summary>Why a setting cannot be limited to these ends, or null.</summary>
    public static string? Problem(double? lowest, double? highest)
    {
        if (lowest is { } low && !double.IsFinite(low) || highest is { } high && !double.IsFinite(high))
            return "A limit must be a number.";

        return lowest > highest ? "The lowest value is above the highest." : null;
    }
}

/// <summary>What opening a project found, besides the project.</summary>
public sealed record ProjectLoadResult(Project Project, IReadOnlyList<string> Warnings);

/// <summary>
/// A deployment built in Sirius Builder: stations, each running a recipe
/// made in Studio, the screens that show them, and later the PLC and I/O
/// (runtime design, section 0; decision 71).
/// </summary>
/// <remarks>
/// <para>
/// A folder, copied to the machine as it is: the project file at its top,
/// and the recipes it runs in <c>recipes/</c> beside it. Recipes are kept
/// inside rather than referenced where they lie, so the folder is complete
/// on its own.
/// </para>
/// <para>
/// The project file is written whenever the project's own structure
/// changes - a station added, renamed or removed - so there is no unsaved
/// project to lose. Recipes are saved as they always were, one at a time.
/// </para>
/// </remarks>
public sealed class Project
{
    /// <summary>The project file's extension.</summary>
    public const string Extension = ".sproj";

    /// <summary>The folder recipes are kept in, inside the project's.</summary>
    public const string RecipesFolder = "recipes";

    /// <summary>The folder controllers' workflows are kept in, inside the project's.</summary>
    public const string ControllersFolder = "controllers";

    /// <summary>The workflow file's extension, as <c>Sirius.Plc</c> writes it.</summary>
    public const string WorkflowExtension = ".sflow";

    /// <summary>The longest station or controller name allowed.</summary>
    public const int MaxNameLength = 64;

    private readonly List<Station> _stations = [];
    private readonly List<Controller> _controllers = [];
    private readonly List<CameraUnit> _cameras = [];
    private readonly List<ProjectUser> _users = [];
    private int _logOutMinutes = DefaultLogOutMinutes;

    /// <summary>How long a user stays logged in, idle, by default.</summary>
    public const int DefaultLogOutMinutes = 5;

    private Project(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
    }

    /// <summary>The project file.</summary>
    public string FilePath { get; }

    /// <summary>The project's folder.</summary>
    public string Folder => Path.GetDirectoryName(FilePath)!;

    /// <summary>The project's name: its file's, without the extension.</summary>
    public string Name => Path.GetFileNameWithoutExtension(FilePath);

    public IReadOnlyList<Station> Stations => _stations;

    /// <summary>The PLCs and their workflows.</summary>
    public IReadOnlyList<Controller> Controllers => _controllers;

    /// <summary>The cameras stations take their pictures from.</summary>
    public IReadOnlyList<CameraUnit> Cameras => _cameras;

    /// <summary>
    /// Who may log in on the line (F2). With none, nothing on the running
    /// screens is locked: a project without users runs as before.
    /// </summary>
    public IReadOnlyList<ProjectUser> Users => _users;

    /// <summary>Whether user levels are in force: there is someone to log in as.</summary>
    public bool HasUsers => _users.Count > 0;

    /// <summary>Minutes idle before a user is logged out, back to Operator; 0 for never.</summary>
    public int LogOutMinutes => _logOutMinutes;

    /// <summary>Raised after stations, cameras or controllers are added, removed, renamed, reordered or changed.</summary>
    public event EventHandler? Changed;

    // ---- creating and opening ------------------------------------------

    /// <summary>
    /// Makes a project folder <paramref name="name"/> inside
    /// <paramref name="parent"/>, with one station - or none.
    /// </summary>
    /// <param name="firstRecipe">The first station's recipe, or null for an empty one.</param>
    /// <param name="firstStation">The first station's name; null makes the project with no station yet.</param>
    public static Project Create(string parent, string name, VisionGraph? firstRecipe = null, string? firstStation = "Station 1")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);

        if (FileNameProblem(name) is { } problem)
            throw new ArgumentException(problem, nameof(name));

        var folder = Path.Combine(parent, name.Trim());

        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new IOException($"{folder} already exists and is not empty. Choose another name or place.");

        Directory.CreateDirectory(Path.Combine(folder, RecipesFolder));

        var project = new Project(Path.Combine(folder, name.Trim() + Extension));

        if (firstStation is null)
            project.Save();
        else
            project.AddStation(firstStation, firstRecipe ?? new VisionGraph());
        return project;
    }

    public static ProjectLoadResult Load(string path)
    {
        var document = JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(path), Options)
                       ?? throw new InvalidDataException("The project file is empty.");

        if (document.Version > ProjectDocument.CurrentVersion)
        {
            throw new InvalidDataException(
                $"This project was saved by a newer version of Sirius Studio Plus " +
                $"(format {document.Version}, this build reads {ProjectDocument.CurrentVersion}).");
        }

        var project = new Project(path);
        var warnings = new List<string>();

        foreach (var record in document.Cameras ?? [])
        {
            var name = record.Name?.Trim() ?? string.Empty;

            if (name.Length == 0)
            {
                warnings.Add("A camera with no name was skipped.");
                continue;
            }

            if (project._cameras.Any(camera => Same(camera.Name, name)))
            {
                warnings.Add($"Camera '{name}' is listed twice; the second was skipped.");
                continue;
            }

            var setup = new CameraSetup(record.Device?.Trim() ?? string.Empty, record.Exposure, record.Gain, record.Trigger, record.Timeout);

            if (setup.Problem() is { } problem)
            {
                warnings.Add($"Camera '{name}': its settings could not be used ({problem}); it takes the recipe's.");
                setup = new CameraSetup(setup.Device);
            }

            project._cameras.Add(new CameraUnit(name, setup));
        }

        foreach (var record in document.Stations)
        {
            var name = record.Name?.Trim() ?? string.Empty;

            if (name.Length == 0 || string.IsNullOrWhiteSpace(record.Recipe))
            {
                warnings.Add("A station with no name or no recipe was skipped.");
                continue;
            }

            if (project._stations.Any(station => Same(station.Name, name)))
            {
                warnings.Add($"Station '{name}' is listed twice; the second was skipped.");
                continue;
            }

            var station = new Station(name, record.Recipe.Replace('\\', '/'));

            if (!project.IsInside(project.RecipePath(station)))
            {
                warnings.Add($"Station '{name}': its recipe '{record.Recipe}' is outside the project folder; skipped.");
                continue;
            }

            if (!File.Exists(project.RecipePath(station)))
                warnings.Add($"Station '{name}': its recipe {record.Recipe} is missing.");

            foreach (var (setting, limit) in record.Limits ?? [])
            {
                if (string.IsNullOrWhiteSpace(setting) || limit is null)
                    continue;

                if (SettingLimit.Problem(limit.Lowest, limit.Highest) is { } problem)
                    warnings.Add($"Station '{name}': the limits of '{setting}' could not be used ({problem}).");
                else
                    station.SetLimit(setting.Trim(), new SettingLimit(limit.Lowest, limit.Highest));
            }

            if (record.Shots is { } shots)
                station.ShotsPerPart = shots;

            if (!string.IsNullOrWhiteSpace(record.Camera))
            {
                if (project.FindCamera(record.Camera) is { } camera)
                    station.Camera = camera.Name;
                else
                    warnings.Add($"Station '{name}': it names a camera '{record.Camera}' the project does not have; it uses its recipe's own.");
            }

            if (record.Images is { } images)
            {
                var saving = new ImageSaving(
                    ParseEnum(images.Save, ImageSavePolicy.None),
                    ParseEnum(images.Format, ImageFileFormat.Png),
                    string.IsNullOrWhiteSpace(images.Folder) ? null : images.Folder,
                    images.KeepDays ?? 0,
                    images.Annotations ?? true,
                    images.MaxGb ?? 0,
                    images.LeaveFreeGb ?? 0,
                    images.SamplePercent ?? 0);

                if (saving.Problem() is { } problem)
                    warnings.Add($"Station '{name}': its image saving could not be used ({problem}); it saves nothing.");
                else
                    station.Saving = saving;
            }

            project._stations.Add(station);
        }

        foreach (var record in document.Controllers ?? [])
        {
            var name = record.Name?.Trim() ?? string.Empty;

            if (name.Length == 0 || string.IsNullOrWhiteSpace(record.Workflow))
            {
                warnings.Add("A controller with no name or no workflow was skipped.");
                continue;
            }

            if (project._controllers.Any(controller => Same(controller.Name, name)))
            {
                warnings.Add($"Controller '{name}' is listed twice; the second was skipped.");
                continue;
            }

            var controller = new Controller(
                name,
                string.IsNullOrWhiteSpace(record.Driver) ? "simulated" : record.Driver.Trim(),
                record.Address?.Trim() ?? string.Empty,
                record.Workflow.Replace('\\', '/'),
                record.Options is { Count: > 0 } options
                    ? new Dictionary<string, string>(options, StringComparer.OrdinalIgnoreCase)
                    : null);

            if (!project.IsInside(project.WorkflowPath(controller)))
            {
                warnings.Add($"Controller '{name}': its workflow '{record.Workflow}' is outside the project folder; skipped.");
                continue;
            }

            if (!File.Exists(project.WorkflowPath(controller)))
                warnings.Add($"Controller '{name}': its workflow {record.Workflow} is missing.");

            project._controllers.Add(controller);
        }

        foreach (var record in document.Users ?? [])
        {
            var name = record.Name?.Trim() ?? string.Empty;

            if (name.Length == 0)
            {
                warnings.Add("A user with no name was skipped.");
                continue;
            }

            if (project._users.Any(user => Same(user.Name, name)))
            {
                warnings.Add($"User '{name}' is listed twice; the second was skipped.");
                continue;
            }

            if (!Pins.IsKept(record.Pin))
            {
                warnings.Add($"User '{name}' has no PIN that can be checked, and was skipped: set one again in Builder.");
                continue;
            }

            if (!Enum.TryParse<UserLevel>(record.Level, ignoreCase: true, out var level) || !Enum.IsDefined(level))
            {
                warnings.Add($"User '{name}': the level '{record.Level}' is not one this build knows; taken as Operator.");
                level = UserLevel.Operator;
            }

            project._users.Add(new ProjectUser(name, level, record.Pin!));
        }

        if (document.LogOutMinutes is { } minutes)
            project._logOutMinutes = Math.Clamp(minutes, 0, MaxLogOutMinutes);

        return new ProjectLoadResult(project, warnings);
    }

    public void Save()
    {
        var document = new ProjectDocument
        {
            Version = _users.Count > 0 ? 2 : 1,
            Stations = _stations
                .Select(station => new StationRecord
                {
                    Name = station.Name,
                    Recipe = station.Recipe,
                    Camera = station.Camera,
                    Shots = station.ShotsPerPart > 1 ? station.ShotsPerPart : null,
                    Limits = station.Limits.Count == 0
                        ? null
                        : station.Limits.ToDictionary(
                            pair => pair.Key,
                            pair => (LimitRecord?)new LimitRecord { Lowest = pair.Value.Lowest, Highest = pair.Value.Highest }),
                    Images = station.Saving == ImageSaving.Off ? null : new ImagesRecord
                    {
                        Save = station.Saving.Policy.ToString().ToLowerInvariant(),
                        Format = station.Saving.Format.ToString().ToLowerInvariant(),
                        Folder = station.Saving.Folder,
                        KeepDays = station.Saving.KeepDays == 0 ? null : station.Saving.KeepDays,
                        Annotations = station.Saving.Annotations ? null : false,
                        MaxGb = station.Saving.MaxGigabytes == 0 ? null : station.Saving.MaxGigabytes,
                        LeaveFreeGb = station.Saving.LeaveFreeGigabytes == 0 ? null : station.Saving.LeaveFreeGigabytes,
                        SamplePercent = station.Saving.SamplePercent == 0 ? null : station.Saving.SamplePercent,
                    },
                })
                .ToList(),

            // Absent with none, so a project without cameras is written as before.
            Cameras = _cameras.Count == 0
                ? null
                : _cameras
                    .Select(camera => new CameraRecord
                    {
                        Name = camera.Name,
                        Device = camera.Setup.Device,
                        Exposure = camera.Setup.Exposure,
                        Gain = camera.Setup.Gain,
                        Trigger = camera.Setup.Trigger,
                        Timeout = camera.Setup.Timeout,
                    })
                    .ToList(),

            // Absent with none, so a project without a PLC is written as before.
            Controllers = _controllers.Count == 0
                ? null
                : _controllers
                    .Select(controller => new ControllerRecord
                    {
                        Name = controller.Name,
                        Driver = controller.Driver,
                        Address = controller.Address,
                        Options = controller.Options.Count == 0 ? null : new Dictionary<string, string>(controller.Options),
                        Workflow = controller.Workflow,
                    })
                    .ToList(),

            // Absent with none, so a project without user levels is written as before.
            Users = _users.Count == 0
                ? null
                : _users
                    .Select(user => new UserRecord { Name = user.Name, Level = user.Level.ToString(), Pin = user.Pin })
                    .ToList(),

            LogOutMinutes = _logOutMinutes == DefaultLogOutMinutes ? null : _logOutMinutes,
        };

        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options));
        File.Move(temporary, FilePath, overwrite: true);
    }

    // ---- stations -------------------------------------------------------

    /// <summary>The station's recipe file, in full.</summary>
    public string RecipePath(Station station)
    {
        ArgumentNullException.ThrowIfNull(station);

        return Path.GetFullPath(Path.Combine(Folder, station.Recipe.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>The station called <paramref name="name"/>, ignoring case, or null.</summary>
    public Station? Find(string name) => _stations.FirstOrDefault(station => Same(station.Name, name));

    /// <summary>Why <paramref name="name"/> cannot name a station, or null when it can.</summary>
    /// <param name="except">The station being renamed, whose own name is no clash.</param>
    public string? StationNameProblem(string? name, Station? except = null)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            return "A name is needed.";

        if (trimmed.Length > MaxNameLength)
            return $"A name can be at most {MaxNameLength} characters.";

        if (trimmed.Any(char.IsControl) || trimmed.Contains('›'))
            return "A name cannot contain '›' or control characters.";

        return _stations.Any(station => station != except && Same(station.Name, trimmed))
            ? $"There is already a station called '{trimmed}'."
            : null;
    }

    /// <summary>"Station 2", or the first "Station n" not taken.</summary>
    public string SuggestStationName(string stem = "Station")
    {
        for (var i = _stations.Count + 1; ; i++)
        {
            var candidate = $"{stem} {i}";

            if (Find(candidate) is null)
                return candidate;
        }
    }

    /// <summary>
    /// Adds a station whose recipe is <paramref name="recipe"/>, written into
    /// the project's recipes folder.
    /// </summary>
    public Station AddStation(string name, VisionGraph recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        if (StationNameProblem(name) is { } problem)
            throw new ArgumentException(problem, nameof(name));

        var station = new Station(name.Trim(), NewRecipeFile(name.Trim()));
        GraphSerializer.Save(recipe, RecipePath(station));

        return Added(station);
    }

    /// <summary>
    /// Adds a station running a copy of an existing recipe file, copied into
    /// the project so the folder stays complete.
    /// </summary>
    public Station ImportStation(string name, string recipeFile)
    {
        if (StationNameProblem(name) is { } problem)
            throw new ArgumentException(problem, nameof(name));

        if (!File.Exists(recipeFile))
            throw new FileNotFoundException($"No recipe at {recipeFile}.", recipeFile);

        var station = new Station(name.Trim(), NewRecipeFile(Path.GetFileNameWithoutExtension(recipeFile)));
        File.Copy(recipeFile, RecipePath(station));

        return Added(station);
    }

    public void RenameStation(Station station, string name)
    {
        ArgumentNullException.ThrowIfNull(station);

        if (StationNameProblem(name, station) is { } problem)
            throw new ArgumentException(problem, nameof(name));

        station.Name = name.Trim();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Takes a station out of the project. Its recipe file stays in the
    /// recipes folder: work is not deleted by tidying a list.
    /// </summary>
    /// <summary>
    /// Sets how far the line may take a station's published setting; both
    /// ends null takes the limit off. Written at once, as every change is.
    /// </summary>
    /// <returns>Whether anything changed.</returns>
    public bool SetLimit(Station station, string setting, double? lowest, double? highest)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);

        if (SettingLimit.Problem(lowest, highest) is { } problem)
            throw new ArgumentException(problem, nameof(lowest));

        var limit = new SettingLimit(lowest, highest);
        var current = station.LimitFor(setting) ?? new SettingLimit(null, null);

        if (current == limit)
            return false;

        station.SetLimit(setting.Trim(), limit);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Sets which pictures a station keeps when it runs. Written at once, as
    /// every change is.
    /// </summary>
    /// <returns>Whether anything changed.</returns>
    public bool SetSaving(Station station, ImageSaving saving)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentNullException.ThrowIfNull(saving);

        if (saving.Problem() is { } problem)
            throw new ArgumentException(problem, nameof(saving));

        if (station.Saving == saving)
            return false;

        station.Saving = saving;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void RemoveStation(Station station)
    {
        if (!_stations.Remove(station))
            return;

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves a station up or down the list, which is the order screens and the runtime show them in.</summary>
    public void MoveStation(Station station, int offset)
    {
        var from = _stations.IndexOf(station);
        var to = from + offset;

        if (from < 0 || to < 0 || to >= _stations.Count || to == from)
            return;

        _stations.RemoveAt(from);
        _stations.Insert(to, station);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- cameras ----------------------------------------------------------

    /// <summary>The camera called <paramref name="name"/>, ignoring case, or null.</summary>
    public CameraUnit? FindCamera(string? name) =>
        name is null ? null : _cameras.FirstOrDefault(camera => Same(camera.Name, name.Trim()));

    /// <summary>The camera <paramref name="station"/> takes its pictures from, or null for its recipe's own.</summary>
    public CameraUnit? CameraOf(Station station)
    {
        ArgumentNullException.ThrowIfNull(station);
        return FindCamera(station.Camera);
    }

    /// <summary>The stations taking their pictures from <paramref name="camera"/>.</summary>
    public IReadOnlyList<Station> StationsUsing(CameraUnit camera) =>
        _stations.Where(station => Same(station.Camera ?? string.Empty, camera.Name)).ToArray();

    /// <summary>Why <paramref name="name"/> cannot name a camera, or null when it can.</summary>
    /// <param name="except">The camera being renamed, whose own name is no clash.</param>
    public string? CameraNameProblem(string? name, CameraUnit? except = null)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            return "A name is needed.";

        if (trimmed.Length > MaxNameLength)
            return $"A name can be at most {MaxNameLength} characters.";

        if (trimmed.Any(char.IsControl) || trimmed.Contains('›'))
            return "A name cannot contain '›' or control characters.";

        return _cameras.Any(camera => camera != except && Same(camera.Name, trimmed))
            ? $"There is already a camera called '{trimmed}'."
            : null;
    }

    /// <summary>"Camera 1", or the first "Camera n" not taken.</summary>
    public string SuggestCameraName(string stem = "Camera")
    {
        for (var i = _cameras.Count + 1; ; i++)
        {
            var candidate = $"{stem} {i}";

            if (FindCamera(candidate) is null)
                return candidate;
        }
    }

    /// <summary>Adds a camera, its camera and settings chosen or not yet.</summary>
    public CameraUnit AddCamera(string name, CameraSetup? setup = null)
    {
        if (CameraNameProblem(name) is { } problem)
            throw new ArgumentException(problem, nameof(name));

        if ((setup ?? CameraSetup.None).Problem() is { } settings)
            throw new ArgumentException(settings, nameof(setup));

        var camera = new CameraUnit(name.Trim(), setup);
        _cameras.Add(camera);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return camera;
    }

    // ---- users (F2) --------------------------------------------------------

    /// <summary>The longest idle time before a log-out that may be set: a working day.</summary>
    public const int MaxLogOutMinutes = 600;

    /// <summary>The user called <paramref name="name"/>, ignoring case, or null.</summary>
    public ProjectUser? FindUser(string? name) =>
        name is null ? null : _users.FirstOrDefault(user => Same(user.Name, name.Trim()));

    /// <summary>Why <paramref name="name"/> cannot name a user, or null when it can.</summary>
    public string? UserNameProblem(string? name, ProjectUser? except = null)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            return "A name is needed.";

        if (trimmed.Length > MaxNameLength)
            return $"A name can be at most {MaxNameLength} characters.";

        if (trimmed.Any(char.IsControl))
            return "A name cannot contain control characters.";

        return _users.Any(user => user != except && Same(user.Name, trimmed))
            ? $"There is already a user called '{trimmed}'."
            : null;
    }

    /// <summary>Adds someone who may log in on the line.</summary>
    /// <exception cref="ArgumentException">The name or the PIN cannot be used.</exception>
    public ProjectUser AddUser(string name, UserLevel level, string pin)
    {
        if (UserNameProblem(name) is { } problem)
            throw new ArgumentException(problem, nameof(name));

        var user = new ProjectUser(name.Trim(), level, Pins.Hash(pin));
        _users.Add(user);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return user;
    }

    public void RenameUser(ProjectUser user, string name)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (UserNameProblem(name, user) is { } problem)
            throw new ArgumentException(problem, nameof(name));

        user.Name = name.Trim();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetUserLevel(ProjectUser user, UserLevel level)
    {
        ArgumentNullException.ThrowIfNull(user);

        user.Level = level;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <exception cref="ArgumentException">The PIN cannot be used.</exception>
    public void SetUserPin(ProjectUser user, string pin)
    {
        ArgumentNullException.ThrowIfNull(user);

        user.Pin = Pins.Hash(pin);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveUser(ProjectUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (_users.Remove(user))
        {
            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Sets how long a user stays logged in, idle; 0 for never.</summary>
    public void SetLogOutMinutes(int minutes)
    {
        _logOutMinutes = Math.Clamp(minutes, 0, MaxLogOutMinutes);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The user <paramref name="name"/> with <paramref name="pin"/>, or null
    /// when there is no such user or the PIN is not theirs.
    /// </summary>
    public ProjectUser? LogIn(string? name, string? pin) =>
        FindUser(name) is { } user && user.Checks(pin) ? user : null;

    /// <summary>Renames a camera; the stations taking their pictures from it follow.</summary>
    public void RenameCamera(CameraUnit camera, string name)
    {
        ArgumentNullException.ThrowIfNull(camera);

        if (CameraNameProblem(name, camera) is { } problem)
            throw new ArgumentException(problem, nameof(name));

        foreach (var station in StationsUsing(camera))
            station.Camera = name.Trim();

        camera.Name = name.Trim();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Changes which camera a unit is, and how it is set. Written at once.</summary>
    /// <returns>Whether anything changed.</returns>
    public bool SetCameraSetup(CameraUnit camera, CameraSetup setup)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(setup);

        if (setup.Problem() is { } problem)
            throw new ArgumentException(problem, nameof(setup));

        setup = setup with { Device = setup.Device.Trim() };

        if (camera.Setup == setup)
            return false;

        camera.Setup = setup;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Has <paramref name="station"/> take its pictures from
    /// <paramref name="camera"/>, or - null - from its recipe's own Camera
    /// node. Written at once.
    /// </summary>
    /// <returns>Whether anything changed.</returns>
    /// <summary>How many pictures make a part at a station (F26), 1 to <see cref="Station.MostShots"/>.</summary>
    /// <returns>Whether it changed.</returns>
    public bool SetShotsPerPart(Station station, int shots)
    {
        ArgumentNullException.ThrowIfNull(station);

        if (shots < 1 || shots > Station.MostShots)
            throw new ArgumentOutOfRangeException(nameof(shots), shots, $"A part takes 1 to {Station.MostShots} pictures.");

        if (station.ShotsPerPart == shots)
            return false;

        station.ShotsPerPart = shots;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool SetStationCamera(Station station, CameraUnit? camera)
    {
        ArgumentNullException.ThrowIfNull(station);

        if (camera is not null && !_cameras.Contains(camera))
            throw new ArgumentException($"'{camera.Name}' is not one of this project's cameras.", nameof(camera));

        if (Same(station.Camera ?? string.Empty, camera?.Name ?? string.Empty))
            return false;

        station.Camera = camera?.Name;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Takes a camera out of the project. The stations using it go back to
    /// their recipes' own Camera nodes.
    /// </summary>
    public void RemoveCamera(CameraUnit camera)
    {
        var users = StationsUsing(camera);

        if (!_cameras.Remove(camera))
            return;

        foreach (var station in users)
            station.Camera = null;

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- controllers ------------------------------------------------------

    /// <summary>The controller's workflow file, in full.</summary>
    public string WorkflowPath(Controller controller)
    {
        ArgumentNullException.ThrowIfNull(controller);

        return Path.GetFullPath(Path.Combine(Folder, controller.Workflow.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>The controller called <paramref name="name"/>, ignoring case, or null.</summary>
    public Controller? FindController(string name) => _controllers.FirstOrDefault(controller => Same(controller.Name, name));

    /// <summary>Why <paramref name="name"/> cannot name a controller, or null when it can.</summary>
    /// <param name="except">The controller being renamed, whose own name is no clash.</param>
    public string? ControllerNameProblem(string? name, Controller? except = null)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            return "A name is needed.";

        if (trimmed.Length > MaxNameLength)
            return $"A name can be at most {MaxNameLength} characters.";

        if (trimmed.Any(char.IsControl) || trimmed.Contains('›'))
            return "A name cannot contain '›' or control characters.";

        return _controllers.Any(controller => controller != except && Same(controller.Name, trimmed))
            ? $"There is already a controller called '{trimmed}'."
            : null;
    }

    /// <summary>"PLC 1", or the first "PLC n" not taken.</summary>
    public string SuggestControllerName(string stem = "PLC")
    {
        for (var i = _controllers.Count + 1; ; i++)
        {
            var candidate = $"{stem} {i}";

            if (FindController(candidate) is null)
                return candidate;
        }
    }

    /// <summary>
    /// Adds a controller, its workflow written by <paramref name="writeWorkflow"/>
    /// into the project's controllers folder first - the core does not know
    /// the workflow format; <c>Sirius.Plc</c> does.
    /// </summary>
    /// <param name="writeWorkflow">Writes the new workflow to the full path it is given.</param>
    public Controller AddController(string name, string driver, string address, Action<string> writeWorkflow)
    {
        ArgumentNullException.ThrowIfNull(writeWorkflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(driver);

        if (ControllerNameProblem(name) is { } problem)
            throw new ArgumentException(problem, nameof(name));

        var controller = new Controller(name.Trim(), driver.Trim(), address?.Trim() ?? string.Empty, NewWorkflowFile(name.Trim()));
        writeWorkflow(WorkflowPath(controller));

        _controllers.Add(controller);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return controller;
    }

    /// <summary>Renames a controller. Its workflow keeps its file.</summary>
    public void RenameController(Controller controller, string name)
    {
        ArgumentNullException.ThrowIfNull(controller);

        if (ControllerNameProblem(name, controller) is { } problem)
            throw new ArgumentException(problem, nameof(name));

        controller.Name = name.Trim();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Changes which driver talks to a controller's PLC, and where it is. Written at once.</summary>
    /// <returns>Whether anything changed.</returns>
    public bool SetConnection(Controller controller, string driver, string address, IReadOnlyDictionary<string, string>? options = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentException.ThrowIfNullOrWhiteSpace(driver);

        var given = options ?? controller.Options;
        address = address?.Trim() ?? string.Empty;

        if (Same(controller.Driver, driver.Trim()) && controller.Address == address &&
            given.Count == controller.Options.Count && given.All(pair => controller.Options.GetValueOrDefault(pair.Key) == pair.Value))
        {
            return false;
        }

        controller.Driver = driver.Trim();
        controller.Address = address;
        controller.Options = new Dictionary<string, string>(given, StringComparer.OrdinalIgnoreCase);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Takes a controller out of the project. Its workflow file stays in the
    /// controllers folder, as a removed station's recipe does.
    /// </summary>
    public void RemoveController(Controller controller)
    {
        if (!_controllers.Remove(controller))
            return;

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"controllers/Line 1 PLC.sflow", or "Line 1 PLC (2).sflow" if that file exists.</summary>
    private string NewWorkflowFile(string stem)
    {
        var safe = new string(stem.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();

        if (safe.Length == 0)
            safe = "controller";

        var folder = Path.Combine(Folder, ControllersFolder);
        Directory.CreateDirectory(folder);

        var file = safe + WorkflowExtension;

        for (var i = 2; File.Exists(Path.Combine(folder, file)); i++)
            file = $"{safe} ({i}){WorkflowExtension}";

        return $"{ControllersFolder}/{file}";
    }

    private Station Added(Station station)
    {
        _stations.Add(station);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return station;
    }

    /// <summary>"recipes/Camera 1.sgraph", or "Camera 1 (2).sgraph" if that file exists.</summary>
    private string NewRecipeFile(string stem)
    {
        var safe = new string(stem.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();

        if (safe.Length == 0)
            safe = "recipe";

        var folder = Path.Combine(Folder, RecipesFolder);
        Directory.CreateDirectory(folder);

        var file = safe + ".sgraph";

        for (var i = 2; File.Exists(Path.Combine(folder, file)); i++)
            file = $"{safe} ({i}).sgraph";

        return $"{RecipesFolder}/{file}";
    }

    private bool IsInside(string path)
    {
        var folder = Folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Why <paramref name="name"/> cannot name a project folder, or null.</summary>
    public static string? FileNameProblem(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            return "A name is needed.";

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "A project name cannot contain \\ / : * ? \" < > |.";

        return trimmed.EndsWith('.') ? "A project name cannot end with a full stop." : null;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static T ParseEnum<T>(string? text, T fallback)
        where T : struct, Enum =>
        Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(value) ? value : fallback;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class ProjectDocument
    {
        /// <summary>2 with users (F2): a build that does not know them refuses the project rather than run it unlocked.</summary>
        public const int CurrentVersion = 2;

        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("stations")]
        public List<StationRecord> Stations { get; set; } = [];

        [JsonPropertyName("cameras")]
        public List<CameraRecord>? Cameras { get; set; }

        [JsonPropertyName("controllers")]
        public List<ControllerRecord>? Controllers { get; set; }

        [JsonPropertyName("users")]
        public List<UserRecord>? Users { get; set; }

        [JsonPropertyName("logOutMinutes")]
        public int? LogOutMinutes { get; set; }
    }

    private sealed class UserRecord
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("level")]
        public string? Level { get; set; }

        /// <summary>Salted and hashed: "pbkdf2$iterations$salt$hash".</summary>
        [JsonPropertyName("pin")]
        public string? Pin { get; set; }
    }

    private sealed class CameraRecord
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        /// <summary>"Basler acA1920-40gm [23456789]": found by the serial number.</summary>
        [JsonPropertyName("device")]
        public string? Device { get; set; }

        /// <summary>Each absent for the recipe's own.</summary>
        [JsonPropertyName("exposure")]
        public double? Exposure { get; set; }

        [JsonPropertyName("gain")]
        public double? Gain { get; set; }

        [JsonPropertyName("trigger")]
        public string? Trigger { get; set; }

        [JsonPropertyName("timeout")]
        public int? Timeout { get; set; }
    }

    private sealed class ControllerRecord
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        /// <summary>The driver's key: simulated, siemens-s7.</summary>
        [JsonPropertyName("driver")]
        public string? Driver { get; set; }

        [JsonPropertyName("address")]
        public string? Address { get; set; }

        [JsonPropertyName("options")]
        public Dictionary<string, string>? Options { get; set; }

        [JsonPropertyName("workflow")]
        public string? Workflow { get; set; }
    }

    private sealed class StationRecord
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("recipe")]
        public string? Recipe { get; set; }

        /// <summary>The project camera it takes its pictures from; absent for the recipe's own.</summary>
        [JsonPropertyName("camera")]
        public string? Camera { get; set; }

        /// <summary>Pictures a part takes (F26); absent for one.</summary>
        [JsonPropertyName("shots")]
        public int? Shots { get; set; }

        /// <summary>Published setting name to its limits on the line.</summary>
        [JsonPropertyName("limits")]
        public Dictionary<string, LimitRecord?>? Limits { get; set; }

        /// <summary>Which pictures it keeps; absent for none.</summary>
        [JsonPropertyName("images")]
        public ImagesRecord? Images { get; set; }
    }

    private sealed class ImagesRecord
    {
        /// <summary>none, failed or all.</summary>
        [JsonPropertyName("save")]
        public string? Save { get; set; }

        /// <summary>tiff, png, jpeg or bmp; png when not said - what a file from before TIF meant.</summary>
        [JsonPropertyName("format")]
        public string? Format { get; set; }

        [JsonPropertyName("folder")]
        public string? Folder { get; set; }

        [JsonPropertyName("keepDays")]
        public int? KeepDays { get; set; }

        [JsonPropertyName("annotations")]
        public bool? Annotations { get; set; }

        /// <summary>The most the station's pictures may take, in GB; absent for no limit.</summary>
        [JsonPropertyName("maxGB")]
        public double? MaxGb { get; set; }

        /// <summary>Free space left on the drive, in GB; absent for none.</summary>
        [JsonPropertyName("leaveFreeGB")]
        public double? LeaveFreeGb { get; set; }

        /// <summary>The share of pictures deleted kept in the Kept folder; absent for none.</summary>
        [JsonPropertyName("samplePercent")]
        public double? SamplePercent { get; set; }
    }

    private sealed class LimitRecord
    {
        [JsonPropertyName("lowest")]
        public double? Lowest { get; set; }

        [JsonPropertyName("highest")]
        public double? Highest { get; set; }
    }
}
