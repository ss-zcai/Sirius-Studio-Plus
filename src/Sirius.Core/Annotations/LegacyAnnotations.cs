using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sirius.Core.Shapes;

namespace Sirius.Core.Annotations;

/// <summary>
/// The earlier image viewer's annotation files, read - never written: a
/// picture's <c>name.tiff.json</c> beside it, as the earlier vision system
/// wrote them (the Jianfan multi-image examples, 24 November 2026).
/// </summary>
/// <remarks>
/// <para>
/// Read into an <see cref="AnnotationFile"/>, so Sirius IV's viewers, its
/// Annotations panel, layers and exports take them as they take a
/// station's <c>.sann</c>. A <c>.sann</c> beside the picture wins.
/// </para>
/// <para>
/// The file holds JSON inside JSON: <c>meta</c>, <c>output.annotations</c>
/// and <c>output.info</c> are strings of JSON. <c>info</c> carries Windows
/// paths with their backslashes left unescaped, so it is read leniently.
/// </para>
/// <list type="bullet">
/// <item><c>Circle</c> (Centre, Radius) and <c>RotatedRectangle</c> (Centre,
/// Width, Height, Rotation - clockwise, as ours and OpenCV's) become shapes;
/// <c>Name</c> is the shape's label and its layer.</item>
/// <item><c>Colour</c> is [b, g, r], as OpenCV's - to be confirmed.</item>
/// <item><c>meta</c> - batch, pallet, position, QR code, the multi-image
/// group - is the picture's part; <c>output</c> its results.</item>
/// <item>Which result codes pass is not known yet: the verdict is left
/// unjudged, its code and message shown as the grade.</item>
/// </list>
/// </remarks>
public static class LegacyAnnotations
{
    /// <summary>What the earlier viewer's file adds to the picture's whole name.</summary>
    public const string Extension = ".json";

    /// <summary>The earlier viewer's file for a picture: its whole name and <c>.json</c>.</summary>
    public static string PathFor(string imagePath) => imagePath + Extension;

    /// <summary>The file read, or null with <paramref name="problem"/> when it cannot be.</summary>
    public static AnnotationFile? TryLoad(string path, out string? problem)
    {
        problem = null;

        try
        {
            var json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                       ?? throw new InvalidDataException("It is not a JSON object.");

            return FromJson(json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
                                       or InvalidOperationException or FormatException)
        {
            problem = $"{Path.GetFileName(path)} could not be read: {ex.Message}";
            return null;
        }
    }

    /// <summary>Whether a JSON object is one of the earlier viewer's: it has an <c>output</c> and a <c>fileName</c>.</summary>
    public static bool IsLegacy(JsonObject json) => json["output"] is JsonObject && json["fileName"] is not null;

    public static AnnotationFile FromJson(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (!IsLegacy(json))
            throw new InvalidDataException("It is not one of the earlier image viewer's annotation files.");

        var output = (JsonObject)json["output"]!;
        var meta = Inner(json["meta"]) as JsonObject;
        var request = Text(json["requestId"]);

        var results = new List<KeyValuePair<string, object?>>();

        void Result(string name, JsonNode? node)
        {
            if (Value(node) is { } value)
                results.Add(new(name, value));
        }

        Result("Result", output["result"]);
        Result("Defect Code", output["defectCode"]);
        Result("Message", output["message"]);
        Result("Processing Time (ms)", output["processingTime"]);
        Result("Algorithm", json["algo"]);
        Result("Algorithm Version", json["algoVersion"]);

        if (json["args"] is JsonArray args && args.Count > 0)
            results.Add(new("Arguments", string.Join(", ", args.Select(Text))));

        switch (Inner(output["info"], lenient: true))
        {
            case JsonObject info:
                foreach (var (name, value) in info)
                    Result(name, value);
                break;

            case JsonValue text when Text(text) is { Length: > 0 } words:
                results.Add(new("Info", words));
                break;
        }

        var part = meta is null
            ? []
            : meta.Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value is JsonArray list ? string.Join(", ", list.Select(Text)) : Value(pair.Value)))
                .Where(pair => pair.Value is not null)
                .ToArray();

        var code = Text(output["defectCode"]);
        var message = Text(output["message"]);

        return new AnnotationFile
        {
            Image = Text(json["fileName"]),
            Bits = BitsOf(Text(meta?["PixelFormat"])),
            Station = Text(meta?["Station"]),
            Recipe = Text(json["algo"]) is { } algo ? $"{algo} {Text(json["algoVersion"])}".Trim() : null,
            Serial = long.TryParse(request, NumberStyles.None, CultureInfo.InvariantCulture, out var serial) ? serial : 0,
            Time = TimeOf(request),
            Passed = null,
            Grade = string.Join(" ", new[] { code, message }.Where(word => !string.IsNullOrWhiteSpace(word))) is { Length: > 0 } grade ? grade : null,
            Results = results,
            Part = part,
            Layers = Layers(Inner(output["annotations"]) as JsonArray),
        };
    }

    /// <summary>The shapes, a layer for each name in the order first drawn.</summary>
    private static IReadOnlyList<AnnotationLayer> Layers(JsonArray? annotations)
    {
        if (annotations is null)
            return [];

        var layers = new List<(string Name, List<Shape> Shapes)>();

        foreach (var item in annotations.OfType<JsonObject>())
        {
            if (ShapeOf(item) is not { } shape)
                continue;

            var name = shape.Label ?? "Annotations";
            var layer = layers.FirstOrDefault(entry => entry.Name == name);

            if (layer.Shapes is null)
            {
                layer = (name, []);
                layers.Add(layer);
            }

            layer.Shapes.Add(shape);
        }

        return layers.Select(layer => new AnnotationLayer(layer.Name, layer.Shapes)).ToArray();
    }

    private static Shape? ShapeOf(JsonObject item)
    {
        var centre = PointOf(item["Centre"]);

        Shape? shape = Text(item["Type"]) switch
        {
            "Circle" when centre is { } at && Number(item["Radius"]) is { } radius
                => new CircleShape(at, radius),
            "RotatedRectangle" when centre is { } at && Number(item["Width"]) is { } width && Number(item["Height"]) is { } height
                => new OrientedRectShape(at, width, height, Number(item["Rotation"]) ?? 0),
            "Rectangle" when centre is { } at && Number(item["Width"]) is { } width && Number(item["Height"]) is { } height
                => new OrientedRectShape(at, width, height, 0),
            _ => null,
        };

        return shape is null
            ? null
            : shape with { Label = Text(item["Name"]), Colour = ColourOf(item["Colour"]) };
    }

    /// <summary>[b, g, r], as OpenCV's, to an opaque ARGB colour.</summary>
    private static uint? ColourOf(JsonNode? node)
    {
        if (node is not JsonArray { Count: >= 3 } parts || parts.Take(3).Select(Number).ToArray() is not [{ } b, { } g, { } r])
            return null;

        static uint Channel(double value) => (uint)Math.Clamp(Math.Round(value), 0, 255);

        return 0xFF000000u | (Channel(r) << 16) | (Channel(g) << 8) | Channel(b);
    }

    private static Point2? PointOf(JsonNode? node) =>
        node is JsonArray { Count: >= 2 } point && Number(point[0]) is { } x && Number(point[1]) is { } y ? new Point2(x, y) : null;

    /// <summary>"Mono8" 8 bits, "Mono12" 12; 0 - unknown - for any other.</summary>
    private static int BitsOf(string? format) =>
        format is not null && Regex.Match(format, @"^Mono(\d+)", RegexOptions.IgnoreCase) is { Success: true } match &&
        int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bits) && bits is >= 8 and <= 16
            ? bits
            : 0;

    /// <summary>
    /// The time in a request ID: "260511155932312022" is 2026-05-11
    /// 15:59:32.312, then the shot. Null when it does not read as one.
    /// </summary>
    internal static DateTime? TimeOf(string? request) =>
        request is { Length: >= 15 } &&
        DateTime.TryParseExact(request[..15], "yyMMddHHmmssfff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : null;

    /// <summary>
    /// A string of JSON, read; the node itself when it is not a string. A
    /// string that is not JSON stays text.
    /// </summary>
    /// <param name="lenient">When it does not read as it is, its backslashes - Windows paths - taken as themselves.</param>
    private static JsonNode? Inner(JsonNode? node, bool lenient = false)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
            return node;

        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Leniently, every backslash but one before a quote is a path's: "\feature" is not a form feed.
        foreach (var attempt in lenient ? new[] { text, Regex.Replace(text, @"\\(?!"")", @"\\") } : [text])
        {
            try
            {
                return JsonNode.Parse(attempt);
            }
            catch (JsonException)
            {
                // Next, or the text as it is.
            }
        }

        return JsonValue.Create(text);
    }

    private static string? Text(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value => value.ToJsonString(),
        _ => null,
    };

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    /// <summary>A plain value as a result keeps it: a number, a yes/no, text.</summary>
    private static object? Value(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        JsonValue value when value.TryGetValue<long>(out var whole) => whole,
        JsonValue value when value.TryGetValue<double>(out var number) => number,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray list => string.Join(", ", list.Select(Text)),
        _ => null,
    };
}
