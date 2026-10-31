using System.Globalization;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>What is wrong with a stretch of a circular edge.</summary>
public enum DefectKind
{
    /// <summary>Material missing: the edge lies inside the circle by more than the inner tolerance - a chip, a notch.</summary>
    Missing = -1,

    /// <summary>
    /// No edge found: the calipers there crossed nothing in the band. On
    /// its own, a defect too big for the band; beside a missing or excess
    /// stretch, it is taken as part of that one.
    /// </summary>
    OutOfBand = 0,

    /// <summary>Material extra: the edge lies outside the circle by more than the outer tolerance - a burr, a flash.</summary>
    Excess = 1,
}

/// <summary>What Circle Defects measures the edge against.</summary>
public enum DefectReference
{
    /// <summary>
    /// The circle the most edge points agree with, found by RANSAC so that a
    /// large defect loses the vote rather than dragging the circle towards
    /// itself.
    /// </summary>
    Fitted,

    /// <summary>
    /// A known radius, about the fitted centre - so a part that is round
    /// but the wrong size fails all the way round.
    /// </summary>
    Nominal,

    /// <summary>The circle wired into Circle, exactly as given.</summary>
    Wired,

    /// <summary>
    /// The edge's own shape, smoothed over Form Window: what a part that is
    /// not quite round is meant to look like, so only what departs from it
    /// sharply - a notch, a burr - is a defect.
    /// </summary>
    LocalForm,

    /// <summary>An ellipse fitted to the edge, the points furthest off it set aside - for a part that is oval.</summary>
    FittedEllipse,
}

/// <summary>
/// One stretch of an edge out of tolerance, in caliper positions.
/// </summary>
/// <param name="Kind">Missing, excess, or no edge found.</param>
/// <param name="Start">The first caliper in it.</param>
/// <param name="Length">How many calipers it covers; it may wrap past the last into the first.</param>
public readonly record struct DefectRun(DefectKind Kind, int Start, int Length);

/// <summary>
/// Inspects a circular edge for defects: where material is missing, where
/// there is extra, how deep and how long, against a tolerance.
/// </summary>
/// <remarks>
/// <para>
/// Calipers cast round a ring, as Ring Calipers does, measured against a
/// reference circle - by default the one most of the edge agrees with,
/// fitted by RANSAC so that a notch or a burr covering a good part of the
/// edge does not pull the circle towards itself and hide its own depth.
/// </para>
/// <para>
/// A caliper whose edge lies further inside the circle than the inner
/// tolerance is missing material; further outside than the outer
/// tolerance, extra material. Neighbouring calipers out of tolerance the
/// same way are one defect, its ends followed out while the edge is still
/// more than half the tolerance off; defects a caliper or two apart are
/// joined; one shorter than Min Length is noise. A caliper that found no
/// edge at all is a defect of its own - a burr or a chip too big for the
/// band - unless it lies beside one, which it is then taken to be part of.
/// </para>
/// <para>
/// Each defect comes out as an outline between the edge and the circle,
/// red for missing and blue for extra, carrying where it is (its angle
/// and its worst point), how far it goes (its deviation), how long it runs
/// and its area. The part passes when there are none.
/// </para>
/// </remarks>
[VisionNode(
    "Circle Defects",
    TypeKey = "judging.circleDefects",
    Category = NodeCategory.Judging,
    Icon = "ph_circle_defects",
    Description = "Finds missing and extra material round a circular edge against a tolerance, and outlines " +
                  "each defect with its angle, depth, length and area. Passes when there are none.")]
public sealed class CircleDefectsNode : VisionNode, IJudgingNode
{
    private const uint Evidence = 0xFFFBBF24;
    private const uint Reference = 0xFF22C55E;
    private const uint ToleranceBand = 0x8022C55E;
    private const uint MissingColour = 0xFFEF4444;
    private const uint ExcessColour = 0xFF3B82F6;
    private const uint OutOfBandColour = 0xFFF97316;

    private Ring _band = new(500, 500, 80, 140);
    private double _bandWidth = 40;
    private double _offset;
    private double _startAngle;
    private double _sweepAngle = 360;
    private int _caliperCount = 360;
    private int _caliperWidth = 3;
    private double _spacing = 0.5;
    private int _smoothing = 3;
    private double _minimumContrast = 10;
    private EdgePolarity _polarity = EdgePolarity.Either;
    private EdgeChoice _choice = EdgeChoice.First;
    private RadialDirection _direction = RadialDirection.Inward;
    private DefectReference _reference = DefectReference.LocalForm;
    private double _formWindow = 15;
    private double _nominalRadius = 100;
    private double _innerTolerance = 2;
    private double _outerTolerance = 2;
    private double _minimumLength = 2;
    private int _mergeGap = 2;
    private int _decimals = 2;

    public CircleDefectsNode()
    {
        ImageIn = AddInput<ImageData>("In");
        CircleIn = AddInput<ShapeSet>("Circle", isRequired: false);
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

    /// <summary>A found circle to search round - and, with Reference set to Wired, to measure against.</summary>
    public InputPort CircleIn { get; }

    /// <summary>What a pixel is worth. Tolerances, Nominal Radius and Worst are then in its unit.</summary>
    public InputPort CalibrationIn { get; }

    /// <summary>One outline per defect, between the edge and the circle, with its measurements.</summary>
    public OutputPort DefectsOut { get; }

    /// <summary>The reference circle, and the tolerance band either side of it.</summary>
    public OutputPort ReferenceOut { get; }

    /// <summary>Every edge point found, with its deviation from the reference circle.</summary>
    public OutputPort PointsOut { get; }

    /// <summary>Deviation against angle, for a Line Chart; a gap where a caliper found no edge.</summary>
    public OutputPort ProfileOut { get; }

    /// <summary>How many defects.</summary>
    public OutputPort CountOut { get; }

    /// <summary>
    /// The largest deviation anywhere round the edge, signed - negative
    /// inside the circle. Read even when the part passes, as its margin.
    /// </summary>
    public OutputPort WorstOut { get; }

    /// <summary>True when there are no defects.</summary>
    public OutputPort PassOut { get; }

    /// <summary>
    /// How far the reference itself is from round: its largest radius less
    /// its smallest. Zero against a circle; the part's out-of-roundness
    /// against an ellipse or its local form - for a Limit Check of its own,
    /// apart from the defects.
    /// </summary>
    public OutputPort FormOut { get; }

    OutputPort IJudgingNode.VerdictPort => PassOut;

    // ---- where to look -------------------------------------------------

    [Param("Band",
        Editor = ParamEditor.Ring,
        Connectable = true,
        Description = "Search band as x, y, inner, outer - wide enough to take the biggest burr and the deepest " +
                      "chip. A wired circle replaces it.")]
    public Ring Band
    {
        get => _band;
        set => SetParam(ref _band, value);
    }

    [Param("Band Width", Group = "Around a circle", Min = 1, Max = 400, Step = 1, Connectable = true,
        Description = "With a circle wired in: how wide a band to search, centred on its edge.")]
    public double BandWidth
    {
        get => _bandWidth;
        set => SetParam(ref _bandWidth, Math.Max(1, value));
    }

    [Param("Offset", Group = "Around a circle", Min = -400, Max = 400, Step = 1, Connectable = true,
        Description = "With a circle wired in: moves the band outward from its edge, or inward when negative.")]
    public double Offset
    {
        get => _offset;
        set => SetParam(ref _offset, value);
    }

    [Param("Start Angle", Group = "Ring", Min = -360, Max = 360, Step = 1,
        Description = "Degrees clockwise from the positive X axis.")]
    public double StartAngle
    {
        get => _startAngle;
        set => SetParam(ref _startAngle, value);
    }

    [Param("Sweep", Group = "Ring", Min = 1, Max = 360, Step = 1,
        Description = "How much of the ring to inspect.")]
    public double SweepAngle
    {
        get => _sweepAngle;
        set => SetParam(ref _sweepAngle, Math.Clamp(value, 1, 360));
    }

    // ---- how to look ---------------------------------------------------

    [Param("Calipers", Group = "Edge", Min = 12, Max = 3600, Step = 1,
        Description = "How many radial scans. The narrowest defect seen is about one caliper's spacing along " +
                      "the edge: 2 pi r / Calipers.")]
    public int CaliperCount
    {
        get => _caliperCount;
        set => SetParam(ref _caliperCount, Math.Clamp(value, 12, 3600));
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
        Description = "Which edge to take when a scan crosses more than one. First, scanning inward, is the " +
                      "outermost material - the top of a burr, the floor of a chip.")]
    public EdgeChoice Choice
    {
        get => _choice;
        set => SetParam(ref _choice, value);
    }

    [Param("Direction", Group = "Edge",
        Description = "Which end of the band each scan starts from.")]
    public RadialDirection Direction
    {
        get => _direction;
        set => SetParam(ref _direction, value);
    }

    // ---- what is a defect ----------------------------------------------

    [Param("Reference", Group = "Tolerance",
        Description = "Local Form: the edge's own shape, smoothed - for a part that is not quite round. Fitted: the " +
                      "circle most of the edge agrees with. Nominal: a known radius about the fitted centre. Wired: " +
                      "the circle wired in. Fitted Ellipse: for an oval part.")]
    public DefectReference ReferenceKind
    {
        get => _reference;
        set => SetParam(ref _reference, value);
    }

    [Param("Nominal Radius", Group = "Tolerance", Min = 0, Connectable = true,
        Description = "With Reference set to Nominal: the radius the part should have, in pixels or the " +
                      "Calibration's unit.")]
    public double NominalRadius
    {
        get => _nominalRadius;
        set => SetParam(ref _nominalRadius, Math.Max(0, value));
    }

    [Param("Inner Tolerance", Group = "Tolerance", Min = 0, Step = 0.1, Connectable = true,
        Description = "How far inside the circle the edge may be before it is missing material.")]
    public double InnerTolerance
    {
        get => _innerTolerance;
        set => SetParam(ref _innerTolerance, Math.Max(0, value));
    }

    [Param("Outer Tolerance", Group = "Tolerance", Min = 0, Step = 0.1, Connectable = true,
        Description = "How far outside the circle the edge may be before it is extra material.")]
    public double OuterTolerance
    {
        get => _outerTolerance;
        set => SetParam(ref _outerTolerance, Math.Max(0, value));
    }

    [Param("Min Length", Group = "Tolerance", Min = 0, Max = 90, Step = 0.5,
        Description = "Degrees of edge a defect must cover to count. Shorter is taken as noise.")]
    public double MinimumLength
    {
        get => _minimumLength;
        set => SetParam(ref _minimumLength, Math.Clamp(value, 0, 360));
    }

    [Param("Form Window", Group = "Tolerance", Min = 2, Max = 180, Step = 1,
        Description = "With Reference set to Local Form: degrees of edge the form is smoothed over. Make it about " +
                      "three times the widest defect, and narrower than the part's own waves.")]
    public double FormWindow
    {
        get => _formWindow;
        set => SetParam(ref _formWindow, Math.Clamp(value, 2, 180));
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

    protected override void OnEvaluate(EvalContext context)
    {
        if (!context.TryGet<ImageData>(ImageIn, out var image))
            throw new InvalidOperationException("No image is connected.");

        var calibration = context.GetValue<Calibration>(CalibrationIn) ?? Calibration.None;
        var inner = calibration.ToPixels(context.Param(nameof(InnerTolerance), _innerTolerance));
        var outer = calibration.ToPixels(context.Param(nameof(OuterTolerance), _outerTolerance));

        var band = RingCalipersNode.BandAround(
            context,
            context.Param(nameof(Band), _band),
            CircleIn,
            context.Param(nameof(BandWidth), _bandWidth),
            context.Param(nameof(Offset), _offset));

        using var sampler = LineProfileSource.From(image);

        var edges = CaliperScan.AroundRing(
            sampler,
            band,
            _startAngle,
            _sweepAngle,
            _direction,
            new CaliperSettings(
                _caliperCount, _caliperWidth, _spacing, _smoothing, _minimumContrast, _polarity, _choice),
            context.CancellationToken);

        var (width, height) = (image.Width, image.Height);
        var step = CaliperScan.RingStep(_sweepAngle, _caliperCount);

        if (ReferenceFor(context, edges, calibration, inner, outer, step) is not { } reference)
        {
            context.Set(DefectsOut, new ShapeSet([], width, height));
            context.Set(ReferenceOut, new ShapeSet([], width, height));
            context.Set(PointsOut, new ShapeSet(
                edges.Select(edge => (Shape)CaliperScan.RingPoint(edge, Evidence)).ToArray(), width, height));
            context.Set(ProfileOut, Profile(Enumerable.Repeat(double.NaN, _caliperCount).ToArray(), null, calibration));
            context.Set(CountOut, 0);
            context.Set(WorstOut, 0d);
            context.Set(FormOut, 0d);
            context.Set(PassOut, false);
            return;
        }

        // Deviation per caliper, in pixels: the edge's radius less the
        // reference's along the same ray. NaN where no edge was found.
        var deviation = new double[_caliperCount];
        Array.Fill(deviation, double.NaN);

        var byCaliper = new EdgePoint?[_caliperCount];

        foreach (var edge in edges)
        {
            deviation[edge.Caliper] = edge.At.DistanceTo(reference.Centre) - reference.Radius[edge.Caliper];
            byCaliper[edge.Caliper] = edge;
        }

        var minimumCalipers = Math.Max(1, (int)Math.Ceiling(_minimumLength / step - 1e-9));

        var runs = FindRuns(deviation, inner, outer, _mergeGap, minimumCalipers, CaliperScan.IsFullCircle(_sweepAngle));

        var defects = runs
            .Select(run => Outline(run, deviation, byCaliper, reference, band, step, calibration))
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
                    [ShapeMetric.Angle] = edge.Along,
                    [ShapeMetric.Radius] = edge.At.DistanceTo(reference.Centre),
                    [ShapeMetric.Deviation] = deviation[edge.Caliper],
                    [ShapeMetric.Contrast] = Math.Abs(edge.Strength),
                },
            })
            .ToArray();

        var form = reference.Radius.Max() - reference.Radius.Min();

        context.Set(DefectsOut, new ShapeSet(defects, width, height));
        context.Set(ReferenceOut, new ShapeSet(ReferenceShapes(reference, inner, outer), width, height));
        context.Set(PointsOut, new ShapeSet(points, width, height));
        context.Set(ProfileOut, Profile(deviation, reference.IsRound ? null : reference.Radius, calibration));
        context.Set(CountOut, defects.Length);
        context.Set(WorstOut, calibration.ToUnits(worst));
        context.Set(FormOut, calibration.ToUnits(form));
        context.Set(PassOut, defects.Length == 0);
    }

    /// <summary>
    /// What the edge is measured against: a centre, and for each caliper
    /// the reference's radius along that caliper's ray, and its angle.
    /// </summary>
    /// <param name="Centre">Where radii are measured from.</param>
    /// <param name="Radius">The reference radius along each caliper, in pixels.</param>
    /// <param name="Angle">Each caliper's direction from <paramref name="Centre"/>, in radians.</param>
    /// <param name="Drawn">The reference as a shape, carrying its measurements.</param>
    /// <param name="IsRound">A circle: the same radius all round, so drawn as circles and with no form to plot.</param>
    private sealed record ReferenceCurve(Point2 Centre, double[] Radius, double[] Angle, Shape Drawn, bool IsRound);

    /// <summary>The reference for this run, or null - having said why - when there is none.</summary>
    private ReferenceCurve? ReferenceFor(
        EvalContext context,
        IReadOnlyList<EdgePoint> edges,
        Calibration calibration,
        double inner,
        double outer,
        double step)
    {
        if (_reference == DefectReference.Wired)
        {
            if (context.Get<ShapeSet>(CircleIn)?.Shapes.FirstOrDefault() is CircleShape wired)
                return Round(wired.Centre, wired.Radius, edges, step, error: double.NaN, used: 0);

            context.Warn("Reference is Wired, but no circle is wired into Circle; fitting one instead.");
        }

        var points = edges.Select(edge => edge.At).ToArray();
        var needed = _reference == DefectReference.FittedEllipse ? 5 : CircleFit.MinimumPoints;

        if (points.Length < needed)
        {
            context.Warn(points.Length == 0
                ? "No edge was found in the ring. Check the band covers the edge, and lower Min Contrast."
                : $"Only {points.Length} edge points were found; the reference needs {needed}.");
            return null;
        }

        // Agreeing within the tolerance is what not being a defect means,
        // so it is the right test for which points a fit should follow -
        // with a floor, since a tolerance of nothing would leave no point
        // that agrees exactly.
        var agree = Math.Max(Math.Min(inner, outer), 0.5);

        switch (_reference)
        {
            case DefectReference.FittedEllipse:
                return Elliptical(context, edges, points, agree, step);

            case DefectReference.LocalForm:
            {
                // The centre only sets where radii are measured from; the
                // baseline follows whatever shape the edge has round it. A
                // plain fit over the whole ring finds the middle of a part
                // that is not round, where a consensus would pick whichever
                // arc happened to be most circular.
                var centre = CircleFit.Fit(points);

                if (!centre.Success)
                {
                    context.Warn("The edge points do not go round a centre.");
                    return null;
                }

                return Local(centre.Centre, edges, step, agree);
            }
        }

        var fit = CircleFit.FitConsensus(points, agree);

        if (!fit.Success)
        {
            context.Warn("The edge points do not describe a circle.");
            return null;
        }

        var radius = _reference == DefectReference.Nominal
            ? calibration.ToPixels(context.Param(nameof(NominalRadius), _nominalRadius))
            : fit.Radius;

        return Round(fit.Centre, radius, edges, step, fit.Error, fit.Used.Count);
    }

    /// <summary>Each caliper's direction from <paramref name="centre"/>: towards its edge point, or along the caliper where it found none.</summary>
    private double[] AnglesFrom(Point2 centre, IReadOnlyList<EdgePoint> edges, double step)
    {
        var angles = new double[_caliperCount];

        for (var i = 0; i < _caliperCount; i++)
            angles[i] = (_startAngle + (i * step)) * Math.PI / 180;

        foreach (var edge in edges)
            angles[edge.Caliper] = Math.Atan2(edge.At.Y - centre.Y, edge.At.X - centre.X);

        return angles;
    }

    private ReferenceCurve Round(
        Point2 centre, double radius, IReadOnlyList<EdgePoint> edges, double step, double error, int used)
    {
        var radii = new double[_caliperCount];
        Array.Fill(radii, radius);

        var metrics = new Dictionary<string, double>
        {
            [ShapeMetric.CentroidX] = centre.X,
            [ShapeMetric.CentroidY] = centre.Y,
            [ShapeMetric.Radius] = radius,
        };

        if (!double.IsNaN(error))
        {
            metrics[ShapeMetric.FitError] = error;
            metrics[ShapeMetric.FitPoints] = used;
        }

        return new ReferenceCurve(
            centre,
            radii,
            AnglesFrom(centre, edges, step),
            new CircleShape(centre, radius) { Metrics = metrics },
            IsRound: true);
    }

    /// <summary>
    /// An ellipse fitted to the edge, the points furthest off it set aside
    /// and the fit made again - so a notch does not pull it, as a plain fit
    /// would.
    /// </summary>
    private ReferenceCurve? Elliptical(
        EvalContext context, IReadOnlyList<EdgePoint> edges, Point2[] points, double agree, double step)
    {
        var fit = FitEllipseNode.FitOnce(points);
        var kept = points;

        for (var pass = 0; pass < 4 && fit is { } current; pass++)
        {
            var residuals = points
                .Select(point => Math.Abs(FitEllipseNode.EllipseResidual(
                    point, current.Centre, current.Major, current.Minor, current.Angle)))
                .ToArray();

            // Three robust spreads, or the tolerance if that is wider: the
            // edge's own scatter, never less than what counts as good.
            var spread = 1.4826 * Median(residuals);
            var limit = Math.Max(agree, 3 * spread);

            var survivors = points.Where((_, i) => residuals[i] <= limit).ToArray();

            if (survivors.Length < 5 || survivors.Length == kept.Length)
                break;

            kept = survivors;
            fit = FitEllipseNode.FitOnce(survivors) ?? fit;
        }

        if (fit is not { } ellipse)
        {
            context.Warn("The edge points do not describe an ellipse.");
            return null;
        }

        var angles = AnglesFrom(ellipse.Centre, edges, step);
        var a = ellipse.Major / 2;
        var b = ellipse.Minor / 2;
        var tilt = ellipse.Angle * Math.PI / 180;

        // Where the ellipse crosses each caliper's ray from its centre.
        var radii = angles
            .Select(angle =>
            {
                var along = angle - tilt;
                return a * b / Math.Sqrt(Math.Pow(b * Math.Cos(along), 2) + Math.Pow(a * Math.Sin(along), 2));
            })
            .ToArray();

        var drawn = new EllipseShape(ellipse.Centre, ellipse.Major, ellipse.Minor, ellipse.Angle)
        {
            Metrics = new Dictionary<string, double>
            {
                [ShapeMetric.CentroidX] = ellipse.Centre.X,
                [ShapeMetric.CentroidY] = ellipse.Centre.Y,
                [ShapeMetric.MajorAxis] = ellipse.Major,
                [ShapeMetric.MinorAxis] = ellipse.Minor,
                [ShapeMetric.Angle] = ellipse.Angle,
                [ShapeMetric.FitPoints] = kept.Length,
            },
        };

        return new ReferenceCurve(ellipse.Centre, radii, angles, drawn, IsRound: false);
    }

    /// <summary>
    /// The edge's own shape, smoothed: at each caliper, the median radius
    /// of the calipers within half a Form Window either side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A median rather than a mean because a notch narrower than half the
    /// window does not move it: the baseline runs straight across the notch
    /// and follows the part's slow form everywhere else, so what is left is
    /// the notch.
    /// </para>
    /// <para>
    /// Then again without what stood out. On a steep stretch of the form a
    /// defect a third of the window wide still lifts the median towards
    /// itself by a pixel or two, and reads that much shallower. So what
    /// stands out is set aside, and a short curve - a quadratic over the
    /// window - is fitted through what is left: it follows the slope and the
    /// bend of the form straight across the gap, where a median of what is
    /// left either side would jump. Each pass starts again from every
    /// point, so one set aside wrongly comes back.
    /// </para>
    /// </remarks>
    private ReferenceCurve Local(Point2 centre, IReadOnlyList<EdgePoint> edges, double step, double agree)
    {
        var radius = new double[_caliperCount];
        Array.Fill(radius, double.NaN);

        foreach (var edge in edges)
            radius[edge.Caliper] = edge.At.DistanceTo(centre);

        var window = Math.Max(3, (int)Math.Round(_formWindow / step) | 1);
        var wraps = CaliperScan.IsFullCircle(_sweepAngle);
        var baseline = LocalForm(radius, window, wraps);

        for (var pass = 0; pass < 3; pass++)
        {
            var residual = radius.Select((r, i) => Math.Abs(r - baseline[i])).Where(double.IsFinite).ToArray();

            if (residual.Length == 0)
                break;

            // Half the tolerance, or three of the edge's own robust spreads
            // if it is that rough: what stands out from the form.
            var limit = Math.Max(agree / 2, 3 * 1.4826 * Median(residual));

            var kept = radius
                .Select((r, i) => double.IsFinite(r) && Math.Abs(r - baseline[i]) <= limit ? r : double.NaN)
                .ToArray();

            baseline = LocalCurve(kept, window, wraps, fallback: baseline);
        }
        var angles = AnglesFrom(centre, edges, step);

        var outline = baseline
            .Select((r, i) => new Point2(centre.X + (r * Math.Cos(angles[i])), centre.Y + (r * Math.Sin(angles[i]))))
            .ToArray();

        var drawn = new PolygonShape(outline, CaliperScan.IsFullCircle(_sweepAngle))
        {
            Metrics = new Dictionary<string, double>
            {
                [ShapeMetric.CentroidX] = centre.X,
                [ShapeMetric.CentroidY] = centre.Y,
                [ShapeMetric.Radius] = baseline.Average(),
            },
        };

        return new ReferenceCurve(centre, baseline, angles, drawn, IsRound: false);
    }

    /// <summary>
    /// A running median of <paramref name="radius"/> over
    /// <paramref name="window"/> calipers, centred - round the seam when
    /// the ring is whole. Gaps (NaN) are left out of each median, and a
    /// caliper with none either side takes its nearest neighbour's.
    /// </summary>
    public static double[] LocalForm(IReadOnlyList<double> radius, int window, bool wraps)
    {
        var count = radius.Count;
        var half = window / 2;
        var baseline = new double[count];
        var span = new List<double>(window);

        for (var i = 0; i < count; i++)
        {
            span.Clear();

            for (var j = i - half; j <= i + half; j++)
            {
                var k = wraps ? ((j % count) + count) % count : j;

                if (k >= 0 && k < count && double.IsFinite(radius[k]))
                    span.Add(radius[k]);
            }

            baseline[i] = span.Count == 0 ? double.NaN : Median(span);
        }

        // Filled from the nearest caliper that has one.
        var known = Enumerable.Range(0, count).Where(i => double.IsFinite(baseline[i])).ToArray();

        if (known.Length == 0)
            return baseline;

        for (var i = 0; i < count; i++)
        {
            if (!double.IsFinite(baseline[i]))
                baseline[i] = baseline[known.MinBy(k => Math.Abs(k - i))];
        }

        return baseline;
    }

    /// <summary>
    /// At each caliper, a quadratic fitted by least squares through the
    /// known points within half a window either side - where too few are
    /// known, <paramref name="fallback"/>.
    /// </summary>
    internal static double[] LocalCurve(IReadOnlyList<double> radius, int window, bool wraps, IReadOnlyList<double> fallback)
    {
        var count = radius.Count;
        var half = window / 2;
        var curve = new double[count];

        for (var i = 0; i < count; i++)
        {
            // Sums for the normal equations of y = c0 + c1 x + c2 x^2, with
            // x the distance in calipers from this one.
            double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, t0 = 0, t1 = 0, t2 = 0;
            var used = 0;
            var left = 0;
            var right = 0;

            for (var j = i - half; j <= i + half; j++)
            {
                var k = wraps ? ((j % count) + count) % count : j;

                if (k < 0 || k >= count || !double.IsFinite(radius[k]))
                    continue;

                double x = j - i;
                var y = radius[k];
                var x2 = x * x;

                s0 += 1;
                s1 += x;
                s2 += x2;
                s3 += x2 * x;
                s4 += x2 * x2;
                t0 += y;
                t1 += x * y;
                t2 += x2 * y;
                used++;

                if (j < i)
                    left++;
                else if (j > i)
                    right++;
            }

            // A curve needs points to hold it: a few in all, and some on
            // each side, or its ends swing wherever they like.
            if (used < 6 || left == 0 || right == 0)
            {
                curve[i] = fallback[i];
                continue;
            }

            // Cramer's rule on the 3 x 3 system.
            var det = Determinant(s0, s1, s2, s1, s2, s3, s2, s3, s4);

            curve[i] = Math.Abs(det) < 1e-9
                ? t0 / s0
                : Determinant(t0, s1, s2, t1, s2, s3, t2, s3, s4) / det;
        }

        return curve;
    }

    private static double Determinant(
        double a, double b, double c, double d, double e, double f, double g, double h, double i) =>
        (a * ((e * i) - (f * h))) - (b * ((d * i) - (f * g))) + (c * ((d * h) - (e * g)));

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;

        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>
    /// The stretches of an edge out of tolerance.
    /// </summary>
    /// <param name="deviation">Per caliper, in order: positive outside the circle, NaN where no edge was found.</param>
    /// <param name="inner">How far inside the circle is still acceptable.</param>
    /// <param name="outer">How far outside is still acceptable.</param>
    /// <param name="mergeGap">Runs of one kind this many calipers apart, or fewer, become one.</param>
    /// <param name="minimumCalipers">Runs shorter than this are dropped as noise.</param>
    /// <param name="wraps">Whether the last caliper sits beside the first - a full circle.</param>
    public static IReadOnlyList<DefectRun> FindRuns(
        IReadOnlyList<double> deviation, double inner, double outer, int mergeGap, int minimumCalipers, bool wraps)
    {
        var count = deviation.Count;
        if (count == 0)
            return [];

        var kinds = new DefectKind?[count];

        for (var i = 0; i < count; i++)
            kinds[i] = Classify(deviation[i], inner, outer);

        // On a full circle, work from a caliper that is in tolerance, so no
        // defect is cut in two at the seam. With none in tolerance the whole
        // edge is one defect.
        var origin = 0;

        if (wraps)
        {
            origin = Array.FindIndex(kinds, kind => kind is null);

            if (origin < 0)
            {
                var kind = kinds.All(k => k == DefectKind.OutOfBand)
                    ? DefectKind.OutOfBand
                    : kinds.Where(k => k != DefectKind.OutOfBand).GroupBy(k => k).MaxBy(g => g.Count())!.Key!.Value;

                return count >= minimumCalipers ? [new DefectRun(kind, 0, count)] : [];
            }
        }

        int At(int i) => (origin + i) % count;

        // Followed out while the edge is still more than half the tolerance
        // off the same way: where a defect begins and ends is where it is
        // half out, not where it first crosses the line.
        for (var i = 0; i < count; i++)
        {
            var seed = kinds[At(i)];

            if (seed is not (DefectKind.Missing or DefectKind.Excess))
                continue;

            for (var j = i - 1; j >= 0 && Continues(At(j), seed.Value); j--)
                kinds[At(j)] = seed;

            for (var j = i + 1; j < count && Continues(At(j), seed.Value); j++)
                kinds[At(j)] = seed;
        }

        bool Continues(int index, DefectKind kind) =>
            kinds[index] is null && double.IsFinite(deviation[index]) &&
            (kind == DefectKind.Excess ? deviation[index] > outer / 2 : deviation[index] < -inner / 2);

        // A stretch with no edge beside a missing or excess one is taken as
        // part of it - the burr ran past the band, or the chip below it.
        var runs = new List<(DefectKind Kind, int Start, int End)>();

        for (var i = 0; i < count;)
        {
            if (kinds[At(i)] is not { } kind)
            {
                i++;
                continue;
            }

            var end = i;
            while (end + 1 < count && kinds[At(end + 1)] == kind)
                end++;

            runs.Add((kind, i, end));
            i = end + 1;
        }

        for (var r = 0; r < runs.Count; r++)
        {
            if (runs[r].Kind != DefectKind.OutOfBand)
                continue;

            var before = r > 0 && runs[r - 1].End + 1 == runs[r].Start ? runs[r - 1].Kind : (DefectKind?)null;
            var after = r + 1 < runs.Count && runs[r + 1].Start == runs[r].End + 1 ? runs[r + 1].Kind : (DefectKind?)null;

            var adopted = (before, after) switch
            {
                ({ } b, null) => b,
                (null, { } a) => a,
                ({ } b, { } a) when b == a => b,
                _ => (DefectKind?)null,
            };

            if (adopted is { } taken && taken != DefectKind.OutOfBand)
                runs[r] = runs[r] with { Kind = taken };
        }

        // Joined: touching runs of one kind, and runs of one kind a small
        // gap apart.
        var merged = new List<(DefectKind Kind, int Start, int End)>();

        foreach (var run in runs)
        {
            if (merged.Count > 0 && merged[^1].Kind == run.Kind && run.Start - merged[^1].End - 1 <= mergeGap)
                merged[^1] = merged[^1] with { End = run.End };
            else
                merged.Add(run);
        }

        // And across the seam, which on a full circle is a gap like any other.
        if (wraps && merged.Count > 1 && merged[0].Kind == merged[^1].Kind &&
            (count - merged[^1].End - 1) + merged[0].Start <= mergeGap)
        {
            merged[0] = (merged[0].Kind, merged[^1].Start - count, merged[0].End);
            merged.RemoveAt(merged.Count - 1);
        }

        return merged
            .Where(run => run.End - run.Start + 1 >= minimumCalipers)
            .Select(run => new DefectRun(run.Kind, ((origin + run.Start) % count + count) % count, run.End - run.Start + 1))
            .ToArray();
    }

    private static DefectKind? Classify(double deviation, double inner, double outer) =>
        double.IsNaN(deviation) ? DefectKind.OutOfBand
        : deviation > outer ? DefectKind.Excess
        : deviation < -inner ? DefectKind.Missing
        : null;

    /// <summary>
    /// A defect as a shape: the region between the edge and the reference,
    /// with its measurements.
    /// </summary>
    private PolygonShape Outline(
        DefectRun run,
        double[] deviation,
        EdgePoint?[] byCaliper,
        ReferenceCurve reference,
        Ring band,
        double step,
        Calibration calibration)
    {
        var count = deviation.Length;
        var indices = Enumerable.Range(0, run.Length).Select(i => (run.Start + i) % count).ToArray();
        var radians = step * Math.PI / 180;

        var edgeSide = new List<Point2>();
        var referenceSide = new List<Point2>();
        var area = 0.0;
        var length = 0.0;
        var worst = double.NaN;
        var worstAt = reference.Centre;

        foreach (var index in indices)
        {
            var angle = reference.Angle[index];
            var (ux, uy) = (Math.Cos(angle), Math.Sin(angle));
            var expected = reference.Radius[index];

            referenceSide.Add(new Point2(reference.Centre.X + (ux * expected), reference.Centre.Y + (uy * expected)));
            length += expected * radians;

            if (byCaliper[index] is { } edge)
            {
                edgeSide.Add(edge.At);

                var radius = expected + deviation[index];

                // The sector between the two radii, which summed over the
                // calipers is the area between edge and reference.
                area += Math.Abs((radius * radius) - (expected * expected)) / 2 * radians;

                if (double.IsNaN(worst) || Math.Abs(deviation[index]) > Math.Abs(worst))
                {
                    worst = deviation[index];
                    worstAt = edge.At;
                }
            }
            else
            {
                // No edge here: as far as the band reaches, the side the
                // defect is on - which is all that can be said of it.
                var reach = run.Kind == DefectKind.Missing ? band.InnerRadius
                    : run.Kind == DefectKind.Excess ? band.OuterRadius
                    : _direction == RadialDirection.Inward ? band.OuterRadius : band.InnerRadius;

                var nominal = (_startAngle + (index * step)) * Math.PI / 180;
                edgeSide.Add(new Point2(band.CentreX + (Math.Cos(nominal) * reach), band.CentreY + (Math.Sin(nominal) * reach)));
            }
        }

        var points = edgeSide.Concat(Enumerable.Reverse(referenceSide)).ToArray();

        var from = Normalise(_startAngle + (run.Start * step));
        var to = Normalise(_startAngle + ((run.Start + run.Length - 1) * step));
        var middle = Normalise(_startAngle + ((run.Start + ((run.Length - 1) / 2.0)) * step));

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
            [ShapeMetric.Angle] = middle,
            [ShapeMetric.StartAngle] = from,
            [ShapeMetric.EndAngle] = to,
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
    private static IReadOnlyList<Shape> ReferenceShapes(ReferenceCurve reference, double inner, double outer)
    {
        var shapes = new List<Shape> { reference.Drawn with { Colour = Reference, StrokeWidth = 2 } };

        switch (reference.Drawn)
        {
            case CircleShape circle:
                if (outer > 0)
                    shapes.Add(new CircleShape(circle.Centre, circle.Radius + outer) { Colour = ToleranceBand, StrokeWidth = 1 });

                if (inner > 0 && circle.Radius - inner > 0)
                    shapes.Add(new CircleShape(circle.Centre, circle.Radius - inner) { Colour = ToleranceBand, StrokeWidth = 1 });

                break;

            case EllipseShape ellipse:
                if (outer > 0)
                {
                    shapes.Add(ellipse with
                    {
                        MajorAxis = ellipse.MajorAxis + (2 * outer),
                        MinorAxis = ellipse.MinorAxis + (2 * outer),
                        Colour = ToleranceBand,
                        StrokeWidth = 1,
                        Metrics = new Dictionary<string, double>(),
                    });
                }

                if (inner > 0 && ellipse.MinorAxis - (2 * inner) > 0)
                {
                    shapes.Add(ellipse with
                    {
                        MajorAxis = ellipse.MajorAxis - (2 * inner),
                        MinorAxis = ellipse.MinorAxis - (2 * inner),
                        Colour = ToleranceBand,
                        StrokeWidth = 1,
                        Metrics = new Dictionary<string, double>(),
                    });
                }

                break;

            case PolygonShape polygon:
                foreach (var offset in new[] { outer, -inner })
                {
                    if (offset == 0)
                        continue;

                    var shifted = reference.Radius
                        .Select((r, i) => new Point2(
                            reference.Centre.X + ((r + offset) * Math.Cos(reference.Angle[i])),
                            reference.Centre.Y + ((r + offset) * Math.Sin(reference.Angle[i]))))
                        .ToArray();

                    shapes.Add(new PolygonShape(shifted, polygon.Closed) { Colour = ToleranceBand, StrokeWidth = 1 });
                }

                break;
        }

        return shapes;
    }

    /// <summary>
    /// Deviation against angle - and, when the reference is not a circle,
    /// its form: how far its radius runs above or below its own average.
    /// </summary>
    private ProfileData Profile(double[] deviation, double[]? form, Calibration calibration)
    {
        static float[] Samples(IEnumerable<double> values, Calibration calibration) => values
            .Select(value => double.IsNaN(value) ? float.NaN : (float)calibration.ToUnits(value))
            .ToArray();

        var channels = new List<ProfileChannel> { new("Deviation", Samples(deviation, calibration)) };

        if (form is not null)
        {
            var mean = form.Average();
            channels.Add(new ProfileChannel("Form", Samples(form.Select(r => r - mean), calibration)));
        }

        return new ProfileData(
            channels,
            default,
            default,
            CaliperScan.RingStep(_sweepAngle, _caliperCount))
        {
            XUnit = "°",
            XOrigin = _startAngle,
            ValueFormat = "0.###",
            GapLabel = "missed",
            IsLine = false,
        };
    }

    private static double Normalise(double degrees)
    {
        var wrapped = degrees % 360;
        return wrapped < 0 ? wrapped + 360 : wrapped;
    }
}
