using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>
/// An outline through points, with what can be measured of it.
/// </summary>
/// <remarks>
/// Shared by the caliper tools and Points to Polygon, so a contour measures
/// the same however it was made.
/// </remarks>
internal static class Outline
{
    /// <summary>
    /// A polygon through <paramref name="points"/> in the order given, or
    /// null when there are too few to draw one.
    /// </summary>
    /// <param name="closed">
    /// Whether the last point joins back to the first. Needs three points;
    /// two make an open segment whatever is asked.
    /// </param>
    public static PolygonShape? Through(IReadOnlyList<Point2> points, bool closed, uint? colour)
    {
        if (points.Count < 2)
            return null;

        closed &= points.Count >= 3;

        var perimeter = 0.0;
        for (var i = 1; i < points.Count; i++)
            perimeter += points[i - 1].DistanceTo(points[i]);

        if (closed)
            perimeter += points[^1].DistanceTo(points[0]);

        var metrics = new Dictionary<string, double>
        {
            [ShapeMetric.CentroidX] = points.Average(point => point.X),
            [ShapeMetric.CentroidY] = points.Average(point => point.Y),
            [ShapeMetric.Perimeter] = perimeter,
        };

        // An open path encloses nothing, so it has no area to report.
        if (closed)
            metrics[ShapeMetric.Area] = Math.Abs(SignedArea(points));

        return new PolygonShape(points.ToArray(), closed)
        {
            Colour = colour,
            Metrics = metrics,
        };
    }

    /// <summary>The shoelace formula: half the sum of the cross products round the outline.</summary>
    private static double SignedArea(IReadOnlyList<Point2> points)
    {
        var twice = 0.0;

        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            twice += (a.X * b.Y) - (b.X * a.Y);
        }

        return twice / 2;
    }
}

/// <summary>What Ring Calipers measures each point's radius from.</summary>
public enum RadiusFrom
{
    /// <summary>
    /// The centre of the band - a wired circle's centre when one is wired.
    /// Deviation is from the band's middle radius.
    /// </summary>
    Band,

    /// <summary>
    /// A circle fitted through the points found, least squares and nothing
    /// rejected. Deviation is from that circle, so it is the edge's own
    /// departure from round, whatever the band's placement.
    /// </summary>
    Fitted,
}

/// <summary>What Ring Calipers plots round the ring.</summary>
public enum RingProfileValue
{
    /// <summary>Each point's distance off the reference circle - the roundness profile.</summary>
    Deviation,

    /// <summary>Each point's distance from the reference centre.</summary>
    Radius,

    /// <summary>How sharp the edge was - where it is weak.</summary>
    Contrast,
}

/// <summary>
/// Casts radial calipers through a ring and gives every edge they find.
/// </summary>
/// <remarks>
/// <para>
/// Find Circle without the circle. The calipers, the band and every edge
/// setting are Find Circle's - the same code - but nothing is fitted and
/// nothing is rejected, so every edge found comes out: as points, each
/// knowing its angle, its radius and how sharp it was, and as a contour
/// joining them in order round the ring.
/// </para>
/// <para>
/// For what Find Circle cannot answer. An edge that is not a circle - an
/// ellipse, a lobed cam - is fitted by wiring the points into Fit Ellipse or
/// anything else. A local fault in the edge - a notch, a burr, a chip - is
/// kept: Find Circle's rejection removes it as an outlier, which is right
/// for fitting a circle and wrong for finding the fault. And the points are
/// the measurement in their own right: a radius per angle is a roundness
/// profile, a contrast per angle shows where the edge is weak, and the
/// contour's area is the area inside the edge.
/// </para>
/// <para>
/// Wire a found circle into Circle and the band follows it - centred on its
/// edge, Band Width wide - so Find Circle locates the part and this
/// measures it. Radius From picks what each radius is measured from, and
/// the Profile output plots it against angle for a Line Chart.
/// </para>
/// </remarks>
[VisionNode(
    "Ring Calipers",
    TypeKey = "fitting.ringCalipers",
    Category = NodeCategory.Fitting,
    Icon = "ph_ring_calipers",
    Description = "Casts radial calipers through a ring and gives every edge they find, as points and as a contour, " +
                  "without fitting anything. Wire the points into Fit Ellipse, Points to Polygon or a Shape Table.")]
public sealed class RingCalipersNode : VisionNode
{
    /// <summary>Amber, as the finders draw their edge points.</summary>
    private const uint Evidence = 0xFFFBBF24;

    /// <summary>Green, for the outline joining them.</summary>
    private const uint Contour = 0xFF22C55E;

    private Ring _band = new(500, 500, 80, 140);
    private double _startAngle;
    private double _sweepAngle = 360;
    private int _caliperCount = 72;
    private int _caliperWidth = 5;
    private double _spacing = 0.5;
    private int _smoothing = 5;
    private double _minimumContrast = 3;
    private EdgePolarity _polarity = EdgePolarity.Either;
    private EdgeChoice _choice = EdgeChoice.Strongest;
    private RadialDirection _direction = RadialDirection.Outward;
    private double _bandWidth = 40;
    private double _offset;
    private RadiusFrom _radiusFrom = RadiusFrom.Band;
    private RingProfileValue _profileValue = RingProfileValue.Deviation;

    public RingCalipersNode()
    {
        ImageIn = AddInput<ImageData>("In");
        CircleIn = AddInput<ShapeSet>("Circle", isRequired: false);
        PointsOut = AddOutput<ShapeSet>("Points");
        ContourOut = AddOutput<ShapeSet>("Contour");
        ProfileOut = AddOutput<ProfileData>("Profile");
        RoundnessOut = AddOutput<double>("Roundness");
        CountOut = AddOutput<int>("Count");
        MissedOut = AddOutput<int>("Missed");
    }

    public InputPort ImageIn { get; }

    /// <summary>
    /// A circle to search around - Find Circle's, say - or a point to centre
    /// the band on.
    /// </summary>
    public InputPort CircleIn { get; }

    /// <summary>
    /// Every edge found, in order round the ring, each with its angle, its
    /// radius and deviation from the reference circle, and its contrast.
    /// </summary>
    public OutputPort PointsOut { get; }

    /// <summary>
    /// One outline through the points in order - closed for a full ring,
    /// open for an arc - with its perimeter, and its area when closed.
    /// </summary>
    public OutputPort ContourOut { get; }

    /// <summary>
    /// The chosen value at every caliper, against angle - a gap where a
    /// caliper found nothing. Wire it to a Line Chart.
    /// </summary>
    public OutputPort ProfileOut { get; }

    /// <summary>
    /// The largest radius found less the smallest, from the reference
    /// centre. With Radius From set to Fitted, this is roundness against
    /// the least-squares circle.
    /// </summary>
    public OutputPort RoundnessOut { get; }

    /// <summary>How many calipers found an edge.</summary>
    public OutputPort CountOut { get; }

    /// <summary>How many found none - gaps in the edge, or a band that misses it.</summary>
    public OutputPort MissedOut { get; }

    // ---- where to look -------------------------------------------------

    [Param("Band",
        Editor = ParamEditor.Ring,
        Connectable = true,
        Description = "Search band as x, y, inner, outer. Draw it on the image: centre, then the edge, then the width. " +
                      "A wired point moves it; a wired circle replaces it.")]
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
        Description = "Degrees clockwise from the positive X axis. The points and the contour start here.")]
    public double StartAngle
    {
        get => _startAngle;
        set => SetParam(ref _startAngle, value);
    }

    [Param("Sweep", Group = "Ring", Min = 1, Max = 360, Step = 1,
        Description = "How much of the ring to search. Less than 360 searches an arc, and the contour is left open.")]
    public double SweepAngle
    {
        get => _sweepAngle;
        set => SetParam(ref _sweepAngle, Math.Clamp(value, 1, 360));
    }

    // ---- how to look ---------------------------------------------------

    [Param("Calipers", Group = "Edge", Min = 3, Max = 720, Step = 1,
        Description = "How many radial scans to cast - one point at most from each.")]
    public int CaliperCount
    {
        get => _caliperCount;
        set => SetParam(ref _caliperCount, Math.Clamp(value, 3, 720));
    }

    [Param("Caliper Width", Group = "Edge", Min = 1, Max = 101, Step = 2, OddOnly = true,
        Description = "Pixels averaged across each scan. Wider is steadier on noise, blunter on detail.")]
    public int CaliperWidth
    {
        get => _caliperWidth;
        set => SetParam(ref _caliperWidth, Math.Clamp(value | 1, 1, 101));
    }

    [Param("Sampling", Group = "Edge", Min = 0.1, Max = 2, Step = 0.1,
        Description = "Distance between samples along a scan, in pixels. Below one over-samples for sub-pixel work.")]
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
        Description = "Smallest intensity step counted as an edge. Raise it to ignore texture.")]
    public double MinimumContrast
    {
        get => _minimumContrast;
        set => SetParam(ref _minimumContrast, Math.Max(0, value));
    }

    [Param("Polarity", Group = "Edge",
        Description = "Dark to light, light to dark, or whichever is strongest - measured along the scan direction.")]
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
        Description = "Which end of the band each scan starts from; this is what Polarity and Edge are relative to.")]
    public RadialDirection Direction
    {
        get => _direction;
        set => SetParam(ref _direction, value);
    }

    // ---- what to measure -----------------------------------------------

    [Param("Radius From", Group = "Measure",
        Description = "Band: radii from the band's centre - a wired circle's centre. Fitted: from a circle fitted " +
                      "through the points found, so deviation is the edge's own departure from round.")]
    public RadiusFrom RadiusFrom
    {
        get => _radiusFrom;
        set => SetParam(ref _radiusFrom, value);
    }

    [Param("Profile", Group = "Measure",
        Description = "What the Profile output plots against angle: deviation from the reference circle, radius, or contrast.")]
    public RingProfileValue ProfileValue
    {
        get => _profileValue;
        set => SetParam(ref _profileValue, value);
    }

    protected override void OnEvaluate(EvalContext context)
    {
        if (!context.TryGet<ImageData>(ImageIn, out var image))
            throw new InvalidOperationException("No image is connected.");

        var band = BandFor(context);

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

        if (edges.Count == 0)
            context.Warn("No edge was found in the ring. Check the band covers the edge, and lower Min Contrast.");
        else if (edges.Count < _caliperCount / 2)
            context.Warn($"Only {edges.Count} of {_caliperCount} calipers found an edge.");

        var contour = Outline.Through(
            edges.Select(edge => edge.At).ToArray(),
            CaliperScan.IsFullCircle(_sweepAngle),
            Contour);

        var (centre, reference) = ReferenceFor(context, band, edges);

        var points = edges
            .Select(edge => CaliperScan.RingPoint(edge, Evidence, centre, reference))
            .ToArray();

        context.Set(PointsOut, new ShapeSet(points.Select(point => (Shape)point).ToArray(), image.Width, image.Height));
        context.Set(ContourOut, new ShapeSet(contour is null ? [] : [contour], image.Width, image.Height));
        context.Set(ProfileOut, ProfileOf(edges, points, centre));
        context.Set(RoundnessOut, points.Length == 0 ? 0d : Spread(points, ShapeMetric.Radius));
        context.Set(CountOut, edges.Count);
        context.Set(MissedOut, _caliperCount - edges.Count);
    }

    /// <summary>
    /// The band this run searches: the drawn one, moved to a wired point, or
    /// replaced by one around a wired circle.
    /// </summary>
    internal Ring BandFor(EvalContext context)
    {
        var band = context.Param(nameof(Band), _band);

        if (context.Get<ShapeSet>(CircleIn) is not { Count: > 0 } shapes)
            return band;

        if (shapes.Count > 1)
            context.Warn($"{shapes.Count} shapes arrived on Circle; the band follows the first.");

        var width = context.Param(nameof(BandWidth), _bandWidth);
        var offset = context.Param(nameof(Offset), _offset);

        return shapes.Shapes[0] switch
        {
            CircleShape circle =>
                Ring.FromMean(circle.Centre.X, circle.Centre.Y, circle.Radius + offset, width / 2),

            // Near enough a circle for its mean radius to be what is meant.
            EllipseShape ellipse => Ring.FromMean(
                ellipse.Centre.X, ellipse.Centre.Y, ((ellipse.MajorAxis + ellipse.MinorAxis) / 4) + offset, width / 2),

            PointShape point => band with { CentreX = point.At.X, CentreY = point.At.Y },

            var other => band with { CentreX = other.Bounds.Centre.X, CentreY = other.Bounds.Centre.Y },
        };
    }

    /// <summary>The centre each radius is measured from, and the radius deviation is measured against.</summary>
    private (Point2 Centre, double Radius) ReferenceFor(EvalContext context, Ring band, IReadOnlyList<EdgePoint> edges)
    {
        var drawn = (new Point2(band.CentreX, band.CentreY), band.MeanRadius);

        if (_radiusFrom == RadiusFrom.Band || edges.Count == 0)
            return drawn;

        // Plain least squares, nothing rejected: a notch is what is being
        // looked for, and rejecting it as an outlier would hide it.
        var fit = CircleFit.Fit(edges.Select(edge => edge.At).ToArray());

        if (!fit.Success)
        {
            context.Warn(
                $"No circle could be fitted through {edges.Count} points; radii are from the band's centre.");
            return drawn;
        }

        return (fit.Centre, fit.Radius);
    }

    /// <summary>
    /// The chosen value at every caliper in turn, with a gap where one
    /// found nothing, so the angle axis stays even.
    /// </summary>
    private ProfileData ProfileOf(IReadOnlyList<EdgePoint> edges, IReadOnlyList<PointShape> points, Point2 centre)
    {
        var (name, metric) = _profileValue switch
        {
            RingProfileValue.Radius => ("Radius", ShapeMetric.Radius),
            RingProfileValue.Contrast => ("Contrast", ShapeMetric.Contrast),
            _ => ("Deviation", ShapeMetric.Deviation),
        };

        var samples = new float[_caliperCount];
        Array.Fill(samples, float.NaN);

        for (var i = 0; i < edges.Count; i++)
            samples[edges[i].Caliper] = (float)points[i].Metrics[metric];

        var at = new System.Drawing.PointF((float)centre.X, (float)centre.Y);

        return new ProfileData(
            [new ProfileChannel(name, samples)],
            at,
            at,
            CaliperScan.RingStep(_sweepAngle, _caliperCount))
        {
            XUnit = "°",
            XOrigin = _startAngle,
            ValueFormat = _profileValue == RingProfileValue.Contrast ? "0.#" : "0.###",
            GapLabel = "missed",
            IsLine = false,
        };
    }

    private static double Spread(IReadOnlyList<PointShape> points, string metric)
    {
        var values = points.Select(point => point.Metrics[metric]).ToArray();
        return values.Max() - values.Min();
    }
}

/// <summary>
/// Casts calipers across a rectangle and gives every edge they find.
/// </summary>
/// <remarks>
/// Find Line without the line - the same calipers and region, nothing fitted
/// and nothing rejected. Each point knows how far along the region it is and
/// how far off its middle, so the points read as a straightness profile; the
/// contour joins them into the edge as it actually is.
/// </remarks>
[VisionNode(
    "Line Calipers",
    TypeKey = "fitting.lineCalipers",
    Category = NodeCategory.Fitting,
    Icon = "ph_line_calipers",
    Description = "Casts calipers across a rectangle and gives every edge they find, as points and as an open contour, " +
                  "without fitting anything.")]
public sealed class LineCalipersNode : VisionNode
{
    private const uint Evidence = 0xFFFBBF24;

    private const uint Contour = 0xFF22C55E;

    private OrientedRect _region = new(300, 200, 240, 60, 0);
    private int _caliperCount = 24;
    private int _caliperWidth = 5;
    private double _spacing = 0.5;
    private int _smoothing = 5;
    private double _minimumContrast = 3;
    private EdgePolarity _polarity = EdgePolarity.Either;
    private EdgeChoice _choice = EdgeChoice.Strongest;
    private ScanDirection _direction = ScanDirection.Across;

    public LineCalipersNode()
    {
        ImageIn = AddInput<ImageData>("In");
        PointsOut = AddOutput<ShapeSet>("Points");
        ContourOut = AddOutput<ShapeSet>("Contour");
        CountOut = AddOutput<int>("Count");
        MissedOut = AddOutput<int>("Missed");
    }

    public InputPort ImageIn { get; }

    /// <summary>Every edge found, in order along the region, each with its distance along, offset and contrast.</summary>
    public OutputPort PointsOut { get; }

    /// <summary>An open outline through the points in order, with its length as its perimeter.</summary>
    public OutputPort ContourOut { get; }

    public OutputPort CountOut { get; }

    public OutputPort MissedOut { get; }

    [Param("Region",
        Editor = ParamEditor.Rect,
        Connectable = true,
        Description = "Search rectangle as x, y, w, h, angle. Its long axis runs along the expected edge.")]
    public OrientedRect Region
    {
        get => _region;
        set => SetParam(ref _region, value);
    }

    [Param("Calipers", Group = "Edge", Min = 2, Max = 500, Step = 1,
        Description = "How many scans to cast along the region - one point at most from each.")]
    public int CaliperCount
    {
        get => _caliperCount;
        set => SetParam(ref _caliperCount, Math.Clamp(value, 2, 500));
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

    [Param("Min Contrast", Group = "Edge", Min = 0, Max = 255, Step = 0.5,
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
        Description = "Which side each scan starts from; Polarity and Edge are relative to it.")]
    public ScanDirection Direction
    {
        get => _direction;
        set => SetParam(ref _direction, value);
    }

    protected override void OnEvaluate(EvalContext context)
    {
        if (!context.TryGet<ImageData>(ImageIn, out var image))
            throw new InvalidOperationException("No image is connected.");

        var region = context.Param(nameof(Region), _region);

        using var sampler = LineProfileSource.From(image);

        var edges = CaliperScan.AcrossRect(
            sampler,
            region,
            _direction,
            new CaliperSettings(
                _caliperCount, _caliperWidth, _spacing, _smoothing, _minimumContrast, _polarity, _choice),
            context.CancellationToken);

        if (edges.Count == 0)
            context.Warn("No edge was found in the region. Check it spans the edge, and lower Min Contrast.");
        else if (edges.Count < _caliperCount / 2)
            context.Warn($"Only {edges.Count} of {_caliperCount} calipers found an edge.");

        var contour = Outline.Through(edges.Select(edge => edge.At).ToArray(), closed: false, Contour);

        context.Set(
            PointsOut,
            new ShapeSet(edges.Select(edge => (Shape)CaliperScan.LinePoint(edge, Evidence)).ToArray(), image.Width, image.Height));

        context.Set(ContourOut, new ShapeSet(contour is null ? [] : [contour], image.Width, image.Height));
        context.Set(CountOut, edges.Count);
        context.Set(MissedOut, _caliperCount - edges.Count);
    }
}

/// <summary>How Points to Polygon orders what it joins.</summary>
public enum PointOrder
{
    /// <summary>
    /// The order the points arrive in - right for anything that produced
    /// them in order, as the caliper tools do.
    /// </summary>
    AsGiven,

    /// <summary>
    /// Sorted by angle round their middle, clockwise from the positive X
    /// axis - for points that arrive in no useful order, such as blob
    /// centres round a ring of holes.
    /// </summary>
    AroundCentre,
}

/// <summary>
/// Joins points into one outline.
/// </summary>
/// <remarks>
/// The caliper tools already give a contour; this is for points from
/// anywhere else - blob centres, a filtered subset of edge points, the
/// corners Read Code found - and for joining them in an order of your
/// choosing.
/// </remarks>
[VisionNode(
    "Points to Polygon",
    TypeKey = "fitting.pointsToPolygon",
    Category = NodeCategory.Fitting,
    Icon = "ph_points_polygon",
    Description = "Joins points into one outline, in the order they arrive or sorted round their middle, " +
                  "open or closed. Reports its perimeter, and its area when closed.")]
public sealed class PointsToPolygonNode : VisionNode
{
    private PointOrder _order = PointOrder.AsGiven;
    private bool _closed = true;

    public PointsToPolygonNode()
    {
        PointsIn = AddInput<ShapeSet>("Points");
        PolygonOut = AddOutput<ShapeSet>("Polygon");
        CountOut = AddOutput<int>("Count");
    }

    /// <summary>
    /// Points, or any shapes: a point gives itself, a polygon its corners,
    /// anything else its centre.
    /// </summary>
    public InputPort PointsIn { get; }

    public OutputPort PolygonOut { get; }

    /// <summary>How many points the outline joins.</summary>
    public OutputPort CountOut { get; }

    [Param("Order", Description = "As they arrive, or sorted by angle round their middle.")]
    public PointOrder Order
    {
        get => _order;
        set => SetParam(ref _order, value);
    }

    [Param("Closed", Description = "Join the last point back to the first. Needs at least three.")]
    public bool Closed
    {
        get => _closed;
        set => SetParam(ref _closed, value);
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var input = context.Get<ShapeSet>(PointsIn)!;
        var points = input.Shapes.SelectMany(PointsOf).ToList();

        if (_order == PointOrder.AroundCentre && points.Count > 0)
            points = AroundCentre(points);

        var polygon = Outline.Through(points, _closed, colour: null);

        if (polygon is null)
            context.Warn($"An outline needs at least two points; {points.Count} arrived.");

        context.Set(PolygonOut, new ShapeSet(polygon is null ? [] : [polygon], input.FrameWidth, input.FrameHeight));
        context.Set(CountOut, polygon is null ? 0 : points.Count);
    }

    internal static IEnumerable<Point2> PointsOf(Shape shape) => shape switch
    {
        PointShape point => [point.At],
        PolygonShape polygon => polygon.Points,
        _ => [shape.Bounds.Centre],
    };

    /// <summary>
    /// Sorted clockwise round the points' mean - angle measured with y
    /// growing downward, as everything on an image is.
    /// </summary>
    internal static List<Point2> AroundCentre(IReadOnlyList<Point2> points)
    {
        var centreX = points.Average(point => point.X);
        var centreY = points.Average(point => point.Y);

        return points
            .OrderBy(point =>
            {
                var angle = Math.Atan2(point.Y - centreY, point.X - centreX);
                return angle < 0 ? angle + (2 * Math.PI) : angle;
            })
            .ToList();
    }
}
