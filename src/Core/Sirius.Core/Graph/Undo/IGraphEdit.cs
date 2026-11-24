namespace Sirius.Core.Graph.Undo;

/// <summary>
/// One reversible change to a graph.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a snapshot of the whole graph. Undoing by reloading a
/// saved copy would replace every node instance, and the shell holds node
/// references everywhere - panels, previews, the scheduler's cache, the
/// properties panel, the selection. Each one would be pointing at a node
/// that is no longer in the graph, and the symptom would be panels that
/// quietly stop updating rather than anything that throws.
/// </para>
/// <para>
/// So an edit changes the least it can and keeps the instances it removed,
/// which means undoing a delete puts back the very same node and everything
/// holding it reattaches without being told.
/// </para>
/// </remarks>
public interface IGraphEdit
{
    /// <summary>
    /// What this did, in the words the Edit menu uses: "Delete 3 nodes",
    /// "Change Threshold". Shown after Undo and Redo, so it has to read as
    /// the thing being undone rather than as a class name.
    /// </summary>
    string Label { get; }

    /// <summary>Does it, or does it again after an undo.</summary>
    void Apply(VisionGraph graph);

    /// <summary>Puts the graph back as it was.</summary>
    void Revert(VisionGraph graph);

    /// <summary>
    /// Whether the graph has to be re-evaluated afterwards.
    /// </summary>
    /// <remarks>
    /// False for the edits that change only how things look - a move, a
    /// rename, a colour. Running a recipe again because a node was dragged
    /// would be absurd on a large frame, and it is the sort of thing undo
    /// makes happen repeatedly.
    /// </remarks>
    bool AffectsResults { get; }

    /// <summary>
    /// Absorbs <paramref name="next"/> into this edit, or declines.
    /// </summary>
    /// <remarks>
    /// A slider dragged across its range raises a property change per pixel.
    /// Without merging, undo walks back through two hundred of them and the
    /// operator concludes it is broken. The rule lives on the edit because
    /// only the edit knows what continuing the same gesture means.
    /// </remarks>
    bool TryAbsorb(IGraphEdit next);
}
