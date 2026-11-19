using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>How the four sides' edge points are made into a rectangle.</summary>
public enum RectangleFitMode
{
    /// <summary>All the points at once, the sides parallel and square.</summary>
    Rectangle,

    /// <summary>Each side alone, the corners where neighbouring sides meet.</summary>
    FourLines,
}

/// <summary>
/// Finds a rectangular outline - a board's edge, a chip's body - inside a
/// band round the rectangle expected.
/// </summary>
/// <remarks>
/// <para>
/// Find Line's calipers, cast across each of the four sides of a turned
/// rectangle: the region is drawn round the part as it should lie, and the
/// calipers look <b>Search Width</b> either side of each of its sides. Each
/// side's calipers stop short of the corners by that much, so a caliper
/// near a corner cannot take the neighbouring side's edge.
/// </para>
/// <para>
/// <b>Direction</b> is the way every caliper crosses its side - from outside
/// the rectangle in, or from inside out - and is what Polarity and Edge
/// are relative to: a light board on a dark bench is Dark to Light going
/// Inward on all four sides alike, which one direction per side would not
/// give.
/// </para>
/// <para>
/// The region can be wired from a match, as Find Line's can, so the search
/// follows a part that moves.
/// </para>
/// </remarks>
[VisionNode(
    "Find Rectangle",
    TypeKey = "fitting.findrectangle",
    Category = NodeCategory.Fitting,
    Icon = "ph_find_rectangle",
    Description = "Casts calipers across the four sides of a rectangle and fits a rectangle to the edges they find.")]
public sealed class FindRectangleNode : VisionNode
{
    /// <summary>Green for the result, amber for the evidence behind it, blue for where it looked.</summary>
    private const uint Found = 0xFF22C55E;

    private const uint Evidence = 0xFFFBBF24;

    private const uint Searched = 0xFF3B82F6;

    private OrientedRect _region = new(320, 240, 300, 200, 0);
    private double _searchWidth = 20;
    private RadialDirection _direction = RadialDirection.Inward;
    private int _caliperCount = 12;
    private int _caliperWidth = 5;
    private double _spacing = 0.5;
    private int _smoothing = 5;
    private double _minimumContrast = 3;
    private EdgePolarity _polarity = EdgePolarity.Either;
    private EdgeChoice _choice = EdgeChoice.Strongest;
    private RectangleFitMode _fit = RectangleFitMode.Rectangle;
    private bool _square;
    private double _rejectSigma = 2.5;

    public FindRectangleNode()
    {
        ImageIn = AddInput<ImageData>("In");
        RectangleOut = AddOutput<ShapeSet>("Rectangle");
        SidesOut = AddOutput<ShapeSet>("Sides");
        CornersOut = AddOutput<ShapeSet>("Corners");
        PointsOut = AddOutput<ShapeSet>("Points");
        SearchOut = AddOutput<ShapeSet>("Search");
        LengthOut = AddOutput<double>("Length");
        WidthOut = AddOutput<double>("Width");
        AngleOut = AddOutput<double>("Angle");
    }

    public InputPort ImageIn { get; }

    /// <summary>
    /// The rectangle found: a turned rectangle for the Rectangle fit, which
    /// Measure reads as it reads any; for Four Lines, the four corners as an
    /// outline, which need not be square.
    /// </summary>
    public OutputPort RectangleOut { get; }

    /// <summary>The four sides, top, right, bottom, left, each between its corners.</summary>
    public OutputPort SidesOut { get; }

    /// <summary>The corners, top left first and round clockwise, each with its angle.</summary>
    public OutputPort CornersOut { get; }

    /// <summary>The edge points the fit used, each with the side it was found on.</summary>
    public OutputPort PointsOut { get; }

    /// <summary>Where it looked: the band's inner and outer edges, to check the band covers the part's edge.</summary>
    public OutputPort SearchOut { get; }

    /// <summary>The longer pair of sides' length.</summary>
    public OutputPort LengthOut { get; }

    /// <summary>The shorter pair's.</summary>
    public OutputPort WidthOut { get; }

    /// <summary>The turn of the rectangle's top, in degrees clockwise.</summary>
    public OutputPort AngleOut { get; }

    // ---- where to look -----------------------------------------------------

    [Param("Region",
        Editor = ParamEditor.Rect,
        Connectable = true,
        Description = "The rectangle expected, as x, y, w, h, angle: drawn round the part as it should lie. Its top is the side the angle turns.")]
    public OrientedRect Region
    {
        get => _region;
        set => SetParam(ref _region, value);
    }

    [Param("Search Width", Min = 1, Max = 500, Step = 1,
        Description = "How far either side of each of the region's sides to look for the edge, in pixels.")]
    public double SearchWidth
    {
        get => _searchWidth;
        set => SetParam(ref _searchWidth, Math.Clamp(value, 1, 500));
    }

    [Param("Direction", Group = "Edge",
        Description = "Which way every caliper crosses its side: from outside in, or inside out. Polarity and Edge are relative to it.")]
    public RadialDirection Direction
    {
        get => _direction;
        set => SetParam(ref _direction, value);
    }

    // ---- how to look -------------------------------------------------------

    [Param("Calipers", Group = "Edge", Min = 2, Max = 200, Step = 1,
        Description = "How many scans to cast across each side.")]
    public int CaliperCount
    {
        get => _caliperCount;
        set => SetParam(ref _caliperCount, Math.Clamp(value, 2, 200));
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

    // ---- the fit -----------------------------------------------------------

    [Param("Fit", Group = "Fit",
        Description = "Rectangle: all the points at once, sides parallel and square - the steadiest length and width. Four Lines: each side alone, for a part not truly square or a camera at an angle.")]
    public RectangleFitMode Fit
    {
        get => _fit;
        set => SetParam(ref _fit, value);
    }

    [Param("Square", Group = "Fit",
        Description = "Length and width held equal, for a square part such as a chip. With the Rectangle fit.")]
    public bool Square
    {
        get => _square;
        set => SetParam(ref _square, value);
    }

    [Param("Reject Above", Group = "Fit", Min = 0, Max = 6, Step = 0.1,
        Description = "Drop points this many standard deviations off their side, then fit again. Zero keeps all.")]
    public double RejectSigma
    {
        get => _rejectSigma;
        set => SetParam(ref _rejectSigma, Math.Clamp(value, 0, 6));
    }

    // ---- running -----------------------------------------------------------

    protected override void OnEvaluate(EvalContext context)
    {
        if (!context.TryGet<ImageData>(ImageIn, out var image))
            throw new InvalidOperationException("No image is connected.");

        var region = context.Param(nameof(Region), _region);

        using var sampler = LineProfileSource.From(image);

        var settings = new CaliperSettings(
            _caliperCount, _caliperWidth, _spacing, _smoothing, _minimumContrast, _polarity, _choice);

        var scan = _direction == RadialDirection.Inward ? ScanDirection.Across : ScanDirection.Back;

        var edges = new List<EdgePoint>[4];
        for (var side = 0; side < 4; side++)
        {
            edges[side] = CaliperScan.AcrossRect(
                sampler, Band(region, (RectangleSide)side, _searchWidth), scan, settings, context.CancellationToken);
        }

        var missed = Enumerable.Range(0, 4)
            .Where(side => edges[side].Count < RectangleFit.MinimumPerSide)
            .Select(side => ((RectangleSide)side).ToString().ToLowerInvariant())
            .ToArray();

        if (missed.Length > 0)
        {
            context.Warn(missed.Length == 1
                ? $"No edge was found on the {missed[0]} side. Check the band covers it, and lower Min Contrast."
                : $"No edge was found on the {string.Join(", ", missed[..^1])} and {missed[^1]} sides. Check the band covers them, and lower Min Contrast.");

            Publish(context, image, region, null, edges);
            return;
        }

        var points = edges.Select(side => (IReadOnlyList<Point2>)side.Select(edge => edge.At).ToArray()).ToArray();

        var fit = _fit == RectangleFitMode.FourLines
            ? RectangleFit.FitFourLines(points, _rejectSigma)
            : RectangleFit.FitRobust(points, region.Angle, _square, _rejectSigma);

        if (!fit.Success)
        {
            context.Warn("The edge points do not describe a rectangle. Check each side of the band covers its edge.");
            Publish(context, image, region, null, edges);
            return;
        }

        // Said rather than left to be noticed: a side found by a couple of
        // calipers out of a dozen can still give a plausible rectangle.
        for (var side = 0; side < 4; side++)
        {
            if (fit.Used[side].Count < _caliperCount / 2)
            {
                context.Warn(
                    $"Only {fit.Used[side].Count} of {_caliperCount} calipers found a usable edge on the {((RectangleSide)side).ToString().ToLowerInvariant()} side.");
                break;
            }
        }

        var kept = edges
            .Select((side, s) => FindCircleNode.Kept(side, fit.Used[s]).ToList())
            .ToArray();

        Publish(context, image, region, fit, kept);
    }

    /// <summary>
    /// The band one side's calipers cross: centred on that side of the
    /// region, Search Width either side of it, and stopping that far short
    /// of each corner so as not to take the next side's edge. Turned a
    /// quarter for each side round, so every band's Across runs from
    /// outside the region in.
    /// </summary>
    internal static OrientedRect Band(OrientedRect region, RectangleSide side, double searchWidth)
    {
        var horizontal = side is RectangleSide.Top or RectangleSide.Bottom;
        var length = horizontal ? region.Width : region.Height;
        var reach = horizontal ? region.Height / 2 : region.Width / 2;

        var (along, across) = side switch
        {
            RectangleSide.Top => (0.0, -reach),
            RectangleSide.Right => (reach, 0.0),
            RectangleSide.Bottom => (0.0, reach),
            _ => (-reach, 0.0),
        };

        var centre = CaliperScan.InRect(region, along, across);
        var inset = Math.Max(length * 0.2, length - (2 * searchWidth));

        return new OrientedRect(centre.X, centre.Y, inset, 2 * searchWidth, region.Angle + (90 * (int)side));
    }

    private void Publish(
        EvalContext context, ImageData image, OrientedRect region, RectangleFitResult? fit, IReadOnlyList<IReadOnlyList<EdgePoint>> edges)
    {
        var (width, height) = (image.Width, image.Height);

        var rectangles = new List<Shape>();
        var sides = new List<Shape>();
        var corners = new List<Shape>();
        var (length, breadth, angle) = (0d, 0d, 0d);

        if (fit is { Success: true } found)
        {
            var centre = found.Centre;
            length = Math.Max(found.Width, found.Height);
            breadth = Math.Min(found.Width, found.Height);
            angle = found.AngleDegrees;

            var metrics = new Dictionary<string, double>
            {
                [ShapeMetric.CentroidX] = centre.X,
                [ShapeMetric.CentroidY] = centre.Y,
                [ShapeMetric.Length] = length,
                [ShapeMetric.Width] = breadth,
                [ShapeMetric.Angle] = angle,
                [ShapeMetric.FitError] = found.Error,
                [ShapeMetric.FitPoints] = found.Used.Sum(side => side.Count),
            };

            Shape outline = _fit == RectangleFitMode.Rectangle
                ? new OrientedRectShape(centre, found.Width, found.Height, angle)
                : new PolygonShape(found.Corners, Closed: true);

            rectangles.Add(outline with { Colour = Found, StrokeWidth = 2, Metrics = metrics });

            for (var side = 0; side < 4; side++)
            {
                var (from, to) = (found.Corners[side], found.Corners[(side + 1) % 4]);

                sides.Add(new LineShape(from, to)
                {
                    Colour = Found,
                    StrokeWidth = 2,
                    Metrics = new Dictionary<string, double>
                    {
                        [ShapeMetric.CentroidX] = (from.X + to.X) / 2,
                        [ShapeMetric.CentroidY] = (from.Y + to.Y) / 2,
                        [ShapeMetric.Length] = from.DistanceTo(to),
                        [ShapeMetric.Angle] = Math.Atan2(to.Y - from.Y, to.X - from.X) * 180 / Math.PI,
                    },
                });
            }

            var cornerAngles = found.CornerAngles();

            for (var i = 0; i < 4; i++)
            {
                var at = found.Corners[i];

                corners.Add(new PointShape(at)
                {
                    Colour = Found,
                    Metrics = new Dictionary<string, double>
                    {
                        [ShapeMetric.CentroidX] = at.X,
                        [ShapeMetric.CentroidY] = at.Y,
                        [ShapeMetric.Angle] = cornerAngles[i],
                    },
                });
            }
        }

        context.Set(RectangleOut, new ShapeSet(rectangles, width, height));
        context.Set(SidesOut, new ShapeSet(sides, width, height));
        context.Set(CornersOut, new ShapeSet(corners, width, height));

        // Each point says which side it was found on, 0 to 3 from the top
        // round clockwise, as well as where along it.
        var points = edges
            .SelectMany((side, s) => side.Select(edge =>
            {
                var point = CaliperScan.LinePoint(edge, Evidence);
                var metrics = new Dictionary<string, double>(point.Metrics) { ["side"] = s };
                return (Shape)(point with { Metrics = metrics });
            }))
            .ToArray();

        context.Set(PointsOut, new ShapeSet(points, width, height));

        // The band's two edges, as outlines round the region.
        var outer = region with { Width = region.Width + (2 * _searchWidth), Height = region.Height + (2 * _searchWidth) };
        var inner = region with
        {
            Width = Math.Max(1, region.Width - (2 * _searchWidth)),
            Height = Math.Max(1, region.Height - (2 * _searchWidth)),
        };

        Shape Outline(OrientedRect r) =>
            new OrientedRectShape(new Point2(r.CentreX, r.CentreY), r.Width, r.Height, r.Angle) { Colour = Searched, StrokeWidth = 1 };

        context.Set(SearchOut, new ShapeSet([Outline(outer), Outline(inner)], width, height));

        context.Set(LengthOut, length);
        context.Set(WidthOut, breadth);
        context.Set(AngleOut, angle);
    }
}
