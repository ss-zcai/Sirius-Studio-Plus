using System.Drawing;
using System.Text.Json;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Features2D;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>What kind of target a lens is calibrated from.</summary>
public enum CalibrationTarget
{
    /// <summary>A chessboard. Columns and Rows count the inner corners, where four squares meet.</summary>
    Chessboard,

    /// <summary>Dark dots on a light ground, in a square grid. Columns and Rows count the dots.</summary>
    DotGrid,
}

/// <summary>How much of a lens's distortion is calibrated.</summary>
public enum DistortionModel
{
    /// <summary>
    /// Bending that grows with distance from the centre - barrel or
    /// pincushion. Almost every machine-vision lens, and the steadier fit
    /// from few pictures.
    /// </summary>
    Radial,

    /// <summary>Radial, and a sensor not quite square to the lens.</summary>
    RadialAndTangential,
}

/// <summary>
/// Works out what a lens does to the picture from views of a printed target,
/// so Undistort can take it out again.
/// </summary>
/// <remarks>
/// <para>
/// For a lens whose distortion is larger than the tolerance being measured -
/// a wide lens, a short working distance - where a single scale from
/// Calibrate is right in the middle of the picture and wrong at its edges.
/// Hold a chessboard or a dot grid in the field of view, press Add View,
/// move it, press Add View again: the lens is fitted to every view kept so
/// far. Three or more views at different places and tilts fit it well; one
/// flat view in the middle fits the distortion with the centre and focal
/// length held, which is often enough for a camera looking straight down.
/// </para>
/// <para>
/// The views are kept in the recipe - the corners found, not the pictures -
/// so the lens is calibrated once and the target can be put away. Wire Lens
/// into Undistort, ahead of everything that measures.
/// </para>
/// <para>
/// Calibration says what a pixel is worth in the plane of the target, after
/// the distortion is taken out: the target's own spacing over the distance
/// between its corners in the corrected picture, in the latest view. Right
/// for parts lying where the target lay; for anywhere else, use Calibrate
/// on a reference there, after Undistort.
/// </para>
/// </remarks>
[VisionNode(
    "Lens Calibration",
    TypeKey = "calibration.lens",
    Category = NodeCategory.Calibration,
    Icon = "ph_lens_calibration",
    Description = "Works out a lens's distortion from views of a chessboard or dot grid, for Undistort to take out. " +
                  "Press Add View with the target in sight, a few times at different places.")]
public sealed class LensCalibrationNode : VisionNode, INodeActions
{
    private const uint Corner = 0xFFFBBF24;

    private const uint Row = 0xFF22C55E;

    private readonly object _gate = new();

    private CalibrationTarget _target = CalibrationTarget.Chessboard;
    private int _columns = 9;
    private int _rows = 6;
    private double _spacing = 10;
    private string _unit = "mm";
    private DistortionModel _model = DistortionModel.Radial;
    private bool _findTarget = true;
    private string _views = string.Empty;

    /// <summary>The target found on the latest run, for Add View to keep.</summary>
    private TargetView? _lastFound;

    /// <summary>The lens fitted to <see cref="_views"/> as they last were.</summary>
    private (string Views, Fitted? Fit) _cache = (string.Empty, null);

    public LensCalibrationNode()
    {
        ImageIn = AddInput<ImageData>("In", isRequired: false);

        LensOut = AddOutput<LensModel>("Lens");
        CalibrationOut = AddOutput<Calibration>("Calibration");
        ErrorOut = AddOutput<double>("Error");
        ViewsOut = AddOutput<int>("Views");
        TargetOut = AddOutput<ShapeSet>("Target");
    }

    /// <summary>The camera's picture, with the target in it while calibrating.</summary>
    public InputPort ImageIn { get; }

    /// <summary>The lens, for Undistort. Nothing until a view has been added.</summary>
    public OutputPort LensOut { get; }

    /// <summary>What a pixel is worth in the target's plane, once the distortion is taken out.</summary>
    public OutputPort CalibrationOut { get; }

    /// <summary>
    /// How far, on average, the target's points lie from where the fitted
    /// lens puts them, in pixels. Well under one is a good calibration.
    /// </summary>
    public OutputPort ErrorOut { get; }

    /// <summary>How many views the lens is fitted to.</summary>
    public OutputPort ViewsOut { get; }

    /// <summary>The target found in the current picture, its corners and rows - so Add View is pressed knowing it was seen.</summary>
    public OutputPort TargetOut { get; }

    public IReadOnlyList<NodeAction> Actions =>
    [
        new NodeAction(
            "Add View",
            "Keep the target found in the current picture, and fit the lens to every view kept so far.",
            AddView),
        new NodeAction(
            "Clear Views",
            "Forget every view kept, to calibrate again.",
            ClearViews),
    ];

    [Param("Target", Group = "Target", Description = "A chessboard, or a grid of dark dots on a light ground.")]
    public CalibrationTarget Target
    {
        get => _target;
        set => SetParam(ref _target, value);
    }

    [Param("Columns", Group = "Target", Min = 2, Max = 100, Step = 1,
        Description = "Across the target: inner corners of a chessboard - one fewer than its squares - or dots.")]
    public int Columns
    {
        get => _columns;
        set => SetParam(ref _columns, Math.Clamp(value, 2, 100));
    }

    [Param("Rows", Group = "Target", Min = 2, Max = 100, Step = 1,
        Description = "Down the target: inner corners of a chessboard, or dots.")]
    public int Rows
    {
        get => _rows;
        set => SetParam(ref _rows, Math.Clamp(value, 2, 100));
    }

    [Param("Spacing", Group = "Target", Min = 0, Connectable = true,
        Description = "How far apart the target's corners or dots are, in the unit below - for Calibration.")]
    public double Spacing
    {
        get => _spacing;
        set => SetParam(ref _spacing, Math.Max(0, value));
    }

    [Param("Unit", Group = "Target", Description = "What Spacing, and so Calibration, is in: mm, in, um.")]
    public string Unit
    {
        get => _unit;
        set => SetParam(ref _unit, value ?? string.Empty);
    }

    [Param("Distortion", Group = "Lens",
        Description = "Radial: barrel or pincushion, the usual case. Radial and Tangential also fits a sensor not " +
                      "square to the lens, and needs more views.")]
    public DistortionModel Model
    {
        get => _model;
        set => SetParam(ref _model, value);
    }

    [Param("Find Target", Group = "Lens",
        Description = "Look for the target on every run, to show it and so Add View can keep it. Turn off once " +
                      "calibrated: it costs time on every picture.")]
    public bool FindTarget
    {
        get => _findTarget;
        set => SetParam(ref _findTarget, value);
    }

    /// <summary>The views kept, as the corners found in each - set by Add View.</summary>
    [Param("Views", Hidden = true)]
    public string Views
    {
        get => _views;
        set => SetParam(ref _views, value ?? string.Empty);
    }

    /// <summary>Keeps the target found on the latest run as a view, or says why it cannot.</summary>
    public string? AddView()
    {
        TargetView found;

        lock (_gate)
        {
            if (_lastFound is not { } last)
            {
                return _findTarget
                    ? "No target was found in the current picture. Check Columns and Rows count the inner corners " +
                      "(or dots), that the whole target is in view, and run the recipe."
                    : "Find Target is off, so nothing was looked for. Turn it on and run the recipe.";
            }

            found = last;
        }

        var views = Read(_views);

        if (views.Count > 0 && (views[0].Width != found.Width || views[0].Height != found.Height))
        {
            return $"This picture is {found.Width}x{found.Height}, and the views kept are " +
                   $"{views[0].Width}x{views[0].Height}. Clear Views to calibrate at the new size.";
        }

        views.Add(found);
        Views = JsonSerializer.Serialize(views);
        return null;
    }

    public string? ClearViews()
    {
        Views = string.Empty;
        return null;
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var image = context.Get<ImageData>(ImageIn);
        var shown = new List<Shape>();

        if (image is not null && _findTarget)
        {
            var found = Find(image, _target, _columns, _rows, context.CancellationToken);

            lock (_gate)
                _lastFound = found;

            if (found is not null)
                shown.AddRange(Drawn(found));
        }

        var fit = FitFor(_views);
        var views = fit?.Lens.Views ?? 0;

        context.Set(TargetOut, new ShapeSet(shown, image?.Width ?? 0, image?.Height ?? 0));
        context.Set(ViewsOut, views);

        if (fit is null)
        {
            context.Warn(_views.Length == 0
                ? "No views yet: hold the target in sight and press Add View."
                : "The lens could not be fitted to the views kept. Clear Views and add them again.");

            context.Set(LensOut, null);
            context.Set(CalibrationOut, Calibration.None);
            context.Set(ErrorOut, 0d);
            return;
        }

        if (fit.Lens.Error > 1)
            context.Warn($"The fit is {fit.Lens.Error:0.##} px off on average. Add views with the target sharp and " +
                         "flat, or clear any where it was not found cleanly.");

        var spacing = context.Param(nameof(Spacing), _spacing);

        context.Set(LensOut, fit.Lens);
        context.Set(
            CalibrationOut,
            spacing > 0 && fit.PixelsPerSpacing > 0
                ? Calibration.FromKnownLength(fit.PixelsPerSpacing, spacing, _unit)
                : Calibration.None);
        context.Set(ErrorOut, fit.Lens.Error);
    }

    /// <summary>One view of the target: its grid and the points found, in reading order.</summary>
    public sealed record TargetView(int Columns, int Rows, int Width, int Height, float[] Points);

    /// <summary>A fitted lens, and the target's spacing in pixels once corrected.</summary>
    private sealed record Fitted(LensModel Lens, double PixelsPerSpacing);

    private Fitted? FitFor(string views)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_cache.Views, views) || _cache.Views == views)
                return _cache.Fit;
        }

        var fit = Fit(Read(views), _model);

        lock (_gate)
            _cache = (views, fit);

        return fit;
    }

    private static List<TargetView> Read(string views)
    {
        if (string.IsNullOrWhiteSpace(views))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<TargetView>>(views) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The target in a picture, its points refined to a fraction of a
    /// pixel, or null when it is not there whole.
    /// </summary>
    public static TargetView? Find(
        ImageData image, CalibrationTarget target, int columns, int rows, CancellationToken cancellationToken)
    {
        using var grey = Grey(image.Mat);
        using var points = new VectorOfPointF();

        var size = new Size(columns, rows);
        bool found;

        cancellationToken.ThrowIfCancellationRequested();

        if (target == CalibrationTarget.DotGrid)
        {
            using var detector = new SimpleBlobDetector();
            found = CvInvoke.FindCirclesGrid(grey, size, points, CalibCgType.SymmetricGrid, detector);
        }
        else
        {
            found = CvInvoke.FindChessboardCorners(
                grey,
                size,
                points,
                CalibCbType.AdaptiveThresh | CalibCbType.NormalizeImage | CalibCbType.FastCheck);

            if (found)
            {
                CvInvoke.CornerSubPix(
                    grey, points, new Size(5, 5), new Size(-1, -1), new MCvTermCriteria(40, 0.001));
            }
        }

        if (!found || points.Size != columns * rows)
            return null;

        var array = points.ToArray();
        var flat = new float[array.Length * 2];

        for (var i = 0; i < array.Length; i++)
        {
            flat[2 * i] = array[i].X;
            flat[(2 * i) + 1] = array[i].Y;
        }

        return new TargetView(columns, rows, image.Width, image.Height, flat);
    }

    /// <summary>
    /// The lens fitted to <paramref name="views"/>, or null when there are
    /// none or the fit fails.
    /// </summary>
    /// <remarks>
    /// With fewer than three views the centre is held at the middle of the
    /// picture, the pixels square and the third radial term at nothing: one
    /// flat view cannot tell a longer lens from a nearer target, and a fit
    /// left free to try finds a confident wrong answer.
    /// </remarks>
    private static Fitted? Fit(IReadOnlyList<TargetView> views, DistortionModel model)
    {
        if (views.Count == 0)
            return null;

        var (width, height) = (views[0].Width, views[0].Height);

        var objectPoints = views
            .Select(view => Enumerable.Range(0, view.Columns * view.Rows)
                .Select(i => new MCvPoint3D32f(i % view.Columns, i / view.Columns, 0))
                .ToArray())
            .ToArray();

        var imagePoints = views.Select(PointsOf).ToArray();

        using var camera = new Matrix<double>(3, 3);
        camera.Data[0, 0] = Math.Max(width, height);
        camera.Data[1, 1] = Math.Max(width, height);
        camera.Data[0, 2] = width / 2.0;
        camera.Data[1, 2] = height / 2.0;
        camera.Data[2, 2] = 1;

        using var distortion = new Matrix<double>(1, 5);

        var flags = CalibType.UseIntrinsicGuess;

        if (model == DistortionModel.Radial)
            flags |= CalibType.ZeroTangentDist;

        if (views.Count < 3)
            flags |= CalibType.FixPrincipalPoint | CalibType.FixAspectRatio | CalibType.FixK3;

        double error;

        try
        {
            error = CvInvoke.CalibrateCamera(
                objectPoints,
                imagePoints,
                new Size(width, height),
                camera,
                distortion,
                flags,
                new MCvTermCriteria(100, 1e-9),
                out var rotations,
                out var translations);

            foreach (var mat in rotations.Concat(translations))
                mat.Dispose();
        }
        catch (CvException)
        {
            return null;
        }

        if (!double.IsFinite(error))
            return null;

        var lens = new LensModel(
            camera.Data[0, 0],
            camera.Data[1, 1],
            camera.Data[0, 2],
            camera.Data[1, 2],
            Enumerable.Range(0, 5).Select(i => distortion.Data[0, i]).ToArray(),
            width,
            height,
            error,
            views.Count);

        return new Fitted(lens, SpacingOf(views[^1], camera, distortion));
    }

    /// <summary>
    /// The mean distance between neighbouring points of a view, in pixels,
    /// once the lens's distortion is taken out of where they were found.
    /// </summary>
    private static double SpacingOf(TargetView view, Matrix<double> camera, Matrix<double> distortion)
    {
        using var found = new VectorOfPointF(PointsOf(view));
        using var corrected = new VectorOfPointF();

        // Back into pixels through the same camera, so the points are where
        // Undistort puts them.
        CvInvoke.UndistortPoints(found, corrected, camera, distortion, null, camera);

        var points = corrected.ToArray();
        var total = 0.0;
        var count = 0;

        for (var row = 0; row < view.Rows; row++)
        {
            for (var column = 0; column < view.Columns; column++)
            {
                var here = points[(row * view.Columns) + column];

                if (column + 1 < view.Columns)
                {
                    total += Distance(here, points[(row * view.Columns) + column + 1]);
                    count++;
                }

                if (row + 1 < view.Rows)
                {
                    total += Distance(here, points[((row + 1) * view.Columns) + column]);
                    count++;
                }
            }
        }

        return count == 0 ? 0 : total / count;
    }

    private static double Distance(PointF a, PointF b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static PointF[] PointsOf(TargetView view) =>
        Enumerable.Range(0, view.Points.Length / 2)
            .Select(i => new PointF(view.Points[2 * i], view.Points[(2 * i) + 1]))
            .ToArray();

    /// <summary>The target as found: each row as a line through its points, and the points.</summary>
    private static IEnumerable<Shape> Drawn(TargetView view)
    {
        var points = PointsOf(view).Select(point => new Point2(point.X, point.Y)).ToArray();

        for (var row = 0; row < view.Rows; row++)
        {
            yield return new PolygonShape(points.Skip(row * view.Columns).Take(view.Columns).ToArray(), Closed: false)
            {
                Colour = Row,
            };
        }

        foreach (var point in points)
            yield return new PointShape(point) { Colour = Corner };
    }

    /// <summary>An 8-bit single-channel copy, which is what the target finders read.</summary>
    internal static Mat Grey(Mat source)
    {
        var grey = new Mat();

        switch (source.NumberOfChannels)
        {
            case 3:
                CvInvoke.CvtColor(source, grey, ColorConversion.Bgr2Gray);
                break;
            case 4:
                CvInvoke.CvtColor(source, grey, ColorConversion.Bgra2Gray);
                break;
            default:
                source.CopyTo(grey);
                break;
        }

        if (grey.Depth != DepthType.Cv8U)
            CvInvoke.Normalize(grey, grey, 0, 255, NormType.MinMax, DepthType.Cv8U);

        return grey;
    }
}

/// <summary>
/// Takes a lens's distortion out of the picture, so straight edges are
/// straight and a pixel is worth the same everywhere.
/// </summary>
/// <remarks>
/// <para>
/// Ahead of anything that measures: a line found on the corrected picture
/// is straight because the part is, not bowed because the lens is. The
/// picture keeps its size; the lens's centre stays where it was, and the
/// corners - pulled in by a barrel lens - are filled with black where the
/// sensor saw nothing.
/// </para>
/// <para>
/// The correction is worked out once per lens and picture size and kept,
/// so after the first picture it costs one remap.
/// </para>
/// </remarks>
[VisionNode(
    "Undistort",
    TypeKey = "calibration.undistort",
    Category = NodeCategory.Calibration,
    Icon = "ph_undistort",
    Description = "Takes a lens's distortion out of the picture, using the lens from Lens Calibration.")]
public sealed class UndistortNode : VisionNode
{
    private readonly object _gate = new();

    private (LensModel Lens, Mat MapX, Mat MapY)? _maps;

    public UndistortNode()
    {
        ImageIn = AddInput<ImageData>("In");
        LensIn = AddInput<LensModel>("Lens", isRequired: false);
        ImageOut = AddOutput<ImageData>("Out");
    }

    public InputPort ImageIn { get; }

    /// <summary>The lens, from Lens Calibration. Without one the picture passes through.</summary>
    public InputPort LensIn { get; }

    public OutputPort ImageOut { get; }

    protected override void OnEvaluate(EvalContext context)
    {
        if (!context.TryGet<ImageData>(ImageIn, out var image))
            throw new InvalidOperationException("No image is connected.");

        if (context.Get<LensModel>(LensIn) is not { } lens)
        {
            context.Warn("No lens is wired in, or it has no views yet; the picture passes through uncorrected.");
            context.Forward(ImageOut, image);
            return;
        }

        if (lens.Width != image.Width || lens.Height != image.Height)
        {
            context.Warn($"The lens was calibrated on {lens.Width}x{lens.Height} pictures and this one is " +
                         $"{image.Width}x{image.Height}; it is scaled to match, which is right only for the same " +
                         "camera binned or resized.");
        }

        var scaled = lens.ScaledTo(image.Width, image.Height);
        var corrected = new Mat();

        lock (_gate)
        {
            var (mapX, mapY) = MapsFor(scaled);
            CvInvoke.Remap(image.Mat, corrected, mapX, mapY, Inter.Linear, BorderType.Constant, new MCvScalar(0));
        }

        context.Set(ImageOut, ImageData.Adopt(corrected, image.Space));
    }

    /// <summary>Where each corrected pixel comes from, for one lens at one size - kept until either changes.</summary>
    private (Mat MapX, Mat MapY) MapsFor(LensModel lens)
    {
        if (_maps is { } kept && kept.Lens.SameCorrection(lens))
            return (kept.MapX, kept.MapY);

        _maps?.MapX.Dispose();
        _maps?.MapY.Dispose();

        using var camera = new Matrix<double>(3, 3);
        camera.Data[0, 0] = lens.Fx;
        camera.Data[1, 1] = lens.Fy;
        camera.Data[0, 2] = lens.Cx;
        camera.Data[1, 2] = lens.Cy;
        camera.Data[2, 2] = 1;

        using var distortion = new Matrix<double>(1, lens.Distortion.Count);

        for (var i = 0; i < lens.Distortion.Count; i++)
            distortion.Data[0, i] = lens.Distortion[i];

        var mapX = new Mat();
        var mapY = new Mat();

        CvInvoke.InitUndistortRectifyMap(
            camera, distortion, null, camera, new Size(lens.Width, lens.Height), DepthType.Cv32F, 1, mapX, mapY);

        _maps = (lens, mapX, mapY);
        return (mapX, mapY);
    }
}
