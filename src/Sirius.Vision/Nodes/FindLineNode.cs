using System.Drawing;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>Which way the calipers cross the search band.</summary>
public enum ScanDirection
{
    /// <summary>Across the region, from one long side to the other.</summary>
    Across,

    /// <summary>The same, reversed - which swaps what Polarity means.</summary>
    Back,
}

/// <summary>
/// Finds a straight edge inside a rectangular search region.
/// </summary>
/// <remarks>
/// <para>
/// The same caliper core as Find Circle, aimed along a rectangle instead of
/// around a ring. Scans are spaced along the region's length and cross its
/// width, so the region is placed with its long axis running <em>along</em>
/// the expected edge and its width spanning where the edge might be.
/// </para>
/// <para>
/// The region is an <see cref="OrientedRect"/>, the same type the Region of
/// Interest node uses, so it is typed as "x, y, w, h, angle" and can be
/// drawn and turned on the image viewer with the machinery that already
/// exists for ROIs.
/// </para>
/// </remarks>
[VisionNode(
    "Find Line",
    TypeKey = "fitting.findline",
    Category = NodeCategory.Fitting,
    Icon = "ph_fit_line",
    Description = "Casts calipers across a rectangle and fits a line to the edges they find.")]
public sealed class FindLineNode : VisionNode
{
    /// <summary>Green for the result, amber for the evidence behind it.</summary>
    private const uint Found = 0xFF22C55E;

    private const uint Evidence = 0xFFFBBF24;

    private OrientedRect _region = new(300, 200, 240, 60, 0);
    private int _caliperCount = 24;
    private int _caliperWidth = 5;
    private double _spacing = 0.5;
    private int _smoothing = 5;
    private double _minimumContrast = 3;
    private EdgePolarity _polarity = EdgePolarity.Either;
    private EdgeChoice _choice = EdgeChoice.Strongest;
    private ScanDirection _direction = ScanDirection.Across;
    private double _rejectSigma = 2.5;

    public FindLineNode()
    {
        ImageIn = AddInput<ImageData>("In");
        LineOut = AddOutput<ShapeSet>("Line");
        PointsOut = AddOutput<ShapeSet>("Points");
        AngleOut = AddOutput<double>("Angle");
    }

    public InputPort ImageIn { get; }

    /// <summary>The fitted segment, spanning the points it was fitted to.</summary>
    public OutputPort LineOut { get; }

    /// <summary>
    /// The edge points found, after rejection - so a bad result can be
    /// read rather than guessed at.
    /// </summary>
    public OutputPort PointsOut { get; }

    /// <summary>Degrees, measured from the positive X axis.</summary>
    public OutputPort AngleOut { get; }

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
        Description = "How many scans to cast along the region.")]
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

    [Param("Reject Above", Group = "Fit", Min = 0, Max = 6, Step = 0.1,
        Description = "Drop points this many standard deviations off the fit, then fit again.")]
    public double RejectSigma
    {
        get => _rejectSigma;
        set => SetParam(ref _rejectSigma, Math.Clamp(value, 0, 6));
    }

    protected override void OnEvaluate(EvalContext context)
    {
        if (!context.TryGet<ImageData>(ImageIn, out var image))
            throw new InvalidOperationException("No image is connected.");

        // No zero-area check: OrientedRect clamps both sides to at least
        // one pixel, so a region without area cannot be constructed.
        var region = context.Param(nameof(Region), _region);

        using var sampler = LineProfileSource.From(image);

        var points = Scan(context, sampler, region);

        if (points.Count < LineFit.MinimumPoints)
        {
            context.Warn(points.Count == 0
                ? "No edge was found in the region. Check it spans the edge, and lower Min Contrast."
                : $"Only {points.Count} edge points were found; a line needs {LineFit.MinimumPoints}.");

            Publish(context, image, null, points);
            return;
        }

        var fit = LineFit.FitRobust(points, _rejectSigma);

        if (!fit.Success)
        {
            context.Warn("The edge points do not describe a line.");
            Publish(context, image, null, points);
            return;
        }

        if (fit.Used.Count < _caliperCount / 2)
            context.Warn($"Only {fit.Used.Count} of {_caliperCount} calipers found a usable edge.");

        Publish(context, image, fit, fit.Used);
    }

    /// <summary>
    /// Casts the calipers across the region and collects what they find.
    /// </summary>
    /// <remarks>
    /// Scans are spaced along the region's own X axis and run along its Y
    /// axis, so turning the region turns the scans with it. Everything is
    /// computed in the region's frame and rotated out at the end, which
    /// keeps the rotation in one place - the lesson from the oriented ROI,
    /// where it was spread across three calculations and wrong in two.
    /// </remarks>
    private List<Point2> Scan(EvalContext context, LineProfileSource sampler, OrientedRect region)
    {
        var points = new List<Point2>(_caliperCount);

        var radians = region.Angle * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        var halfLength = region.Width / 2;
        var halfWidth = region.Height / 2;

        var step = _caliperCount > 1 ? region.Width / (_caliperCount - 1) : 0;

        for (var i = 0; i < _caliperCount; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var along = _caliperCount > 1 ? -halfLength + (i * step) : 0;

            var (fromAcross, toAcross) = _direction == ScanDirection.Across
                ? (-halfWidth, halfWidth)
                : (halfWidth, -halfWidth);

            var start = ToImage(region, cos, sin, along, fromAcross);
            var end = ToImage(region, cos, sin, along, toAcross);

            var profile = sampler.Sample(
                new PointF((float)start.X, (float)start.Y),
                new PointF((float)end.X, (float)end.Y),
                _spacing,
                _caliperWidth,
                ProfileInterpolation.Linear,
                warn: null,
                context.CancellationToken);

            if (profile.Channels.Count == 0)
                continue;

            if (!Caliper.TryFind(
                    profile.Channels[0].Samples,
                    _spacing,
                    _polarity,
                    _minimumContrast,
                    _smoothing,
                    _choice,
                    out var hit))
            {
                continue;
            }

            var across = _direction == ScanDirection.Across
                ? fromAcross + hit.Offset
                : fromAcross - hit.Offset;

            points.Add(ToImage(region, cos, sin, along, across));
        }

        return points;
    }

    /// <summary>A point in the region's own frame, in image coordinates.</summary>
    private static Point2 ToImage(
        OrientedRect region, double cos, double sin, double along, double across) =>
        new(
            region.CentreX + (along * cos) - (across * sin),
            region.CentreY + (along * sin) + (across * cos));

    private void Publish(
        EvalContext context, ImageData image, LineFitResult? fit, IReadOnlyList<Point2> points)
    {
        var lines = new List<Shape>();
        var angle = 0d;

        if (fit is { Success: true } result)
        {
            var shape = result.ToShape();
            var length = Math.Sqrt(
                Math.Pow(shape.End.X - shape.Start.X, 2) + Math.Pow(shape.End.Y - shape.Start.Y, 2));

            angle = result.AngleDegrees;

            lines.Add(shape with
            {
                Colour = Found,
                StrokeWidth = 2,
                Metrics = new Dictionary<string, double>
                {
                    [ShapeMetric.CentroidX] = (shape.Start.X + shape.End.X) / 2,
                    [ShapeMetric.CentroidY] = (shape.Start.Y + shape.End.Y) / 2,
                    [ShapeMetric.Length] = length,
                    [ShapeMetric.Angle] = angle,
                    [ShapeMetric.FitError] = result.Error,
                    [ShapeMetric.FitPoints] = result.Used.Count,
                },
            });
        }

        context.Set(LineOut, new ShapeSet(lines, image.Width, image.Height));

        context.Set(
            PointsOut,
            new ShapeSet(
                points
                    .Select(p => (Shape)new PointShape(p) { Colour = Evidence })
                    .ToArray(),
                image.Width,
                image.Height));

        context.Set(AngleOut, angle);
    }
}
