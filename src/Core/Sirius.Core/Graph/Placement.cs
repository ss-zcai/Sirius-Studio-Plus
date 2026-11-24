namespace Sirius.Core.Graph;

/// <summary>
/// Where a run put a piece of a node's geometry that something wired in
/// moved - a band placed round a wired circle, a region laid on a wired
/// line - so the editor can show where the node actually looked.
/// </summary>
/// <remarks>
/// <para>
/// Without it the image showed the band as drawn while the node searched
/// somewhere else entirely. A Ring Calipers with a circle wired in drew a
/// ring the operator had placed by hand and searched a band forty pixels
/// wide round the circle's edge, a hundred pixels away, and nothing on
/// screen said so: the only clue was a warning that no edge was found.
/// </para>
/// <para>
/// Reported by the run rather than worked out by the editor, because only
/// the node knows what it did with what arrived - a circle replaces a band,
/// a point only moves it, and a shape on a region parameter sets some of it
/// and leaves the rest to the drawing.
/// </para>
/// </remarks>
/// <param name="Value">The geometry as the run used it, in the parameter's own type.</param>
/// <param name="Note">
/// One sentence for the properties panel: what placed it, and what drawing
/// over it still changes.
/// </param>
public sealed record Placement(object Value, string Note)
{
    /// <summary>
    /// How a drawing over the placed geometry is kept, when it is not simply
    /// stored as the parameter's value - a band round a wired circle is kept
    /// as its offset and width from the circle. Null stores the drawing.
    /// </summary>
    public Func<object, Redrawn?>? Redraw { get; init; }
}

/// <summary>
/// What drawing over placed geometry changes: the settings to write, and
/// the geometry to show until the next run places it again.
/// </summary>
public sealed record Redrawn(IReadOnlyDictionary<string, object> Settings, object Shown);
