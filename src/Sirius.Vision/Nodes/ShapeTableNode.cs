using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Vision.Nodes;

/// <summary>
/// Shows a set of shapes as a table of measurements, in its own panel.
/// </summary>
/// <remarks>
/// <para>
/// Terminal, like the viewer and the histogram: it reports rather than
/// transforms. To both see the shapes on the image and read their numbers,
/// branch the producer's Shapes output to this and to a viewer - which the
/// graph supports directly.
/// </para>
/// <para>
/// The columns come from whatever measurements the shapes actually carry, so
/// this works for any producer, not only the blob finder.
/// </para>
/// </remarks>
[VisionNode(
    "Shape Table",
    TypeKey = "analysis.shapeTable",
    Category = NodeCategory.Analysis,
    Icon = "ph_shape_table",
    Description = "Lists shapes and their measurements in a table panel.")]
public sealed class ShapeTableNode : VisionNode
{
    public ShapeTableNode()
    {
        ShapesIn = AddInput<ShapeSet>("In");
    }

    public InputPort ShapesIn { get; }

    protected override void OnEvaluate(EvalContext context)
    {
        // Nothing to compute. The required input is enough to put the node in
        // a Ready state, and the panel reads the set straight from upstream.
    }
}
