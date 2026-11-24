using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emgu.CV;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Core.Serialization;
using Sirius.UI.Imaging;
using Sirius.Vision;
using Sirius.Vision.Cameras;

namespace Sirius.Builder;

/// <summary>A trigger as the camera window offers it: "As the recipe", "Software"…</summary>
public sealed record TriggerChoice(string Label, string Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// A project camera's window: find the cameras the drivers can see, choose
/// one, set it, and look through it - live, or a frame at a time, saved for
/// the vision engineer (backlog F29, runtime design section 15).
/// </summary>
/// <remarks>
/// <para>
/// Every change is written to the project at once, as Builder's other
/// changes are. A setting left "as the recipe" shows - and the live view
/// uses - the value in the recipe of the first station using the camera.
/// </para>
/// <para>
/// The camera is let go when the window closes, so Sirius Edge or Studio
/// on the same PC can open it: a GigE camera is controlled by one program
/// at a time.
/// </para>
/// </remarks>
public sealed partial class CameraViewModel : ObservableObject, IDisposable
{
    /// <summary>What a Camera node starts with: used where neither the unit nor any recipe says.</summary>
    private static readonly CameraSetup NodeDefaults = new(string.Empty, 10_000, 0, "Software", 2000);

    private readonly Project _project;
    private readonly Stopwatch _clock = new();
    private CancellationTokenSource? _live;

    /// <summary>The grab under way off the window's thread, if any - waited for before the camera is let go.</summary>
    private Task _inFlight = Task.CompletedTask;
    private ICamera? _open;
    private ImageData? _last;
    private int _frames;

    [ObservableProperty]
    private BitmapSource? _picture;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private CameraInfo? _selectedFound;

    [ObservableProperty]
    private string _findText = "Find lists every camera the drivers here can see.";

    [ObservableProperty]
    private string _viewText = "Grab takes one frame; Live keeps taking them.";

    [ObservableProperty]
    private string? _problem;

    [ObservableProperty]
    private bool _isGrabbing;

    public CameraViewModel(Project project, CameraUnit camera)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        Camera = camera ?? throw new ArgumentNullException(nameof(camera));

        Recipe = RecipeSetup(project, camera, out var recipeStation);
        RecipeStation = recipeStation;
        _note = string.Empty;

        FindCommand = new AsyncRelayCommand(FindAsync, () => !IsSearching);
        UseCommand = new RelayCommand(() => Use(SelectedFound), () => SelectedFound is not null);
        GrabCommand = new AsyncRelayCommand(GrabAsync, () => Camera.Setup.HasDevice && !IsLive && !IsGrabbing);
        LiveCommand = new RelayCommand(ToggleLive, () => Camera.Setup.HasDevice);
        SaveImageCommand = new RelayCommand(() => SaveImage(), () => _last is not null);

        TriggerChoices =
        [
            new TriggerChoice(RecipeStation is null ? "As the recipe" : $"As the recipe ({TriggerLabel(Recipe.Trigger)})", string.Empty),
            new TriggerChoice("Free Run", "FreeRun"),
            new TriggerChoice("Software", "Software"),
            new TriggerChoice("Hardware", "Hardware"),
        ];
    }

    public CameraUnit Camera { get; }

    public string Title => $"Camera - {Camera.Name}";

    /// <summary>The settings "as the recipe" means: the first station using it's recipe, or a new Camera node's.</summary>
    public CameraSetup Recipe { get; private set; }

    /// <summary>The station whose recipe <see cref="Recipe"/> is from, or null.</summary>
    public string? RecipeStation { get; private set; }

    // ---- Studio and the line agreeing (F29) -------------------------------------------

    private string _note;

    /// <summary>What Take from Recipe or Give to Recipe did, said under the buttons' row as Problem is.</summary>
    public string Note
    {
        get => _note;
        private set => SetProperty(ref _note, value);
    }

    /// <summary>
    /// The recipe's Camera node's settings into this camera, as its own:
    /// what the engineer tuned in Studio, now the line's. The camera
    /// chosen stays, unless none is; then the recipe's.
    /// </summary>
    /// <returns>Whether there was a recipe to take from.</returns>
    internal bool TakeFromRecipe()
    {
        Recipe = RecipeSetup(_project, Camera, out var station);
        RecipeStation = station;

        if (station is null)
        {
            Refused(_project.StationsUsing(Camera).Count == 0
                ? "No station takes its pictures from this camera, so there is no recipe to take from."
                : "The recipe has no Camera node for this camera to take from.");
            return false;
        }

        var setup = Camera.Setup with
        {
            Device = Camera.Setup.HasDevice ? Camera.Setup.Device : Recipe.Device,
            Exposure = Recipe.Exposure,
            Gain = Recipe.Gain,
            Trigger = Recipe.Trigger,
            Timeout = Recipe.Timeout,
        };

        if (!Update(setup) && Problem is not null)
            return false;

        Note = $"Taken from {station}'s recipe: exposure {Recipe.Exposure} µs, gain {Recipe.Gain} dB, trigger {TriggerLabel(Recipe.Trigger)}, timeout {Recipe.Timeout} ms - this camera's own now.";
        return true;
    }

    /// <summary>The recipes this camera's stations run, each once.</summary>
    private IReadOnlyList<(string Path, IReadOnlyList<string> Stations)> Recipes() =>
        _project.StationsUsing(Camera)
            .GroupBy(station => Path.GetFullPath(_project.RecipePath(station)), StringComparer.OrdinalIgnoreCase)
            .Select(group => (group.Key, (IReadOnlyList<string>)group.Select(station => station.Name).ToArray()))
            .ToArray();

    /// <summary>What Give to Recipe will write, and where, to ask before; null - said - when there is nothing to give.</summary>
    internal string? GiveQuestion()
    {
        var setup = Camera.Setup;
        var given = new List<string>();

        if (setup.Exposure is { } exposure)
            given.Add($"exposure {exposure} µs");

        if (setup.Gain is { } gain)
            given.Add($"gain {gain} dB");

        if (setup.Trigger is { } trigger)
            given.Add($"trigger {TriggerLabel(trigger)}");

        if (setup.Timeout is { } timeout)
            given.Add($"timeout {timeout} ms");

        var recipes = Recipes();

        if (recipes.Count == 0)
        {
            Refused("No station takes its pictures from this camera, so there is no recipe to give to.");
            return null;
        }

        if (given.Count == 0)
        {
            Refused("Every setting is \"As the recipe\": there is nothing of this camera's own to give.");
            return null;
        }

        var files = string.Join(", ", recipes.Select(recipe => $"{Path.GetFileName(recipe.Path)} ({string.Join(", ", recipe.Stations)})"));

        return $"Write {string.Join(", ", given)} into the Camera node of {files}?\n\n" +
               "The camera itself is left as the recipe has it: at the desk it may be another. " +
               "If the recipe is open in Studio with changes not saved, save or close it there first.";
    }

    /// <summary>
    /// This camera's own settings into each recipe's Camera node, saved: so
    /// Studio at the desk grabs as the line does.
    /// </summary>
    /// <returns>Whether every recipe took them.</returns>
    internal bool GiveToRecipe()
    {
        var problems = new List<string>();
        var written = new List<string>();

        foreach (var (path, _) in Recipes())
        {
            var name = Path.GetFileName(path);

            try
            {
                var loaded = GraphSerializer.Load(path);

                // A recipe that lost something on loading would lose it for good on saving.
                if (loaded.HasWarnings)
                {
                    problems.Add($"{name} loads with warnings - open it in Studio and save it first");
                    continue;
                }

                if (StationCamera.GiveTo(loaded.Graph, Camera, out var problem) is not { } given)
                {
                    problems.Add($"{name}: {problem}");
                    continue;
                }

                if (given.Count > 0)
                {
                    GraphSerializer.Save(loaded.Graph, path);
                    written.Add(name);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                problems.Add($"{name}: {ex.Message}");
            }
        }

        Recipe = RecipeSetup(_project, Camera, out var station);
        RecipeStation = station;
        OnPropertyChanged(string.Empty);

        Note = written.Count == 0
            ? "The recipe has these settings already."
            : $"Given to {string.Join(", ", written)}.";

        Problem = problems.Count == 0 ? null : string.Join("; ", problems) + ".";
        return problems.Count == 0;
    }

    /// <summary>"Used by Top, Bottom", or that no station uses it yet.</summary>
    public string UsedByText
    {
        get
        {
            var stations = _project.StationsUsing(Camera);

            return stations.Count == 0
                ? "No station takes its pictures from this camera yet: right-click a station ▸ Camera."
                : $"Used by {string.Join(", ", stations.Select(station => station.Name))}." +
                  (RecipeStation is null ? string.Empty : $" \"As the recipe\" is {RecipeStation}'s recipe's.");
        }
    }

    // ---- which camera ---------------------------------------------------------------

    /// <summary>The cameras found, the last time Find was pressed.</summary>
    public ObservableCollection<CameraInfo> Found { get; } = [];

    /// <summary>The camera chosen, as a Camera node names it - or that none is.</summary>
    public string DeviceText => Camera.Setup.HasDevice ? Camera.Setup.Device : "None chosen: Find, choose one, then Use.";

    public bool HasDevice => Camera.Setup.HasDevice;

    public IAsyncRelayCommand FindCommand { get; }

    public IRelayCommand UseCommand { get; }

    /// <summary>Lists every camera every driver can see - off this thread, since GigE cameras take a moment to answer.</summary>
    internal async Task FindAsync()
    {
        IsSearching = true;
        FindText = "Looking for cameras…";

        try
        {
            var found = await Task.Run(CameraDrivers.Enumerate).ConfigureAwait(true);

            Found.Clear();

            foreach (var camera in found)
                Found.Add(camera);

            SelectedFound = Found.FirstOrDefault(camera => IsChosen(camera)) ?? Found.FirstOrDefault();

            var unavailable = CameraDrivers.LoadProblems;
            var note = unavailable.Count == 0 ? string.Empty : " Not listed: " + string.Join(" ", unavailable);

            FindText = found.Count switch
            {
                0 => "No camera found. Check it is powered and connected." + note,
                1 => "One camera found." + note,
                _ => $"{found.Count} cameras found." + note,
            };

            if (Camera.Setup.HasDevice && !found.Any(IsChosen))
                FindText += $" The chosen camera, {Camera.Setup.Device}, is not among them.";
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>Whether <paramref name="camera"/> is the one chosen, by its serial number.</summary>
    public bool IsChosen(CameraInfo camera) =>
        string.Equals(CameraInfo.SerialIn(Camera.Setup.Device), camera.Serial, StringComparison.OrdinalIgnoreCase);

    /// <summary>Makes <paramref name="camera"/> this unit's camera.</summary>
    internal void Use(CameraInfo? camera)
    {
        if (camera is null)
            return;

        StopLive();
        LetGo();

        if (Update(Camera.Setup with { Device = camera.Label }))
            ViewText = $"Now {camera.Label}. Grab to see through it.";
    }

    // ---- its settings ------------------------------------------------------------------

    /// <summary>Exposure from the recipe rather than this unit.</summary>
    public bool ExposureAsRecipe
    {
        get => Camera.Setup.Exposure is null;
        set => Update(Camera.Setup with { Exposure = value ? null : EffectiveExposure });
    }

    /// <summary>The exposure in use, in microseconds - typed, it becomes the unit's own.</summary>
    public string ExposureText
    {
        get => EffectiveExposure.ToString("0.##", CultureInfo.CurrentCulture);
        set
        {
            if (Number(value) is { } exposure)
                Update(Camera.Setup with { Exposure = exposure });
            else
                Refused($"'{value}' is not a number of microseconds.");
        }
    }

    public bool GainAsRecipe
    {
        get => Camera.Setup.Gain is null;
        set => Update(Camera.Setup with { Gain = value ? null : EffectiveGain });
    }

    /// <summary>The gain in use, in decibels.</summary>
    public string GainText
    {
        get => EffectiveGain.ToString("0.##", CultureInfo.CurrentCulture);
        set
        {
            if (Number(value) is { } gain)
                Update(Camera.Setup with { Gain = gain });
            else
                Refused($"'{value}' is not a number of decibels.");
        }
    }

    public IReadOnlyList<TriggerChoice> TriggerChoices { get; }

    /// <summary>The trigger chosen, by value: empty for the recipe's.</summary>
    public string TriggerValue
    {
        get => Camera.Setup.Trigger ?? string.Empty;
        set => Update(Camera.Setup with { Trigger = string.IsNullOrEmpty(value) ? null : value });
    }

    public bool TimeoutAsRecipe
    {
        get => Camera.Setup.Timeout is null;
        set => Update(Camera.Setup with { Timeout = value ? null : EffectiveTimeout });
    }

    /// <summary>How long to wait for a frame, in milliseconds.</summary>
    public string TimeoutText
    {
        get => EffectiveTimeout.ToString(CultureInfo.CurrentCulture);
        set
        {
            if (Number(value) is { } timeout && timeout == Math.Floor(timeout))
                Update(Camera.Setup with { Timeout = (int)timeout });
            else
                Refused($"'{value}' is not a whole number of milliseconds.");
        }
    }

    public double EffectiveExposure => Camera.Setup.Exposure ?? Recipe.Exposure ?? NodeDefaults.Exposure!.Value;

    public double EffectiveGain => Camera.Setup.Gain ?? Recipe.Gain ?? NodeDefaults.Gain!.Value;

    public string EffectiveTrigger => Camera.Setup.Trigger ?? Recipe.Trigger ?? NodeDefaults.Trigger!;

    public int EffectiveTimeout => Camera.Setup.Timeout ?? Recipe.Timeout ?? NodeDefaults.Timeout!.Value;

    /// <summary>Writes the unit's new setup to the project; a setup refused is said and left as it was.</summary>
    private bool Update(CameraSetup setup)
    {
        bool changed;

        try
        {
            changed = _project.SetCameraSetup(Camera, setup);
            Problem = null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Refused(ex.Message);
            return false;
        }

        OnPropertyChanged(string.Empty);
        GrabCommand.NotifyCanExecuteChanged();
        LiveCommand.NotifyCanExecuteChanged();
        return changed;
    }

    private void Refused(string problem)
    {
        Problem = problem;

        // The boxes read back what is kept.
        OnPropertyChanged(nameof(ExposureText));
        OnPropertyChanged(nameof(GainText));
        OnPropertyChanged(nameof(TimeoutText));
    }

    // ---- looking through it ----------------------------------------------------------

    public IAsyncRelayCommand GrabCommand { get; }

    /// <summary>Starts taking frames one after another, or stops.</summary>
    public IRelayCommand LiveCommand { get; }

    public IRelayCommand SaveImageCommand { get; }

    public bool IsLive => _live is not null;

    /// <summary>The last frame's size, and the rate while live: "1920 × 1200 · 24.8 frames a second".</summary>
    [ObservableProperty]
    private string _frameText = string.Empty;

    internal async Task GrabAsync()
    {
        IsGrabbing = true;

        try
        {
            await GrabOnceAsync(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            IsGrabbing = false;
            GrabCommand.NotifyCanExecuteChanged();
        }
    }

    private void ToggleLive()
    {
        if (IsLive)
        {
            StopLive();
            return;
        }

        _live = new CancellationTokenSource();
        _frames = 0;
        _clock.Restart();
        OnPropertyChanged(nameof(IsLive));
        GrabCommand.NotifyCanExecuteChanged();
        _ = LiveAsync(_live.Token);
    }

    private async Task LiveAsync(CancellationToken token)
    {
        var lostRunning = 0;

        while (!token.IsCancellationRequested)
        {
            // A timeout - a hardware trigger not yet come - is waited out
            // again. A frame lost on the network is said and gone past - the
            // next is usually whole - unless frames go on being lost; anything
            // else stops.
            switch (await GrabOnceAsync(token).ConfigureAwait(true))
            {
                case GrabResult.Lost when ++lostRunning < LiveLostLimit:
                    continue;

                case GrabResult.Lost or GrabResult.Failed:
                    StopLive();
                    return;

                default:
                    lostRunning = 0;
                    break;
            }
        }
    }

    /// <summary>Frames lost running - each already tried again by the driver - before Live gives up.</summary>
    private const int LiveLostLimit = 10;

    /// <summary>Stops taking frames; the last stays on view.</summary>
    internal void StopLive()
    {
        if (_live is not { } live)
            return;

        live.Cancel();
        _live = null;
        live.Dispose();
        OnPropertyChanged(nameof(IsLive));
        GrabCommand.NotifyCanExecuteChanged();
    }

    private enum GrabResult
    {
        Taken,
        TimedOut,
        Lost,
        Failed,
        Cancelled,
    }

    private async Task<GrabResult> GrabOnceAsync(CancellationToken token)
    {
        var device = Camera.Setup.Device;
        var exposure = EffectiveExposure;
        var gain = EffectiveGain;
        var trigger = Enum.Parse<CameraTrigger>(EffectiveTrigger, ignoreCase: true);
        var timeout = EffectiveTimeout;

        try
        {
            var grab = Task.Run(() => Take(device, exposure, gain, trigger, timeout, token), token);
            _inFlight = grab;
            var frame = await grab.ConfigureAwait(true);

            // Closed meanwhile: the frame is not wanted.
            if (_disposed)
            {
                frame.Dispose();
                return GrabResult.Cancelled;
            }

            _last?.Dispose();
            _last = frame;
            Picture = MatImageConverter.ToBitmapSource(frame);
            SaveImageCommand.NotifyCanExecuteChanged();
            Problem = null;

            _frames++;
            var rate = IsLive && _clock.Elapsed.TotalSeconds > 0.5
                ? $" · {_frames / _clock.Elapsed.TotalSeconds:0.0} frames a second"
                : string.Empty;

            FrameText = $"{frame.Width} × {frame.Height}{rate}";
            ViewText = IsLive ? "Live. Live again stops it." : $"Taken {DateTime.Now:HH:mm:ss}.";
            return GrabResult.Taken;
        }
        catch (OperationCanceledException)
        {
            return GrabResult.Cancelled;
        }
        catch (TimeoutException)
        {
            ViewText = trigger == CameraTrigger.Hardware
                ? $"No trigger arrived within {timeout} ms: waiting for a pulse on the camera's trigger input."
                : $"No frame arrived within {timeout} ms.";
            return GrabResult.TimedOut;
        }
        catch (FrameLostException ex)
        {
            Problem = ex.Message;
            ViewText = IsLive ? "A frame was lost; Live goes on." : "Not taken.";
            return GrabResult.Lost;
        }
        catch (Exception ex)
        {
            Problem = ExceptionText(ex);
            ViewText = "Not taken.";
            return GrabResult.Failed;
        }
    }

    /// <summary>One frame, off the window's thread.</summary>
    private ImageData Take(string device, double exposure, double gain, CameraTrigger trigger, int timeout, CancellationToken token)
    {
        var camera = CameraDrivers.Acquire(device)
            ?? throw new InvalidOperationException($"{device} was not found. Check it is powered and connected, then Find.");

        _open = camera;

        try
        {
            camera.Trigger = trigger;
            camera.Exposure = exposure;
            camera.Gain = gain;
            return camera.Grab(TimeSpan.FromMilliseconds(timeout), token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TimeoutException and not FrameLostException)
        {
            // Unplugged, reset: let it go, so the next grab opens it afresh.
            // A frame lost on the network is neither: the camera is kept.
            CameraDrivers.Release(camera);
            _open = null;
            throw;
        }
    }

    private static string ExceptionText(Exception ex) => Sirius.Core.Graph.ExceptionText.Describe(ex);

    /// <summary>Saves the last frame, as it came from the camera, for the vision engineer.</summary>
    /// <param name="path">Where; none asks, starting in the project's images\samples folder.</param>
    /// <returns>The file written, or null.</returns>
    internal string? SaveImage(string? path = null)
    {
        if (_last is not { } frame)
            return null;

        if (path is null)
        {
            var folder = Path.Combine(_project.Folder, "images", "samples");
            Directory.CreateDirectory(folder);

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Image",
                InitialDirectory = folder,
                FileName = $"{Safe(Camera.Name)} {DateTime.Now:yyyyMMdd-HHmmss}.png",
                Filter = "PNG (*.png)|*.png|BMP (*.bmp)|*.bmp|TIFF (*.tif)|*.tif",
                DefaultExt = ".png",
            };

            if (dialog.ShowDialog() != true)
                return null;

            path = dialog.FileName;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

            if (!CvInvoke.Imwrite(path, frame.Mat))
                throw new IOException("the picture could not be encoded.");

            ViewText = $"Saved {Path.GetFileName(path)}.";
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Emgu.CV.Util.CvException)
        {
            Problem = $"Could not save {Path.GetFileName(path)}: {ex.Message}";
            return null;
        }
    }

    private static string Safe(string name) =>
        new(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

    // ---- the recipe's settings -------------------------------------------------------

    /// <summary>The settings in the recipe of the first station using the camera, or a new Camera node's.</summary>
    private static CameraSetup RecipeSetup(Project project, CameraUnit camera, out string? station)
    {
        foreach (var user in project.StationsUsing(camera))
        {
            try
            {
                if (StationCamera.From(GraphSerializer.Load(project.RecipePath(user)).Graph) is { } setup)
                {
                    station = user.Name;
                    return setup;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                // The next station's, or the defaults.
            }
        }

        station = null;
        return NodeDefaults;
    }

    private static string TriggerLabel(string? trigger) => trigger switch
    {
        "FreeRun" => "Free Run",
        null => "Software",
        _ => trigger,
    };

    private static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) && double.IsFinite(number) ? number : null;

    /// <summary>Lets go of the camera, so another program on this PC can open it.</summary>
    private void LetGo()
    {
        if (_open is { } open)
        {
            _open = null;
            CameraDrivers.Release(open);
        }
    }

    partial void OnSelectedFoundChanged(CameraInfo? value) => UseCommand.NotifyCanExecuteChanged();

    partial void OnIsSearchingChanged(bool value) => FindCommand.NotifyCanExecuteChanged();

    partial void OnIsGrabbingChanged(bool value) => GrabCommand.NotifyCanExecuteChanged();

    private bool _disposed;

    /// <summary>
    /// The window is closing: stop, wait for the grab in hand - cancelled, it
    /// ends at once - and let the camera go.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        StopLive();

        try
        {
            // The grab itself, not the loop round it, which would want this thread back.
            _inFlight.Wait(TimeSpan.FromMilliseconds(EffectiveTimeout + 2000));
        }
        catch (AggregateException)
        {
            // Cancelled, or failed; either way it is over.
        }

        LetGo();
        _last?.Dispose();
        _last = null;
    }
}
