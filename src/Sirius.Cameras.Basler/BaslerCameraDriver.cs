using Basler.Pylon;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Vision;
using Sirius.Vision.Cameras;
using PylonCamera = Basler.Pylon.Camera;
using PylonICamera = Basler.Pylon.ICamera;

namespace Sirius.Cameras.Basler;

/// <summary>
/// Basler cameras - GigE and USB3 alike - through pylon.
/// </summary>
/// <remarks>
/// pylon drives both interfaces through the same calls, so a USB3 camera is
/// listed and opened exactly as a GigE one is. The camera emulator pylon
/// ships with appears too when it is switched on (PYLON_CAMEMU=1), which is
/// what the tests run against.
/// </remarks>
public sealed class BaslerCameraDriver : ICameraDriver
{
    public string Name => "Basler";

    public IReadOnlyList<CameraInfo> Enumerate() =>
        CameraFinder.Enumerate().Select(Describe).ToArray();

    public Sirius.Vision.Cameras.ICamera Open(CameraInfo camera)
    {
        var info = CameraFinder.Enumerate().FirstOrDefault(found =>
                       string.Equals(Get(found, CameraInfoKey.SerialNumber), camera.Serial, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException($"Basler camera {camera.Serial} is not connected.");

        return new BaslerCamera(Describe(info), info);
    }

    private static CameraInfo Describe(ICameraInfo info) => new(
        "Basler",
        Get(info, CameraInfoKey.SerialNumber),
        Get(info, CameraInfoKey.ModelName),
        Get(info, CameraInfoKey.DeviceIpAddress));

    private static string Get(ICameraInfo info, string key) =>
        info.ContainsKey(key) ? info[key] ?? string.Empty : string.Empty;
}

/// <summary>One open Basler camera.</summary>
/// <remarks>
/// <para>
/// Grabbing runs continuously once started, and is restarted only when the
/// trigger changes, since a camera will not change its trigger while it is
/// acquiring. Exposure and gain are set while it runs, and only when they
/// change: on GigE each is a round trip on the network.
/// </para>
/// <para>
/// Settings go by the names newer cameras use (ExposureTime, Gain) and fall
/// back to those of older GigE models (ExposureTimeAbs, GainAbs); values
/// outside a camera's range are brought into it rather than refused.
/// </para>
/// </remarks>
internal sealed class BaslerCamera : Sirius.Vision.Cameras.ICamera
{
    private readonly PylonCamera _camera;
    private readonly PixelDataConverter _converter = new();
    private readonly object _gate = new();

    private CameraTrigger _trigger = CameraTrigger.Software;
    private bool _triggerApplied;
    private double _exposure = double.NaN;
    private double _gain = double.NaN;

    public BaslerCamera(CameraInfo info, ICameraInfo pylonInfo)
    {
        Info = info;
        _camera = new PylonCamera(pylonInfo);

        try
        {
            _camera.Open();

            // Fixed settings, not the camera's own idea of them: an
            // inspection that changes its exposure between parts measures
            // differently between parts.
            TrySetEnum("ExposureAuto", "Off");
            TrySetEnum("GainAuto", "Off");
            TrySetEnum("AcquisitionMode", "Continuous");
        }
        catch
        {
            _camera.Dispose();
            throw;
        }
    }

    public CameraInfo Info { get; }

    private IStreamGrabber Grabber =>
        _camera.StreamGrabber ?? throw new InvalidOperationException("The camera has no image stream.");

    public double Exposure
    {
        get => _exposure;
        set
        {
            lock (_gate)
            {
                if (value == _exposure)
                    return;

                if (!TrySetFloat("ExposureTime", value) && !TrySetFloat("ExposureTimeAbs", value))
                    TrySetInteger("ExposureTimeRaw", (long)Math.Round(value));

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
                if (value == _gain)
                    return;

                // Older GigE models have only a raw gain with no unit; there
                // is no decibel value to give it, so it is left as set.
                if (!TrySetFloat("Gain", value))
                    TrySetFloat("GainAbs", value);

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
            if (!_triggerApplied)
                ApplyTrigger();

            var milliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);

            if (_trigger == CameraTrigger.Software)
            {
                if (!_camera.WaitForFrameTriggerReady(milliseconds, TimeoutHandling.Return))
                    throw new TimeoutException("The camera was not ready for a trigger.");

                _camera.ExecuteSoftwareTrigger();
            }

            using var result = Retrieve(milliseconds, cancellationToken)
                ?? throw new TimeoutException("No frame arrived.");

            if (!result.GrabSucceeded)
                throw new InvalidOperationException($"The camera reported an error: {result.ErrorDescription}");

            return Convert(result);
        }
    }

    /// <summary>
    /// The next frame, waited for in short slices so a hardware trigger
    /// that never comes can still be cancelled.
    /// </summary>
    private IGrabResult? Retrieve(int milliseconds, CancellationToken cancellationToken)
    {
        var remaining = milliseconds;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var slice = Math.Min(remaining, 100);
            var result = Grabber.RetrieveResult(slice, TimeoutHandling.Return);

            if (result is not null)
                return result;

            remaining -= slice;
        }

        return null;
    }

    /// <summary>Stops grabbing, sets the trigger up, and starts again.</summary>
    private void ApplyTrigger()
    {
        if (Grabber.IsGrabbing)
            Grabber.Stop();

        // Newer cameras trigger each frame (FrameStart); some older ones
        // only the acquisition (AcquisitionStart).
        if (!TrySetEnum("TriggerSelector", "FrameStart"))
            TrySetEnum("TriggerSelector", "AcquisitionStart");

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
                TrySetEnum("TriggerSource", "Line1");
                TrySetEnum("TriggerActivation", "RisingEdge");
                break;
        }

        // Free running keeps only the newest frame, so a grab is never a
        // stale one; triggered, every frame is one that was asked for.
        Grabber.Start(
            _trigger == CameraTrigger.FreeRun ? GrabStrategy.LatestImages : GrabStrategy.OneByOne,
            GrabLoop.ProvidedByUser);

        _triggerApplied = true;
    }

    /// <summary>
    /// The frame as an image: mono stays mono - 8-bit, or 16-bit for a
    /// deeper sensor - and colour or Bayer becomes BGR, converted straight
    /// into the image's own buffer.
    /// </summary>
    private ImageData Convert(IGrabResult result)
    {
        var type = result.PixelTypeValue;

        var (format, depth, channels) = type.IsMonoImage()
            ? type.BitDepth() > 8 ? (PixelType.Mono16, DepthType.Cv16U, 1) : (PixelType.Mono8, DepthType.Cv8U, 1)
            : (PixelType.BGR8packed, DepthType.Cv8U, 3);

        _converter.OutputPixelFormat = format;

        var mat = new Mat(result.Height, result.Width, depth, channels);

        try
        {
            _converter.Convert(mat.DataPointer, (long)mat.Step * mat.Rows, result);
            return ImageData.Adopt(mat);
        }
        catch
        {
            mat.Dispose();
            throw;
        }
    }

    private bool TrySetFloat(string name, double value)
    {
        var parameter = _camera.Parameters[(FloatName)name];
        return parameter.IsWritable && parameter.TrySetValue(value, FloatValueCorrection.ClipToRange);
    }

    private bool TrySetInteger(string name, long value)
    {
        var parameter = _camera.Parameters[(IntegerName)name];

        if (!parameter.IsWritable)
            return false;

        parameter.SetValue(Math.Clamp(value, parameter.GetMinimum(), parameter.GetMaximum()));
        return true;
    }

    private bool TrySetEnum(string name, string value)
    {
        var parameter = _camera.Parameters[(EnumName)name];
        return parameter.IsWritable && parameter.TrySetValue(value);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                if (Grabber.IsGrabbing)
                    Grabber.Stop();

                _camera.Close();
            }
            finally
            {
                _camera.Dispose();
                _converter.Dispose();
            }
        }
    }
}
