using Sirius.Core.Graph;
using Sirius.Core.Serialization;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// A vector is however many numbers it needs to be.
/// </summary>
/// <remarks>
/// It was a fixed pair with a numeric field per component, and two is the
/// wrong number as soon as anything wants three. One text box takes any
/// count, and the parsing is the part worth testing - a text box is the
/// only input in this application a person can put arbitrary characters
/// into.
/// </remarks>
public class NumberVectorTests
{
    public NumberVectorTests() => TestCatalog.Ensure();

    // ---- reading what people type -------------------------------------------

    [Theory]
    [InlineData("3, 3", new[] { 3d, 3d })]
    [InlineData("3,3", new[] { 3d, 3d })]
    [InlineData("3 3 1", new[] { 3d, 3d, 1d })]
    [InlineData("3, 3, 1", new[] { 3d, 3d, 1d })]
    [InlineData("  3 ,  3 ,1  ", new[] { 3d, 3d, 1d })]
    [InlineData("1.5, -2.25", new[] { 1.5, -2.25 })]
    [InlineData("7", new[] { 7d })]
    [InlineData("1,2,3,4,5,6", new[] { 1d, 2d, 3d, 4d, 5d, 6d })]
    public void It_reads_what_people_type(string text, double[] expected)
    {
        Assert.True(NumberVector.TryParse(text, out var vector));
        Assert.Equal(expected, vector.Components);
    }

    /// <summary>
    /// A half-typed vector is a normal state for a text box to be in, so
    /// these report failure rather than throwing or guessing.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,,")]
    [InlineData("3, banana")]
    [InlineData("--3")]
    public void Nonsense_is_refused(string? text)
    {
        Assert.False(NumberVector.TryParse(text, out var vector));
        Assert.Equal(0, vector.Count);
    }

    /// <summary>
    /// A trailing separator is tolerated, and reads as the components
    /// before it rather than as one more ending in zero.
    /// </summary>
    /// <remarks>
    /// Deliberately lenient. Typing "3, 3, 1" passes through "3, 3," on the
    /// way, and a node that went to Warning for that keystroke would flash
    /// a complaint at somebody who is doing nothing wrong. Text that cannot
    /// be read at all still warns.
    /// </remarks>
    [Theory]
    [InlineData("3, 3,", new[] { 3d, 3d })]
    [InlineData("3, 3, ", new[] { 3d, 3d })]
    [InlineData(",3, 3", new[] { 3d, 3d })]
    public void A_trailing_separator_is_tolerated(string text, double[] expected)
    {
        Assert.True(NumberVector.TryParse(text, out var vector));
        Assert.Equal(expected, vector.Components);
    }

    /// <summary>
    /// Parsed in the invariant culture, so a recipe written on a machine
    /// that uses a decimal comma reads the same here - which is also why a
    /// comma is only ever a separator.
    /// </summary>
    [Fact]
    public void A_comma_is_always_a_separator()
    {
        Assert.True(NumberVector.TryParse("1,5", out var vector));

        Assert.Equal(2, vector.Count);
        Assert.Equal(new[] { 1d, 5d }, vector.Components);
    }

    [Fact]
    public void It_round_trips_through_its_own_text()
    {
        var original = new NumberVector([3, 3, 1.5]);

        Assert.True(NumberVector.TryParse(original.ToString(), out var again));
        Assert.Equal(original, again);
    }

    // ---- reading it back out -------------------------------------------------

    /// <summary>
    /// Past the end reads zero rather than throwing, so a consumer that
    /// wants three components from a two-component vector gets a usable
    /// answer and can say so itself.
    /// </summary>
    [Fact]
    public void Past_the_end_is_zero()
    {
        var vector = new NumberVector([3, 4]);

        Assert.Equal(3, vector.X);
        Assert.Equal(4, vector.Y);
        Assert.Equal(0, vector.Z);
        Assert.Equal(0, vector[99]);
        Assert.Equal(0, vector[-1]);
    }

    [Fact]
    public void Take_pads_and_truncates()
    {
        var vector = new NumberVector([3, 4, 5]);

        Assert.Equal(new[] { 3d, 4d }, vector.Take(2));
        Assert.Equal(new[] { 3d, 4d, 5d, 0d }, vector.Take(4));
        Assert.Empty(vector.Take(0));
    }

    [Fact]
    public void Equality_is_by_value()
    {
        Assert.Equal(new NumberVector([1, 2]), new NumberVector([1, 2]));
        Assert.NotEqual(new NumberVector([1, 2]), new NumberVector([1, 2, 0]));
        Assert.Equal(
            new NumberVector([1, 2]).GetHashCode(),
            new NumberVector([1, 2]).GetHashCode());
    }

    // ---- the node -------------------------------------------------------------

    [Fact]
    public void The_node_starts_as_a_pair()
    {
        var node = new VectorNode();

        Assert.Equal("3, 3", node.Components);
        Assert.Equal(new[] { 3d, 3d }, node.Value.Components);
    }

    [Fact]
    public void The_node_emits_what_was_typed()
    {
        var graph = new VisionGraph();
        var node = new VectorNode { Components = "3, 3, 1" };
        graph.Add(node);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        var produced = (NumberVector?)scheduler.GetOutput(node.VectorOut);

        Assert.Equal(new[] { 3d, 3d, 1d }, produced!.Components);
        Assert.Equal(NodeStatus.Ready, node.Status);
    }

    /// <summary>
    /// Text that does not read is a warning, not a failed pass. Somebody is
    /// probably still typing.
    /// </summary>
    [Fact]
    public void Unreadable_text_warns_rather_than_failing()
    {
        var graph = new VisionGraph();
        var node = new VectorNode { Components = "3, banana" };
        graph.Add(node);

        using var scheduler = CreateScheduler(graph);
        scheduler.RunAsync().GetAwaiter().GetResult();

        Assert.Equal(NodeStatus.Warning, node.Status);
        Assert.Equal(0, ((NumberVector?)scheduler.GetOutput(node.VectorOut))!.Count);
    }

    // ---- recipes written before this -------------------------------------------

    /// <summary>
    /// The node stored X and Y as separate parameters. A recipe from then
    /// has to keep its vector rather than silently reverting to the
    /// default, which is the sort of loss nothing would report.
    /// </summary>
    [Fact]
    public void An_old_recipe_keeps_its_vector()
    {
        var graph = new VisionGraph();
        graph.Add(new VectorNode());

        var document = GraphSerializer.ToDocument(graph);
        var record = document.Nodes[0];

        record.Parameters.Clear();
        record.Parameters["X"] = System.Text.Json.JsonSerializer.SerializeToElement(5.0);
        record.Parameters["Y"] = System.Text.Json.JsonSerializer.SerializeToElement(7.0);

        var restored = (VectorNode)GraphSerializer.FromDocument(document).Graph.Nodes.Single();

        Assert.Equal(new[] { 5d, 7d }, restored.Value.Components);
    }

    /// <summary>
    /// Whichever order the file lists them in, since a JSON object has no
    /// guaranteed one.
    /// </summary>
    [Fact]
    public void An_old_recipe_keeps_its_vector_in_either_order()
    {
        var graph = new VisionGraph();
        graph.Add(new VectorNode());

        var document = GraphSerializer.ToDocument(graph);
        var record = document.Nodes[0];

        record.Parameters.Clear();
        record.Parameters["Y"] = System.Text.Json.JsonSerializer.SerializeToElement(7.0);
        record.Parameters["X"] = System.Text.Json.JsonSerializer.SerializeToElement(5.0);

        var restored = (VectorNode)GraphSerializer.FromDocument(document).Graph.Nodes.Single();

        Assert.Equal(new[] { 5d, 7d }, restored.Value.Components);
    }

    [Fact]
    public void A_vector_survives_a_round_trip()
    {
        var graph = new VisionGraph();
        graph.Add(new VectorNode { Components = "3, 3, 1" });

        var result = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));

        Assert.Empty(result.Warnings);
        Assert.Equal("3, 3, 1", ((VectorNode)result.Graph.Nodes.Single()).Components);
    }

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
}
