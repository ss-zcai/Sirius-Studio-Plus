using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Sirius.Core.Graph;

namespace Sirius.Studio.App.ViewModels;

/// <summary>
/// A socket as the node editor sees it.
/// </summary>
/// <remarks>
/// <see cref="Anchor"/> is pushed here by the Nodify connector control once it
/// has been laid out; connection lines bind to it, which is how a wire follows
/// its node when it is dragged.
/// </remarks>
public sealed class ConnectorViewModel : ObservableObject
{
    private Point _anchor;
    private bool _isConnected;

    public ConnectorViewModel(NodeViewModel node, Port port)
    {
        Node = node;
        Port = port;
        IsConnected = port.IsConnected;

        port.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Port.IsConnected))
                IsConnected = port.IsConnected;
        };
    }

    public NodeViewModel Node { get; }

    public Port Port { get; }

    public string Title => Port.DisplayName;

    public bool IsInput => Port.Direction == PortDirection.Input;

    /// <summary>Raw CLR type name; drives socket shape and colour.</summary>
    public string DataTypeName => Port.DataType.Name;

    /// <summary>
    /// Human-readable type, e.g. "Image" rather than "ImageData".
    /// </summary>
    /// <remarks>
    /// Mapped from the type name rather than the type itself so this stays
    /// free of any dependency on the node library that defines them.
    /// </remarks>
    public string TypeDisplayName => Sirius.Core.Nodes.TypeNames.Friendly(DataTypeName);

    /// <summary>
    /// Socket tooltip. Sockets render as shapes rather than labels, so this
    /// carries both which port it is and what flows through it.
    /// </summary>
    public string ToolTipText => $"{DisplayName}  ·  {TypeDisplayName}";

    private string DisplayName => Port.DisplayName;

    /// <summary>
    /// Editor-space position of the socket. Written by the view.
    /// </summary>
    public Point Anchor
    {
        get => _anchor;
        set => SetProperty(ref _anchor, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }
}
