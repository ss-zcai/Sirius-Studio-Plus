using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using Sirius.Core.Graph;
using Sirius.Core.Graph.Undo;
using Sirius.Core.Nodes;
using Sirius.Core.Serialization;
using Sirius.Core.Shapes;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// A tool's regions following a found result: drawn once relative to it,
/// carried with it from part to part, turned with it where it has a
/// direction.
/// </summary>
/// <remarks>
/// The picture throughout: a black frame with a white patch 80 pixels to
/// the right of a reference box, at whatever place and angle the box is
/// given. Region Stats measures a region drawn on the patch, so its mean
/// says at once whether the region went where the patch went.
/// </remarks>
public class ReferenceTests
{
    public ReferenceTests() => TestCatalog.Ensure();

    private static readonly ReferencePose Taught = new(100, 100, 0);

    /// <summary>Drawn on the patch, with the reference where it was taught.</summary>
    private static readonly OrientedRect OnThePatch = new(180, 100, 30, 14, 0);

    // ---- where a reference is ----------------------------------------------------

    [Fact]
    public void A_circle_gives_its_centre_and_no_angle() =>
        Assert.Equal(new ReferencePose(50, 60, null), ReferencePose.Of(new CircleShape(new Point2(50, 60), 10)));

    [Fact]
    public void A_turned_box_gives_its_centre_and_angle() =>
        Assert.Equal(
            new ReferencePose(50, 60, 25),
            ReferencePose.Of(new OrientedRectShape(new Point2(50, 60), 40, 20, 25)));

    [Fact]
    public void A_line_gives_its_middle_and_direction()
    {
        var pose = ReferencePose.Of(new LineShape(new Point2(0, 0), new Point2(100, 100)));

        Assert.Equal((50, 50), (pose.X, pose.Y));
        Assert.Equal(45, pose.Angle!.Value, 9);
    }

    /// <summary>A nearly round fitted ellipse's angle swings from part to part; following it would jitter.</summary>
    [Fact]
    public void An_ellipse_gives_its_centre_only() =>
        Assert.Null(ReferencePose.Of(new EllipseShape(new Point2(5, 5), 40, 38, 70)).Angle);

    /// <summary>A line read from the other end is the same line, not a half turn.</summary>
    [Theory]
    [InlineData(10, 190, 0)]
    [InlineData(10, 40, 30)]
    [InlineData(80, -80, 20)]
    [InlineData(0, 135, -45)]
    public void A_turn_is_the_smaller_reading(double from, double to, double expected) =>
        Assert.Equal(expected, ReferencePose.Turn(new(0, 0, from), new(0, 0, to)), 9);

    [Fact]
    public void A_pose_survives_the_recipe()
    {
        Assert.True(ReferencePose.TryParse(new ReferencePose(1.5, -2, 30).ToString(), out var turned));
        Assert.Equal(new ReferencePose(1.5, -2, 30), turned);

        Assert.True(ReferencePose.TryParse(new ReferencePose(3, 4, null).ToString(), out var plain));
        Assert.Null(plain.Angle);

        Assert.False(ReferencePose.TryParse("", out _));
    }

    // ---- which tools have one ----------------------------------------------------

    [Fact]
    public void Every_tool_with_a_region_has_a_reference_and_no_other_does()
    {
        foreach (var descriptor in NodeCatalog.All.Where(d => d.IsInstantiable && !d.IsRetired))
        {
            var node = descriptor.CreateInstance();
            var reference = node.Inputs.FirstOrDefault(port => port.Name == VisionNode.ReferenceInputName);

            if (descriptor.HasRegions)
            {
                Assert.True(ReferenceEquals(reference, node.RegionReference), $"{descriptor.DisplayName} has no Reference");
                Assert.Contains(descriptor.Parameters, p => p.Name == nameof(VisionNode.FollowReferenceAngle));
            }
            else
            {
                Assert.Null(node.RegionReference);
                Assert.DoesNotContain(descriptor.Parameters, p => p.Name == nameof(VisionNode.FollowReferenceAngle));
            }
        }
    }

    /// <summary>Calibrate's Reference is a different thing, and keeps its name to itself.</summary>
    [Fact]
    public void Calibrates_own_reference_is_left_alone()
    {
        var calibrate = new CalibrateNode();

        Assert.Null(((VisionNode)calibrate).RegionReference);
        Assert.Single(calibrate.Inputs, port => port.Name == "Reference");
        Assert.Same(calibrate.ReferenceIn, calibrate.Inputs.Single(port => port.Name == "Reference"));
    }

    // ---- following ----------------------------------------------------------------

    [Fact]
    public void Wiring_a_reference_moves_nothing()
    {
        using var rig = new Rig(Taught);

        Assert.Equal(255, rig.Mean, 0);
        Assert.Equal(OnThePatch, rig.Placed);
        Assert.Equal(Taught.ToString(), rig.Stats.TaughtReference);
    }

    /// <summary>The point of it: the part moves and turns, and the region goes with it.</summary>
    [Fact]
    public void The_region_follows_where_the_reference_goes_and_how_it_turns()
    {
        using var rig = new Rig(Taught);

        rig.MoveTo(new ReferencePose(200, 250, 30));

        Assert.Equal(255, rig.Mean, 0);

        var placed = rig.Placed;
        Assert.Equal(200 + (80 * Math.Cos(Math.PI / 6)), placed.CentreX, 6);
        Assert.Equal(250 + (80 * Math.Sin(Math.PI / 6)), placed.CentreY, 6);
        Assert.Equal(30, placed.Angle, 9);
    }

    [Fact]
    public void With_the_angle_switch_off_it_only_follows_where()
    {
        using var rig = new Rig(Taught);
        rig.Stats.FollowReferenceAngle = false;

        rig.MoveTo(new ReferencePose(200, 250, 30));

        Assert.Equal(new OrientedRect(280, 250, 30, 14, 0), rig.Placed);
        Assert.True(rig.Mean < 255, "the region should have missed the turned patch");
    }

    /// <summary>A circle has no direction, so a turned part's region only moves.</summary>
    [Fact]
    public void A_reference_with_no_direction_only_moves_it()
    {
        using var rig = new Rig(Taught, pose => new CircleShape(new Point2(pose.X, pose.Y), 20));

        rig.MoveTo(new ReferencePose(150, 130, 30));

        Assert.Equal(new OrientedRect(230, 130, 30, 14, 0), rig.Placed);
    }

    /// <summary>
    /// The part is not where the region expects it, and searching the empty
    /// fixture instead would pass parts nobody looked at.
    /// </summary>
    [Fact]
    public void Nothing_on_the_reference_fails_the_tool()
    {
        using var rig = new Rig(Taught);

        rig.Reference.Shapes = [];
        rig.Run();

        Assert.Equal(NodeStatus.Error, rig.Stats.Status);
        Assert.Contains("Nothing arrived on Reference", rig.Stats.StatusMessage);
    }

    [Fact]
    public void Unwiring_forgets_where_it_was_taught_and_rewiring_teaches_again()
    {
        using var rig = new Rig(Taught);

        rig.Graph.Disconnect(rig.Stats.RegionReference!.Connection!);
        Assert.Equal(string.Empty, rig.Stats.TaughtReference);

        rig.Reference.Shapes = [Box(new ReferencePose(300, 300, 0))];
        rig.Graph.TryConnect(rig.Reference.Out, rig.Stats.RegionReference!);
        rig.Run();

        Assert.Equal(new ReferencePose(300, 300, 0).ToString(), rig.Stats.TaughtReference);
        Assert.Equal(OnThePatch, rig.Placed);
    }

    /// <summary>
    /// Saved with the recipe, and not lost on opening it - the wires go in
    /// after the settings, and must not count as wiring it afresh.
    /// </summary>
    [Fact]
    public void Where_it_was_taught_is_kept_in_the_recipe()
    {
        // Tools a recipe can make again: blobs as the reference.
        var graph = new VisionGraph();
        var blobs = new BlobFilterNode();
        var stats = new RegionStatsNode { FollowReferenceAngle = false };
        graph.Add(blobs);
        graph.Add(stats);
        graph.TryConnect(blobs.Outputs.Single(port => port.Name == "Shapes"), stats.RegionReference!);
        stats.TaughtReference = Taught.ToString();

        var loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph)).Graph;
        var again = loaded.Nodes.OfType<RegionStatsNode>().Single();

        Assert.True(again.RegionReference!.IsConnected);
        Assert.Equal(Taught.ToString(), again.TaughtReference);
        Assert.False(again.FollowReferenceAngle);
    }

    /// <summary>
    /// Wiring is the step undo takes back. Teaching is its consequence, and
    /// an undo step of its own would be re-taught by the next run.
    /// </summary>
    [Fact]
    public void Teaching_is_not_an_undo_step()
    {
        var graph = new VisionGraph();
        var stack = new UndoStack(graph);
        using var recorder = new UndoRecorder(graph, stack);
        using var rig = new Rig(Taught, graph: graph);

        Assert.False(stack.CanUndo);
        Assert.Equal(Taught.ToString(), rig.Stats.TaughtReference);
    }

    [Fact]
    public void A_pasted_copy_without_its_reference_forgets_where_it_was_taught()
    {
        using var rig = new Rig(Taught);

        var copied = GraphClipboard.Copy(rig.Graph, [rig.Stats]);
        var pasted = GraphClipboard.Paste(rig.Graph, copied, offset: 40).Nodes.OfType<RegionStatsNode>().Single();

        Assert.Equal(string.Empty, pasted.TaughtReference);
        Assert.Equal(Taught.ToString(), rig.Stats.TaughtReference);
    }

    // ---- on the image -------------------------------------------------------------

    /// <summary>
    /// Drawn on a part that has moved, the region is kept relative to the
    /// reference - so it is in the same place on the part after.
    /// </summary>
    [Fact]
    public void Drawing_on_a_moved_part_keeps_the_region_relative_to_it()
    {
        using var rig = new Rig(Taught);
        rig.MoveTo(new ReferencePose(200, 100, 90));

        var region = new ParameterViewModel(
            rig.Stats, rig.Stats.Descriptor.Parameters.Single(p => p.Name == nameof(RegionStatsNode.Region)));

        // Shown where it went: straight below the reference, turned with
        // it - the box as it is before turning, and the angle beside it.
        Assert.Equal(new System.Drawing.Rectangle(185, 173, 30, 14), region.ShownRegion);
        Assert.Equal(90, region.ShownAngle, 9);

        // Drawn 10 further from the reference, along the part.
        region.ShownRegion = new System.Drawing.Rectangle(185, 183, 30, 14);

        var stored = rig.Stats.Region;
        Assert.Equal(190, stored.CentreX, 6);
        Assert.Equal(100, stored.CentreY, 6);
        Assert.Equal(0, stored.Angle, 9);
    }

    [Fact]
    public void The_angle_switch_is_offered_only_while_a_reference_is_wired()
    {
        using var rig = new Rig(Taught);

        var follow = new ParameterViewModel(
            rig.Stats, rig.Stats.Descriptor.Parameters.Single(p => p.Name == nameof(VisionNode.FollowReferenceAngle)));

        Assert.True(follow.IsShown);

        rig.Graph.Disconnect(rig.Stats.RegionReference!.Connection!);

        Assert.False(follow.IsShown);
    }

    /// <summary>An upright region has no angle to turn, so it follows only where.</summary>
    [Fact]
    public void Crops_upright_region_follows_where_and_stays_upright()
    {
        var crop = new CropNode { Roi = new System.Drawing.Rectangle(160, 90, 40, 20) };
        using var rig = new Rig(Taught, tool: crop);

        rig.MoveTo(new ReferencePose(200, 250, 90));

        // The centre is carried round the reference; the sides stay upright.
        Assert.Equal(
            new System.Drawing.Rectangle(180, 320, 40, 20),
            crop.Placements[nameof(CropNode.Roi)].Value);
    }

    // ---- the rig --------------------------------------------------------------------

    private static OrientedRectShape Box(ReferencePose pose) =>
        new(new Point2(pose.X, pose.Y), 40, 20, pose.Angle ?? 0);

    /// <summary>
    /// Picture, reference and a tool, run once with the reference where it
    /// is taught, so every test starts from a taught tool.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        private readonly Func<ReferencePose, Shape> _shape;
        private readonly GraphScheduler _scheduler;
        private ReferencePose _pose;

        public Rig(
            ReferencePose start,
            Func<ReferencePose, Shape>? shape = null,
            VisionNode? tool = null,
            VisionGraph? graph = null)
        {
            _shape = shape ?? Box;
            _pose = start;

            Graph = graph ?? new VisionGraph();
            Picture = new PictureNode(() => Patch(_pose));
            Reference = new ShapesNode([_shape(start)]);
            Tool = tool ?? new RegionStatsNode { Region = OnThePatch };

            Graph.Add(Picture);
            Graph.Add(Reference);
            Graph.Add(Tool);
            Graph.TryConnect(Picture.Out, Tool.Inputs.First(port => port.DataType == typeof(ImageData)));
            Graph.TryConnect(Reference.Out, Tool.RegionReference!);

            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);

            try
            {
                _scheduler = new GraphScheduler(Graph);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            Run();
        }

        public VisionGraph Graph { get; }

        public PictureNode Picture { get; }

        public ShapesNode Reference { get; }

        public VisionNode Tool { get; }

        public RegionStatsNode Stats => (RegionStatsNode)Tool;

        public double Mean => (double)_scheduler.GetOutput(Stats.MeanOut)!;

        public OrientedRect Placed => (OrientedRect)Tool.Placements[nameof(RegionStatsNode.Region)].Value;

        /// <summary>The part comes to rest somewhere else.</summary>
        public void MoveTo(ReferencePose pose)
        {
            _pose = pose;
            Reference.Shapes = [_shape(pose)];
            Picture.Invalidate();
            Run();
        }

        public void Run() => _scheduler.RunAsync().GetAwaiter().GetResult();

        public void Dispose() => _scheduler.Dispose();

        /// <summary>Black, with a white 40 x 20 patch 80 along from the reference, turned with it.</summary>
        private static Mat Patch(ReferencePose pose)
        {
            var mat = new Mat(400, 400, DepthType.Cv8U, 1);
            mat.SetTo(new MCvScalar(0));

            var radians = (pose.Angle ?? 0) * Math.PI / 180;
            var centre = new System.Drawing.PointF(
                (float)(pose.X + (80 * Math.Cos(radians))),
                (float)(pose.Y + (80 * Math.Sin(radians))));

            var corners = new RotatedRect(centre, new System.Drawing.SizeF(40, 20), (float)(pose.Angle ?? 0))
                .GetVertices()
                .Select(point => new System.Drawing.Point((int)Math.Round(point.X), (int)Math.Round(point.Y)))
                .ToArray();

            using var polygon = new VectorOfPoint(corners);
            CvInvoke.FillConvexPoly(mat, polygon, new MCvScalar(255));
            return mat;
        }
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
