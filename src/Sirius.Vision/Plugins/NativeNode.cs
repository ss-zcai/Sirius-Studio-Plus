using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Plugins;

namespace Sirius.Vision.Plugins;

/// <summary>
/// One node class from a C++ library, checked and described for the
/// catalogue.
/// </summary>
/// <remarks>
/// A C++ library describes its nodes in the same words a Python one does -
/// the same port and setting types, the same JSON - so the two are read by
/// the same code and look the same in the toolbox and the properties panel.
/// </remarks>
internal sealed class NativeNodeType
{
    private NativeNodeType(
        string key, string name, NativeNodeLibrary library, IReadOnlyList<PythonPort> inputs,
        IReadOnlyList<PythonPort> outputs, IReadOnlyList<PythonSetting> settings, IReadOnlySet<string> reloading,
        NodeDescriptor descriptor)
    {
        Key = key;
        Name = name;
        Library = library;
        Inputs = inputs;
        Outputs = outputs;
        Settings = settings;
        Reloading = reloading;
        Descriptor = descriptor;
    }

    /// <summary>The key as the library wrote it, without the custom prefix.</summary>
    public string Key { get; }

    public string Name { get; }

    public NativeNodeLibrary Library { get; }

    public IReadOnlyList<PythonPort> Inputs { get; }

    public IReadOnlyList<PythonPort> Outputs { get; }

    public IReadOnlyList<PythonSetting> Settings { get; }

    /// <summary>The settings whose change calls the node's load() again.</summary>
    public IReadOnlySet<string> Reloading { get; }

    public NodeDescriptor Descriptor { get; }

    /// <summary>
    /// Builds a node type from what the library said, or explains why it
    /// cannot be added.
    /// </summary>
    public static NativeNodeType? TryCreate(
        JsonObject description, NativeNodeLibrary library, string origin, ICollection<string> problems)
    {
        var name = PythonNodeType.Text(description, "name") ?? "A node";
        var found = new List<string>();

        var key = PythonNodeType.Text(description, "key");
        // The same rule as a C# node's TypeKey, in this interface's word for it.
        if (CustomNodeType.KeyProblem(key) is { } keyProblem)
            found.Add(keyProblem.Replace("TypeKey", "key", StringComparison.Ordinal));

        if (!Enum.TryParse<NodeCategory>(PythonNodeType.Text(description, "group"), out var category))
            category = NodeCategory.Custom;

        var inputs = PythonNodeType.Ports(description["inputs"], found);
        var outputs = PythonNodeType.Ports(description["outputs"], found);
        var declared = (description["settings"]?.AsArray() ?? []).OfType<JsonObject>().ToList();
        var settings = declared.Select(setting => PythonNodeType.Setting(setting, found)).OfType<PythonSetting>().ToList();

        // One name, one thing: run() asks for all three by name alone.
        foreach (var clash in inputs.Select(port => port.Name)
                     .Concat(outputs.Select(port => port.Name))
                     .Concat(settings.Select(setting => setting.Name))
                     .GroupBy(item => item).Where(group => group.Count() > 1))
        {
            found.Add($"uses the name '{clash.Key}' for more than one port or setting.");
        }

        if (inputs.Concat(outputs).Any(port => port.Name.Length == 0) || settings.Any(setting => setting.Name.Length == 0))
            found.Add("has a port or a setting with no name.");

        if (found.Count > 0)
        {
            foreach (var problem in found.Distinct())
                problems.Add($"{name}: {problem}");

            return null;
        }

        var reloading = declared
            .Where(setting => setting["reload"] is JsonValue value && value.TryGetValue<bool>(out var reload) && reload)
            .Select(setting => PythonNodeType.Text(setting, "name") ?? "")
            .ToHashSet(StringComparer.Ordinal);

        NativeNodeType? type = null;

        var descriptor = NodeDescriptor.Create(
            typeof(NativeNode),
            name,
            category,
            PythonNodeType.Text(description, "icon") ?? CustomNodeType.DefaultIcon,
            PythonNodeType.Text(description, "description"),
            CustomNodeType.KeyPrefix + key,
            settings.Select(Describe).ToArray(),
            origin,
            () => new NativeNode(type!));

        type = new NativeNodeType(key!, name, library, inputs, outputs, settings, reloading, descriptor);
        return type;
    }

    private static ParamDescriptor Describe(PythonSetting setting) =>
        ParamDescriptor.Create(
            setting.Name,
            setting.ValueType,
            setting.Attribute,
            node => ((NativeNode)node).Value(setting),
            (node, value) => ((NativeNode)node).Change(setting, value));
}

/// <summary>
/// The node a C++ node lives inside.
/// </summary>
/// <remarks>
/// To the graph, the editor and the serialiser this is an ordinary node, as
/// a C# or a Python custom node is. Each run calls straight into the
/// library, in this process: images go as pointers to the memory they are
/// already in, and come back with one copy. The C++ object behind it is
/// made on the first run and kept, so its <c>load()</c> runs once - and
/// again only when a setting declared with <c>reload</c> changes.
/// </remarks>
public sealed class NativeNode : VisionNode, IParameterChoices
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InputPort> _inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OutputPort> _outputs = new(StringComparer.Ordinal);

    private Instance? _instance;
    private object[]? _loadedWith;

    internal NativeNode(NativeNodeType type)
        : base(type.Descriptor)
    {
        Type = type;

        foreach (var setting in type.Settings)
            _values[setting.Name] = setting.Default;

        foreach (var port in type.Inputs)
            _inputs[port.Name] = AddInput(PythonNodeType.PortType(port.Kind), port.Name, port.Label, port.Required);

        foreach (var port in type.Outputs)
            _outputs[port.Name] = AddOutput(PythonNodeType.PortType(port.Kind), port.Name, port.Label);
    }

    internal NativeNodeType Type { get; }

    public IReadOnlyList<string> ChoicesFor(string parameter) =>
        Type.Settings.FirstOrDefault(setting => setting.Name == parameter)?.Choices ?? [];

    public string? ChoicesNote(string parameter) => null;

    internal object Value(PythonSetting setting) => _values[setting.Name];

    internal InputPort Input(string name) => _inputs[name];

    internal OutputPort Output(string name) => _outputs[name];

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

    protected override unsafe void OnEvaluate(EvalContext context)
    {
        var library = Type.Library;

        lock (library.Gate)
        {
            library.ThrowIfStopped();

            _instance ??= new Instance(library, library.Create(Type.Key, Type.Name));

            using var run = new NativeRun(this, context);
            var host = run.Host();

            // What load() was last given. A change to any of it - typed, or
            // arriving on a wire - is a different model to load.
            var loadWith = Type.Settings
                .Where(setting => Type.Reloading.Contains(setting.Name))
                .Select(run.Resolve)
                .ToArray();

            if (_loadedWith is null || !loadWith.SequenceEqual(_loadedWith))
            {
                run.SettingsOnly = true;
                library.Load(_instance.Pointer, &host, Type.Name);
                run.SettingsOnly = false;
                _loadedWith = loadWith;
            }

            try
            {
                library.Run(_instance.Pointer, &host, Type.Name);
            }
            catch (NativeNodeException) when (context.CancellationToken.IsCancellationRequested)
            {
                // Stopping early is what it was asked to do.
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            run.Publish();
        }
    }

    /// <summary>
    /// The C++ object behind this node. A node has no moment of its own to
    /// let go, so the object is handed back when nothing refers to the node
    /// any more, and freed the next time its library is called.
    /// </summary>
    private sealed class Instance(NativeNodeLibrary library, IntPtr pointer)
    {
        public IntPtr Pointer { get; } = pointer;

        ~Instance() => library.Release(Pointer);
    }
}

/// <summary>
/// One call into a C++ node: what it may read, and what it has written.
/// </summary>
/// <remarks>
/// The library calls back through the functions here for each setting,
/// input and output, by name. Nothing thrown here may travel back through
/// the library's frames, so each one catches everything and answers -1,
/// leaving the reason for <c>last_error</c>; the library's side turns that
/// into its own exception, and so into the node's error.
/// </remarks>
internal sealed unsafe class NativeRun : IDisposable
{
    private readonly NativeNode _node;
    private readonly EvalContext _context;
    private readonly GCHandle _handle;

    private readonly List<IntPtr> _texts = [];
    private readonly List<IntPtr> _shapeMemory = [];
    private readonly Dictionary<string, IReadOnlyList<Shape>> _shapesIn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _written = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];

    private IntPtr _lastError;
    private (int Width, int Height) _frame;
    private (DepthType Depth, int Bits)? _first;

    public NativeRun(NativeNode node, EvalContext context)
    {
        _node = node;
        _context = context;
        _handle = GCHandle.Alloc(this);
    }

    /// <summary>True while load() runs: inputs read as not wired.</summary>
    public bool SettingsOnly { get; set; }

    private NativeNodeType Type => _node.Type;

    /// <summary>The table of functions the library is handed.</summary>
    public SiriusHost Host() => new()
    {
        Size = sizeof(SiriusHost),
        Context = (void*)GCHandle.ToIntPtr(_handle),
        Has = &HasCallback,
        GetNumber = &GetNumberCallback,
        GetInteger = &GetIntegerCallback,
        GetBool = &GetBoolCallback,
        GetText = &GetTextCallback,
        GetImage = &GetImageCallback,
        GetShapeCount = &GetShapeCountCallback,
        GetShape = &GetShapeCallback,
        SetNumber = &SetNumberCallback,
        SetInteger = &SetIntegerCallback,
        SetBool = &SetBoolCallback,
        SetText = &SetTextCallback,
        SetImage = &SetImageCallback,
        AddShape = &AddShapeCallback,
        Warn = &WarnCallback,
        Cancelled = &CancelledCallback,
        LastError = &LastErrorCallback,
        LibraryFolder = &LibraryFolderCallback,
    };

    /// <summary>A setting's value for this run: what is typed, or what arrives on its wire.</summary>
    public object Resolve(PythonSetting setting)
    {
        var value = _node.Value(setting);

        if (!setting.Connectable)
            return value;

        return value switch
        {
            double number => _context.Param(setting.Name, number),
            int integer => _context.Param(setting.Name, integer),
            bool flag => _context.Param(setting.Name, flag),
            string text => _context.Param(setting.Name, text),
            _ => value,
        };
    }

    /// <summary>Hands the node's outputs to the graph, once the run has succeeded.</summary>
    public void Publish()
    {
        // Shapes are in the coordinates of the image they were found in:
        // the first image in, or failing that the first image out.
        if (_frame == default && _written.Values.OfType<ImageData>().FirstOrDefault() is { } made)
            _frame = (made.Width, made.Height);

        foreach (var port in Type.Outputs)
        {
            var output = _node.Output(port.Name);

            if (port.Kind == "shapes")
            {
                var shapes = _written.Remove(port.Name, out var added) ? (List<Shape>)added : [];
                _context.Set(output, new ShapeSet(shapes, _frame.Width, _frame.Height));
            }
            else if (_written.Remove(port.Name, out var value))
            {
                _context.Set(output, value);
            }
        }

        if (_warnings.Count > 0)
            _context.Warn(string.Join(" ", _warnings));
    }

    public void Dispose()
    {
        // Whatever was written and not published - the run failed - is nobody's now.
        foreach (var image in _written.Values.OfType<ImageData>())
            image.Dispose();

        _written.Clear();

        foreach (var text in _texts)
            Marshal.FreeCoTaskMem(text);

        FreeShapeMemory();
        Marshal.FreeCoTaskMem(_lastError);
        _handle.Free();
    }

    // ---- reading ----------------------------------------------------------------

    private PythonSetting? SettingNamed(string name) => Type.Settings.FirstOrDefault(setting => setting.Name == name);

    private PythonPort? InputNamed(string name) => Type.Inputs.FirstOrDefault(port => port.Name == name);

    private PythonPort OutputNamed(string name, params string[] kinds)
    {
        if (Type.Outputs.FirstOrDefault(port => port.Name == name) is not { } port)
        {
            throw new NativeNodeException(
                $"It has no output called '{name}'. Its outputs: {Names(Type.Outputs.Select(output => output.Name))}.");
        }

        if (!kinds.Contains(port.Kind))
            throw new NativeNodeException($"'{name}' is {A(port.Kind)} output; set it with {Setter(port.Kind)}.");

        return port;
    }

    private NativeNodeException Unknown(string name) => new(
        $"It has no setting or input called '{name}'. Its settings: {Names(Type.Settings.Select(setting => setting.Name))}; " +
        $"its inputs: {Names(Type.Inputs.Select(port => port.Name))}.");

    private static string Names(IEnumerable<string> names) =>
        string.Join(", ", names) is { Length: > 0 } list ? list : "none";

    private static string A(string kind) => kind is "image" or "integer" ? $"an {kind}" : $"a {kind}";

    private static string Getter(string kind) => kind switch
    {
        "number" => "number()",
        "integer" => "integer()",
        "bool" => "boolean()",
        "image" => "image()",
        "shapes" => "shapes()",
        _ => "text()",
    };

    private static string Setter(string kind) => kind switch
    {
        "number" => "set_number",
        "integer" => "set_integer",
        "bool" => "set_bool",
        "image" => "set_image",
        "shapes" => "add_shape",
        _ => "set_text",
    };

    /// <summary>
    /// The value behind a name: a setting's, or what is wired to an input -
    /// null for an input with nothing wired.
    /// </summary>
    private object? Read(string name, out string kind)
    {
        if (SettingNamed(name) is { } setting)
        {
            kind = setting.Kind is "choice" or "file" ? "text" : setting.Kind;
            return Resolve(setting);
        }

        if (InputNamed(name) is { } port)
        {
            kind = port.Kind;
            return SettingsOnly ? null : _context.Get<object>(_node.Input(name));
        }

        throw Unknown(name);
    }

    private NativeNodeException Mismatch(string name, string kind) =>
        new($"'{name}' is {A(kind)}; read it with {Getter(kind)}.");

    private int Has(string name) => Read(name, out _) is null ? 0 : 1;

    private int GetNumber(string name, double* value)
    {
        var read = Read(name, out var kind);

        if (kind is not ("number" or "integer"))
            throw Mismatch(name, kind);

        if (read is null)
            return 0;

        *value = Convert.ToDouble(read, CultureInfo.InvariantCulture);
        return 1;
    }

    private int GetInteger(string name, long* value)
    {
        var read = Read(name, out var kind);

        if (kind != "integer")
            throw Mismatch(name, kind);

        if (read is null)
            return 0;

        *value = (long)Math.Round(Convert.ToDouble(read, CultureInfo.InvariantCulture));
        return 1;
    }

    private int GetBool(string name, int* value)
    {
        var read = Read(name, out var kind);

        if (kind != "bool")
            throw Mismatch(name, kind);

        if (read is null)
            return 0;

        *value = Convert.ToBoolean(read, CultureInfo.InvariantCulture) ? 1 : 0;
        return 1;
    }

    private int GetText(string name, byte** value)
    {
        var read = Read(name, out var kind);

        if (kind != "text")
            throw Mismatch(name, kind);

        if (read is null)
            return 0;

        *value = Keep(Convert.ToString(read, CultureInfo.InvariantCulture) ?? "");
        return 1;
    }

    private int GetImage(string name, SiriusImage* image)
    {
        var read = Read(name, out var kind);

        if (kind != "image")
            throw Mismatch(name, kind);

        if (read is not ImageData data)
            return 0;

        var mat = data.Mat;

        if ((int)mat.Depth is < 0 or > 6)
        {
            throw new NativeNodeException(
                $"'{name}' carries {mat.Depth} samples, a type a C++ node cannot be given. Convert it first.");
        }

        // As it is, where it is: the rows of a slice of a larger image are
        // not adjacent, and the stride says so.
        *image = new SiriusImage
        {
            Width = mat.Width,
            Height = mat.Height,
            Channels = mat.NumberOfChannels,
            Pixels = (int)mat.Depth,
            Layout = (int)PictureBridge.LayoutOf(data.Space, mat.NumberOfChannels),
            Bits = data.Bits,
            Stride = mat.Step,
            Data = (void*)mat.DataPointer,
        };

        _first ??= (data.Depth, data.Bits);

        if (_frame == default)
            _frame = (data.Width, data.Height);

        return 1;
    }

    private IReadOnlyList<Shape>? ShapesIn(string name)
    {
        if (_shapesIn.TryGetValue(name, out var known))
            return known;

        var read = Read(name, out var kind);

        if (kind != "shapes")
            throw Mismatch(name, kind);

        if (read is not ShapeSet set)
            return null;

        // Only the kinds the interface has a number for.
        var shapes = set.Shapes.Where(shape => KindOf(shape) >= 0).ToArray();
        _shapesIn[name] = shapes;
        return shapes;
    }

    private int GetShapeCount(string name, int* count)
    {
        if (ShapesIn(name) is not { } shapes)
            return 0;

        *count = shapes.Count;
        return 1;
    }

    private int GetShape(string name, int index, SiriusShape* raw)
    {
        if (ShapesIn(name) is not { } shapes)
            return 0;

        if (index < 0 || index >= shapes.Count)
            throw new NativeNodeException($"'{name}' has {shapes.Count} shapes; there is no shape {index}.");

        // The last shape's text and points are no longer promised.
        FreeShapeMemory();

        var shape = shapes[index];
        *raw = default;
        raw->Kind = KindOf(shape);
        raw->Closed = 1;
        raw->Colour = shape.Colour ?? 0;

        var v = raw->V;

        switch (shape)
        {
            case PointShape point:
                (v[0], v[1]) = (point.At.X, point.At.Y);
                break;

            case LineShape line:
                (v[0], v[1], v[2], v[3]) = (line.Start.X, line.Start.Y, line.End.X, line.End.Y);
                break;

            case RectShape rect:
                (v[0], v[1], v[2], v[3]) = (rect.Rect.X, rect.Rect.Y, rect.Rect.Width, rect.Rect.Height);
                break;

            case OrientedRectShape box:
                (v[0], v[1], v[2], v[3], v[4]) = (box.Centre.X, box.Centre.Y, box.Width, box.Height, box.AngleDegrees);
                break;

            case CircleShape circle:
                (v[0], v[1], v[2]) = (circle.Centre.X, circle.Centre.Y, circle.Radius);
                break;

            case EllipseShape ellipse:
                (v[0], v[1], v[2], v[3], v[4]) =
                    (ellipse.Centre.X, ellipse.Centre.Y, ellipse.MajorAxis, ellipse.MinorAxis, ellipse.Angle);
                break;

            case PolygonShape polygon:
                var points = (double*)Allocate(Math.Max(1, polygon.Points.Count) * 2 * sizeof(double));

                for (var i = 0; i < polygon.Points.Count; i++)
                    (points[2 * i], points[(2 * i) + 1]) = (polygon.Points[i].X, polygon.Points[i].Y);

                raw->Points = points;
                raw->PointCount = polygon.Points.Count;
                raw->Closed = polygon.Closed ? 1 : 0;
                break;
        }

        if (shape.Label is not null)
        {
            var label = Marshal.StringToCoTaskMemUTF8(shape.Label);
            _shapeMemory.Add(label);
            raw->Label = (byte*)label;
        }

        var metrics = shape.Metrics.Where(metric => double.IsFinite(metric.Value)).ToArray();

        if (metrics.Length > 0)
        {
            var list = (SiriusMetric*)Allocate(metrics.Length * sizeof(SiriusMetric));

            for (var i = 0; i < metrics.Length; i++)
            {
                var metricName = Marshal.StringToCoTaskMemUTF8(metrics[i].Key);
                _shapeMemory.Add(metricName);
                list[i] = new SiriusMetric { Name = (byte*)metricName, Value = metrics[i].Value };
            }

            raw->Metrics = list;
            raw->MetricCount = metrics.Length;
        }

        return 1;
    }

    private static int KindOf(Shape shape) => shape switch
    {
        PointShape => 0,
        LineShape => 1,
        RectShape => 2,
        OrientedRectShape => 3,
        CircleShape => 4,
        EllipseShape => 5,
        PolygonShape => 6,
        _ => -1,
    };

    // ---- writing ----------------------------------------------------------------

    private void Write(string name, object value)
    {
        if (_written.TryGetValue(name, out var before) && before is IDisposable replaced)
            replaced.Dispose();

        _written[name] = value;
    }

    private int SetNumber(string name, double value)
    {
        OutputNamed(name, "number");
        Write(name, value);
        return 1;
    }

    private int SetInteger(string name, long value)
    {
        // A whole number is a number too.
        if (OutputNamed(name, "integer", "number").Kind == "number")
            Write(name, (double)value);
        else
            Write(name, (int)Math.Clamp(value, int.MinValue, int.MaxValue));

        return 1;
    }

    private int SetBool(string name, int value)
    {
        OutputNamed(name, "bool");
        Write(name, value != 0);
        return 1;
    }

    private int SetText(string name, byte* value)
    {
        OutputNamed(name, "text");
        Write(name, value == null ? "" : Marshal.PtrToStringUTF8((IntPtr)value) ?? "");
        return 1;
    }

    private int SetImage(string name, SiriusImage* image)
    {
        OutputNamed(name, "image");

        if (image == null || image->Data == null)
            throw new NativeNodeException($"'{name}' was given an image with no pixels.");

        var (width, height, channels) = (image->Width, image->Height, image->Channels);

        if (width < 1 || height < 1 || channels is < 1 or > 4)
        {
            throw new NativeNodeException(
                $"'{name}' was given a {width} x {height} image with {channels} channels, which cannot be made.");
        }

        if (image->Pixels is < 0 or > 6)
        {
            throw new NativeNodeException(
                $"'{name}' was given an image of sample type {image->Pixels}, which is not one of SIRIUS_U8 to SIRIUS_F64.");
        }

        var depth = (DepthType)image->Pixels;
        var sample = depth switch
        {
            DepthType.Cv8U or DepthType.Cv8S => 1,
            DepthType.Cv16U or DepthType.Cv16S => 2,
            DepthType.Cv64F => 8,
            _ => 4,
        };

        var rowBytes = (long)width * channels * sample;

        if (image->Stride < rowBytes)
        {
            throw new NativeNodeException(
                $"'{name}' was given an image whose stride, {image->Stride}, is less than one row of it, {rowBytes} bytes.");
        }

        var layout = Enum.IsDefined((ColourLayout)image->Layout) ? (ColourLayout)image->Layout : ColourLayout.Unknown;

        if (PictureBridge.LayoutOf(PictureBridge.SpaceOf(layout) ?? ColourSpace.Gray, channels) != layout)
            layout = ColourLayout.Unknown;

        // A result of the same type as the first image in keeps its bits
        // unless the node said otherwise, as the built-in tools keep theirs.
        int? bits = image->Bits > 0 ? image->Bits
            : _first is { } first && first.Depth == depth ? first.Bits
            : null;

        var library = Type.Library;
        var mat = new Mat(height, width, depth, channels);

        try
        {
            var source = (byte*)image->Data;
            var destination = (byte*)mat.DataPointer;
            var whole = mat.IsContinuous && mat.Step == rowBytes && image->Stride == rowBytes;

            // Through the library's own guard: a pointer that has gone
            // stale is then its error to report, not this process's end.
            var copied = whole
                ? library.Copy(destination, source, rowBytes * height)
                : CopyRows(library, destination, mat.Step, source, image->Stride, rowBytes, height);

            if (!copied)
            {
                throw new NativeNodeException(
                    $"'{name}' was given an image whose pixels cannot be read - freed already, or smaller than it says.");
            }

            Write(name, ImageData.Adopt(mat, PictureBridge.SpaceOf(layout), bits is 0 ? null : bits));
        }
        catch
        {
            mat.Dispose();
            throw;
        }

        return 1;
    }

    private static bool CopyRows(
        NativeNodeLibrary library, byte* destination, long destinationStride, byte* source, long sourceStride,
        long rowBytes, int height)
    {
        for (var y = 0; y < height; y++)
        {
            if (!library.Copy(destination + (y * destinationStride), source + (y * sourceStride), rowBytes))
                return false;
        }

        return true;
    }

    private int AddShape(string name, SiriusShape* raw)
    {
        OutputNamed(name, "shapes");

        if (raw == null)
            throw new NativeNodeException($"'{name}' was given no shape.");

        var v = raw->V;

        Shape shape = raw->Kind switch
        {
            0 => new PointShape(new Point2(v[0], v[1])),
            1 => new LineShape(new Point2(v[0], v[1]), new Point2(v[2], v[3])),
            2 => new RectShape(new Bounds(v[0], v[1], v[2], v[3])),
            3 => new OrientedRectShape(new Point2(v[0], v[1]), v[2], v[3], v[4]),
            4 => new CircleShape(new Point2(v[0], v[1]), v[2]),
            5 => new EllipseShape(new Point2(v[0], v[1]), v[2], v[3], v[4]),
            6 => new PolygonShape(Points(name, raw), raw->Closed != 0),
            var other => throw new NativeNodeException(
                $"'{name}' was given a shape of kind {other}, which is not one of SIRIUS_POINT to SIRIUS_POLYGON."),
        };

        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);

        if (raw->Metrics != null)
        {
            for (var i = 0; i < raw->MetricCount; i++)
            {
                if (raw->Metrics[i].Name != null && Marshal.PtrToStringUTF8((IntPtr)raw->Metrics[i].Name) is { Length: > 0 } metric)
                    metrics[metric] = raw->Metrics[i].Value;
            }
        }

        shape = shape with
        {
            Label = raw->Label == null ? null : Marshal.PtrToStringUTF8((IntPtr)raw->Label),
            Colour = raw->Colour == 0 ? null : raw->Colour,
            Metrics = metrics,
        };

        if (!_written.TryGetValue(name, out var list))
            _written[name] = list = new List<Shape>();

        ((List<Shape>)list).Add(shape);
        return 1;
    }

    private Point2[] Points(string name, SiriusShape* raw)
    {
        if (raw->PointCount < 0 || (raw->PointCount > 0 && raw->Points == null))
            throw new NativeNodeException($"'{name}' was given a polygon whose points are missing.");

        var values = new double[raw->PointCount * 2];

        fixed (double* destination = values)
        {
            if (values.Length > 0 && !Type.Library.Copy(destination, raw->Points, (long)values.Length * sizeof(double)))
                throw new NativeNodeException($"'{name}' was given a polygon whose points cannot be read.");
        }

        var points = new Point2[raw->PointCount];

        for (var i = 0; i < points.Length; i++)
            points[i] = new Point2(values[2 * i], values[(2 * i) + 1]);

        return points;
    }

    // ---- memory the library is lent ---------------------------------------------

    private byte* Keep(string text)
    {
        var memory = Marshal.StringToCoTaskMemUTF8(text);
        _texts.Add(memory);
        return (byte*)memory;
    }

    private void* Allocate(int bytes)
    {
        var memory = Marshal.AllocCoTaskMem(bytes);
        _shapeMemory.Add(memory);
        return (void*)memory;
    }

    private void FreeShapeMemory()
    {
        foreach (var memory in _shapeMemory)
            Marshal.FreeCoTaskMem(memory);

        _shapeMemory.Clear();
    }

    private int Fail(Exception ex)
    {
        Marshal.FreeCoTaskMem(_lastError);
        _lastError = Marshal.StringToCoTaskMemUTF8(ex is NativeNodeException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
        return -1;
    }

    // ---- what the library calls ---------------------------------------------------

    private static NativeRun Of(void* context) => (NativeRun)GCHandle.FromIntPtr((IntPtr)context).Target!;

    private static string Name(byte* name) => name == null ? "" : Marshal.PtrToStringUTF8((IntPtr)name) ?? "";

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HasCallback(void* context, byte* name)
    {
        var run = Of(context);
        try { return run.Has(Name(name)); }
        catch (Exception ex) { return run.Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetNumberCallback(void* context, byte* name, double* value)
    {
        var run = Of(context);
        try { return run.GetNumber(Name(name), value); }
        catch (Exception ex) { return run.Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetIntegerCallback(void* context, byte* name, long* value)
    {
        var run = Of(context);
        try { return run.GetInteger(Name(name), value); }
        catch (Exception ex) { return run.Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetBoolCallback(void* context, byte* name, int* value)
    {
        var run = Of(context);
        try { return run.GetBool(Name(name), value); }
        catch (Exception ex) { return run.Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetTextCallback(void* context, byte* name, byte** value)
    {
        var run = Of(context);
        try { return run.GetText(Name(name), value); }
        catch (Exception ex) { return run.Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetImageCallback(void* context, byte* name, SiriusImage* image)
    {
        var run = Of(context);
        try { return run.GetImage(Name(name), image); }
        catch (Exception ex) { return run.Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetShapeCountCallback(void* context, byte* name, int* count)
    {
        var run = Of(context);
        try { return run.GetShapeCount(Name(name), count); }
        catch (Exception ex) { return run.Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetShapeCallback(void* context, byte* name, int index, SiriusShape* shape)
    {
        var run = Of(context);
        try { return run.GetShape(Name(name), index, shape); }
        catch (Exception ex) { return run.Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetNumberCallback(void* context, byte* name, double value)
    {
        var run = Of(context);
        try { return run.SetNumber(Name(name), value); }
        catch (Exception ex) { return run.Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetIntegerCallback(void* context, byte* name, long value)
    {
        var run = Of(context);
        try { return run.SetInteger(Name(name), value); }
        catch (Exception ex) { return run.Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetBoolCallback(void* context, byte* name, int value)
    {
        var run = Of(context);
        try { return run.SetBool(Name(name), value); }
        catch (Exception ex) { return run.Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetTextCallback(void* context, byte* name, byte* value)
    {
        var run = Of(context);
        try { return run.SetText(Name(name), value); }
        catch (Exception ex) { return run.Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetImageCallback(void* context, byte* name, SiriusImage* image)
    {
        var run = Of(context);
        try { return run.SetImage(Name(name), image); }
        catch (Exception ex) { return run.Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AddShapeCallback(void* context, byte* name, SiriusShape* shape)
    {
        var run = Of(context);
        try { return run.AddShape(Name(name), shape); }
        catch (Exception ex) { return run.Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WarnCallback(void* context, byte* message)
    {
        try
        {
            if (Name(message) is { Length: > 0 } text)
                Of(context)._warnings.Add(text);
        }
        catch (Exception)
        {
            // A warning that cannot be read is not worth a failure.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CancelledCallback(void* context)
    {
        try { return Of(context)._context.CancellationToken.IsCancellationRequested ? 1 : 0; }
        catch (Exception) { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte* LastErrorCallback(void* context)
    {
        try { return (byte*)Of(context)._lastError; }
        catch (Exception) { return null; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte* LibraryFolderCallback(void* context)
    {
        try { return Of(context).Type.Library.Folder; }
        catch (Exception) { return null; }
    }
}
