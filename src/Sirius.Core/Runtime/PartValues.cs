using System.Collections;
using System.Globalization;

namespace Sirius.Core.Runtime;

/// <summary>
/// What is known about the part an inspection is of, from whatever asked
/// for it - a PLC's pallet ID, position and run mode - by name, in order.
/// </summary>
/// <remarks>
/// <para>
/// Carried with the inspection from the trigger to what is kept of it: the
/// snapshot, and the picture's <c>.sann</c> file, where it is the part ID
/// that pictures from two stations can be matched on.
/// </para>
/// <para>
/// Values are kept plain - yes/no, whole numbers, numbers, text - so they
/// can be written anywhere and outlive whatever they came from. Names
/// compare ignoring case; a name given twice keeps its first place and
/// its last value.
/// </para>
/// </remarks>
public sealed class PartValues : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly List<KeyValuePair<string, object?>> _values = [];

    public PartValues(IEnumerable<KeyValuePair<string, object?>> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        foreach (var (name, value) in values)
        {
            var key = name?.Trim();

            if (string.IsNullOrEmpty(key))
                continue;

            var plain = Plain(value);
            var at = IndexOf(key);

            if (at >= 0)
                _values[at] = new(_values[at].Key, plain);
            else
                _values.Add(new(key, plain));
        }
    }

    public static PartValues Empty { get; } = new([]);

    public int Count => _values.Count;

    public KeyValuePair<string, object?> this[int index] => _values[index];

    /// <summary>The value called <paramref name="name"/>, ignoring case, or null.</summary>
    public object? this[string name] => TryGetValue(name, out var value) ? value : null;

    public IEnumerable<string> Names => _values.Select(pair => pair.Key);

    public bool TryGetValue(string name, out object? value)
    {
        var at = IndexOf(name);
        value = at >= 0 ? _values[at].Value : null;
        return at >= 0;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>"PalletId 12, PositionId 3" - for a log line.</summary>
    public override string ToString() =>
        string.Join(", ", _values.Select(pair => $"{pair.Key} {Text(pair.Value)}"));

    private int IndexOf(string name) =>
        _values.FindIndex(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));

    private static string Text(object? value) => value switch
    {
        null => "(none)",
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>
    /// A value as one of the plain kinds kept: bool, long, double or
    /// string. Anything else is kept as its text.
    /// </summary>
    public static object? Plain(object? value) => value switch
    {
        null => null,
        bool flag => flag,
        string text => text,
        byte or sbyte or short or ushort or int or uint or long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        ulong whole => whole <= long.MaxValue ? (long)whole : (double)whole,
        float number => (double)number,
        double number => number,
        decimal number => (double)number,
        Enum choice => choice.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
