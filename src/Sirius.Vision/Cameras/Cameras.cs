using System.Reflection;
using Sirius.Core.Graph;

namespace Sirius.Vision.Cameras;

/// <summary>What starts a camera's exposure.</summary>
public enum CameraTrigger
{
    /// <summary>The camera exposes continuously; a grab takes the latest frame.</summary>
    FreeRun,

    /// <summary>Each grab asks the camera for one frame.</summary>
    Software,

    /// <summary>The camera waits for a pulse on its trigger input - from a sensor or a PLC - and each grab waits for that frame.</summary>
    Hardware,
}

/// <summary>A camera a driver can see.</summary>
/// <param name="Driver">Which driver found it - "Basler", "Hikrobot", "Simulated".</param>
/// <param name="Serial">Its serial number: what a recipe remembers it by, since an address can change.</param>
/// <param name="Model">Its model name.</param>
/// <param name="Address">Where it is - an IP address for GigE - or empty.</param>
public sealed record CameraInfo(string Driver, string Serial, string Model, string Address)
{
    /// <summary>"Basler acA1920-40gm [23456789]" - the name in a recipe, with the serial it is found by.</summary>
    public string Label => $"{Driver} {Model} [{Serial}]";

    /// <summary>The serial number in a label, or null when there is none.</summary>
    public static string? SerialIn(string label)
    {
        var open = label.LastIndexOf('[');
        var close = label.LastIndexOf(']');
        return open >= 0 && close > open ? label[(open + 1)..close] : null;
    }
}

/// <summary>An open camera.</summary>
/// <remarks>
/// Settings are applied as they are set; an implementation keeps the last
/// value it sent and does not send it again, since on GigE every setting
/// is a round trip on the network.
/// </remarks>
public interface ICamera : IDisposable
{
    CameraInfo Info { get; }

    /// <summary>Exposure time, in microseconds.</summary>
    double Exposure { get; set; }

    /// <summary>Gain, in decibels.</summary>
    double Gain { get; set; }

    CameraTrigger Trigger { get; set; }

    /// <summary>
    /// One frame - the next one to arrive, or for a software trigger the
    /// one this asks for. Throws <see cref="TimeoutException"/> when none
    /// arrives in time.
    /// </summary>
    ImageData Grab(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>One make of camera: Basler through pylon, Hikrobot through MVS.</summary>
public interface ICameraDriver
{
    /// <summary>"Basler", "Hikrobot".</summary>
    string Name { get; }

    /// <summary>The cameras it can see now.</summary>
    IReadOnlyList<CameraInfo> Enumerate();

    ICamera Open(CameraInfo camera);
}

/// <summary>
/// The camera drivers available here, and the cameras they have open.
/// </summary>
/// <remarks>
/// <para>
/// A driver joins only when its maker's runtime is installed - pylon for
/// Basler, MVS for Hikrobot - so a PC without one simply lists none of
/// those cameras rather than failing to start.
/// </para>
/// <para>
/// Open cameras are kept here rather than by the nodes using them. A camera
/// can be opened once, two Camera nodes may want the same one, and a node
/// that is deleted is kept by undo and may come back. They are closed when
/// the application closes (<see cref="ShutDown"/>).
/// </para>
/// <para>
/// Only one thread at a time goes into the cameras' SDKs to list, open or
/// close (<see cref="SdkGate"/>). Two searches at once crash either SDK,
/// in the code of a transport DLL the other search has just unloaded -
/// and cameras are listed from three places at once as a recipe opens:
/// the start-up search, the Camera node's Device list and the first
/// inspection. pylon also searches through MVS's GenTL producers when
/// both are installed, so one SDK's search runs into the other's: the
/// gate is shared by every driver, not kept by each.
/// </para>
/// </remarks>
public static class CameraDrivers
{
    /// <summary>The lists below. Never held while taking <see cref="SdkGate"/>.</summary>
    private static readonly object Gate = new();

    /// <summary>
    /// Held while any driver lists, opens or closes a camera. Grabbing is
    /// left out: a grab waiting for a trigger would hold up every search.
    /// </summary>
    private static readonly object SdkGate = new();

    private static readonly List<ICameraDriver> Drivers = [new SimulatedCameraDriver()];
    private static readonly Dictionary<string, ICamera> Open = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> Problems = [];
    private static bool _loaded;
    private static bool _closed;
    private static volatile bool _searching;

    /// <summary>
    /// Whether the drivers beside the application are still being looked
    /// for - the first search, which waits for GigE cameras to answer.
    /// </summary>
    public static bool IsSearching => _searching;

    /// <summary>
    /// Why a driver beside the application was left out - its maker's
    /// runtime missing or too old, usually - one line each, starting with
    /// the make: "Basler: ...".
    /// </summary>
    public static IReadOnlyList<string> LoadProblems
    {
        get
        {
            lock (Gate)
                return Problems.ToArray();
        }
    }

    /// <summary>
    /// Adds every camera driver beside the application - a
    /// Sirius.Cameras.*.dll - that works here. Once; later calls do nothing.
    /// </summary>
    /// <remarks>
    /// Each is asked to list its cameras before it joins, because a driver
    /// whose runtime is missing fails then rather than when it is made:
    /// left out with the reason kept, it does not fail every time the
    /// cameras are listed afterwards. Slow on a network with GigE cameras -
    /// the first search waits for them to answer - so the application does
    /// this off its UI thread.
    /// </remarks>
    public static void LoadInstalled(string? folder = null)
    {
        lock (Gate)
        {
            if (_loaded)
                return;

            _loaded = true;
            _searching = true;
        }

        try
        {
            Load(folder ?? AppContext.BaseDirectory);
        }
        finally
        {
            _searching = false;
        }
    }

    private static void Load(string folder)
    {
        if (!Directory.Exists(folder))
            return;

        foreach (var path in Directory.EnumerateFiles(folder, "Sirius.Cameras.*.dll"))
        {
            try
            {
                var types = Assembly.LoadFrom(path).GetTypes().Where(type =>
                    typeof(ICameraDriver).IsAssignableFrom(type) &&
                    !type.IsAbstract &&
                    type.GetConstructor(Type.EmptyTypes) is not null);

                foreach (var type in types)
                {
                    ICameraDriver driver;

                    lock (SdkGate)
                    {
                        driver = (ICameraDriver)Activator.CreateInstance(type)!;
                        driver.Enumerate();
                    }

                    Register(driver);
                }
            }
            catch (Exception ex)
            {
                // Past the wrappers reflection puts round the real reason.
                var cause = ex switch
                {
                    ReflectionTypeLoadException { LoaderExceptions: { Length: > 0 } loader } when loader[0] is { } first => first,
                    TargetInvocationException { InnerException: { } inner } => inner,
                    _ => ex,
                };

                // "Basler", not "Sirius.Cameras.Basler": this is read by
                // whoever is looking for their camera.
                var make = Path.GetFileNameWithoutExtension(path)["Sirius.Cameras.".Length..];

                lock (Gate)
                    Problems.Add($"{make}: {ExceptionText.Describe(cause)}");
            }
        }
    }

    /// <summary>Every driver available.</summary>
    public static IReadOnlyList<ICameraDriver> All
    {
        get
        {
            lock (Gate)
                return Drivers.ToArray();
        }
    }

    /// <summary>Adds a driver - at start-up, once its runtime is known to be there.</summary>
    public static void Register(ICameraDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);

        lock (Gate)
        {
            if (Drivers.All(existing => existing.Name != driver.Name))
                Drivers.Add(driver);
        }
    }

    /// <summary>Every camera every driver can see. A driver that fails to list is left out.</summary>
    public static IReadOnlyList<CameraInfo> Enumerate() =>
        All.SelectMany(driver =>
        {
            try
            {
                lock (SdkGate)
                    return driver.Enumerate();
            }
            catch (Exception)
            {
                return [];
            }
        }).ToArray();

    /// <summary>
    /// The camera a recipe names, opened - or already open - or null when
    /// no driver can see one with that serial number.
    /// </summary>
    /// <remarks>
    /// After <see cref="ShutDown"/> none opens again: an inspection still
    /// finishing would otherwise open one that nothing closes, and its SDK
    /// would shut down around it.
    /// </remarks>
    public static ICamera? Acquire(string label)
    {
        if (CameraInfo.SerialIn(label) is not { } serial)
            return null;

        lock (Gate)
        {
            if (_closed)
                throw Closed();

            if (Open.TryGetValue(serial, out var open))
                return open;
        }

        foreach (var driver in All)
        {
            ICamera camera;

            lock (SdkGate)
            {
                IReadOnlyList<CameraInfo> found;

                try
                {
                    found = driver.Enumerate();
                }
                catch (Exception)
                {
                    continue;
                }

                var info = found.FirstOrDefault(camera =>
                    string.Equals(camera.Serial, serial, StringComparison.OrdinalIgnoreCase));

                if (info is null)
                    continue;

                camera = driver.Open(info);
            }

            ICamera? kept = null;

            lock (Gate)
            {
                // Opened by another node meanwhile: keep the first. Closed
                // down meanwhile: keep none.
                if (!_closed && !Open.TryGetValue(serial, out kept))
                {
                    Open[serial] = camera;
                    return camera;
                }
            }

            Close(camera);

            return kept ?? throw Closed();
        }

        return null;
    }

    /// <summary>Lets a camera go - after it failed, so the next grab opens it afresh.</summary>
    public static void Release(ICamera camera)
    {
        lock (Gate)
        {
            if (Open.TryGetValue(camera.Info.Serial, out var open) && ReferenceEquals(open, camera))
                Open.Remove(camera.Info.Serial);
        }

        Close(camera);
    }

    /// <summary>
    /// Closes every open camera for good, as the application closes: none
    /// opens after this.
    /// </summary>
    public static void ShutDown()
    {
        lock (Gate)
            _closed = true;

        CloseAll();
    }

    /// <summary>Closes every open camera. The next grab opens its camera again.</summary>
    public static void CloseAll()
    {
        ICamera[] cameras;

        lock (Gate)
        {
            cameras = Open.Values.ToArray();
            Open.Clear();
        }

        foreach (var camera in cameras)
        {
            try
            {
                Close(camera);
            }
            catch (Exception)
            {
                // Closing down: a camera that will not close is not worth
                // stopping the rest for.
            }
        }
    }

    /// <summary>Closes one camera, with no other call into the SDKs meanwhile.</summary>
    private static void Close(ICamera camera)
    {
        lock (SdkGate)
            camera.Dispose();
    }

    private static InvalidOperationException Closed() =>
        new("The cameras are closed: the application is closing.");
}
