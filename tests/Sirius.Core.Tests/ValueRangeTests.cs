using System.IO;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Serialization;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// A low and a high bound held as one parameter.
/// </summary>
public class ValueRangeTests
{
    public ValueRangeTests() => TestCatalog.Ensure();

    /// <summary>
    /// Ordered on construction, so nothing downstream has to ask. A range
    /// typed in backwards is a range, not an error.
    /// </summary>
    [Fact]
    public void A_range_orders_its_ends()
    {
        var range = new ValueRange(200, 50);

        Assert.Equal(50, range.Low);
        Assert.Equal(200, range.High);
    }

    [Fact]
    public void Clamping_holds_both_ends_inside_the_bounds()
    {
        var clamped = new ValueRange(-20, 400).Clamp(0, 255);

        Assert.Equal(0, clamped.Low);
        Assert.Equal(255, clamped.High);
    }

    [Theory]
    [InlineData(49, false)]
    [InlineData(50, true)]
    [InlineData(120, true)]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void Containment_includes_both_ends(double value, bool expected) =>
        Assert.Equal(expected, new ValueRange(50, 200).Contains(value));

    // ---- the node -----------------------------------------------------------

    /// <summary>
    /// The node rounds and refuses nonsense, but does not impose eight bits:
    /// how far a channel runs depends on the depth and the colour space -
    /// hue stops at 179 - and that belongs to the editor, which knows the
    /// space, not to a setter that a loading recipe calls before anything
    /// has been evaluated.
    /// </summary>
    [Fact]
    public void A_channel_range_is_rounded_and_cannot_go_negative()
    {
        var node = new ColourRangeNode { Blue = new ValueRange(-5.4, 200.6) };

        Assert.Equal(0, node.Blue.Low);
        Assert.Equal(201, node.Blue.High);
    }

    [Fact]
    public void A_colour_range_starts_wide_open()
    {
        var node = new ColourRangeNode();

        Assert.Equal(new ValueRange(0, 255), node.Blue);
        Assert.Equal(new ValueRange(0, 255), node.Green);
        Assert.Equal(new ValueRange(0, 255), node.Red);
    }

    // ---- saving and loading -------------------------------------------------

    private static string SaveAndReload(VisionGraph graph)
    {
        var path = Path.Combine(Path.GetTempPath(), $"range-{Guid.NewGuid():N}.sgraph");
        GraphSerializer.Save(graph, path, "ranges");
        return path;
    }

    [Fact]
    public void A_range_survives_a_round_trip()
    {
        var graph = new VisionGraph();
        var node = new ColourRangeNode
        {
            Blue = new ValueRange(10, 20),
            Green = new ValueRange(30, 40),
            Red = new ValueRange(50, 60),
        };
        graph.Add(node);

        var path = SaveAndReload(graph);

        try
        {
            var loaded = GraphSerializer.Load(path);
            var reloaded = Assert.Single(loaded.Graph.Nodes.OfType<ColourRangeNode>());

            Assert.Empty(loaded.Warnings);
            Assert.Equal(new ValueRange(10, 20), reloaded.Blue);
            Assert.Equal(new ValueRange(30, 40), reloaded.Green);
            Assert.Equal(new ValueRange(50, 60), reloaded.Red);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A recipe saved before the change carries six separate bounds. Ignoring
    /// them would open every channel wide, which is a different result
    /// reported only as a warning - and nothing would connect the two for
    /// whoever opened it.
    /// </summary>
    [Fact]
    public void A_recipe_saved_with_the_old_six_parameters_still_loads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"legacy-{Guid.NewGuid():N}.sgraph");

        File.WriteAllText(path, """
        {
          "version": 2,
          "name": "legacy",
          "nodes": [
            {
              "id": "aaaaaaaaaaaa4d7f8a1b2c3d4e5f6071",
              "type": "basic.colourRange",
              "name": "Colour Range 1",
              "enabled": true,
              "x": 0,
              "y": 0,
              "params": {
                "LowerBlue": 10,
                "UpperBlue": 20,
                "LowerGreen": 30,
                "UpperGreen": 40,
                "LowerRed": 50,
                "UpperRed": 60
              }
            }
          ],
          "connections": []
        }
        """);

        try
        {
            var loaded = GraphSerializer.Load(path);
            var node = Assert.Single(loaded.Graph.Nodes.OfType<ColourRangeNode>());

            Assert.Equal(new ValueRange(10, 20), node.Blue);
            Assert.Equal(new ValueRange(30, 40), node.Green);
            Assert.Equal(new ValueRange(50, 60), node.Red);
            Assert.Empty(loaded.Warnings);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The upper bound can be read before the lower one, and folding them in
    /// that order must not order the half-built range and swap the two.
    /// </summary>
    [Fact]
    public void The_old_bounds_fold_in_whatever_order_they_appear()
    {
        var path = Path.Combine(Path.GetTempPath(), $"legacy-{Guid.NewGuid():N}.sgraph");

        File.WriteAllText(path, """
        {
          "version": 2,
          "name": "legacy",
          "nodes": [
            {
              "id": "bbbbbbbbbbbb4d7f8a1b2c3d4e5f6071",
              "type": "basic.colourRange",
              "name": "Colour Range 1",
              "enabled": true,
              "x": 0,
              "y": 0,
              "params": { "UpperBlue": 200, "LowerBlue": 100 }
            }
          ],
          "connections": []
        }
        """);

        try
        {
            var node = Assert.Single(
                GraphSerializer.Load(path).Graph.Nodes.OfType<ColourRangeNode>());

            Assert.Equal(new ValueRange(100, 200), node.Blue);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- an absent end ------------------------------------------------------

    /// <summary>
    /// "No limit" is the absence of a limit, not a magic number. The blob
    /// filters used -1, which every description had to explain and which a
    /// slider cannot tell apart from a handle parked at the end.
    /// </summary>
    [Fact]
    public void An_absent_end_never_rejects()
    {
        var atLeast = new ValueRange(100, null);

        Assert.True(atLeast.Contains(1_000_000));
        Assert.False(atLeast.Contains(99));
        Assert.True(ValueRange.Unbounded.Contains(-5));
    }

    /// <summary>
    /// With one end missing there is nothing to order against, so the
    /// constructor must not treat the absence as a smaller number.
    /// </summary>
    [Fact]
    public void A_half_open_range_keeps_the_end_it_has()
    {
        Assert.Equal(100, new ValueRange(100, null).Low);
        Assert.Null(new ValueRange(100, null).High);

        Assert.Equal(100, new ValueRange(null, 100).High);
        Assert.Null(new ValueRange(null, 100).Low);
    }

    [Fact]
    public void Clamping_leaves_an_absent_end_absent()
    {
        var clamped = new ValueRange(null, 400).Clamp(0, 255);

        Assert.Null(clamped.Low);
        Assert.Equal(255, clamped.High);
    }

    [Fact]
    public void An_absent_end_survives_a_round_trip()
    {
        var graph = new VisionGraph();
        var node = new BlobFilterNode { Area = new ValueRange(500, null) };
        graph.Add(node);

        var path = SaveAndReload(graph);

        try
        {
            var reloaded = Assert.Single(
                GraphSerializer.Load(path).Graph.Nodes.OfType<BlobFilterNode>());

            Assert.Equal(500, reloaded.Area.Low);
            Assert.Null(reloaded.Area.High);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- the blob filters' own old names ------------------------------------

    /// <summary>
    /// The colour channels used "LowerBlue" and the blob filters "AreaMin",
    /// so the fold has to know both. A recipe in use carries
    /// <c>"AreaMin": 50000</c>, and losing it would quietly stop filtering.
    /// </summary>
    [Fact]
    public void A_recipe_with_the_old_blob_filter_names_still_loads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blobs-{Guid.NewGuid():N}.sgraph");

        File.WriteAllText(path, """
        {
          "version": 2,
          "name": "legacy",
          "nodes": [
            {
              "id": "cccccccccccc4d7f8a1b2c3d4e5f6071",
              "type": "blob.filter",
              "name": "Find PCB board",
              "enabled": true,
              "x": 0,
              "y": 0,
              "params": {
                "AreaMin": 50000,
                "AreaMax": -1,
                "CircularityMin": 0.5,
                "HolesMax": 3
              }
            }
          ],
          "connections": []
        }
        """);

        try
        {
            var loaded = GraphSerializer.Load(path);
            var node = Assert.Single(loaded.Graph.Nodes.OfType<BlobFilterNode>());

            Assert.Equal(50000, node.Area.Low);

            // -1 meant no limit, and now there is simply no upper end.
            Assert.Null(node.Area.High);

            Assert.Equal(0.5, node.Circularity.Low);
            Assert.Equal(3, node.Holes.High);
            Assert.Null(node.Holes.Low);
            Assert.Empty(loaded.Warnings);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A filter nobody set must not start rejecting things, and must not
    /// make the node measure what it was not asked for.
    /// </summary>
    [Fact]
    public void A_fresh_blob_node_filters_nothing()
    {
        var node = new BlobFilterNode();

        Assert.True(node.Area.IsUnbounded);
        Assert.True(node.Circularity.IsUnbounded);
        Assert.True(node.Holes.IsUnbounded);
    }
}
