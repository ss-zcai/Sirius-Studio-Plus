namespace Sirius.Core.Nodes;

/// <summary>
/// Palette grouping for node types. The middle entries match
/// <c>VisionToolCategory</c> from Sirius Studio, including Blob and
/// Transformation. <see cref="Source"/>, <see cref="Analysis"/> and
/// <see cref="Output"/> are new because a graph - unlike a linear tool rack -
/// needs explicit entry and exit points.
/// </summary>
public enum NodeCategory
{
    Source,
    Basic,

    /// <summary>Operations taking more than one image or shape set.</summary>
    Combine,

    Filtering,
    Morphology,
    Blob,

    /// <summary>Reducing located points to the ideal figure underneath.</summary>
    Fitting,

    Transformation,
    Analysis,
    Calibration,
    TextAndCodeReading,
    Output,

    /// <summary>
    /// Nodes from a custom node library, unless the library placed them in
    /// one of the groups above.
    /// </summary>
    Custom,
}
