using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>How a Calibrate node is told what a pixel is worth.</summary>
public enum CalibrationSource
{
    /// <summary>
    /// From something of known size in the picture: measure it on the
    /// image, type what it really is.
    /// </summary>
    KnownLength,

    /// <summary>
    /// From the number itself, when it is already known - off a lens
    /// data sheet, or from a previous calibration.
    /// </summary>
    Scale,
}

/// <summary>
/// Says what a pixel is worth, so readings come out in real units.
/// </summary>
/// <remarks>
/// <para>
/// Wire its output into a Measure node and the distance it reports is in
/// millimetres rather than pixels, and the limits on the check after it
/// are in millimetres too - which is how they arrive from a drawing.
/// </para>
/// <para>
/// The usual way round is Known Length: put a gauge block, a printed
/// scale or a feature of known size in the field of view, draw a Find
/// Line or a Measure across it, wire that in, and type what it really
/// is. The scale follows. Nothing has to be typed in pixels, which is
/// the one number nobody can read off a drawing.
/// </para>
/// <para>
/// A node rather than a setting on the recipe, because a scale is not a
/// property of a recipe: two cameras have two, a Resize changes one, and
/// a global number could only ever be right about the simplest case.
/// </para>
/// </remarks>
[VisionNode(
    "Calibrate",
    TypeKey = "calibration.scale",
    Category = NodeCategory.Calibration,
    Icon = "ph_calibrate",
    Description = "Says what a pixel is worth, so readings come out in real units.")]
public sealed class CalibrateNode : VisionNode
{
    private CalibrationSource _source = CalibrationSource.KnownLength;
    private double _pixels = 100;
    private double _length = 10;
    private double _unitsPerPixel = 0.1;
    private string _unit = "mm";

    public CalibrateNode()
    {
        ReferenceIn = AddInput<ShapeSet>("Reference", isRequired: false);

        CalibrationOut = AddOutput<Calibration>("Calibration");
        ScaleOut = AddOutput<double>("Units/Pixel");
    }

    /// <summary>
    /// Something of known size, measured on the image.
    /// </summary>
    /// <remarks>
    /// Takes the length of the first shape: a found line, or the
    /// annotation a Measure node drew. Wiring one in is what saves
    /// anybody counting pixels by hand.
    /// </remarks>
    public InputPort ReferenceIn { get; }

    /// <summary>The conversion, for a Measure node downstream.</summary>
    public OutputPort CalibrationOut { get; }

    /// <summary>
    /// The scale on its own, for a label or a check on the calibration
    /// itself - a lens that has moved shows up as a scale that has.
    /// </summary>
    public OutputPort ScaleOut { get; }

    [Param("From", Description = "Measure something known, or type the scale.")]
    public CalibrationSource Source
    {
        get => _source;
        set => SetParam(ref _source, value);
    }

    [Param("Pixels", Group = "Known Length", Connectable = true, Min = 0,
        Description = "How long the reference is on the image. Wire a reference in instead.")]
    public double Pixels
    {
        get => _pixels;
        set => SetParam(ref _pixels, Math.Max(0, value));
    }

    [Param("Real Length", Group = "Known Length", Connectable = true, Min = 0,
        Description = "How long it really is, in the unit below.")]
    public double Length
    {
        get => _length;
        set => SetParam(ref _length, Math.Max(0, value));
    }

    [Param("Units per Pixel", Group = "Scale", Connectable = true,
        Description = "Used when From is Scale. One pixel covers this much.")]
    public double UnitsPerPixel
    {
        get => _unitsPerPixel;
        set => SetParam(ref _unitsPerPixel, value);
    }

    [Param("Unit", Description = "What readings are reported in: mm, in, um.")]
    public string Unit
    {
        get => _unit;
        set => SetParam(ref _unit, value ?? string.Empty);
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var calibration = _source == CalibrationSource.Scale
            ? new Calibration(context.Param(nameof(UnitsPerPixel), _unitsPerPixel), _unit)
            : FromLength(context);

        context.Set(CalibrationOut, calibration);
        context.Set(ScaleOut, calibration.UnitsPerPixel);
    }

    private Calibration FromLength(EvalContext context)
    {
        var pixels = PixelsOf(context);
        var length = context.Param(nameof(Length), _length);

        if (pixels <= 0 || length <= 0)
        {
            context.Warn(
                "A calibration needs a reference with a length on the image and a " +
                "length in real units; reporting pixels until it has both.");

            return Calibration.None;
        }

        return Calibration.FromKnownLength(pixels, length, _unit);
    }

    /// <summary>
    /// How long the reference is, in pixels: measured off the wire when
    /// something is attached, typed otherwise.
    /// </summary>
    private double PixelsOf(EvalContext context)
    {
        if (context.Get<ShapeSet>(ReferenceIn) is not { Count: > 0 } reference)
            return context.Param(nameof(Pixels), _pixels);

        var shape = reference.Shapes[0];

        if (reference.Count > 1)
        {
            context.Warn(
                $"The reference carries {reference.Count} shapes; measuring the first. " +
                "Use Select Shape to choose which.");
        }

        return LengthOf(shape);
    }

    /// <summary>
    /// A shape's length in pixels, by whichever reading it offers.
    /// </summary>
    /// <remarks>
    /// A producer's own length first, because a Measure node publishes
    /// exactly that and it is the number that was wanted. Then a line's
    /// own ends. A circle falls back to its diameter, which is the one
    /// length a circle has and is what a calibration target of drilled
    /// holes would be measured by.
    /// </remarks>
    internal static double LengthOf(Shape shape) => shape switch
    {
        _ when shape.Metrics.TryGetValue(ShapeMetric.Length, out var length) && length > 0 => length,
        LineShape line => line.Start.DistanceTo(line.End),
        CircleShape circle => circle.Radius * 2,
        EllipseShape ellipse => ellipse.MajorAxis,
        OrientedRectShape rect => Math.Max(rect.Width, rect.Height),
        _ => Math.Max(shape.Bounds.Width, shape.Bounds.Height),
    };
}
