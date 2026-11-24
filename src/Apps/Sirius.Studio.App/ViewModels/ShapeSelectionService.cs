using Sirius.Core.Graph;

namespace Sirius.Studio.App.ViewModels;

/// <summary>
/// Which shape is selected, shared between everything showing the same set.
/// </summary>
/// <remarks>
/// <para>
/// A viewer and a table fed from one output are not showing copies of the
/// same blobs - the scheduler hands both the same <c>ShapeSet</c> instance.
/// So nothing needs to be passed between them; the only thing missing is
/// agreement on which one is selected, and that is all this holds.
/// </para>
/// <para>
/// It cannot live on the shape. Shapes are port data: immutable, rebuilt on
/// every evaluation, and handed to every consumer of that output. A selection
/// flag there would be erased by the next pass, and would mean the UI writing
/// into values the graph owns.
/// </para>
/// <para>
/// The key is the port the shapes were first produced on - their
/// <see cref="Origin"/> - traced back through nodes that hand them on one
/// for one (Shape Style) and nodes that hand on some of them (Filter
/// Shapes, Select Shape). So a table on a blob finder and a viewer drawing
/// what a filter kept are one selection, each in its own numbering: row 7
/// of the table is the 3rd kept blob on the image, and a blob the filter
/// rejected is simply not selected on the Kept wire.
/// </para>
/// <para>
/// Selection is <b>positional</b>: it remembers an index, not a blob. Nothing
/// about a found region identifies it across evaluations, so when the part
/// moves and the set re-sorts, index 5 may be a different hole. Sorting the
/// producer by Position is what makes the number mean something stable.
/// </para>
/// </remarks>
public sealed class ShapeSelectionService
{
    /// <summary>The selected index, in the origin's numbering, by origin.</summary>
    private readonly Dictionary<OutputPort, int> _selected = new();

    /// <summary>The port each selection was made on, by origin.</summary>
    private readonly Dictionary<OutputPort, OutputPort> _pickedOn = new();

    /// <summary>Raised with the port whose selection changed, as <see cref="Origin"/> gives it.</summary>
    public event EventHandler<OutputPort>? Changed;

    /// <summary>
    /// The port these shapes were first produced on: back through any node
    /// that hands its shapes on one for one, such as Shape Style, or some
    /// of them, such as Filter Shapes.
    /// </summary>
    public static OutputPort? Origin(OutputPort? port) => Trace(port).Origin;

    /// <summary>Whether two ports carry shapes from the same origin, so share a selection.</summary>
    public static bool Same(OutputPort? a, OutputPort? b) =>
        a is not null && ReferenceEquals(Origin(a), Origin(b));

    /// <summary>
    /// The selected index on <paramref name="source"/>, counting from zero,
    /// or null when nothing is selected - or what is selected is not among
    /// the shapes on that port.
    /// </summary>
    public int? For(OutputPort? source)
    {
        var (origin, map) = Trace(source);

        if (origin is null || !_selected.TryGetValue(origin, out var index))
            return null;

        if (map is null)
            return index;

        for (var i = 0; i < map.Count; i++)
        {
            if (map[i] == index)
                return i;
        }

        return null;
    }

    /// <summary>
    /// The port the current selection among <paramref name="source"/>'s
    /// shapes was made on - a table's, or a viewer's - so a number shown
    /// beside it can be the one that table shows.
    /// </summary>
    public OutputPort? PickedOn(OutputPort? source) =>
        Origin(source) is { } origin && _pickedOn.TryGetValue(origin, out var picked) ? picked : null;

    public void Select(OutputPort? source, int? index)
    {
        var (origin, map) = Trace(source);

        if (origin is null)
            return;

        // Into the origin's numbering; a shape the map does not know - a
        // run since that changed the set - selects nothing.
        int? wanted = index is { } local && local >= 0
            ? map is null ? local : local < map.Count && map[local] >= 0 ? map[local] : null
            : null;

        if (wanted is { } chosen)
        {
            // The same shape picked again elsewhere still changes which
            // numbering is shown beside it.
            if (_selected.TryGetValue(origin, out var current) && current == chosen &&
                _pickedOn.TryGetValue(origin, out var before) && ReferenceEquals(before, source))
            {
                return;
            }

            _selected[origin] = chosen;
            _pickedOn[origin] = source!;
        }
        else
        {
            _pickedOn.Remove(origin);

            if (!_selected.Remove(origin))
                return;
        }

        Changed?.Invoke(this, origin);
    }

    /// <summary>
    /// The origin of <paramref name="port"/>'s shapes, and for each shape on
    /// it, its index there - or null for a map when they are the origin's,
    /// one for one.
    /// </summary>
    private static (OutputPort? Origin, IReadOnlyList<int>? Map) Trace(OutputPort? port)
    {
        IReadOnlyList<int>? map = null;

        // A wire loop is refused by the graph; the count only stops a
        // mistake there from hanging the interface.
        for (var step = 0; step < 64 && port is not null; step++)
        {
            if (port.Node is IKeepsShapeOrder keeper &&
                ReferenceEquals(port, keeper.ShapesOut) &&
                keeper.ShapesIn.Connection?.Source is { } same)
            {
                port = same;
                continue;
            }

            if (port.Node is IPassesSomeShapes filter &&
                filter.SourceIndices(port) is { } some &&
                filter.ShapesIn.Connection?.Source is { } upstream)
            {
                // Each shape here, in the numbering of the port upstream.
                map = map is null
                    ? some
                    : map.Select(i => i >= 0 && i < some.Count ? some[i] : -1).ToArray();

                port = upstream;
                continue;
            }

            break;
        }

        return (port, map);
    }
}
