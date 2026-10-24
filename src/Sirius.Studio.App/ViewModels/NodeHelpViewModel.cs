using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;

namespace Sirius.Studio.App.ViewModels;

/// <summary>
/// What a tool is for, read off the tool itself.
/// </summary>
/// <remarks>
/// <para>
/// Built from the node's descriptor and its ports rather than from a
/// written page. A page has to be kept in step with the code and will not
/// be: a parameter renamed or a port added is a silent contradiction the
/// moment it ships. This cannot disagree with the node, because it is the
/// node.
/// </para>
/// <para>
/// It also means a node added later - including one from a plugin - is
/// documented the day it appears, without anybody writing anything beyond
/// the descriptions already on its attributes.
/// </para>
/// </remarks>
public sealed class NodeHelpViewModel : ObservableObject
{
    public NodeHelpViewModel(VisionNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        Node = node;
        Title = node.Descriptor.DisplayName;
        TypeKey = node.Descriptor.TypeKey;
        Category = Humanise(node.Descriptor.Category.ToString());

        Summary = string.IsNullOrWhiteSpace(node.Descriptor.Description)
            ? "No description yet."
            : node.Descriptor.Description!;

        Inputs = node.Inputs.Select(Describe).ToArray();
        Outputs = node.Outputs.Select(Describe).ToArray();
        Parameters = node.Descriptor.Parameters.Select(Describe).ToArray();
    }

    public VisionNode Node { get; }

    public string Title { get; }

    public string TypeKey { get; }

    public string Category { get; }

    public string Summary { get; }

    public IReadOnlyList<PortHelp> Inputs { get; }

    public IReadOnlyList<PortHelp> Outputs { get; }

    public IReadOnlyList<ParameterHelp> Parameters { get; }

    public bool HasInputs => Inputs.Count > 0;

    public bool HasOutputs => Outputs.Count > 0;

    public bool HasParameters => Parameters.Count > 0;

    private static PortHelp Describe(Port port) => new(
        port.DisplayName,
        TypeNames.Friendly(port.DataType),
        port is InputPort { IsRequired: false } ? "optional" : string.Empty);

    private static ParameterHelp Describe(ParamDescriptor parameter) => new(
        parameter.DisplayName,
        TypeNames.Friendly(parameter.ValueType),
        RangeOf(parameter),
        parameter.Description ?? string.Empty);

    /// <summary>
    /// The bounds, where there are fixed ones. A bound read from another
    /// property depends on the data, so it is left out rather than shown
    /// as whatever it happens to be right now.
    /// </summary>
    private static string RangeOf(ParamDescriptor parameter)
    {
        var attribute = parameter.Attribute;

        if (attribute.MinSource is not null || attribute.MaxSource is not null)
            return "depends on the input";

        if (!attribute.HasMin && !attribute.HasMax)
            return parameter.ValueType.IsEnum
                ? string.Join(", ", Enum.GetNames(parameter.ValueType).Select(Humanise))
                : string.Empty;

        return string.Format(
            CultureInfo.CurrentCulture,
            "{0} to {1}",
            Trim(attribute.HasMin ? attribute.Min : 0),
            Trim(attribute.HasMax ? attribute.Max : 0));
    }

    private static string Trim(double value) =>
        value == Math.Floor(value)
            ? ((long)value).ToString(CultureInfo.CurrentCulture)
            : value.ToString("0.##", CultureInfo.CurrentCulture);

    /// <summary>"TextAndCodeReading" reads better as "Text and code reading".</summary>
    private static string Humanise(string value)
    {
        var spaced = string.Concat(value.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));

        return spaced.Replace(" and ", " and ", StringComparison.Ordinal);
    }
}

/// <summary>One port, as the help window shows it.</summary>
public sealed record PortHelp(string Name, string Type, string Note);

/// <summary>One parameter, as the help window shows it.</summary>
public sealed record ParameterHelp(string Name, string Type, string Range, string Description)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool HasRange => !string.IsNullOrWhiteSpace(Range);
}
