using System.Globalization;
using Sirius.Core.Shapes;

namespace Sirius.Core.Annotations;

/// <summary>A line of what is known about a picture: "Station" "Top", "Diameter" "12.48".</summary>
public sealed record AnnotationFact(string Name, string Value);

/// <summary>
/// An annotation file read off as tables (F20b): what is known about the
/// picture - station, verdict, the part, every result - and its shapes, a
/// row each.
/// </summary>
/// <remarks>
/// Kept out of the window, as <see cref="ShapeTable"/> is, so what is shown
/// - and the CSV written from the same words - is tested without one.
/// </remarks>
public static class AnnotationTable
{
    /// <summary>The columns a shape's row starts with, before its measurements.</summary>
    public static IReadOnlyList<string> ShapeColumns { get; } = ["#", "Layer", "Kind", "Label", "X", "Y", "W", "H"];

    /// <summary>
    /// What the file says about the picture, in reading order: station,
    /// recipe, inspection, time, verdict, size; then what was known about the
    /// part, then the results, by published name.
    /// </summary>
    public static IReadOnlyList<AnnotationFact> Facts(AnnotationFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var facts = new List<AnnotationFact>();

        void Add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                facts.Add(new AnnotationFact(name, value));
        }

        Add("Station", file.Station);
        Add("Recipe", file.Recipe);
        Add("Inspection", file.Serial > 0 ? file.Serial.ToString(CultureInfo.CurrentCulture) : null);
        Add("Time", file.Time?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.CurrentCulture));
        Add("Verdict", file.Grade is null ? file.VerdictWord : $"{file.VerdictWord} ({file.Grade})");
        Add("Nodes gone wrong", file.NodeErrors > 0 ? file.NodeErrors.ToString(CultureInfo.CurrentCulture) : null);
        Add("Size", file.Width > 0 && file.Height > 0
            ? $"{file.Width} × {file.Height}" + (file.Bits > 0 ? $", {file.Bits}-bit" : string.Empty)
            : null);

        foreach (var (name, value) in file.Part)
            facts.Add(new AnnotationFact($"Part · {name}", Display(value)));

        foreach (var (name, value) in file.Results)
            facts.Add(new AnnotationFact(name, Display(value)));

        return facts;
    }

    /// <summary>
    /// The layers' shapes, a row each in the order given - the order a viewer
    /// drawing the same layers numbers them - with the layer, kind, label,
    /// place and size, then every measurement any of them carries.
    /// </summary>
    public static ShapeTable Shapes(IEnumerable<AnnotationLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        var shapes = layers.SelectMany(layer => layer.Shapes.Select(shape => (layer.Name, Shape: shape))).ToArray();

        if (shapes.Length == 0)
            return ShapeTable.Of(ShapeColumns, []);

        var keys = ShapeTable.MetricKeys(shapes.Select(entry => entry.Shape));
        var columns = ShapeColumns.Concat(keys.Select(ShapeTable.Heading)).ToArray();
        var rows = new List<string[]>(shapes.Length);

        for (var i = 0; i < shapes.Length; i++)
        {
            var (layer, shape) = shapes[i];
            var (x, y, w, h) = Geometry(shape);
            var row = new string[columns.Length];

            row[0] = (i + 1).ToString(CultureInfo.CurrentCulture);
            row[1] = layer;
            row[2] = KindOf(shape);
            row[3] = shape.Label ?? string.Empty;
            row[4] = Place(x);
            row[5] = Place(y);
            row[6] = Place(w);
            row[7] = Place(h);

            for (var k = 0; k < keys.Count; k++)
                row[ShapeColumns.Count + k] = ShapeTable.FormatMetric(keys[k], shape.Metric(keys[k]));

            rows.Add(row);
        }

        return ShapeTable.Of(columns, rows);
    }

    /// <summary>What a shape is, in a word: "Circle", "Box" - a turned rectangle.</summary>
    public static string KindOf(Shape shape) => shape switch
    {
        PointShape => "Point",
        LineShape => "Line",
        RectShape => "Rectangle",
        OrientedRectShape => "Box",
        CircleShape => "Circle",
        EllipseShape => "Ellipse",
        PolygonShape => "Polygon",
        _ => shape.GetType().Name.Replace("Shape", string.Empty, StringComparison.Ordinal),
    };

    /// <summary>
    /// Where a shape is and how big, in the picture's pixels: its centre,
    /// and its own width and height - a turned box's sides, an ellipse's
    /// axes, a circle's diameter - else the box it fits in. A point has no
    /// size.
    /// </summary>
    public static (double X, double Y, double? Width, double? Height) Geometry(Shape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        switch (shape)
        {
            case PointShape point:
                return (point.At.X, point.At.Y, null, null);
            case OrientedRectShape box:
                return (box.Centre.X, box.Centre.Y, box.Width, box.Height);
            case EllipseShape ellipse:
                return (ellipse.Centre.X, ellipse.Centre.Y, ellipse.MajorAxis, ellipse.MinorAxis);
            case CircleShape circle:
                return (circle.Centre.X, circle.Centre.Y, circle.Radius * 2, circle.Radius * 2);
            default:
                var bounds = shape.Bounds;
                return (bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2), bounds.Width, bounds.Height);
        }
    }

    /// <summary>A result as it is read: numbers to three places, yes or no, a dash for nothing.</summary>
    public static string Display(object? value) => value switch
    {
        double number => number.ToString("0.###", CultureInfo.CurrentCulture),
        float number => number.ToString("0.###", CultureInfo.CurrentCulture),
        bool flag => flag ? "Yes" : "No",
        null => "—",
        _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty,
    };

    /// <summary>A place or size, to a tenth of a pixel.</summary>
    private static string Place(double? value) =>
        value is { } number ? number.ToString("0.#", CultureInfo.CurrentCulture) : string.Empty;
}
