using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sirius.Core.Serialization;

/// <summary>
/// On-disk shape of a saved vision recipe (<c>.sgraph</c>).
/// </summary>
/// <remarks>
/// Nodes reference their type by the stable <c>TypeKey</c> from
/// <c>VisionNodeAttribute</c>, not by CLR type name, so node classes can be
/// renamed or moved between assemblies without breaking saved files.
/// </remarks>
public sealed class GraphDocument
{
    /// <summary>
    /// Bumped on breaking format changes; see <c>GraphSerializer</c>.
    /// </summary>
    /// <remarks>
    /// 2 - image ports renamed from "Image" to "In"/"Out". Version 1 files
    ///     still load: the reader maps the old names by port direction.
    /// </remarks>
    public const int CurrentVersion = 2;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("nodes")]
    public List<NodeRecord> Nodes { get; set; } = new();

    [JsonPropertyName("connections")]
    public List<ConnectionRecord> Connections { get; set; } = new();
}

public sealed class NodeRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Stable node type key.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Measurements drawn on this node's viewer, if any.
    /// </summary>
    /// <remarks>
    /// Omitted when there are none, so an ordinary recipe is unchanged and
    /// a file written by this build still opens in one that predates it.
    /// </remarks>
    [JsonPropertyName("measurements")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<MeasurementDocument>? Measurements { get; set; }

    /// <summary>
    /// The node's colouring, when it is not the default one.
    /// </summary>
    /// <remarks>
    /// Written as a name, and omitted when it is the default, so an
    /// unmarked recipe is byte-identical to one written before accents
    /// existed.
    ///
    /// A string rather than the enum, so a recipe naming an accent this
    /// build does not have falls back to the default with a warning. The
    /// enum converter would throw instead, failing the whole load over a
    /// colour.
    /// </remarks>
    [JsonPropertyName("accent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Accent { get; set; }

    /// <summary>
    /// The names this node's outputs, settings or viewer are published
    /// under, for screens.
    /// </summary>
    /// <remarks>
    /// Omitted when there are none, so a recipe that publishes nothing is
    /// unchanged, and opens in a build that predates publishing.
    /// </remarks>
    [JsonPropertyName("published")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PublishedRecord>? Published { get; set; }

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    /// <summary>
    /// Parameter values keyed by property name, held as raw JSON so an unknown
    /// or renamed parameter can be skipped without failing the whole load.
    /// </summary>
    [JsonPropertyName("params")]
    public Dictionary<string, JsonElement> Parameters { get; set; } = new();

    /// <summary>
    /// The graph inside a sub-graph node; absent for every other node.
    /// </summary>
    /// <remarks>
    /// A whole document, nested, so a sub-graph is saved and read by exactly
    /// the code that saves and reads a recipe. Omitted when empty, so a
    /// recipe without sub-graphs is unchanged - and still opens in a build
    /// that predates them.
    /// </remarks>
    [JsonPropertyName("graph")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GraphDocument? Graph { get; set; }
}

/// <summary>One published name, as saved.</summary>
public sealed class PublishedRecord
{
    /// <summary>
    /// Result, Setting or View - a string, so a kind this build does not
    /// know is skipped with a warning rather than failing the load.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>The output's or setting's name; absent for a view.</summary>
    [JsonPropertyName("member")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Member { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>A setting's lowest value on the line; absent for no limit of its own.</summary>
    [JsonPropertyName("lowest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Lowest { get; set; }

    [JsonPropertyName("highest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Highest { get; set; }
}

public sealed class ConnectionRecord
{
    [JsonPropertyName("fromNode")]
    public string FromNode { get; set; } = string.Empty;

    [JsonPropertyName("fromPort")]
    public string FromPort { get; set; } = string.Empty;

    [JsonPropertyName("toNode")]
    public string ToNode { get; set; } = string.Empty;

    [JsonPropertyName("toPort")]
    public string ToPort { get; set; } = string.Empty;
}
