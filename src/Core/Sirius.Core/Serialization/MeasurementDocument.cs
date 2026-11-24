using System.Globalization;
using System.Text.Json.Serialization;
using Sirius.Core.Graph;
using Sirius.Core.Shapes;

namespace Sirius.Core.Serialization;

/// <summary>
/// One hand-drawn measurement as it appears in a saved recipe.
/// </summary>
/// <remarks>
/// Points are written as a flat list of numbers rather than as objects -
/// a measurement is two to four points, and <c>[12, 40, 300, 180]</c> is
/// both smaller and easier to read in a diff than four <c>{x, y}</c>
/// records.
/// </remarks>
public sealed class MeasurementDocument
{
    /// <summary>Which tool drew it, as its name.</summary>
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = string.Empty;

    /// <summary>Points in image coordinates, as x, y, x, y.</summary>
    [JsonPropertyName("points")]
    public List<double> Points { get; set; } = [];

    /// <summary>Outline colour, as #RRGGBB.</summary>
    [JsonPropertyName("colour")]
    public string Colour { get; set; } = "#F59E0B";

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// Which of the node's pictures it was drawn on - a port name for a
    /// preview, absent for a Viewer node's own panel.
    /// </summary>
    [JsonPropertyName("view")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? View { get; set; }

    public static MeasurementDocument From(MeasurementRecord record)
    {
        var flat = new List<double>(record.Points.Count * 2);

        foreach (var point in record.Points)
        {
            flat.Add(Math.Round(point.X, 3));
            flat.Add(Math.Round(point.Y, 3));
        }

        return new MeasurementDocument
        {
            Tool = record.Tool,
            Points = flat,
            Colour = record.Colour,
            Name = record.Name,
            View = string.IsNullOrEmpty(record.View) ? null : record.View,
        };
    }

    public MeasurementRecord ToRecord()
    {
        var points = new List<Point2>(Points.Count / 2);

        // An odd trailing number is a half-written point; dropping it is
        // better than reading a coordinate that was never saved.
        for (var i = 0; i + 1 < Points.Count; i += 2)
            points.Add(new Point2(Points[i], Points[i + 1]));

        return new MeasurementRecord(
            Tool,
            points,
            string.IsNullOrWhiteSpace(Colour) ? "#F59E0B" : Colour,
            string.IsNullOrWhiteSpace(Name) ? null : Name,
            View);
    }

    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture, "{0} ({1} points)", Tool, Points.Count / 2);
}
