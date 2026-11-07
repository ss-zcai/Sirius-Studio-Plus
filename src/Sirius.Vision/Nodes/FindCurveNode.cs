using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>
/// Finds an edge of any shape along a drawn path, and fits a smooth curve
/// through it.
/// </summary>
/// <remarks>
/// <para>
/// The third finder on the caliper core, after Find Line and Find Circle,
/// for the edges that are neither: a fillet, the lip of a moulding, a
/// cam profile, the outline of a part taken from a good one. A path is
/// drawn along where the edge should be, a band either side of it, and
/// calipers are cast square across the path at even spacing along it.
/// </para>
/// <para>
/// The curve has no formula to fit, so it is fitted locally: each point of
/// it is a short quadratic through the edge points within Fit Window
/// calipers either side - the same local form the defect tools measure
/// against. A point further off than Reject Above of the edge's own robust
/// spreads is set aside and the curve fitted again, so a caliper that
/// caught a scratch or a neighbouring feature does not bend it.
/// </para>
/// <para>
/// The path can be wired as well as drawn: a contour from a good part, a
/// blob's outline, a circle or a box, which is followed with the band
/// width drawn here. A closed path gives a closed curve.
/// </para>
/// </remarks>
[VisionNode(
    "Find Curve",
    TypeKey = "fitting.findcurve",
    Category = NodeCategory.Fitting,
    Icon = "ph_find_curve",
    Description = "Casts calipers along a drawn path and fits a smooth curve through the edge they find - for an " +
                  "edge that is neither a line nor a circle.")]
public sealed class FindCurveNode : VisionNode
{
    private const uint Found = 0xFF22C55E;

    private const uint Evidence = 0xFFFBBF24;

    private PathBand _path = PathBand.Default;
    private int _caliperCount = 60;
    private int _caliperWidth = 5;
    private double _spacing = 0.5;
    private int _smoothing = 5;
    private double _minimumContrast = 3;
    private EdgePolarity _polarity = EdgePolarity.Either;
    private EdgeChoice _choice = EdgeChoice.Strongest;
    private ScanDirection _direction = ScanDirection.Across;
    private int _fitWindow = 7;
    private double _rejectSigma = 3;

    public FindCurveNode()
    {
        ImageIn = AddInput<ImageData>("In");
        CurveOut = AddOutput<ShapeSet>("Curve");
        PointsOut = AddOutput<ShapeSet>("Points");
        ProfileOut = AddOutput<ProfileData>("Profile");
        CountOut = AddOutput<int>("Count");
        MissedOut = AddOutput<int>("Missed");
    }

    public InputPort ImageIn { get; }

    /// <summary>
    /// The fitted curve, from the first caliper that found the edge to the
    /// last - all the way round for a closed path - with its length and
    /// how well it fitted.
    /// </summary>
    public OutputPort CurveOut { get; }

    /// <summary>
    /// The edge points the curve was fitted to, after rejection, each with
    /// its distance along the path, its offset from it, its deviation from
    /// the curve and its contrast.
    /// </summary>
    public OutputPort PointsOut { get; }

    /// <summary>
    /// How far the edge is off the drawn path along its length: each point
    /// found, and the fitted curve. A gap where a caliper found nothing.
    /// </summary>
    public OutputPort ProfileOut { get; }

    /// <summary>How many points the curve was fitted to.</summary>
    public OutputPort CountOut { get; }

    /// <summary>How many calipers found no edge.</summary>
    public OutputPort MissedOut { get; }

    [Param("Path",
        Editor = ParamEditor.Path,
        Connectable = true,
        Description = "Where the edge should be, corner by corner, and how wide a band to search: " +
                      "\"x, y; x, y; ... | width\". Draw it on the image. A wired shape is followed instead.")]
    public PathBand Path
    {
        get => _path;
        set => SetParam(ref _path, value ?? PathBand.Default);
    }

    [Param("Calipers", Group = "Edge", Min = 3, Max = 2000, Step = 1,
        Description = "How many scans to cast along the path - one point at most from each.")]
    public int CaliperCount
    {
        get => _caliperCount;
        set => SetParam(ref _caliperCount, Math.Clamp(value, 3, 2000));
    }

    [Param("Caliper Width", Group = "Edge", Min = 1, Max = 101, Step = 2, OddOnly = true,
        Description = "Pixels averaged across each scan.")]
    public int CaliperWidth
    {
        get => _caliperWidth;
        set => SetParam(ref _caliperWidth, Math.Clamp(value | 1, 1, 101));
    }

    [Param("Sampling", Group = "Edge", Min = 0.1, Max = 2, Step = 0.1,
        Description = "Distance between samples along a scan, in pixels.")]
    public double Spacing
    {
        get => _spacing;
        set => SetParam(ref _spacing, Math.Clamp(value, 0.1, 2));
    }

    [Param("Smoothing", Group = "Edge", Min = 1, Max = 51, Step = 2, OddOnly = true,
        Description = "Samples averaged before the edge is measured.")]
    public int Smoothing
    {
        get => _smoothing;
        set => SetParam(ref _smoothing, Math.Clamp(value | 1, 1, 51));
    }

    [Param("Min Contrast", Group = "Edge", Min = 0, Max = 255, Step = 0.5, Intensity = true,
        Description = "Smallest intensity step counted as an edge.")]
    public double MinimumContrast
    {
        get => _minimumContrast;
        set => SetParam(ref _minimumContrast, Math.Max(0, value));
    }

    [Param("Polarity", Group = "Edge",
        Description = "Dark to light, light to dark, or strongest - along the scan direction.")]
    public EdgePolarity Polarity
    {
        get => _polarity;
        set => SetParam(ref _polarity, value);
    }

    [Param("Edge", Group = "Edge",
        Description = "Which edge to take when a scan crosses more than one.")]
    public EdgeChoice Choice
    {
        get => _choice;
        set => SetParam(ref _choice, value);
    }

    [Param("Direction", Group = "Edge",
        Description = "Which side of the path each scan starts from: Across starts on the left as the path " +
                      "was drawn, Back on the right. Polarity and Edge are relative to it.")]
    public ScanDirection Direction
    {
        get => _direction;
        set => SetParam(ref _direction, value);
    }

    [Param("Fit Window", Group = "Fit", Min = 3, Max = 101, Step = 2, OddOnly = true,
        Description = "How many calipers each point of the curve is fitted over. Wider is smoother; narrower " +
                      "follows tighter bends.")]
    public int FitWindow
    {
        get => _fitWindow;
        set => SetParam(ref _fitWindow, Math.Clamp(value | 1, 3, 101));
    }

    [Param("Reject Above", Group = "Fit", Min = 0, Max = 6, Step = 0.1,
        Description = "Set aside points this many of the edge's own spreads off the curve, then fit again. Zero " +
                      "keeps every point.")]
    public double RejectSigma
    {
        get => _rejectSigma;
        set => SetParam(ref _rejectSigma, Math.Clamp(value, 0, 6));
    }

    protected override void OnEvaluate(EvalContext context)
    {
        if (!context.TryGet<ImageData>(ImageIn, out var image))
            throw new InvalidOperationException("No image is connected.");

        var path = context.Param(nameof(Path), _path);

        if (!path.IsUsable)
            throw new InvalidOperationException("The path needs at least two points apart. Draw it on the image.");

        var stations = CaliperScan.Stations(path, _caliperCount);

        using var sampler = LineProfileSource.From(image);

        var edges = CaliperScan.AlongPath(
            sampler,
            path,
            stations,
            _direction,
            new CaliperSettings(
                _caliperCount, _caliperWidth, _spacing, _smoothing, _minimumContrast, _polarity, _choice),
            context.CancellationToken);

        var offsets = new double[_caliperCount];
        Array.Fill(offsets, double.NaN);

        foreach (var edge in edges)
            offsets[edge.Caliper] = edge.Across;

        if (edges.Count < 3)
        {
            context.Warn(edges.Count == 0
                ? "No edge was found along the path. Check the band covers the edge, and lower Min Contrast."
                : $"Only {edges.Count} edge points were found; a curve needs 3.");

            Publish(context, image, path, stations, edges, offsets, curve: null, kept: []);
            return;
        }

        var curve = EdgeForm.Follow(
            offsets, Math.Min(_fitWindow, _caliperCount | 1), path.Closed, floor: 0.5, sigma: _rejectSigma, bridge: true);

        var kept = Kept(edges, curve);

        if (kept.Count < _caliperCount / 2)
            context.Warn($"Only {kept.Count} of {_caliperCount} calipers found a usable edge.");

        Publish(context, image, path, stations, edges, offsets, curve, kept);
    }

    /// <summary>
    /// The edges the curve agrees with: within Reject Above of the edge's
    /// own robust spread of it, or half a pixel, whichever is wider.
    /// </summary>
    private List<EdgePoint> Kept(IReadOnlyList<EdgePoint> edges, double[] curve)
    {
        if (_rejectSigma <= 0)
            return edges.ToList();

        var residual = edges.Select(edge => Math.Abs(edge.Across - curve[edge.Caliper])).ToArray();
        var limit = Math.Max(0.5, _rejectSigma * 1.4826 * CircleDefectsNode.Median(residual));

        return edges.Where((_, i) => residual[i] <= limit).ToList();
    }

    private void Publish(
        EvalContext context,
        ImageData image,
        PathBand path,
        IReadOnlyList<PathStation> stations,
        IReadOnlyList<EdgePoint> edges,
        double[] offsets,
        double[]? curve,
        IReadOnlyList<EdgePoint> kept)
    {
        var (width, height) = (image.Width, image.Height);
        var curves = new List<Shape>();

        if (curve is not null && kept.Count > 0)
        {
            // An open curve runs from the first caliper that found the edge
            // to the last, as a fitted line spans only its points; beyond
            // them it would be the fit guessing.
            var (first, last) = path.Closed
                ? (0, _caliperCount - 1)
                : (kept.Min(edge => edge.Caliper), kept.Max(edge => edge.Caliper));

            var points = Enumerable.Range(first, last - first + 1)
                .Select(i => stations[i].Across(curve[i]))
                .ToArray();

            var error = Math.Sqrt(kept.Average(edge => Math.Pow(edge.Across - curve[edge.Caliper], 2)));

            curves.Add(new PolygonShape(points, path.Closed)
            {
                Colour = Found,
                StrokeWidth = 2,
                Metrics = new Dictionary<string, double>
                {
                    [ShapeMetric.CentroidX] = points.Average(point => point.X),
                    [ShapeMetric.CentroidY] = points.Average(point => point.Y),
                    [ShapeMetric.Length] = LengthOf(points, path.Closed),
                    [ShapeMetric.FitError] = error,
                    [ShapeMetric.FitPoints] = kept.Count,
                },
            });
        }

        var found = kept
            .Select(edge => (Shape)new PointShape(edge.At)
            {
                Colour = Evidence,
                Metrics = new Dictionary<string, double>
                {
                    [ShapeMetric.CentroidX] = edge.At.X,
                    [ShapeMetric.CentroidY] = edge.At.Y,
                    [ShapeMetric.Along] = edge.Along,
                    [ShapeMetric.Offset] = edge.Across,
                    [ShapeMetric.Deviation] = curve is null ? 0 : edge.Across - curve[edge.Caliper],
                    [ShapeMetric.Contrast] = Math.Abs(edge.Strength),
                },
            })
            .ToArray();

        context.Set(CurveOut, new ShapeSet(curves, width, height));
        context.Set(PointsOut, new ShapeSet(found, width, height));
        context.Set(ProfileOut, Profile(path, offsets, curve));
        context.Set(CountOut, kept.Count);
        context.Set(MissedOut, _caliperCount - edges.Count);
    }

    private ProfileData Profile(PathBand path, double[] offsets, double[]? curve)
    {
        var channels = new List<ProfileChannel>
        {
            new("Offset", offsets.Select(value => (float)value).ToArray()),
        };

        if (curve is not null)
            channels.Add(new ProfileChannel("Curve", curve.Select(value => (float)value).ToArray()));

        var step = path.Closed ? path.Length / _caliperCount : path.Length / Math.Max(1, _caliperCount - 1);

        return new ProfileData(channels, default, default, step)
        {
            ValueFormat = "0.###",
            GapLabel = "missed",
            IsLine = false,
        };
    }

    private static double LengthOf(IReadOnlyList<Point2> points, bool closed)
    {
        var length = 0.0;

        for (var i = 1; i < points.Count; i++)
            length += points[i - 1].DistanceTo(points[i]);

        return closed && points.Count > 2 ? length + points[^1].DistanceTo(points[0]) : length;
    }
}
