using CommunityToolkit.Mvvm.ComponentModel;

namespace Sirius.Core.Graph;

public enum PortDirection
{
    Input,
    Output,
}

/// <summary>
/// A typed socket on a node. Ports are created once, in the node constructor,
/// and never replaced - the editor binds directly to these instances.
/// </summary>
public abstract class Port : ObservableObject
{
    protected Port(string name, string displayName, Type dataType)
    {
        Name = name;
        DisplayName = displayName;
        DataType = dataType;
    }

    /// <summary>Stable identifier, used by serialization. Never localised.</summary>
    public string Name { get; }

    /// <summary>Label shown on the node in the editor.</summary>
    public string DisplayName { get; }

    /// <summary>
    /// CLR type of the payload this port carries (e.g. <c>ImageData</c>).
    /// Connection validity is decided by assignability between these types,
    /// which is what stops an operator wiring contours into an image input.
    /// </summary>
    public Type DataType { get; }

    public abstract PortDirection Direction { get; }

    /// <summary>Owning node. Set by <see cref="VisionNode"/> during registration.</summary>
    public VisionNode Node { get; internal set; } = null!;

    public abstract bool IsConnected { get; }

    public override string ToString() => $"{Node?.Name ?? "<detached>"}.{Name}";
}

/// <summary>
/// An input socket. Accepts at most one connection - the usual dataflow rule,
/// and what lets a node read its inputs without any merge semantics.
/// </summary>
public sealed class InputPort : Port
{
    private Connection? _connection;

    public InputPort(string name, string displayName, Type dataType, bool isRequired = true)
        : base(name, displayName, dataType)
    {
        IsRequired = isRequired;
    }

    public override PortDirection Direction => PortDirection.Input;

    /// <summary>
    /// When true, evaluation fails with <see cref="NodeStatus.Error"/> if nothing
    /// is connected. Optional inputs (e.g. a mask) simply read as null.
    /// </summary>
    public bool IsRequired { get; }

    public Connection? Connection
    {
        get => _connection;
        internal set
        {
            if (SetProperty(ref _connection, value))
                OnPropertyChanged(nameof(IsConnected));
        }
    }

    public override bool IsConnected => _connection is not null;
}

/// <summary>
/// An output socket. Fans out to any number of inputs; the scheduler caches the
/// produced value once and hands the same instance to every consumer.
/// </summary>
public sealed class OutputPort : Port
{
    private readonly List<Connection> _connections = new();

    public OutputPort(string name, string displayName, Type dataType)
        : base(name, displayName, dataType)
    {
    }

    public override PortDirection Direction => PortDirection.Output;

    public IReadOnlyList<Connection> Connections => _connections;

    public override bool IsConnected => _connections.Count > 0;

    internal void Add(Connection connection)
    {
        _connections.Add(connection);
        OnPropertyChanged(nameof(Connections));
        OnPropertyChanged(nameof(IsConnected));
    }

    internal void Remove(Connection connection)
    {
        if (!_connections.Remove(connection))
            return;

        OnPropertyChanged(nameof(Connections));
        OnPropertyChanged(nameof(IsConnected));
    }
}
