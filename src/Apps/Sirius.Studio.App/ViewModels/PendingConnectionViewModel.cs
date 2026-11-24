using CommunityToolkit.Mvvm.ComponentModel;

namespace Sirius.Studio.App.ViewModels;

/// <summary>
/// State of a wire the operator is currently dragging.
/// </summary>
/// <remarks>
/// <see cref="IsValid"/> and <see cref="ValidationMessage"/> come from
/// <c>VisionGraph.CanConnect</c> while the drag is in flight, so an incompatible
/// drop is signalled before the operator releases the mouse rather than being
/// silently ignored.
/// </remarks>
public sealed class PendingConnectionViewModel : ObservableObject
{
    private ConnectorViewModel? _source;
    private object? _target;
    private bool _isValid = true;
    private string? _validationMessage;

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

    public bool IsValid
    {
        get => _isValid;
        set => SetProperty(ref _isValid, value);
    }

    public string? ValidationMessage
    {
        get => _validationMessage;
        set => SetProperty(ref _validationMessage, value);
    }
}
