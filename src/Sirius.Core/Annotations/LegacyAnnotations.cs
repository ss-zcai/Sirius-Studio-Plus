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
/// <item><c>Circle</c> (Centre, Radius), <c>RotatedRectangle</c> and
/// <c>Ellipse</c> (Centre, Width, Height, Rotation - clockwise, as ours and
/// OpenCV's) become shapes, each in a layer: its <c>LayerName</c>, or else
/// its <c>Name</c>. Not labelled on the picture - a lens can carry 150
/// defects. A defect's <c>ExtInfo.Descriptions</c> numbers - its length,
/// width and confidence, and the limits it was judged by - are its
/// measurements.</item>
/// <item><c>Colour</c> is [b, g, r], as OpenCV's.</item>
/// <item><c>meta</c> - batch, pallet, position, QR code, the multi-image
/// group - is the picture's part; <c>output</c> its results.</item>
/// <item>The verdict (examples of 24 November): <c>result</c> 200 with
/// <c>defectCode</c> 1 passes, 200 with any other code fails - the code
/// and message its grade, "803 Foreign Material"; 202, "Image received",
/// is a set's first picture, not judged; any other result is an
/// error.</item>
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

        if (json["extra"] is JsonObject extra)
        {
            foreach (var (name, value) in extra)
                Result(name, value);
        }

        var code = Text(output["defectCode"]);
        var message = Text(output["message"]);
        var (passed, errors) = VerdictOf(Number(output["result"]), code);
        var grade = string.Join(" ", new[] { code, message }.Where(word => !string.IsNullOrWhiteSpace(word)));

        return new AnnotationFile
        {
            Image = Text(json["fileName"]),
            Bits = BitsOf(Text(meta?["PixelFormat"])),
            Station = Text(meta?["Station"]),
            Recipe = Text(json["algo"]) is { } algo ? $"{algo} {Text(json["algoVersion"])}".Trim() : null,
            Serial = long.TryParse(request, NumberStyles.None, CultureInfo.InvariantCulture, out var serial) ? serial : 0,
            Time = TimeOf(request),
            Passed = passed,
            NodeErrors = errors,
            Grade = passed == true || grade.Length == 0 ? null : grade,
            Results = results,
            Part = part,
            Layers = Layers(Inner(output["annotations"]) as JsonArray),
        };
    }

    /// <summary>The code that passes: 200 and defect code 1 - "1_Pass" in the earlier names.</summary>
    public const string PassCode = "1";

    /// <summary>
    /// How it went: 200 with defect code 1 passed, with any other failed;
    /// 202 - a set's first picture, received - not judged; anything else
    /// an error.
    /// </summary>
    public static (bool? Passed, int Errors) VerdictOf(double? result, string? code) => result switch
    {
        200 => (string.Equals(code?.Trim(), PassCode, StringComparison.Ordinal), 0),
        202 => (null, 0),
        null => (null, 0),
        _ => (null, 1),
    };

    /// <summary>
    /// PASS, FAIL, ERROR or NONE from a picture's earlier annotation file,
    /// for Sirius IV's list; null when it has none or it cannot be read.
    /// </summary>
    public static string? VerdictWordFor(string imagePath)
    {
        var path = PathFor(imagePath);

        if (!File.Exists(path))
            return null;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject { } json || json["output"] is not JsonObject output)
                return null;

            return VerdictOf(Number(output["result"]), Text(output["defectCode"])) switch
            {
                (_, > 0) => "ERROR",
                (true, _) => "PASS",
                (false, _) => "FAIL",
                _ => "NONE",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The shapes, a layer for each layer name in the order first drawn.</summary>
    private static IReadOnlyList<AnnotationLayer> Layers(JsonArray? annotations)
    {
        if (annotations is null)
            return [];

        var layers = new List<(string Name, List<Shape> Shapes)>();

        foreach (var item in annotations.OfType<JsonObject>())
        {
            if (ShapeOf(item) is not { } shape)
                continue;

            var name = Text(item["LayerName"]) is { Length: > 0 } layerName ? layerName
                : Text(item["Name"]) is { Length: > 0 } named ? named
                : "Annotations";
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
            "Ellipse" when centre is { } at && Number(item["Width"]) is { } width && Number(item["Height"]) is { } height
                => new EllipseShape(at, width, height, Number(item["Rotation"]) ?? 0),
            _ => null,
        };

        return shape is null
            ? null
            : shape with { Colour = ColourOf(item["Colour"]), Metrics = MetricsOf(item["ExtInfo"]) };
    }

    /// <summary>The earlier system's names for a defect's numbers, in English.</summary>
    private static readonly IReadOnlyDictionary<string, string> MetricNames = new Dictionary<string, string>
    {
        ["瑕疵长度"] = "Length",
        ["瑕疵宽度"] = "Width",
        ["瑕疵置信度"] = "Confidence",
        ["当前调参应用检出长度"] = "Length Limit",
        ["当前调参应用检出宽度"] = "Width Limit",
        ["当前调参应用检出置信度"] = "Confidence Limit",
    };

    /// <summary>A defect's numbers - its <c>ExtInfo.Descriptions</c> - as its measurements; text left out.</summary>
    private static IReadOnlyDictionary<string, double> MetricsOf(JsonNode? extInfo)
    {
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);

        if (extInfo is JsonObject { } info && info["Descriptions"] is JsonObject descriptions)
        {
            foreach (var (name, value) in descriptions)
            {
                if (Number(value) is { } number && double.IsFinite(number))
                    metrics[MetricNames.GetValueOrDefault(name, name)] = number;
            }
        }

        return metrics;
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
