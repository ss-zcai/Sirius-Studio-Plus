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
        _ => null,
    };
}
