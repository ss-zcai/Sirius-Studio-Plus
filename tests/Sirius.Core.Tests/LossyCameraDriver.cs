using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Vision;
using Sirius.Vision.Cameras;

namespace Sirius.Core.Tests;

/// <summary>
/// A camera on a lossy network, its own serial number per test: frames can
/// be made to come incomplete, and its first opens refused as a camera just
/// let go of by another program is.
/// </summary>
internal sealed class LossyCameraDriver : ICameraDriver
{
    private int _opens;

    public LossyCameraDriver(string serial) => Serial = serial;

    public string Serial { get; }

    public string Name => "Lossy " + Serial;

    /// <summary>The device, as a recipe or a camera unit names it.</summary>
    public string Device => $"Lossy net [{Serial}]";

    /// <summary>Opens refused before one is let through.</summary>
    public int RefuseOpens { get; set; }

    /// <summary>Grabs to fail as frames lost, before frames come whole.</summary>
    public int LoseFrames { get; set; }

    /// <summary>Opens asked for, refused ones too.</summary>
    public int Opens => Volatile.Read(ref _opens);

    public IReadOnlyList<CameraInfo> Enumerate() => [new CameraInfo("Lossy", Serial, "net", "192.168.10.10")];

    public ICamera Open(CameraInfo camera)
    {
        Interlocked.Increment(ref _opens);

        if (RefuseOpens > 0)
        {
            RefuseOpens--;
            throw new InvalidOperationException("Device access denied (0x80000203).");
        }

        return new Lossy(this, camera);
    }

    private sealed class Lossy(LossyCameraDriver driver, CameraInfo info) : ICamera
    {
        public CameraInfo Info => info;

        public double Exposure { get; set; }

        public double Gain { get; set; }

        public CameraTrigger Trigger { get; set; }

        public ImageData Grab(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (driver.LoseFrames > 0)
            {
                driver.LoseFrames--;
                throw new FrameLostException(info, "The buffer was incompletely grabbed.", 3);
            }

            var mat = new Mat(6, 8, DepthType.Cv8U, 1);
            mat.SetTo(new Emgu.CV.Structure.MCvScalar(90));
            return ImageData.Adopt(mat);
        }

        public void Dispose()
        {
        }
    }
}
