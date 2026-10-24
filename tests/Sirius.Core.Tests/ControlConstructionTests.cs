
using System.Windows;

using Sirius.Studio.App.Controls;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Builds the viewer the way the application does.
/// </summary>
/// <remarks>
/// <para>
/// A smoke test for the viewer's markup: it builds the control against the
/// real theme dictionaries and lays it out, so a broken resource key, an
/// unresolvable type or a handler that throws on an empty control shows up as
/// a named failure carrying the exception.
/// </para>
/// <para>
/// It does <b>not</b> reproduce the construction-order fault that produced the
/// <c>_templateReady</c> flag. In the running application a pre-selected
/// ComboBox item raises SelectionChanged while the template is still being
/// built; here that event does not fire, so the test passes either way. It was
/// kept anyway - the markup is worth a guard - but the flag is what prevents
/// that fault, not this.
/// </para>
/// </remarks>
[Collection(WpfCollection.Name)]
public class ControlConstructionTests
{
    public ControlConstructionTests(WpfTestHost host) => _ = host;

    [Fact]
    public void The_image_viewer_constructs_with_its_whole_template()
    {
        OnStaThread(() =>
        {
            EnsureApplicationResources();

            var viewer = new ImageViewer();

            // Laying it out is the point, not constructing it. A ComboBox does
            // not raise SelectionChanged for a pre-selected item until its
            // containers are generated, which happens in the first measure - so
            // a handler running against a half-built tree only shows up here.
            viewer.Measure(new Size(900, 700));
            viewer.Arrange(new Rect(0, 0, 900, 700));
            viewer.UpdateLayout();

            // The parts the profile tool drives, all of which are touched by
            // handlers that can run before the template is complete.
            Assert.Equal(ViewerTool.None, viewer.ActiveTool);

            // The overlay switches start on.
            Assert.True(viewer.ShowShapesButton.IsChecked);
            Assert.True(viewer.ShowMeasurementsButton.IsChecked);

            // Picking a tool releases the other tools and nothing else. The
            // group is found by scanning the toolbar, so a toggle that is not
            // a tool would otherwise be switched off by surprise - turning the
            // overlay off when the operator picked a measuring tool.
            viewer.PointerTool.RaiseEvent(
                new System.Windows.RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.True(viewer.ShowShapesButton.IsChecked, "picking a tool hid graph results");
            Assert.True(viewer.ShowMeasurementsButton.IsChecked, "picking a tool hid measurements");
            Assert.Empty(viewer.ProfileLines ?? new System.Collections.ObjectModel.ObservableCollection<Sirius.Studio.App.ViewModels.ProfileLinePanelViewModel>());
        });
    }

    private static void OnStaThread(Action action) => WpfTestHost.OnStaThread(action);

    private static void EnsureApplicationResources() => WpfTestHost.EnsureApplicationResources();
}
