using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using Sirius.Core.Graph;
using Sirius.Core.Serialization;
using Sirius.Core.Shapes;
using Sirius.Studio.App.Controls;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Hand-drawn measurements are part of the recipe.
/// </summary>
/// <remarks>
/// <para>
/// They were not. They lived in the viewer control and nowhere else, so an
/// afternoon of measuring disappeared on File - New without a prompt: the
/// recipe was unchanged, because the measurements had never been in it. That
/// is the failure these tests exist to keep out, and it has three parts -
/// the node holds them, the file carries them, and the control hands them
/// over - each of which looks fine on its own while the work is still lost.
/// </para>
/// <para>
/// Keyed by view as well as by node, because a node can be looked at through
/// more than one window and a preview of an input is not the same picture as
/// a preview of an output.
/// </para>
/// </remarks>
[Collection(WpfCollection.Name)]
public class MeasurementPersistenceTests
{
    public MeasurementPersistenceTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
    }

    private static MeasurementRecord Line(
        double x1, double y1, double x2, double y2, string? view = null) =>
        new(
            nameof(ViewerTool.PointToPoint),
            new[] { new Point2(x1, y1), new Point2(x2, y2) },
            "#FFD700",
            null,
            view);

    // ---- the file carries them ---------------------------------------------

    [Fact]
    public void A_measurement_survives_a_round_trip()
    {
        var graph = new VisionGraph();
        var node = new ThresholdNode();
        graph.Add(node);

        node.Measurements =
        [
            new MeasurementRecord(
                nameof(ViewerTool.LineProfile),
                new[] { new Point2(12.5, 40), new Point2(300, 180.25) },
                "#FF00FF00",
                "Profile 1",
                "In"),
        ];

        var result = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));
        var restored = Assert.Single(result.Graph.Nodes).Measurements;
        var measurement = Assert.Single(restored);

        Assert.Empty(result.Warnings);
        Assert.Equal(nameof(ViewerTool.LineProfile), measurement.Tool);
        Assert.Equal("#FF00FF00", measurement.Colour);
        Assert.Equal("Profile 1", measurement.Name);
        Assert.Equal("In", measurement.View);
        Assert.Equal(
            new[] { 12.5, 40, 300, 180.25 },
            measurement.Points.SelectMany(p => new[] { p.X, p.Y }));
    }

    /// <summary>
    /// A recipe with nothing drawn on it is written exactly as it was before
    /// measurements existed - so a file from this build still opens in one
    /// that predates it, and a diff of an untouched recipe is empty.
    /// </summary>
    [Fact]
    public void A_recipe_with_none_says_nothing_about_them()
    {
        var graph = new VisionGraph();
        graph.Add(new ThresholdNode());

        Assert.DoesNotContain("measurements", GraphSerializer.Serialize(graph), StringComparison.Ordinal);
    }

    /// <summary>
    /// The view is omitted when there is none, for the same reason - most
    /// measurements are drawn on a Viewer node's own panel.
    /// </summary>
    [Fact]
    public void An_unkeyed_measurement_writes_no_view()
    {
        var graph = new VisionGraph();
        var node = new ThresholdNode();
        graph.Add(node);
        node.Measurements = [Line(0, 0, 10, 10)];

        var json = GraphSerializer.Serialize(graph);

        Assert.Contains("\"measurements\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"view\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// An entry with nothing in it is dropped and reported rather than
    /// restored as a measurement with no points, which would draw as nothing
    /// and delete as a mystery.
    /// </summary>
    [Fact]
    public void An_empty_measurement_is_dropped_with_a_warning()
    {
        var graph = new VisionGraph();
        graph.Add(new ThresholdNode());

        var document = GraphSerializer.ToDocument(graph);
        document.Nodes[0].Measurements =
        [
            new MeasurementDocument { Tool = string.Empty, Points = [1, 2, 3, 4] },
            new MeasurementDocument { Tool = nameof(ViewerTool.PointToPoint), Points = [] },
            new MeasurementDocument { Tool = nameof(ViewerTool.PointToPoint), Points = [1, 2, 3, 4] },
        ];

        var result = GraphSerializer.FromDocument(document);

        Assert.Single(Assert.Single(result.Graph.Nodes).Measurements);
        Assert.Equal(2, result.Warnings.Count);
    }

    /// <summary>
    /// Points are written as a flat list, so an odd trailing number is a
    /// half-written point. Dropping it beats reading a coordinate that was
    /// never saved.
    /// </summary>
    [Fact]
    public void A_half_written_point_is_dropped()
    {
        var document = new MeasurementDocument
        {
            Tool = nameof(ViewerTool.PointToPoint),
            Points = [1, 2, 3, 4, 5],
        };

        Assert.Equal(2, document.ToRecord().Points.Count);
    }

    // ---- the node holds them, so they count as a change --------------------

    /// <summary>
    /// The point of the whole exercise: New and Open ask before discarding
    /// them. They do that by asking whether the recipe differs from what was
    /// saved, so the measurement has to be in the recipe.
    /// </summary>
    [Fact]
    public void Drawing_a_measurement_counts_as_a_change()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sirius-measure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            OnModelThread(() =>
            {
                var model = new MainViewModel();
                var item = model.Palette
                    .SelectMany(group => group.Items)
                    .First(entry => entry.DisplayName == "Threshold");
                var node = model.AddNodeAt(item, new Point(40, 40))!.Node;

                model.Write(Path.Combine(folder, "recipe.sgraph"));
                Assert.False(model.IsModified);

                node.Measurements = [Line(4, 4, 60, 60)];

                Assert.True(model.IsModified);
            });
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception)
            {
                // A leftover temp folder is not worth failing a run over.
            }
        }
    }

    /// <summary>
    /// Drawing does not re-run the graph. It says nothing about what the
    /// recipe computes, and recomputing a large frame because somebody
    /// measured something on it would be absurd.
    /// </summary>
    [Fact]
    public void Drawing_a_measurement_does_not_invalidate()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 32, Height = 32, Value = 10 };
        var node = new ThresholdNode();
        graph.Add(source);
        graph.Add(node);
        graph.TryConnect(source.ImageOut, node.ImageIn);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(0, scheduler.DirtyCount);

        node.Measurements = [Line(1, 1, 20, 20)];

        Assert.Equal(0, scheduler.DirtyCount);
    }

    // ---- a panel reads and writes only its own view ------------------------

    [Fact]
    public void A_panel_writes_what_was_drawn_onto_its_node()
    {
        var (graph, _, tool) = Build();
        using var scheduler = CreateScheduler(graph);
        var panel = Preview(tool, tool.ImageIn);

        panel.Measurements = [Line(2, 3, 40, 50)];

        var stored = Assert.Single(tool.Measurements);
        Assert.Equal(tool.ImageIn.Name, stored.View);
        _ = graph;
    }

    /// <summary>
    /// Opening a preview of a node that already has measurements works.
    /// </summary>
    /// <remarks>
    /// It did not. The base constructor reads the node's measurements, and
    /// reading them asks each one whether it belongs to this view - which
    /// was a virtual property, so the override ran before the derived
    /// constructor had assigned the port it reads, and threw a
    /// NullReferenceException.
    ///
    /// It threw only once there were measurements to ask about, because
    /// with none the predicate is never invoked. So every preview worked
    /// until somebody drew on one, and then that node's previews stopped
    /// opening - with the button appearing to do nothing, since the
    /// exception surfaced in a dialog the shell suppresses after the
    /// first.
    /// </remarks>
    [Fact]
    public void A_preview_opens_on_a_node_that_already_has_measurements()
    {
        var (graph, _, tool) = Build();
        using var scheduler = CreateScheduler(graph);

        tool.Measurements =
        [
            Line(1, 1, 10, 10, tool.ImageIn.Name),
            Line(2, 2, 20, 20, "Out"),
            Line(3, 3, 30, 30),
        ];

        var preview = Preview(tool, tool.ImageIn);

        // And it sees its own view's measurement, not the other two.
        var mine = Assert.Single(preview.Measurements);
        Assert.Equal(tool.ImageIn.Name, mine.View);

        preview.Dispose();
        _ = graph;
    }

    /// <summary>
    /// Every panel kind reports the view it was built for, which is what
    /// the filtering keys on.
    /// </summary>
    [Fact]
    public void A_panel_knows_its_view_from_construction()
    {
        var (graph, _, tool) = Build();
        using var scheduler = CreateScheduler(graph);

        var input = Preview(tool, tool.ImageIn);
        var output = Preview(tool, tool.ImageOut);
        var viewer = new ViewerPanelViewModel(new ViewerNode(), new ShapeSelectionService());

        Assert.Equal(tool.ImageIn.Name, input.MeasurementView);
        Assert.Equal(tool.ImageOut.Name, output.MeasurementView);
        Assert.Equal(string.Empty, viewer.MeasurementView);

        input.Dispose();
        output.Dispose();
        viewer.Dispose();
        _ = graph;
    }

    [Fact]
    public void A_panel_sees_what_was_loaded_onto_its_node()
    {
        var (graph, _, tool) = Build();
        using var scheduler = CreateScheduler(graph);
        var panel = Preview(tool, tool.ImageIn);

        tool.Measurements = [Line(2, 3, 40, 50, tool.ImageIn.Name)];

        Assert.Single(panel.Measurements);
        _ = graph;
    }

    /// <summary>
    /// A preview of an input and a preview of an output show different
    /// pixels - this tool thresholds, and an ROI would crop - so each keeps
    /// its own measurements and neither clears the other's.
    /// </summary>
    [Fact]
    public void One_view_does_not_clear_anothers_measurements()
    {
        var (graph, _, tool) = Build();
        using var scheduler = CreateScheduler(graph);

        var input = Preview(tool, tool.ImageIn);
        var output = Preview(tool, tool.ImageOut);

        input.Measurements = [Line(1, 1, 10, 10)];
        output.Measurements = [Line(5, 5, 50, 50), Line(6, 6, 60, 60)];

        Assert.Equal(3, tool.Measurements.Count);
        Assert.Single(input.Measurements);
        Assert.Equal(2, output.Measurements.Count);

        // And clearing one leaves the other alone.
        output.Measurements = [];

        Assert.Single(tool.Measurements);
        Assert.Single(input.Measurements);
        _ = graph;
    }

    /// <summary>
    /// A Viewer node's own panel is the unkeyed view, so its measurements
    /// are written without a view and read back by it.
    /// </summary>
    [Fact]
    public void A_viewer_panel_uses_the_unkeyed_view()
    {
        var graph = new VisionGraph();
        var node = new ViewerNode();
        graph.Add(node);

        using var scheduler = CreateScheduler(graph);
        var panel = new ViewerPanelViewModel(node, new ShapeSelectionService());

        panel.Measurements = [Line(1, 1, 10, 10)];

        Assert.Null(Assert.Single(node.Measurements).View);
        Assert.Single(panel.Measurements);

        panel.Dispose();
    }

    // ---- the control hands them over --------------------------------------

    /// <summary>
    /// Drawing publishes. Without this the node never hears about the
    /// measurement and the whole chain above is a chain to nowhere - which
    /// is exactly the shape of the original bug.
    /// </summary>
    [Fact]
    public void The_control_publishes_what_is_drawn()
    {
        OnViewerThread(() =>
        {
            var viewer = Laid();

            Add(viewer, new Point(4, 4), new Point(40, 40));

            var published = Assert.Single(viewer.Measurements!);
            Assert.Equal(nameof(ViewerTool.PointToPoint), published.Tool);
            Assert.Equal(
                new[] { 4d, 4, 40, 40 },
                published.Points.SelectMany(p => new[] { p.X, p.Y }));
        });
    }

    [Fact]
    public void Clearing_publishes_an_empty_set()
    {
        OnViewerThread(() =>
        {
            var viewer = Laid();
            Add(viewer, new Point(4, 4), new Point(40, 40));

            viewer.ClearMeasurements();

            Assert.Empty(viewer.Measurements!);
        });
    }

    /// <summary>
    /// And a saved set comes back drawn, not merely held: the records are
    /// turned back into measurements the control can select, recolour and
    /// delete.
    /// </summary>
    [Fact]
    public void A_saved_set_is_drawn_again()
    {
        OnViewerThread(() =>
        {
            var viewer = Laid();

            viewer.Measurements =
            [
                new MeasurementRecord(
                    nameof(ViewerTool.LineProfile),
                    new[] { new Point2(2, 2), new Point2(30, 30) },
                    "#FFFFD700",
                    "Profile 1",
                    null),
            ];

            var restored = Assert.Single(Drawn(viewer).Cast<Measurement>());
            Assert.Equal(ViewerTool.LineProfile, restored.Tool);
            Assert.Equal("Profile 1", restored.Name);
            Assert.Equal(new Point(2, 2), restored.Points[0]);
        });
    }

    /// <summary>
    /// A measurement drawn by a tool this build does not have is skipped
    /// rather than guessed at: its points would mean something this one
    /// cannot interpret.
    /// </summary>
    [Fact]
    public void An_unknown_tool_is_skipped()
    {
        OnViewerThread(() =>
        {
            var viewer = Laid();

            viewer.Measurements =
            [
                new MeasurementRecord(
                    "FindBezierWhatever",
                    new[] { new Point2(2, 2), new Point2(30, 30) },
                    "#FFFFD700",
                    null,
                    null),
                Line(1, 1, 9, 9),
            ];

            Assert.Single(Drawn(viewer).Cast<Measurement>());
        });
    }

    /// <summary>
    /// Restoring must not come straight back out as a publish, or opening a
    /// recipe would mark it modified the moment it finished loading and
    /// every Open would offer to save what it had just read.
    /// </summary>
    [Fact]
    public void Restoring_is_not_a_change()
    {
        OnViewerThread(() =>
        {
            var viewer = Laid();
            var panel = new ViewerPanelViewModel(new ViewerNode(), new ShapeSelectionService());

            viewer.DataContext = panel;
            BindingOperations.SetBinding(
                viewer,
                ImageViewer.MeasurementsProperty,
                new Binding(nameof(ViewerPanelViewModel.Measurements)) { Mode = BindingMode.TwoWay });

            panel.Node.Measurements = [Line(1, 1, 10, 10)];

            // One measurement in, one measurement out - not two, and not
            // zero because the publish raced the restore.
            Assert.Single(Drawn(viewer).Cast<Measurement>());
            Assert.Single(panel.Node.Measurements);

            panel.Dispose();
        });
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Runs on the collection's shared user-interface thread.
    /// </summary>
    /// <remarks>
    /// Not on a thread of its own. WPF keeps one <c>Application</c> per
    /// process and it is thread-affine, so building controls on a second
    /// thread creates a second one - and the next test to touch the shared
    /// one fails inside a window procedure, taking the test host down
    /// instead of failing a case. Which is what these tests did when they
    /// were first written.
    /// </remarks>
    private static void OnViewerThread(Action action) => WpfTestHost.OnStaThread(() =>
    {
        WpfTestHost.EnsureApplicationResources();
        action();
    });

    /// <summary>
    /// A bare thread, for the cases that build a view-model and no control.
    /// </summary>
    private static void OnModelThread(Action action)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    /// <summary>A viewer with its template applied, over an image.</summary>
    private static ImageViewer Laid()
    {
        var viewer = new ImageViewer
        {
            ImageSource = new System.Windows.Media.Imaging.WriteableBitmap(
                64, 48, 96, 96, System.Windows.Media.PixelFormats.Gray8, null),
        };

        viewer.Measure(new Size(900, 700));
        viewer.Arrange(new Rect(0, 0, 900, 700));
        viewer.UpdateLayout();

        return viewer;
    }

    /// <summary>
    /// Places a two-point measurement the way a completed drag does. The
    /// gesture itself needs a live mouse device; what it ends in is this.
    /// </summary>
    private static void Add(ImageViewer viewer, Point from, Point to)
    {
        Drawn(viewer).Add(new Measurement(
            ViewerTool.PointToPoint,
            new[] { from, to },
            System.Windows.Media.Colors.Gold));

        Invoke(viewer, "Publish");
    }

    private static System.Collections.IList Drawn(ImageViewer viewer) =>
        (System.Collections.IList)typeof(ImageViewer)
            .GetField("_measurements", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewer)!;

    private static void Invoke(ImageViewer viewer, string method) =>
        typeof(ImageViewer)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewer, null);

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

    private static (VisionGraph Graph, ConstantImageNode Source, ThresholdNode Tool) Build()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 64, Height = 48, Value = 200 };
        var tool = new ThresholdNode { Threshold = 128 };

        graph.Add(source);
        graph.Add(tool);
        graph.TryConnect(source.ImageOut, tool.ImageIn);

        return (graph, source, tool);
    }

    private static PreviewPanelViewModel Preview(VisionNode node, Port port) =>
        new(node, port, null, new ShapeSelectionService());
}
