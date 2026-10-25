using System.Reflection;
using System.Windows;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Studio.App.Controls;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Moving and resizing a search band once it is drawn.
/// </summary>
/// <remarks>
/// The band's own circles are its handles. Separate knobs would have to
/// sit somewhere on a shape that is the same all the way round, and
/// wherever they went they would cover the feature the band was drawn
/// around.
/// </remarks>
[Collection(WpfCollection.Name)]
public class RingEditingTests
{
    public RingEditingTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
    }

    private static ImageViewer Laid(Ring ring)
    {
        WpfTestHost.EnsureApplicationResources();

        var viewer = new ImageViewer
        {
            ImageSource = new System.Windows.Media.Imaging.WriteableBitmap(
                400, 400, 96, 96, System.Windows.Media.PixelFormats.Gray8, null),
            IsRingEnabled = true,
            Ring = ring,
        };

        viewer.Measure(new Size(500, 500));
        viewer.Arrange(new Rect(0, 0, 500, 500));
        viewer.UpdateLayout();
        viewer.FitToWindow();

        return viewer;
    }

    /// <summary>Hit-tests in image coordinates, via the control's own mapping.</summary>
    private static string HitAt(ImageViewer viewer, double x, double y)
    {
        // Image coordinates, which is where the band's geometry lives -
        // the screen-space entry point is a one-line conversion over this.
        return Invoke(viewer, "HitTestRingAt", new Point(x, y))!.ToString()!;
    }

    private static object? Invoke(ImageViewer viewer, string name, params object[] args) =>
        typeof(ImageViewer)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewer, args);

    private static void Set(ImageViewer viewer, string field, object value) =>
        typeof(ImageViewer)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewer, value);

    // ---- what a point grabs --------------------------------------------------

    [Fact]
    public void The_outer_circle_is_a_handle() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(new Ring(200, 200, 80, 140));

            Assert.Equal("Outer", HitAt(viewer, 340, 200));
        });

    [Fact]
    public void The_inner_circle_is_a_handle() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(new Ring(200, 200, 80, 140));

            Assert.Equal("Inner", HitAt(viewer, 280, 200));
        });

    [Fact]
    public void The_band_itself_moves_it() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(new Ring(200, 200, 80, 140));

            Assert.Equal("Inside", HitAt(viewer, 310, 200));
        });

    /// <summary>
    /// The hole is not a handle. That is where the part being measured is,
    /// and a click there is far likelier meant for the image.
    /// </summary>
    [Fact]
    public void The_hole_is_not_a_handle() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(new Ring(200, 200, 80, 140));

            Assert.Equal("None", HitAt(viewer, 200, 200));
            Assert.Equal("None", HitAt(viewer, 230, 200));
        });

    [Fact]
    public void Outside_the_band_is_not_a_handle() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(new Ring(200, 200, 80, 140));

            Assert.Equal("None", HitAt(viewer, 390, 200));
        });

    /// <summary>
    /// Nothing is grabbable while a band is being placed - the clicks
    /// belong to the gesture.
    /// </summary>
    [Fact]
    public void A_half_placed_band_offers_no_handles() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(new Ring(200, 200, 80, 140));

            Invoke(viewer, "AddRingClick", new Point(100, 100));

            Assert.Equal("None", HitAt(viewer, 340, 200));
        });

    // ---- what a drag does ----------------------------------------------------

    [Fact]
    public void Dragging_the_band_moves_it() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(new Ring(200, 200, 80, 140));

            Set(viewer, "_dragStartRing", viewer.Ring);
            Set(viewer, "_dragStartImage", new Point(310, 200));

            Invoke(viewer, "DragRing", DragMode(viewer, "MoveRing"), new Point(330, 215));

            Assert.Equal(220, viewer.Ring.CentreX);
            Assert.Equal(215, viewer.Ring.CentreY);

            // The band keeps its shape.
            Assert.Equal(80, viewer.Ring.InnerRadius);
            Assert.Equal(140, viewer.Ring.OuterRadius);
        });

    [Fact]
    public void Dragging_the_outer_edge_resizes_it() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(new Ring(200, 200, 80, 140));

            Set(viewer, "_dragStartRing", viewer.Ring);
            Set(viewer, "_dragStartImage", new Point(340, 200));

            Invoke(viewer, "DragRing", DragMode(viewer, "ResizeRingOuter"), new Point(360, 200));

            Assert.Equal(160, viewer.Ring.OuterRadius);
            Assert.Equal(80, viewer.Ring.InnerRadius);
            Assert.Equal(200, viewer.Ring.CentreX);
        });

    [Fact]
    public void Dragging_the_inner_edge_resizes_it() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(new Ring(200, 200, 80, 140));

            Set(viewer, "_dragStartRing", viewer.Ring);
            Set(viewer, "_dragStartImage", new Point(280, 200));

            Invoke(viewer, "DragRing", DragMode(viewer, "ResizeRingInner"), new Point(300, 200));

            Assert.Equal(100, viewer.Ring.InnerRadius);
            Assert.Equal(140, viewer.Ring.OuterRadius);
        });

    /// <summary>
    /// Dragging one edge past the other swaps them rather than inverting
    /// the band, because Ring orders its radii.
    /// </summary>
    [Fact]
    public void Dragging_an_edge_past_the_other_swaps_them() =>
        WpfTestHost.OnStaThread(() =>
        {
            var viewer = Laid(new Ring(200, 200, 80, 140));

            Set(viewer, "_dragStartRing", viewer.Ring);
            Set(viewer, "_dragStartImage", new Point(280, 200));

            Invoke(viewer, "DragRing", DragMode(viewer, "ResizeRingInner"), new Point(400, 200));

            Assert.True(viewer.Ring.InnerRadius <= viewer.Ring.OuterRadius);
            Assert.Equal(140, viewer.Ring.InnerRadius);
            Assert.Equal(200, viewer.Ring.OuterRadius);
        });

    private static object DragMode(ImageViewer viewer, string name)
    {
        var type = typeof(ImageViewer)
            .GetNestedType("DragMode", BindingFlags.NonPublic)!;

        return Enum.Parse(type, name);
    }

    // ---- where a band may appear ----------------------------------------------

    /// <summary>
    /// A Viewer node is where results are looked at, so a search band does
    /// not belong on it.
    /// </summary>
    /// <remarks>
    /// It was appearing on every viewer in the recipe at once, simply
    /// because a node that owns one happened to be selected.
    /// </remarks>
    [Fact]
    public void A_viewer_panel_refuses_a_band()
    {
        var graph = new VisionGraph();
        var viewerNode = new ViewerNode();
        var finder = new FindCircleNode();
        graph.Add(viewerNode);
        graph.Add(finder);

        var panel = new ViewerPanelViewModel(viewerNode, new ShapeSelectionService());
        var band = new NodeViewModel(finder).Parameters.Single(p => p.Kind == ParamEditor.Ring);

        panel.RingParameter = band;

        Assert.Null(panel.RingParameter);
        Assert.False(panel.IsRingEnabled);

        panel.Dispose();
    }

    [Fact]
    public void The_owning_nodes_input_preview_accepts_it()
    {
        var graph = new VisionGraph();
        var finder = new FindCircleNode();
        graph.Add(finder);

        var preview = new PreviewPanelViewModel(
            finder, finder.ImageIn, null, new ShapeSelectionService());

        var band = new NodeViewModel(finder).Parameters.Single(p => p.Kind == ParamEditor.Ring);

        preview.RingParameter = band;

        Assert.NotNull(preview.RingParameter);
        Assert.True(preview.IsRingEnabled);

        preview.Dispose();
    }

    /// <summary>
    /// And another node's preview does not, even though it shows an image.
    /// </summary>
    [Fact]
    public void Another_nodes_preview_refuses_it()
    {
        var graph = new VisionGraph();
        var finder = new FindCircleNode();
        var other = new ThresholdNode();
        graph.Add(finder);
        graph.Add(other);

        var preview = new PreviewPanelViewModel(
            other, other.ImageIn, null, new ShapeSelectionService());

        var band = new NodeViewModel(finder).Parameters.Single(p => p.Kind == ParamEditor.Ring);

        preview.RingParameter = band;

        Assert.Null(preview.RingParameter);

        preview.Dispose();
    }
}
