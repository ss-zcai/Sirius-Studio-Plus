using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Core.Annotations;

/// <summary>A named set of shapes drawn on a picture: what one viewer in the recipe drew.</summary>
/// <param name="Name">The viewer's name in the recipe - "Main View".</param>
/// <param name="Shapes">The shapes, in the picture's pixels.</param>
public sealed record AnnotationLayer(string Name, IReadOnlyList<Shape> Shapes);

/// <summary>
/// What was found on a saved picture, kept beside it in a file of the same
/// name with the extension <c>.sann</c>: the shapes drawn on it, in layers,
/// the verdict and the published results.
/// </summary>
/// <remarks>
/// <para>
/// Vectors, not a second picture with the shapes burnt in: a viewer can
/// draw them over the picture, switch layers on and off, list them in a
/// table, and the picture itself stays as the camera took it, ready to be
/// inspected again.
/// </para>
/// <para>
/// JSON, so anything can read it. The format is <c>"sirius-annotations"</c>,
/// version 1; a reader ignores what it does not know, so later versions
/// add rather than change. Shapes are written as the custom node interfaces
/// write them - <c>point</c>, <c>line</c>, <c>rect</c>, <c>box</c>,
/// <c>circle</c>, <c>ellipse</c>, <c>polygon</c> - with their label, colour
/// and measurements.
/// </para>
/// </remarks>
public sealed class AnnotationFile
{
    /// <summary>The extension of an annotation file: "part.png" is described by "part.sann".</summary>
    public const string Extension = ".sann";

    /// <summary>What the file says it is.</summary>
    public const string Format = "sirius-annotations";

    /// <summary>The version this build writes, and the newest it reads.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The picture's file name, without its folder.</summary>
    public string? Image { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Bits that carry the picture: 12 for a 12-bit camera's frame. 0 when not known.</summary>
    public int Bits { get; init; }

    /// <summary>The station that took it.</summary>
    public string? Station { get; init; }

    /// <summary>The recipe's file name.</summary>
    public string? Recipe { get; init; }

    /// <summary>The station's count of inspections, this one included.</summary>
    public long Serial { get; init; }

    public DateTime? Time { get; init; }

    /// <summary>True passed, false failed, null not judged.</summary>
    public bool? Passed { get; init; }

    /// <summary>The worst grade, when the recipe grades.</summary>
    public string? Grade { get; init; }

    /// <summary>Nodes that went wrong in this inspection.</summary>
    public int NodeErrors { get; init; }

    /// <summary>
    /// The published results: numbers, yes/no and text, by published name,
    /// in the order the recipe publishes them.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, object?>> Results { get; init; } = [];

    public IReadOnlyList<AnnotationLayer> Layers { get; init; } = [];

    /// <summary>"PASS", "FAIL", "ERROR" or "NONE", as the picture's name says.</summary>
    public string VerdictWord => NodeErrors > 0 && Passed != false ? "ERROR"
        : Passed switch
        {
            true => "PASS",
            false => "FAIL",
            null => "NONE",
        };

    /// <summary>Every layer's shapes as one set, in the picture's frame.</summary>
    public ShapeSet AllShapes() => new(Layers.SelectMany(layer => layer.Shapes).ToArray(), Width, Height);

    /// <summary>The annotation file that goes with a picture: the same name, ending <c>.sann</c>.</summary>
    public static string PathFor(string imagePath) => Path.ChangeExtension(imagePath, Extension);

    // ---- writing ------------------------------------------------------------

    /// <summary>
    /// Writes the file, whole: to a temporary file first, so a failure part
    /// way - a full disk - leaves no half a file in place.
    /// </summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var temporary = path + ".saving";

        try
        {
            File.WriteAllText(temporary, ToJson().ToJsonString(Options));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["format"] = Format,
            ["version"] = CurrentVersion,
            ["image"] = Image,
            ["width"] = Width,
            ["height"] = Height,
        };

        if (Bits > 0)
            json["bits"] = Bits;

        if (Station is not null)
            json["station"] = Station;

        if (Recipe is not null)
            json["recipe"] = Recipe;

        if (Serial > 0)
            json["serial"] = Serial;

        if (Time is { } time)
            json["time"] = time.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

        json["verdict"] = VerdictWord;
        json["passed"] = Passed;

        if (Grade is not null)
            json["grade"] = Grade;

        if (NodeErrors > 0)
            json["nodeErrors"] = NodeErrors;

        var results = new JsonObject();

        foreach (var (name, value) in Results)
        {
            results[name] = value switch
            {
                double number when !double.IsFinite(number) => null,
                double number => number,
                float number when !float.IsFinite(number) => null,
                float number => number,
                int or long or short or byte => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                bool flag => flag,
                null => null,
                _ => Convert.ToString(value, CultureInfo.InvariantCulture),
            };
        }

        json["results"] = results;

        var layers = new JsonArray();

        foreach (var layer in Layers)
        {
            var shapes = new JsonArray();

            foreach (var shape in layer.Shapes)
            {
                if (ShapeJson.Write(shape) is { } written)
                    shapes.Add(written);
            }

            layers.Add(new JsonObject { ["name"] = layer.Name, ["shapes"] = shapes });
        }

        json["layers"] = layers;
        return json;
    }

    // ---- reading --------------------------------------------------------------

    /// <summary>Reads a file, or says in a sentence why it cannot.</summary>
    public static AnnotationFile? TryLoad(string path, out string? problem)
    {
        problem = null;

        try
        {
            return FromJson(JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                            ?? throw new InvalidDataException("It is not a JSON object."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
                                       or InvalidOperationException or FormatException)
        {
            problem = $"{Path.GetFileName(path)} could not be read: {ex.Message}";
            return null;
        }
    }

    public static AnnotationFile FromJson(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (Text(json, "format") is { } format && format != Format)
            throw new InvalidDataException($"It is a '{format}' file, not Sirius annotations.");

        if (json["version"] is JsonValue version && version.TryGetValue<int>(out var number) && number > CurrentVersion)
        {
            throw new InvalidDataException(
                $"It was written by a newer Sirius (version {number}); this one reads up to {CurrentVersion}.");
        }

        var results = new List<KeyValuePair<string, object?>>();

        if (json["results"] is JsonObject published)
        {
            foreach (var (name, value) in published)
                results.Add(new(name, Plain(value)));
        }

        var layers = new List<AnnotationLayer>();

        foreach (var layer in (json["layers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var shapes = (layer["shapes"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(ShapeJson.Read)
                .OfType<Shape>()
                .ToArray();

            layers.Add(new AnnotationLayer(Text(layer, "name") ?? $"Layer {layers.Count + 1}", shapes));
        }

        return new AnnotationFile
        {
            Image = Text(json, "image"),
            Width = Int(json, "width"),
            Height = Int(json, "height"),
            Bits = Int(json, "bits"),
            Station = Text(json, "station"),
            Recipe = Text(json, "recipe"),
            Serial = json["serial"] is JsonValue serial && serial.TryGetValue<long>(out var count) ? count : 0,
            Time = Text(json, "time") is { } time &&
                   DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)
                ? when
                : null,
            Passed = json["passed"] is JsonValue passed && passed.TryGetValue<bool>(out var flag) ? flag
                : Text(json, "verdict") switch
                {
                    "PASS" => true,
                    "FAIL" => false,
                    _ => null,
                },
            Grade = Text(json, "grade"),
            NodeErrors = Int(json, "nodeErrors"),
            Results = results,
            Layers = layers,
        };
    }

    private static object? Plain(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        JsonValue value when value.TryGetValue<long>(out var whole) => whole,
        JsonValue value when value.TryGetValue<double>(out var number) => number,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        null => null,
        _ => node.ToJsonString(),
    };

    private static string? Text(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int Int(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue<double>(out var number) ? (int)number : 0;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}

/// <summary>Shapes as JSON: the words the custom node interfaces use, read and written in one place.</summary>
public static class ShapeJson
{
    /// <summary>One shape, or null for a kind with no word.</summary>
    public static JsonObject? Write(Shape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        var json = shape switch
        {
            PointShape point => new JsonObject { ["kind"] = "point", ["x"] = point.At.X, ["y"] = point.At.Y },
            LineShape line => new JsonObject
            {
                ["kind"] = "line", ["x1"] = line.Start.X, ["y1"] = line.Start.Y, ["x2"] = line.End.X, ["y2"] = line.End.Y,
            },
            RectShape rect => new JsonObject
            {
                ["kind"] = "rect", ["x"] = rect.Rect.X, ["y"] = rect.Rect.Y, ["width"] = rect.Rect.Width, ["height"] = rect.Rect.Height,
            },
            OrientedRectShape box => new JsonObject
            {
                ["kind"] = "box", ["cx"] = box.Centre.X, ["cy"] = box.Centre.Y,
                ["width"] = box.Width, ["height"] = box.Height, ["angle"] = box.AngleDegrees,
            },
            CircleShape circle => new JsonObject
            {
                ["kind"] = "circle", ["cx"] = circle.Centre.X, ["cy"] = circle.Centre.Y, ["radius"] = circle.Radius,
            },
            EllipseShape ellipse => new JsonObject
            {
                ["kind"] = "ellipse", ["cx"] = ellipse.Centre.X, ["cy"] = ellipse.Centre.Y,
                ["major"] = ellipse.MajorAxis, ["minor"] = ellipse.MinorAxis, ["angle"] = ellipse.Angle,
            },
            PolygonShape polygon => new JsonObject
            {
                ["kind"] = "polygon",
                ["points"] = new JsonArray(polygon.Points.Select(point => (JsonNode)new JsonArray(point.X, point.Y)).ToArray()),
                ["closed"] = polygon.Closed,
            },
            _ => null,
        };

        if (json is null)
            return null;

        // Nothing that is not a number goes in: JSON has no NaN.
        foreach (var (name, value) in json.ToArray())
        {
            if (value is JsonValue number && number.TryGetValue<double>(out var reading) && !double.IsFinite(reading))
                json[name] = 0;
        }

        if (shape.Label is not null)
            json["label"] = shape.Label;

        if (shape.Colour is { } colour)
            json["colour"] = colour;

        if (shape.StrokeWidth is { } stroke)
            json["stroke"] = stroke;

        if (shape.LabelOnly)
            json["labelOnly"] = true;

        if (shape.LabelAnchor != LabelAnchor.AboveRight)
            json["anchor"] = shape.LabelAnchor.ToString();

        if (shape.LabelSize is { } size)
            json["labelSize"] = size;

        var metrics = new JsonObject();

        foreach (var (name, value) in shape.Metrics)
        {
            if (double.IsFinite(value))
                metrics[name] = value;
        }

        if (metrics.Count > 0)
            json["metrics"] = metrics;

        return json;
    }

    /// <summary>One shape, or null for a kind this build does not know.</summary>
    public static Shape? Read(JsonObject item)
    {
        ArgumentNullException.ThrowIfNull(item);

        double N(string name) => item[name] is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;

        Shape? shape = (item["kind"] as JsonValue)?.TryGetValue<string>(out var kind) == true ? kind switch
        {
            "point" => new PointShape(new Point2(N("x"), N("y"))),
            "line" => new LineShape(new Point2(N("x1"), N("y1")), new Point2(N("x2"), N("y2"))),
            "rect" => new RectShape(new Bounds(N("x"), N("y"), N("width"), N("height"))),
            "box" => new OrientedRectShape(new Point2(N("cx"), N("cy")), N("width"), N("height"), N("angle")),
            "circle" => new CircleShape(new Point2(N("cx"), N("cy")), N("radius")),
            "ellipse" => new EllipseShape(new Point2(N("cx"), N("cy")), N("major"), N("minor"), N("angle")),
            "polygon" => new PolygonShape(
                (item["points"] as JsonArray ?? []).OfType<JsonArray>()
                    .Where(point => point.Count >= 2)
                    .Select(point => new Point2(Number(point[0]), Number(point[1])))
                    .ToArray(),
                item["closed"] is not JsonValue closed || !closed.TryGetValue<bool>(out var isClosed) || isClosed),
            _ => null,
        } : null;

        if (shape is null)
            return null;

        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);

        if (item["metrics"] is JsonObject measured)
        {
            foreach (var (name, value) in measured)
            {
                if (value is JsonValue number && number.TryGetValue<double>(out var reading))
                    metrics[name] = reading;
            }
        }

        return shape with
        {
            Label = (item["label"] as JsonValue)?.TryGetValue<string>(out var label) == true ? label : null,
            Colour = item["colour"] is JsonValue colour && colour.TryGetValue<double>(out var argb) ? (uint)argb : null,
            StrokeWidth = item["stroke"] is JsonValue stroke && stroke.TryGetValue<double>(out var width) ? width : null,
            LabelOnly = item["labelOnly"] is JsonValue only && only.TryGetValue<bool>(out var labelOnly) && labelOnly,
            LabelAnchor = (item["anchor"] as JsonValue)?.TryGetValue<string>(out var anchor) == true &&
                          Enum.TryParse<LabelAnchor>(anchor, out var parsed)
                ? parsed
                : LabelAnchor.AboveRight,
            LabelSize = item["labelSize"] is JsonValue size && size.TryGetValue<double>(out var labelSize) ? labelSize : null,
            Metrics = metrics,
        };
    }

    private static double Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;
}
