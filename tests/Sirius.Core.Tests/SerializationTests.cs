using System.Drawing;
using System.IO;
using Emgu.CV.CvEnum;
using Sirius.Core.Graph;
using Sirius.Core.Serialization;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

public class SerializationTests
{
    public SerializationTests() => TestCatalog.Ensure();

    [Fact]
    public void Round_trip_preserves_nodes_connections_and_parameters()
    {
        var graph = new VisionGraph();
        var source = new ImageFileSourceNode { FilePath = @"C:\images\part.tif", X = 10, Y = 20 };
        var threshold = new ThresholdNode
        {
            Threshold = 77,
            MaxValue = 200,
            ThresholdType = ThresholdType.BinaryInv,
            Name = "My Threshold",
            X = 300,
            Y = 40,
        };
        var viewer = new ViewerNode { X = 600, Y = 40 };

        graph.Add(source);
        graph.Add(threshold);
        graph.Add(viewer);
        graph.TryConnect(source.ImageOut, threshold.ImageIn);
        graph.TryConnect(threshold.ImageOut, viewer.ImageIn);

        var json = GraphSerializer.Serialize(graph, "test");
        var loaded = GraphSerializer.Deserialize(json);

        Assert.False(loaded.HasWarnings);
        Assert.Equal(3, loaded.Graph.Nodes.Count);
        Assert.Equal(2, loaded.Graph.Connections.Count);

        var loadedThreshold = loaded.Graph.Nodes.OfType<ThresholdNode>().Single();
        Assert.Equal(77, loadedThreshold.Threshold);
        Assert.Equal(200, loadedThreshold.MaxValue);
        Assert.Equal(ThresholdType.BinaryInv, loadedThreshold.ThresholdType);
        Assert.Equal("My Threshold", loadedThreshold.Name);
        Assert.Equal(300, loadedThreshold.X);
        Assert.Equal(40, loadedThreshold.Y);

        var loadedSource = loaded.Graph.Nodes.OfType<ImageFileSourceNode>().Single();
        Assert.Equal(@"C:\images\part.tif", loadedSource.FilePath);
    }

    [Fact]
    public void Round_trip_preserves_the_disabled_state()
    {
        var graph = new VisionGraph();
        graph.Add(new ThresholdNode { Enabled = false });

        var loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));

        Assert.False(loaded.Graph.Nodes.Single().Enabled);
    }

    [Fact]
    public void Rectangle_parameters_round_trip()
    {
        var graph = new VisionGraph();
        graph.Add(new CropNode { Roi = new Rectangle(12, 34, 56, 78) });

        var loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));

        Assert.Equal(new Rectangle(12, 34, 56, 78), loaded.Graph.Nodes.OfType<CropNode>().Single().Roi);
    }

    [Fact]
    public void Enums_are_written_as_names_so_reordering_them_is_safe()
    {
        var graph = new VisionGraph();
        graph.Add(new ColourConvertNode { Conversion = ColorConversion.Bgr2Hsv });

        var json = GraphSerializer.Serialize(graph);

        Assert.Contains("Bgr2Hsv", json);
    }

    [Fact]
    public void An_unknown_node_type_is_skipped_with_a_warning_rather_than_failing_the_load()
    {
        const string json = """
        {
          "version": 1,
          "nodes": [
            { "id": "a", "type": "does.not.exist", "name": "Ghost", "enabled": true, "x": 0, "y": 0, "params": {} }
          ],
          "connections": []
        }
        """;

        var loaded = GraphSerializer.Deserialize(json);

        Assert.Empty(loaded.Graph.Nodes);
        Assert.True(loaded.HasWarnings);
        Assert.Contains(loaded.Warnings, w => w.Contains("does.not.exist"));
    }

    [Fact]
    public void An_unknown_parameter_is_ignored_and_the_node_still_loads()
    {
        const string json = """
        {
          "version": 1,
          "nodes": [
            {
              "id": "a", "type": "basic.threshold", "name": "T", "enabled": true, "x": 0, "y": 0,
              "params": { "Threshold": 50, "ParameterThatWasRenamed": 1 }
            }
          ],
          "connections": []
        }
        """;

        var loaded = GraphSerializer.Deserialize(json);

        var node = Assert.IsType<ThresholdNode>(loaded.Graph.Nodes.Single());
        Assert.Equal(50, node.Threshold);
        Assert.Contains(loaded.Warnings, w => w.Contains("ParameterThatWasRenamed"));
    }

    [Fact]
    public void A_newer_file_format_is_refused_with_a_clear_message()
    {
        var json = $$"""
        { "version": {{GraphDocument.CurrentVersion + 1}}, "nodes": [], "connections": [] }
        """;

        var ex = Assert.Throws<InvalidDataException>(() => GraphSerializer.Deserialize(json));
        Assert.Contains("newer version", ex.Message);
    }

    [Fact]
    public void A_connection_to_a_missing_node_is_dropped_with_a_warning()
    {
        const string json = """
        {
          "version": 1,
          "nodes": [
            { "id": "a", "type": "basic.threshold", "enabled": true, "x": 0, "y": 0, "params": {} }
          ],
          "connections": [
            { "fromNode": "ghost", "fromPort": "Image", "toNode": "a", "toPort": "Image" }
          ]
        }
        """;

        var loaded = GraphSerializer.Deserialize(json);

        Assert.Empty(loaded.Graph.Connections);
        Assert.Contains(loaded.Warnings, w => w.Contains("ghost"));
    }

    [Fact]
    public void Node_ids_survive_a_round_trip_so_connections_stay_attached()
    {
        var graph = new VisionGraph();
        var source = new ImageFileSourceNode();
        var viewer = new ViewerNode();
        graph.Add(source);
        graph.Add(viewer);
        graph.TryConnect(source.ImageOut, viewer.ImageIn);

        var originalId = source.Id;
        var loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));

        Assert.Equal(originalId, loaded.Graph.Nodes.OfType<ImageFileSourceNode>().Single().Id);
        Assert.Single(loaded.Graph.Connections);
    }
}
