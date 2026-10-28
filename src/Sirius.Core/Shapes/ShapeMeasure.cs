namespace Sirius.Core.Shapes;

/// <summary>
/// A measurement that can be filtered on or ordered by.
/// </summary>
/// <remarks>
/// One vocabulary for filtering, sorting and selecting, as Cognex VisionPro
/// does. Separate lists would be separate places to add each new measure, and
/// they would drift.
///
/// The members name measurements; <see cref="ShapeMeasures.MetricFor"/> maps
/// them to the <see cref="ShapeMetric"/> keys a shape actually carries.
/// </remarks>
public enum ShapeMeasure
{
    Area,
    Perimeter,
    Circularity,
    Solidity,
    AspectRatio,
    Width,
    Length,
    Extent,
    HoleCount,

    /// <summary>Reading order: top to bottom, then left to right within a row.</summary>
    Position,

    /// <summary>An edge point's strength, so weak edges can be dropped or the strongest picked.</summary>
    Contrast,

    /// <summary>A circle's radius, or an edge point's distance from the middle of its ring.</summary>
    Radius,

    /// <summary>An edge point's distance to one side of the middle of its region.</summary>
    Offset,
}

public static class ShapeMeasures
{
    /// <summary>The metric key a measure reads, or null for Position.</summary>
    public static string? MetricFor(ShapeMeasure measure) => measure switch
    {
        ShapeMeasure.Area => ShapeMetric.Area,
        ShapeMeasure.Perimeter => ShapeMetric.Perimeter,
        ShapeMeasure.Circularity => ShapeMetric.Circularity,
        ShapeMeasure.Solidity => ShapeMetric.Solidity,
        ShapeMeasure.AspectRatio => ShapeMetric.AspectRatio,
        ShapeMeasure.Width => ShapeMetric.Width,
        ShapeMeasure.Length => ShapeMetric.Length,
        ShapeMeasure.Extent => ShapeMetric.Extent,
        ShapeMeasure.HoleCount => ShapeMetric.HoleCount,
        ShapeMeasure.Contrast => ShapeMetric.Contrast,
        ShapeMeasure.Radius => ShapeMetric.Radius,
        ShapeMeasure.Offset => ShapeMetric.Offset,
        _ => null,
    };
}
