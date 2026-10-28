using System.Reflection;
using System.Text.RegularExpressions;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Plugins;

namespace Sirius.Vision.Plugins;

/// <summary>
/// One custom node class, checked and described for the catalogue.
/// </summary>
/// <remarks>
/// Everything that can be wrong with a custom node is found here, when its
/// library loads, rather than when somebody places it on a canvas: a key
/// that would collide or cannot be saved, a setting of a type nothing can
/// edit, a port of a type nothing can wire, a constructor that throws. Each
/// becomes a sentence in the Custom Nodes window.
/// </remarks>
internal sealed partial class CustomNodeType
{
    /// <summary>
    /// What goes in front of every custom node's key in a saved recipe.
    /// </summary>
    /// <remarks>
    /// Built-in keys are written <c>group.name</c> - <c>basic.threshold</c> -
    /// which is also the natural way to write a plugin's. Without a prefix a
    /// plugin's <c>measure.angle</c> could be claimed by a built-in node in a
    /// later release, and the recipes using it would open with the wrong node.
    /// No built-in key contains a slash, so none ever can collide.
    /// </remarks>
    public const string KeyPrefix = "custom/";

    public const string DefaultIcon = "ph_custom_node";

    private CustomNodeType(
        Type pluginType, CustomNodeAttribute attribute, IReadOnlyList<DeclaredSetting> settings, string origin)
    {
        PluginType = pluginType;
        Attribute = attribute;
        Settings = settings;

        Descriptor = NodeDescriptor.Create(
            pluginType,
            attribute.DisplayName,
            Enum.Parse<NodeCategory>(attribute.Group.ToString()),
            string.IsNullOrWhiteSpace(attribute.Icon) ? DefaultIcon : attribute.Icon,
            attribute.Description,
            KeyPrefix + attribute.TypeKey,
            settings.Select(Describe).ToArray(),
            origin,
            () => new CustomNodeHost(this));
    }

    public Type PluginType { get; }

    public CustomNodeAttribute Attribute { get; }

    public IReadOnlyList<DeclaredSetting> Settings { get; }

    public NodeDescriptor Descriptor { get; }

    /// <summary>
    /// Checks a class and describes it, or explains why it cannot be added.
    /// </summary>
    /// <param name="origin">The library it came from, as the operator should see it.</param>
    public static CustomNodeType? TryCreate(Type type, string origin, ICollection<string> problems)
    {
        var attribute = type.GetCustomAttribute<CustomNodeAttribute>();
        if (attribute is null)
        {
            problems.Add($"{type.Name} implements ICustomNode but has no [CustomNode] attribute, so it was not added.");
            return null;
        }

        var found = new List<string>();

        if (string.IsNullOrWhiteSpace(attribute.DisplayName))
            found.Add("needs a display name.");

        if (KeyProblem(attribute.TypeKey) is { } keyProblem)
            found.Add(keyProblem);

        if (type.GetConstructor(Type.EmptyTypes) is not { IsPublic: true })
            found.Add("needs a public constructor taking no arguments.");

        var settings = SettingScanner.Scan(type, found);

        if (found.Count == 0)
            CheckDeclaration(type, settings, found);

        if (found.Count > 0)
        {
            var name = string.IsNullOrWhiteSpace(attribute.DisplayName)
                ? type.Name
                : $"{attribute.DisplayName} ({type.Name})";

            foreach (var problem in found)
                problems.Add($"{name}: {problem}");

            return null;
        }

        return new CustomNodeType(type, attribute, settings, origin);
    }

    /// <summary>What is wrong with a key, or null when it will do.</summary>
    internal static string? KeyProblem(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "needs a TypeKey, such as \"acme.flatfield\". It is what saved recipes use to " +
                   "find the node, so it must be set and never changed.";
        }

        return KeyPattern().IsMatch(key)
            ? null
            : $"has the TypeKey \"{key}\", which should be written vendor.name - letters, digits, " +
              "'-' and '_', with at least one dot, like \"acme.flatfield\".";
    }

    /// <summary>
    /// Makes one and declares its ports, as placing it on a canvas will -
    /// so a constructor that throws or a port nothing can carry is found
    /// now, once, rather than every time somebody uses it.
    /// </summary>
    private static void CheckDeclaration(Type type, IReadOnlyList<DeclaredSetting> settings, ICollection<string> problems)
    {
        ShapeRecorder shape;

        try
        {
            var instance = (ICustomNode)Activator.CreateInstance(type)!;
            shape = new ShapeRecorder();
            instance.Declare(shape);
            shape.Close();
        }
        catch (Exception ex)
        {
            var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            problems.Add($"could not be created and declared: {cause.Message}");
            return;
        }

        // A connectable setting becomes an input of the same name.
        foreach (var setting in settings.Where(setting => setting.Attribute.Connectable))
        {
            if (shape.Inputs.Any(port => port.Name == setting.Name))
                problems.Add($"has an input and a connectable setting both called '{setting.Name}'.");
        }
    }

    private static ParamDescriptor Describe(DeclaredSetting setting)
    {
        var source = setting.Attribute;

        var attribute = new ParamAttribute(source.DisplayName)
        {
            Description = source.Description,
            Group = source.Group,
            Min = source.Min,
            Max = source.Max,
            Step = source.Step,
            Editor = EditorFor(setting),
            OddOnly = source.OddOnly,
            Logarithmic = source.Logarithmic,
            Filter = source.Filter,
            AllowsNoLimit = source.AllowsNoLimit,
            Connectable = source.Connectable,
        };

        return ParamDescriptor.Create(
            setting.Name,
            setting.Type,
            attribute,
            node => setting.Property.GetValue(((CustomNodeHost)node).Plugin),
            (node, value) => ((CustomNodeHost)node).Change(setting, value));
    }

    /// <summary>
    /// The editor a setting gets. A rectangle, ring or range left on
    /// <see cref="SettingEditor.Auto"/> gets its drawable editor rather than a
    /// text box, which is what anybody choosing the type meant.
    /// </summary>
    internal static ParamEditor EditorFor(DeclaredSetting setting) => setting.Attribute.Editor switch
    {
        SettingEditor.Auto when setting.Type == typeof(OrientedRect) => ParamEditor.Rect,
        SettingEditor.Auto when setting.Type == typeof(Ring) => ParamEditor.Ring,
        SettingEditor.Auto when setting.Type == typeof(ValueRange) => ParamEditor.Range,
        SettingEditor.Auto => ParamEditor.Auto,
        SettingEditor.Slider => ParamEditor.Slider,
        SettingEditor.TextBox => ParamEditor.TextBox,
        SettingEditor.ComboBox => ParamEditor.ComboBox,
        SettingEditor.CheckBox => ParamEditor.CheckBox,
        SettingEditor.FilePath => ParamEditor.FilePath,
        SettingEditor.Colour => ParamEditor.Colour,
        SettingEditor.Rect => ParamEditor.Rect,
        SettingEditor.Ring => ParamEditor.Ring,
        SettingEditor.Range => ParamEditor.Range,
        _ => ParamEditor.Auto,
    };

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)+$")]
    private static partial Regex KeyPattern();
}

/// <summary>
/// The node a custom node lives inside.
/// </summary>
/// <remarks>
/// <para>
/// To the graph, the editor, the serialiser and undo this is an ordinary
/// node: its ports are real ports, its settings are parameters with
/// descriptors, and changing one announces itself the way every built-in
/// parameter does. The plugin it wraps sees none of that - only the
/// declarations it made and a <see cref="IRunContext"/> while it runs.
/// </para>
/// <para>
/// An input carrying a <see cref="Picture"/> is an <see cref="ImageData"/>
/// port here, so a custom node wires to the built-in ones with no adapter.
/// </para>
/// </remarks>
public sealed class CustomNodeHost : VisionNode
{
    private readonly ShapeRecorder _shape = new();
    private readonly Dictionary<object, Port> _ports = new(ReferenceEqualityComparer.Instance);

    internal CustomNodeHost(CustomNodeType type)
        : base(type.Descriptor)
    {
        CustomType = type;
        Plugin = (ICustomNode)Activator.CreateInstance(type.PluginType)!;
        Plugin.Declare(_shape);
        _shape.Close();

        foreach (var port in _shape.Ports)
        {
            var dataType = port.Type == typeof(Picture) ? typeof(ImageData) : port.Type;

            _ports[port.Handle] = port.IsInput
                ? AddInput(dataType, port.Name, port.Label, port.IsRequired)
                : AddOutput(dataType, port.Name, port.Label);
        }
    }

    /// <summary>The plugin object this node runs.</summary>
    public ICustomNode Plugin { get; }

    internal CustomNodeType CustomType { get; }

    internal InputPort InputFor(object handle) =>
        _ports.TryGetValue(handle, out var port) && port is InputPort input
            ? input
            : throw new ArgumentException("That port is not an input of this node.", nameof(handle));

    internal OutputPort OutputFor(object handle) =>
        _ports.TryGetValue(handle, out var port) && port is OutputPort output
            ? output
            : throw new ArgumentException("That port is not an output of this node.", nameof(handle));

    /// <summary>
    /// Sets a setting, announcing it the way a built-in parameter does - a
    /// change before and after, then an invalidation - so the properties
    /// panel, undo and the scheduler all see it.
    /// </summary>
    internal void Change(DeclaredSetting setting, object? value)
    {
        if (value is not null && !setting.Type.IsInstanceOfType(value))
        {
            value = Values.TryConvert(value, setting.Type, out var converted)
                ? converted
                : throw new ArgumentException(
                    $"'{setting.Name}' is {setting.Type.Name}; a {value.GetType().Name} cannot be stored in it.");
        }

        if (Equals(setting.Property.GetValue(Plugin), value))
            return;

        OnPropertyChanging(setting.Name);
        setting.Property.SetValue(Plugin, value);
        OnPropertyChanged(setting.Name);
        Invalidate();
    }

    protected override void OnEvaluate(EvalContext context)
    {
        var run = new HostRunContext(this, context);

        try
        {
            Plugin.Run(run);
        }
        finally
        {
            run.End();
        }
    }
}

/// <summary>
/// What a custom node sees while it runs: its inputs as pictures and
/// values, and somewhere to put its outputs.
/// </summary>
internal sealed class HostRunContext : IRunContext
{
    private readonly CustomNodeHost _host;
    private readonly EvalContext _context;
    private readonly List<Picture> _views = [];
    private readonly List<IDisposable> _temporaries = [];
    private readonly Dictionary<InputPort, Picture> _viewsByPort = [];
    private readonly HashSet<object> _borrowed = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _resolved = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];

    public HostRunContext(CustomNodeHost host, EvalContext context)
    {
        _host = host;
        _context = context;
    }

    public CancellationToken Cancellation => _context.CancellationToken;

    public T? Read<T>(InPort<T> port) where T : class
    {
        var input = _host.InputFor(port);
        var raw = _context.Get<object>(input);

        if (raw is null)
            return null;

        if (typeof(T) == typeof(Picture))
            return raw is ImageData image ? (T)(object)ViewOf(input, image) : null;

        _borrowed.Add(raw);
        return Values.TryConvert(raw, typeof(T), out var converted) ? (T?)converted : null;
    }

    public T? ReadValue<T>(InPort<T> port) where T : struct
    {
        var raw = _context.Get<object>(_host.InputFor(port));
        return Values.TryConvert(raw, typeof(T), out var converted) ? (T)converted! : null;
    }

    public bool IsWired<T>(InPort<T> port) => _host.InputFor(port).IsConnected;

    public T Resolve<T>(string settingName, T current)
    {
        var setting = _host.CustomType.Settings.FirstOrDefault(candidate => candidate.Name == settingName);
        if (setting is null || !setting.Attribute.Connectable)
        {
            throw new ArgumentException(
                $"'{settingName}' is not a connectable setting of {_host.Descriptor.DisplayName}.",
                nameof(settingName));
        }

        _resolved.Add(settingName);
        return _context.Param(settingName, current);
    }

    public void Publish<T>(OutPort<T> port, T value)
    {
        var output = _host.OutputFor(port);

        switch (value)
        {
            // An input handed straight back: pass the original through
            // rather than copying a frame nothing changed.
            case Picture { Source: ImageData source }:
                _context.Forward(output, source);
                break;

            case Picture picture:
                _context.Set(output, PictureBridge.ToImage(picture));
                break;

            case not null when _borrowed.Contains(value):
                _context.Forward(output, value);
                break;

            default:
                _context.Set(output, value);
                break;
        }
    }

    public void Warn(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            _warnings.Add(message);
    }

    /// <summary>
    /// Ends the run: views stop working, copies are released, and anything
    /// worth warning about is reported.
    /// </summary>
    public void End()
    {
        foreach (var view in _views)
            view.Expire();

        foreach (var temporary in _temporaries)
            temporary.Dispose();

        foreach (var setting in _host.CustomType.Settings)
        {
            if (setting.Attribute.Connectable &&
                _context.IsDriven(setting.Name) &&
                !_resolved.Contains(setting.Name))
            {
                _warnings.Add(Sirius.Plugins.Testing.TestRun.Unresolved(setting.Name));
            }
        }

        // The node shows one message, so several become one line.
        if (_warnings.Count > 0)
            _context.Warn(string.Join(" ", _warnings.Distinct()));
    }

    /// <summary>One view per input per run, however many times it is read.</summary>
    private Picture ViewOf(InputPort input, ImageData image)
    {
        if (_viewsByPort.TryGetValue(input, out var existing))
            return existing;

        var view = PictureBridge.View(image, _temporaries);
        _views.Add(view);
        _viewsByPort[input] = view;
        return view;
    }
}
