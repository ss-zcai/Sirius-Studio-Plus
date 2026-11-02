using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Graph.Undo;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Geometry a wire moved: shown where the node looked, and drawn over in
/// the settings that put it there.
/// </summary>
/// <remarks>
/// The case that started it: a fitted circle wired into Ring Calipers to
/// find a second circle a hundred pixels inside it. The image showed the
/// band drawn by hand; the node searched forty pixels round the fitted
/// circle's own edge; and the only sign was "no edge was found".
/// </remarks>
public class PlacementTests
{
    public PlacementTests() => TestCatalog.Ensure();

    /// <summary>The circle a fit would hand on: the disc's own edge, radius 80 at (200, 200).</summary>
    private static readonly CircleShape Fitted = new(new Point2(200, 200), 80);

    // ---- what a run reports ---------------------------------------------------

    [Fact]
    public void A_wired_circle_places_the_band_round_it()
    {
        var calipers = new RingCalipersNode { Band = new Ring(50, 50, 10, 20), BandWidth = 30, Offset = -10 };
        using var rig = Wired(calipers, Fitted);

        var placement = Assert.Contains(nameof(RingCalipersNode.Band), calipers.Placements);

        Assert.Equal(new Ring(200, 200, 55, 85), placement.Value);
        Assert.Contains("Offset and Band Width", placement.Note);
    }

    [Fact]
    public void A_drawn_band_with_nothing_wired_is_not_placed()
    {
        var calipers = new RingCalipersNode { Band = new Ring(200, 200, 60, 100) };
        using var rig = Wired(calipers, shape: null);

        Assert.Empty(calipers.Placements);
    }

    [Fact]
    public void Unwiring_the_circle_clears_the_placement()
    {
        var calipers = new RingCalipersNode { Band = new Ring(200, 200, 60, 100) };
        using var rig = Wired(calipers, Fitted);

        Assert.NotEmpty(calipers.Placements);

        rig.Graph.Disconnect(calipers.CircleIn.Connection!);
        rig.Run();

        Assert.Empty(calipers.Placements);
    }

    [Fact]
    public void A_wired_point_moves_the_drawn_band()
    {
        var calipers = new RingCalipersNode { Band = new Ring(50, 50, 60, 100) };
        using var rig = Wired(calipers, new PointShape(new Point2(200, 200)));

        var placement = calipers.Placements[nameof(RingCalipersNode.Band)];

        Assert.Equal(new Ring(200, 200, 60, 100), placement.Value);
        Assert.Null(placement.Redraw);
    }

    [Fact]
    public void Circle_defects_reports_its_band_too()
    {
        var defects = new CircleDefectsNode { BandWidth = 20 };
        using var rig = Wired(defects, defects.ImageIn, defects.CircleIn, Fitted);

        Assert.Equal(new Ring(200, 200, 70, 90), defects.Placements[nameof(CircleDefectsNode.Band)].Value);
    }

    /// <summary>Nothing drawn unwraps the largest circle that fits, and the image should say so.</summary>
    [Fact]
    public void Polar_to_linear_shows_the_band_it_chose_when_none_was_drawn()
    {
        var unwrap = new PolarToLinearNode();
        using var rig = Wired(unwrap, unwrap.ImageIn, unwrap.CircleIn, shape: null);

        Assert.Equal(new Ring(200, 200, 0, 200), unwrap.Placements[nameof(PolarToLinearNode.Band)].Value);
    }

    /// <summary>
    /// The other tools a shape can place: Find Line's region from a wired
    /// line, which the image showed as drawn before.
    /// </summary>
    [Fact]
    public void A_shape_wired_into_a_region_places_it()
    {
        var finder = new FindLineNode { Region = new OrientedRect(50, 50, 40, 30, 0) };
        var graph = new VisionGraph();
        var source = new PictureNode(Disc);
        var line = new ShapesNode([new LineShape(new Point2(150, 120), new Point2(250, 120))]);
        graph.Add(source);
        graph.Add(line);
        graph.Add(finder);
        graph.TryConnect(source.Out, finder.ImageIn);
        graph.TryConnect(line.Out, finder.FindInput(nameof(FindLineNode.Region))!);

        using var rig = Run(graph);

        var placed = Assert.IsType<OrientedRect>(finder.Placements[nameof(FindLineNode.Region)].Value);

        // Where the line is and how long; how wide is the drawing's.
        Assert.Equal(new OrientedRect(200, 120, 100, 30, 0), placed);
    }

    // ---- drawing over it --------------------------------------------------------

    /// <summary>
    /// The fix itself: drawing over a band round a wired circle sets its
    /// offset and width from the circle, and the circle keeps the centre.
    /// </summary>
    [Fact]
    public void Drawing_over_a_placed_band_sets_offset_and_width()
    {
        var calipers = new RingCalipersNode { Band = new Ring(50, 50, 10, 20), BandWidth = 40 };
        using var rig = Wired(calipers, Fitted);
        var band = Band(calipers);

        Assert.Equal(new Ring(200, 200, 60, 100), band.ShownRing);

        // Dragged inward, and off-centre by a slip of the hand.
        band.ShownRing = new Ring(203, 198, 50, 70);

        Assert.Equal(-20, calipers.Offset);
        Assert.Equal(20, calipers.BandWidth);
        Assert.Equal(new Ring(50, 50, 10, 20), calipers.Band);
        Assert.Equal(new Ring(200, 200, 50, 70), band.ShownRing);

        rig.Run();

        Assert.Equal(new Ring(200, 200, 50, 70), band.ShownRing);
    }

    /// <summary>The band follows the circle when it moves, keeping its offset and width.</summary>
    [Fact]
    public void The_shown_band_follows_the_circle()
    {
        var calipers = new RingCalipersNode { BandWidth = 20, Offset = 5 };
        var circle = new ShapesNode([Fitted]);
        using var rig = Wired(calipers, circle);
        var band = Band(calipers);

        circle.Shapes = [new CircleShape(new Point2(180, 210), 70)];
        rig.Run();

        Assert.Equal(new Ring(180, 210, 65, 85), band.ShownRing);
    }

    [Fact]
    public void A_wired_setting_is_not_overwritten_by_drawing()
    {
        var calipers = new RingCalipersNode { BandWidth = 40, Offset = 0 };
        using var rig = Wired(calipers, Fitted);

        var number = new NumberNode { Value = 3 };
        rig.Graph.Add(number);
        rig.Graph.TryConnect(number.ValueOut, calipers.FindInput(nameof(RingCalipersNode.Offset))!);
        rig.Run();

        Band(calipers).ShownRing = new Ring(200, 200, 50, 70);

        Assert.Equal(0, calipers.Offset);
        Assert.Equal(20, calipers.BandWidth);
    }

    [Fact]
    public void With_nothing_wired_the_drawing_is_the_band()
    {
        var calipers = new RingCalipersNode { Band = new Ring(200, 200, 60, 100) };
        using var rig = Wired(calipers, shape: null);
        var band = Band(calipers);

        band.ShownRing = new Ring(190, 190, 50, 90);

        Assert.Equal(new Ring(190, 190, 50, 90), calipers.Band);
        Assert.False(band.IsPlaced);
    }

    /// <summary>
    /// A drag over a placed band sets two settings every frame; it must
    /// still undo in one step, not one per setting per frame.
    /// </summary>
    [Fact]
    public void A_drag_over_a_placed_band_undoes_in_one_step()
    {
        var calipers = new RingCalipersNode { BandWidth = 40, Offset = 0 };
        using var rig = Wired(calipers, Fitted);

        var stack = new UndoStack(rig.Graph);
        using var recorder = new UndoRecorder(rig.Graph, stack);

        var panel = new PreviewPanelViewModel(calipers, calipers.ImageIn, null, new ShapeSelectionService())
        {
            RingParameter = Band(calipers),
            BeginEdit = label => stack.Begin(label),
        };

        Assert.Equal(new Ring(200, 200, 60, 100), panel.Ring);

        for (var inner = 59; inner >= 40; inner--)
            panel.Ring = new Ring(200, 200, inner, 100);

        Assert.Equal(60, calipers.BandWidth);
        Assert.Equal(-10, calipers.Offset);

        stack.Undo();

        Assert.Equal(40, calipers.BandWidth);
        Assert.Equal(0, calipers.Offset);
        Assert.False(stack.CanUndo);
    }

    /// <summary>The properties panel says why the box above is not what is searched.</summary>
    [Fact]
    public void The_band_says_what_placed_it()
    {
        var calipers = new RingCalipersNode();
        using var rig = Wired(calipers, Fitted);

        var band = Band(calipers);

        Assert.True(band.IsPlaced);
        Assert.Contains("wired circle", band.PlacementNote);
    }

    // ---- helpers ----------------------------------------------------------------

    private static ParameterViewModel Band(VisionNode node) =>
        new(node, node.Descriptor.Parameters.Single(p => p.Name == "Band"));

    private static Rig Wired(RingCalipersNode calipers, Shape? shape) =>
        Wired(calipers, calipers.ImageIn, calipers.CircleIn, shape);

    private static Rig Wired(RingCalipersNode calipers, ShapesNode circle) =>
        Wired(calipers, calipers.ImageIn, calipers.CircleIn, circle);

    private static Rig Wired(VisionNode node, InputPort imageIn, InputPort circleIn, Shape? shape) =>
        Wired(node, imageIn, circleIn, shape is null ? null : new ShapesNode([shape]));

    /// <summary>The disc into the node, the shape into its circle input, run once.</summary>
    private static Rig Wired(VisionNode node, InputPort imageIn, InputPort circleIn, ShapesNode? shapes)
    {
        var graph = new VisionGraph();
        var source = new PictureNode(Disc);
        graph.Add(source);
        graph.Add(node);
        graph.TryConnect(source.Out, imageIn);

        if (shapes is not null)
        {
            graph.Add(shapes);
            graph.TryConnect(shapes.Out, circleIn);
        }

        return Run(graph);
    }

    private static Rig Run(VisionGraph graph)
    {
        // No context, so the run reports straight back rather than posting
        // to a UI thread a test does not have.
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);

        try
        {
            var rig = new Rig(graph, new GraphScheduler(graph));
            rig.Run();
            return rig;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed record Rig(VisionGraph Graph, GraphScheduler Scheduler) : IDisposable
    {
        public void Run() => Scheduler.RunAsync().GetAwaiter().GetResult();

        public void Dispose() => Scheduler.Dispose();
    }

    /// <summary>A white disc of radius 80 at (200, 200) on 400 x 400.</summary>
    private static Mat Disc()
    {
        var mat = new Mat(400, 400, DepthType.Cv8U, 1);
        mat.SetTo(new MCvScalar(0));
        CvInvoke.Circle(mat, new System.Drawing.Point(200, 200), 80, new MCvScalar(255), -1);
        return mat;
    }

    private sealed class PictureNode : VisionNode
    {
        private readonly Func<Mat> _picture;

        public PictureNode(Func<Mat> picture)
        {
            _picture = picture;
            Out = AddOutput<ImageData>("Out");
        }

        public OutputPort Out { get; }

        protected override void OnEvaluate(EvalContext context) => context.Set(Out, ImageData.Adopt(_picture()));
    }

    private sealed class ShapesNode : VisionNode
    {
        private IReadOnlyList<Shape> _shapes;

        public ShapesNode(IReadOnlyList<Shape> shapes)
        {
            _shapes = shapes;
            Out = AddOutput<ShapeSet>("Out");
        }

        public OutputPort Out { get; }

        public IReadOnlyList<Shape> Shapes
        {
            get => _shapes;
            set
            {
                _shapes = value;
                Invalidate();
            }
        }

        protected override void OnEvaluate(EvalContext context) => context.Set(Out, new ShapeSet(_shapes, 400, 400));
    }
}
