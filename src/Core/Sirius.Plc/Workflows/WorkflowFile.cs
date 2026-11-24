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

    /// <summary>
    /// After a station was renamed: the nodes in <paramref name="workflow"/>
    /// naming it - Inspect, Station Result - name it anew.
    /// </summary>
    /// <returns>How many did.</returns>
    public static int RenameStation(VisionGraph workflow, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var renamed = 0;

        foreach (var node in workflow.Nodes)
        {
            switch (node)
            {
                case Nodes.InspectNode inspect when string.Equals(inspect.Station, from, StringComparison.OrdinalIgnoreCase):
                    inspect.Station = to;
                    renamed++;
                    break;

                case Nodes.StationResultNode heard when string.Equals(heard.Station, from, StringComparison.OrdinalIgnoreCase):
                    heard.Station = to;
                    renamed++;
                    break;
            }
        }

        return renamed;
    }

    /// <summary>
    /// The same, in the workflow file at <paramref name="path"/>, written
    /// back only if a node changed - and left alone if reading it found
    /// anything amiss, since writing it back would lose what was not read.
    /// </summary>
    /// <returns>Whether the file was changed.</returns>
    public static bool RenameStation(string path, string from, string to)
    {
        var text = File.ReadAllText(path);
        var loaded = Deserialize(text);

        if (loaded.Warnings.Count > 0 || RenameStation(loaded.Graph, from, to) == 0)
            return false;

        var name = (JsonNode.Parse(text) as JsonObject)?["name"]?.GetValue<string>();
        Save(loaded.Graph, path, name);
        return true;
    }
}
