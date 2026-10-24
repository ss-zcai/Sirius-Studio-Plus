using System.Drawing;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>Which way the calipers sweep across the band.</summary>
public enum RadialDirection
{
    /// <summary>From the inner radius outwards.</summary>
    Outward,

    /// <summary>From the outer radius inwards.</summary>
    Inward,
}

/// <summary>
/// Finds a circular edge inside an annular search band.
/// </summary>
/// <remarks>
/// <para>
/// The same answer Find Blobs and Fit Circle together will give on a clean
/// image, arrived at directly. Blob finding has to threshold the whole
/// frame, label every connected region, measure them all and discard the
/// ones that are not wanted; this looks only along a few dozen short lines
/// where the edge is expected, and never visits the rest of the image. On
/// a 2800 x 2400 frame that is the difference between megapixels of work
/// and a few thousand samples.
/// </para>
/// <para>
/// It is also the more robust of the two where the part is lit unevenly. A
/// threshold is one number for the whole image and a gradient across the
/// field defeats it; a caliper asks only that the edge be locally the
/// steepest thing along its own short scan, which stays true when one side
/// of the part is brighter than the other.
/// </para>
/// <para>
/// What it gives up is discovery. It finds the circle you told it roughly
/// where to look for; Find Blobs finds circles you did not know were there.
/// The ring is the tool's whole prior, which is why a wrong one produces
/// nothing rather than a wrong answer.
/// </para>
/// </remarks>
[VisionNode(
    "Find Circle",
    TypeKey = "fitting.findcircle",
    Category = NodeCategory.Fitting,
    Icon = "ph_fit_circle",
    Description = "Casts radial calipers through a ring and fits a circle to the edges they find.")]
public sealed class FindCircleNode : VisionNode
{
    private Ring _band = new(500, 500, 80, 140);
    private double _startAngle;
    private double _sweepAngle = 360;
    private int _caliperCount = 48;
    private int _caliperWidth = 5;
    private double _spacing = 0.5;
    private int _smoothing = 5;
    private double _minimumContrast = 3;
    private EdgePolarity _polarity = EdgePolarity.Either;
    private EdgeChoice _choice = EdgeChoice.Strongest;
    private RadialDirection _direction = RadialDirection.Outward;
    private double _rejectSigma = 2.5;

    public FindCircleNode()
    {
        ImageIn = AddInput<ImageData>("In");
        CircleOut = AddOutput<ShapeSet>("Circle");
        PointsOut = AddOutput<ShapeSet>("Points");
        RadiusOut = AddOutput<double>("Radius");
    }

    public InputPort ImageIn { get; }

    /// <summary>The fitted circle, as a one-shape set ready for a viewer.</summary>
    public OutputPort CircleOut { get; }

    /// <summary>
    /// The edge points the calipers found, after outlier rejection.
    /// </summary>
    /// <remarks>
    /// Published because a circle finder that returns only a circle is
    /// impossible to diagnose. Drawn on the image beside the fit, a bad
    /// result says immediately whether the calipers missed the edge, found
    /// the wrong one, or found it correctly and the fit is being dragged by
    /// a handful of strays.
    /// </remarks>
    public OutputPort PointsOut { get; }

    public OutputPort RadiusOut { get; }

    // ---- where to look -------------------------------------------------

    [Param("Band",
        Editor = ParamEditor.Ring,
        Connectable = true,
        Description = "Search band as x, y, inner, outer. Draw it on the image: centre, then the edge, then the width.")]
    public Ring Band
    {
        get => _band;
        set => SetParam(ref _band, value);
    }

    [Param("Start Angle", Group = "Ring", Min = -360, Max = 360, Step = 1,
        Description = "Degrees clockwise from the positive X axis.")]
    public double StartAngle
    {
        get => _startAngle;
        set => SetParam(ref _startAngle, value);
    }

    [Param("Sweep", Group = "Ring", Min = 1, Max = 360, Step = 1,
        Description = "How much of the ring to search. Less than 360 searches an arc.")]
    public double SweepAngle
    {
        get => _sweepAngle;
        set => SetParam(ref _sweepAngle, Math.Clamp(value, 1, 360));
    }

    // ---- how to look ---------------------------------------------------

    [Param("Calipers", Group = "Edge", Min = 3, Max = 720, Step = 1,
        Description = "How many radial scans to cast. More is steadier and slower.")]
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

    [Param("Reject Above", Group = "Fit", Min = 0, Max = 6, Step = 0.1,
        Description = "Drop points this many standard deviations off the fit, then fit again. Zero keeps all.")]
    public double RejectSigma
    {
        get => _rejectSigma;
        set => SetParam(ref _rejectSigma, Math.Clamp(value, 0, 6));
    }

    protected override void OnEvaluate(EvalContext context)
    {
        if (!context.TryGet<ImageData>(ImageIn, out var image))
            throw new InvalidOperationException("No image is connected.");

        // Ring orders and widens on construction, so there is no
        // inside-out or zero-width band to check for here.
        var band = context.Param(nameof(Band), _band);

        using var sampler = LineProfileSource.From(image);

        var points = Scan(context, sampler, band);

        if (points.Count < CircleFit.MinimumPoints)
        {
            context.Warn(points.Count == 0
                ? "No edge was found in the ring. Check the band covers the edge, and lower Min Contrast."
                : $"Only {points.Count} edge points were found; a circle needs {CircleFit.MinimumPoints}.");

            Publish(context, null, points);
            return;
        }

        var fit = CircleFit.FitRobust(points, _rejectSigma);

        if (!fit.Success)
        {
            context.Warn("The edge points do not describe a circle.");
            Publish(context, null, points);
            return;
        }

        // Said rather than left to be noticed: a fit over a short arc of a
        // mostly-missed edge can look perfectly good as a number.
        if (fit.Used.Count < _caliperCount / 2)
        {
            context.Warn(
                $"Only {fit.Used.Count} of {_caliperCount} calipers found a usable edge.");
        }

        Publish(context, fit, fit.Used);
    }

    /// <summary>
    /// Casts the calipers and collects the edges they find.
    /// </summary>
    private List<Point2> Scan(EvalContext context, LineProfileSource sampler, Ring band)
    {
        var centreX = band.CentreX;
        var centreY = band.CentreY;
        var inner = band.InnerRadius;
        var outer = band.OuterRadius;

        var points = new List<Point2>(_caliperCount);

        // The last caliper lands on the first when the sweep is a full
        // circle, so it is dropped - two readings of one angle would weight
        // that part of the edge twice.
        var closed = _sweepAngle >= 359.999;
        var steps = closed ? _caliperCount : Math.Max(1, _caliperCount - 1);
        var step = _sweepAngle / steps;

        for (var i = 0; i < _caliperCount; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var radians = (_startAngle + (i * step)) * Math.PI / 180.0;
            var ux = Math.Cos(radians);
            var uy = Math.Sin(radians);

            var (fromRadius, toRadius) = _direction == RadialDirection.Outward
                ? (inner, outer)
                : (outer, inner);

            var start = new PointF(
                (float)(centreX + (ux * fromRadius)),
                (float)(centreY + (uy * fromRadius)));

            var end = new PointF(
                (float)(centreX + (ux * toRadius)),
                (float)(centreY + (uy * toRadius)));

            var profile = sampler.Sample(
                start,
                end,
                _spacing,
                _caliperWidth,
                ProfileInterpolation.Linear,
                warn: null,
                context.CancellationToken);

            if (profile.Channels.Count == 0)
                continue;

            // One channel: a colour frame is read as its first channel
            // rather than averaged, because averaging can cancel an edge
            // that is strong in one channel and reversed in another.
            var samples = profile.Channels[0].Samples;

            if (!Caliper.TryFind(
                    samples, _spacing, _polarity, _minimumContrast, _smoothing, _choice, out var hit))
            {
                continue;
            }

            var radius = _direction == RadialDirection.Outward
                ? fromRadius + hit.Offset
                : fromRadius - hit.Offset;

            points.Add(new Point2(centreX + (ux * radius), centreY + (uy * radius)));
        }

        return points;
    }

    private void Publish(EvalContext context, CircleFitResult? fit, IReadOnlyList<Point2> points)
    {
        var width = 0;
        var height = 0;

        if (context.TryGet<ImageData>(ImageIn, out var image))
        {
            width = image.Width;
            height = image.Height;
        }

        var circles = new List<Shape>();

        if (fit is { Success: true } result)
        {
            circles.Add(result.ToShape() with
            {
                Metrics = new Dictionary<string, double>
                {
                    [ShapeMetric.CentroidX] = result.Centre.X,
                    [ShapeMetric.CentroidY] = result.Centre.Y,
                    [ShapeMetric.Radius] = result.Radius,
                    [ShapeMetric.FitError] = result.Error,
                    [ShapeMetric.FitPoints] = result.Used.Count,
                },
            });
        }

        context.Set(CircleOut, new ShapeSet(circles, width, height));

        context.Set(
            PointsOut,
            new ShapeSet(points.Select(p => (Shape)new PointShape(p)).ToArray(), width, height));

        context.Set(RadiusOut, fit is { Success: true } found ? found.Radius : 0d);
    }
}
