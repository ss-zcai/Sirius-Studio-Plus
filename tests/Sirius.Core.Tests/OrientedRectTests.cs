using System.IO;
using System.Drawing;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Serialization;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The region of interest as a single parameter.
/// </summary>
/// <remarks>
/// It was five - a centre, a size and an angle - which meant five rows in
/// the inspector and four connectable ports on the node, so a tool with a
/// region looked like an arithmetic unit.
/// </remarks>
public class OrientedRectTests
{
    public OrientedRectTests() => TestCatalog.Ensure();

    // ---- the value ----------------------------------------------------------

    [Fact]
    public void A_region_with_no_area_is_not_allowed()
    {
        var rect = new OrientedRect(10, 10, 0, -5, 0);

        Assert.Equal(1, rect.Width);
        Assert.Equal(1, rect.Height);
    }

    /// <summary>
    /// The upright form is the centre and size, not the bounding box of the
    /// rotated rectangle - which would grow a little every time the viewer
    /// read it back and wrote it out again.
    /// </summary>
    [Fact]
    public void The_upright_form_ignores_the_angle_rather_than_bounding_it()
    {
        var rect = new OrientedRect(100, 100, 40, 20, 45);

        Assert.Equal((80, 90, 40, 20), rect.Upright);
    }

    [Fact]
    public void Moving_and_resizing_keeps_the_angle()
    {
        var moved = new OrientedRect(100, 100, 40, 20, 30).WithUpright(0, 0, 10, 10);

        Assert.Equal(5, moved.CentreX);
        Assert.Equal(5, moved.CentreY);
        Assert.Equal(30, moved.Angle);
    }

    [Theory]
    [InlineData("10, 20, 30, 40, 50")]
    [InlineData("(10, 20, 30, 40, 50)")]
    [InlineData("  10 ,20,30 , 40 , 50 ")]
    public void It_parses_the_line_it_prints(string text)
    {
        Assert.True(OrientedRect.TryParse(text, out var rect));
        Assert.Equal(new OrientedRect(10, 20, 30, 40, 50), rect);
    }

    [Fact]
    public void An_angle_left_off_reads_as_upright()
    {
        Assert.True(OrientedRect.TryParse("10, 20, 30, 40", out var rect));
        Assert.Equal(0, rect.Angle);
    }

    [Theory]
    [InlineData("")]
    [InlineData("10, 20")]
    [InlineData("10, 20, 30, 40, 50, 60")]
    [InlineData("ten, 20, 30, 40")]
    public void Nonsense_is_refused_rather_than_guessed_at(string text) =>
        Assert.False(OrientedRect.TryParse(text, out _));

    [Fact]
    public void What_it_prints_parses_back_to_itself()
    {
        var rect = new OrientedRect(500, 500, 400, 300, -1.92);

        Assert.True(OrientedRect.TryParse(rect.ToString(), out var round));
        Assert.Equal(rect, round);
    }

    // ---- the node -----------------------------------------------------------

    /// <summary>
    /// Two inputs, not five. The region is one of them, and it can still be
    /// driven by a wire.
    /// </summary>
    [Fact]
    public void The_node_exposes_the_region_as_one_connectable_parameter()
    {
        var node = new RegionOfInterestNode();
        var rect = node.Descriptor.Parameters.Single(p => p.Name == "Rect");

        Assert.Equal(typeof(OrientedRect), rect.ValueType);
        Assert.Equal(ParamEditor.Rect, rect.Editor);
        Assert.True(rect.Attribute.Connectable);

        Assert.DoesNotContain(
            node.Descriptor.Parameters,
            p => p.Name is "CentreX" or "CentreY" or "Width" or "Height" or "Angle");
    }

    /// <summary>
    /// A recipe saved with the five numbers must cut the same region, not
    /// quietly fall back to the node's default.
    /// </summary>
    [Fact]
    public void A_recipe_saved_with_the_five_old_numbers_still_loads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"roi-{Guid.NewGuid():N}.sgraph");

        File.WriteAllText(path, """
        {
          "version": 2,
          "name": "legacy",
          "nodes": [
            {
              "id": "dddddddddddd4d7f8a1b2c3d4e5f6071",
              "type": "basic.roi",
              "name": "Region of Interest 1",
              "enabled": true,
              "x": 0,
              "y": 0,
              "params": {
                "CentreX": 500,
                "CentreY": 400,
                "Width": 300,
                "Height": 200,
                "Angle": -1.92
              }
            }
          ],
          "connections": []
        }
        """);

        try
        {
            var loaded = GraphSerializer.Load(path);
            var node = Assert.Single(loaded.Graph.Nodes.OfType<RegionOfInterestNode>());

            Assert.Equal(new OrientedRect(500, 400, 300, 200, -1.92), node.Rect);
            Assert.Empty(loaded.Warnings);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_region_survives_a_round_trip()
    {
        var graph = new VisionGraph();
        graph.Add(new RegionOfInterestNode { Rect = new OrientedRect(1, 2, 3, 4, 5) });

        var path = Path.Combine(Path.GetTempPath(), $"roi-{Guid.NewGuid():N}.sgraph");
        GraphSerializer.Save(graph, path, "roi");

        try
        {
            var node = Assert.Single(
                GraphSerializer.Load(path).Graph.Nodes.OfType<RegionOfInterestNode>());

            Assert.Equal(new OrientedRect(1, 2, 3, 4, 5), node.Rect);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- dragging it on the viewer -----------------------------------------

    private static ParameterViewModel RectParameter(RegionOfInterestNode node) =>
        new(node, node.Descriptor.Parameters.Single(p => p.Name == "Rect"));

    /// <summary>
    /// The viewer edits an upright rectangle, so a rect parameter has to
    /// answer as one - otherwise the Draw button opens an image with
    /// nothing on it to drag.
    /// </summary>
    [Fact]
    public void A_rect_parameter_reads_as_a_rectangle_for_the_viewer()
    {
        var node = new RegionOfInterestNode { Rect = new OrientedRect(100, 100, 40, 20, 45) };

        Assert.Equal(new Rectangle(80, 90, 40, 20), RectParameter(node).RegionValue);
    }

    /// <summary>
    /// And dragging writes back through it, leaving the angle alone -
    /// position and size are what a drag says, rotation is a separate
    /// question.
    /// </summary>
    [Fact]
    public void Dragging_the_region_moves_it_without_straightening_it()
    {
        var node = new RegionOfInterestNode { Rect = new OrientedRect(100, 100, 40, 20, 45) };

        RectParameter(node).RegionValue = new Rectangle(0, 0, 10, 10);

        Assert.Equal(new OrientedRect(5, 5, 10, 10, 45), node.Rect);
    }

    [Fact]
    public void Typing_the_line_sets_the_region()
    {
        var node = new RegionOfInterestNode();

        RectParameter(node).RectText = "(300, 200, 50, 60, 15)";

        Assert.Equal(new OrientedRect(300, 200, 50, 60, 15), node.Rect);
    }

    [Fact]
    public void Typing_nonsense_leaves_the_region_alone()
    {
        var node = new RegionOfInterestNode { Rect = new OrientedRect(1, 2, 3, 4, 5) };

        RectParameter(node).RectText = "not a rectangle";

        Assert.Equal(new OrientedRect(1, 2, 3, 4, 5), node.Rect);
    }
}
