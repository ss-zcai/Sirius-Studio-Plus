using System.Globalization;

namespace Sirius.Core.Nodes;

/// <summary>
/// An annular search band: a centre, and the two radii that bound it.
/// </summary>
/// <remarks>
/// <para>
/// One value rather than four numbers, for the reason the region of
/// interest became one <see cref="OrientedRect"/> rather than five: it is
/// a single decision, it is drawn in a single gesture, and four separate
/// parameters are four things that can be edited into a state that is not
/// a ring.
/// </para>
/// <para>
/// Held as a centre and two radii rather than as a centre, a mean radius
/// and a width, because that is what the consumer wants - a caliper scans
/// from one radius to the other. The drawing gesture thinks in mean and
/// width and converts, which is the right way round: the stored form
/// should suit the thing that reads it.
/// </para>
/// </remarks>
public readonly record struct Ring
{
    public Ring(double centreX, double centreY, double innerRadius, double outerRadius)
    {
        CentreX = centreX;
        CentreY = centreY;

        // Ordered on construction, so nothing downstream has to wonder.
        // A ring drawn inwards is still a ring.
        var low = Math.Max(0, Math.Min(innerRadius, outerRadius));
        var high = Math.Max(0, Math.Max(innerRadius, outerRadius));

        InnerRadius = low;

        // A band of zero width can be typed but cannot be scanned, so it
        // is widened rather than left to produce "no edge found" from a
        // tool that looks correctly configured.
        OuterRadius = high > low ? high : low + 1;
    }

    public double CentreX { get; init; }

    public double CentreY { get; init; }

    public double InnerRadius { get; init; }

    public double OuterRadius { get; init; }

    /// <summary>Halfway between the two radii - where the edge is expected.</summary>
    public double MeanRadius => (InnerRadius + OuterRadius) / 2;

    /// <summary>How far the band reaches either side of the mean radius.</summary>
    public double HalfWidth => (OuterRadius - InnerRadius) / 2;

    /// <summary>The band's full width.</summary>
    public double Width => OuterRadius - InnerRadius;

    /// <summary>
    /// A ring from the form the drawing gesture produces: a centre, the
    /// radius of the edge, and how far either side to search.
    /// </summary>
    public static Ring FromMean(double centreX, double centreY, double meanRadius, double halfWidth)
    {
        var half = Math.Max(0.5, Math.Abs(halfWidth));

        return new Ring(centreX, centreY, meanRadius - half, meanRadius + half);
    }

    public Ring WithCentre(double x, double y) => this with { CentreX = x, CentreY = y };

    /// <summary>Whether a point falls inside the band.</summary>
    public bool Contains(double x, double y)
    {
        var dx = x - CentreX;
        var dy = y - CentreY;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));

        return distance >= InnerRadius && distance <= OuterRadius;
    }

    /// <summary>Reads "500, 500, 80, 140".</summary>
    public static bool TryParse(string? text, out Ring ring)
    {
        ring = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 4)
            return false;

        var values = new double[4];

        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return false;
        }

        ring = new Ring(values[0], values[1], values[2], values[3]);
        return true;
    }

    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "{0:0.##}, {1:0.##}, {2:0.##}, {3:0.##}",
        CentreX,
        CentreY,
        InnerRadius,
        OuterRadius);
}
