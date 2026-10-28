using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Serialization;
using Sirius.Core.Shapes;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Polar to Linear: a ring of the image unwrapped into a strip, from a drawn
/// band or around a circle found upstream.
/// </summary>
/// <remarks>
/// Every test unwraps one picture: black, with a white ring between radius
/// 50 and 60 about (100, 100). Where that ring lands in the strip says
/// whether the band was where it should be.
/// </remarks>
public class PolarUnwrapTests
{
    public PolarUnwrapTests() => TestCatalog.Ensure();

    [Fact]
    public void A_drawn_band_unwraps_into_a_strip_angle_across_radius_down()
    {
        var strip = Unwrap(new PolarToLinearNode { Band = new Ring(100, 100, 40, 70) });

        // 30 pixels deep, and one pixel per pixel of arc at radius 55.
        Assert.Equal(30, strip.Height);
        Assert.Equal((int)Math.Round(2 * Math.PI * 55), strip.Width);

        // Radius 55 is the white ring; radius 42 is the black inside it.
        Assert.True(RowMean(strip, 14) > 200, "the ring should be white across the strip");
        Assert.True(RowMean(strip, 2) < 20, "inside the ring should be black");
    }

    /// <summary>
    /// The point of wiring a circle in: the band sits on the edge that was
    /// found, so whatever is on that edge is unwrapped wherever the part is.
    /// </summary>
    [Fact]
    public void A_wired_circle_puts_the_band_on_its_edge()
    {
        var strip = Unwrap(new PolarToLinearNode { BandWidth = 8 }, new CircleShape(new Point2(100, 100), 55));

        Assert.Equal(8, strip.Height);
        Assert.True(CvInvoke.Mean(strip.Mat).V0 > 200, "a band on the ring should be all ring");
    }

    [Fact]
    public void An_offset_moves_the_band_in_from_the_circle()
    {
        var node = new PolarToLinearNode { BandWidth = 8, Offset = -20 };

        var strip = Unwrap(node, new CircleShape(new Point2(100, 100), 55));

        Assert.True(CvInvoke.Mean(strip.Mat).V0 < 20, "twenty pixels inside the ring is black");
    }

    [Fact]
    public void A_wired_point_moves_the_drawn_band_there()
    {
        var node = new PolarToLinearNode { Band = new Ring(30, 30, 40, 70) };

        var strip = Unwrap(node, new PointShape(new Point2(100, 100)));

        Assert.Equal(30, strip.Height);
        Assert.True(RowMean(strip, 14) > 200);
    }

    [Fact]
    public void A_sweep_unwraps_only_part_of_the_ring()
    {
        var strip = Unwrap(new PolarToLinearNode { Band = new Ring(100, 100, 40, 70), SweepAngle = 90 });

        Assert.Equal((int)Math.Round(Math.PI / 2 * 55), strip.Width);
    }

    [Fact]
    public void With_no_band_it_unwraps_the_largest_circle_that_fits()
    {
        var strip = Unwrap(new PolarToLinearNode());

        Assert.Equal(100, strip.Height);
        Assert.Equal((int)Math.Round(2 * Math.PI * 50), strip.Width);
    }

    [Fact]
    public void The_band_it_used_comes_out_to_be_drawn_on_the_image()
    {
        var (_, band) = Run(new PolarToLinearNode { BandWidth = 10 }, new CircleShape(new Point2(100, 100), 55));

        var circles = band.Shapes.OfType<CircleShape>().Select(circle => circle.Radius).Order().ToArray();
        Assert.Equal(new[] { 50.0, 60.0 }, circles);
        Assert.Single(band.Shapes.OfType<LineShape>());
    }

    [Fact]
    public void A_band_too_large_to_unwrap_fails_with_a_reason()
    {
        var node = new PolarToLinearNode { Band = new Ring(100, 100, 0, 5_000_000) };

        var graph = Graph(node, null, out _);
        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Error, node.Status);
        Assert.Contains("too large", node.StatusMessage);
    }

    /// <summary>
    /// Set up the way Find Circle is, so the same ring is drawn on the image
    /// the same way and can be wired the same way.
    /// </summary>
    [Fact]
    public void The_band_is_the_same_drawable_ring_as_find_circles()
    {
        var polar = NodeCatalog.Describe(typeof(PolarToLinearNode)).Parameters.Single(p => p.Name == "Band");
        var find = NodeCatalog.Describe(typeof(FindCircleNode)).Parameters.Single(p => p.Name == "Band");

        Assert.Equal(find.ValueType, polar.ValueType);
        Assert.Equal(ParamEditor.Ring, polar.Editor);
        Assert.Equal(find.Attribute.Connectable, polar.Attribute.Connectable);
    }

    /// <summary>
    /// A recipe from before the band: its centre and radius become the band,
    /// and the setting that no longer exists goes without a warning.
    /// </summary>
    [Fact]
    public void An_older_recipes_centre_and_radius_become_the_band()
    {
        const string json = """
            {
              "version": 2,
              "nodes": [
                {
                  "id": "a",
                  "type": "transform.polarToLinear",
                  "params": { "CentreX": 120, "CentreY": 80, "MaxRadius": 60, "Interpolation": "Linear", "Warp": "FillOutliers" }
                }
              ],
              "connections": []
            }
            """;

        var loaded = GraphSerializer.Deserialize(json);

        Assert.False(loaded.HasWarnings, string.Join(Environment.NewLine, loaded.Warnings));

        var band = ((PolarToLinearNode)loaded.Graph.Nodes.Single()).Band;
        Assert.Equal(120, band.CentreX);
        Assert.Equal(80, band.CentreY);
        Assert.Equal(60, band.OuterRadius);
    }

    // ---- helpers ------------------------------------------------------------------

    private static ImageData Unwrap(PolarToLinearNode node, Shape? around = null) => Run(node, around).Strip;

    private static (ImageData Strip, ShapeSet Band) Run(PolarToLinearNode node, Shape? around = null)
    {
        var graph = Graph(node, around, out _);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.True(node.Status != NodeStatus.Error, node.StatusMessage);

        // Copied out: the scheduler and its cache go when this returns.
        var strip = scheduler.GetOutput<ImageData>(node.ImageOut)!.Clone();
        var band = scheduler.GetOutput<ShapeSet>(node.BandOut)!;

        return (strip, band);
    }

    private static VisionGraph Graph(PolarToLinearNode node, Shape? around, out RingImageNode source)
    {
        var graph = new VisionGraph();
        source = new RingImageNode();

        graph.Add(source);
        graph.Add(node);
        graph.TryConnect(source.ImageOut, node.ImageIn);

        if (around is not null)
        {
            var shapes = new ShapesNode(around);
            graph.Add(shapes);
            graph.TryConnect(shapes.Out, node.CircleIn);
        }

        return graph;
    }

    private static double RowMean(ImageData image, int row)
    {
        using var line = new Mat(image.Mat, new System.Drawing.Rectangle(0, row, image.Width, 1));
        return CvInvoke.Mean(line).V0;
    }

    private static GraphScheduler CreateScheduler(VisionGraph graph)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return new GraphScheduler(graph);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Black, with a white ring between radius 50 and 60 about (100, 100).</summary>
    private sealed class RingImageNode : VisionNode
    {
        public RingImageNode() => ImageOut = AddOutput<ImageData>("Out");

        public OutputPort ImageOut { get; }

        protected override void OnEvaluate(EvalContext context)
        {
            var mat = new Mat(200, 200, DepthType.Cv8U, 1);
            mat.SetTo(new MCvScalar(0));
            CvInvoke.Circle(mat, new System.Drawing.Point(100, 100), 55, new MCvScalar(255), 10);
            context.Set(ImageOut, ImageData.Adopt(mat));
        }
    }

    private sealed class ShapesNode : VisionNode
    {
        private readonly Shape _shape;

        public ShapesNode(Shape shape)
        {
            _shape = shape;
            Out = AddOutput<ShapeSet>("Out");
        }

        public OutputPort Out { get; }

        protected override void OnEvaluate(EvalContext context) =>
            context.Set(Out, new ShapeSet([_shape], 200, 200));
    }
}
