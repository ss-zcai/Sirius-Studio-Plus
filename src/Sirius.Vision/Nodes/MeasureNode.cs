using System.Globalization;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>What a Measure node reads between two shapes.</summary>
public enum MeasureKind
{
    /// <summary>Between the two centres.</summary>
    CentreToCentre,

    /// <summary>The closest approach of the two outlines - the gap between them.</summary>
    EdgeToEdge,

    /// <summary>The angle between the directions the two shapes run in.</summary>
    Angle,

    /// <summary>
    /// From the first shape's centre, perpendicular to the second's
    /// direction - the offset of a feature from a datum line.
    /// </summary>
    PerpendicularToB,
}

/// <summary>
/// Measures between two shapes, and draws the reading on the image.
/// </summary>
/// <remarks>
/// <para>
/// The thing that turns found geometry into a number a recipe can pass or
/// fail on. The viewer has measured interactively from the start, but
/// those readings live on the picture and nothing downstream can see them;
/// this one travels on a wire.
/// </para>
/// <para>
/// Two shape inputs rather than a way of naming other nodes' outputs. The
/// roadmap had this down as needing a reference mechanism - "the line from
/// Find Line 1, the circle from Find Circle 2" - which was true before
/// shapes flowed on wires and is not now: wiring two producers in says
/// exactly that, in the vocabulary the graph already has.
/// </para>
/// <para>
/// The first shape of each set. A measurement is between two things, and
/// picking which two out of a crowd is a filtering question that
/// Select Shape already answers - putting that here as well would be two
/// ways to say one thing.
/// </para>
/// </remarks>
[VisionNode(
    "Measure",
    TypeKey = "analysis.measure",
    Category = NodeCategory.Analysis,
    Icon = "ph_measure_p2p",
    Description = "Reads a distance or angle between two shapes and annotates it.")]
public sealed class MeasureNode : VisionNode
{
    /// <summary>Amber, the colour the viewer's own measurements are drawn in.</summary>
    private const uint Reading = 0xFFFBBF24;

    private MeasureKind _kind = MeasureKind.CentreToCentre;
    private int _decimals = 2;
    private string _units = "px";
    private bool _showLabel = true;

    public MeasureNode()
    {
        FirstIn = AddInput<ShapeSet>("A");
        SecondIn = AddInput<ShapeSet>("B");
        CalibrationIn = AddInput<Calibration>("Calibration", isRequired: false);

        ValueOut = AddOutput<double>("Value");
        AnnotationOut = AddOutput<ShapeSet>("Annotation");
    }

    public InputPort FirstIn { get; }

    public InputPort SecondIn { get; }

    /// <summary>
    /// What a pixel is worth. Without one, readings are in pixels.
    /// </summary>
    /// <remarks>
    /// Wired rather than assumed, so a recipe with two cameras in it can
    /// report each in its own scale - and so that a reading in
    /// millimetres is visibly a reading in millimetres, with the node
    /// that said so on the canvas beside it.
    ///
    /// An angle is not scaled: a scale factor is a length, and the
    /// angle between two lines is the same whatever a pixel is worth.
    /// </remarks>
    public InputPort CalibrationIn { get; }

    /// <summary>The reading, for a downstream limit check or a label.</summary>
    public OutputPort ValueOut { get; }

    /// <summary>
    /// The reading drawn: a line between the two points it was taken
    /// across, labelled with the number.
    /// </summary>
    /// <remarks>
    /// The same thing a measurement drawn by hand on the viewer looks
    /// like, which is the point - somebody checking a recipe should not
    /// have to tell a measured distance from a computed one by eye.
    /// </remarks>
    public OutputPort AnnotationOut { get; }

    [Param("Measure", Description = "What to read between the two shapes.")]
    public MeasureKind Kind
    {
        get => _kind;
        set => SetParam(ref _kind, value);
    }

    [Param("Decimals", Min = 0, Max = 6, Step = 1,
        Description = "Figures after the point in the drawn label.")]
    public int Decimals
    {
        get => _decimals;
        set => SetParam(ref _decimals, Math.Clamp(value, 0, 6));
    }

    [Param("Units",
        Description = "Written after the number. Ignored when a Calibration is wired in, which names its own.")]
    public string Units
    {
        get => _units;
        set => SetParam(ref _units, value ?? string.Empty);
    }

    [Param("Label", Description = "Draw the number beside the line.")]
    public bool ShowLabel
    {
        get => _showLabel;
        set => SetParam(ref _showLabel, value);
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var first = First(context, FirstIn, "A");
        var second = First(context, SecondIn, "B");

        if (first is null || second is null)
        {
            context.Set(ValueOut, 0d);
            context.Set(AnnotationOut, ShapeSet.Empty);
            return;
        }

        var frame = Frame(context);

        if (_kind == MeasureKind.Angle)
        {
            MeasureAngle(context, first, second, frame);
            return;
        }

        var (from, to) = _kind switch
        {
            MeasureKind.EdgeToEdge => ClosestApproach(first, second),
            MeasureKind.PerpendicularToB => Perpendicular(context, first, second),
            _ => (ShapeGeometry.Centre(first), ShapeGeometry.Centre(second)),
        };

        var calibration = Calibrated(context);
        var value = calibration.ToUnits(Distance(from, to));

        context.Set(ValueOut, value);
        context.Set(AnnotationOut, Annotate(from, to, value, frame, UnitOf(calibration)));
    }

    /// <summary>The calibration on the wire, or pixels at one to one.</summary>
    private Calibration Calibrated(EvalContext context) =>
        context.GetValue<Calibration>(CalibrationIn) ?? Calibration.None;

    /// <summary>
    /// What to write after the number: the calibration's unit when there
    /// is one, since a reading converted to millimetres and labelled
    /// "px" would be worse than either on its own.
    /// </summary>
    /// <remarks>
    /// With its own leading space: the suffix is taken literally, so
    /// that an angle can pass a degree sign that sits against the
    /// number.
    /// </remarks>
    private static string? UnitOf(Calibration calibration) =>
        calibration.IsCalibrated ? " " + calibration.Unit : null;

    /// <summary>
    /// The angle between the two shapes' directions, folded into 0..90.
    /// </summary>
    /// <remarks>
    /// Folded because a line has no head or tail: two lines three degrees
    /// apart are three degrees apart whichever end of each you measure
    /// from, and reporting 177 for that pair would be arithmetically true
    /// and useless.
    /// </remarks>
    private void MeasureAngle(EvalContext context, Shape first, Shape second, (int W, int H) frame)
    {
        var a = ShapeGeometry.Direction(first);
        var b = ShapeGeometry.Direction(second);

        if (a is null || b is null)
        {
            context.Warn(
                "An angle needs two shapes that run in a direction - a line, a fitted " +
                "ellipse or a turned rectangle. A circle has no direction.");

            context.Set(ValueOut, 0d);
            context.Set(AnnotationOut, ShapeSet.Empty);
            return;
        }

        var degrees = Math.Abs(
            (Math.Atan2(b.Value.Y, b.Value.X) - Math.Atan2(a.Value.Y, a.Value.X))
            * 180.0 / Math.PI);

        degrees %= 180;

        if (degrees > 90)
            degrees = 180 - degrees;

        context.Set(ValueOut, degrees);

        // Drawn between the two centres: there is no one place an angle
        // lives, and the line joining what was compared at least says
        // which two things they were.
        context.Set(
            AnnotationOut,
            Annotate(ShapeGeometry.Centre(first), ShapeGeometry.Centre(second), degrees, frame, "°"));
    }

    /// <summary>
    /// The nearest pair of points on the two outlines.
    /// </summary>
    /// <remarks>
    /// Two circles have a closed form and get it, because it is exact and
    /// because two circles is the case this node exists for. Everything
    /// else walks one outline against the other's distance function, which
    /// is approximate in the last fraction of a pixel and is not worth a
    /// solver per pair of shape kinds.
    /// </remarks>
    private static (Point2 From, Point2 To) ClosestApproach(Shape first, Shape second)
    {
        if (first is CircleShape a && second is CircleShape b)
        {
            var dx = b.Centre.X - a.Centre.X;
            var dy = b.Centre.Y - a.Centre.Y;
            var between = Math.Sqrt((dx * dx) + (dy * dy));

            // Only when one lies wholly outside the other. The closed form
            // walks out from each centre towards the other, which is the
            // nearest pair exactly while the circles are separate - and
            // crosses over into nonsense the moment they overlap, reporting
            // a large gap for two circles that touch. Those fall through to
            // the walk below, which handles crossing and containment alike.
            if (between > a.Radius + b.Radius)
            {
                var ux = dx / between;
                var uy = dy / between;

                return (
                    new Point2(a.Centre.X + (ux * a.Radius), a.Centre.Y + (uy * a.Radius)),
                    new Point2(b.Centre.X - (ux * b.Radius), b.Centre.Y - (uy * b.Radius)));
            }
        }

        var outline = ShapeGeometry.Outline(first);
        var other = ShapeGeometry.Outline(second);

        var best = double.MaxValue;
        var from = outline[0];
        var to = other[0];

        foreach (var point in outline)
        {
            foreach (var candidate in other)
            {
                var distance = Distance(point, candidate);

                if (distance >= best)
                    continue;

                best = distance;
                from = point;
                to = candidate;
            }
        }

        return (from, to);
    }

    /// <summary>
    /// From the first shape's centre, straight onto the second's line.
    /// </summary>
    private (Point2 From, Point2 To) Perpendicular(
        EvalContext context, Shape first, Shape second)
    {
        var centre = ShapeGeometry.Centre(first);
        var direction = ShapeGeometry.Direction(second);

        if (direction is null)
        {
            context.Warn(
                "A perpendicular needs a second shape that runs in a direction; " +
                "measuring centre to centre instead.");

            return (centre, ShapeGeometry.Centre(second));
        }

        var origin = ShapeGeometry.Centre(second);
        var along = ((centre.X - origin.X) * direction.Value.X) +
                    ((centre.Y - origin.Y) * direction.Value.Y);

        return (
            centre,
            new Point2(
                origin.X + (along * direction.Value.X),
                origin.Y + (along * direction.Value.Y)));
    }

    private ShapeSet Annotate(
        Point2 from, Point2 to, double value, (int W, int H) frame, string? units = null)
    {
        var text = string.Format(
            CultureInfo.InvariantCulture,
            "{0:F" + _decimals + "}{1}",
            value,
            units ?? (string.IsNullOrEmpty(_units) ? string.Empty : " " + _units));

        var line = new LineShape(from, to)
        {
            Colour = Reading,
            StrokeWidth = 2,
            Label = _showLabel ? text : null,
            Metrics = new Dictionary<string, double>
            {
                // The reading as reported, so a Calibrate node downstream
                // taking this as a reference gets the number it drew.
                [ShapeMetric.Length] = value,
                [ShapeMetric.CentroidX] = (from.X + to.X) / 2,
                [ShapeMetric.CentroidY] = (from.Y + to.Y) / 2,
            },
        };

        return new ShapeSet([line], frame.W, frame.H);
    }

    /// <summary>
    /// The first shape of a set, or null with a reason.
    /// </summary>
    private static Shape? First(EvalContext context, InputPort port, string name)
    {
        var set = context.Get<ShapeSet>(port);

        if (set is not { Count: > 0 })
        {
            context.Warn($"Input {name} has no shape to measure from.");
            return null;
        }

        if (set.Count > 1)
        {
            context.Warn(
                $"Input {name} carries {set.Count} shapes; measuring from the first. " +
                "Use Select Shape to choose which.");
        }

        return set.Shapes[0];
    }

    /// <summary>The frame the annotation belongs to, taken from whichever input knows.</summary>
    private (int W, int H) Frame(EvalContext context)
    {
        foreach (var port in new[] { FirstIn, SecondIn })
        {
            if (context.Get<ShapeSet>(port) is { FrameWidth: > 0, FrameHeight: > 0 } set)
                return (set.FrameWidth, set.FrameHeight);
        }

        return (0, 0);
    }

    private static double Distance(Point2 a, Point2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;

        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
