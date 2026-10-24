using System.Globalization;

namespace Sirius.Core.Nodes;

/// <summary>
/// A low and a high bound, edited as one parameter. Either end may be
/// absent, meaning no limit on that side.
/// </summary>
/// <remarks>
/// <para>
/// A band is one decision, not two. Split across separate "min" and "max"
/// parameters it reads as six unrelated sliders on a colour range node, the
/// pairing is left to the operator to infer from the labels, and nothing
/// stops the low being dragged above the high.
/// </para>
/// <para>
/// An absent end is absent, not a magic number. The blob filters used
/// <c>-1</c> for "no limit", which every parameter description then had to
/// explain, made a legitimate negative bound unexpressible, and - once
/// there is a slider - cannot be told apart from a handle parked at the end
/// of whatever track happens to be drawn. A filter whose upper bound is "the
/// area of the image I was set up on" quietly becomes wrong when the camera
/// changes.
/// </para>
/// <para>
/// Held as doubles so the same type serves an integer channel and a
/// fractional threshold. Whoever declares the parameter decides the bounds
/// and whether to round.
/// </para>
/// </remarks>
public readonly record struct ValueRange
{
    public ValueRange(double? low, double? high)
    {
        // Ordered when both ends are known, so no consumer has to wonder. A
        // range typed in backwards is a range, not an error. With one end
        // absent there is nothing to order against.
        if (low is { } l && high is { } h)
        {
            Low = Math.Min(l, h);
            High = Math.Max(l, h);
        }
        else
        {
            Low = low;
            High = high;
        }
    }

    /// <summary>No limit at either end.</summary>
    public static ValueRange Unbounded => new(null, null);

    /// <summary>The lower bound, or null for no limit.</summary>
    public double? Low { get; init; }

    /// <summary>The upper bound, or null for no limit.</summary>
    public double? High { get; init; }

    public bool HasLow => Low.HasValue;

    public bool HasHigh => High.HasValue;

    /// <summary>True when neither end limits anything.</summary>
    public bool IsUnbounded => !HasLow && !HasHigh;

    /// <summary>The lower bound, or <paramref name="fallback"/> if there is none.</summary>
    public double LowOr(double fallback) => Low ?? fallback;

    /// <summary>The upper bound, or <paramref name="fallback"/> if there is none.</summary>
    public double HighOr(double fallback) => High ?? fallback;

    /// <summary>Whether <paramref name="value"/> passes. An absent end never rejects.</summary>
    public bool Contains(double value) =>
        (Low is not { } low || value >= low) &&
        (High is not { } high || value <= high);

    /// <summary>
    /// The same range with any present end held inside
    /// <paramref name="minimum"/> and <paramref name="maximum"/>. An absent
    /// end stays absent.
    /// </summary>
    public ValueRange Clamp(double minimum, double maximum) => new(
        Low is { } low ? Math.Clamp(low, minimum, maximum) : null,
        High is { } high ? Math.Clamp(high, minimum, maximum) : null);

    /// <summary>The same range with any present end rounded to a whole number.</summary>
    public ValueRange Rounded() => new(
        Low is { } low ? Math.Round(low) : null,
        High is { } high ? Math.Round(high) : null);

    public override string ToString() => (Low, High) switch
    {
        (null, null) => "any",
        (null, { } high) => string.Format(CultureInfo.InvariantCulture, "up to {0:0.###}", high),
        ({ } low, null) => string.Format(CultureInfo.InvariantCulture, "{0:0.###} and up", low),
        var (low, high) => string.Format(
            CultureInfo.InvariantCulture, "{0:0.###} to {1:0.###}", low, high),
    };
}
