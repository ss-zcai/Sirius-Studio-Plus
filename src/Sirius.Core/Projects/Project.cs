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

    /// <summary>Raised after stations or controllers are added, removed, renamed, reordered or reconnected.</summary>
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

            if (record.Images is { } images)
            {
                var saving = new ImageSaving(
                    ParseEnum(images.Save, ImageSavePolicy.None),
                    ParseEnum(images.Format, ImageFileFormat.Png),
                    string.IsNullOrWhiteSpace(images.Folder) ? null : images.Folder,
                    images.KeepDays ?? 0,
                    images.Annotations ?? true);

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

        return new ProjectLoadResult(project, warnings);
    }

    public void Save()
    {
        var document = new ProjectDocument
        {
            Stations = _stations
                .Select(station => new StationRecord
                {
                    Name = station.Name,
                    Recipe = station.Recipe,
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
                    },
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
        public const int CurrentVersion = 1;

        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("stations")]
        public List<StationRecord> Stations { get; set; } = [];

        [JsonPropertyName("controllers")]
        public List<ControllerRecord>? Controllers { get; set; }
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

        /// <summary>png, jpeg or bmp.</summary>
        [JsonPropertyName("format")]
        public string? Format { get; set; }

        [JsonPropertyName("folder")]
        public string? Folder { get; set; }

        [JsonPropertyName("keepDays")]
        public int? KeepDays { get; set; }

        [JsonPropertyName("annotations")]
        public bool? Annotations { get; set; }
    }

    private sealed class LimitRecord
    {
        [JsonPropertyName("lowest")]
        public double? Lowest { get; set; }

        [JsonPropertyName("highest")]
        public double? Highest { get; set; }
    }
}
