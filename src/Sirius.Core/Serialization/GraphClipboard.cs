using System.IO;
using System.Text.Json;
using Sirius.Core.Graph;

namespace Sirius.Core.Serialization;

/// <summary>What a paste produced.</summary>
public sealed class PasteResult
{
    public PasteResult(
        IReadOnlyList<VisionNode> nodes,
        IReadOnlyList<(OutputPort Source, InputPort Target)> wires,
        IReadOnlyList<string> warnings)
    {
        Nodes = nodes;
        Wires = wires;
        Warnings = warnings;
    }

    /// <summary>The new nodes, already added to the graph.</summary>
    public IReadOnlyList<VisionNode> Nodes { get; }

    /// <summary>
    /// The wires rebuilt between them, already connected.
    /// </summary>
    /// <remarks>
    /// Reported so the caller can record the paste as one undoable step. It
    /// could re-derive them by walking the new nodes' ports, but having the
    /// method that made them say what it made is less to get wrong.
    /// </remarks>
    public IReadOnlyList<(OutputPort Source, InputPort Target)> Wires { get; }

    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>
/// Copy and paste over a graph, in the recipe's own file format.
/// </summary>
/// <remarks>
/// <para>
/// The clipboard carries a `.sgraph` document holding just the copied nodes.
/// Using the save format rather than a second one for the clipboard means
/// parameters, colours, measurements and the wires between the copied nodes
/// all survive a paste without anything being written here about them - and
/// a format that already has legacy migrations and a version check gets
/// those for free. It also means the clipboard text is something a person
/// can read, and paste into a recipe file by hand if they ever need to.
/// </para>
/// <para>
/// No Windows clipboard here. This is the core; the shell puts the string on
/// the clipboard and takes it off again.
/// </para>
/// </remarks>
public static class GraphClipboard
{
    /// <summary>
    /// The clipboard format name, so a paste can tell a copied graph from
    /// arbitrary text that happens to be on the clipboard.
    /// </summary>
    public const string Format = "SiriusStudioPlus.Graph";

    /// <summary>
    /// Writes the chosen nodes, and the wires between them, as a document.
    /// </summary>
    public static string Copy(VisionGraph graph, IEnumerable<VisionNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(nodes);

        return JsonSerializer.Serialize(
            GraphSerializer.ToDocument(graph, nodes),
            GraphSerializer.Options);
    }

    /// <summary>
    /// Whether a string looks like something this can paste.
    /// </summary>
    /// <remarks>
    /// Asked before enabling the Paste command, so it must not throw on
    /// whatever else is on the clipboard.
    /// </remarks>
    public static bool CanPaste(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            var document = JsonSerializer.Deserialize<GraphDocument>(text, GraphSerializer.Options);
            return document is { Nodes.Count: > 0 };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Adds a copied document into <paramref name="graph"/>, offset so the
    /// new nodes do not land exactly on top of what they came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every node gets a fresh id. Keeping the copied ids would mean pasting
    /// into the recipe the copy came from produced two nodes claiming to be
    /// the same one - which the saved file cannot express and the connection
    /// records resolve by id, so the second paste would silently rewire the
    /// first.
    /// </para>
    /// <para>
    /// Names are left alone. Two nodes called "Threshold" is normal in this
    /// application and always has been; inventing "Threshold (2)" here would
    /// be the only place in it that renames somebody's node for them.
    /// </para>
    /// </remarks>
    /// <param name="offset">
    /// How far to move the pasted nodes, in canvas units. Applied to all of
    /// them equally, so their arrangement relative to each other is kept.
    /// </param>
    public static PasteResult Paste(VisionGraph graph, string text, double offset = 24)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var document = JsonSerializer.Deserialize<GraphDocument>(text, GraphSerializer.Options)
                       ?? throw new InvalidDataException("There is no graph on the clipboard.");

        var loaded = GraphSerializer.FromDocument(document);
        var pasted = loaded.Graph.Nodes.ToArray();

        // Keyed before the ids are replaced. Pairing by position instead
        // would drift the moment a node was skipped for having a type this
        // build does not know, and the wires would then be rebuilt between
        // the wrong nodes - which is worse than dropping them.
        var byOldId = pasted.ToDictionary(node => node.Id, StringComparer.Ordinal);

        // Taken off the throwaway graph first: a node cannot belong to two,
        // and its connections there would otherwise outlive it.
        foreach (var node in pasted)
            loaded.Graph.Remove(node);

        foreach (var node in pasted)
        {
            node.Id = Guid.NewGuid().ToString("N");
            node.X += offset;
            node.Y += offset;
            graph.Add(node);
        }

        var warnings = loaded.Warnings.ToList();
        var wires = new List<(OutputPort Source, InputPort Target)>();

        foreach (var record in document.Connections)
        {
            if (!byOldId.TryGetValue(record.FromNode, out var source) ||
                !byOldId.TryGetValue(record.ToNode, out var target))
            {
                continue;
            }

            var output = source.FindOutput(record.FromPort);
            var input = target.FindInput(record.ToPort);

            if (output is null || input is null)
                continue;

            var connected = graph.TryConnect(output, input);

            if (connected.Success)
                wires.Add((output, input));
            else
                warnings.Add($"Pasted wire dropped: {connected.Message}");
        }

        // After the wires, which found a sub-graph's sockets by the ids of
        // the boundaries inside it. Renewing them renames those sockets in
        // place, so the wires stay; leaving them would give two sub-graphs
        // nodes with the same id, and their panels the same identity.
        foreach (var host in pasted.OfType<ISubGraphHost>())
            host.RenewIds();

        return new PasteResult(pasted, wires, warnings);
    }
}
