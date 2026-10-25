using Emgu.CV;
using Sirius.Core.Graph;
using Sirius.Core.Serialization;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Parameters exposed as optional input ports: the inspector value is a
/// fallback, and a connection overrides it.
/// </summary>
public class ConnectableParameterTests
{
    public ConnectableParameterTests() => TestCatalog.Ensure();

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

    private static double MeanOf(ImageData image) => CvInvoke.Mean(image.Mat).V0;

    [Fact]
    public void A_connectable_parameter_appears_as_an_optional_input_port()
    {
        var node = new ThresholdNode();

        var port = node.FindInput("Threshold");

        Assert.NotNull(port);
        Assert.False(port!.IsRequired);
        Assert.True(node.IsParameterPort(port));
    }

    /// <summary>
    /// Parameter ports follow the data ports, so a node reads In / Out first
    /// and tuning values below.
    /// </summary>
    [Fact]
    public void Parameter_ports_come_after_the_data_ports()
    {
        var node = new ThresholdNode();

        Assert.Equal("In", node.Inputs[0].Name);
        Assert.False(node.IsParameterPort(node.Inputs[0]));
        Assert.Contains(node.Inputs.Skip(1), p => p.Name == "Threshold");
    }

    [Fact]
    public void A_non_connectable_parameter_gets_no_port()
    {
        var node = new ThresholdNode();

        // Max Value and Type stay editor-only.
        Assert.Null(node.FindInput("MaxValue"));
        Assert.Null(node.FindInput("ThresholdType"));
    }

    [Fact]
    public void Bypass_forwards_the_image_rather_than_a_parameter()
    {
        var node = new ThresholdNode();

        Assert.NotNull(node.PrimaryInput);
        Assert.Equal("In", node.PrimaryInput!.Name);
    }

    [Fact]
    public async Task An_unconnected_parameter_uses_the_inspector_value()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 64, Height = 64, Value = 200 };
        var threshold = new ThresholdNode { Threshold = 100, MaxValue = 255 };
        graph.Add(source);
        graph.Add(threshold);
        graph.TryConnect(source.ImageOut, threshold.ImageIn);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        // 200 sits above the level of 100, so the whole image passes.
        Assert.Equal(255, MeanOf(scheduler.GetOutput<ImageData>(threshold.ImageOut)!), 0);
    }

    [Fact]
    public async Task A_connected_parameter_overrides_the_inspector_value()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 64, Height = 64, Value = 200 };
        var level = new NumberNode { Maximum = 255, Value = 220 };
        var threshold = new ThresholdNode { Threshold = 100, MaxValue = 255 };

        graph.Add(source);
        graph.Add(level);
        graph.Add(threshold);
        graph.TryConnect(source.ImageOut, threshold.ImageIn);
        Assert.True(graph.TryConnect(level.ValueOut, threshold.FindInput("Threshold")!).Success);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        // The wire says 220, above the image value of 200, so nothing passes -
        // which could not happen if the inspector value of 100 were used.
        Assert.Equal(0, MeanOf(scheduler.GetOutput<ImageData>(threshold.ImageOut)!), 0);
    }

    [Fact]
    public async Task Changing_the_driving_node_re_evaluates_the_consumer()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 32, Height = 32, Value = 200 };
        var level = new NumberNode { Maximum = 255, Value = 220 };
        var threshold = new ThresholdNode { MaxValue = 255 };

        graph.Add(source);
        graph.Add(level);
        graph.Add(threshold);
        graph.TryConnect(source.ImageOut, threshold.ImageIn);
        graph.TryConnect(level.ValueOut, threshold.FindInput("Threshold")!);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();
        Assert.Equal(0, MeanOf(scheduler.GetOutput<ImageData>(threshold.ImageOut)!), 0);

        level.Value = 100;
        await scheduler.RunAsync();

        Assert.Equal(255, MeanOf(scheduler.GetOutput<ImageData>(threshold.ImageOut)!), 0);
    }

    /// <summary>
    /// An integer output driving a double parameter should not need an adapter
    /// node in between.
    /// </summary>
    [Fact]
    public void Numeric_types_are_interchangeable_across_a_connection()
    {
        var graph = new VisionGraph();
        var level = new NumberNode();
        var threshold = new ThresholdNode();
        graph.Add(level);
        graph.Add(threshold);

        Assert.True(graph.TryConnect(level.ValueOut, threshold.FindInput("Threshold")!).Success);
    }

    [Fact]
    public void An_image_cannot_be_wired_into_a_numeric_parameter()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode();
        var threshold = new ThresholdNode();
        graph.Add(source);
        graph.Add(threshold);

        var result = graph.TryConnect(source.ImageOut, threshold.FindInput("Threshold")!);

        Assert.False(result.Success);
        Assert.Equal(ConnectionRejection.TypeMismatch, result.Reason);
    }

    [Fact]
    public void Parameter_connections_survive_a_save_and_load()
    {
        var graph = new VisionGraph();
        var level = new NumberNode { Maximum = 255, Value = 77 };
        var threshold = new ThresholdNode();
        graph.Add(level);
        graph.Add(threshold);
        graph.TryConnect(level.ValueOut, threshold.FindInput("Threshold")!);

        var loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));

        Assert.False(loaded.HasWarnings);
        Assert.Single(loaded.Graph.Connections);
        Assert.True(loaded.Graph.Nodes.OfType<ThresholdNode>().Single().FindInput("Threshold")!.IsConnected);
    }

    /// <summary>
    /// Format version 1 called both image ports "Image"; those files must
    /// still load after the rename to In / Out.
    /// </summary>
    [Fact]
    public void A_version_1_graph_still_loads_after_the_port_rename()
    {
        const string json = """
        {
          "version": 1,
          "nodes": [
            { "id": "a", "type": "source.imageFile", "enabled": true, "x": 0, "y": 0, "params": {} },
            { "id": "b", "type": "basic.threshold", "enabled": true, "x": 200, "y": 0, "params": {} }
          ],
          "connections": [
            { "fromNode": "a", "fromPort": "Image", "toNode": "b", "toPort": "Image" }
          ]
        }
        """;

        var loaded = GraphSerializer.Deserialize(json);

        Assert.Empty(loaded.Warnings);
        Assert.Single(loaded.Graph.Connections);

        var connection = loaded.Graph.Connections.Single();
        Assert.Equal("Out", connection.Source.Name);
        Assert.Equal("In", connection.Target.Name);
    }
}

