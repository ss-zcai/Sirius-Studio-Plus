using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using NodeGraphKit.Model;

namespace NodeGraphKit.ViewModels;

/// <summary>A socket as the canvas sees it.</summary>
/// <remarks>
/// <see cref="Anchor"/> is pushed here by the Nodify connector once it has
/// been laid out; wires bind to it, which is how a wire follows its node.
/// </remarks>
public sealed class ConnectorViewModel : ObservableObject
{
    private static readonly CornerRadius SquareCorners = new(1);
    private static readonly CornerRadius RoundCorners = new(6);
    private static readonly Dictionary<string, Brush> Brushes = [];

    private Point _anchor;
    private bool _isConnected;

    public ConnectorViewModel(NodeViewModel node, GraphPort port)
    {
        Node = node;
        Port = port;
        _isConnected = port.IsConnected;

        port.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GraphPort.IsConnected))
                IsConnected = port.IsConnected;
        };
    }

    public NodeViewModel Node { get; }

    public GraphPort Port { get; }

    public string Title => Port.Name;

    public bool IsInput => Port.Direction == PortDirection.Input;

    /// <summary>The socket's colour, from its type.</summary>
    public Brush SocketBrush => BrushFor(Port.Type.Colour);

    /// <summary>A square for <see cref="PortShape.Square"/> types, a circle otherwise.</summary>
    public CornerRadius SocketCorners => Port.Type.Shape == PortShape.Square ? SquareCorners : RoundCorners;

    /// <summary>Which port, and what flows through it.</summary>
    public string ToolTipText =>
        Port.Definition.Description is { Length: > 0 } description
            ? $"{Title}  ·  {Port.Type.Name}{Environment.NewLine}{description}"
            : $"{Title}  ·  {Port.Type.Name}";

    /// <summary>Editor-space position of the socket. Written by the view.</summary>
    public Point Anchor
    {
        get => _anchor;
        set => SetProperty(ref _anchor, value);
    }

    /// <summary>A hollow socket means nothing is wired in.</summary>
    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    private static Brush BrushFor(string hex)
    {
        lock (Brushes)
        {
            if (!Brushes.TryGetValue(hex, out var brush))
                Brushes[hex] = brush = NodeAccentOption.Frozen(hex);

            return brush;
        }
    }
}

/// <summary>A wire between two sockets.</summary>
public sealed class ConnectionViewModel : ObservableObject
{
    private bool _isSelected;

    public ConnectionViewModel(GraphConnection connection, ConnectorViewModel source, ConnectorViewModel target)
    {
        Connection = connection;
        Source = source;
        Target = target;
    }

    public GraphConnection Connection { get; }

    public ConnectorViewModel Source { get; }

    public ConnectorViewModel Target { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Shown on hover: "Number.Value → Add.A".</summary>
    public string Description => $"{Source.Node.Title}.{Source.Title} → {Target.Node.Title}.{Target.Title}";
}

/// <summary>The wire being dragged.</summary>
public sealed class PendingConnectionViewModel : ObservableObject
{
    private ConnectorViewModel? _source;
    private object? _target;

    public ConnectorViewModel? Source
    {
        get => _source;
        set => SetProperty(ref _source, value);
    }

    public object? Target
    {
        get => _target;
        set => SetProperty(ref _target, value);
    }
}
