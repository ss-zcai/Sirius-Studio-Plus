using NodeGraphKit.Model;
using NodeGraphKit.Serialization;

namespace NodeGraphKit.Demo;

/// <summary>
/// Five sample tools - enough to show every socket shape and every kind of
/// parameter editor. None of them computes anything: replace them with
/// your own definitions, and your own code for what they do.
/// </summary>
public static class SampleNodes
{
    // Port types: a colour and a shape each. Register yours the same way.
    public static readonly PortType Image = PortTypes.Register("Image", "#60A5FA", PortShape.Square);
    public static readonly PortType Number = PortTypes.Register("Number", "#FBBF24");
    public static readonly PortType Boolean = PortTypes.Register("Boolean", "#F472B6");

    public static readonly NodeDefinition ImageFile = new("sample.image_file", "Image File")
    {
        Category = "Sources",
        Icon = "ph_image_file",
        Description = "Reads an image from disk.",
        Outputs = [new("Image", Image, "The picture that was read")],
        Parameters =
        [
            ParameterDefinition.Text("Path", @"C:\images\part.png", "The file to read"),
            ParameterDefinition.Boolean("Greyscale", false, "Read as one channel"),
        ],
    };

    public static readonly NodeDefinition NumberValue = new("sample.number", "Number")
    {
        Category = "Sources",
        Icon = "ph_number",
        Description = "A constant.",
        Outputs = [new("Value", Number)],
        Parameters = [ParameterDefinition.Number("Value", 1, step: 0.5)],
    };

    public static readonly NodeDefinition Add = new("sample.add", "Add")
    {
        Category = "Maths",
        Icon = "ph_counter",
        Description = "Adds two numbers, then scales the sum.",
        Inputs = [new("A", Number), new("B", Number)],
        Outputs = [new("Sum", Number)],
        Parameters = [ParameterDefinition.Number("Scale", 1, minimum: 0, maximum: 10, step: 0.1)],
    };

    public static readonly NodeDefinition Compare = new("sample.compare", "Compare")
    {
        Category = "Maths",
        Icon = "ph_compare",
        Description = "Passes when the value is on the right side of the limit.",
        Inputs = [new("Value", Number)],
        Outputs = [new("Pass", Boolean)],
        Parameters =
        [
            ParameterDefinition.Choice("Operator", ["Greater than", "Less than", "Equal to"]),
            ParameterDefinition.Number("Limit", 5, minimum: 0, maximum: 100, step: 1),
            ParameterDefinition.Integer("Decimals", 2, minimum: 0, maximum: 6),
        ],
    };

    public static readonly NodeDefinition Viewer = new("sample.viewer", "Viewer")
    {
        Category = "Output",
        Icon = "ph_image_viewer",
        Description = "Shows an image, with anything as its caption.",
        Inputs = [new("Image", Image), new("Caption", PortTypes.Any, "Takes any type")],
        Parameters = [ParameterDefinition.Text("Title", "Result")],
    };

    public static NodeCatalog Catalog() => new([ImageFile, NumberValue, Add, Compare, Viewer]);

    /// <summary>The graph the demo opens with.</summary>
    public static string StarterGraph(NodeCatalog catalog)
    {
        var graph = new NodeGraph();

        GraphNode Place(NodeDefinition definition, double x, double y, string? name = null)
        {
            var node = new GraphNode(definition) { X = x, Y = y };
            if (name is not null)
                node.Name = name;

            graph.Add(node);
            return node;
        }

        var image = Place(ImageFile, 60, 60);
        var a = Place(NumberValue, 60, 200, "Width");
        var b = Place(NumberValue, 60, 300, "Margin");
        var add = Place(Add, 300, 230);
        var compare = Place(Compare, 520, 240);
        var viewer = Place(Viewer, 760, 120);

        b.SetParameter("Value", 0.5);
        compare.Accent = NodeAccent.Amber;
        viewer.Accent = NodeAccent.Green;

        graph.Connect(image.Output("Image")!, viewer.Input("Image")!);
        graph.Connect(a.Output("Value")!, add.Input("A")!);
        graph.Connect(b.Output("Value")!, add.Input("B")!);
        graph.Connect(add.Output("Sum")!, compare.Input("Value")!);
        graph.Connect(compare.Output("Pass")!, viewer.Input("Caption")!);

        return GraphSerializer.Save(graph);
    }
}

/// <summary>
/// Stands in for evaluation: visits the nodes in running order and sets
/// each one's status from its wiring alone. This is where your own code
/// would run each node.
/// </summary>
public static class SampleRunner
{
    public static (int Ready, int Warnings) Run(NodeGraph graph)
    {
        int ready = 0, warnings = 0;

        foreach (var node in graph.TopologicalOrder())
        {
            if (!node.Enabled)
            {
                node.Status = NodeStatus.Idle;
                node.StatusMessage = "Bypassed.";
                continue;
            }

            var unwired = node.Inputs.Where(port => !port.IsConnected).Select(port => port.Name).ToArray();

            if (unwired.Length > 0)
            {
                node.Status = NodeStatus.Warning;
                node.StatusMessage = $"Not connected: {string.Join(", ", unwired)}.";
                warnings++;
            }
            else
            {
                node.Status = NodeStatus.Ready;
                node.StatusMessage = null;
                ready++;
            }
        }

        return (ready, warnings);
    }
}
