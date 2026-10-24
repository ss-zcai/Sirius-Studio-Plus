using System.Text.Json;
using System.Text.Json.Serialization;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;

namespace Sirius.Core.Serialization;

/// <summary>Result of loading a graph, including anything that had to be skipped.</summary>
public sealed class GraphLoadResult
{
    public GraphLoadResult(VisionGraph graph, IReadOnlyList<string> warnings)
    {
        Graph = graph;
        Warnings = warnings;
    }

    public VisionGraph Graph { get; }

    /// <summary>
    /// Non-fatal problems: unknown node types, parameters that no longer exist,
    /// connections that could not be restored. Surfaced to the operator rather
    /// than swallowed, because a silently dropped node changes what the recipe
    /// does.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;
}

/// <summary>Reads and writes <see cref="VisionGraph"/> as JSON.</summary>
public static class GraphSerializer
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // Enums as names so saved files survive reordering of enum members.
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new RectangleJsonConverter());
        options.Converters.Add(new ValueRangeJsonConverter());
        options.Converters.Add(new OrientedRectJsonConverter());
        options.Converters.Add(new SizeJsonConverter());
        options.Converters.Add(new PointJsonConverter());
        options.Converters.Add(new ColorJsonConverter());
        return options;
    }

    public static GraphDocument ToDocument(VisionGraph graph, string? name = null) =>
        ToDocument(graph, graph?.Nodes, name);

    /// <summary>
    /// Writes a document for part of a graph.
    /// </summary>
    /// <remarks>
    /// The subset is what makes copy and paste the same code as save and
    /// open. Connections are kept only where both ends are in the subset:
    /// a wire to a node that is not being copied has nothing to land on at
    /// the other end, and carrying it would mean pasting into a recipe and
    /// hoping the node it names happens to be there.
    /// </remarks>
    public static GraphDocument ToDocument(
        VisionGraph graph,
        IEnumerable<VisionNode>? nodes,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var document = new GraphDocument { Name = name };
        var included = nodes is null
            ? graph.Nodes.ToHashSet()
            : nodes.ToHashSet();

        foreach (var node in graph.Nodes.Where(included.Contains))
        {
            var record = new NodeRecord
            {
                Id = node.Id,
                Type = node.Descriptor.TypeKey,
                Name = node.Name,
                Enabled = node.Enabled,
                X = node.X,
                Y = node.Y,
                Accent = node.Accent == NodeAccent.Default ? null : node.Accent.ToString(),
                Measurements = node.Measurements.Count == 0
                    ? null
                    : node.Measurements.Select(MeasurementDocument.From).ToList(),
            };

            foreach (var parameter in node.Descriptor.Parameters)
            {
                var value = parameter.GetValue(node);
                record.Parameters[parameter.Name] =
                    JsonSerializer.SerializeToElement(value, parameter.ValueType, Options);
            }

            document.Nodes.Add(record);
        }

        foreach (var connection in graph.Connections
                     .Where(c => included.Contains(c.Source.Node) && included.Contains(c.Target.Node)))
        {
            document.Connections.Add(new ConnectionRecord
            {
                FromNode = connection.Source.Node.Id,
                FromPort = connection.Source.Name,
                ToNode = connection.Target.Node.Id,
                ToPort = connection.Target.Name,
            });
        }

        return document;
    }

    public static string Serialize(VisionGraph graph, string? name = null) =>
        JsonSerializer.Serialize(ToDocument(graph, name), Options);

    public static void Save(VisionGraph graph, string path, string? name = null) =>
        File.WriteAllText(path, Serialize(graph, name ?? Path.GetFileNameWithoutExtension(path)));

    public static GraphLoadResult Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<GraphDocument>(json, Options)
                       ?? throw new InvalidDataException("The graph file is empty or not valid JSON.");

        return FromDocument(document);
    }

    public static GraphLoadResult Load(string path) => Deserialize(File.ReadAllText(path));

    public static GraphLoadResult FromDocument(GraphDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Version > GraphDocument.CurrentVersion)
        {
            throw new InvalidDataException(
                $"This graph was saved by a newer version of Sirius Studio Plus " +
                $"(format {document.Version}, this build reads {GraphDocument.CurrentVersion}).");
        }

        var graph = new VisionGraph();
        var warnings = new List<string>();
        var byId = new Dictionary<string, VisionNode>(StringComparer.Ordinal);

        foreach (var record in document.Nodes)
        {
            var descriptor = NodeCatalog.FindByTypeKey(record.Type)
                             ?? NodeCatalog.FindByTypeKey(MapLegacyTypeKey(record.Type));

            if (descriptor is null)
            {
                warnings.Add($"Skipped node '{record.Name ?? record.Id}': unknown node type '{record.Type}'.");
                continue;
            }

            VisionNode node;
            try
            {
                node = descriptor.CreateInstance();
            }
            catch (Exception ex)
            {
                warnings.Add($"Skipped node '{record.Name ?? record.Id}' of type '{record.Type}': {ex.Message}");
                continue;
            }

            node.Id = record.Id;
            node.X = record.X;
            node.Y = record.Y;

            if (!string.IsNullOrWhiteSpace(record.Name))
                node.Name = record.Name;

            ApplyAccent(node, record, warnings);

            ApplyParameters(node, descriptor, record, warnings);
            ApplyMeasurements(node, record, warnings);

            // Set last: assigning parameters above would otherwise be recorded
            // against a node whose bypass state is not yet the saved one.
            node.Enabled = record.Enabled;

            graph.Add(node);
            byId[node.Id] = node;
        }

        foreach (var record in document.Connections)
        {
            if (!byId.TryGetValue(record.FromNode, out var source))
            {
                warnings.Add($"Dropped a connection: source node '{record.FromNode}' is missing.");
                continue;
            }

            if (!byId.TryGetValue(record.ToNode, out var target))
            {
                warnings.Add($"Dropped a connection: target node '{record.ToNode}' is missing.");
                continue;
            }

            var outputPort = source.FindOutput(record.FromPort)
                             ?? source.FindOutput(MapLegacyPortName(record.FromPort, isInput: false));
            if (outputPort is null)
            {
                warnings.Add($"Dropped a connection: '{source.Name}' has no output '{record.FromPort}'.");
                continue;
            }

            // A growing node is built with one spare socket, so a recipe
            // wiring four shape sets names three that do not exist yet.
            // Asked for rather than skipped, or opening the recipe would
            // quietly drop every wire past the second.
            var inputPort = target.FindInput(record.ToPort)
                            ?? (target as IGrowingPorts)?.EnsureInput(record.ToPort)
                            ?? target.FindInput(MapLegacyPortName(record.ToPort, isInput: true));
            if (inputPort is null)
            {
                warnings.Add($"Dropped a connection: '{target.Name}' has no input '{record.ToPort}'.");
                continue;
            }

            var result = graph.TryConnect(outputPort, inputPort);
            if (!result.Success)
                warnings.Add($"Dropped a connection {outputPort} -> {inputPort}: {result.Message}");
        }

        return new GraphLoadResult(graph, warnings);
    }

    /// <summary>
    /// Translates node type keys from earlier builds.
    /// </summary>
    /// <remarks>
    /// A TypeKey exists so a node can be renamed without breaking saved
    /// recipes, and is normally kept. It changes only when a node's meaning
    /// splits: "Number" produced a real number and became Float when Integer
    /// was added alongside it.
    /// </remarks>
    private static string MapLegacyTypeKey(string typeKey) => typeKey switch
    {
        "source.number" => "source.float",
        _ => typeKey,
    };

    /// <summary>
    /// Translates port names from format version 1, where both the image input
    /// and the image output were called "Image".
    /// </summary>
    /// <remarks>
    /// The direction disambiguates them: a connection's source is always an
    /// output and its target always an input, so the same old name resolves to
    /// "Out" or "In" accordingly. Only consulted when the recorded name does
    /// not match a real port, so a node that legitimately has a port called
    /// "Image" is unaffected.
    /// </remarks>
    private static string MapLegacyPortName(string name, bool isInput) =>
        name == "Image" ? (isInput ? "In" : "Out") : name;

    /// <summary>
    /// Folds a recipe's separate "Lower"/"Upper" parameters into the single
    /// range that replaced them.
    /// </summary>
    /// <remarks>
    /// Colour Range used to carry six parameters - a minimum and a maximum
    /// per channel - and now carries three ranges. Without this a saved
    /// recipe would load with every channel wide open, which is a different
    /// result reported only as a warning, and the operator would have no
    /// reason to connect the two.
    /// </remarks>
    private static bool TryApplyLegacyBound(
        VisionNode node, NodeDescriptor descriptor, string name, JsonElement element)
    {
        // Two conventions were in use: "LowerBlue" on the colour channels and
        // "AreaMin" on the blob filters.
        var (measure, isLower) = Split(name);

        if (measure is null)
            return false;

        var parameter = descriptor.Parameters.FirstOrDefault(
            p => p.Name == measure && p.ValueType == typeof(ValueRange));

        if (parameter is null)
            return false;

        if (!element.TryGetDouble(out var bound))
            return false;

        var current = parameter.GetValue(node) is ValueRange range ? range : default;

        // A recipe's -1 meant "no limit", which is now an absent end.
        double? resolved = bound < 0 ? null : bound;

        // Written directly rather than through the constructor, which orders
        // its arguments: the other end has not necessarily been read yet, and
        // ordering a half-built range would swap the two.
        parameter.SetValue(
            node,
            isLower
                ? current with { Low = resolved }
                : current with { High = resolved });

        return true;
    }

    /// <summary>
    /// Folds a recipe's separate centre, size and angle into the single
    /// rectangle that replaced them.
    /// </summary>
    /// <remarks>
    /// Region of Interest carried five numbers and now carries one. Without
    /// this a saved recipe would open with the node's default region, which
    /// is a silently different cut rather than a visible failure.
    /// </remarks>
    private static bool TryApplyLegacyRectPart(
        VisionNode node, NodeDescriptor descriptor, string name, JsonElement element)
    {
        var parameter = descriptor.Parameters.FirstOrDefault(
            p => p.ValueType == typeof(OrientedRect));

        if (parameter is null || !element.TryGetDouble(out var number))
            return false;

        var current = parameter.GetValue(node) is OrientedRect rect ? rect : default;

        parameter.SetValue(node, name switch
        {
            "CentreX" => current with { CentreX = number },
            "CentreY" => current with { CentreY = number },
            "Width" => current with { Width = number },
            "Height" => current with { Height = number },
            "Angle" => current with { Angle = number },
            _ => current,
        });

        return name is "CentreX" or "CentreY" or "Width" or "Height" or "Angle";
    }

    /// <summary>
    /// Splits an old bound's name into the measure it belonged to and which
    /// end it was, or null when it is not one.
    /// </summary>
    private static (string? Measure, bool IsLower) Split(string name)
    {
        if (name.StartsWith("Lower", StringComparison.Ordinal))
            return (name[5..], true);

        if (name.StartsWith("Upper", StringComparison.Ordinal))
            return (name[5..], false);

        if (name.EndsWith("Min", StringComparison.Ordinal))
            return (name[..^3], true);

        if (name.EndsWith("Max", StringComparison.Ordinal))
            return (name[..^3], false);

        return (null, false);
    }

    /// <summary>
    /// Puts hand-drawn measurements back on the node.
    /// </summary>
    /// <remarks>
    /// A measurement whose tool this build does not have is dropped with a
    /// warning rather than failing the load: the recipe is still perfectly
    /// usable without one annotation.
    /// </remarks>
    private static void ApplyMeasurements(
        VisionNode node, NodeRecord record, List<string> warnings)
    {
        if (record.Measurements is not { Count: > 0 } saved)
            return;

        var kept = new List<MeasurementRecord>();

        foreach (var measurement in saved)
        {
            if (string.IsNullOrWhiteSpace(measurement.Tool) || measurement.Points.Count == 0)
            {
                warnings.Add($"'{node.Name}': dropped a measurement with nothing in it.");
                continue;
            }

            kept.Add(measurement.ToRecord());
        }

        node.Measurements = kept;
    }

    /// <summary>
    /// Restores the node's colouring, or leaves it the default.
    /// </summary>
    private static void ApplyAccent(
        VisionNode node, NodeRecord record, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(record.Accent))
            return;

        if (Enum.TryParse<NodeAccent>(record.Accent, out var accent))
        {
            node.Accent = accent;
            return;
        }

        warnings.Add(
            $"'{node.Name}': colour '{record.Accent}' is not one this build has; using the default.");
    }

    private static void ApplyParameters(
        VisionNode node,
        NodeDescriptor descriptor,
        NodeRecord record,
        List<string> warnings)
    {
        foreach (var (name, element) in record.Parameters)
        {
            var parameter = descriptor.Parameters.FirstOrDefault(p => p.Name == name);

            if (parameter is null && TryApplyLegacyBound(node, descriptor, name, element))
                continue;

            if (parameter is null && TryApplyLegacyRectPart(node, descriptor, name, element))
                continue;

            if (parameter is null)
            {
                // Expected when a recipe predates a parameter rename; the node
                // keeps its default rather than failing the load.
                warnings.Add($"'{node.Name}': ignored unknown parameter '{name}'.");
                continue;
            }

            try
            {
                var value = element.Deserialize(parameter.ValueType, Options);
                parameter.SetValue(node, value);
            }
            catch (Exception ex)
            {
                warnings.Add($"'{node.Name}': could not read parameter '{name}' ({ex.Message}); using default.");
            }
        }
    }
}
