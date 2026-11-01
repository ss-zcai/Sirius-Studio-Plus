using System.Globalization;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>What Line Defects measures a straight edge against.</summary>
public enum LineDefectReference
{
    /// <summary>
    /// The line the most edge points agree with, found by a vote so that a
    /// large chip loses rather than tilting the line towards itself.
    /// </summary>
    Fitted,

    /// <summary>The line wired into Line - Find Line's, say - exactly as given.</summary>
    Wired,

    /// <summary>
    /// The edge's own shape, smoothed over Form Window: for an edge that is
    /// meant to be straight but bows a little, so only what departs from it
    /// sharply - a notch, a burr - is a defect.
    /// </summary>
    LocalForm,
}

/// <summary>
/// Inspects a straight edge for defects: where material is missing, where
/// there is extra, how deep and how long, against a tolerance.
/// </summary>
/// <remarks>
/// <para>
/// Circle Defects for an edge that is meant to be straight - a cut edge, a
/// sealed seam, the side of a moulding. Calipers are cast across a
/// rectangle, as Line Calipers does, and each edge point is measured
/// against a reference line: by default the one most of the edge agrees
/// with, chosen by a vote so that a chip covering a fifth of the edge does
/// not tilt the line towards itself and hide its own depth.
/// </para>
/// <para>
/// Which side of the edge is the part comes from Direction: each scan
/// starts outside the part and runs into it. An edge further into the part
/// than the inner tolerance is missing material; further out than the
/// outer tolerance, extra material. Neighbouring calipers out the same way
/// are one defect, its ends followed out while the edge is still more than
/// half the tolerance off; defects a caliper or two apart are joined; one
/// shorter than Min Length is noise. A caliper that found no edge is a
/// defect of its own unless it lies beside one, which it is then taken to
/// be part of - the rules Circle Defects follows, from the same code.
/// </para>
/// <para>
/// Each defect comes out as an outline between the edge and the line, red
/// for missing and blue for extra, carrying where along the edge it is,
/// how deep it goes, how long it runs and its area. The part passes when
/// there are none.
/// </para>
/// </remarks>
[VisionNode(
    "Line Defects",
    TypeKey = "judging.lineDefects",
    Category = NodeCategory.Judging,
    Icon = "ph_line_defects",
    Description = "Finds missing and extra material along a straight edge against a tolerance, and outlines " +
                  "each defect with its position, depth, length and area. Passes when there are none.")]
public sealed class LineDefectsNode : VisionNode, IJudgingNode
{
    private const uint Evidence = 0xFFFBBF24;
    private const uint ReferenceColour = 0xFF22C55E;
    private const uint ToleranceBand = 0x8022C55E;
    private const uint MissingColour = 0xFFEF4444;
    private const uint ExcessColour = 0xFF3B82F6;
    private const uint OutOfBandColour = 0xFFF97316;

    private OrientedRect _region = new(300, 200, 300, 60, 0);
    private double _bandWidth = 40;
    private double _offset;
    private int _caliperCount = 150;
    private int _caliperWidth = 3;
    private double _spacing = 0.5;
    private int _smoothing = 3;
    private double _minimumContrast = 10;
    private EdgePolarity _polarity = EdgePolarity.Either;
    private EdgeChoice _choice = EdgeChoice.First;
    private ScanDirection _direction = ScanDirection.Across;
    private LineDefectReference _reference = LineDefectReference.Fitted;
    private double _formWindow = 60;
    private double _innerTolerance = 2;
    private double _outerTolerance = 2;
    private double _minimumLength = 3;
    private int _mergeGap = 2;
    private int _decimals = 2;

    public LineDefectsNode()
    {
        ImageIn = AddInput<ImageData>("In");
        LineIn = AddInput<ShapeSet>("Line", isRequired: false);
        CalibrationIn = AddInput<Calibration>("Calibration", isRequired: false);

        DefectsOut = AddOutput<ShapeSet>("Defects");
        ReferenceOut = AddOutput<ShapeSet>("Reference");
        PointsOut = AddOutput<ShapeSet>("Points");
        ProfileOut = AddOutput<ProfileData>("Profile");
        CountOut = AddOutput<int>("Count");
        WorstOut = AddOutput<double>("Worst");
        FormOut = AddOutput<double>("Form");
        PassOut = AddOutput<bool>("Pass");
    }

    public InputPort ImageIn { get; }

    /// <summary>
    /// A found line to search along - Find Line's - and, with Reference set
    /// to Wired, to measure against.
    /// </summary>
    public InputPort LineIn { get; }

    /// <summary>What a pixel is worth. Tolerances, Min Length, Form Window and Worst are then in its unit.</summary>
    public InputPort CalibrationIn { get; }

    /// <summary>One outline per defect, between the edge and the line, with its measurements.</summary>
    public OutputPort DefectsOut { get; }

    /// <summary>The reference, and the tolerance either side of it.</summary>
    public OutputPort ReferenceOut { get; }

    /// <summary>Every edge point found, with its deviation from the reference.</summary>
    public OutputPort PointsOut { get; }

    /// <summary>Deviation along the edge, for a Line Chart; a gap where a caliper found no edge.</summary>
    public OutputPort ProfileOut { get; }

    /// <summary>How many defects.</summary>
    public OutputPort CountOut { get; }

    /// <summary>
    /// The largest deviation anywhere along the edge, signed - negative
    /// into the part. Read even when the part passes, as its margin.
    /// </summary>
    public OutputPort WorstOut { get; }

    /// <summary>
    /// How far the reference itself is from straight: zero against a line,
    /// the edge's bow against its local form.
    /// </summary>
    public OutputPort FormOut { get; }

    /// <summary>True when there are no defects.</summary>
    public OutputPort PassOut { get; }

    OutputPort IJudgingNode.VerdictPort => PassOut;

    // ---- where to look -------------------------------------------------

    [Param("Region",
        Editor = ParamEditor.Rect,
        Connectable = true,
        Description = "Search rectangle as x, y, w, h, angle, its long axis along the edge and wide enough to take " +
                      "the biggest burr and the deepest chip. A wired line replaces it.")]
    public OrientedRect Region
    {
        get => _region;
        set => SetParam(ref _region, value);
    }

    [Param("Band Width", Group = "Along a line", Min = 1, Max = 400, Step = 1, Connectable = true,
        Description = "With a line wired in: how wide a band to search, centred on it.")]
    public double BandWidth
    {
        get => _bandWidth;
        set => SetParam(ref _bandWidth, Math.Max(1, value));
    }

    [Param("Offset", Group = "Along a line", Min = -400, Max = 400, Step = 1, Connectable = true,
        Description = "With a line wired in: moves the band out of the part, or into it when negative.")]
    public double Offset
    {
        get => _offset;
        set => SetParam(ref _offset, value);
    }

    // ---- how to look ---------------------------------------------------

    [Param("Calipers", Group = "Edge", Min = 6, Max = 3000, Step = 1,
        Description = "How many scans along the region. The narrowest defect seen is about one caliper's " +
                      "spacing: the region's length over Calipers.")]
    public int CaliperCount
    {
        get => _caliperCount;
        set => SetParam(ref _caliperCount, Math.Clamp(value, 6, 3000));
    }

    [Param("Caliper Width", Group = "Edge", Min = 1, Max = 101, Step = 2, OddOnly = true,
        Description = "Pixels averaged across each scan. Narrow keeps a narrow defect sharp.")]
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

    [Param("Min Contrast", Group = "Edge", Min = 0, Max = 255, Step = 0.5,
        Description = "Smallest intensity step counted as an edge.")]
    public double MinimumContrast
    {
        get => _minimumContrast;
        set => SetParam(ref _minimumContrast, Math.Max(0, value));
    }

    [Param("Polarity", Group = "Edge",
        Description = "Dark to light, light to dark, or either - along the scan direction.")]
    public EdgePolarity Polarity
    {
        get => _polarity;
        set => SetParam(ref _polarity, value);
    }

    [Param("Edge", Group = "Edge",
        Description = "Which edge to take when a scan crosses more than one. First is the outermost material - " +
                      "the top of a burr, the floor of a chip.")]
    public EdgeChoice Choice
    {
        get => _choice;
        set => SetParam(ref _choice, value);
    }

    [Param("Direction", Group = "Edge",
        Description = "Which side each scan starts from. Start them outside the part: that is the side extra " +
                      "material stands out on, and the side missing material is measured from.")]
    public ScanDirection Direction
    {
        get => _direction;
        set => SetParam(ref _direction, value);
    }

    // ---- what is a defect ----------------------------------------------

    [Param("Reference", Group = "Tolerance",
        Description = "Fitted: the line most of the edge agrees with. Wired: the line wired in. Local Form: the " +
                      "edge's own shape, smoothed - for an edge that bows a little.")]
    public LineDefectReference ReferenceKind
    {
        get => _reference;
        set => SetParam(ref _reference, value);
    }

    [Param("Inner Tolerance", Group = "Tolerance", Min = 0, Step = 0.1, Connectable = true,
        Description = "How far into the part the edge may be before it is missing material.")]
    public double InnerTolerance
    {
        get => _innerTolerance;
        set => SetParam(ref _innerTolerance, Math.Max(0, value));
    }

    [Param("Outer Tolerance", Group = "Tolerance", Min = 0, Step = 0.1, Connectable = true,
        Description = "How far out of the part the edge may be before it is extra material.")]
    public double OuterTolerance
    {
        get => _outerTolerance;
        set => SetParam(ref _outerTolerance, Math.Max(0, value));
    }

    [Param("Min Length", Group = "Tolerance", Min = 0, Step = 0.5,
        Description = "How much of the edge a defect must cover to count. Shorter is taken as noise.")]
    public double MinimumLength
    {
        get => _minimumLength;
        set => SetParam(ref _minimumLength, Math.Max(0, value));
    }

    [Param("Form Window", Group = "Tolerance", Min = 5, Step = 1,
        Description = "With Reference set to Local Form: how much of the edge the form is smoothed over. Make it " +
                      "about three times the widest defect, and shorter than the edge's own bow.")]
    public double FormWindow
    {
        get => _formWindow;
        set => SetParam(ref _formWindow, Math.Max(1, value));
    }

    [Param("Merge Gap", Group = "Tolerance", Min = 0, Max = 20, Step = 1,
        Description = "Two defects of one kind this many calipers apart, or fewer, are one.")]
    public int MergeGap
    {
        get => _mergeGap;
        set => SetParam(ref _mergeGap, Math.Clamp(value, 0, 100));
    }

    [Param("Decimals", Min = 0, Max = 6, Step = 1, Description = "Figures after the point in the labels.")]
    public int Decimals
    {
        get => _decimals;
        set => SetParam(ref _decimals, Math.Clamp(value, 0, 6));
    }

    // ---- the inspection --------------------------------------------------

    /// <summary>
    /// The reference along the calipers: for each, where it crosses that
    /// caliper - in the region's own frame, pixels across from its middle -
    /// and how steeply it runs there.
    /// </summary>
    /// <param name="Across">Per caliper, where the reference crosses it.</param>
    /// <param name="Slope">Per caliper, how far across the reference moves per pixel along.</param>
    /// <param name="Drawn">The reference as a shape, carrying its measurements.</param>
    /// <param name="IsStraight">A line, so drawn as one and with no form to report.</param>
    private sealed record ReferenceLine(double[] Across, double[] Slope, Shape Drawn, bool IsStraight);

    protected override void OnEvaluate(EvalContext context)
    {
        if (!context.TryGet<ImageData>(ImageIn, out var image))
            throw new InvalidOperationException("No image is connected.");

        var calibration = context.GetValue<Calibration>(CalibrationIn) ?? Calibration.None;
        var inner = calibration.ToPixels(context.Param(nameof(InnerTolerance), _innerTolerance));
        var outer = calibration.ToPixels(context.Param(nameof(OuterTolerance), _outerTolerance));

        var region = RegionFor(context);

        using var sampler = LineProfileSource.From(image);

        var edges = CaliperScan.AcrossRect(
            sampler,
            region,
            _direction,
            new CaliperSettings(
                _caliperCount, _caliperWidth, _spacing, _smoothing, _minimumContrast, _polarity, _choice),
            context.CancellationToken);

        var (width, height) = (image.Width, image.Height);
        var step = Step(region);

        var across = new double[_caliperCount];
        Array.Fill(across, double.NaN);

        var byCaliper = new EdgePoint?[_caliperCount];

        foreach (var edge in edges)
        {
            across[edge.Caliper] = edge.Across;
            byCaliper[edge.Caliper] = edge;
        }

        if (ReferenceFor(context, region, edges, across, calibration, inner, outer, step) is not { } reference)
        {
            context.Set(DefectsOut, new ShapeSet([], width, height));
            context.Set(ReferenceOut, new ShapeSet([], width, height));
            context.Set(PointsOut, new ShapeSet(
                edges.Select(edge => (Shape)CaliperScan.LinePoint(edge, Evidence)).ToArray(), width, height));
            context.Set(ProfileOut, Profile(Enumerable.Repeat(double.NaN, _caliperCount).ToArray(), null, step, calibration));
            context.Set(CountOut, 0);
            context.Set(WorstOut, 0d);
            context.Set(FormOut, 0d);
            context.Set(PassOut, false);
            return;
        }

        // Deviation per caliper, in pixels square to the reference: positive
        // out of the part, towards where the scans start. NaN where no edge
        // was found.
        var deviation = new double[_caliperCount];

        for (var i = 0; i < _caliperCount; i++)
            deviation[i] = (across[i] - reference.Across[i]) * Outward * Square(reference.Slope[i]);

        var minimumCalipers = Math.Max(
            1, (int)Math.Ceiling(calibration.ToPixels(_minimumLength) / step - 1e-9));

        var runs = CircleDefectsNode.FindRuns(deviation, inner, outer, _mergeGap, minimumCalipers, wraps: false);

        var defects = runs
            .Select(run => Outline(run, region, deviation, across, byCaliper, reference, step, calibration))
            .ToArray();

        var finite = deviation.Where(double.IsFinite).ToArray();
        var worst = finite.Length == 0 ? 0 : finite.MaxBy(value => Math.Abs(value));

        var points = edges
            .Select(edge => (Shape)new PointShape(edge.At)
            {
                Colour = Evidence,
                Metrics = new Dictionary<string, double>
                {
                    [ShapeMetric.CentroidX] = edge.At.X,
                    [ShapeMetric.CentroidY] = edge.At.Y,
                    [ShapeMetric.Along] = edge.Along,
                    [ShapeMetric.Offset] = edge.Across,
                    [ShapeMetric.Deviation] = deviation[edge.Caliper],
                    [ShapeMetric.Contrast] = Math.Abs(edge.Strength),
                },
            })
            .ToArray();

        var form = reference.IsStraight ? null : FormOf(region, reference, step);

        context.Set(DefectsOut, new ShapeSet(defects, width, height));
        context.Set(ReferenceOut, new ShapeSet(ReferenceShapes(region, reference, inner, outer, step), width, height));
        context.Set(PointsOut, new ShapeSet(points, width, height));
        context.Set(ProfileOut, Profile(deviation, form, step, calibration));
        context.Set(CountOut, defects.Length);
        context.Set(WorstOut, calibration.ToUnits(worst));
        context.Set(FormOut, form is null ? 0d : calibration.ToUnits(form.Max() - form.Min()));
        context.Set(PassOut, defects.Length == 0);
    }

    /// <summary>Which way across the region is out of the part: the side the scans start from.</summary>
    private double Outward => _direction == ScanDirection.Across ? -1 : 1;

    /// <summary>Pixels along the region between neighbouring calipers.</summary>
    private double Step(OrientedRect region) => region.Width / Math.Max(1, _caliperCount - 1);

    /// <summary>Where caliper <paramref name="index"/> crosses the region's long axis, from its centre.</summary>
    private double AlongOf(OrientedRect region, int index) => (-region.Width / 2) + (index * Step(region));

    /// <summary>
    /// What a distance along a caliper is square to a reference running
    /// at <paramref name="slope"/> across it: the calipers cross the
    /// region, not the edge, and a tilted edge is crossed at a slant.
    /// </summary>
    private static double Square(double slope) => 1 / Math.Sqrt(1 + (slope * slope));

    /// <summary>
    /// The region this run searches: the drawn one, or one along a wired
    /// line - its length, Band Width wide, moved Offset out of the part.
    /// </summary>
    internal OrientedRect RegionFor(EvalContext context)
    {
        var drawn = context.Param(nameof(Region), _region);

        if (context.Get<ShapeSet>(LineIn) is not { Count: > 0 } shapes)
            return drawn;

        if (shapes.Count > 1)
            context.Warn($"{shapes.Count} shapes arrived on Line; the band follows the first.");

        // Turned to face the way the drawn region does, so which side is
        // the part does not depend on which way round the line was found.
        var along = (OrientedRect)ShapeRegions.From(shapes.Shapes[0], typeof(OrientedRect), drawn);

        if (shapes.Shapes[0] is not LineShape)
            return along;

        var offset = context.Param(nameof(Offset), _offset) * Outward;
        var shifted = CaliperScan.InRect(along, 0, offset);

        return new OrientedRect(
            shifted.X, shifted.Y, along.Width, context.Param(nameof(BandWidth), _bandWidth), along.Angle);
    }

    /// <summary>The reference for this run, or null - having said why - when there is none.</summary>
    private ReferenceLine? ReferenceFor(
        EvalContext context,
        OrientedRect region,
        IReadOnlyList<EdgePoint> edges,
        double[] across,
        Calibration calibration,
        double inner,
        double outer,
        double step)
    {
        if (_reference == LineDefectReference.Wired)
        {
            if (context.Get<ShapeSet>(LineIn)?.Shapes.FirstOrDefault() is LineShape wired && wired.Length > 0)
            {
                var direction = new Point2((wired.End.X - wired.Start.X) / wired.Length, (wired.End.Y - wired.Start.Y) / wired.Length);
                return Straight(context, region, new LineFitResult(wired.Start, direction.X, direction.Y, 0, []), error: double.NaN, used: 0);
            }

            context.Warn("Reference is Wired, but no line is wired into Line; fitting one instead.");
        }

        const int needed = 3;

        if (edges.Count < needed)
        {
            context.Warn(edges.Count == 0
                ? "No edge was found in the region. Check it spans the edge, and lower Min Contrast."
                : $"Only {edges.Count} edge points were found; the reference needs {needed}.");
            return null;
        }

        // Agreeing within the tolerance is what not being a defect means,
        // so it is the right test for which points a fit should follow.
        var agree = Math.Max(Math.Min(inner, outer), 0.5);

        if (_reference == LineDefectReference.LocalForm)
        {
            var window = Math.Max(3, (int)Math.Round(calibration.ToPixels(_formWindow) / step) | 1);
            var baseline = EdgeForm.Follow(across, window, wraps: false, floor: agree / 2, sigma: 3);

            var slope = new double[_caliperCount];

            for (var i = 0; i < _caliperCount; i++)
            {
                var (before, after) = (Math.Max(0, i - 1), Math.Min(_caliperCount - 1, i + 1));
                slope[i] = after == before ? 0 : (baseline[after] - baseline[before]) / ((after - before) * step);
            }

            var outline = baseline
                .Select((value, i) => CaliperScan.InRect(region, AlongOf(region, i), value))
                .ToArray();

            return new ReferenceLine(baseline, slope, new PolygonShape(outline, Closed: false), IsStraight: false);
        }

        var fit = LineFit.FitConsensus(edges.Select(edge => edge.At).ToArray(), agree);

        if (!fit.Success)
        {
            context.Warn("The edge points do not describe a line.");
            return null;
        }

        return Straight(context, region, fit, fit.Error, fit.Used.Count);
    }

    /// <summary>A line as a reference: where it crosses each caliper.</summary>
    private ReferenceLine? Straight(EvalContext context, OrientedRect region, LineFitResult line, double error, int used)
    {
        var radians = region.Angle * Math.PI / 180;
        var (ux, uy) = (Math.Cos(radians), Math.Sin(radians));
        var (vx, vy) = (-uy, ux);
        var (nx, ny) = (-line.DirectionY, line.DirectionX);

        var nv = (nx * vx) + (ny * vy);

        if (Math.Abs(nv) < 1e-6)
        {
            context.Warn("The reference line runs along the calipers rather than across them; turn the region to lie along the edge.");
            return null;
        }

        var slope = -((nx * ux) + (ny * uy)) / nv;
        var across = new double[_caliperCount];

        for (var i = 0; i < _caliperCount; i++)
        {
            var along = AlongOf(region, i);
            var ox = region.CentreX + (along * ux) - line.Point.X;
            var oy = region.CentreY + (along * uy) - line.Point.Y;

            across[i] = -((nx * ox) + (ny * oy)) / nv;
        }

        var start = CaliperScan.InRect(region, AlongOf(region, 0), across[0]);
        var end = CaliperScan.InRect(region, AlongOf(region, _caliperCount - 1), across[^1]);

        var metrics = new Dictionary<string, double>
        {
            [ShapeMetric.CentroidX] = (start.X + end.X) / 2,
            [ShapeMetric.CentroidY] = (start.Y + end.Y) / 2,
            [ShapeMetric.Length] = start.DistanceTo(end),
            [ShapeMetric.Angle] = Math.Atan2(end.Y - start.Y, end.X - start.X) * 180 / Math.PI,
        };

        if (!double.IsNaN(error))
        {
            metrics[ShapeMetric.FitError] = error;
            metrics[ShapeMetric.FitPoints] = used;
        }

        return new ReferenceLine(
            across,
            Enumerable.Repeat(slope, _caliperCount).ToArray(),
            new LineShape(start, end) { Metrics = metrics },
            IsStraight: true);
    }

    /// <summary>
    /// How far the reference bows: each caliper's distance off the straight
    /// line through the whole reference, out of the part positive.
    /// </summary>
    private double[] FormOf(OrientedRect region, ReferenceLine reference, double step)
    {
        var points = reference.Across
            .Select((value, i) => CaliperScan.InRect(region, AlongOf(region, i), value))
            .ToArray();

        var line = LineFit.Fit(points);

        if (!line.Success)
            return new double[_caliperCount];

        // Signed as the deviation is: out of the part, which is the side
        // the region's Across direction starts from.
        var radians = region.Angle * Math.PI / 180;
        var (vx, vy) = (-Math.Sin(radians), Math.Cos(radians));
        var (nx, ny) = (-line.DirectionY, line.DirectionX);

        if (((nx * vx) + (ny * vy)) * Outward < 0)
            (nx, ny) = (-nx, -ny);

        return points
            .Select(point => ((point.X - line.Point.X) * nx) + ((point.Y - line.Point.Y) * ny))
            .ToArray();
    }

    /// <summary>
    /// A defect as a shape: the region between the edge and the reference,
    /// with its measurements.
    /// </summary>
    private PolygonShape Outline(
        DefectRun run,
        OrientedRect region,
        double[] deviation,
        double[] across,
        EdgePoint?[] byCaliper,
        ReferenceLine reference,
        double step,
        Calibration calibration)
    {
        var edgeSide = new List<Point2>();
        var referenceSide = new List<Point2>();
        var area = 0.0;
        var length = 0.0;
        var worst = double.NaN;
        var worstAt = default(Point2);

        // How far the band reaches into and out of the part, for a caliper
        // that found nothing: as far as can be said of where the edge is.
        var halfBand = region.Height / 2;
        var outOf = Outward * halfBand;
        var into = -outOf;

        for (var index = run.Start; index < run.Start + run.Length; index++)
        {
            var along = AlongOf(region, index);

            referenceSide.Add(CaliperScan.InRect(region, along, reference.Across[index]));
            length += step / Square(reference.Slope[index]);

            if (byCaliper[index] is { } edge)
            {
                edgeSide.Add(edge.At);

                // A strip a caliper's spacing long and as deep as the edge is
                // off the reference along the caliper - exact for a slanted
                // strip as for a square one.
                area += Math.Abs(across[index] - reference.Across[index]) * step;

                if (double.IsNaN(worst) || Math.Abs(deviation[index]) > Math.Abs(worst))
                {
                    worst = deviation[index];
                    worstAt = edge.At;
                }
            }
            else
            {
                var reach = run.Kind == DefectKind.Missing ? into : outOf;
                edgeSide.Add(CaliperScan.InRect(region, along, reach));
            }
        }

        var points = edgeSide.Concat(Enumerable.Reverse(referenceSide)).ToArray();

        if (double.IsNaN(worst))
            worstAt = edgeSide[edgeSide.Count / 2];

        var name = run.Kind switch
        {
            DefectKind.Missing => "Missing",
            DefectKind.Excess => "Excess",
            _ => "No edge",
        };

        var unit = calibration.IsCalibrated ? " " + calibration.Unit : " px";
        var label = double.IsNaN(worst)
            ? name
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1:F" + _decimals + "}{2}",
                name,
                Math.Abs(calibration.ToUnits(worst)),
                unit);

        var metrics = new Dictionary<string, double>
        {
            [ShapeMetric.CentroidX] = worstAt.X,
            [ShapeMetric.CentroidY] = worstAt.Y,
            [ShapeMetric.Along] = (run.Start + ((run.Length - 1) / 2.0)) * step,
            [ShapeMetric.StartAlong] = run.Start * step,
            [ShapeMetric.EndAlong] = (run.Start + run.Length - 1) * step,
            [ShapeMetric.Length] = length,
            [ShapeMetric.Area] = area,
        };

        if (!double.IsNaN(worst))
            metrics[ShapeMetric.Deviation] = worst;

        return new PolygonShape(points, Closed: true)
        {
            Colour = run.Kind switch
            {
                DefectKind.Missing => MissingColour,
                DefectKind.Excess => ExcessColour,
                _ => OutOfBandColour,
            },
            StrokeWidth = 2,
            Label = label,
            Metrics = metrics,
        };
    }

    /// <summary>The reference in green, with thin lines where the tolerances lie either side of it.</summary>
    private IReadOnlyList<Shape> ReferenceShapes(OrientedRect region, ReferenceLine reference, double inner, double outer, double step)
    {
        var shapes = new List<Shape> { reference.Drawn with { Colour = ReferenceColour, StrokeWidth = 2 } };

        foreach (var tolerance in new[] { outer, -inner })
        {
            if (tolerance == 0)
                continue;

            // Square to the reference, which along a caliper is a little
            // further when the reference slants across it.
            var shifted = reference.Across
                .Select((value, i) => CaliperScan.InRect(
                    region, AlongOf(region, i), value + (Outward * tolerance / Square(reference.Slope[i]))))
                .ToArray();

            shapes.Add(reference.IsStraight
                ? new LineShape(shifted[0], shifted[^1]) { Colour = ToleranceBand, StrokeWidth = 1 }
                : new PolygonShape(shifted, Closed: false) { Colour = ToleranceBand, StrokeWidth = 1 });
        }

        return shapes;
    }

    /// <summary>Deviation along the edge - and, against the local form, how far the form bows.</summary>
    private ProfileData Profile(double[] deviation, double[]? form, double step, Calibration calibration)
    {
        static float[] Samples(IEnumerable<double> values, Calibration calibration) => values
            .Select(value => double.IsNaN(value) ? float.NaN : (float)calibration.ToUnits(value))
            .ToArray();

        var channels = new List<ProfileChannel> { new("Deviation", Samples(deviation, calibration)) };

        if (form is not null)
            channels.Add(new ProfileChannel("Form", Samples(form, calibration)));

        return new ProfileData(channels, default, default, step)
        {
            ValueFormat = "0.###",
            GapLabel = "missed",
            IsLine = false,
        };
    }
}
