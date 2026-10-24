using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>
/// Marks the image a graph is meant to display. The viewer panel follows the
/// active viewer node, which is what gives a branching graph a well-defined
/// "result" without forcing the operator to keep selecting a node.
/// </summary>
[VisionNode(
    "Image Viewer",
    TypeKey = "output.viewer",
    Category = NodeCategory.Output,
    Icon = "ph_image_viewer",
    Description = "Terminal node; its input is shown in its own image panel.")]
public sealed class ViewerNode : VisionNode, IGrowingPorts
{
    /// <summary>The first shapes socket, named as it always was.</summary>
    private const string FirstShapes = "Shapes";

    public ViewerNode()
    {
        ImageIn = AddInput<ImageData>("In");
        ShapesIn = AddInput<ShapeSet>(FirstShapes, isRequired: false);
    }

    public InputPort ImageIn { get; }

    /// <summary>
    /// Geometry to draw over the image - found blobs, circles, lines.
    /// </summary>
    /// <remarks>
    /// Optional, and the whole of what an "annotation" is here: a shape set
    /// connected to a viewer. Nothing about drawing is special-cased, so any
    /// node that emits shapes can be shown without the viewer knowing what
    /// produced them.
    ///
    /// The first of several - see <see cref="ShapeInputs"/>.
    /// </remarks>
    public InputPort ShapesIn { get; }

    /// <summary>
    /// Every shapes socket, in the order they are drawn.
    /// </summary>
    /// <remarks>
    /// One image usually wants more than one annotation on it: the blobs
    /// that passed in green, the ones that failed in red, the fitted circle
    /// over both. Each producer is styled separately and lands in its own
    /// socket, which keeps a <see cref="Sirius.Core.Shapes.Shape"/>'s own
    /// colour and stroke intact - they are per shape, so merging sets costs
    /// nothing.
    /// </remarks>
    public IReadOnlyList<InputPort> ShapeInputs => Inputs
        .Where(port => port.DataType == typeof(ShapeSet) && !IsParameterPort(port))
        .ToArray();

    /// <summary>
    /// Keeps exactly one empty shapes socket at the end.
    /// </summary>
    /// <remarks>
    /// Derived from the connections rather than configured or saved, so
    /// there is no stored count that can disagree with the wiring. Growth
    /// is capped: past a dozen overlays the picture is unreadable anyway,
    /// and a runaway loop adding sockets for ever would be worse than a
    /// refusal.
    /// </remarks>
    public bool Fit()
    {
        const int limit = 12;

        var changed = false;
        var sockets = ShapeInputs;
        var spare = sockets.Count(port => !port.IsConnected);

        // Trim from the end: a gap in the middle is left alone, because the
        // socket above it is still in use and renumbering would move
        // somebody's wire to a different row.
        for (var i = sockets.Count - 1; i > 0 && spare > 1; i--)
        {
            if (sockets[i].IsConnected || !RemoveInput(sockets[i]))
                continue;

            spare--;
            changed = true;
        }

        if (spare == 0 && sockets.Count < limit)
        {
            Grow();
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Builds sockets up to the one a saved recipe names.
    /// </summary>
    public InputPort? EnsureInput(string name)
    {
        if (!TryReadSlot(name, out var slot) || slot > 32)
            return null;

        while (ShapeInputs.Count < slot)
            Grow();

        return FindInput(name);
    }

    /// <summary>
    /// Appends one shapes socket, immediately below the last one.
    /// </summary>
    /// <remarks>
    /// Named for its position, and the first keeps the bare name "Shapes" it
    /// has always had - so a recipe saved before this existed still finds
    /// its port, and one saved after it is readable rather than a list of
    /// "Shapes" repeated.
    /// </remarks>
    private void Grow()
    {
        var sockets = ShapeInputs;
        var last = sockets[^1];
        var slot = sockets.Count + 1;

        InsertInput<ShapeSet>(
            Inputs.ToList().IndexOf(last) + 1,
            $"{FirstShapes} {slot}",
            isRequired: false);
    }

    /// <summary>Reads "Shapes 3" as 3, and the bare "Shapes" as 1.</summary>
    private static bool TryReadSlot(string name, out int slot)
    {
        slot = 0;

        if (string.Equals(name, FirstShapes, StringComparison.Ordinal))
        {
            slot = 1;
            return true;
        }

        if (!name.StartsWith(FirstShapes + " ", StringComparison.Ordinal))
            return false;

        return int.TryParse(name[(FirstShapes.Length + 1)..], out slot) && slot > 1;
    }

    /// <summary>
    /// Terminal: a viewer displays an image, it does not transform one, so
    /// there is nothing meaningful to pass on. Watching a mid-graph image is
    /// done by branching the producing output to a viewer as well as to the
    /// next stage, which the graph supports directly.
    /// </summary>
    protected override void OnEvaluate(EvalContext context)
    {
        // Nothing to compute: the panel reads the image straight from the
        // upstream output. The one thing worth checking is that the overlays
        // agree with each other, because a set measured in another image
        // size draws in the wrong place and looks like a bad fit rather than
        // a wiring mistake.
        var sets = ShapeInputs
            .Select(context.Get<ShapeSet>)
            .Where(set => set is { Count: > 0 })
            .Cast<ShapeSet>()
            .ToArray();

        if (sets.Length < 2)
            return;

        var frame = sets.FirstOrDefault(set => set.FrameWidth > 0 && set.FrameHeight > 0);

        if (frame is null)
            return;

        var strays = sets.Count(set => !set.MatchesFrame(frame.FrameWidth, frame.FrameHeight));

        if (strays > 0)
        {
            context.Warn(
                $"{strays} shape set(s) are not drawn: they were measured in a different image " +
                $"size to the first ({frame.FrameWidth} x {frame.FrameHeight}).");
        }
    }
}
