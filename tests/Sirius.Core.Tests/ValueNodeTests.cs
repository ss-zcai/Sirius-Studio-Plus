using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Serialization;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The value source nodes and the editable slider range they introduced.
/// </summary>
public class ValueNodeTests
{
    public ValueNodeTests() => TestCatalog.Ensure();

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

    [Fact]
    public async Task Integer_publishes_a_whole_number()
    {
        var graph = new VisionGraph();
        var node = new IntegerNode { Maximum = 500, Value = 42 };
        graph.Add(node);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Equal(42, scheduler.GetOutput(node.ValueOut));
    }

    [Fact]
    public async Task Float_publishes_a_real_number()
    {
        var graph = new VisionGraph();
        var node = new FloatNode { Maximum = 10, Value = 2.5 };
        graph.Add(node);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        Assert.Equal(2.5, scheduler.GetOutput(node.ValueOut));
    }

    [Fact]
    public void A_value_is_held_inside_its_range()
    {
        var node = new IntegerNode { Minimum = 10, Maximum = 20 };

        node.Value = 500;
        Assert.Equal(20, node.Value);

        node.Value = -5;
        Assert.Equal(10, node.Value);
    }

    /// <summary>
    /// Narrowing the range has to pull the value with it, or the slider would
    /// show a thumb outside its own track.
    /// </summary>
    [Fact]
    public void Narrowing_the_range_pulls_the_value_in()
    {
        var node = new IntegerNode { Maximum = 255, Value = 200 };

        node.Maximum = 100;

        Assert.Equal(100, node.Value);
    }

    /// <summary>
    /// Min and Max are typed one at a time, so the range is briefly inverted.
    /// That must not throw or snap the value somewhere unexpected.
    /// </summary>
    [Fact]
    public void An_inverted_range_is_tolerated_while_it_is_being_typed()
    {
        var node = new IntegerNode { Minimum = 0, Maximum = 100, Value = 50 };

        node.Minimum = 200; // above Max, until Max is typed too

        Assert.Equal(50, node.Value);

        node.Maximum = 300;
        Assert.Equal(200, node.Value);
    }

    [Fact]
    public async Task A_float_can_drive_a_connectable_parameter()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 32, Height = 32, Value = 200 };
        var level = new FloatNode { Maximum = 255, Value = 220 };
        var threshold = new ThresholdNode { MaxValue = 255 };

        graph.Add(source);
        graph.Add(level);
        graph.Add(threshold);
        graph.TryConnect(source.ImageOut, threshold.ImageIn);
        graph.TryConnect(level.ValueOut, threshold.FindInput("Threshold")!);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        // 220 is above the image value, so nothing passes.
        var image = scheduler.GetOutput<ImageData>(threshold.ImageOut)!;
        Assert.Equal(0, Emgu.CV.CvInvoke.Mean(image.Mat).V0, 0);
    }

    [Fact]
    public async Task An_integer_can_drive_a_double_parameter()
    {
        var graph = new VisionGraph();
        var level = new IntegerNode { Maximum = 255, Value = 90 };
        var threshold = new ThresholdNode();
        graph.Add(level);
        graph.Add(threshold);

        Assert.True(graph.TryConnect(level.ValueOut, threshold.FindInput("Threshold")!).Success);

        using var scheduler = CreateScheduler(graph);
        var result = await scheduler.RunAsync();

        Assert.NotNull(result);
    }

    [Fact]
    public async Task Vector_publishes_both_components()
    {
        var graph = new VisionGraph();
        var node = new VectorNode { X = 3, Y = 5 };
        graph.Add(node);

        using var scheduler = CreateScheduler(graph);
        await scheduler.RunAsync();

        var vector = Assert.IsType<Vector2>(scheduler.GetOutput(node.VectorOut));
        Assert.Equal(3, vector.X);
        Assert.Equal(5, vector.Y);
        Assert.Equal((3, 5), vector.ToInt());
    }

    [Fact]
    public void A_vector_reads_as_a_pair()
    {
        Assert.Equal("(3, 3)", new Vector2(3, 3).ToString());
        Assert.Equal("(0.5, 2.25)", new Vector2(0.5, 2.25).ToString());
    }

    [Fact]
    public void A_vector_cannot_be_wired_into_a_numeric_parameter()
    {
        var graph = new VisionGraph();
        var vector = new VectorNode();
        var threshold = new ThresholdNode();
        graph.Add(vector);
        graph.Add(threshold);

        var result = graph.TryConnect(vector.VectorOut, threshold.FindInput("Threshold")!);

        Assert.False(result.Success);
        Assert.Equal(ConnectionRejection.TypeMismatch, result.Reason);
    }

    [Fact]
    public void Value_nodes_round_trip_through_a_saved_recipe()
    {
        var graph = new VisionGraph();
        graph.Add(new IntegerNode { Minimum = 5, Maximum = 400, Value = 123 });
        graph.Add(new FloatNode { Minimum = -1, Maximum = 3, Value = 1.5 });
        graph.Add(new VectorNode { X = 7, Y = 9 });

        var loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));

        Assert.False(loaded.HasWarnings);

        var integer = loaded.Graph.Nodes.OfType<IntegerNode>().Single();
        Assert.Equal(123, integer.Value);
        Assert.Equal(400, integer.Maximum);

        var real = loaded.Graph.Nodes.OfType<FloatNode>().Single();
        Assert.Equal(1.5, real.Value);

        var vector = loaded.Graph.Nodes.OfType<VectorNode>().Single();
        Assert.Equal(7, vector.X);
        Assert.Equal(9, vector.Y);
    }

    /// <summary>
    /// "Number" produced a real number, so recipes naming it load as Float.
    /// </summary>
    [Fact]
    public void A_recipe_naming_the_old_Number_node_loads_as_Float()
    {
        const string json = """
        {
          "version": 2,
          "nodes": [
            { "id": "a", "type": "source.number", "enabled": true, "x": 0, "y": 0,
              "params": { "Value": 7.5 } }
          ],
          "connections": []
        }
        """;

        var loaded = GraphSerializer.Deserialize(json);

        var node = Assert.IsType<FloatNode>(loaded.Graph.Nodes.Single());
        Assert.Equal(7.5, node.Value);
    }

    /// <summary>
    /// Blank Image was renamed from Constant Image; its TypeKey did not change,
    /// which is what a stable key is for.
    /// </summary>
    [Fact]
    public void Renaming_a_node_type_does_not_break_saved_recipes()
    {
        const string json = """
        {
          "version": 2,
          "nodes": [
            { "id": "a", "type": "source.constant", "enabled": true, "x": 0, "y": 0,
              "params": { "Width": 64, "Height": 48 } }
          ],
          "connections": []
        }
        """;

        var loaded = GraphSerializer.Deserialize(json);

        var node = Assert.IsType<ConstantImageNode>(loaded.Graph.Nodes.Single());
        Assert.Equal(64, node.Width);
        Assert.Equal("Blank Image", node.Descriptor.DisplayName);
    }

    /// <summary>
    /// The slider range comes from other parameters, so the inspector has to
    /// follow them rather than a constant on the attribute.
    /// </summary>
    [Fact]
    public void The_slider_range_follows_the_min_and_max_parameters()
    {
        var node = new IntegerNode { Minimum = 10, Maximum = 90 };
        var descriptor = node.Descriptor.Parameters.Single(p => p.Name == nameof(IntegerNode.Value));
        var parameter = new ParameterViewModel(node, descriptor);

        Assert.Equal(10, parameter.Minimum);
        Assert.Equal(90, parameter.Maximum);

        var raised = new List<string?>();
        parameter.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        node.Maximum = 500;

        Assert.Equal(500, parameter.Maximum);
        Assert.Contains(nameof(ParameterViewModel.Maximum), raised);
    }
}
