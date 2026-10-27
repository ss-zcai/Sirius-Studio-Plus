using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AvalonDock;
using AvalonDock.Layout;
using Sirius.Studio.App;
using Sirius.Studio.App.ViewModels;
using Sirius.Core.Nodes;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// What the properties panel shows when nothing is selected.
/// </summary>
/// <remarks>
/// It used to show all of it: an empty name box, a help button, an
/// Enabled tick, "This node has no parameters" and an empty Status
/// panel - a page of controls bound to nothing, inviting use. One line
/// of text says everything there is to say.
///
/// Built by constructing the real window, which is also the only test
/// that parses MainWindow.xaml rather than reading it as a string.
/// </remarks>
[Collection(WpfCollection.Name)]
public class PropertiesPanelTests
{
    public PropertiesPanelTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
    }

    /// <summary>The scroller holding the selected node's editors.</summary>
    /// <remarks>
    /// Reached through the dock manager rather than by walking the visual
    /// tree: a panel's content is not realised into that tree until the
    /// window is shown, and showing one in a test is a window on somebody's
    /// screen.
    /// </remarks>
    private static ScrollViewer Body(MainWindow window)
    {
        var docking = (DockingManager)window.FindName("Docking");

        var properties = docking.Layout
            .Descendents()
            .OfType<LayoutContent>()
            .Single(c => c.ContentId == "properties");

        var content = (FrameworkElement)properties.Content;

        // An unhosted panel has no parent to inherit from, so the view
        // model is handed to it the way the window would once it is shown.
        content.DataContext = window.DataContext;

        return Descendants(content).OfType<ScrollViewer>().Single();
    }

    /// <summary>Walks the logical tree, which exists without a layout pass.</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;

            foreach (var further in Descendants(child))
                yield return further;
        }
    }

    /// <summary>
    /// The output readings reach the panel, which is the half a view
    /// model test cannot see: a renamed property or a mistyped binding
    /// path leaves the section silently empty.
    /// </summary>
    [Fact]
    public void A_nodes_output_readings_reach_the_panel() =>
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var window = new MainWindow();
            window.Measure(new Size(1600, 900));
            window.Arrange(new Rect(0, 0, 1600, 900));
            window.UpdateLayout();

            var model = (MainViewModel)window.DataContext;

            Assert.Empty(model.SelectedNodeReadouts);
            Assert.False(model.HasReadouts);

            model.Editor.AddNode(
                NodeCatalog.FindByTypeKey("calibration.scale")!, new Point(0, 0));

            WpfTestHost.Settle(window.Dispatcher);

            // Calibration and Units/Pixel: the two a Calibrate publishes
            // that are not a picture.
            Assert.Equal(2, model.SelectedNodeReadouts.Count);
            Assert.True(model.HasReadouts);

            var list = Descendants(Body(window))
                .OfType<ItemsControl>()
                .Single(items => Path(items) == "DataContext.SelectedNodeReadouts");

            Assert.Equal(model.SelectedNodeReadouts.Count, list.Items.Count);
        });

    private static string? Path(ItemsControl items) =>
        BindingOperations.GetBinding(items, ItemsControl.ItemsSourceProperty)?.Path.Path;

    [Fact]
    public void The_editors_are_hidden_until_a_node_is_selected() =>
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var window = new MainWindow();
            window.Measure(new Size(1600, 900));
            window.Arrange(new Rect(0, 0, 1600, 900));
            window.UpdateLayout();

            var model = (MainViewModel)window.DataContext;
            var body = Body(window);

            WpfTestHost.Settle(window.Dispatcher);

            Assert.Null(model.Editor.SelectedNode);
            Assert.Equal(Visibility.Collapsed, body.Visibility);

            model.Editor.AddNode(
                NodeCatalog.FindByTypeKey("basic.threshold")!, new Point(0, 0));

            WpfTestHost.Settle(window.Dispatcher);

            Assert.NotNull(model.Editor.SelectedNode);
            Assert.Equal(Visibility.Visible, body.Visibility);
        });
}
