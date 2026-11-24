using System.Globalization;
using System.Text.Json.Nodes;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Plugins;

namespace Sirius.Vision.Plugins;

/// <summary>An input or output a Python node declared - or a C++ one, which declares the same way.</summary>
internal sealed record PythonPort(string Name, string Label, string Kind, bool Required);

/// <summary>A setting a Python node declared - or a C++ one.</summary>
internal sealed record PythonSetting(
    string Name, string Kind, object Default, bool Connectable, IReadOnlyList<string> Choices, ParamAttribute Attribute)
{
    public Type ValueType => PythonNodeType.SettingType(Kind);
}

/// <summary>
/// One node class from a Python library, checked and described for the
/// catalogue.
/// </summary>
internal sealed class PythonNodeType
{
    private static readonly Dictionary<string, Type> PortTypes = new(StringComparer.Ordinal)
    {
        ["image"] = typeof(ImageData),
        ["number"] = typeof(double),
        ["integer"] = typeof(int),
        ["bool"] = typeof(bool),
        ["text"] = typeof(string),
        ["shapes"] = typeof(ShapeSet),
    };

    private PythonNodeType(
        string key, string name, PythonWorker worker, IReadOnlyList<PythonPort> inputs,
        IReadOnlyList<PythonPort> outputs, IReadOnlyList<PythonSetting> settings, NodeDescriptor descriptor)
    {
        Key = key;
        Name = name;
        Worker = worker;
        Inputs = inputs;
        Outputs = outputs;
        Settings = settings;
        Descriptor = descriptor;
    }

    /// <summary>The key as the library wrote it, without the custom prefix.</summary>
    public string Key { get; }

    public string Name { get; }

    public PythonWorker Worker { get; }

    public IReadOnlyList<PythonPort> Inputs { get; }

    public IReadOnlyList<PythonPort> Outputs { get; }

    public IReadOnlyList<PythonSetting> Settings { get; }

    public NodeDescriptor Descriptor { get; }

    internal static Type PortType(string kind) => PortTypes[kind];

    internal static Type SettingType(string kind) => kind switch
    {
        "number" => typeof(double),
        "integer" => typeof(int),
        "bool" => typeof(bool),
        _ => typeof(string),
    };

    /// <summary>
    /// Builds a node type from what the worker said, or explains why it
    /// cannot be added.
    /// </summary>
    public static PythonNodeType? TryCreate(
        JsonObject description, PythonWorker worker, string origin, ICollection<string> problems)
    {
        var name = Text(description, "name") ?? Text(description, "class") ?? "A node";
        var found = (description["problems"]?.AsArray() ?? []).Select(problem => problem?.GetValue<string>())
            .OfType<string>().ToList();

        var key = Text(description, "key");
        if (found.Count == 0 && CustomNodeType.KeyProblem(key) is { } keyProblem)
            found.Add(keyProblem);

        if (!Enum.TryParse<NodeCategory>(Text(description, "group"), out var category))
            category = NodeCategory.Custom;

        var inputs = Ports(description["inputs"], found);
        var outputs = Ports(description["outputs"], found);
        var settings = (description["settings"]?.AsArray() ?? []).OfType<JsonObject>()
            .Select(setting => Setting(setting, found))
            .OfType<PythonSetting>()
            .ToList();

        foreach (var clash in inputs.Select(port => port.Name).Concat(outputs.Select(port => port.Name))
                     .GroupBy(port => port).Where(group => group.Count() > 1))
        {
            found.Add($"has two ports called '{clash.Key}'.");
        }

        if (found.Count > 0)
        {
            foreach (var problem in found.Distinct())
                problems.Add($"{name}: {problem}");

            return null;
        }

        PythonNodeType? type = null;

        var descriptor = NodeDescriptor.Create(
            typeof(PythonNode),
            name,
            category,
            Text(description, "icon") ?? CustomNodeType.DefaultIcon,
            Text(description, "description"),
            CustomNodeType.KeyPrefix + key,
            settings.Select(Describe).ToArray(),
            origin,
            () => new PythonNode(type!));

        type = new PythonNodeType(key!, name, worker, inputs, outputs, settings, descriptor);
        return type;
    }

    internal static List<PythonPort> Ports(JsonNode? list, ICollection<string> problems)
    {
        var ports = new List<PythonPort>();

        foreach (var port in (list?.AsArray() ?? []).OfType<JsonObject>())
        {
            var name = Text(port, "name") ?? "";
            var kind = Text(port, "type") ?? "";

            if (!PortTypes.ContainsKey(kind))
            {
                problems.Add($"has a port '{name}' of type '{kind}', which nothing can wire.");
                continue;
            }

            ports.Add(new PythonPort(name, Text(port, "label") ?? name, kind, port["required"]?.GetValue<bool>() ?? true));
        }

        return ports;
    }

    internal static PythonSetting? Setting(JsonObject setting, ICollection<string> problems)
    {
        var name = Text(setting, "name") ?? "";
        var kind = Text(setting, "type") ?? "";

        if (kind is not ("number" or "integer" or "bool" or "text" or "choice" or "file"))
        {
            problems.Add($"has a setting '{name}' of type '{kind}', which nothing can edit.");
            return null;
        }

        var choices = (setting["choices"]?.AsArray() ?? []).Select(choice => choice?.ToString()).OfType<string>().ToArray();

        var attribute = new ParamAttribute(Text(setting, "label") ?? name)
        {
            Description = Text(setting, "description"),
            Group = Text(setting, "group"),
            Min = Number(setting, "min"),
            Max = Number(setting, "max"),
            Step = Number(setting, "step"),
            Logarithmic = setting["logarithmic"]?.GetValue<bool>() ?? false,
            Connectable = setting["connectable"]?.GetValue<bool>() ?? false,
            Filter = Text(setting, "filter"),
            Editor = kind switch
            {
                "choice" => ParamEditor.ComboBox,
                "file" => ParamEditor.FilePath,
                _ => ParamEditor.Auto,
            },
        };

        var fallback = Convert(setting["default"] is JsonValue value ? Plain(value) : null, kind);

        return new PythonSetting(name, kind, fallback, attribute.Connectable, choices, attribute);
    }

    private static ParamDescriptor Describe(PythonSetting setting) =>
        ParamDescriptor.Create(
            setting.Name,
            setting.ValueType,
            setting.Attribute,
            node => ((PythonNode)node).Value(setting),
            (node, value) => ((PythonNode)node).Change(setting, value));

    /// <summary>A setting's value as its type, whatever it was given as.</summary>
    internal static object Convert(object? value, string kind)
    {
        try
        {
            return kind switch
            {
                "number" => value is null ? 0.0 : System.Convert.ToDouble(value, CultureInfo.InvariantCulture),
                "integer" => value is null ? 0 : (int)Math.Round(System.Convert.ToDouble(value, CultureInfo.InvariantCulture)),
                "bool" => value is not null && System.Convert.ToBoolean(value, CultureInfo.InvariantCulture),
                _ => System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new ArgumentException($"'{value}' is not a {kind}.", nameof(value), ex);
        }
    }

    internal static object? Plain(JsonValue value) =>
        value.TryGetValue<bool>(out var flag) ? flag
        : value.TryGetValue<double>(out var number) ? number
        : value.TryGetValue<string>(out var text) ? text
        : value.ToJsonString();

    internal static string? Text(JsonObject source, string name) =>
        source[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static double Number(JsonObject source, string name) =>
        source[name] is JsonValue value && value.TryGetValue<double>(out var number) ? number : double.NaN;
}

/// <summary>
/// The node a Python node lives inside.
/// </summary>
/// <remarks>
/// To the graph, the editor and the serialiser this is an ordinary node,
/// as a C# custom node is. Each run sends its settings and inputs to the
/// library's worker and publishes what comes back. The Python object behind
/// it is made on the first run and kept, so its <c>load()</c> runs once.
/// </remarks>
public sealed class PythonNode : VisionNode, IParameterChoices, INodeActions
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InputPort> _inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OutputPort> _outputs = new(StringComparer.Ordinal);
    private readonly string _instance = Guid.NewGuid().ToString("N");

    internal PythonNode(PythonNodeType type)
        : base(type.Descriptor)
    {
        Type = type;

        foreach (var setting in type.Settings)
            _values[setting.Name] = setting.Default;

        foreach (var port in type.Inputs)
            _inputs[port.Name] = AddInput(PythonNodeType.PortType(port.Kind), port.Name, port.Label, port.Required);

        foreach (var port in type.Outputs)
            _outputs[port.Name] = AddOutput(PythonNodeType.PortType(port.Kind), port.Name, port.Label);

        Actions =
        [
            new NodeAction(
                "Restart Python",
                "Ends this library's Python process and starts a new one, reading the file again - after the code " +
                "has been edited, or to free the memory its models hold. load() runs again on the next run.",
                () =>
                {
                    Type.Worker.Restart();
                    Invalidate();
                    return null;
                }),
        ];
    }

    internal PythonNodeType Type { get; }

    public IReadOnlyList<NodeAction> Actions { get; }

    public IReadOnlyList<string> ChoicesFor(string parameter) =>
        Type.Settings.FirstOrDefault(setting => setting.Name == parameter)?.Choices ?? [];

    internal object Value(PythonSetting setting) => _values[setting.Name];

    /// <summary>
    /// Sets a setting, announcing it the way a built-in parameter does, so
    /// the properties panel, undo and the scheduler all see it.
    /// </summary>
    internal void Change(PythonSetting setting, object? value)
    {
        var converted = PythonNodeType.Convert(value, setting.Kind);

        if (Equals(_values[setting.Name], converted))
            return;

        OnPropertyChanging(setting.Name);
        _values[setting.Name] = converted;
        OnPropertyChanged(setting.Name);
        Invalidate();
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var views = new List<Picture>();
        var temporaries = new List<IDisposable>();

        try
        {
            var blobs = new List<OutgoingBlob>();
            var (inputs, frame, first) = ReadInputs(context, views, temporaries, blobs);

            var request = new JsonObject
            {
                ["op"] = "run",
                ["node"] = Type.Key,
                ["instance"] = _instance,
                ["settings"] = Settings(context),
                ["inputs"] = inputs,
            };

            var reply = Type.Worker.Call(request, blobs, $"running {Type.Name}", context.CancellationToken);
            var header = reply.Header;

            if (header["ok"]?.GetValue<bool>() != true)
                throw new PythonException(Failure(header));

            Publish(context, header["outputs"] as JsonObject, reply.Blobs, frame, first);

            var warnings = (header["warnings"]?.AsArray() ?? []).Select(warning => warning?.GetValue<string>())
                .OfType<string>().ToArray();

            if (warnings.Length > 0)
                context.Warn(string.Join(" ", warnings));
        }
        finally
        {
            foreach (var view in views)
                view.Expire();

            foreach (var temporary in temporaries)
                temporary.Dispose();
        }
    }

    private JsonObject Settings(EvalContext context)
    {
        var settings = new JsonObject();

        foreach (var setting in Type.Settings)
        {
            var value = _values[setting.Name];

            if (setting.Connectable)
            {
                value = value switch
                {
                    double number => context.Param(setting.Name, number),
                    int integer => context.Param(setting.Name, integer),
                    bool flag => context.Param(setting.Name, flag),
                    string text => context.Param(setting.Name, text),
                    _ => value,
                };
            }

            settings[setting.Name] = value switch
            {
                double number when !double.IsFinite(number) => null,
                double number => number,
                int integer => integer,
                bool flag => flag,
                _ => System.Convert.ToString(value, CultureInfo.InvariantCulture),
            };
        }

        return settings;
    }

    private (JsonObject Inputs, (int Width, int Height) Frame, (Emgu.CV.CvEnum.DepthType Depth, int Bits)? First) ReadInputs(
        EvalContext context, List<Picture> views, List<IDisposable> temporaries, List<OutgoingBlob> blobs)
    {
        var inputs = new JsonObject();
        (int Width, int Height) frame = default;
        (Emgu.CV.CvEnum.DepthType Depth, int Bits)? first = null;

        foreach (var port in Type.Inputs)
        {
            var raw = context.Get<object>(_inputs[port.Name]);
            if (raw is null)
                continue;

            switch (port.Kind)
            {
                case "image" when raw is ImageData image:
                    var view = PictureBridge.View(image, temporaries);
                    views.Add(view);

                    var picture = new JsonObject
                    {
                        ["width"] = view.Width,
                        ["height"] = view.Height,
                        ["channels"] = view.Channels,
                        ["dtype"] = DType(view.Pixels),
                        ["layout"] = view.Layout.ToString().ToLowerInvariant(),
                        ["blob"] = blobs.Count,
                    };

                    // How many bits carry it - 12 for a 12-bit camera's
                    // frame - when it went as it is; a type converted to
                    // float on the way has no such limit.
                    if (PictureBridge.PixelsOf(image.Depth) == view.Pixels && image.Bits != 0)
                        picture["bits"] = image.Bits;

                    inputs[port.Name] = picture;
                    first ??= (image.Depth, image.Bits);

                    blobs.Add(new OutgoingBlob((long)view.Stride * view.Height, stream => stream.Write(view.Bytes)));

                    if (frame == default)
                        frame = (view.Width, view.Height);
                    break;

                case "shapes" when raw is ShapeSet shapes:
                    inputs[port.Name] = PythonShapes.ToJson(shapes);
                    break;

                case "number" or "integer":
                    var number = System.Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                    if (double.IsFinite(number))
                        inputs[port.Name] = port.Kind == "integer" ? (long)Math.Round(number) : number;
                    break;

                case "bool":
                    inputs[port.Name] = System.Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
                    break;

                case "text":
                    inputs[port.Name] = System.Convert.ToString(raw, CultureInfo.InvariantCulture);
                    break;
            }
        }

        return (inputs, frame, first);
    }

    private void Publish(
        EvalContext context, JsonObject? outputs, IReadOnlyList<byte[]> blobs, (int Width, int Height) frame,
        (Emgu.CV.CvEnum.DepthType Depth, int Bits)? first)
    {
        if (outputs is null)
            return;

        // Shapes are in the coordinates of the image they were found in:
        // the first image in, or failing that the first image out.
        if (frame == default)
        {
            frame = Type.Outputs.Where(port => port.Kind == "image")
                .Select(port => outputs[port.Name] as JsonObject)
                .OfType<JsonObject>()
                .Select(image => (image["width"]!.GetValue<int>(), image["height"]!.GetValue<int>()))
                .FirstOrDefault();
        }

        foreach (var port in Type.Outputs)
        {
            if (outputs[port.Name] is not { } value)
                continue;

            var output = _outputs[port.Name];

            switch (port.Kind)
            {
                case "image":
                    context.Set(output, Image((JsonObject)value, blobs, first));
                    break;

                case "shapes":
                    context.Set(output, PythonShapes.FromJson(value.AsArray(), frame.Width, frame.Height));
                    break;

                case "number":
                    context.Set(output, value.GetValue<double>());
                    break;

                case "integer":
                    context.Set(output, (int)Math.Clamp(value.GetValue<double>(), int.MinValue, int.MaxValue));
                    break;

                case "bool":
                    context.Set(output, value.GetValue<bool>());
                    break;

                case "text":
                    context.Set(output, value.GetValue<string>());
                    break;
            }
        }
    }

    /// <param name="first">
    /// The first image in, whose bits a result of the same type keeps unless
    /// the node said otherwise - as the built-in tools keep theirs. A
    /// Python node that blurs a 12-bit image with OpenCV gets back a plain
    /// array, which says nothing.
    /// </param>
    private static ImageData Image(JsonObject image, IReadOnlyList<byte[]> blobs, (Emgu.CV.CvEnum.DepthType Depth, int Bits)? first)
    {
        var width = image["width"]!.GetValue<int>();
        var height = image["height"]!.GetValue<int>();
        var channels = image["channels"]!.GetValue<int>();

        var pixels = image["dtype"]?.GetValue<string>() switch
        {
            "uint8" => PixelType.Byte8,
            "uint16" => PixelType.UInt16,
            "float32" => PixelType.Float32,
            var other => throw new PythonException($"returned an image of {other} samples, which cannot be shown."),
        };

        var layout = Enum.TryParse<ColourLayout>(image["layout"]?.GetValue<string>(), ignoreCase: true, out var parsed)
            ? parsed
            : ColourLayout.Unknown;

        int? bits = image["bits"] is { } said
            ? said.GetValue<int>()
            : first is { } inherited && inherited.Depth == PictureBridge.DepthOf(pixels) ? inherited.Bits : null;

        return PictureBridge.ToImage(
            blobs[image["blob"]!.GetValue<int>()], width, height, channels, pixels, layout, bits is 0 ? null : bits);
    }

    private static string DType(PixelType pixels) => pixels switch
    {
        PixelType.UInt16 => "uint16",
        PixelType.Float32 => "float32",
        _ => "uint8",
    };

    /// <summary>"load() failed: ModuleNotFoundError: ... (ocr.py, line 8, in load)".</summary>
    private string Failure(JsonObject header)
    {
        var error = header["error"]?.GetValue<string>() ?? "Python did not say what went wrong";
        var where = PythonErrors.Where(header, Type.Worker.Script);

        return header["where"]?.GetValue<string>() switch
        {
            "load" => $"load() failed: {error}{where}",
            "create" => $"could not be created: {error}{where}",
            _ => $"{error}{where}",
        };
    }
}

/// <summary>Shapes as the Python side writes them.</summary>
internal static class PythonShapes
{
    public static JsonObject ToJson(ShapeSet set)
    {
        var shapes = new JsonArray();

        foreach (var shape in set.Shapes)
        {
            if (ToJson(shape) is { } written)
                shapes.Add(written);
        }

        return new JsonObject { ["shapes"] = shapes, ["width"] = set.FrameWidth, ["height"] = set.FrameHeight };
    }

    private static JsonObject? ToJson(Shape shape)
    {
        var json = shape switch
        {
            PointShape point => new JsonObject { ["kind"] = "point", ["x"] = point.At.X, ["y"] = point.At.Y },
            LineShape line => new JsonObject
            {
                ["kind"] = "line", ["x1"] = line.Start.X, ["y1"] = line.Start.Y, ["x2"] = line.End.X, ["y2"] = line.End.Y,
            },
            RectShape rect => new JsonObject
            {
                ["kind"] = "rect", ["x"] = rect.Rect.X, ["y"] = rect.Rect.Y,
                ["width"] = rect.Rect.Width, ["height"] = rect.Rect.Height,
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

        if (shape.Label is not null)
            json["label"] = shape.Label;

        if (shape.Colour is { } colour)
            json["colour"] = colour;

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

    public static ShapeSet FromJson(JsonArray shapes, int frameWidth, int frameHeight)
    {
        var result = new List<Shape>(shapes.Count);

        foreach (var item in shapes.OfType<JsonObject>())
        {
            double N(string name) => item[name] is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;

            Shape? shape = item["kind"]?.GetValue<string>() switch
            {
                "point" => new PointShape(new Point2(N("x"), N("y"))),
                "line" => new LineShape(new Point2(N("x1"), N("y1")), new Point2(N("x2"), N("y2"))),
                "rect" => new RectShape(new Bounds(N("x"), N("y"), N("width"), N("height"))),
                "box" => new OrientedRectShape(new Point2(N("cx"), N("cy")), N("width"), N("height"), N("angle")),
                "circle" => new CircleShape(new Point2(N("cx"), N("cy")), N("radius")),
                "ellipse" => new EllipseShape(new Point2(N("cx"), N("cy")), N("major"), N("minor"), N("angle")),
                "polygon" => new PolygonShape(
                    (item["points"]?.AsArray() ?? []).OfType<JsonArray>()
                        .Select(point => new Point2(point[0]!.GetValue<double>(), point[1]!.GetValue<double>()))
                        .ToArray(),
                    item["closed"]?.GetValue<bool>() ?? true),
                _ => null,
            };

            if (shape is null)
                continue;

            var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
            if (item["metrics"] is JsonObject measured)
            {
                foreach (var (name, value) in measured)
                {
                    if (value is JsonValue number && number.TryGetValue<double>(out var reading))
                        metrics[name] = reading;
                }
            }

            result.Add(shape with
            {
                Label = item["label"]?.GetValue<string>(),
                Colour = item["colour"] is JsonValue colour && colour.TryGetValue<double>(out var argb) ? (uint)argb : null,
                Metrics = metrics,
            });
        }

        return new ShapeSet(result, frameWidth, frameHeight);
    }
}
