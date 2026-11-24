namespace Sirius.Core.Graph;

/// <summary>
/// A node whose shape outputs each hand on some of the shapes in, in their
/// order - Filter Shapes' Kept and Rejected, Select Shape's one - and can
/// say which input shape each one was.
/// </summary>
/// <remarks>
/// <para>
/// What lets a selection follow through a filter. A table on a blob
/// finder and a viewer drawing what a Filter Shapes kept number the same
/// blob differently - 7th found, 3rd kept - and without the link between
/// the two, picking row 7 picked nothing on the image.
/// </para>
/// <para>
/// Unlike <see cref="IKeepsShapeOrder"/>, the link is from the last run,
/// so the node keeps it: which shapes were kept depends on the shapes.
/// </para>
/// </remarks>
public interface IPassesSomeShapes
{
    InputPort ShapesIn { get; }

    /// <summary>
    /// For each shape on <paramref name="output"/>, its index in the input
    /// on the last run - or null when that is not one of these outputs, or
    /// it has not run.
    /// </summary>
    IReadOnlyList<int>? SourceIndices(OutputPort output);
}
