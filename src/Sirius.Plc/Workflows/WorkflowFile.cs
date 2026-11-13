using System.Text.Json;
using System.Text.Json.Nodes;
using Sirius.Core.Graph;
using Sirius.Core.Serialization;

namespace Sirius.Plc.Workflows;

/// <summary>
/// A controller's workflow on disk: <c>controllers/&lt;name&gt;.sflow</c>.
/// </summary>
/// <remarks>
/// <para>
/// JSON: <c>"format": "sirius-workflow"</c>, a version, the workflow's name,
/// and the graph as a recipe's is written - nodes by their type key, their
/// settings, and wires. A reader ignores fields it does not know, so later
/// versions add rather than change.
/// </para>
/// <code>
/// { "format": "sirius-workflow", "version": 1, "name": "Line 1 PLC",
///   "graph": { "version": 2, "nodes": [ ... ], "connections": [ ... ] } }
/// </code>
/// <para>
/// Needs the workflow nodes registered first: <see cref="PlcNodeLibrary.Register"/>.
/// </para>
/// </remarks>
public static class WorkflowFile
{
    public const string Extension = ".sflow";

    public const string Format = "sirius-workflow";

    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Serialize(VisionGraph workflow, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var json = new JsonObject
        {
            ["format"] = Format,
            ["version"] = CurrentVersion,
            ["name"] = name,
            ["graph"] = JsonSerializer.SerializeToNode(GraphSerializer.ToDocument(workflow, name), GraphSerializer.Options),
        };

        return json.ToJsonString(Indented);
    }

    /// <summary>Writes the file whole, through a temporary one, so a failure part way leaves the old in place.</summary>
    public static void Save(VisionGraph workflow, string path, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var text = Serialize(workflow, name ?? Path.GetFileNameWithoutExtension(path));
        var temporary = path + ".saving";

        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    /// <exception cref="InvalidDataException">It is not a workflow, or one too new to read.</exception>
    public static GraphLoadResult Deserialize(string text)
    {
        var json = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("It is not a JSON object.");

        if (json["format"]?.GetValue<string>() is var format && format != Format)
            throw new InvalidDataException($"It is a '{format ?? "nameless"}' file, not a Sirius workflow.");

        if (json["version"] is JsonValue version && version.TryGetValue<int>(out var number) && number > CurrentVersion)
        {
            throw new InvalidDataException(
                $"It was written by a newer Sirius (version {number}); this one reads up to {CurrentVersion}.");
        }

        var document = json["graph"]?.Deserialize<GraphDocument>(GraphSerializer.Options)
                       ?? throw new InvalidDataException("It holds no workflow.");

        var loaded = GraphSerializer.FromDocument(document);
        var warnings = loaded.Warnings.ToList();

        foreach (var node in loaded.Graph.Nodes.Where(node => node is not WorkflowNode))
            warnings.Add($"'{node.Name}' is a recipe's node: a workflow cannot run it.");

        return new GraphLoadResult(loaded.Graph, warnings);
    }

    public static GraphLoadResult Load(string path) => Deserialize(File.ReadAllText(path));
}
