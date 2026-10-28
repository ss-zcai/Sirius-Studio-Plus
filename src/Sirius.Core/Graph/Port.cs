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
    private string _name;
    private string _displayName;
    private Type _dataType;

    protected Port(string name, string displayName, Type dataType)
    {
        _name = name;
        _displayName = displayName;
        _dataType = dataType;
    }

    /// <summary>Stable identifier, used by serialization. Never localised.</summary>
    public string Name => _name;

    /// <summary>Label shown on the node in the editor.</summary>
    public string DisplayName => _displayName;

    /// <summary>
    /// CLR type of the payload this port carries (e.g. <c>ImageData</c>).
    /// Connection validity is decided by assignability between these types,
    /// which is what stops an operator wiring contours into an image input.
    /// </summary>
    /// <remarks>
    /// <see cref="object"/> means any type: a sub-graph's socket before
    /// anything has been wired to it, which takes the type of the first wire.
    /// </remarks>
    public Type DataType => _dataType;

    /// <summary>
    /// Changes the stable name - for a socket named after something whose
    /// identity was renewed, as a pasted sub-graph's boundaries are.
    /// </summary>
    internal void Rename(string name) => SetProperty(ref _name, name, nameof(Name));

    /// <summary>Changes the label, for a socket named after a node the operator renamed.</summary>
    internal void Retitle(string displayName) => SetProperty(ref _displayName, displayName, nameof(DisplayName));

    /// <summary>
    /// Changes what the port carries.
    /// </summary>
    /// <remarks>
    /// Only for a socket whose type is inferred from its wiring. The caller
    /// is responsible for not leaving a connection that the new type would
    /// have refused.
    /// </remarks>
    internal void Retype(Type dataType) => SetProperty(ref _dataType, dataType, nameof(DataType));

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
