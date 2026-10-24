using System.Reflection;
using Sirius.Core.Graph;

namespace Sirius.Core.Nodes;

/// <summary>
/// One operator-editable parameter on a node type, with its UI hints.
/// </summary>
public sealed class ParamDescriptor
{
    private readonly PropertyInfo _property;

    internal ParamDescriptor(PropertyInfo property, ParamAttribute attribute)
    {
        _property = property;
        Attribute = attribute;
        Name = property.Name;
        DisplayName = attribute.DisplayName ?? Humanise(property.Name);
        ValueType = property.PropertyType;
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

    public object? GetValue(VisionNode node) => _property.GetValue(node);

    public void SetValue(VisionNode node, object? value) => _property.SetValue(node, value);

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

    public Type NodeType { get; }

    public string DisplayName { get; }

    public NodeCategory Category { get; }

    public string? Icon { get; }

    public string? Description { get; }

    /// <summary>Stable key written to saved graphs.</summary>
    public string TypeKey { get; }

    public IReadOnlyList<ParamDescriptor> Parameters { get; }

    /// <summary>
    /// False for abstract bases and helper types that lack
    /// <see cref="VisionNodeAttribute"/> - they must not appear in the palette.
    /// </summary>
    public bool IsInstantiable { get; }

    public VisionNode CreateInstance() =>
        (VisionNode)(Activator.CreateInstance(NodeType)
            ?? throw new InvalidOperationException($"Could not instantiate node type '{NodeType.FullName}'."));

    public override string ToString() => $"{Category}/{DisplayName}";
}
