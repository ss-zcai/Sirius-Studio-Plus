using CommunityToolkit.Mvvm.ComponentModel;
using Sirius.Core.Graph;

namespace Sirius.Studio.App.ViewModels;

/// <summary>A wire between two sockets, as drawn by the editor.</summary>
public sealed class ConnectionViewModel : ObservableObject
{
    private bool _isSelected;

    public ConnectionViewModel(Connection connection, ConnectorViewModel source, ConnectorViewModel target)
    {
        Connection = connection;
        Source = source;
        Target = target;
    }

    /// <summary>Selection state, so a wire can be picked and then deleted.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public Connection Connection { get; }

    public ConnectorViewModel Source { get; }

    public ConnectorViewModel Target { get; }

    /// <summary>Shown on hover, e.g. "Threshold.Image -> Viewer.Image".</summary>
    public string Description => $"{Source.Node.Title}.{Source.Title} → {Target.Node.Title}.{Target.Title}";
}
