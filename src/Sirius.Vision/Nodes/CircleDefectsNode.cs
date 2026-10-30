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
    private DefectReference _reference = DefectReference.Fitted;
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
        Description = "Fitted: the circle most of the edge agrees with. Nominal: a known radius about the fitted " +
                      "centre. Wired: the circle wired in.")]
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

        if (ReferenceFor(context, edges, calibration, inner, outer) is not { } circle)
        {
            context.Set(DefectsOut, new ShapeSet([], width, height));
            context.Set(ReferenceOut, new ShapeSet([], width, height));
            context.Set(PointsOut, new ShapeSet(
                edges.Select(edge => (Shape)CaliperScan.RingPoint(edge, Evidence)).ToArray(), width, height));
            context.Set(ProfileOut, Profile(Enumerable.Repeat(double.NaN, _caliperCount).ToArray(), calibration));
            context.Set(CountOut, 0);
            context.Set(WorstOut, 0d);
            context.Set(PassOut, false);
            return;
        }

        // Deviation per caliper, in pixels; NaN where a caliper found no edge.
        var deviation = new double[_caliperCount];
        Array.Fill(deviation, double.NaN);

        var byCaliper = new EdgePoint?[_caliperCount];

        foreach (var edge in edges)
        {
            deviation[edge.Caliper] = edge.At.DistanceTo(circle.Centre) - circle.Radius;
            byCaliper[edge.Caliper] = edge;
        }

        var step = CaliperScan.RingStep(_sweepAngle, _caliperCount);
        var minimumCalipers = Math.Max(1, (int)Math.Ceiling(_minimumLength / step - 1e-9));

        var runs = FindRuns(deviation, inner, outer, _mergeGap, minimumCalipers, CaliperScan.IsFullCircle(_sweepAngle));

        var defects = runs
            .Select(run => Outline(run, deviation, byCaliper, circle, band, step, calibration))
            .ToArray();

        var finite = deviation.Where(double.IsFinite).ToArray();
        var worst = finite.Length == 0 ? 0 : finite.MaxBy(value => Math.Abs(value));

        context.Set(DefectsOut, new ShapeSet(defects, width, height));
        context.Set(ReferenceOut, new ShapeSet(ReferenceShapes(circle, inner, outer), width, height));
        context.Set(PointsOut, new ShapeSet(
            edges.Select(edge => (Shape)CaliperScan.RingPoint(edge, Evidence, circle.Centre, circle.Radius)).ToArray(),
            width,
            height));
        context.Set(ProfileOut, Profile(deviation, calibration));
        context.Set(CountOut, defects.Length);
        context.Set(WorstOut, calibration.ToUnits(worst));
        context.Set(PassOut, defects.Length == 0);
    }

    /// <summary>The circle the edge is measured against, or null - having said why - when there is none.</summary>
    private CircleShape? ReferenceFor(
        EvalContext context, IReadOnlyList<EdgePoint> edges, Calibration calibration, double inner, double outer)
    {
        if (_reference == DefectReference.Wired)
        {
            if (context.Get<ShapeSet>(CircleIn)?.Shapes.FirstOrDefault() is CircleShape wired)
                return wired;

            context.Warn("Reference is Wired, but no circle is wired into Circle; fitting one instead.");
        }

        var points = edges.Select(edge => edge.At).ToArray();

        if (points.Length < CircleFit.MinimumPoints)
        {
            context.Warn(points.Length == 0
                ? "No edge was found in the ring. Check the band covers the edge, and lower Min Contrast."
                : $"Only {points.Length} edge points were found; a circle needs {CircleFit.MinimumPoints}.");
            return null;
        }

        // Agreeing within the tolerance is what not being a defect means,
        // so it is the right test for which points the circle should
        // follow - with a floor, since a tolerance of nothing would leave
        // the fit with no point that agrees exactly.
        var fit = CircleFit.FitConsensus(points, Math.Max(Math.Min(inner, outer), 0.5));

        if (!fit.Success)
        {
            context.Warn("The edge points do not describe a circle.");
            return null;
        }

        var radius = _reference == DefectReference.Nominal
            ? calibration.ToPixels(context.Param(nameof(NominalRadius), _nominalRadius))
            : fit.Radius;

        return new CircleShape(fit.Centre, radius)
        {
            Metrics = new Dictionary<string, double>
            {
                [ShapeMetric.CentroidX] = fit.Centre.X,
                [ShapeMetric.CentroidY] = fit.Centre.Y,
                [ShapeMetric.Radius] = radius,
                [ShapeMetric.FitError] = fit.Error,
                [ShapeMetric.FitPoints] = fit.Used.Count,
            },
        };
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
    /// A defect as a shape: the region between the edge and the circle,
    /// with its measurements.
    /// </summary>
    private PolygonShape Outline(
        DefectRun run,
        double[] deviation,
        EdgePoint?[] byCaliper,
        CircleShape circle,
        Ring band,
        double step,
        Calibration calibration)
    {
        var count = deviation.Length;
        var indices = Enumerable.Range(0, run.Length).Select(i => (run.Start + i) % count).ToArray();

        var edgeSide = new List<Point2>();
        var circleSide = new List<Point2>();
        var area = 0.0;
        var worst = double.NaN;
        var worstAt = circle.Centre;

        foreach (var index in indices)
        {
            var angle = (_startAngle + (index * step)) * Math.PI / 180;
            var (ux, uy) = (Math.Cos(angle), Math.Sin(angle));

            circleSide.Add(new Point2(circle.Centre.X + (ux * circle.Radius), circle.Centre.Y + (uy * circle.Radius)));

            if (byCaliper[index] is { } edge)
            {
                edgeSide.Add(edge.At);

                var radius = circle.Radius + deviation[index];

                // The sector between the two radii, which summed over the
                // calipers is the area between edge and circle.
                area += Math.Abs((radius * radius) - (circle.Radius * circle.Radius)) / 2 * (step * Math.PI / 180);

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

                edgeSide.Add(new Point2(band.CentreX + (ux * reach), band.CentreY + (uy * reach)));
            }
        }

        var points = edgeSide.Concat(Enumerable.Reverse(circleSide)).ToArray();

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
            [ShapeMetric.Length] = run.Length * step * Math.PI / 180 * circle.Radius,
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

    /// <summary>The reference circle in green, with thin circles where the tolerances lie.</summary>
    private static IReadOnlyList<Shape> ReferenceShapes(CircleShape circle, double inner, double outer)
    {
        var shapes = new List<Shape> { circle with { Colour = Reference, StrokeWidth = 2 } };

        if (outer > 0)
            shapes.Add(new CircleShape(circle.Centre, circle.Radius + outer) { Colour = ToleranceBand, StrokeWidth = 1 });

        if (inner > 0 && circle.Radius - inner > 0)
            shapes.Add(new CircleShape(circle.Centre, circle.Radius - inner) { Colour = ToleranceBand, StrokeWidth = 1 });

        return shapes;
    }

    private ProfileData Profile(double[] deviation, Calibration calibration)
    {
        var samples = deviation
            .Select(value => double.IsNaN(value) ? float.NaN : (float)calibration.ToUnits(value))
            .ToArray();

        return new ProfileData(
            [new ProfileChannel("Deviation", samples)],
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
