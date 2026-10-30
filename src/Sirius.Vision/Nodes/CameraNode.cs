using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Vision.Cameras;

namespace Sirius.Vision.Nodes;

/// <summary>
/// Takes pictures from a camera: one each inspection.
/// </summary>
/// <remarks>
/// <para>
/// A new frame on each inspection - a Run, each frame of Run Continuously -
/// and the same frame again on the re-runs that follow an edit, so a
/// threshold is tuned against one picture rather than one that changes
/// under it. Changing the camera's own settings - exposure, gain, trigger -
/// takes a new frame, since the point of changing them is to see the
/// result.
/// </para>
/// <para>
/// The camera is found by its serial number, which the recipe keeps, so it
/// is found again after its IP address changes. Cameras of a make whose
/// runtime is not installed here - pylon for Basler, MVS for Hikrobot - are
/// not listed; the simulated one always is.
/// </para>
/// </remarks>
[VisionNode(
    "Camera",
    TypeKey = "source.camera",
    Category = NodeCategory.Source,
    Icon = "ph_camera",
    Description = "Takes a picture from a camera on each inspection - each Run, each frame of Run Continuously - " +
                  "and keeps it while settings are tuned.")]
public sealed class CameraNode : VisionNode, IInspectionNode, IParameterChoices
{
    private readonly object _gate = new();

    private string _device = string.Empty;
    private double _exposure = 10_000;
    private double _gain;
    private CameraTrigger _trigger = CameraTrigger.Software;
    private int _timeout = 2000;

    private ImageData? _last;
    private bool _settingsChanged = true;

    public CameraNode()
    {
        ImageOut = AddOutput<ImageData>("Out");
    }

    public OutputPort ImageOut { get; }

    [Param("Device", Editor = ParamEditor.ComboBox,
        Description = "Which camera. Listed by make, model and serial number; the serial number is what the " +
                      "recipe finds it by.")]
    public string Device
    {
        get => _device;
        set
        {
            if (SetParam(ref _device, value ?? string.Empty))
                _settingsChanged = true;
        }
    }

    [Param("Exposure", Min = 10, Max = 1_000_000, Step = 10, Logarithmic = true, Connectable = true,
        Description = "Exposure time, in microseconds.")]
    public double Exposure
    {
        get => _exposure;
        set
        {
            if (SetParam(ref _exposure, Math.Clamp(value, 10, 1_000_000)))
                _settingsChanged = true;
        }
    }

    [Param("Gain", Min = 0, Max = 48, Step = 0.5, Connectable = true,
        Description = "Gain, in decibels. Brighter, and noisier; prefer exposure when the part is still.")]
    public double Gain
    {
        get => _gain;
        set
        {
            if (SetParam(ref _gain, Math.Clamp(value, 0, 48)))
                _settingsChanged = true;
        }
    }

    [Param("Trigger",
        Description = "Free Run: the latest frame. Software: one frame per inspection. Hardware: wait for a " +
                      "pulse on the camera's trigger input.")]
    public CameraTrigger Trigger
    {
        get => _trigger;
        set
        {
            if (SetParam(ref _trigger, value))
                _settingsChanged = true;
        }
    }

    [Param("Timeout", Min = 100, Max = 60_000, Step = 100,
        Description = "How long to wait for a frame, in milliseconds - for a hardware trigger, how long to wait for the part.")]
    public int Timeout
    {
        get => _timeout;
        set => SetParam(ref _timeout, Math.Clamp(value, 100, 60_000));
    }

    /// <summary>The cameras to choose from: every one a driver here can see.</summary>
    public IReadOnlyList<string> ChoicesFor(string parameter) =>
        parameter == nameof(Device)
            ? CameraDrivers.Enumerate().Select(camera => camera.Label).ToArray()
            : [];

    protected override void OnEvaluate(EvalContext context)
    {
        lock (_gate)
        {
            if (context.IsInspection || _settingsChanged || _last is null)
                Take(context);

            // A copy each time: what goes out is the scheduler's to free,
            // and the frame is kept for the next re-run.
            context.Set(ImageOut, ImageData.Adopt(_last!.Mat.Clone(), _last.Space));
        }
    }

    private void Take(EvalContext context)
    {
        if (string.IsNullOrWhiteSpace(_device))
            throw new InvalidOperationException("Choose a camera in Device.");

        var camera = CameraDrivers.Acquire(_device)
            ?? throw new InvalidOperationException(
                $"{_device} was not found. Check it is powered and connected, and that its maker's software " +
                "(pylon for Basler, MVS for Hikrobot) is installed.");

        ImageData frame;

        try
        {
            camera.Trigger = _trigger;
            camera.Exposure = context.Param(nameof(Exposure), _exposure);
            camera.Gain = context.Param(nameof(Gain), _gain);

            frame = camera.Grab(TimeSpan.FromMilliseconds(_timeout), context.CancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(_trigger == CameraTrigger.Hardware
                ? $"No trigger arrived within {_timeout} ms."
                : $"No frame arrived within {_timeout} ms.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A camera that failed - unplugged, reset - is let go, so the
            // next inspection opens it afresh rather than failing for ever.
            CameraDrivers.Release(camera);
            throw;
        }

        _last?.Dispose();
        _last = frame;
        _settingsChanged = false;
    }
}
