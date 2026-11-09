using System.Globalization;
using System.Text.Json;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Projects;

namespace Sirius.Core.Runtime;

/// <summary>How a setting is changed on the line.</summary>
public enum TunableKind
{
    /// <summary>A number with decimals: a tolerance, a scale.</summary>
    Number,

    /// <summary>A whole number: a threshold, a count.</summary>
    WholeNumber,

    /// <summary>On or off.</summary>
    Toggle,

    /// <summary>One of a list: a polarity, a mode.</summary>
    Choice,

    /// <summary>
    /// Shown, not changed: a region, a file, a range - anything a screen
    /// has no control for yet.
    /// </summary>
    Fixed,
}

/// <summary>
/// A published setting as the line sees it: what kind of control changes it,
/// and between which values (runtime MVP step 6).
/// </summary>
/// <remarks>
/// <para>
/// The range is the node's own, narrowed by the station's limits for it
/// (<see cref="Station.Limits"/>, set in Sirius Builder) - never widened by
/// them. The node's
/// own is read as Studio's properties panel reads it: a grey level follows
/// the image's bit depth, and a bound taken from another setting follows that
/// setting.
/// </para>
/// <para>
/// Reading and converting are safe at any time; <see cref="Apply"/> changes
/// the node, and so must not be called while the recipe is running. The
/// station runner sees to that.
/// </para>
/// </remarks>
public sealed class Tunable
{
    private Tunable(VisionNode node, ParamDescriptor parameter, PublishedName entry, SettingLimit? limit)
    {
        Node = node;
        Parameter = parameter;
        Entry = entry;
        Limit = limit;
        Kind = KindOf(parameter);
    }

    /// <summary>The station's limit for it on the line, or null for the node's own range.</summary>
    public SettingLimit? Limit { get; }

    /// <summary>The published name.</summary>
    public string Name => Entry.Name;

    public VisionNode Node { get; }

    public ParamDescriptor Parameter { get; }

    public PublishedName Entry { get; }

    public TunableKind Kind { get; }

    /// <summary>Whether it can be changed on the line at all.</summary>
    public bool IsChangeable => Kind != TunableKind.Fixed && !IsWired;

    /// <summary>
    /// Whether a wire in the recipe sets it - a setting with a socket of its
    /// own, connected - so a value typed for it would not be used.
    /// </summary>
    public bool IsWired =>
        Parameter.Attribute.Connectable &&
        Node.Inputs.Any(port => port.Name == Parameter.Name && port.IsConnected && Node.IsParameterPort(port));

    public bool IsNumeric => Kind is TunableKind.Number or TunableKind.WholeNumber;

    /// <summary>"Threshold 1 › Level".</summary>
    public string Source => $"{Node.Name} › {Parameter.DisplayName}";

    /// <summary>What the node holds now.</summary>
    public object? Value => Parameter.GetValue(Node);

    /// <summary>The lowest the node itself takes; negative infinity for no limit.</summary>
    public double NodeMinimum => IsNumeric ? Bound(Parameter.Attribute.MinSource, Parameter.Attribute.HasMin, Parameter.Attribute.Min, TypeMinimum) : double.NaN;

    /// <summary>The highest the node itself takes; positive infinity for no limit.</summary>
    public double NodeMaximum => IsNumeric ? Bound(Parameter.Attribute.MaxSource, Parameter.Attribute.HasMax, Parameter.Attribute.Max, TypeMaximum) : double.NaN;

    /// <summary>The lowest it may be set to on the line.</summary>
    public double Minimum
    {
        get
        {
            var low = Math.Max(NodeMinimum, Limit?.Lowest ?? double.NegativeInfinity);

            // Limits made for another bit depth, or past where another
            // setting now bounds it, can leave nothing between them: hold
            // the low end at the high one rather than offer no value.
            return Math.Min(low, Maximum);
        }
    }

    /// <summary>The highest it may be set to on the line.</summary>
    public double Maximum => Math.Min(NodeMaximum, Limit?.Highest ?? double.PositiveInfinity);

    /// <summary>Whether both ends are known, so it can be shown as a slider.</summary>
    public bool IsBounded => IsNumeric && double.IsFinite(Minimum) && double.IsFinite(Maximum);

    /// <summary>One press of + or −.</summary>
    public double Step
    {
        get
        {
            if (!IsNumeric)
                return double.NaN;

            if (ReadBound(Parameter.Attribute.StepSource) is { } fromNode && fromNode > 0)
                return fromNode;

            if (Parameter.Attribute.HasStep && Parameter.Attribute.Step > 0)
                return Parameter.Attribute.OddOnly ? Math.Max(2, Parameter.Attribute.Step) : Parameter.Attribute.Step;

            if (Kind == TunableKind.WholeNumber)
                return Parameter.Attribute.OddOnly ? 2 : 1;

            return IsBounded && Maximum > Minimum ? Nice((Maximum - Minimum) / 100) : 0.1;
        }
    }

    /// <summary>A choice's options, by name.</summary>
    public IReadOnlyList<string> Choices =>
        Kind == TunableKind.Choice ? Enum.GetNames(ValueType) : [];

    /// <summary>"0 – 255", "10 or more", or empty for a setting without a range.</summary>
    public string RangeText
    {
        get
        {
            if (!IsNumeric)
                return string.Empty;

            var low = double.IsFinite(Minimum);
            var high = double.IsFinite(Maximum);

            return (low, high) switch
            {
                (true, true) => $"{Format(Minimum)} – {Format(Maximum)}",
                (true, false) => $"{Format(Minimum)} or more",
                (false, true) => $"{Format(Maximum)} or less",
                _ => string.Empty,
            };
        }
    }

    /// <summary>A number as this setting is written: whole, or up to three decimals.</summary>
    public string Format(double value) =>
        Kind == TunableKind.WholeNumber || Step >= 1
            ? Math.Round(value).ToString("0", CultureInfo.CurrentCulture)
            : value.ToString("0.###", CultureInfo.CurrentCulture);

    /// <summary>
    /// The published settings of <paramref name="recipe"/> that its nodes
    /// still have, in node order.
    /// </summary>
    /// <param name="limits">The station's limits, by published name; null for none.</param>
    public static IReadOnlyList<Tunable> In(VisionGraph recipe, IReadOnlyDictionary<string, SettingLimit>? limits = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        return Publishing.In(recipe)
            .Where(item => item.Kind == PublishedKind.Setting && item.Found)
            .Select(item => For(item.Node, item.Entry, limits?.GetValueOrDefault(item.Name)))
            .OfType<Tunable>()
            .ToArray();
    }

    /// <summary>The setting published under <paramref name="name"/>, or null.</summary>
    public static Tunable? Find(VisionGraph recipe, string name, SettingLimit? limit = null) =>
        Publishing.Find(recipe, name) is { Kind: PublishedKind.Setting, Found: true } item ? For(item.Node, item.Entry, limit) : null;

    /// <summary>The tunable for one published setting entry, or null when the node has no such setting.</summary>
    public static Tunable? For(VisionNode node, PublishedName entry, SettingLimit? limit = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Kind != PublishedKind.Setting)
            return null;

        var parameter = node.Descriptor.Parameters.FirstOrDefault(p => p.Name == entry.Member);
        return parameter is null ? null : new Tunable(node, parameter, entry, limit);
    }

    /// <summary>
    /// Reads <paramref name="raw"/> as a value for this setting - a number,
    /// on or off, a choice by name, typed text, or a value from a file - and
    /// checks it against the range.
    /// </summary>
    /// <returns>Why it cannot be used, or null.</returns>
    public string? Convert(object? raw, out object? value)
    {
        value = null;

        if (raw is JsonElement element)
            raw = FromJson(element);

        if (Kind != TunableKind.Fixed && IsWired)
            return $"{Name} is set by a wire in the recipe, so cannot be changed on the line.";

        switch (Kind)
        {
            case TunableKind.Number:
            case TunableKind.WholeNumber:
            {
                if (!TryNumber(raw, out var number))
                    return $"{Name} needs a number.";

                if (Kind == TunableKind.WholeNumber)
                    number = Math.Round(number);

                if (number < Minimum || number > Maximum)
                {
                    return RangeText.Length == 0
                        ? $"{Name} cannot be {Format(number)}."
                        : $"{Name} must be {Describe()}.";
                }

                if (Parameter.Attribute.OddOnly && Math.Abs(Math.Round(number) % 2) != 1)
                    return $"{Name} must be an odd number.";

                try
                {
                    value = System.Convert.ChangeType(number, ValueType, CultureInfo.InvariantCulture);
                }
                catch (OverflowException)
                {
                    return $"{Name} cannot be {Format(number)}.";
                }

                return null;
            }

            case TunableKind.Toggle:
                if (raw is bool yes)
                {
                    value = yes;
                    return null;
                }

                if (raw is string text && bool.TryParse(text.Trim(), out var parsed))
                {
                    value = parsed;
                    return null;
                }

                return $"{Name} is on or off.";

            case TunableKind.Choice:
            {
                var name = raw switch
                {
                    string typed => typed.Trim(),
                    Enum choice when choice.GetType() == ValueType => choice.ToString(),
                    _ => null,
                };

                var match = name is null ? null : Choices.FirstOrDefault(choice => string.Equals(choice, name, StringComparison.OrdinalIgnoreCase));

                if (match is null)
                    return $"{Name} is one of: {string.Join(", ", Choices)}.";

                value = Enum.Parse(ValueType, match);
                return null;
            }

            default:
                return $"{Name} cannot be changed on the line.";
        }
    }

    /// <summary>Puts a value made by <see cref="Convert"/> into the node. Not while the recipe runs.</summary>
    public void Apply(object? value) => Parameter.SetValue(Node, value);

    /// <summary>A value as kept in a file: a number, true or false, a choice's name.</summary>
    public static object? ToStored(object? value) => value switch
    {
        Enum choice => choice.ToString(),
        null => null,
        bool => value,
        IConvertible when ParamDescriptor.IsNumeric(value.GetType()) => System.Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => System.Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private string Describe() =>
        (double.IsFinite(Minimum), double.IsFinite(Maximum)) switch
        {
            (true, true) => $"between {Format(Minimum)} and {Format(Maximum)}",
            (true, false) => $"at least {Format(Minimum)}",
            _ => $"at most {Format(Maximum)}",
        };

    private Type ValueType => Nullable.GetUnderlyingType(Parameter.ValueType) ?? Parameter.ValueType;

    private static TunableKind KindOf(ParamDescriptor parameter)
    {
        var type = Nullable.GetUnderlyingType(parameter.ValueType) ?? parameter.ValueType;

        if (parameter.Attribute.Hidden)
            return TunableKind.Fixed;

        if (type == typeof(bool))
            return TunableKind.Toggle;

        if (type.IsEnum)
            return TunableKind.Choice;

        if (ParamDescriptor.IsNumeric(type))
        {
            return type == typeof(float) || type == typeof(double) || type == typeof(decimal)
                ? TunableKind.Number
                : TunableKind.WholeNumber;
        }

        return TunableKind.Fixed;
    }

    private double Bound(string? source, bool hasFixed, double fixedValue, double typeLimit)
    {
        if (ReadBound(source) is { } fromNode)
            return fromNode;

        return hasFixed ? fixedValue * DepthScale : typeLimit;
    }

    /// <summary>
    /// For a level in the image's own values, how much longer its range is
    /// than at 8 bits - as the properties panel scales it.
    /// </summary>
    private double DepthScale =>
        Parameter.Attribute.Intensity && Node.ImageMaxValue is { } top && top > 1 ? top / 255.0 : 1;

    private double? ReadBound(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
            return null;

        var bound = Node.Descriptor.Parameters.FirstOrDefault(p => p.Name == propertyName);
        var value = bound is not null
            ? bound.GetValue(Node)
            : Node.GetType().GetProperty(propertyName)?.GetValue(Node);

        return value is null ? null : System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private double TypeMinimum => ValueType switch
    {
        var t when t == typeof(byte) || t == typeof(ushort) || t == typeof(uint) || t == typeof(ulong) => 0,
        var t when t == typeof(sbyte) => sbyte.MinValue,
        var t when t == typeof(short) => short.MinValue,
        var t when t == typeof(int) => int.MinValue,
        _ => double.NegativeInfinity,
    };

    private double TypeMaximum => ValueType switch
    {
        var t when t == typeof(byte) => byte.MaxValue,
        var t when t == typeof(sbyte) => sbyte.MaxValue,
        var t when t == typeof(short) => short.MaxValue,
        var t when t == typeof(ushort) => ushort.MaxValue,
        var t when t == typeof(int) => int.MaxValue,
        var t when t == typeof(uint) => uint.MaxValue,
        _ => double.PositiveInfinity,
    };

    private static bool TryNumber(object? raw, out double number)
    {
        number = double.NaN;

        switch (raw)
        {
            case string text:
                return (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out number) ||
                        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) &&
                       double.IsFinite(number);

            case IConvertible when raw is not bool && ParamDescriptor.IsNumeric(raw.GetType()):
                number = System.Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return double.IsFinite(number);

            default:
                return false;
        }
    }

    private static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        _ => null,
    };

    /// <summary>1, 2 or 5 times a power of ten, at most <paramref name="value"/>.</summary>
    private static double Nice(double value)
    {
        if (value <= 0 || !double.IsFinite(value))
            return 0.1;

        var power = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var leading = value / power;

        return (leading >= 5 ? 5 : leading >= 2 ? 2 : 1) * power;
    }
}
