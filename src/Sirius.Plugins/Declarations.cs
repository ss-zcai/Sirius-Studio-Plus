using System.Reflection;

namespace Sirius.Plugins;

/// <summary>A port as a node declared it.</summary>
internal sealed record DeclaredPort(
    string Name, string Label, Type Type, bool IsInput, bool IsRequired, object Handle);

/// <summary>A setting as a node declared it.</summary>
internal sealed record DeclaredSetting(PropertyInfo Property, SettingAttribute Attribute)
{
    public string Name => Property.Name;

    public Type Type => Property.PropertyType;
}

/// <summary>
/// Records what <see cref="ICustomNode.Declare"/> asks for, refusing what a
/// port cannot be.
/// </summary>
/// <remarks>
/// Shared by the application and by <see cref="Testing.TestRun"/>, so a
/// declaration the application would refuse fails in a plugin's own unit
/// tests too, rather than first being seen on somebody else's machine.
/// </remarks>
internal sealed class ShapeRecorder : INodeShape
{
    private readonly List<DeclaredPort> _ports = [];
    private bool _closed;

    public IReadOnlyList<DeclaredPort> Ports => _ports;

    public IEnumerable<DeclaredPort> Inputs => _ports.Where(port => port.IsInput);

    public IEnumerable<DeclaredPort> Outputs => _ports.Where(port => !port.IsInput);

    public InPort<T> Input<T>(string name, string? label = null, bool required = true)
    {
        Check(typeof(T), name, isInput: true);

        var handle = new InPort<T>(name, Label(name, label), required);
        _ports.Add(new DeclaredPort(name, handle.Label, typeof(T), true, required, handle));
        return handle;
    }

    public OutPort<T> Output<T>(string name, string? label = null)
    {
        Check(typeof(T), name, isInput: false);

        var handle = new OutPort<T>(name, Label(name, label));
        _ports.Add(new DeclaredPort(name, handle.Label, typeof(T), false, false, handle));
        return handle;
    }

    /// <summary>
    /// Ends declaration. A port is part of what a recipe saves, so one added
    /// later - halfway through a run, say - would be a socket the canvas
    /// never drew and a saved file could not reconnect.
    /// </summary>
    public void Close() => _closed = true;

    /// <summary>The declaration for a handle, or null when this node did not make it.</summary>
    public DeclaredPort? Find(object handle) =>
        _ports.FirstOrDefault(port => ReferenceEquals(port.Handle, handle));

    private void Check(Type type, string name, bool isInput)
    {
        if (_closed)
            throw new InvalidOperationException("Ports can only be declared inside Declare.");

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A port needs a name.", nameof(name));

        if (!CustomNodeTypes.IsPortType(type))
        {
            throw new NotSupportedException(
                $"Port '{name}' is declared as {type.Name}, which a port cannot carry. " +
                $"A port carries one of: {CustomNodeTypes.Describe(CustomNodeTypes.PortTypes)}.");
        }

        // An input and an output may share a name - "Shapes" in and out is
        // common - because a connection says which end it is on.
        if (_ports.Any(port => port.IsInput == isInput && port.Name == name))
        {
            throw new InvalidOperationException(
                $"There are two {(isInput ? "inputs" : "outputs")} called '{name}'.");
        }
    }

    private static string Label(string name, string? label) =>
        string.IsNullOrWhiteSpace(label) ? name : label;
}

/// <summary>Finds a node class's settings, and what is wrong with them.</summary>
internal static class SettingScanner
{
    public static IReadOnlyList<DeclaredSetting> Scan(Type nodeType, ICollection<string> problems)
    {
        var settings = new List<DeclaredSetting>();

        foreach (var property in nodeType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attribute = property.GetCustomAttribute<SettingAttribute>();
            if (attribute is null)
                continue;

            var problem = ProblemWith(property, attribute);
            if (problem is null)
                settings.Add(new DeclaredSetting(property, attribute));
            else
                problems.Add($"Setting '{property.Name}' {problem}");
        }

        return settings;
    }

    private static string? ProblemWith(PropertyInfo property, SettingAttribute attribute)
    {
        if (property.GetGetMethod() is null || property.GetSetMethod() is null)
            return "needs a public get and set.";

        if (property.GetIndexParameters().Length > 0)
            return "is an indexer, which cannot be a setting.";

        var type = property.PropertyType;

        if (Nullable.GetUnderlyingType(type) is not null)
            return $"is nullable ({Nullable.GetUnderlyingType(type)!.Name}?); give it a default instead.";

        if (!CustomNodeTypes.IsSettingType(type))
        {
            return $"is {type.Name}, which is not a setting type. Use an enum or one of: " +
                   $"{CustomNodeTypes.Describe(CustomNodeTypes.SettingTypes)}.";
        }

        if (attribute.Connectable && type.IsEnum)
            return "is an enum, which cannot be connectable - no node produces one.";

        return EditorProblem(type, attribute.Editor);
    }

    private static string? EditorProblem(Type type, SettingEditor editor)
    {
        var fits = editor switch
        {
            SettingEditor.Auto => true,
            SettingEditor.Slider => CustomNodeTypes.IsNumeric(type),
            SettingEditor.TextBox => CustomNodeTypes.IsNumeric(type)
                                     || type == typeof(string)
                                     || type == typeof(Core.Shapes.Point2),
            SettingEditor.ComboBox => type.IsEnum,
            SettingEditor.CheckBox => type == typeof(bool),
            SettingEditor.FilePath or SettingEditor.Colour => type == typeof(string),
            SettingEditor.Rect => type == typeof(Core.Nodes.OrientedRect),
            SettingEditor.Ring => type == typeof(Core.Nodes.Ring),
            SettingEditor.Range => type == typeof(Core.Nodes.ValueRange),
            _ => false,
        };

        return fits ? null : $"is {type.Name}, which the {editor} editor cannot edit.";
    }
}
