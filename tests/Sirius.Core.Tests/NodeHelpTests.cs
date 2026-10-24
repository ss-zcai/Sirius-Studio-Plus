using Sirius.Core.Nodes;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The help a tool gives about itself.
/// </summary>
/// <remarks>
/// Read off the descriptor rather than written down, so a parameter
/// renamed or a port added cannot leave a page quietly contradicting the
/// tool. These check that what is read is actually useful - a window that
/// faithfully reports nothing would pass a weaker test.
/// </remarks>
public class NodeHelpTests
{
    public NodeHelpTests() => TestCatalog.Ensure();

    [Fact]
    public void It_describes_the_tool_it_was_given()
    {
        var help = new NodeHelpViewModel(new BlobFilterNode());

        Assert.Equal("Find Blobs", help.Title);
        Assert.Equal("blob.filter", help.TypeKey);
        Assert.NotEmpty(help.Summary);
    }

    [Fact]
    public void It_lists_the_ports_with_types_an_operator_recognises()
    {
        var help = new NodeHelpViewModel(new MaskNode());

        Assert.Contains(help.Inputs, port => port.Name == "In" && port.Type == "Image");
        Assert.Contains(help.Inputs, port => port.Name == "Shapes" && port.Type == "Shapes");
        Assert.Contains(help.Outputs, port => port.Name == "Out" && port.Type == "Image");
    }

    /// <summary>
    /// Which inputs may be left unwired is the question most often asked of
    /// a node with several, and the node knows.
    /// </summary>
    [Fact]
    public void It_says_which_inputs_are_optional()
    {
        var help = new NodeHelpViewModel(new MaskNode());

        Assert.Equal(string.Empty, help.Inputs.Single(p => p.Name == "In").Note);
        Assert.Equal("optional", help.Inputs.Single(p => p.Name == "Shapes").Note);
    }

    [Fact]
    public void It_carries_the_descriptions_already_on_the_parameters()
    {
        var help = new NodeHelpViewModel(new MaskNode());
        var fill = help.Parameters.Single(p => p.Name == "Fill");

        Assert.True(fill.HasDescription);
        Assert.Equal("0 to 255", fill.Range);
    }

    /// <summary>A choice is more usefully shown as its options than as a type name.</summary>
    [Fact]
    public void A_choice_lists_what_can_be_chosen()
    {
        var help = new NodeHelpViewModel(new ImageLogicNode());
        var operation = help.Parameters.Single(p => p.Name == "Operation");

        Assert.Contains("And", operation.Range, StringComparison.Ordinal);
        Assert.Contains("Xor", operation.Range, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bound that follows the data has no fixed value worth printing -
    /// hue stops at 179 only once an HSV frame has arrived.
    /// </summary>
    [Fact]
    public void A_bound_that_depends_on_the_input_says_so()
    {
        var help = new NodeHelpViewModel(new ColourRangeNode());

        Assert.Equal("depends on the input", help.Parameters.First().Range);
    }

    /// <summary>
    /// The point of generating it: every node in the library is documented,
    /// including ones added after this was written.
    /// </summary>
    [Fact]
    public void Every_node_in_the_library_can_describe_itself()
    {
        VisionNodeLibrary.Register();

        var shipped = typeof(VisionNodeLibrary).Assembly;
        var undescribed = new List<string>();

        foreach (var descriptor in NodeCatalog.All.Where(d => d.NodeType.Assembly == shipped))
        {
            var help = new NodeHelpViewModel(descriptor.CreateInstance());

            if (string.IsNullOrWhiteSpace(help.Summary) || help.Summary == "No description yet.")
                undescribed.Add(descriptor.DisplayName);

            Assert.NotEmpty(help.Title);
            Assert.All(help.Inputs, port => Assert.NotEmpty(port.Type));
            Assert.All(help.Outputs, port => Assert.NotEmpty(port.Type));
        }

        Assert.True(
            undescribed.Count == 0,
            "No description on: " + string.Join(", ", undescribed));
    }

    // ---- the shared type table ---------------------------------------------

    /// <summary>
    /// There were three of these tables and they had drifted. One of them
    /// called a double "double" and another "Number", and only one knew
    /// what a ShapeSet was.
    /// </summary>
    [Theory]
    [InlineData(typeof(ImageData), "Image")]
    [InlineData(typeof(Sirius.Core.Shapes.ShapeSet), "Shapes")]
    [InlineData(typeof(int), "Integer")]
    [InlineData(typeof(double), "Number")]
    [InlineData(typeof(bool), "Yes/No")]
    public void Port_types_are_named_for_people(Type type, string expected) =>
        Assert.Equal(expected, TypeNames.Friendly(type));

    [Fact]
    public void An_unmapped_type_keeps_its_own_name() =>
        Assert.Equal("SomethingElse", TypeNames.Friendly("SomethingElse"));
}
