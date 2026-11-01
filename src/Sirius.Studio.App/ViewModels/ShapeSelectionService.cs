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
/// The key is the <b>producing port</b>, which is what a viewer and a table
/// reading the same wire have in common. Two viewers on the same blob finder
/// therefore share a selection, while a second finder keeps its own.
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
    private readonly Dictionary<OutputPort, int> _selected = new();

    /// <summary>Raised with the port whose selection changed, as <see cref="Origin"/> gives it.</summary>
    public event EventHandler<OutputPort>? Changed;

    /// <summary>
    /// The port these shapes were first produced on: back through any node
    /// that hands its shapes on one for one, such as Shape Style.
    /// </summary>
    /// <remarks>
    /// The key, rather than the port itself, so a viewer on the styled wire
    /// and a table on the finder's wire are one selection - the same shapes
    /// at the same indices.
    /// </remarks>
    public static OutputPort? Origin(OutputPort? port)
    {
        // A wire loop is refused by the graph; the count only stops a
        // mistake there from hanging the interface.
        for (var step = 0; step < 64 && port?.Node is IKeepsShapeOrder keeper; step++)
        {
            if (!ReferenceEquals(port, keeper.ShapesOut) || keeper.ShapesIn.Connection?.Source is not { } upstream)
                break;

            port = upstream;
        }

        return port;
    }

    /// <summary>Whether two ports carry the same shapes at the same indices.</summary>
    public static bool Same(OutputPort? a, OutputPort? b) =>
        a is not null && ReferenceEquals(Origin(a), Origin(b));

    /// <summary>The selected index for a producer, counting from zero.</summary>
    public int? For(OutputPort? source) =>
        Origin(source) is { } origin && _selected.TryGetValue(origin, out var index) ? index : null;

    public void Select(OutputPort? source, int? index)
    {
        source = Origin(source);

        if (source is null)
            return;

        if (index is { } wanted && wanted >= 0)
        {
            if (_selected.TryGetValue(source, out var current) && current == wanted)
                return;

            _selected[source] = wanted;
        }
        else
        {
            if (!_selected.Remove(source))
                return;
        }

        Changed?.Invoke(this, source);
    }
}
