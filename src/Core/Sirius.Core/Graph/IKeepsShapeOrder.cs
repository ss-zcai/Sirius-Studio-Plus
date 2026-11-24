namespace Sirius.Core.Graph;

/// <summary>
/// A node whose shapes out are its shapes in, one for one and in the same
/// order - restyled, perhaps, but the third shape out is the third shape in.
/// </summary>
/// <remarks>
/// A shape picked on the image is matched to a row in a table by its index
/// in the set the producing port gave. A Shape Style between the finder
/// and the viewer is a different port giving the same shapes, so without
/// this the viewer on the styled wire and the table on the finder's wire
/// would each keep a selection of their own. A node that drops, adds or
/// reorders shapes must not say this.
/// </remarks>
public interface IKeepsShapeOrder
{
    InputPort ShapesIn { get; }

    OutputPort ShapesOut { get; }
}
