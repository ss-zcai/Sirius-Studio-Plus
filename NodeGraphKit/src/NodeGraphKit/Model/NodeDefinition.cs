namespace NodeGraphKit.Model;

/// <summary>One socket a node type declares.</summary>
/// <param name="Name">The label beside the socket, and the port's key in saved files. Unique per side of a node.</param>
public sealed record PortDefinition(string Name, PortType Type, string? Description = null);

/// <summary>
/// A kind of node - what the toolbox lists and what a node on the canvas is
/// an instance of. Pure description: what the node <i>does</i> lives in the
/// host application, keyed on <see cref="TypeId"/>.
/// </summary>
/// <example>
/// <code>
/// new NodeDefinition("math.add", "Add")
/// {
///     Category = "Math",
///     Icon = "ph_counter",
///     Description = "Adds two numbers.",
///     Inputs = [new("A", Number), new("B", Number)],
///     Outputs = [new("Sum", Number)],
///     Parameters = [ParameterDefinition.Number("Scale", 1, minimum: 0, maximum: 10)],
/// };
/// </code>
/// </example>
public sealed class NodeDefinition
{
    public NodeDefinition(string typeId, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        TypeId = typeId;
        DisplayName = displayName;
    }

    /// <summary>
    /// The stable key written to saved graphs. Never change it once graphs
    /// have been saved - rename <see cref="DisplayName"/> instead.
    /// </summary>
    public string TypeId { get; }

    /// <summary>The toolbox entry, and a new node's first name.</summary>
    public string DisplayName { get; }

    /// <summary>The toolbox group. Groups appear in the order their first node was added to the catalogue.</summary>
    public string Category { get; init; } = "General";

    /// <summary>One sentence, shown as the toolbox and node tooltip.</summary>
    public string? Description { get; init; }

    /// <summary>Icon key: "ph_number" finds the resource <c>Icon.ph_number</c>.</summary>
    public string? Icon { get; init; }

    public IReadOnlyList<PortDefinition> Inputs { get; init; } = [];

    public IReadOnlyList<PortDefinition> Outputs { get; init; } = [];

    public IReadOnlyList<ParameterDefinition> Parameters { get; init; } = [];

    /// <summary>Free for the host: an evaluator, a factory, anything.</summary>
    public object? Tag { get; init; }

    public override string ToString() => DisplayName;
}

/// <summary>The node types an application offers, in toolbox order.</summary>
public sealed class NodeCatalog
{
    private readonly List<NodeDefinition> _definitions = [];
    private readonly Dictionary<string, NodeDefinition> _byId = new(StringComparer.Ordinal);

    public NodeCatalog(IEnumerable<NodeDefinition>? definitions = null)
    {
        foreach (var definition in definitions ?? [])
            Add(definition);
    }

    public IReadOnlyList<NodeDefinition> Definitions => _definitions;

    /// <summary>Raised when a definition is added, so a toolbox can refresh.</summary>
    public event EventHandler? Changed;

    public void Add(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!_byId.TryAdd(definition.TypeId, definition))
            throw new ArgumentException($"A node type '{definition.TypeId}' is already in the catalogue.", nameof(definition));

        _definitions.Add(definition);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public NodeDefinition? Find(string typeId) => _byId.GetValueOrDefault(typeId);
}
