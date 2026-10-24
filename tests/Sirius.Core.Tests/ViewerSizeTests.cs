using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Sirius.Core.Graph;
using Sirius.Studio.App.Controls;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Each viewer says how big the frame it is showing is.
/// </summary>
/// <remarks>
/// The shell used to put one size in the main status bar, taken from
/// whichever viewer panel happened to be first. With two viewers open it
/// showed one of them and said nothing about which; and since Crop, Resize
/// and Region of Interest all change the size, the number on screen
/// routinely belonged to a different picture than the one being looked at.
/// It is per viewer now, which is the only place it can be unambiguous.
/// </remarks>
[Collection(WpfCollection.Name)]
public class ViewerSizeTests
{
    public ViewerSizeTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
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

    /// <summary>A source, a crop, and a viewer on each.</summary>
    private static (VisionGraph Graph, ViewerNode Whole, ViewerNode Cropped) Build()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 640, Height = 480, Value = 200 };
        var crop = new CropNode { Roi = new System.Drawing.Rectangle(10, 10, 100, 50) };

        var whole = new ViewerNode();
        var cropped = new ViewerNode();

        foreach (var node in new VisionNode[] { source, crop, whole, cropped })
            graph.Add(node);

        graph.TryConnect(source.ImageOut, crop.ImageIn);
        graph.TryConnect(source.ImageOut, whole.ImageIn);
        graph.TryConnect(crop.ImageOut, cropped.ImageIn);

        return (graph, whole, cropped);
    }

    private static ViewerPanelViewModel Panel(GraphScheduler scheduler, ViewerNode node)
    {
        var panel = new ViewerPanelViewModel(node, new ShapeSelectionService());
        panel.Prepare(scheduler);
        panel.Commit();
        return panel;
    }

    /// <summary>
    /// The case the main status bar could not express: two viewers on one
    /// graph, showing different sizes, each saying its own.
    /// </summary>
    [Fact]
    public void Two_viewers_report_their_own_sizes()
    {
        var (graph, whole, cropped) = Build();

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var first = Panel(scheduler, whole);
        var second = Panel(scheduler, cropped);

        Assert.Equal("640 x 480 x 1", first.SizeText);
        Assert.Equal("100 x 50 x 1", second.SizeText);

        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public void A_viewer_with_nothing_wired_reports_no_size()
    {
        var graph = new VisionGraph();
        var viewer = new ViewerNode();
        graph.Add(viewer);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var panel = Panel(scheduler, viewer);

        Assert.Null(panel.SizeText);

        panel.Dispose();
    }

    /// <summary>
    /// The control shows what it is given, in its own status bar.
    /// </summary>
    [Fact]
    public void The_control_shows_the_size() =>
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var viewer = new ImageViewer { ImageSizeText = "100 x 50 x 1" };

            viewer.Measure(new Size(900, 700));
            viewer.Arrange(new Rect(0, 0, 900, 700));
            viewer.UpdateLayout();

            var label = (TextBlock)viewer.FindName("SizeText")!;

            Assert.Equal("100 x 50 x 1", label.Text);
        });

    /// <summary>
    /// The size reads first, then the cursor position, then the pixel
    /// value.
    /// </summary>
    /// <remarks>
    /// Order matters here for a reason that only shows when the pointer is
    /// elsewhere: the position and pixel readouts reserve a fixed width so
    /// the bar does not jitter as their digits change, and while they came
    /// first those reservations were two blank gaps with the size label
    /// stranded in the middle of them.
    /// </remarks>
    [Fact]
    public void The_size_reads_before_the_cursor_readouts() =>
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var viewer = new ImageViewer { ImageSizeText = "100 x 50 x 1" };

            viewer.Measure(new Size(900, 700));
            viewer.Arrange(new Rect(0, 0, 900, 700));
            viewer.UpdateLayout();

            var bar = (DockPanel)VisualTreeHelper.GetParent((TextBlock)viewer.FindName("SizeText")!);

            var order = bar.Children
                .OfType<TextBlock>()
                .Where(child => DockPanel.GetDock(child) == Dock.Left)
                .Select(child => child.Name)
                .ToArray();

            Assert.Equal(new[] { "SizeText", "PositionText", "PixelText" }, order);
        });

    /// <summary>
    /// And it is wired to the panel in the shipped templates - the control
    /// having the property is no use if nothing fills it.
    /// </summary>
    [Fact]
    public void Both_templates_bind_the_size()
    {
        var theme = File.ReadAllText(Path.Combine(Repository.AppSource, "Themes", "Dark.xaml"));

        Assert.Equal(
            2,
            theme.Split("ImageSizeText=\"{Binding SizeText}\"").Length - 1);
    }

    /// <summary>
    /// The binding survives: a two-way write elsewhere on the control must
    /// not replace it with a local value, which is how the profile source
    /// binding was lost once before.
    /// </summary>
    [Fact]
    public void The_binding_is_not_replaced_by_a_local_value() =>
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var graph = new VisionGraph();
            var node = new ViewerNode();
            graph.Add(node);

            using var scheduler = CreateScheduler(graph);
            var panel = new ViewerPanelViewModel(node, new ShapeSelectionService());

            var viewer = new ImageViewer { DataContext = panel };
            BindingOperations.SetBinding(
                viewer,
                ImageViewer.ImageSizeTextProperty,
                new Binding(nameof(ViewerPanelViewModel.SizeText)));

            viewer.Measure(new Size(900, 700));
            viewer.Arrange(new Rect(0, 0, 900, 700));
            viewer.UpdateLayout();

            Assert.NotNull(BindingOperations.GetBindingExpression(
                viewer, ImageViewer.ImageSizeTextProperty));

            panel.Dispose();
        });

    /// <summary>
    /// The shell no longer claims to know one size for the whole window.
    /// </summary>
    [Fact]
    public void The_main_status_bar_does_not_show_a_size()
    {
        var xaml = File.ReadAllText(Path.Combine(Repository.AppSource, "MainWindow.xaml"));

        Assert.DoesNotContain("{Binding ImageSizeText}", xaml, StringComparison.Ordinal);
        Assert.Null(typeof(MainViewModel).GetProperty("ImageSizeText"));
    }
}
