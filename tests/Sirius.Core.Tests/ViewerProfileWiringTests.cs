using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using Sirius.Core.Graph;
using Sirius.Studio.App.Controls;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Interactive profiling across the control and its panel, wired as the shell
/// wires them.
/// </summary>
/// <remarks>
/// <para>
/// The halves are each covered elsewhere - <see cref="ViewerProfilingTests"/>
/// takes the panel from "profiling on" to a sampled frame - but neither knows
/// whether the other is ever asked. The join is four hops: the control decides
/// profiling is wanted, a two-way binding carries that to the panel, the panel
/// asks the shell for a pass, and the pass hands a frame back through a
/// one-way binding. A break anywhere in that loop looks identical from the
/// outside: a line drawn on the image and a chart that says there is none.
/// </para>
/// <para>
/// The shell's part is stood in for here by running the scheduler directly,
/// which is all <c>MainViewModel</c> does with the request.
/// </para>
/// </remarks>
[Collection(WpfCollection.Name)]
public class ViewerProfileWiringTests
{
    public ViewerProfileWiringTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
    }

    private static void OnStaThread(Action action) => WpfTestHost.OnStaThread(action);

    private static void EnsureApplicationResources() => WpfTestHost.EnsureApplicationResources();

    /// <summary>
    /// The viewer, laid out, bound to a panel over a one-node graph, with the
    /// shell's response to a frame request stood in.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            EnsureApplicationResources();

            var graph = new VisionGraph();
            var source = new ConstantImageNode { Width = 64, Height = 48, Value = 120 };
            var node = new ViewerNode();
            graph.Add(source);
            graph.Add(node);
            graph.TryConnect(source.ImageOut, node.ImageIn);

            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                Scheduler = new GraphScheduler(graph);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            Panel = new ViewerPanelViewModel(node, new ShapeSelectionService());

            // What MainViewModel does with the request: a pass, later. The
            // delay is the point - answering synchronously would hide any
            // break in the path that carries the frame back.
            Panel.ProfileDataRequested += (_, _) =>
                Dispatcher.CurrentDispatcher.BeginInvoke(Pass, DispatcherPriority.Background);

            Viewer = new ImageViewer { DataContext = Panel };

            // The bindings the panel's DataTemplate declares.
            BindingOperations.SetBinding(Viewer, ImageViewer.ProfileSourceProperty,
                new Binding(nameof(ViewerPanelViewModel.ProfileSource)));
            BindingOperations.SetBinding(Viewer, ImageViewer.IsProfilingEnabledProperty,
                new Binding(nameof(ViewerPanelViewModel.IsProfilingEnabled)) { Mode = BindingMode.TwoWay });
            BindingOperations.SetBinding(Viewer, ImageViewer.ProfileLinesProperty,
                new Binding(nameof(ViewerPanelViewModel.ProfileLines)));

            Viewer.Measure(new Size(900, 700));
            Viewer.Arrange(new Rect(0, 0, 900, 700));
            Viewer.UpdateLayout();

            Pass();
        }

        public GraphScheduler Scheduler { get; }

        public ViewerPanelViewModel Panel { get; }

        public ImageViewer Viewer { get; }

        /// <summary>Runs queued work, as the message loop would.</summary>
        public void Settle()
        {
            for (var i = 0; i < 10; i++)
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }

        /// <summary>One evaluation pass, refreshing the panel as the shell does.</summary>
        public void Pass()
        {
            Scheduler.RunAsync().GetAwaiter().GetResult();
            Panel.Prepare(Scheduler);
            Panel.Commit();
        }

        /// <summary>
        /// A tool button by its tag. Asked of the viewer rather than of a
        /// parent panel, because the toolbar moves what does not fit into
        /// an overflow menu - so which panel holds a button depends on how
        /// wide the viewer happens to be.
        /// </summary>
        public ToggleButton Tool(string tag) =>
            Viewer.ToolButtons().First(button => (button.Tag as string) == tag);

        /// <summary>Clicks a tool button the way the operator does.</summary>
        public void Pick(string tag)
        {
            var button = Tool(tag);
            button.IsChecked = true;
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }

        /// <summary>
        /// Stands in for the drag that places a line. The gesture itself needs
        /// a live mouse device; what it ends in is this.
        /// </summary>
        public Measurement Draw(Point from, Point to)
        {
            var measurement = new Measurement(
                ViewerTool.LineProfile,
                new[] { from, to },
                System.Windows.Media.Colors.Gold)
            {
                Name = "Profile 1",
            };

            Measurements.Add(measurement);
            Invoke("UpdateReading");
            return measurement;
        }

        public System.Collections.IList Measurements =>
            (System.Collections.IList)Viewer.GetType()
                .GetField("_measurements", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Viewer)!;

        public void Invoke(string method) =>
            Viewer.GetType()
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Viewer, null);

        public void Dispose()
        {
            Panel.Dispose();
            Scheduler.Dispose();
        }
    }

    /// <summary>
    /// The frame has to be asked for when the tool is picked, not when the
    /// first line lands: the graph reclaims its buffers between passes, so
    /// there is nothing to sample until a pass runs with profiling on.
    /// </summary>
    [Fact]
    public void Picking_the_profile_tool_asks_for_a_frame()
    {
        OnStaThread(() =>
        {
            using var harness = new Harness();

            harness.Pick("LineProfile");
            harness.Settle();

            Assert.Equal(ViewerTool.LineProfile, harness.Viewer.ActiveTool);
            Assert.True(harness.Viewer.IsProfilingEnabled, "the control did not ask for a frame");
            Assert.True(harness.Panel.IsProfilingEnabled, "the request did not reach the panel");
            Assert.NotNull(harness.Panel.ProfileSource);
            Assert.NotNull(harness.Viewer.ProfileSource);
        });
    }

    [Fact]
    public void A_line_drawn_with_the_tool_charts_immediately()
    {
        OnStaThread(() =>
        {
            using var harness = new Harness();

            harness.Pick("LineProfile");
            harness.Settle();
            harness.Draw(new Point(2, 10), new Point(60, 10));
            harness.Settle();

            var line = Assert.Single(harness.Panel.ProfileLines);
            Assert.Equal("Profile 1", line.Title);
            Assert.NotNull(line.Data);
            Assert.All(line.Data!.Gray().Samples, sample => Assert.Equal(120f, sample, 1));
        });
    }

    /// <summary>
    /// Changing the band re-samples through the viewer, which is the only
    /// thing holding the frame - the panel has no node to re-evaluate it.
    /// </summary>
    [Fact]
    public void Changing_the_band_re_samples()
    {
        OnStaThread(() =>
        {
            using var harness = new Harness();

            harness.Pick("LineProfile");
            harness.Settle();
            harness.Draw(new Point(2, 10), new Point(60, 10));
            harness.Settle();

            var line = harness.Panel.ProfileLines[0];
            line.Thickness = 5;

            Assert.NotNull(line.Data);
            Assert.Equal(5, line.Data!.Thickness);
        });
    }

    /// <summary>
    /// Deleting the line closes its window, and putting the tool away releases
    /// the frame - it is four bytes a pixel for as long as it is held.
    /// </summary>
    [Fact]
    public void Removing_the_last_line_and_the_tool_releases_the_frame()
    {
        OnStaThread(() =>
        {
            using var harness = new Harness();

            harness.Pick("LineProfile");
            harness.Settle();
            harness.Draw(new Point(2, 10), new Point(60, 10));
            harness.Settle();
            Assert.Single(harness.Panel.ProfileLines);

            harness.Measurements.Clear();
            harness.Pick("None");
            harness.Settle();

            Assert.Empty(harness.Panel.ProfileLines);
            Assert.False(harness.Viewer.IsProfilingEnabled);
            Assert.Null(harness.Panel.ProfileSource);
        });
    }

    /// <summary>
    /// The same loop, but with the viewer built by the application's own
    /// DataTemplate rather than by hand.
    /// </summary>
    /// <remarks>
    /// The harness above hand-writes the bindings the template declares,
    /// which tests the control and the panel but not the markup joining
    /// them. A property renamed on one side, or a binding left one-way,
    /// would pass everything above and still ship a chart that never fills.
    /// </remarks>
    [Fact]
    public void The_shipped_template_wires_the_same_loop()
    {
        OnStaThread(() =>
        {
            EnsureApplicationResources();

            var graph = new VisionGraph();
            var source = new ConstantImageNode { Width = 64, Height = 48, Value = 90 };
            var node = new ViewerNode();
            graph.Add(source);
            graph.Add(node);
            graph.TryConnect(source.ImageOut, node.ImageIn);

            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            GraphScheduler scheduler;
            try { scheduler = new GraphScheduler(graph); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }

            using var _ = scheduler;
            var panel = new ViewerPanelViewModel(node, new ShapeSelectionService());

            void Pass()
            {
                scheduler.RunAsync().GetAwaiter().GetResult();
                panel.Prepare(scheduler);
                panel.Commit();
            }

            panel.ProfileDataRequested += (_, _) => Pass();

            // Built the way the shell builds it: a presenter picks the
            // DataTemplate up from the merged theme by view-model type.
            var presenter = new ContentPresenter { Content = panel };
            presenter.Measure(new Size(900, 700));
            presenter.Arrange(new Rect(0, 0, 900, 700));
            presenter.UpdateLayout();

            var viewer = Descendants(presenter).OfType<ImageViewer>().SingleOrDefault();
            Assert.True(viewer is not null, "the template produced no ImageViewer");

            Pass();

            // The binding behind the switch has to survive the first frame
            // arriving: the control syncs itself when that happens, and a
            // write of the value it already holds would replace the binding
            // with a local value - after which nothing the control decides
            // ever reaches the panel.
            var report = string.Join(", ", new[]
            {
                ("ImageSource", ImageViewer.ImageSourceProperty),
                ("ProfileSource", ImageViewer.ProfileSourceProperty),
                ("IsProfilingEnabled", ImageViewer.IsProfilingEnabledProperty),
                ("ProfileLines", ImageViewer.ProfileLinesProperty),
                ("Shapes", ImageViewer.ShapesProperty),
            }.Select(entry =>
            {
                var bound = System.Windows.Data.BindingOperations.GetBindingExpression(viewer, entry.Item2);
                return $"{entry.Item1}={(bound is null ? "none" : bound.ParentBinding.Mode.ToString())}";
            }));

            Assert.Equal(
                "ImageSource=Default, ProfileSource=Default, IsProfilingEnabled=TwoWay, "
                + "ProfileLines=Default, Shapes=Default",
                report);

            var button = viewer!.ToolButtons()
                .First(candidate => (candidate.Tag as string) == "LineProfile");

            button.IsChecked = true;
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.True(
                panel.IsProfilingEnabled,
                "the template did not carry the switch to the panel");
            Assert.NotNull(panel.ProfileSource);

            var measurements = (System.Collections.IList)viewer.GetType()
                .GetField("_measurements", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(viewer)!;

            measurements.Add(new Measurement(
                ViewerTool.LineProfile,
                new[] { new Point(2, 10), new Point(60, 10) },
                System.Windows.Media.Colors.Gold) { Name = "Profile 1" });

            viewer.GetType()
                .GetMethod("UpdateReading", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewer, null);

            var line = Assert.Single(panel.ProfileLines);
            Assert.NotNull(line.Data);
        });
    }

    /// <summary>
    /// A preview of an image port draws a viewer and nothing else.
    /// </summary>
    /// <remarks>
    /// "The markup resolves the way I believe it does" is exactly the
    /// assumption that cost three rounds on the profile chart, so this
    /// builds one and looks at what comes out rather than reading the
    /// template.
    /// </remarks>
    [Fact]
    public void A_preview_is_drawn_by_the_image_panel_template()
    {
        OnStaThread(() =>
        {
            EnsureApplicationResources();

            var graph = new VisionGraph();
            var source = new ConstantImageNode { Width = 32, Height = 24, Value = 70 };
            var tool = new ThresholdNode();
            graph.Add(source);
            graph.Add(tool);
            graph.TryConnect(source.ImageOut, tool.ImageIn);

            var panel = new PreviewPanelViewModel(
                tool, tool.ImageOut, null, new ShapeSelectionService());

            var presenter = new ContentPresenter { Content = panel };
            presenter.Measure(new Size(600, 400));
            presenter.Arrange(new Rect(0, 0, 600, 400));
            presenter.UpdateLayout();

            Assert.Single(Descendants(presenter).OfType<ImageViewer>());
        });
    }

    /// <summary>
    /// A preview of a shape port draws the measurements as well. A shape set
    /// is data: the picture says where they are, the table says what they
    /// are, and the shared selection ties a row to a blob.
    /// </summary>
    [Fact]
    public void A_preview_of_shapes_draws_the_table_too()
    {
        OnStaThread(() =>
        {
            EnsureApplicationResources();

            var graph = new VisionGraph();
            var source = new ConstantImageNode { Width = 32, Height = 24, Value = 200 };
            var blobs = new BlobFilterNode();
            graph.Add(source);
            graph.Add(blobs);
            graph.TryConnect(source.ImageOut, blobs.ImageIn);

            var panel = new PreviewPanelViewModel(
                blobs, blobs.ShapesOut, blobs.ImageIn, new ShapeSelectionService());

            var presenter = new ContentPresenter { Content = panel };
            presenter.Measure(new Size(600, 400));
            presenter.Arrange(new Rect(0, 0, 600, 400));
            presenter.UpdateLayout();

            Assert.Single(Descendants(presenter).OfType<ImageViewer>());
            Assert.Single(Descendants(presenter).OfType<ShapeTableControl>());
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;

            foreach (var further in Descendants(child))
                yield return further;
        }
    }
}
