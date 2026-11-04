using System.Globalization;

namespace NodeGraphKit.Model;

/// <summary>Which editor the properties panel shows for a parameter.</summary>
public enum ParameterKind
{
    /// <summary>A double. A slider and a box when it has a minimum and maximum, a box otherwise.</summary>
    Number,

    /// <summary>An int. Drawn as <see cref="Number"/>, snapped to whole steps.</summary>
    Integer,

    /// <summary>A string, in a text box.</summary>
    Text,

    /// <summary>A bool, as a check box.</summary>
    Boolean,

    /// <summary>One string out of <see cref="ParameterDefinition.Options"/>, as a drop-down.</summary>
    Choice,
}

/// <summary>
/// A setting on a node, edited on the properties panel and saved with the graph.
/// </summary>
/// <remarks>
/// Built with the static helpers: <see cref="Number"/>, <see cref="Integer"/>,
/// <see cref="Text"/>, <see cref="Boolean"/> and <see cref="Choice"/>.
/// </remarks>
public sealed class ParameterDefinition
{
    private ParameterDefinition(string name, ParameterKind kind, object defaultValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
        Kind = kind;
        Default = defaultValue;
    }

    /// <summary>The label on the panel, and the key in saved files.</summary>
    public string Name { get; }

    public ParameterKind Kind { get; }

    /// <summary>A new node's value: double, int, string or bool, by <see cref="Kind"/>.</summary>
    public object Default { get; }

    public double? Minimum { get; private init; }

    public double? Maximum { get; private init; }

    /// <summary>The slider's step.</summary>
    public double Step { get; private init; } = 1;

    public IReadOnlyList<string> Options { get; private init; } = [];

    /// <summary>Shown as the label's tooltip.</summary>
    public string? Description { get; init; }

    public static ParameterDefinition Number(
        string name, double defaultValue, double? minimum = null, double? maximum = null, double step = 0.1,
        string? description = null) =>
        new(name, ParameterKind.Number, defaultValue)
        {
            Minimum = minimum, Maximum = maximum, Step = step, Description = description,
        };

    public static ParameterDefinition Integer(
        string name, int defaultValue, int? minimum = null, int? maximum = null, string? description = null) =>
        new(name, ParameterKind.Integer, defaultValue)
        {
            Minimum = minimum, Maximum = maximum, Step = 1, Description = description,
        };

    public static ParameterDefinition Text(string name, string defaultValue = "", string? description = null) =>
        new(name, ParameterKind.Text, defaultValue) { Description = description };

    public static ParameterDefinition Boolean(string name, bool defaultValue = false, string? description = null) =>
        new(name, ParameterKind.Boolean, defaultValue) { Description = description };

    public static ParameterDefinition Choice(
        string name, IReadOnlyList<string> options, string? defaultValue = null, string? description = null)
    {
        if (options.Count == 0)
            throw new ArgumentException("A choice needs at least one option.", nameof(options));

        return new(name, ParameterKind.Choice, defaultValue ?? options[0])
        {
            Options = options, Description = description,
        };
    }

    /// <summary>
    /// A value as this parameter holds it: the right CLR type, inside the
    /// range, one of the options. Anything unreadable falls back to the default.
    /// </summary>
    public object Coerce(object? value)
    {
        try
        {
            switch (Kind)
            {
                case ParameterKind.Number:
                    return Clamp(Convert.ToDouble(value, CultureInfo.InvariantCulture));

                case ParameterKind.Integer:
                    return (int)Clamp(Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture)));

                case ParameterKind.Boolean:
                    return value is string text ? bool.Parse(text) : Convert.ToBoolean(value, CultureInfo.InvariantCulture);

                case ParameterKind.Choice:
                    var option = Convert.ToString(value, CultureInfo.InvariantCulture);
                    return option is not null && Options.Contains(option) ? option : Default;

                default:
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return Default;
        }
    }

    private double Clamp(double value)
    {
        if (double.IsNaN(value))
            return Convert.ToDouble(Default, CultureInfo.InvariantCulture);

        if (Minimum is { } minimum && value < minimum)
            value = minimum;

        if (Maximum is { } maximum && value > maximum)
            value = maximum;

        return value;
    }
}
