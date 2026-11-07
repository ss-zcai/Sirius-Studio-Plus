using System.Reflection;
using Emgu.CV;
using Emgu.CV.CvEnum;
using MvCameraControl;
using Sirius.Vision;
using Sirius.Vision.Cameras;

namespace Sirius.Cameras.Hikrobot;

/// <summary>
/// Hikrobot cameras - Hikvision's industrial range, GigE and USB3 alike -
/// through the MVS SDK.
/// </summary>
/// <remarks>
/// <para>
/// MVS drives both interfaces through the same calls, as pylon does, so a
/// USB3 camera is listed and opened exactly as a GigE one is. Virtual
/// cameras made with MVS's Virtual Device Tool are listed too: they exist
/// only where somebody made one, and they are what the tests run against.
/// </para>
/// <para>
/// Uses the SDK's object API (MvCameraControl), not its older one
/// (MvCamCtrl.NET) that mirrors the C calls: Hikrobot recommends it for new
/// code, and its devices and frames are disposable.
/// </para>
/// </remarks>
public sealed class HikrobotCameraDriver : ICameraDriver
{
    /// <summary>
    /// Every kind of camera MVS can reach, less serial CameraLink, whose
    /// search is slow and which this application has no use for.
    /// </summary>
    private const DeviceTLayerType Searched =
        DeviceTLayerType.MvGigEDevice | DeviceTLayerType.MvUsbDevice |
        DeviceTLayerType.MvVirGigEDevice | DeviceTLayerType.MvVirUsbDevice |
        DeviceTLayerType.MvGenTLGigEDevice | DeviceTLayerType.MvGenTLCameraLinkDevice |
        DeviceTLayerType.MvGenTLCXPDevice | DeviceTLayerType.MvGenTLXoFDevice | DeviceTLayerType.MvGenTLXoCDevice;

    private static readonly object Gate = new();
    private static bool _initialised;

    public HikrobotCameraDriver() => Initialise();

    public string Name => "Hikrobot";

    public IReadOnlyList<CameraInfo> Enumerate() => Find().Select(Describe).ToArray();

    public ICamera Open(CameraInfo camera)
    {
        var info = Find().FirstOrDefault(found =>
                       string.Equals(found.SerialNumber, camera.Serial, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException($"Hikrobot camera {camera.Serial} is not connected.");

        return new HikrobotCamera(Describe(info), info);
    }

    /// <summary>
    /// Starts the SDK, once for the process, and shuts it down as the
    /// process ends - after the application has closed its cameras.
    /// </summary>
    /// <remarks>
    /// MVS keeps threads and network sessions of its own, and Hikrobot's
    /// notes are firm that a process that leaves without finalising can
    /// hang on the way out.
    /// </remarks>
    private static void Initialise()
    {
        lock (Gate)
        {
            if (_initialised)
                return;

            Mvs.Check(SDKSystem.Initialize(), "Starting the MVS SDK");
            _initialised = true;

            AppDomain.CurrentDomain.ProcessExit += (_, _) => SDKSystem.Finalize();
        }
    }

    /// <summary>The cameras MVS can see. One search at a time: the SDK's notes warn against two at once.</summary>
    private static List<IDeviceInfo> Find()
    {
        lock (Gate)
        {
            Mvs.Check(DeviceEnumerator.EnumDevices(Searched, out var found), "Searching for Hikrobot cameras");
            return found ?? [];
        }
    }

    private static CameraInfo Describe(IDeviceInfo info) => new(
        "Hikrobot",
        info.SerialNumber ?? string.Empty,
        info.ModelName ?? string.Empty,
        info is IGigEDeviceInfo gige ? Address(gige.CurrentIp) : string.Empty);

    /// <summary>An address as MVS keeps it - one number, the first part in its top byte.</summary>
    internal static string Address(uint ip) =>
        ip == 0 ? string.Empty : $"{(ip >> 24) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";
}

/// <summary>One open Hikrobot camera.</summary>
/// <remarks>
/// <para>
/// Grabbing runs continuously once started, and is restarted only when the
/// trigger changes, since a camera will not change its trigger while it is
/// acquiring. Exposure and gain are set while it runs, and only when they
/// change: on GigE each is a round trip on the network.
/// </para>
/// <para>
/// A hardware trigger is Line 0, the opto-isolated input on Hikrobot's
/// cameras - where Basler's is Line 1.
/// </para>
/// </remarks>
internal sealed class HikrobotCamera : ICamera
{
    /// <summary>Frames the SDK holds while they wait to be taken - its default of one drops frames.</summary>
    private const uint Buffers = 8;

    private readonly IDevice _device;
    private readonly object _gate = new();

    private CameraTrigger _trigger = CameraTrigger.Software;
    private bool _triggerApplied;
    private bool _grabbing;
    private bool _latestOnly = true;
    private bool _disposed;

    /// <summary>The time between frames the camera says it can manage, in milliseconds.</summary>
    private double _framePeriod = 250;
    private double _exposure = double.NaN;
    private double _gain = double.NaN;

    public HikrobotCamera(CameraInfo info, IDeviceInfo deviceInfo)
    {
        Info = info;
        _device = DeviceFactory.CreateDevice(deviceInfo);

        try
        {
            var opened = _device.Open();

            if (opened == MvError.MV_E_ACCESS_DENIED)
            {
                throw new InvalidOperationException(
                    $"{info.Label} is in use by another program - the MVS client, or another copy of this one. " +
                    "Close it there and try again. If nothing else has it, restart the camera - a virtual one " +
                    "in MVS's Virtual Device Tool.");
            }

            Mvs.Check(opened, $"Opening {info.Label}");

            // The largest packet the network between here and the camera
            // carries; MVS's own default is smaller, and costs frames.
            if (_device is IGigEDevice gige && gige.GetOptimalPacketSize(out var packet) == MvError.MV_OK && packet > 0)
                _device.Parameters.SetIntValue("GevSCPSPacketSize", packet);

            // Fixed settings, not the camera's own idea of them: an
            // inspection that changes its exposure between parts measures
            // differently between parts.
            _device.Parameters.SetEnumValueByString("ExposureAuto", "Off");
            _device.Parameters.SetEnumValueByString("GainAuto", "Off");
            _device.Parameters.SetEnumValueByString("AcquisitionMode", "Continuous");

            _device.StreamGrabber.SetImageNodeNum(Buffers);
        }
        catch
        {
            _device.Dispose();
            throw;
        }
    }

    public CameraInfo Info { get; }

    public double Exposure
    {
        get => _exposure;
        set
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (value == _exposure)
                    return;

                SetFloat("ExposureTime", value);
                _exposure = value;
            }
        }
    }

    public double Gain
    {
        get => _gain;
        set
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (value == _gain)
                    return;

                SetFloat("Gain", value);
                _gain = value;
            }
        }
    }

    public CameraTrigger Trigger
    {
        get => _trigger;
        set
        {
            lock (_gate)
            {
                if (value == _trigger && _triggerApplied)
                    return;

                _trigger = value;
                _triggerApplied = false;
            }
        }
    }

    public ImageData Grab(TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // Closed while this waited its turn - at exit, say. The SDK's
            // handle is gone, and a call on it is a crash, not an error.
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_triggerApplied)
                ApplyTrigger();

            var milliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);

            var software = _trigger == CameraTrigger.Software;

            // Triggered by software, a frame left over from a grab that gave
            // up would otherwise be taken as the answer to this trigger.
            // Free running one by one, the oldest frame queued is not the
            // latest, so the next to arrive is taken instead.
            if (software || !_latestOnly)
                _device.StreamGrabber.ClearImageBuffer();

            if (software)
                SendTrigger();

            var frame = Retrieve(milliseconds, software, cancellationToken)
                        ?? throw new TimeoutException("No frame arrived.");

            try
            {
                return Convert(frame.Image);
            }
            finally
            {
                _device.StreamGrabber.FreeImageBuffer(frame);
            }
        }
    }

    private void SendTrigger() =>
        Mvs.Check(_device.Parameters.SetCommandValue("TriggerSoftware"), "Triggering the camera");

    /// <summary>
    /// The next frame, waited for in short slices so a hardware trigger
    /// that never comes can still be cancelled - and a camera unplugged
    /// meanwhile is reported as that, not as a trigger that never came.
    /// </summary>
    /// <remarks>
    /// A software trigger the camera was not ready for - sent as grabbing
    /// starts, or while it is still busy with the last frame - is dropped
    /// without a word, and MVS has no way to ask whether it is ready. So
    /// one that has had time to be answered and was not is sent again. Any
    /// second frame that brings is cleared before the next grab. A hardware
    /// trigger is never repeated: that one is the line's to give.
    /// </remarks>
    private IFrameOut? Retrieve(int milliseconds, bool retrigger, CancellationToken cancellationToken)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var triggered = 0L;

        // Two of the camera's frame times and the exposure: long enough for
        // a trigger it was ready for to have been answered.
        var patience = Math.Max(150, (2 * _framePeriod) + (double.IsNaN(_exposure) ? 0 : _exposure / 1000));

        while (clock.ElapsedMilliseconds < milliseconds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var slice = (uint)Math.Clamp(milliseconds - clock.ElapsedMilliseconds, 1, 100);

            if (_device.StreamGrabber.GetImageBuffer(slice, out var frame) == MvError.MV_OK && frame is not null)
                return frame;

            if (!_device.IsConnected)
                throw new InvalidOperationException($"{Info.Label} is no longer connected.");

            if (retrigger && clock.ElapsedMilliseconds - triggered >= patience)
            {
                SendTrigger();
                triggered = clock.ElapsedMilliseconds;
            }
        }

        return null;
    }

    /// <summary>Stops grabbing, sets the trigger up, and starts again.</summary>
    private void ApplyTrigger()
    {
        if (_grabbing)
        {
            _device.StreamGrabber.StopGrabbing();
            _grabbing = false;
        }

        // Hikrobot cameras trigger a burst of frames (FrameBurstStart, one
        // frame unless told otherwise); others the frame itself.
        if (!TrySetEnum("TriggerSelector", "FrameBurstStart"))
            TrySetEnum("TriggerSelector", "FrameStart");

        switch (_trigger)
        {
            case CameraTrigger.FreeRun:
                TrySetEnum("TriggerMode", "Off");
                break;

            case CameraTrigger.Software:
                TrySetEnum("TriggerMode", "On");
                TrySetEnum("TriggerSource", "Software");
                break;

            case CameraTrigger.Hardware:
                TrySetEnum("TriggerMode", "On");
                TrySetEnum("TriggerSource", "Line0");
                TrySetEnum("TriggerActivation", "RisingEdge");
                break;
        }

        // Free running keeps only the newest frame, so a grab is never a
        // stale one; triggered, every frame is one that was asked for.
        var result = _device.StreamGrabber.StartGrabbing(
            _trigger == CameraTrigger.FreeRun ? StreamGrabStrategy.LatestImageOnly : StreamGrabStrategy.OneByOne);

        // MVS's virtual cameras take no strategy at all - MV_E_DEV_SUPPORT -
        // and grab one by one. Free running, the frames queued meanwhile
        // are then cleared before each grab instead.
        _latestOnly = result == MvError.MV_OK || _trigger != CameraTrigger.FreeRun;

        if (result == MvError.MV_E_DEV_SUPPORT)
            result = _device.StreamGrabber.StartGrabbing();

        Mvs.Check(result, "Starting the camera");

        if (_device.Parameters.GetFloatValue("ResultingFrameRate", out var rate) == MvError.MV_OK && rate?.CurValue > 0)
            _framePeriod = 1000.0 / rate.CurValue;

        _grabbing = true;
        _triggerApplied = true;
    }

    /// <summary>
    /// The frame as an image: mono stays mono and colour or Bayer becomes
    /// BGR.
    /// </summary>
    /// <remarks>
    /// Mono that is already a byte or two a pixel - Mono8, or Mono10, 12
    /// and 16 sent as whole 16-bit words - is copied as it is, so a deep
    /// sensor's levels survive. The rest goes through MVS's converter,
    /// straight into the image's own buffer: Bayer and colour to BGR, and
    /// packed Mono10 or 12 to 8-bit.
    /// </remarks>
    private ImageData Convert(IImage image)
    {
        var (width, height) = ((int)image.Width, (int)image.Height);
        var format = image.PixelType;
        var name = format.ToString();
        var mono = name.StartsWith("PixelType_Gvsp_Mono", StringComparison.Ordinal);
        var packed = name.Contains("Packed", StringComparison.Ordinal);

        if (mono && !packed && (format == MvGvspPixelType.PixelType_Gvsp_Mono8 || Bits(format) == 16))
            return Copy(image, width, height, Bits(format) == 16 ? DepthType.Cv16U : DepthType.Cv8U, SensorBits(name));

        var (target, depth, channels) = mono
            ? (MvGvspPixelType.PixelType_Gvsp_Mono8, DepthType.Cv8U, 1)
            : (MvGvspPixelType.PixelType_Gvsp_BGR8_Packed, DepthType.Cv8U, 3);

        var mat = new Mat(height, width, depth, channels);

        try
        {
            var source = new ImageInfo
            {
                Width = image.Width,
                Height = image.Height,
                PixelType = format,
                ImageSize = image.ImageSize,
            };

            Mvs.Check(
                _device.PixelTypeConverter.ConvertPixelType(
                    image.PixelDataPtr, source, mat.DataPointer, (ulong)mat.Step * (ulong)mat.Rows, target, out _),
                $"Converting a {Friendly(format)} frame");

            return ImageData.Adopt(mat);
        }
        catch
        {
            mat.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Mono already a byte or two a pixel, copied into an image of its own -
    /// the SDK's buffer goes back to it as soon as this returns.
    /// </summary>
    private static ImageData Copy(IImage image, int width, int height, DepthType depth, int? bits)
    {
        var rowBytes = width * (depth == DepthType.Cv16U ? 2 : 1);

        if ((long)image.ImageSize < (long)rowBytes * height)
            throw new InvalidOperationException("The camera sent a frame smaller than its size says.");

        using var view = new Mat(height, width, depth, 1, image.PixelDataPtr, rowBytes);
        var mat = new Mat();

        try
        {
            view.CopyTo(mat);
            return ImageData.Adopt(mat, bits: bits);
        }
        catch
        {
            mat.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The bits that carry the picture, from the format's name - 12 for
    /// Mono12, which occupies 16 - or null when the name does not say.
    /// </summary>
    internal static int? SensorBits(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name, @"Mono(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var bits) ? bits : null;
    }

    /// <summary>Bits a pixel occupies - the format's second byte, by the GigE Vision numbering.</summary>
    private static int Bits(MvGvspPixelType format) => (int)(((uint)format >> 16) & 0xFF);

    /// <summary>"BayerRG8" for PixelType_Gvsp_BayerRG8.</summary>
    private static string Friendly(MvGvspPixelType format) =>
        format.ToString().Replace("PixelType_Gvsp_", string.Empty, StringComparison.Ordinal);

    /// <summary>A float setting, brought into the camera's range rather than refused.</summary>
    private void SetFloat(string name, double value)
    {
        if (_device.Parameters.GetFloatValue(name, out var range) == MvError.MV_OK && range is not null)
            value = Math.Clamp(value, range.Min, range.Max);

        _device.Parameters.SetFloatValue(name, (float)value);
    }

    private bool TrySetEnum(string name, string value) =>
        _device.Parameters.SetEnumValueByString(name, value) == MvError.MV_OK;

    /// <summary>Closes the camera; once - a second close is a call on a handle already gone.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                if (_grabbing)
                    _device.StreamGrabber.StopGrabbing();

                _device.Close();
            }
            finally
            {
                _grabbing = false;
                _device.Dispose();
            }
        }
    }
}

/// <summary>MVS's result codes, as exceptions that say what failed and why.</summary>
internal static class Mvs
{
    /// <summary>The SDK's names for its codes, read off it once - so a message says MV_E_NODATA, not just a number.</summary>
    private static readonly Lazy<Dictionary<int, string>> Names = new(() =>
        typeof(MvError)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(int))
            .GroupBy(field => (int)field.GetRawConstantValue()!)
            .ToDictionary(group => group.Key, group => group.First().Name));

    public static void Check(int result, string what)
    {
        if (result != MvError.MV_OK)
            throw new InvalidOperationException($"{what} failed: {Describe(result)}.");
    }

    /// <summary>"MV_E_NODATA (0x80000007)" - the name Hikrobot's documentation looks it up by, and the number.</summary>
    public static string Describe(int result) =>
        Names.Value.TryGetValue(result, out var name) ? $"{name} (0x{result:X8})" : $"0x{result:X8}";
}
