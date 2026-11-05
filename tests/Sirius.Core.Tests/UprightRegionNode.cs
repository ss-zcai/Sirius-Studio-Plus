using System.Drawing;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Vision;

namespace Sirius.Core.Tests;

/// <summary>
/// A node with a plain upright <see cref="Rectangle"/> region - the kind
/// Crop had.
/// </summary>
/// <remarks>
/// No built-in node has one since Crop became Region of Interest, but the
/// editor and the reference still handle the type, so the tests that cover
/// that handling need something carrying one.
/// </remarks>
internal sealed class UprightRegionNode : VisionNode
{
    private Rectangle _region = new(10, 10, 40, 20);

    public UprightRegionNode() => ImageIn = AddInput<ImageData>("In");

    public InputPort ImageIn { get; }

    [Param("Region", Editor = ParamEditor.Region, Description = "Drawable on the image viewer.")]
    public Rectangle Region
    {
        get => _region;
        set => SetParam(ref _region, value);
    }

    // Read through the context, so a wired reference places it.
    protected override void OnEvaluate(EvalContext context) => context.Param(nameof(Region), _region);
}
