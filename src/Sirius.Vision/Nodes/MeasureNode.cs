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

    // ---- of one shape: A alone --------------------------------------------

    /// <summary>A circle's radius; an ellipse's mean radius.</summary>
    Radius,

    /// <summary>A circle's diameter; an ellipse's mean diameter.</summary>
    Diameter,

    /// <summary>An ellipse's long axis, end to end; a circle's diameter; a blob's length.</summary>
    MajorAxis,

    /// <summary>An ellipse's short axis, end to end; a circle's diameter; a blob's width.</summary>
    MinorAxis,

    /// <summary>
    /// Which way the shape runs, in degrees clockwise from the positive X
    /// axis, 0 to 180: a line, an ellipse's long axis, a turned rectangle.
    /// </summary>
    Orientation,

    /// <summary>The area inside the shape.</summary>
    Area,

    /// <summary>The length round the shape - a circle's circumference.</summary>
    Perimeter,

    /// <summary>A line's length, end to end; an open outline's length along it.</summary>
    Length,
}

/// <summary>
/// Measures between two shapes, or one shape on its own, and draws the
/// reading on the image.
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
/// <para>
/// Radius to Length read one shape, wired into A alone: a fitted circle's
/// radius or diameter, an ellipse's axes, a shape's area or perimeter.
/// The number comes out on the same Value, in a Calibration's unit when
/// one is wired, and is drawn the way the others are - a radius as a
/// line from the centre to the edge, an axis as a line across, an area
/// as the outline itself - so a Limit Check or Grade downstream colours
/// it like any other reading.
/// </para>
/// </remarks>
[VisionNode(
    "Measure",
    TypeKey = "analysis.measure",
    Category = NodeCategory.Analysis,
    Icon = "ph_measure_p2p",
    Description = "Reads a distance or angle between two shapes - or the radius, diameter, axes, orientation, " +
                  "area or perimeter of one - and annotates it.")]
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

        // Optional, because Radius to Length read A alone. The two-shape
        // readings say so when B is missing.
        SecondIn = AddInput<ShapeSet>("B", isRequired: false);
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

    [Param("Measure",
        Description = "Between A and B: centre to centre, edge to edge, angle, perpendicular. Of A alone: " +
                      "radius, diameter, major or minor axis, orientation, area, perimeter, length.")]
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

    /// <summary>Whether a reading is of A alone rather than between A and B.</summary>
    public static bool IsOfOneShape(MeasureKind kind) => kind >= MeasureKind.Radius;

    protected override void OnEvaluate(EvalContext context)
    {
        if (IsOfOneShape(_kind))
        {
            MeasureOne(context);
            return;
        }

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

    /// <summary>What a single-shape reading is: a length, an area, or an angle.</summary>
    private enum Quantity
    {
        Length,
        Area,
        Degrees,
    }

    /// <summary>
    /// A reading of one shape, in pixels (or square pixels, or degrees),
    /// with what to draw for it: a line between two points, or the shape's
    /// own outline.
    /// </summary>
    private readonly record struct OneReading(
        double Pixels, Quantity Quantity, Point2 From, Point2 To, bool DrawOutline = false);

    private void MeasureOne(EvalContext context)
    {
        if (SecondIn.IsConnected)
            context.Warn($"{TypeNames.Words(_kind.ToString())} is read from A alone; B is not used.");

        var shape = First(context, FirstIn, "A");
        var frame = Frame(context);

        if (shape is null || Read(context, shape, _kind) is not { } reading)
        {
            context.Set(ValueOut, 0d);
            context.Set(AnnotationOut, ShapeSet.Empty);
            return;
        }

        var calibration = Calibrated(context);

        var (value, units) = reading.Quantity switch
        {
            Quantity.Degrees => (reading.Pixels, "°"),
            Quantity.Area => (
                calibration.AreaToUnits(reading.Pixels),
                calibration.IsCalibrated
                    ? $" {calibration.Unit}²"
                    : string.IsNullOrEmpty(_units) ? string.Empty : $" {_units}²"),
            _ => (calibration.ToUnits(reading.Pixels), UnitOf(calibration)),
        };

        context.Set(ValueOut, value);

        if (reading.DrawOutline)
        {
            context.Set(AnnotationOut, Outlined(shape, value, frame, units, reading.Quantity));
            return;
        }

        context.Set(AnnotationOut, Annotate(reading.From, reading.To, value, frame, units));
    }

    /// <summary>
    /// What <paramref name="kind"/> reads from <paramref name="shape"/>, or
    /// null - having said why - when the shape has no such thing.
    /// </summary>
    /// <remarks>
    /// Worked out from the geometry where the shape is one - a circle's
    /// area is pi r squared, whatever its producer measured - and taken
    /// from the measurements a producer attached where it is not: a blob's
    /// area is counted pixels, which no outline reproduces exactly.
    /// </remarks>
    private static OneReading? Read(EvalContext context, Shape shape, MeasureKind kind)
    {
        var centre = ShapeGeometry.Centre(shape);

        switch (kind, shape)
        {
            case (MeasureKind.Radius, CircleShape circle):
                return Radial(centre, circle.Radius, diameter: false);

            case (MeasureKind.Diameter, CircleShape circle):
                return Radial(centre, circle.Radius, diameter: true);

            case (MeasureKind.Radius or MeasureKind.Diameter, EllipseShape ellipse):
                context.Warn("An ellipse has no single radius; this is the mean of its two axes.");
                return Radial(centre, (ellipse.MajorAxis + ellipse.MinorAxis) / 4, kind == MeasureKind.Diameter);

            case (MeasureKind.Radius or MeasureKind.Diameter, _) when shape.Metric(ShapeMetric.Radius) is { } radius:
                return Radial(centre, radius, kind == MeasureKind.Diameter);

            case (MeasureKind.MajorAxis or MeasureKind.MinorAxis, EllipseShape ellipse):
            {
                var major = kind == MeasureKind.MajorAxis;
                return Across(centre, major ? ellipse.MajorAxis : ellipse.MinorAxis, ellipse.Angle + (major ? 0 : 90));
            }

            case (MeasureKind.MajorAxis or MeasureKind.MinorAxis, CircleShape circle):
                return Across(centre, 2 * circle.Radius, kind == MeasureKind.MajorAxis ? 0 : 90);

            case (MeasureKind.MajorAxis or MeasureKind.MinorAxis, OrientedRectShape rect):
            {
                var wide = rect.Width >= rect.Height;
                var major = kind == MeasureKind.MajorAxis;
                var along = major == wide;

                return Across(
                    centre,
                    major ? Math.Max(rect.Width, rect.Height) : Math.Min(rect.Width, rect.Height),
                    rect.AngleDegrees + (along ? 0 : 90));
            }

            case (MeasureKind.MajorAxis, _) when shape.Metric(ShapeMetric.Length) is { } length:
                return Across(centre, length, DegreesOf(shape) ?? 0);

            case (MeasureKind.MinorAxis, _) when shape.Metric(ShapeMetric.Width) is { } width:
                return Across(centre, width, (DegreesOf(shape) ?? 0) + 90);

            case (MeasureKind.Orientation, _) when DegreesOf(shape) is { } degrees:
            {
                var size = Math.Max(20, Math.Max(shape.Bounds.Width, shape.Bounds.Height));
                var line = Across(centre, size, degrees);
                return line with { Pixels = degrees, Quantity = Quantity.Degrees };
            }

            case (MeasureKind.Orientation, _):
                context.Warn("This shape runs in no direction - a circle, a point. Orientation needs a line, " +
                             "an ellipse or a turned rectangle.");
                return null;

            case (MeasureKind.Area or MeasureKind.Perimeter, _) when Enclosed(shape, kind) is { } enclosed:
                return new OneReading(enclosed, kind == MeasureKind.Area ? Quantity.Area : Quantity.Length,
                    centre, centre, DrawOutline: true);

            case (MeasureKind.Length, LineShape line):
                return new OneReading(line.Length, Quantity.Length, line.Start, line.End);

            case (MeasureKind.Length, PolygonShape { Closed: false } path) when path.Points.Count >= 2:
                return new OneReading(PathLength(path.Points, closed: false), Quantity.Length,
                    centre, centre, DrawOutline: true);

            case (MeasureKind.Length, _) when shape.Metric(ShapeMetric.Length) is { } length:
                return Across(centre, length, DegreesOf(shape) ?? 0);
        }

        context.Warn($"A {Describe(shape)} has no {TypeNames.Words(kind.ToString()).ToLowerInvariant()} to read.");
        return null;
    }

    /// <summary>A radius drawn from the centre up and to the right, or a diameter through it.</summary>
    private static OneReading Radial(Point2 centre, double radius, bool diameter)
    {
        // Up and to the right: clear of a horizontal edge or a vertical
        // datum, which is where most other annotations run.
        var ux = Math.Cos(-Math.PI / 4);
        var uy = Math.Sin(-Math.PI / 4);
        var edge = new Point2(centre.X + (ux * radius), centre.Y + (uy * radius));

        return diameter
            ? new OneReading(2 * radius, Quantity.Length, new Point2(centre.X - (ux * radius), centre.Y - (uy * radius)), edge)
            : new OneReading(radius, Quantity.Length, centre, edge);
    }

    /// <summary>A length drawn across the centre at an angle, half each side.</summary>
    private static OneReading Across(Point2 centre, double length, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var half = length / 2;
        var dx = Math.Cos(radians) * half;
        var dy = Math.Sin(radians) * half;

        return new OneReading(
            length,
            Quantity.Length,
            new Point2(centre.X - dx, centre.Y - dy),
            new Point2(centre.X + dx, centre.Y + dy));
    }

    /// <summary>The way a shape runs, 0 to 180 degrees clockwise from +X, or null when it runs no way.</summary>
    private static double? DegreesOf(Shape shape)
    {
        if (ShapeGeometry.Direction(shape) is not { } direction)
            return null;

        var degrees = Math.Atan2(direction.Y, direction.X) * 180 / Math.PI;
        degrees %= 180;
        return degrees < 0 ? degrees + 180 : degrees;
    }

    /// <summary>A shape's area or perimeter, from its geometry, or its producer's measurement.</summary>
    private static double? Enclosed(Shape shape, MeasureKind kind)
    {
        var area = kind == MeasureKind.Area;

        switch (shape)
        {
            case CircleShape circle:
                return area ? Math.PI * circle.Radius * circle.Radius : 2 * Math.PI * circle.Radius;

            case EllipseShape ellipse:
            {
                var a = ellipse.MajorAxis / 2;
                var b = ellipse.MinorAxis / 2;

                // Ramanujan's approximation, within a few parts per million
                // of the exact elliptic integral for any ellipse a fit gives.
                return area
                    ? Math.PI * a * b
                    : Math.PI * ((3 * (a + b)) - Math.Sqrt(((3 * a) + b) * (a + (3 * b))));
            }

            case OrientedRectShape rect:
                return area ? rect.Width * rect.Height : 2 * (rect.Width + rect.Height);

            case RectShape rect:
                return area ? rect.Rect.Width * rect.Rect.Height : 2 * (rect.Rect.Width + rect.Rect.Height);
        }

        // A blob's own measurements before its outline: its area is the
        // pixels it covers, and the polygon traced round it is an
        // approximation of that.
        if (shape.Metric(area ? ShapeMetric.Area : ShapeMetric.Perimeter) is { } measured)
            return measured;

        if (shape is PolygonShape { Closed: true } polygon && polygon.Points.Count >= 3)
            return area ? Math.Abs(SignedArea(polygon.Points)) : PathLength(polygon.Points, closed: true);

        return null;
    }

    private static double PathLength(IReadOnlyList<Point2> points, bool closed)
    {
        var length = 0.0;

        for (var i = 1; i < points.Count; i++)
            length += points[i - 1].DistanceTo(points[i]);

        return closed && points.Count > 2 ? length + points[^1].DistanceTo(points[0]) : length;
    }

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

    private static string Describe(Shape shape) => shape switch
    {
        CircleShape => "circle",
        EllipseShape => "ellipse",
        LineShape => "line",
        PointShape => "point",
        PolygonShape => "outline",
        _ => "rectangle",
    };

    /// <summary>The shape itself, drawn in the reading's colour and labelled with it.</summary>
    private ShapeSet Outlined(Shape shape, double value, (int W, int H) frame, string? units, Quantity quantity)
    {
        var text = string.Format(
            CultureInfo.InvariantCulture,
            "{0:F" + _decimals + "}{1}",
            value,
            units ?? (string.IsNullOrEmpty(_units) ? string.Empty : " " + _units));

        var centre = ShapeGeometry.Centre(shape);

        var drawn = shape with
        {
            Colour = Reading,
            StrokeWidth = 2,
            Label = _showLabel ? text : null,
            Metrics = new Dictionary<string, double>
            {
                [quantity == Quantity.Area ? ShapeMetric.Area : ShapeMetric.Length] = value,
                [ShapeMetric.CentroidX] = centre.X,
                [ShapeMetric.CentroidY] = centre.Y,
            },
        };

        return new ShapeSet([drawn], frame.W, frame.H);
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
