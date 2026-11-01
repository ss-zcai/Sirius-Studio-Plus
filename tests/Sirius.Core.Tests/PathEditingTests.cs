using System.Reflection;
using System.Windows;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Shapes;
using Sirius.Studio.App.Controls;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Drawing a search path corner by corner, and editing it where it lies.
/// </summary>
[Collection(WpfCollection.Name)]
public class PathEditingTests
{
    public PathEditingTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
    }

    private static readonly PathBand Arc = new(
        [new Point2(100, 200), new Point2(200, 150), new Point2(300, 200)], closed: false, width: 40);

    private static ImageViewer Laid(PathBand? path)
    {
        WpfTestHost.EnsureApplicationResources();

        var viewer = new ImageViewer
        {
            ImageSource = new System.Windows.Media.Imaging.WriteableBitmap(
                400, 400, 96, 96, System.Windows.Media.PixelFormats.Gray8, null),
            IsPathEnabled = true,
            GuidePath = path,
        };

        viewer.Measure(new Size(500, 500));
        viewer.Arrange(new Rect(0, 0, 500, 500));
        viewer.UpdateLayout();
        viewer.FitToWindow();

        return viewer;
    }

    private static object? Invoke(ImageViewer viewer, string name, params object[] args) =>
        typeof(ImageViewer)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewer, args);

    private static void Set(ImageViewer viewer, string field, object? value) =>
        typeof(ImageViewer)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewer, value);

    private static object DragMode(string name) =>
        Enum.Parse(typeof(ImageViewer).GetNestedType("DragMode", BindingFlags.NonPublic)!, name);

    private static string HitAt(ImageViewer viewer, double x, double y) =>
        viewer.HitTestPathAt(new Point(x, y), out _).ToString();

    // ---- drawing ---------------------------------------------------------------

    [Fact]
    public void Clicks_place_corners_and_a_double_click_finishes_the_path() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(Arc);
            viewer.ActiveTool = ViewerTool.Path;

            Invoke(viewer, "AddPathClick", new Point(50, 50), 1);
            Invoke(viewer, "AddPathClick", new Point(150, 80), 1);
            Invoke(viewer, "AddPathClick", new Point(250, 60), 1);

            // Nothing is written until the path is finished.
            Assert.Equal(Arc, viewer.GuidePath);

            Invoke(viewer, "AddPathClick", new Point(250, 60), 2);

            Assert.Equal(new PathBand([new Point2(50, 50), new Point2(150, 80), new Point2(250, 60)], false, 40), viewer.GuidePath);
        });

    [Fact]
    public void Clicking_the_first_corner_again_closes_the_path() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(Arc);
            viewer.ActiveTool = ViewerTool.Path;

            foreach (var corner in new[] { new Point(50, 50), new Point(150, 50), new Point(150, 150), new Point(51, 51) })
                Invoke(viewer, "AddPathClick", corner, 1);

            var path = viewer.GuidePath!;
            Assert.True(path.Closed);
            Assert.Equal(3, path.Points.Count);
        });

    [Fact]
    public void Backspace_takes_a_corner_back_and_Escape_abandons_the_path() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(Arc);
            viewer.ActiveTool = ViewerTool.Path;

            Invoke(viewer, "AddPathClick", new Point(50, 50), 1);
            Invoke(viewer, "AddPathClick", new Point(150, 80), 1);
            Invoke(viewer, "UndoPathClick");
            Invoke(viewer, "AddPathClick", new Point(10, 10), 1);
            Invoke(viewer, "FinishPath", false);

            Assert.Equal([new Point2(50, 50), new Point2(10, 10)], viewer.GuidePath!.Points);

            Invoke(viewer, "AddPathClick", new Point(300, 300), 1);
            Invoke(viewer, "ResetPath");
            Invoke(viewer, "FinishPath", false);

            Assert.Equal([new Point2(50, 50), new Point2(10, 10)], viewer.GuidePath!.Points);
        });

    // ---- what a point grabs ----------------------------------------------------

    [Fact]
    public void A_corner_is_a_handle() =>
        WpfTestHost.OnStaThread(() => Assert.Equal("Corner", HitAt(Laid(Arc), 200, 150)));

    [Fact]
    public void The_bands_edge_is_a_handle() =>
        WpfTestHost.OnStaThread(() => Assert.Equal("Edge", HitAt(Laid(Arc), 150, 175 + 20 * Math.Sqrt(1.25))));

    [Fact]
    public void Inside_the_band_moves_it_and_outside_is_nothing() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(Arc);

            Assert.Equal("Inside", HitAt(viewer, 250, 178));
            Assert.Equal("None", HitAt(viewer, 200, 300));
        });

    // ---- what a drag does --------------------------------------------------------

    [Fact]
    public void Dragging_a_corner_moves_only_it() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(Arc);

            Set(viewer, "_dragStartPath", Arc);
            Set(viewer, "_pathCorner", 1);
            Invoke(viewer, "DragPath", DragMode("MovePathCorner"), new Point(210, 120));

            Assert.Equal([new Point2(100, 200), new Point2(210, 120), new Point2(300, 200)], viewer.GuidePath!.Points);
            Assert.Equal(40, viewer.GuidePath.Width);
        });

    [Fact]
    public void Dragging_the_band_moves_the_whole_path() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(Arc);

            Set(viewer, "_dragStartPath", Arc);
            Set(viewer, "_dragStartImage", new Point(250, 178));
            Invoke(viewer, "DragPath", DragMode("MovePath"), new Point(260, 168));

            Assert.Equal(Arc.Moved(10, -10), viewer.GuidePath);
        });

    [Fact]
    public void Dragging_the_edge_sets_the_width() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(new PathBand([new Point2(100, 200), new Point2(300, 200)], false, 40));

            Set(viewer, "_dragStartPath", viewer.GuidePath);
            Invoke(viewer, "DragPath", DragMode("ResizePath"), new Point(180, 235));

            Assert.Equal(70, viewer.GuidePath!.Width);
        });

    // ---- which panels take it --------------------------------------------------

    [Fact]
    public void A_viewer_panel_refuses_a_path()
    {
        var graph = new VisionGraph();
        var viewerNode = new ViewerNode();
        var finder = new FindCurveNode();
        graph.Add(viewerNode);
        graph.Add(finder);

        var panel = new ViewerPanelViewModel(viewerNode, new ShapeSelectionService());
        panel.PathParameter = new NodeViewModel(finder).Parameters.Single(p => p.Kind == ParamEditor.Path);

        Assert.Null(panel.PathParameter);
        Assert.False(panel.IsPathEnabled);

        panel.Dispose();
    }

    [Fact]
    public void The_owning_nodes_input_preview_draws_and_edits_it()
    {
        var graph = new VisionGraph();
        var finder = new FindCurveNode();
        graph.Add(finder);

        var preview = new PreviewPanelViewModel(finder, finder.ImageIn, null, new ShapeSelectionService());
        var parameter = new NodeViewModel(finder).Parameters.Single(p => p.Kind == ParamEditor.Path);

        preview.PathParameter = parameter;

        Assert.True(preview.IsPathEnabled);
        Assert.Equal(finder.Path, preview.GuidePath);

        preview.GuidePath = Arc;
        Assert.Equal(Arc, finder.Path);
        Assert.Equal(Arc.ToString(), parameter.PathText);

        // Typed in the properties panel, and back the other way.
        parameter.PathText = "0, 0; 50, 0 | 12";
        Assert.Equal(new PathBand([new Point2(0, 0), new Point2(50, 0)], false, 12), finder.Path);

        // Text that is not a path leaves it alone.
        parameter.PathText = "nonsense";
        Assert.Equal(12, finder.Path.Width);

        preview.Dispose();
    }
}
