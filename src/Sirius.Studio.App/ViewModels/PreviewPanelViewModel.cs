using System.Windows;
using Sirius.Core.Graph;
using Sirius.Core.Shapes;
using Sirius.Vision;

namespace Sirius.Studio.App.ViewModels;

/// <summary>
/// A viewer opened on one port of one node.
/// </summary>
/// <remarks>
/// <para>
/// Bound to a <b>port</b>, not to a node, which is what makes it
/// unambiguous. A node can have several image ports - Find Blobs has an
/// image out, a count and a shape set - so "preview the node" has no answer
/// for it while "preview Find Blobs.Shapes" does. More importantly, a
/// preview shows by construction the pixels that port carries, so a point
/// clicked in it is a point in that port's coordinate space.
/// </para>
/// <para>
/// That is the fault it exists to fix. Region editing elsewhere pushes the
/// selected node's region parameter to every viewer at once and lets the
/// operator draw on whichever looks useful - which is correct only while no
/// node between them rotates, crops or resizes the image.
/// </para>
/// <para>
/// Unlike every other panel, a preview exists because somebody opened it
/// rather than because its node exists, so the panel sweep must not recreate
/// one that was closed. It is removed when its node is deleted, like the
/// rest.
/// </para>
/// </remarks>
public sealed class PreviewPanelViewModel : ImagePanelViewModel
{
    private readonly Port _port;
    private readonly InputPort? _backdrop;

    /// <param name="node">The node the port belongs to.</param>
    /// <param name="port">The port being previewed.</param>
    /// <param name="backdrop">
    /// For a shape port, the image input the shapes are drawn over - there is
    /// nothing else to draw them on. Null for an image port.
    /// </param>
    /// <param name="selection">Shared blob selection, keyed by producing port.</param>
    public PreviewPanelViewModel(
        VisionNode node,
        Port port,
        InputPort? backdrop,
        ShapeSelectionService selection)
        : base(node, selection, port.Name)
    {
        _port = port;
        _backdrop = backdrop;
    }

    /// <summary>The port this preview is showing.</summary>
    public Port Port => _port;

    /// <summary>
    /// Whether this preview carries the measurements as well as the picture.
    /// </summary>
    /// <remarks>
    /// A shape set is data, and drawing it on the image answers only half of
    /// what is asked of it: the picture says <em>where</em> they are, the
    /// table says <em>what</em> they are. Both are shown, because choosing
    /// between them at the moment the window opens is a choice nobody has
    /// the information to make yet.
    ///
    /// They are two views of one thing rather than two panels: the selection
    /// is keyed by the producing port, so a row and a blob highlight each
    /// other without either knowing about the other.
    /// </remarks>
    public bool ShowsShapeTable => IsShapePort;

    /// <summary>
    /// How much of the window the table takes, or nothing at all when there
    /// is no table.
    /// </summary>
    /// <remarks>
    /// A collapsed control still occupies its row, so the row has to go as
    /// well or an image preview would open with a third of it blank.
    /// </remarks>
    public GridLength TableHeight => ShowsShapeTable
        ? new GridLength(2, GridUnitType.Star)
        : new GridLength(0);

    protected override string ContentIdPrefix => "preview";

    /// <summary>
    /// Identity carries the port as well as the node, so a preview of an
    /// input and one of an output are different windows rather than one
    /// window that fights over which to show.
    /// </summary>
    public override string ContentId => $"{ContentIdPrefix}:{Node.Id}:{_port.Name}";

    /// <summary>
    /// Named for the port as well as the node, or two previews of the same
    /// node would be indistinguishable on their tabs.
    /// </summary>
    public override string Title => $"{Node.Name} · {_port.DisplayName}";

    /// <summary>
    /// Shapes shown here came from this port, when the port is a shape port.
    /// Sharing that key with a table reading the same wire is what lets a row
    /// and a blob highlight together.
    /// </summary>
    protected override OutputPort? ShapeProducer =>
        IsShapePort ? _port as OutputPort : null;

    /// <summary>
    /// A region may be drawn here only if this preview is showing the very
    /// image it is measured in.
    /// </summary>
    /// <remarks>
    /// Which means: this node's own image input. Not its output - that is
    /// the cut, in which the region has no meaning and was being drawn over
    /// the result as though it did - and not another node's anything.
    /// </remarks>
    protected override bool Accepts(ParameterViewModel? parameter) =>
        parameter is null ||
        (ReferenceEquals(parameter.Node, Node) &&
         _port is InputPort &&
         _port.DataType == typeof(ImageData));

    protected override (ImageData? Image, ShapeSet? Shapes) Read(GraphScheduler scheduler)
    {
        if (IsShapePort)
        {
            return (
                _backdrop is null ? null : scheduler.GetInput<ImageData>(_backdrop),
                ValueOf<ShapeSet>(scheduler));
        }

        return (ValueOf<ImageData>(scheduler), null);
    }

    private bool IsShapePort => _port.DataType == typeof(ShapeSet);

    /// <summary>
    /// The port's value from the last pass. An input reads what is wired into
    /// it, an output what the node produced.
    /// </summary>
    private T? ValueOf<T>(GraphScheduler scheduler) where T : class => _port switch
    {
        InputPort input => scheduler.GetInput<T>(input),
        OutputPort output => scheduler.GetOutput<T>(output),
        _ => null,
    };
}
