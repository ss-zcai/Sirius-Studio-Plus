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
/// the application closes (<see cref="CloseAll"/>).
/// </para>
/// </remarks>
public static class CameraDrivers
{
    private static readonly object Gate = new();
    private static readonly List<ICameraDriver> Drivers = [new SimulatedCameraDriver()];
    private static readonly Dictionary<string, ICamera> Open = new(StringComparer.OrdinalIgnoreCase);

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
    public static ICamera? Acquire(string label)
    {
        if (CameraInfo.SerialIn(label) is not { } serial)
            return null;

        lock (Gate)
        {
            if (Open.TryGetValue(serial, out var open))
                return open;
        }

        foreach (var driver in All)
        {
            var info = driver.Enumerate().FirstOrDefault(camera =>
                string.Equals(camera.Serial, serial, StringComparison.OrdinalIgnoreCase));

            if (info is null)
                continue;

            var camera = driver.Open(info);

            lock (Gate)
            {
                // Opened by another node meanwhile: keep the first.
                if (Open.TryGetValue(serial, out var raced))
                {
                    camera.Dispose();
                    return raced;
                }

                Open[serial] = camera;
                return camera;
            }
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

        camera.Dispose();
    }

    /// <summary>Closes every open camera.</summary>
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
                camera.Dispose();
            }
            catch (Exception)
            {
                // Closing down: a camera that will not close is not worth
                // stopping the rest for.
            }
        }
    }
}
