using Sirius.Core.Shapes;

namespace Sirius.Core.Graph;

/// <summary>
/// A measurement drawn by hand on a viewer, in a form a recipe can keep.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not the viewer's own <c>Measurement</c>: that one carries
/// selection state, cached geometry and a WPF colour, none of which belongs
/// in a saved file. This is the part worth keeping - which tool drew it,
/// where, in what colour, and what it is called.
/// </para>
/// <para>
/// The tool is a string rather than an enum so a recipe written by a build
/// that has a measurement this one does not can still be opened: the
/// unknown one is dropped with a warning instead of failing the load.
/// </para>
/// </remarks>
/// <param name="View">
/// Which of the node's pictures it was drawn on - a port name for a
/// preview, empty or null for a Viewer node's own panel.
/// </param>
/// <remarks>
/// <para>
/// <paramref name="View"/> is the part that is easy to leave out and wrong
/// to. A node can be looked at through more than one window, and a preview
/// of an input and a preview of an output are not the same picture: ROI cuts
/// it, Rotate turns it, Resize scales it. A measurement held against the
/// node alone would reappear on the other window at coordinates that mean
/// something else there.
/// </para>
/// </remarks>
public sealed record MeasurementRecord(
    string Tool,
    IReadOnlyList<Point2> Points,
    string Colour,
    string? Name,
    string? View = null);
