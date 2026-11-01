using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;
using static Sirius.Core.Tests.EdgeTestKit;

namespace Sirius.Core.Tests;

/// <summary>
/// Calibrating a lens from views of a chessboard, and taking its distortion
/// out again.
/// </summary>
/// <remarks>
/// The views are made through a known lens: a camera with a focal length
/// of 500 pixels and its centre in the middle of a 640 x 480 picture,
/// looking at a board of 10 x 7 squares - 9 x 6 inner corners - from 12.5
/// squares away, upright or tilted, then bent by barrel distortion
/// (k1 = -0.25, k2 = 0.05). Calibration should find that lens again, and
/// Undistort should make the board's rows straight.
/// </remarks>
public class LensCalibrationTests
{
    public LensCalibrationTests() => TestCatalog.Ensure();

    private const int Width = 640;
    private const int Height = 480;
    private const double Focal = 500;
    private const double K1 = -0.25;
    private const double K2 = 0.05;

    [Fact]
    public void A_lens_is_found_again_from_views_of_a_chessboard()
    {
        var (node, result) = Calibrated();

        Assert.Equal(4, result.Get<int>(node.ViewsOut));
        Assert.InRange(result.Get<double>(node.ErrorOut), 0, 0.3);

        var lens = result.Get<LensModel>(node.LensOut);
        Assert.InRange(lens.Fx, Focal * 0.97, Focal * 1.03);
        Assert.InRange(lens.Distortion[0], K1 - 0.04, K1 + 0.04);
        Assert.Equal(0, lens.Distortion[2]);
        Assert.Equal(0, lens.Distortion[3]);

        // The last view is the upright one, a square 40 px across once
        // corrected: at 10 mm a square, a quarter of a millimetre a pixel.
        var calibration = result.Get<Calibration>(node.CalibrationOut);
        Assert.Equal("mm", calibration.Unit);
        Assert.InRange(calibration.UnitsPerPixel, 0.245, 0.255);
    }

    /// <summary>
    /// Bowed by the lens, the board's outer rows are well over a pixel off
    /// straight; corrected, they are straight to the accuracy the corners
    /// can be found to.
    /// </summary>
    [Fact]
    public void Undistort_makes_straight_lines_straight()
    {
        var (calibrated, _) = Calibrated();
        var lens = new LensCalibrationNode { Views = calibrated.Views };

        var raw = View(0, 0, 0, 0);
        Assert.True(WorstBow(raw) > 0.8, $"the raw rows bow only {WorstBow(raw):0.##} px");

        var undistort = new UndistortNode();
        var (graph, _) = Wire(() => View(0, 0, 0, 0), undistort);
        graph.Add(lens);

        // A calibration node holding the same views, with no picture of its own.
        Assert.True(graph.TryConnect(lens.LensOut, undistort.LensIn).Success);

        var result = Run(graph);
        var corrected = result.Get<ImageData>(undistort.ImageOut);

        Assert.Equal((Width, Height), (corrected.Width, corrected.Height));
        // Corner finding is good to a tenth or two of a pixel, so the worst
        // of nine points off a line is a few tenths however straight it is.
        Assert.InRange(WorstBow(corrected), 0, 0.5);
        Assert.True(WorstBow(corrected) < WorstBow(raw) / 2.5, $"corrected {WorstBow(corrected):0.###} px against {WorstBow(raw):0.###} px raw");
    }

    [Fact]
    public void Add_View_says_why_when_there_is_no_target()
    {
        var node = new LensCalibrationNode();
        var (graph, _) = Wire(() => Render(Width, Height, (x, y) => 0), node);
        var result = Run(graph);

        Assert.Null(result.GetOutput(node.LensOut));
        Assert.Contains("No target", node.AddView(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, node.Views);
    }

    [Fact]
    public void Without_a_lens_the_picture_passes_through()
    {
        var node = new UndistortNode();
        var (graph, _) = Wire(() => View(0, 0, 0, 0), node);
        var result = Run(graph);

        Assert.NotNull(result.Get<ImageData>(node.ImageOut));
        Assert.Equal(NodeStatus.Warning, node.Status);
    }

    /// <summary>The views kept are the corners, saved with the recipe, so a reopened recipe has its lens without the target.</summary>
    [Fact]
    public void The_views_are_kept_in_the_recipe()
    {
        var (calibrated, _) = Calibrated();
        var node = new LensCalibrationNode { Views = calibrated.Views };

        var graph = new VisionGraph();
        graph.Add(node);
        var reopened = (LensCalibrationNode)Sirius.Core.Serialization.GraphSerializer
            .Deserialize(Sirius.Core.Serialization.GraphSerializer.Serialize(graph)).Graph.Nodes.Single();

        Assert.Equal(node.Views, reopened.Views);

        var result = Run(Single(reopened));
        Assert.Equal(4, result.Get<int>(reopened.ViewsOut));
        Assert.NotNull(result.Get<LensModel>(reopened.LensOut));
    }

    // ---- making the views --------------------------------------------------------

    /// <summary>A calibration node shown four views - tilted three ways, then upright - with each one added.</summary>
    private static (LensCalibrationNode Node, GraphScheduler Result) Calibrated()
    {
        var node = new LensCalibrationNode { Spacing = 10, Unit = "mm" };
        ImageData current = View(0, 0, 0, 0);

        var (graph, _) = Wire(() => current.Clone(), node);
        GraphScheduler? result = null;

        foreach (var (tiltX, tiltY, dx, dy) in new[] { (20.0, 0.0, 0.5, 0.0), (0.0, 22.0, 0.0, 0.5), (-15.0, 15.0, -0.5, 0.0), (0.0, 0.0, 0.0, 0.0) })
        {
            current = View(tiltX, tiltY, dx, dy);
            result = Run(graph);
            Assert.Null(node.AddView());
        }

        // Once more, so the outputs follow the last view added.
        return (node, Run(graph));
    }

    private static VisionGraph Single(VisionNode node)
    {
        var graph = new VisionGraph();
        graph.Add(node);
        return graph;
    }

    /// <summary>
    /// The board seen through the lens: tilted <paramref name="tiltX"/>
    /// degrees about its across axis and <paramref name="tiltY"/> about its
    /// down axis, moved by a fraction of a square, then distorted.
    /// </summary>
    private static ImageData View(double tiltX, double tiltY, double dx, double dy)
    {
        var toBoard = Invert(Homography(tiltX, tiltY, dx, dy));
        var data = new byte[Width * Height];

        // Four samples a pixel, so the squares' edges are soft as a real
        // camera's are rather than staircased.
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var light = 0;

                for (var s = 0; s < 4; s++)
                {
                    var (ux, uy) = Ideal(x - 0.25 + (0.5 * (s % 2)), y - 0.25 + (0.5 * (s / 2)));
                    light += Square(toBoard, ux, uy);
                }

                data[(y * Width) + x] = (byte)(20 + (light * 55));
            }
        }

        var mat = new Mat(Height, Width, DepthType.Cv8U, 1);
        mat.SetTo(data);
        return ImageData.Adopt(mat);
    }

    /// <summary>
    /// Where a pixel of the distorted picture came from in the ideal one:
    /// the lens's distortion undone by fixed-point iteration, as OpenCV
    /// does it.
    /// </summary>
    private static (double X, double Y) Ideal(double x, double y)
    {
        var (xd, yd) = ((x - (Width / 2.0)) / Focal, (y - (Height / 2.0)) / Focal);
        var (xu, yu) = (xd, yd);

        for (var i = 0; i < 20; i++)
        {
            var r2 = (xu * xu) + (yu * yu);
            var radial = 1 + (K1 * r2) + (K2 * r2 * r2);
            (xu, yu) = (xd / radial, yd / radial);
        }

        return ((xu * Focal) + (Width / 2.0), (yu * Focal) + (Height / 2.0));
    }

    /// <summary>1 on a light square, 0 on a dark one or off the board.</summary>
    private static int Square(double[,] toBoard, double x, double y)
    {
        var w = (toBoard[2, 0] * x) + (toBoard[2, 1] * y) + toBoard[2, 2];
        var bx = ((toBoard[0, 0] * x) + (toBoard[0, 1] * y) + toBoard[0, 2]) / w;
        var by = ((toBoard[1, 0] * x) + (toBoard[1, 1] * y) + toBoard[1, 2]) / w;

        // A white margin of a square round the 10 x 7 board.
        if (bx < -1 || by < -1 || bx > 11 || by > 8)
            return 0;

        if (bx < 0 || by < 0 || bx >= 10 || by >= 7)
            return 1;

        return ((int)Math.Floor(bx) + (int)Math.Floor(by)) % 2 == 0 ? 0 : 1;
    }

    /// <summary>Board squares to ideal pixels: K [r1 r2 t], the board turned about its centre 12.5 squares from the camera.</summary>
    private static double[,] Homography(double tiltX, double tiltY, double dx, double dy)
    {
        var (a, b) = (tiltX * Math.PI / 180, tiltY * Math.PI / 180);

        // R = Ry(b) * Rx(a).
        var r = new double[3, 3]
        {
            { Math.Cos(b), Math.Sin(b) * Math.Sin(a), Math.Sin(b) * Math.Cos(a) },
            { 0, Math.Cos(a), -Math.Sin(a) },
            { -Math.Sin(b), Math.Cos(b) * Math.Sin(a), Math.Cos(b) * Math.Cos(a) },
        };

        // The board's centre, (5, 3.5), sits on the axis 12.5 away, moved by dx, dy.
        var t = new double[3];

        for (var i = 0; i < 3; i++)
            t[i] = -((r[i, 0] * 5) + (r[i, 1] * 3.5));

        t[0] += dx;
        t[1] += dy;
        t[2] += 12.5;

        var k = new double[3, 3] { { Focal, 0, Width / 2.0 }, { 0, Focal, Height / 2.0 }, { 0, 0, 1 } };
        var h = new double[3, 3];

        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                var column = j == 2 ? t : new[] { r[0, j], r[1, j], r[2, j] };
                h[i, j] = (k[i, 0] * column[0]) + (k[i, 1] * column[1]) + (k[i, 2] * column[2]);
            }
        }

        return h;
    }

    private static double[,] Invert(double[,] m)
    {
        var det =
            (m[0, 0] * ((m[1, 1] * m[2, 2]) - (m[1, 2] * m[2, 1]))) -
            (m[0, 1] * ((m[1, 0] * m[2, 2]) - (m[1, 2] * m[2, 0]))) +
            (m[0, 2] * ((m[1, 0] * m[2, 1]) - (m[1, 1] * m[2, 0])));

        var inverse = new double[3, 3];

        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                // Cofactor of (j, i), for the transpose.
                var (r0, r1) = (j == 0 ? 1 : 0, j == 2 ? 1 : 2);
                var (c0, c1) = (i == 0 ? 1 : 0, i == 2 ? 1 : 2);
                var minor = (m[r0, c0] * m[r1, c1]) - (m[r0, c1] * m[r1, c0]);
                inverse[i, j] = ((i + j) % 2 == 0 ? 1 : -1) * minor / det;
            }
        }

        return inverse;
    }

    /// <summary>How far off straight the board's rows and columns run at worst, in pixels.</summary>
    private static double WorstBow(ImageData picture)
    {
        var found = LensCalibrationNode.Find(picture, CalibrationTarget.Chessboard, 9, 6, CancellationToken.None);
        Assert.NotNull(found);

        var points = Enumerable.Range(0, found.Points.Length / 2)
            .Select(i => new Core.Shapes.Point2(found.Points[2 * i], found.Points[(2 * i) + 1]))
            .ToArray();

        var worst = 0.0;

        for (var row = 0; row < 6; row++)
        {
            var line = points.Skip(row * 9).Take(9).ToArray();
            var fit = LineFit.Fit(line);
            worst = Math.Max(worst, line.Max(fit.DistanceTo));
        }

        return worst;
    }
}
