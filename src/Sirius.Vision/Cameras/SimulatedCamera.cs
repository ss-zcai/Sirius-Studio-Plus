using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace Sirius.Vision.Cameras;

/// <summary>
/// A camera that is always there: a disc on a textured background,
/// moving a little each frame, with exposure and gain that do what they
/// say.
/// </summary>
/// <remarks>
/// For trying a recipe with a Camera in it - and Run Continuously - on a
/// PC with no camera attached, and for the tests. The disc moves so a live
/// run visibly changes; its radius wobbles a pixel or two so a measurement
/// of it changes too.
/// </remarks>
public sealed class SimulatedCameraDriver : ICameraDriver
{
    public const string SerialNumber = "SIM-0001";

    private static readonly CameraInfo Camera = new("Simulated", SerialNumber, "test pattern", string.Empty);

    public string Name => "Simulated";

    public IReadOnlyList<CameraInfo> Enumerate() => [Camera];

    public ICamera Open(CameraInfo camera) => new SimulatedCamera(Camera);

    private sealed class SimulatedCamera(CameraInfo info) : ICamera
    {
        private const int Width = 640;
        private const int Height = 480;

        private readonly object _gate = new();
        private int _frame;

        public CameraInfo Info { get; } = info;

        public double Exposure { get; set; } = 10_000;

        public double Gain { get; set; }

        public CameraTrigger Trigger { get; set; } = CameraTrigger.Software;

        public ImageData Grab(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int frame;
            lock (_gate)
                frame = _frame++;

            // Brighter with longer exposure and more gain, as a real one is:
            // 10 ms at 0 dB is the picture as drawn.
            var level = Exposure / 10_000 * Math.Pow(10, Gain / 20);

            var mat = new Mat(Height, Width, DepthType.Cv8U, 1);

            try
            {
                mat.SetTo(new MCvScalar(40 * level));

                var phase = frame * 0.15;
                var centre = new System.Drawing.Point(
                    (int)Math.Round((Width / 2.0) + (60 * Math.Cos(phase))),
                    (int)Math.Round((Height / 2.0) + (40 * Math.Sin(phase))));

                var radius = 90 + (int)Math.Round(2 * Math.Sin(phase * 3));

                CvInvoke.Circle(mat, centre, radius, new MCvScalar(Math.Min(255, 200 * level)), -1, LineType.AntiAlias);

                using var noise = new Mat(Height, Width, DepthType.Cv8U, 1);
                CvInvoke.Randn(noise, new MCvScalar(0), new MCvScalar(4));
                CvInvoke.Add(mat, noise, mat);

                CvInvoke.PutText(
                    mat,
                    $"frame {frame}",
                    new System.Drawing.Point(12, 28),
                    FontFace.HersheySimplex,
                    0.7,
                    new MCvScalar(255));

                return ImageData.Adopt(mat);
            }
            catch
            {
                mat.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
        }
    }
}
