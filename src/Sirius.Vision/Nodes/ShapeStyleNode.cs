using Emgu.CV;
using Emgu.CV.Util;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>What a styled shape is drawn as.</summary>
public enum ShapeForm
{
    /// <summary>The shape as found - a blob's own outline.</summary>
    Outline,

    /// <summary>The axis-aligned box around it.</summary>
    BoundingBox,

    /// <summary>The smallest rectangle around it, at whatever angle that takes.</summary>
    MinimumRect,

    /// <summary>A cross at its centre.</summary>
    Centre,
}

/// <summary>What is written beside each shape.</summary>
public enum ShapeLabelling
{
    None,

    /// <summary>Its position in the set, matching the table's row number.</summary>
    Index,

    /// <summary>The value of the chosen measure.</summary>
    Measure,
}

/// <summary>
/// Restyles a set of shapes: colour, weight, what they are drawn as, and what
/// is written beside them.
/// </summary>
/// <remarks>
/// <para>
/// Styling is a graph operation rather than a viewer setting because it is
/// part of the result. Two finders can draw in two colours on one image, and
/// a pass/fail branch can colour the same features differently, without the
/// viewer knowing anything about either.
/// </para>
/// <para>
/// Reducing a blob to its bounding or minimum-area box is more than
/// decoration: a thousand outlines of a thousand points each is a lot to
/// look at, and four corners often says what is needed.
/// </para>
/// </remarks>
[VisionNode(
    "Shape Style",
    TypeKey = "shape.style",
    Category = NodeCategory.Blob,
    Icon = "ph_shape_style",
    Description = "Sets the colour, weight, form and labels of a set of shapes.")]
public sealed class ShapeStyleNode : VisionNode
{
    private ShapeForm _form = ShapeForm.Outline;
    private ShapeLabelling _labelling = ShapeLabelling.None;
    private ShapeMeasure _measure = ShapeMeasure.Area;
    private string _colour = "#A78BFA";
    private double _strokeWidth = 1.4;

    public ShapeStyleNode()
    {
        ShapesIn = AddInput<ShapeSet>("In");
        ShapesOut = AddOutput<ShapeSet>("Out");
    }

    public InputPort ShapesIn { get; }

    public OutputPort ShapesOut { get; }

    [Param("Form", Description = "What each shape is drawn as.")]
    public ShapeForm Form
    {
        get => _form;
        set => SetParam(ref _form, value);
    }

    [Param("Colour",
        Editor = ParamEditor.Colour,
        Description = "Pick a standard colour, or type one as #RRGGBB or #AARRGGBB.")]
    public string Colour
    {
        get => _colour;
        set => SetParam(ref _colour, value);
    }

    [Param("Stroke Width",
        Min = 0.2,
        Max = 12,
        Step = 0.2,
        Description = "Line weight in image pixels; the viewer still scales it by the zoom.")]
    public double StrokeWidth
    {
        get => _strokeWidth;
        set => SetParam(ref _strokeWidth, Math.Clamp(value, 0.2, 12));
    }

    [Param("Labels", Description = "What to write beside each shape.")]
    public ShapeLabelling Labelling
    {
        get => _labelling;
        set => SetParam(ref _labelling, value);
    }

    [Param("Label Measure", Description = "Which measurement to write, when labelling by measure.")]
    public ShapeMeasure LabelMeasure
    {
        get => _measure;
        set => SetParam(ref _measure, value);
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var set = context.Get<ShapeSet>(ShapesIn)!;
        var colour = ParseColour(_colour, context);

        var styled = new List<Shape>(set.Count);

        for (var i = 0; i < set.Count; i++)
        {
            var source = set.Shapes[i];

            styled.Add(Reshape(source) with
            {
                Metrics = source.Metrics,
                Colour = colour,
                StrokeWidth = _strokeWidth,
                Label = LabelFor(source, i),
            });
        }

        context.Set(ShapesOut, new ShapeSet(styled, set.FrameWidth, set.FrameHeight));
    }

    private Shape Reshape(Shape shape) => _form switch
    {
        ShapeForm.BoundingBox => new RectShape(shape.Bounds),
        ShapeForm.Centre => new PointShape(shape.Bounds.Centre),
        ShapeForm.MinimumRect => MinimumRect(shape),
        _ => shape,
    };

    /// <summary>
    /// The smallest rectangle around the shape, at whatever angle that takes.
    /// </summary>
    /// <remarks>
    /// Computed here rather than read from the shape's measurements, because
    /// the producer only takes those when a filter asks for them - a blob
    /// finder with no aspect filter has no rectangle to report.
    /// </remarks>
    private static Shape MinimumRect(Shape shape)
    {
        if (shape is not PolygonShape { Points.Count: >= 3 } polygon)
            return new RectShape(shape.Bounds);

        using var points = new VectorOfPointF(
            polygon.Points
                .Select(point => new System.Drawing.PointF((float)point.X, (float)point.Y))
                .ToArray());

        var rect = CvInvoke.MinAreaRect(points);

        double width = rect.Size.Width;
        double height = rect.Size.Height;
        double angle = rect.Angle;

        // OpenCV may label either side as the width, describing the same
        // rectangle two ways 90 degrees apart. Left alone, a region cut from
        // this would come out landscape or portrait depending on which
        // labelling happened to fall out - so the long side is always the
        // width here, and the angle follows it.
        if (height > width)
        {
            (width, height) = (height, width);
            angle += 90;
        }

        while (angle > 180)
            angle -= 360;

        while (angle <= -180)
            angle += 360;

        return new OrientedRectShape(
            new Point2(rect.Center.X, rect.Center.Y),
            width,
            height,
            angle);
    }

    private string? LabelFor(Shape shape, int index) => _labelling switch
    {
        ShapeLabelling.Index => (index + 1).ToString(System.Globalization.CultureInfo.CurrentCulture),

        ShapeLabelling.Measure when ShapeMeasures.MetricFor(_measure) is { } metric =>
            shape.Metric(metric)?.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture),

        _ => null,
    };

    /// <summary>
    /// Reads #RRGGBB or #AARRGGBB into the packed form a shape carries.
    /// </summary>
    /// <remarks>
    /// A bad colour warns and falls back rather than failing the node: it is
    /// a presentation detail, and losing the whole result over a typo in a
    /// hex string would be a poor trade.
    /// </remarks>
    private static uint ParseColour(string text, EvalContext context)
    {
        const uint fallback = 0xFFA78BFAu;

        var trimmed = (text ?? string.Empty).Trim().TrimStart('#');

        if (trimmed.Length == 6 &&
            uint.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return 0xFF000000u | rgb;
        }

        if (trimmed.Length == 8 &&
            uint.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out var argb))
        {
            return argb;
        }

        context.Warn($"'{text}' is not a colour like #RRGGBB; using the default.");
        return fallback;
    }
}
