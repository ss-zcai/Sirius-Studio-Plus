using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Sirius.Studio.App.Controls;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The image viewer's toolbar stays on one line.
/// </summary>
/// <remarks>
/// It used to wrap, which cost a second row of chrome on every viewer - on
/// a small screen with three viewers open, most of the window. What matters
/// is not only that it fits on one line but that nothing is lost doing so:
/// a tool pushed into the overflow menu is still a tool, and the radio
/// group it belongs to spans both panels.
/// </remarks>
[Collection(WpfCollection.Name)]
public class ViewerToolbarTests
{
    public ViewerToolbarTests(WpfTestHost host) => _ = host;

    private static void Laid(double width, Action<ImageViewer> test)
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var viewer = new ImageViewer();
            viewer.Measure(new Size(width, 600));
            viewer.Arrange(new Rect(0, 0, width, 600));
            viewer.UpdateLayout();

            test(viewer);
        });
    }

    /// <summary>Every tool, wherever the toolbar has put it.</summary>
    private static int ToolCount(ImageViewer viewer) => viewer.ToolButtons().Count();

    [Fact]
    public void A_wide_viewer_shows_everything_without_an_overflow_menu() =>
        Laid(1400, viewer =>
        {
            Assert.False(
                viewer.Tools.HasOverflowItems,
                "a wide viewer should not need the overflow menu");
        });

    /// <summary>
    /// The case this exists for. A narrow viewer must put the remainder
    /// somewhere rather than growing a second row or clipping it away.
    /// </summary>
    [Fact]
    public void A_narrow_viewer_moves_the_remainder_into_the_menu() =>
        Laid(240, viewer =>
        {
            Assert.True(
                viewer.Tools.HasOverflowItems,
                "a narrow viewer kept everything on the bar, so something is clipped");
        });

    /// <summary>
    /// The toolbar is one line at any width. Height is what the wrapping
    /// cost, so height is what is asserted.
    /// </summary>
    [Fact]
    public void The_bar_is_no_taller_when_the_viewer_is_narrow()
    {
        double? wide = null;

        Laid(1400, viewer => wide = BarHeight(viewer));
        Laid(240, viewer => Assert.Equal(wide!.Value, BarHeight(viewer), 0));
    }

    private static double BarHeight(ImageViewer viewer) => viewer.Tools.ActualHeight;

    /// <summary>
    /// A tool in the overflow menu is still part of the radio group. The
    /// group used to be found by scanning a button's parent panel, which
    /// returns half of it once the toolbar splits them in two.
    /// </summary>
    [Fact]
    public void Every_tool_is_still_reachable_when_the_bar_overflows()
    {
        var expected = 0;

        Laid(1400, viewer => expected = ToolCount(viewer));
        Assert.True(expected > 1, "the toolbar has no tools, so this proves nothing");

        Laid(240, viewer => Assert.Equal(expected, ToolCount(viewer)));
    }

    /// <summary>
    /// Picking a tool releases the others and nothing else. The overlay
    /// switches are toggles too, and turning them off by surprise would
    /// hide the measurements the operator just drew.
    /// </summary>
    [Fact]
    public void Picking_a_tool_in_a_narrow_viewer_leaves_the_overlays_alone() =>
        Laid(240, viewer =>
        {
            var profile = viewer.ToolButtons()
                .First(button => (button.Tag as string) == "LineProfile");

            profile.IsChecked = true;
            profile.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(ViewerTool.LineProfile, viewer.ActiveTool);
            Assert.True(viewer.ShowShapesButton.IsChecked, "picking a tool hid graph results");
            Assert.True(viewer.ShowMeasurementsButton.IsChecked, "picking a tool hid measurements");
        });
}
