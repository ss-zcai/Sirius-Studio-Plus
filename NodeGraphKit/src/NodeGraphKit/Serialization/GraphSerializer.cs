using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using NodeGraphKit.Model;

namespace NodeGraphKit.Serialization;

/// <summary>What a graph looks like on disk.</summary>
/// <remarks>
/// Plain JSON, so a saved graph can be read and diffed by eye. Ids tie the
/// wires to the nodes; types are <see cref="NodeDefinition.TypeId"/>.
/// </remarks>
public sealed class GraphFile
{
    public string Format { get; set; } = GraphSerializer.FormatName;

    public int Version { get; set; } = 1;

    public List<NodeRecord> Nodes { get; set; } = [];

    public List<ConnectionRecord> Connections { get; set; } = [];
}

public sealed class NodeRecord
{
    public string Id { get; set; } = "";

    public string Type { get; set; } = "";

    public string Name { get; set; } = "";

    public double X { get; set; }

    public double Y { get; set; }

    public NodeAccent Accent { get; set; }

    public bool Enabled { get; set; } = true;

    public Dictionary<string, JsonElement> Parameters { get; set; } = [];
}

public sealed class ConnectionRecord
{
    public string From { get; set; } = "";

    public string Output { get; set; } = "";

    public string To { get; set; } = "";

    public string Input { get; set; } = "";
}

/// <summary>What a load added, and what it had to leave out.</summary>
public sealed record LoadResult(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads and writes graphs as JSON. The same format serves files, the
/// clipboard and the undo history.
/// </summary>
public static class GraphSerializer
{
    public const string FormatName = "NodeGraphKit";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The graph as JSON - or only <paramref name="subset"/> of its nodes,
    /// with the wires that run between them (a copy).
    /// </summary>
    public static string Save(NodeGraph graph, IEnumerable<GraphNode>? subset = null)
    {
        var nodes = subset?.ToHashSet() ?? graph.Nodes.ToHashSet();
        var file = new GraphFile();

        foreach (var node in graph.Nodes.Where(nodes.Contains))
        {
            file.Nodes.Add(new NodeRecord
            {
                Id = node.Id,
                Type = node.Definition.TypeId,
                Name = node.Name,
                X = Math.Round(node.X, 2),
                Y = Math.Round(node.Y, 2),
                Accent = node.Accent,
                Enabled = node.Enabled,
                Parameters = node.Definition.Parameters.ToDictionary(
                    p => p.Name,
                    p => JsonSerializer.SerializeToElement(node.GetParameter(p.Name))),
            });
        }

        foreach (var connection in graph.Connections)
        {
            if (!nodes.Contains(connection.Source.Node) || !nodes.Contains(connection.Target.Node))
                continue;

            file.Connections.Add(new ConnectionRecord
            {
                From = connection.Source.Node.Id,
                Output = connection.Source.Name,
                To = connection.Target.Node.Id,
                Input = connection.Target.Name,
            });
        }

        return JsonSerializer.Serialize(file, Options);
    }

    /// <summary>
    /// Adds the nodes and wires in <paramref name="json"/> to <paramref name="graph"/>.
    /// </summary>
    /// <param name="newIds">Give every node a fresh id - for a paste, which must not collide with what is there.</param>
    /// <param name="offset">Moves every node by this much.</param>
    /// <remarks>
    /// Never throws for content: a node type the catalogue does not have, a
    /// port that no longer exists or a wire the rules refuse is skipped and
    /// named in <see cref="LoadResult.Warnings"/>, so an old file still opens.
    /// It throws <see cref="JsonException"/> only for text that is not a graph.
    /// </remarks>
    public static LoadResult Load(
        string json, NodeCatalog catalog, NodeGraph graph, bool newIds = false, Vector offset = default)
    {
        var file = JsonSerializer.Deserialize<GraphFile>(json, Options)
                   ?? throw new JsonException("The file is empty.");

        if (file.Format != FormatName)
            throw new JsonException($"Not a {FormatName} graph.");

        var warnings = new List<string>();
        var added = new List<GraphNode>();
        var byId = new Dictionary<string, GraphNode>(StringComparer.Ordinal);

        foreach (var record in file.Nodes)
        {
            if (catalog.Find(record.Type) is not { } definition)
            {
                warnings.Add($"'{record.Name}' was left out: there is no node type '{record.Type}'.");
                continue;
            }

            var id = newIds || graph.Find(record.Id) is not null ? null : record.Id;
            var node = new GraphNode(definition, id)
            {
                Name = string.IsNullOrWhiteSpace(record.Name) ? definition.DisplayName : record.Name,
                X = record.X + offset.X,
                Y = record.Y + offset.Y,
                Accent = record.Accent,
                Enabled = record.Enabled,
            };

            foreach (var (name, value) in record.Parameters)
            {
                if (definition.Parameters.Any(p => p.Name == name))
                    node.SetParameter(name, ValueOf(value));
                else
                    warnings.Add($"'{node.Name}': the parameter '{name}' no longer exists.");
            }

            graph.Add(node);
            added.Add(node);
            byId[record.Id] = node;
        }

        foreach (var record in file.Connections)
        {
            if (!byId.TryGetValue(record.From, out var from) || !byId.TryGetValue(record.To, out var to))
                continue;

            if (from.Output(record.Output) is not { } output || to.Input(record.Input) is not { } input)
            {
                warnings.Add($"The wire {from.Name}.{record.Output} -> {to.Name}.{record.Input} was left out: the port no longer exists.");
                continue;
            }

            var result = graph.Connect(output, input);
            if (!result.Success)
                warnings.Add($"The wire {output} -> {input} was left out: {result.Message}");
        }

        return new LoadResult(added, warnings);
    }

    private static object? ValueOf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        _ => null,
    };
}
