using CommunityToolkit.Mvvm.ComponentModel;

namespace NodeGraphKit.Model;

/// <summary>The indicator at a node's top right.</summary>
public enum NodeStatus
{
    /// <summary>Grey: not run yet.</summary>
    Idle,

    /// <summary>Green.</summary>
    Ready,

    /// <summary>Amber.</summary>
    Warning,

    /// <summary>Red.</summary>
    Error,
}

/// <summary>
/// A colour a node can be given to mark up a large graph. The background
/// stays dark and the border carries the colour; see docs/style-guide.md.
/// </summary>
public enum NodeAccent
{
    Default,
    Muted,
    Blue,
    Teal,
    Green,
    Amber,
    Red,
    Purple,
}

public enum PortDirection
{
    Input,
    Output,
}

/// <summary>A socket on a node on the canvas.</summary>
public sealed class GraphPort : ObservableObject
{
    private bool _isConnected;

    internal GraphPort(GraphNode node, PortDefinition definition, PortDirection direction)
    {
        Node = node;
        Definition = definition;
        Direction = direction;
    }

    public GraphNode Node { get; }

    public PortDefinition Definition { get; }

    public PortDirection Direction { get; }

    public string Name => Definition.Name;

    public PortType Type => Definition.Type;

    /// <summary>Kept up to date by the <see cref="NodeGraph"/> the node belongs to.</summary>
    public bool IsConnected
    {
        get => _isConnected;
        internal set => SetProperty(ref _isConnected, value);
    }

    public override string ToString() => $"{Node.Name}.{Name}";
}

/// <summary>A wire from an output to an input.</summary>
public sealed class GraphConnection
{
    internal GraphConnection(GraphPort source, GraphPort target)
    {
        Source = source;
        Target = target;
    }

    /// <summary>The output end.</summary>
    public GraphPort Source { get; }

    /// <summary>The input end. An input holds one wire; an output feeds any number.</summary>
    public GraphPort Target { get; }

    public override string ToString() => $"{Source} -> {Target}";
}

/// <summary>
/// A node on the canvas: an instance of a <see cref="NodeDefinition"/>
/// with its own name, place, colour and parameter values.
/// </summary>
public sealed class GraphNode : ObservableObject
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

    private string _name;
    private double _x;
    private double _y;
    private NodeAccent _accent;
    private bool _enabled = true;
    private NodeStatus _status;
    private string? _statusMessage;

    public GraphNode(NodeDefinition definition, string? id = null)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
        _name = definition.DisplayName;

        Inputs = definition.Inputs.Select(port => new GraphPort(this, port, PortDirection.Input)).ToArray();
        Outputs = definition.Outputs.Select(port => new GraphPort(this, port, PortDirection.Output)).ToArray();

        foreach (var parameter in definition.Parameters)
            _values[parameter.Name] = parameter.Default;
    }

    /// <summary>Unique within a graph; what saved wires refer to.</summary>
    public string Id { get; }

    public NodeDefinition Definition { get; }

    public IReadOnlyList<GraphPort> Inputs { get; }

    public IReadOnlyList<GraphPort> Outputs { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public double X
    {
        get => _x;
        set => SetProperty(ref _x, value);
    }

    public double Y
    {
        get => _y;
        set => SetProperty(ref _y, value);
    }

    public NodeAccent Accent
    {
        get => _accent;
        set => SetProperty(ref _accent, value);
    }

    /// <summary>Cleared to bypass the node; the canvas dims it.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    /// <summary>Set by the host after running the graph. Not saved.</summary>
    public NodeStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>The status dot's tooltip. Not saved.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Raised with the parameter's name when its value changes.</summary>
    public event EventHandler<string>? ParameterChanged;

    public GraphPort? Input(string name) => Inputs.FirstOrDefault(port => port.Name == name);

    public GraphPort? Output(string name) => Outputs.FirstOrDefault(port => port.Name == name);

    /// <summary>A parameter's current value: double, int, string or bool, by its kind.</summary>
    public object GetParameter(string name) =>
        _values.TryGetValue(name, out var value)
            ? value
            : throw new KeyNotFoundException($"'{Definition.DisplayName}' has no parameter '{name}'.");

    public T GetParameter<T>(string name) => (T)GetParameter(name);

    /// <summary>Sets a parameter, coerced to its kind and range.</summary>
    public void SetParameter(string name, object? value)
    {
        var definition = Definition.Parameters.FirstOrDefault(p => p.Name == name)
                         ?? throw new KeyNotFoundException($"'{Definition.DisplayName}' has no parameter '{name}'.");

        var coerced = definition.Coerce(value);

        if (Equals(_values[name], coerced))
            return;

        _values[name] = coerced;
        ParameterChanged?.Invoke(this, name);
    }

    public override string ToString() => Name;
}
