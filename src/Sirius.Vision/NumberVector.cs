using System.Globalization;

namespace Sirius.Vision;

/// <summary>
/// A list of numbers travelling as one value - a size, an offset, a kernel
/// extent, a per-channel setting.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately one value rather than a port per component: a size is one
/// decision, and splitting it means two wires that can disagree. It replaced
/// a fixed pair for the ordinary reason - two is the wrong number as soon as
/// anything has three. A kernel is two, an RGB offset is three, a per-channel
/// threshold is however many channels the frame has.
/// </para>
/// <para>
/// Length is not checked here. What counts as the right number of components
/// depends entirely on what is reading them, so a consumer asks for what it
/// needs through <see cref="Take"/> and says so itself when the answer is
/// wrong - which it can do by name, where this could only say "3 is not 2".
/// </para>
/// </remarks>
public sealed class NumberVector : IEquatable<NumberVector>
{
    public static NumberVector Empty { get; } = new([]);

    public NumberVector(IReadOnlyList<double> components) =>
        Components = components ?? [];

    public IReadOnlyList<double> Components { get; }

    public int Count => Components.Count;

    /// <summary>A component, or zero past the end.</summary>
    public double this[int index] =>
        index >= 0 && index < Components.Count ? Components[index] : 0;

    public double X => this[0];

    public double Y => this[1];

    public double Z => this[2];

    /// <summary>
    /// The first <paramref name="count"/> components, padded with zero.
    /// </summary>
    /// <remarks>
    /// Padding rather than throwing, because the alternative is every
    /// consumer writing the same bounds check. A node that cannot work with
    /// a short vector should say so with its own message.
    /// </remarks>
    public double[] Take(int count)
    {
        var values = new double[Math.Max(count, 0)];

        for (var i = 0; i < values.Length; i++)
            values[i] = this[i];

        return values;
    }

    /// <summary>Rounded to whole numbers, for APIs that take pixel counts.</summary>
    public int[] ToInt() => Components.Select(v => (int)Math.Round(v)).ToArray();

    /// <summary>
    /// Reads "3, 3" or "3 3 1" or "3,3,1".
    /// </summary>
    /// <remarks>
    /// Commas and whitespace both separate, because both are what people
    /// type. Parsed in the invariant culture so a recipe written on a
    /// machine using a decimal comma still reads the same here - which is
    /// also why a comma is only ever a separator.
    /// </remarks>
    public static bool TryParse(string? text, out NumberVector vector)
    {
        vector = Empty;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split([',', ' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries);
        var values = new List<double>(parts.Length);

        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return false;

            values.Add(value);
        }

        if (values.Count == 0)
            return false;

        vector = new NumberVector(values);
        return true;
    }

    public static NumberVector Parse(string? text) =>
        TryParse(text, out var vector) ? vector : Empty;

    public bool Equals(NumberVector? other) =>
        other is not null && Components.SequenceEqual(other.Components);

    public override bool Equals(object? obj) => Equals(obj as NumberVector);

    public override int GetHashCode()
    {
        var hash = new HashCode();

        foreach (var value in Components)
            hash.Add(value);

        return hash.ToHashCode();
    }

    /// <summary>
    /// Formatted the way it is typed, so the text box round-trips: what is
    /// read back is what somebody could have written.
    /// </summary>
    public override string ToString() =>
        string.Join(", ", Components.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));
}
