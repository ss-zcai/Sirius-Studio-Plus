using System.Windows;
using System.Windows.Controls;
using AvalonDock;
using AvalonDock.Layout;
using Sirius.Studio.App.Layout;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Opening a panel in a window of its own.
/// </summary>
/// <remarks>
/// <para>
/// One case, deliberately, and it asserts everything from a single float. A
/// real <see cref="DockingManager"/> is the only thing that can answer the
/// question worth asking here: the layout model is exactly what misled the
/// first attempt, since adding a <c>LayoutAnchorableFloatingWindow</c> to
/// <c>Layout.FloatingWindows</c> yields a consistent model, throws nothing,
/// satisfies every assertion that reads the model back - and shows no
/// window.
/// </para>
/// <para>
/// It is one case because AvalonDock keeps static state across docking
/// managers, and floating a second panel in the same process reuses a window
/// control the first case closed. That is a property of the test process,
/// not of the application, which floats into a manager that lives as long as
/// it does.
/// </para>
/// <para>
/// The host sits far off-screen: this needs a realised visual tree, and a
/// window flashing up mid-run is worse than an odd-looking coordinate.
/// </para>
/// </remarks>
[Collection(WpfCollection.Name)]
public class FloatingPanelTests
{
    public FloatingPanelTests(WpfTestHost host) => _ = host;

    private const double OffScreen = -20000;

    private static LayoutAnchorable Anchorable(string contentId) =>
        new()
        {
            Title = contentId,
            ContentId = contentId,
            Content = new TextBlock { Text = contentId },
        };

    /// <summary>
    /// The regression: the panel was in the model, inside a floating window,
    /// and no window ever appeared.
    /// </summary>
    [Fact]
    public void A_floated_panel_opens_a_window_and_leaves_the_docked_panels_alone()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var viewer = Anchorable("viewer:abc");

            var horizontal = new LayoutPanel { Orientation = Orientation.Horizontal };
            horizontal.Children.Add(new LayoutDocumentPane(
                new LayoutDocument { Title = "Graph", ContentId = "graph" }));
            horizontal.Children.Add(new LayoutAnchorablePane(viewer));

            var manager = new DockingManager
            {
                Layout = new LayoutRoot { RootPanel = horizontal },
            };

            var host = new Window
            {
                Content = manager,
                Width = 1000,
                Height = 700,
                Left = OffScreen,
                Top = OffScreen,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
            };

            host.Show();

            // The manager finishes setting itself up on the render pass after
            // the window appears, so nothing may run ahead of it.
            WpfTestHost.Settle(host.Dispatcher);

            try
            {
                Assert.Empty(manager.FloatingWindows);

                var profile = Anchorable("profileline:def");
                Float(manager, profile, new Point(OffScreen + 100, OffScreen + 120));

                // The manager shows the window on a later dispatcher pass,
                // so this has to wait for it - closing down first would run
                // that deferred Show against a window already torn down.
                WpfTestHost.Settle(host.Dispatcher);

                // A window exists, and it is the one holding the chart.
                var window = Assert.Single(manager.FloatingWindows);
                Assert.Contains(
                    window.Model.Descendents().OfType<LayoutAnchorable>(),
                    anchorable => anchorable.ContentId == "profileline:def");

                // The point of floating it: the viewer keeps its pane, rather
                // than the chart arriving as a tab over the image it measures.
                Assert.Single(Assert.IsType<LayoutAnchorablePane>(viewer.Parent).Children);
                Assert.DoesNotContain(
                    manager.Layout.RootPanel.Descendents().OfType<LayoutAnchorable>(),
                    anchorable => anchorable.ContentId == "profileline:def");

                // And it opens at the size and place it was given.
                Assert.Equal(OffScreen + 100, profile.FloatingLeft);
                Assert.Equal(OffScreen + 120, profile.FloatingTop);
                Assert.Equal(PanelLayout.DefaultFloatingSize.Width, profile.FloatingWidth);
                Assert.Equal(PanelLayout.DefaultFloatingSize.Height, profile.FloatingHeight);
            }
            finally
            {
                foreach (var floating in manager.FloatingWindows.ToArray())
                    floating.Close();

                host.Close();
                WpfTestHost.Settle(host.Dispatcher);
            }
        });
    }

    /// <summary>
    /// Mirrors <c>MainWindow.FloatPanel</c>: the manager has to build the
    /// window, which is what <c>AddToLayout</c> then <c>Float</c> makes it do.
    /// </summary>
    private static void Float(DockingManager manager, LayoutAnchorable anchorable, Point position)
    {
        anchorable.FloatingLeft = position.X;
        anchorable.FloatingTop = position.Y;
        anchorable.FloatingWidth = PanelLayout.DefaultFloatingSize.Width;
        anchorable.FloatingHeight = PanelLayout.DefaultFloatingSize.Height;

        anchorable.AddToLayout(manager, AnchorableShowStrategy.Most);
        anchorable.Float();
    }
}
