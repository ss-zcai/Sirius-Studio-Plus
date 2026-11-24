using Sirius.Core.Graph;
using Sirius.Core.Nodes;

namespace Sirius.Core.Runtime;

/// <summary>
/// What an inspection left behind, read off a recipe as it ran: the picture
/// it took, what each image viewer drew, and every published name's value.
/// </summary>
/// <remarks>
/// Shared by a station (<see cref="StationRunner"/>) and a folder run
/// (<see cref="FolderRun"/>), so a picture's annotation file says the same
/// whichever wrote it.
/// </remarks>
public static class InspectionCapture
{
    /// <summary>
    /// The picture the recipe took: its camera's, else its image sequence's,
    /// else any source node's - the first output carrying pixels.
    /// </summary>
    public static object? Acquired(VisionGraph recipe, GraphScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(scheduler);

        var nodes = recipe.Nodes.Where(node => node.Enabled).ToArray();

        var sources = nodes.Where(node => node is IInspectionNode)
            .Concat(nodes.Where(node => node is ISequenceNode))
            .Concat(nodes.Where(node => node.Descriptor.Category == NodeCategory.Source));

        foreach (var node in sources)
        {
            foreach (var output in node.Outputs)
            {
                if (scheduler.GetOutput(output) is IPixelValues picture)
                    return picture;
            }
        }

        return null;
    }

    /// <summary>Each image viewer in the recipe: its picture and what is drawn on it.</summary>
    public static List<ViewerRecord> Viewers(VisionGraph recipe, GraphScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(scheduler);

        var viewers = new List<ViewerRecord>();

        foreach (var node in recipe.Nodes.Where(node => node.Enabled && node.Descriptor.TypeKey == "output.viewer"))
        {
            var inputs = node.Inputs.Where(input => !node.IsParameterPort(input)).ToArray();

            if (inputs.Length == 0)
                continue;

            viewers.Add(new ViewerRecord(
                node.Name,
                scheduler.GetInput(inputs[0]),
                inputs.Skip(1).Select(scheduler.GetInput).OfType<Shapes.ShapeSet>().ToArray()));
        }

        return viewers;
    }

    /// <summary>Every published name's value, as the inspection just left it.</summary>
    public static Dictionary<string, object?> Published(VisionGraph recipe, GraphScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(scheduler);

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in Publishing.In(recipe).Where(item => item.Found))
        {
            values[item.Name] = item.Kind switch
            {
                PublishedKind.Result => item.Node.FindOutput(item.Entry.Member) is { } port ? scheduler.GetOutput(port) : null,
                PublishedKind.Setting => item.Node.Descriptor.Parameters.First(p => p.Name == item.Entry.Member).GetValue(item.Node),
                _ => new ViewFrame(item.Node.Inputs
                    .Where(input => !item.Node.IsParameterPort(input))
                    .Select(input => new KeyValuePair<string, object?>(input.Name, scheduler.GetInput(input)))
                    .ToArray()),
            };
        }

        return values;
    }
}
