using System.Drawing;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Editing a region on a panel.
/// </summary>
/// <remarks>
/// Two things a region has to get right: it can be turned, and it only
/// appears where it means something. The second is not cosmetic - a region
/// drawn over a node's *output* is drawn over the cut it produced, in
/// coordinates it has nothing to do with.
/// </remarks>
public class RoiEditingTests
{
    public RoiEditingTests() => TestCatalog.Ensure();

    private static (RegionOfInterestNode Node, ParameterViewModel Rect) Roi()
    {
        var node = new RegionOfInterestNode { Rect = new OrientedRect(100, 100, 40, 20, 30) };

        return (node, new ParameterViewModel(
            node, node.Descriptor.Parameters.Single(p => p.Name == "Rect")));
    }

    // ---- turning ------------------------------------------------------------

    [Fact]
    public void A_panel_offers_the_regions_angle()
    {
        var (_, rect) = Roi();
        var panel = new ViewerPanelViewModel(new ViewerNode(), new ShapeSelectionService())
        {
            RegionParameter = rect,
        };

        Assert.Equal(30, panel.RoiAngle);
    }

    [Fact]
    public void Turning_it_on_the_panel_reaches_the_node()
    {
        var (node, rect) = Roi();
        var panel = new ViewerPanelViewModel(new ViewerNode(), new ShapeSelectionService())
        {
            RegionParameter = rect,
        };

        panel.RoiAngle = -45;

        Assert.Equal(-45, node.Rect.Angle);
    }

    /// <summary>
    /// Turning must not move or resize it. The region is held upright with
    /// an angle beside it precisely so the two cannot interfere.
    /// </summary>
    [Fact]
    public void Turning_it_leaves_where_and_how_big_alone()
    {
        var (node, rect) = Roi();
        var panel = new ViewerPanelViewModel(new ViewerNode(), new ShapeSelectionService())
        {
            RegionParameter = rect,
        };

        panel.RoiAngle = 75;

        Assert.Equal(new OrientedRect(100, 100, 40, 20, 75), node.Rect);
    }

    /// <summary>A plain rectangle has no angle, and must not pretend to.</summary>
    [Fact]
    public void A_plain_rectangle_parameter_reports_no_angle()
    {
        var crop = new CropNode();
        var region = crop.Descriptor.Parameters.FirstOrDefault(p => p.Editor == ParamEditor.Region);

        Assert.True(region is not null, "Crop no longer has a region parameter; pick another node");

        var panel = new ViewerPanelViewModel(new ViewerNode(), new ShapeSelectionService())
        {
            RegionParameter = new ParameterViewModel(crop, region!),
        };

        panel.RoiAngle = 45;

        Assert.Equal(0, panel.RoiAngle);
    }

    // ---- where a region may be drawn ---------------------------------------

    private static PreviewPanelViewModel Preview(RegionOfInterestNode node, Port port) =>
        new(node, port, null, new ShapeSelectionService());

    /// <summary>
    /// The regression: the region was pushed to every panel, so the preview
    /// of the node's *output* drew the region over the cut.
    /// </summary>
    [Fact]
    public void A_preview_of_the_output_refuses_the_region()
    {
        var (node, rect) = Roi();
        var panel = Preview(node, node.ImageOut);

        panel.RegionParameter = rect;

        Assert.False(panel.IsRoiEnabled, "the region was drawn over the node's own output");
    }

    [Fact]
    public void A_preview_of_the_input_accepts_it()
    {
        var (node, rect) = Roi();
        var panel = Preview(node, node.ImageIn);

        panel.RegionParameter = rect;

        Assert.True(panel.IsRoiEnabled, "the region cannot be drawn where it belongs");
    }

    /// <summary>
    /// Another node's input is another node's coordinates, however similar
    /// the picture happens to look.
    /// </summary>
    [Fact]
    public void A_preview_of_another_nodes_input_refuses_it()
    {
        var (_, rect) = Roi();
        var other = new RegionOfInterestNode();
        var panel = Preview(other, other.ImageIn);

        panel.RegionParameter = rect;

        Assert.False(panel.IsRoiEnabled);
    }

    /// <summary>
    /// A viewer shows whatever is wired to it and cannot know whose
    /// coordinates that is, so it still takes any region - the behaviour it
    /// has always had, and what the Image Viewer workflow relies on.
    /// </summary>
    [Fact]
    public void A_viewer_panel_still_accepts_any_region()
    {
        var (_, rect) = Roi();
        var panel = new ViewerPanelViewModel(new ViewerNode(), new ShapeSelectionService())
        {
            RegionParameter = rect,
        };

        Assert.True(panel.IsRoiEnabled);
    }
}
