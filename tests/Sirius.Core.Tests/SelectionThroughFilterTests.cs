using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// A selection follows through Filter Shapes and Select Shape: a table on
/// the finder and a viewer drawing what was kept and rejected pick the same
/// blob, each in its own numbering.
/// </summary>
/// <remarks>
/// The recipe this came from: Find Blobs into a Shape Table, and into a
/// Filter Shapes whose Kept and Rejected go through two Shape Styles, red
/// and green, to one viewer. Picking row 7 in the table picked nothing on
/// the image.
/// </remarks>
public class SelectionThroughFilterTests
{
    public SelectionThroughFilterTests() => TestCatalog.Ensure();

    [Fact]
    public void A_row_picked_in_the_finders_table_is_picked_among_the_kept_or_the_rejected()
    {
        using var rig = new Rig();

        // Row 4 - area 30 - was kept: second of the kept, which come first
        // on the viewer.
        rig.Selection.Select(rig.Finder.Out, 3);

        Assert.Equal(1, rig.Selection.For(rig.Filter.KeptOut));
        Assert.Null(rig.Selection.For(rig.Filter.RejectedOut));
        Assert.Equal(1, rig.Panel.SelectedShapeIndex);

        // Labelled with the table's row, so the two can be matched.
        Assert.Equal(4, rig.Panel.SelectedShapeNumber);

        // Row 2 - area 10 - was rejected: second of the rejected, after
        // the three kept on the viewer.
        rig.Selection.Select(rig.Finder.Out, 1);

        Assert.Null(rig.Selection.For(rig.Filter.KeptOut));
        Assert.Equal(3 + 1, rig.Panel.SelectedShapeIndex);
        Assert.Equal(2, rig.Panel.SelectedShapeNumber);
    }

    [Fact]
    public void A_shape_picked_on_the_viewer_is_the_finders_row()
    {
        using var rig = new Rig();

        // The first kept blob is the third found.
        rig.Panel.SelectedShapeIndex = 0;

        Assert.Equal(2, rig.Selection.For(rig.Finder.Out));

        // Picked here, it is numbered as here.
        Assert.Equal(1, rig.Panel.SelectedShapeNumber);
    }

    [Fact]
    public void Kept_and_rejected_share_a_selection_with_the_finder()
    {
        using var rig = new Rig();

        Assert.True(ShapeSelectionService.Same(rig.Finder.Out, rig.Filter.KeptOut));
        Assert.True(ShapeSelectionService.Same(rig.Red.ShapesOut, rig.Green.ShapesOut));
        Assert.Same(rig.Finder.Out, ShapeSelectionService.Origin(rig.Green.ShapesOut));
    }

    [Fact]
    public void Select_shape_is_selected_when_its_one_shape_is()
    {
        var graph = new VisionGraph();
        var finder = new EdgeTestKit.ShapesNode(Blobs());
        var pick = new SelectShapeNode { Mode = ShapeSelection.Largest, Measure = ShapeMeasure.Area };

        graph.Add(finder);
        graph.Add(pick);
        graph.TryConnect(finder.Out, pick.ShapesIn);

        using var scheduler = EdgeTestKit.Run(graph);
        var selection = new ShapeSelectionService();

        selection.Select(finder.Out, 4);
        Assert.Equal(0, selection.For(pick.ShapesOut));

        selection.Select(finder.Out, 2);
        Assert.Null(selection.For(pick.ShapesOut));

        selection.Select(pick.ShapesOut, 0);
        Assert.Equal(4, selection.For(finder.Out));
    }

    /// <summary>Five blobs of area 0, 10, 20, 30, 40.</summary>
    private static Shape[] Blobs() =>
        Enumerable.Range(0, 5)
            .Select(i => (Shape)new PointShape(new Point2(i * 10, i * 5))
            {
                Metrics = new Dictionary<string, double> { [ShapeMetric.Area] = i * 10 },
            })
            .ToArray();

    /// <summary>
    /// Finder, Filter Shapes keeping area 20 and up, Kept and Rejected
    /// through a Shape Style each to one viewer, run once.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        private readonly GraphScheduler _scheduler;

        public Rig()
        {
            var graph = new VisionGraph();
            var image = new ConstantImageNode { Width = 64, Height = 48, Value = 200 };
            var viewer = new ViewerNode();

            Finder = new EdgeTestKit.ShapesNode(Blobs());
            Filter = new FilterShapesNode { Measure = ShapeMeasure.Area, Range = new ValueRange(20, null) };
            Red = new ShapeStyleNode { Colour = "#FF0000" };
            Green = new ShapeStyleNode { Colour = "#00FF00" };

            foreach (var node in new VisionNode[] { image, viewer, Finder, Filter, Red, Green })
                graph.Add(node);

            graph.TryConnect(image.ImageOut, viewer.ImageIn);
            graph.TryConnect(Finder.Out, Filter.ShapesIn);
            graph.TryConnect(Filter.KeptOut, Red.ShapesIn);
            graph.TryConnect(Filter.RejectedOut, Green.ShapesIn);

            graph.TryConnect(Red.ShapesOut, viewer.ShapesIn);
            viewer.Fit();
            graph.TryConnect(Green.ShapesOut, viewer.ShapeInputs[1]);
            viewer.Fit();

            _scheduler = EdgeTestKit.Run(graph);

            Selection = new ShapeSelectionService();
            Panel = new ViewerPanelViewModel(viewer, Selection);
            Panel.Prepare(_scheduler);
            Panel.Commit();
        }

        public EdgeTestKit.ShapesNode Finder { get; }

        public FilterShapesNode Filter { get; }

        public ShapeStyleNode Red { get; }

        public ShapeStyleNode Green { get; }

        public ShapeSelectionService Selection { get; }

        public ViewerPanelViewModel Panel { get; }

        public void Dispose()
        {
            Panel.Dispose();
            _scheduler.Dispose();
        }
    }
}
