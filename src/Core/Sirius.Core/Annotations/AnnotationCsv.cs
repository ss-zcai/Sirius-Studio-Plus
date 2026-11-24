using System.Globalization;
using System.Text;
using Sirius.Core.Shapes;

namespace Sirius.Core.Annotations;

/// <summary>A picture to export: its file, as it should be named in the CSV, and its annotations if it has any.</summary>
/// <param name="File">The picture's name, with its folder inside the one open: "20261121\0042_FAIL.png".</param>
/// <param name="Annotations">Its annotation file; null when it has none, or it could not be read.</param>
/// <param name="Verdict">The verdict its name says, for a picture without annotations.</param>
public sealed record AnnotationPicture(string File, AnnotationFile? Annotations, string? Verdict = null);

/// <summary>
/// Pictures' annotations as one CSV (F20b), one row per picture - its
/// verdict and results, a data log - or one per shape, with its
/// measurements, as the old ICB's Export Features.
/// </summary>
/// <remarks>
/// <para>
/// Columns are taken across every picture, in the order first met: a
/// recipe changed part way through a folder adds its new results as columns
/// rather than losing them, and a picture without one leaves it empty.
/// </para>
/// <para>
/// Numbers are written with a full stop and to six places, whatever the
/// machine's language - a CSV with decimal commas is read as text by half
/// the programs that open it. Times as "2026-11-21 14:23:05.123", which a
/// spreadsheet reads as a time.
/// </para>
/// </remarks>
public static class AnnotationCsv
{
    /// <summary>What a CSV file is saved as: UTF-8 with its mark, so Excel reads "×" and "µ" as written.</summary>
    public static Encoding Encoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private static readonly string[] PictureColumns = ["File", "Station", "Recipe", "Inspection", "Time", "Verdict", "Grade", "Node Errors"];

    private static readonly string[] ShapeLead = ["File", "Station", "Inspection", "Verdict"];

    /// <summary>A row for each picture: what is known about it, the part, then each result.</summary>
    /// <returns>The rows written, not counting the heading.</returns>
    public static int WritePictures(TextWriter writer, IReadOnlyList<AnnotationPicture> pictures)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(pictures);

        var parts = Names(pictures.Select(picture => picture.Annotations?.Part));
        var results = Names(pictures.Select(picture => picture.Annotations?.Results));

        WriteRow(writer, PictureColumns.Concat(parts.Select(name => $"Part: {name}")).Concat(results));

        foreach (var picture in pictures)
        {
            var file = picture.Annotations;
            var row = new List<string?>
            {
                picture.File,
                file?.Station,
                file?.Recipe,
                file is { Serial: > 0 } ? file.Serial.ToString(CultureInfo.InvariantCulture) : null,
                Time(file?.Time),
                file?.VerdictWord ?? picture.Verdict,
                file?.Grade,
                file is null ? null : file.NodeErrors.ToString(CultureInfo.InvariantCulture),
            };

            row.AddRange(parts.Select(name => Value(Find(file?.Part, name))));
            row.AddRange(results.Select(name => Value(Find(file?.Results, name))));
            WriteRow(writer, row);
        }

        return pictures.Count;
    }

    /// <summary>
    /// A row for each shape on each picture: the picture, the layer, the
    /// shape's number on its picture, kind, label, place and size, then
    /// every measurement any shape carries. A picture without shapes has no row.
    /// </summary>
    /// <returns>The rows written, not counting the heading.</returns>
    public static int WriteShapes(TextWriter writer, IReadOnlyList<AnnotationPicture> pictures)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(pictures);

        var keys = ShapeTable.MetricKeys(pictures.SelectMany(picture => picture.Annotations?.Layers.SelectMany(layer => layer.Shapes) ?? []));

        // "#" says little in a sheet of many pictures: the shape's number on its own.
        var lead = AnnotationTable.ShapeColumns.Select(column => column == "#" ? "Shape" : column);
        WriteRow(writer, ShapeLead.Concat(lead).Concat(keys.Select(ShapeTable.Heading)));

        var rows = 0;

        foreach (var picture in pictures)
        {
            if (picture.Annotations is not { } file)
                continue;

            var number = 0;

            foreach (var layer in file.Layers)
            {
                foreach (var shape in layer.Shapes)
                {
                    var (x, y, w, h) = AnnotationTable.Geometry(shape);
                    var row = new List<string?>
                    {
                        picture.File,
                        file.Station,
                        file.Serial > 0 ? file.Serial.ToString(CultureInfo.InvariantCulture) : null,
                        file.VerdictWord,
                        (++number).ToString(CultureInfo.InvariantCulture),
                        layer.Name,
                        AnnotationTable.KindOf(shape),
                        shape.Label,
                        Number(x),
                        Number(y),
                        Number(w),
                        Number(h),
                    };

                    row.AddRange(keys.Select(key => Number(shape.Metric(key))));
                    WriteRow(writer, row);
                    rows++;
                }
            }
        }

        return rows;
    }

    /// <summary>A field as CSV has it: in quotes, its own quotes doubled, when it holds a comma, quote or line break.</summary>
    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static void WriteRow(TextWriter writer, IEnumerable<string?> fields)
    {
        writer.Write(string.Join(",", fields.Select(Field)));
        writer.Write("\r\n");
    }

    /// <summary>The names met across the pictures, in the order first met, alike ignoring case.</summary>
    private static List<string> Names(IEnumerable<IReadOnlyList<KeyValuePair<string, object?>>?> lists)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();

        foreach (var list in lists)
        {
            foreach (var (name, _) in list ?? [])
            {
                if (seen.Add(name))
                    names.Add(name);
            }
        }

        return names;
    }

    private static object? Find(IReadOnlyList<KeyValuePair<string, object?>>? list, string name)
    {
        foreach (var (key, value) in list ?? [])
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    private static string? Value(object? value) => value switch
    {
        null => null,
        double number => Number(number),
        float number => Number(number),
        bool flag => flag ? "TRUE" : "FALSE",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static string? Number(double? value) => value switch
    {
        null => null,
        { } number when double.IsNaN(number) || double.IsInfinity(number) => null,
        { } number => number.ToString("0.######", CultureInfo.InvariantCulture),
    };

    private static string? Time(DateTime? time) =>
        time?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
