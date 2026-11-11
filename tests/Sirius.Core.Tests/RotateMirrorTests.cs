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
/// Rotate and Mirror: quarter turns and mirrors of an image, and of the
/// shapes found on it.
/// </summary>
public class RotateMirrorTests
{
    public RotateMirrorTests() => TestCatalog.Ensure();

    /// <summary>
    /// Every combination, on an image small enough to write out: three
    /// wide, two high, each pixel its own number.
    ///
    ///     0 1 2
    ///     3 4 5
    /// </summary>
    [Theory]
    [InlineData(QuarterTurn.None, MirrorSides.None, "012/345")]
    [InlineData(QuarterTurn.Quarter, MirrorSides.None, "30/41/52")]
    [InlineData(QuarterTurn.Half, MirrorSides.None, "543/210")]
    [InlineData(QuarterTurn.ThreeQuarters, MirrorSides.None, "25/14/03")]
    [InlineData(QuarterTurn.None, MirrorSides.LeftRight, "210/543")]
    [InlineData(QuarterTurn.None, MirrorSides.TopBottom, "345/012")]
    [InlineData(QuarterTurn.Quarter, MirrorSides.LeftRight, "03/14/25")]
    [InlineData(QuarterTurn.Quarter, MirrorSides.TopBottom, "52/41/30")]
    [InlineData(QuarterTurn.Half, MirrorSides.LeftRight, "345/012")]
    [InlineData(QuarterTurn.Half, MirrorSides.TopBottom, "210/543")]
    [InlineData(QuarterTurn.ThreeQuarters, MirrorSides.LeftRight, "52/41/30")]
    [InlineData(QuarterTurn.ThreeQuarters, MirrorSides.TopBottom, "03/14/25")]
    public void Each_turn_and_mirror_puts_every_pixel_where_it_should_be(QuarterTurn rotate, MirrorSides mirror, string expected)
    {
        var node = new RotateMirrorNode { Rotate = rotate, Mirror = mirror };

        var (image, _) = Run(node, Numbered);

        Assert.Equal(expected, Written(image.Mat));

        // And the map shapes are carried by agrees with the pixels: where it
        // says a pixel went is where that pixel's number now is.
        var map = RotateMirrorNode.Map(rotate, mirror, 3, 2);
        var pixels = (byte[,])image.Mat.GetData();

        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 3; x++)
            {
                var went = map.MapPoint(new Point2(x, y))!.Value;
                Assert.Equal((y * 3) + x, pixels[(int)went.Y, (int)went.X]);
            }
        }
    }

    /// <summary>
    /// What Polar to Linear can hand back: upside down and reading
    /// backwards at once. Turned 180 and mirrored left to right is one
    /// mirror, top to bottom.
    /// </summary>
    [Fact]
    public void A_half_turn_and_a_mirror_is_the_other_mirror()
    {
        var both = Run(new RotateMirrorNode { Rotate = QuarterTurn.Half, Mirror = MirrorSides.LeftRight }, Numbered).Image;
        var one = Run(new RotateMirrorNode { Mirror = MirrorSides.TopBottom }, Numbered).Image;

        Assert.Equal(Written(one.Mat), Written(both.Mat));
    }

    [Fact]
    public void A_quarter_turn_swaps_width_and_height_and_keeps_what_the_image_is()
    {
        ImageData Frame()
        {
            var mat = new Mat(20, 50, DepthType.Cv16U, 3);
            mat.SetTo(new MCvScalar(100, 2000, 4000));
            return ImageData.Adopt(mat, ColourSpace.Bgr, bits: 12);
        }

        var (image, _) = Run(new RotateMirrorNode { Rotate = QuarterTurn.Quarter, Mirror = MirrorSides.LeftRight }, Frame);

        Assert.Equal((20, 50), (image.Width, image.Height));
        Assert.Equal((DepthType.Cv16U, 3, 12, ColourSpace.Bgr), (image.Depth, image.Channels, image.Bits, image.Space));

        var mean = CvInvoke.Mean(image.Mat);
        Assert.Equal((100.0, 2000.0, 4000.0), (mean.V0, mean.V1, mean.V2));
    }

    /// <summary>
    /// Shapes go with the image, and stay what they were: a rectangle with
    /// level sides still has them after a quarter turn, and a circle is a
    /// circle in a mirror - not an outline of points.
    /// </summary>
    [Fact]
    public void Shapes_turn_with_the_image_and_stay_the_shapes_they_were()
    {
        var shapes = new ShapeSet(
            [
                new RectShape(new Bounds(2, 3, 10, 5)) { Label = "part", Colour = 0xFF00FF00 },
                new CircleShape(new Point2(10, 20), 4),
                new PointShape(new Point2(0, 0)),
                new LineShape(new Point2(0, 0), new Point2(39, 0)),
            ],
            40, 30);

        // A quarter turn: (x, y) goes to (29 - y, x).
        var turned = Run(new RotateMirrorNode { Rotate = QuarterTurn.Quarter }, () => Blank(40, 30), shapes).Overlay;

        Assert.Equal((30, 40), (turned.FrameWidth, turned.FrameHeight));

        var rect = Assert.IsType<RectShape>(turned.Shapes[0]);
        Assert.Equal(new Bounds(21, 2, 5, 10), rect.Rect);
        Assert.Equal(("part", 0xFF00FF00u), (rect.Label, rect.Colour));

        var circle = Assert.IsType<CircleShape>(turned.Shapes[1]);
        Assert.Equal((new Point2(9, 10), 4.0), (circle.Centre, circle.Radius));

        // The top left corner is now the top right; the top edge the right one.
        Assert.Equal(new Point2(29, 0), Assert.IsType<PointShape>(turned.Shapes[2]).At);
        var line = Assert.IsType<LineShape>(turned.Shapes[3]);
        Assert.Equal((new Point2(29, 0), new Point2(29, 39)), (line.Start, line.End));

        // The same turn, then mirrored left to right: x goes to 29 - x.
        var mirrored = Run(
            new RotateMirrorNode { Rotate = QuarterTurn.Quarter, Mirror = MirrorSides.LeftRight }, () => Blank(40, 30), shapes).Overlay;

        Assert.Equal(new Bounds(3, 2, 5, 10), Assert.IsType<RectShape>(mirrored.Shapes[0]).Rect);

        var reflected = Assert.IsType<CircleShape>(mirrored.Shapes[1]);
        Assert.Equal((new Point2(20, 10), 4.0), (reflected.Centre, reflected.Radius));
    }

    /// <summary>
    /// A turned rectangle and an ellipse lean the other way in a mirror, and
    /// a quarter further round after a quarter turn.
    /// </summary>
    [Fact]
    public void A_leaning_shape_leans_the_other_way_in_a_mirror()
    {
        var shapes = new ShapeSet(
            [
                new OrientedRectShape(new Point2(20, 15), 10, 4, 30),
                new EllipseShape(new Point2(8, 8), 6, 2, 30),
            ],
            40, 30);

        var mirrored = Run(new RotateMirrorNode { Mirror = MirrorSides.LeftRight }, () => Blank(40, 30), shapes).Overlay;

        var box = Assert.IsType<OrientedRectShape>(mirrored.Shapes[0]);
        Assert.Equal(new Point2(19, 15), box.Centre);
        Assert.Equal((10.0, 4.0), (box.Width, box.Height));
        Assert.Equal(150, box.AngleDegrees, 6);

        var ellipse = Assert.IsType<EllipseShape>(mirrored.Shapes[1]);
        Assert.Equal(new Point2(31, 8), ellipse.Centre);
        Assert.Equal((6.0, 2.0), (ellipse.MajorAxis, ellipse.MinorAxis));
        Assert.Equal(150, ellipse.Angle, 6);

        var turned = Run(new RotateMirrorNode { Rotate = QuarterTurn.Quarter }, () => Blank(40, 30), shapes).Overlay;

        Assert.Equal(120, Assert.IsType<OrientedRectShape>(turned.Shapes[0]).AngleDegrees, 6);
        Assert.Equal(120, Assert.IsType<EllipseShape>(turned.Shapes[1]).Angle, 6);
    }

    [Fact]
    public void It_is_in_the_toolbox_under_Basic_and_a_recipe_keeps_its_settings()
    {
        var descriptor = NodeCatalog.FindByTypeKey("basic.rotateMirror")!;

        Assert.Equal("Rotate and Mirror", descriptor.DisplayName);
        Assert.Equal(NodeCategory.Basic, descriptor.Category);
        Assert.Equal(new[] { "Rotate", "Mirror" }, descriptor.Parameters.Select(parameter => parameter.Name));

        // As the properties panel lists them.
        Assert.Equal(
            new[] { "None", "90°", "180°", "270°" },
            Enum.GetValues<QuarterTurn>().Select(value => TypeNames.OptionName(value)));
        Assert.Equal(
            new[] { "None", "Left ↔ Right", "Top ↔ Bottom" },
            Enum.GetValues<MirrorSides>().Select(value => TypeNames.OptionName(value)));

        var graph = new VisionGraph();
        graph.Add(new RotateMirrorNode { Rotate = QuarterTurn.ThreeQuarters, Mirror = MirrorSides.TopBottom });

        var loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));

        Assert.False(loaded.HasWarnings, string.Join(Environment.NewLine, loaded.Warnings));
        var reopened = Assert.IsType<RotateMirrorNode>(Assert.Single(loaded.Graph.Nodes));
        Assert.Equal((QuarterTurn.ThreeQuarters, MirrorSides.TopBottom), (reopened.Rotate, reopened.Mirror));
    }

    // ---- helpers ------------------------------------------------------------------

    private static ImageData Numbered()
    {
        var mat = new Mat(2, 3, DepthType.Cv8U, 1);
        mat.SetTo(new byte[] { 0, 1, 2, 3, 4, 5 });
        return ImageData.Adopt(mat);
    }

    private static ImageData Blank(int width, int height)
    {
        var mat = new Mat(height, width, DepthType.Cv8U, 1);
        mat.SetTo(new MCvScalar(0));
        return ImageData.Adopt(mat);
    }

    /// <summary>An image's rows as digits, top to bottom: "012/345".</summary>
    private static string Written(Mat mat)
    {
        var pixels = (byte[,])mat.GetData();

        return string.Join("/", Enumerable.Range(0, mat.Rows).Select(
            y => string.Concat(Enumerable.Range(0, mat.Cols).Select(x => pixels[y, x].ToString()))));
    }

    private static (ImageData Image, ShapeSet Overlay) Run(RotateMirrorNode node, Func<ImageData> frame, ShapeSet? shapes = null)
    {
        var graph = new VisionGraph();
        var source = new SourceNode<ImageData>(frame);

        graph.Add(source);
        graph.Add(node);
        Assert.True(graph.TryConnect(source.Out, node.ImageIn).Success);

        if (shapes is not null)
        {
            var found = new SourceNode<ShapeSet>(() => shapes);
            graph.Add(found);
            Assert.True(graph.TryConnect(found.Out, node.OverlayIn).Success);
        }

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        GraphScheduler scheduler;

        try
        {
            scheduler = new GraphScheduler(graph);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        using (scheduler)
        {
            scheduler.RunAsync().GetAwaiter().GetResult();

            Assert.True(node.Status == NodeStatus.Ready, node.StatusMessage);

            // Copies, so they outlive the scheduler that owns the originals.
            var image = scheduler.GetOutput<ImageData>(node.ImageOut)!;
            return (ImageData.CopyOf(image.Mat, image.Space, image.Bits), scheduler.GetOutput<ShapeSet>(node.OverlayOut)!);
        }
    }

    private sealed class SourceNode<T> : VisionNode
        where T : class
    {
        private readonly Func<T> _value;

        public SourceNode(Func<T> value)
        {
            _value = value;
            Out = AddOutput<T>("Out");
        }

        public OutputPort Out { get; }

        protected override void OnEvaluate(EvalContext context)
        {
            if (_value() is IDisposable owned)
                context.Set(Out, owned);
            else
                context.Forward(Out, _value());
        }
    }
}
