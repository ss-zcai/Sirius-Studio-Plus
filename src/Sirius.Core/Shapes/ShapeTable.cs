using System.Globalization;

namespace Sirius.Core.Shapes;

/// <summary>
/// A shape set arranged as rows and columns, for reading off.
/// </summary>
/// <remarks>
/// Kept out of the UI because it is entirely about the data: which
/// measurements a set actually carries, in what order to show them, and how
/// to render a number as text. Being a pure function of a
/// <see cref="ShapeSet"/>, it can be tested without a window.
/// </remarks>
public sealed class ShapeTable
{
    /// <summary>
    /// The order measurements are shown in, when a set carries them.
    /// </summary>
    /// <remarks>
    /// Fixed rather than alphabetical: area before perimeter before the ratios
    /// derived from them reads the way the measurement was taken. Anything not
    /// listed - a metric from a node this library has never heard of - follows
    /// in alphabetical order.
    /// </remarks>
    private static readonly string[] PreferredOrder =
    [
        ShapeMetric.Area,
        ShapeMetric.Perimeter,
        ShapeMetric.CentroidX,
        ShapeMetric.CentroidY,
        ShapeMetric.BoundsX,
        ShapeMetric.BoundsY,
        ShapeMetric.BoundsWidth,
        ShapeMetric.BoundsHeight,
        ShapeMetric.Width,
        ShapeMetric.Length,
        ShapeMetric.Angle,
        ShapeMetric.Radius,
        ShapeMetric.Deviation,
        ShapeMetric.Along,
        ShapeMetric.Offset,
        ShapeMetric.Contrast,
        ShapeMetric.AspectRatio,
        ShapeMetric.Circularity,
        ShapeMetric.Solidity,
        ShapeMetric.Extent,
        ShapeMetric.HoleCount,
        ShapeMetric.TouchesBorder,
    ];

    private static readonly Dictionary<string, string> Headings = new()
    {
        [ShapeMetric.Area] = "Area",
        [ShapeMetric.Perimeter] = "Perimeter",
        [ShapeMetric.CentroidX] = "Centre X",
        [ShapeMetric.CentroidY] = "Centre Y",
        [ShapeMetric.BoundsX] = "Box X",
        [ShapeMetric.BoundsY] = "Box Y",
        [ShapeMetric.BoundsWidth] = "Box W",
        [ShapeMetric.BoundsHeight] = "Box H",
        [ShapeMetric.Width] = "Width",
        [ShapeMetric.Length] = "Length",
        [ShapeMetric.Angle] = "Angle",
        [ShapeMetric.Radius] = "Radius",
        [ShapeMetric.Deviation] = "Deviation",
        [ShapeMetric.Along] = "Along",
        [ShapeMetric.Offset] = "Offset",
        [ShapeMetric.Contrast] = "Contrast",
        [ShapeMetric.AspectRatio] = "Aspect",
        [ShapeMetric.Circularity] = "Circularity",
        [ShapeMetric.Solidity] = "Solidity",
        [ShapeMetric.Extent] = "Extent",
        [ShapeMetric.HoleCount] = "Holes",
        [ShapeMetric.TouchesBorder] = "At edge",
    };

    private ShapeTable(IReadOnlyList<string> columns, IReadOnlyList<string[]> rows)
    {
        Columns = columns;
        Rows = rows;
    }

    /// <summary>Column headings, starting with the row number.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>One row per shape, each as many cells as there are columns.</summary>
    public IReadOnlyList<string[]> Rows { get; }

    public static ShapeTable Empty { get; } = new(["#"], []);

    public static ShapeTable From(ShapeSet? set)
    {
        if (set is not { Count: > 0 })
            return Empty;

        var keys = OrderedKeys(set);

        // A point has a position, not a centre. "Centre X" is right for a
        // blob, which is somewhere around it; for a point it reads as though
        // there were something else the point was the centre of.
        var points = set.Shapes.All(shape => shape is PointShape);

        var columns = new List<string> { "#" };
        columns.AddRange(keys.Select(key => key switch
        {
            ShapeMetric.CentroidX when points => "X",
            ShapeMetric.CentroidY when points => "Y",
            _ => Headings.GetValueOrDefault(key, key),
        }));

        var rows = new List<string[]>(set.Count);

        for (var i = 0; i < set.Count; i++)
        {
            var shape = set.Shapes[i];
            var row = new string[columns.Count];

            row[0] = (i + 1).ToString(CultureInfo.CurrentCulture);

            for (var c = 0; c < keys.Count; c++)
                row[c + 1] = Format(shape.Metric(keys[c]));

            rows.Add(row);
        }

        return new ShapeTable(columns, rows);
    }

    /// <summary>
    /// Every measurement any shape in the set carries, in reading order.
    /// </summary>
    /// <remarks>
    /// Taken across the whole set rather than from the first shape: a producer
    /// may measure one shape and not another, and a column missing because of
    /// the row that happened to be first would be a confusing way to lose
    /// data.
    /// </remarks>
    private static List<string> OrderedKeys(ShapeSet set)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var shape in set.Shapes)
        {
            foreach (var key in shape.Metrics.Keys)
                present.Add(key);
        }

        var ordered = PreferredOrder.Where(present.Contains).ToList();

        ordered.AddRange(
            present.Except(PreferredOrder, StringComparer.Ordinal)
                   .OrderBy(key => key, StringComparer.Ordinal));

        return ordered;
    }

    /// <summary>
    /// Renders a measurement.
    /// </summary>
    /// <remarks>
    /// A whole number prints without a decimal part, because a hole count of
    /// "2.00" reads as a measurement when it is a count.
    /// </remarks>
    private static string Format(double? value)
    {
        if (value is not { } number)
            return string.Empty;

        return Math.Abs(number - Math.Round(number)) < 1e-9
            ? number.ToString("0", CultureInfo.CurrentCulture)
            : number.ToString("0.##", CultureInfo.CurrentCulture);
    }
}
