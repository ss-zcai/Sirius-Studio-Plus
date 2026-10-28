using System.Reflection;
using Sirius.Core.Graph;

namespace Sirius.Core.Nodes;

/// <summary>
/// One operator-editable parameter on a node type, with its UI hints.
/// </summary>
public sealed class ParamDescriptor
{
    private readonly Func<VisionNode, object?> _get;
    private readonly Action<VisionNode, object?> _set;

    internal ParamDescriptor(PropertyInfo property, ParamAttribute attribute)
        : this(property.Name, property.PropertyType, attribute, property.GetValue, property.SetValue)
    {
    }

    private ParamDescriptor(
        string name,
        Type valueType,
        ParamAttribute attribute,
        Func<VisionNode, object?> get,
        Action<VisionNode, object?> set)
    {
        _get = get;
        _set = set;
        Attribute = attribute;
        Name = name;
        DisplayName = attribute.DisplayName ?? Humanise(name);
        ValueType = valueType;
    }

    /// <summary>
    /// A parameter that is not a property of the node class.
    /// </summary>
    /// <remarks>
    /// For a node that stands in for something else - a custom node's
    /// settings live on the plugin object it wraps, not on the wrapper, so
    /// there is no property to reflect on. Everything that reads or writes
    /// a parameter goes through <see cref="GetValue"/> and
    /// <see cref="SetValue"/>, which is what lets this work: the editor, the
    /// serialiser and undo never need to know where the value lives.
    /// </remarks>
    public static ParamDescriptor Create(
        string name,
        Type valueType,
        ParamAttribute attribute,
        Func<VisionNode, object?> get,
        Action<VisionNode, object?> set)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(valueType);
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        return new ParamDescriptor(name, valueType, attribute, get, set);
    }

    public string Name { get; }

    public string DisplayName { get; }

    public Type ValueType { get; }

    public ParamAttribute Attribute { get; }

    public string? Group => Attribute.Group;

    public string? Description => Attribute.Description;

    /// <summary>
    /// Resolved editor kind: the explicit hint if given, otherwise inferred
    /// from the property type.
    /// </summary>
    public ParamEditor Editor
    {
        get
        {
            if (Attribute.Editor != ParamEditor.Auto)
                return Attribute.Editor;

            var type = Nullable.GetUnderlyingType(ValueType) ?? ValueType;

            if (type == typeof(bool))
                return ParamEditor.CheckBox;
            if (type.IsEnum)
                return ParamEditor.ComboBox;
            if (IsNumeric(type))
                return Attribute.HasMin && Attribute.HasMax ? ParamEditor.Slider : ParamEditor.TextBox;

            return ParamEditor.TextBox;
        }
    }

    public object? GetValue(VisionNode node) => _get(node);

    public void SetValue(VisionNode node, object? value) => _set(node, value);

    internal static bool IsNumeric(Type type) =>
        type == typeof(byte) || type == typeof(sbyte) ||
        type == typeof(short) || type == typeof(ushort) ||
        type == typeof(int) || type == typeof(uint) ||
        type == typeof(long) || type == typeof(ulong) ||
        type == typeof(float) || type == typeof(double) || type == typeof(decimal);

    /// <summary>Turns "BlockSize" into "Block Size" for display.</summary>
    private static string Humanise(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
                builder.Append(' ');
            builder.Append(c);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Cached static metadata for a node type: how it appears in the palette and
/// what parameters the inspector should show.
/// </summary>
public sealed class NodeDescriptor
{
    private readonly Func<VisionNode>? _factory;

    internal NodeDescriptor(Type nodeType, VisionNodeAttribute? attribute, IReadOnlyList<ParamDescriptor> parameters)
    {
        NodeType = nodeType;
        DisplayName = attribute?.DisplayName ?? nodeType.Name;
        Category = attribute?.Category ?? NodeCategory.Basic;
        Icon = attribute?.Icon;
        Description = attribute?.Description;
        TypeKey = attribute?.TypeKey ?? nodeType.Name;
        Parameters = parameters;
        IsInstantiable = attribute is not null && !nodeType.IsAbstract;
    }

    private NodeDescriptor(
        Type nodeType,
        string displayName,
        NodeCategory category,
        string? icon,
        string? description,
        string typeKey,
        IReadOnlyList<ParamDescriptor> parameters,
        string? origin,
        Func<VisionNode> factory)
    {
        NodeType = nodeType;
        DisplayName = displayName;
        Category = category;
        Icon = icon;
        Description = description;
        TypeKey = typeKey;
        Parameters = parameters;
        Origin = origin;
        IsInstantiable = true;
        _factory = factory;
    }

    /// <summary>
    /// A node type that is not a <see cref="VisionNode"/> subclass of its own.
    /// </summary>
    /// <remarks>
    /// One wrapper class stands in for every custom node, so the class
    /// cannot be what tells two of them apart: the name, the parameters and
    /// the key are each custom node's own. <paramref name="factory"/> is
    /// what makes one, since there is no constructor of the right shape to
    /// find by reflection.
    /// </remarks>
    /// <param name="nodeType">What the node really is, for messages and help.</param>
    /// <param name="origin">Where it came from, for the operator - a library name and version.</param>
    public static NodeDescriptor Create(
        Type nodeType,
        string displayName,
        NodeCategory category,
        string? icon,
        string? description,
        string typeKey,
        IReadOnlyList<ParamDescriptor> parameters,
        string? origin,
        Func<VisionNode> factory)
    {
        ArgumentNullException.ThrowIfNull(nodeType);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeKey);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(factory);

        return new NodeDescriptor(
            nodeType, displayName, category, icon, description, typeKey, parameters, origin, factory);
    }

    public Type NodeType { get; }

    public string DisplayName { get; }

    public NodeCategory Category { get; }

    public string? Icon { get; }

    public string? Description { get; }

    /// <summary>Stable key written to saved graphs.</summary>
    public string TypeKey { get; }

    public IReadOnlyList<ParamDescriptor> Parameters { get; }

    /// <summary>
    /// Which library supplied this node, or null for the built-in ones.
    /// </summary>
    public string? Origin { get; }

    /// <summary>
    /// False for abstract bases and helper types that lack
    /// <see cref="VisionNodeAttribute"/> - they must not appear in the palette.
    /// </summary>
    public bool IsInstantiable { get; }

    public VisionNode CreateInstance() =>
        _factory?.Invoke()
        ?? (VisionNode)(Activator.CreateInstance(NodeType)
            ?? throw new InvalidOperationException($"Could not instantiate node type '{NodeType.FullName}'."));

    public override string ToString() => $"{Category}/{DisplayName}";
}
