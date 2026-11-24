using CommunityToolkit.Mvvm.Input;
using Sirius.Core.Nodes;

namespace Sirius.Studio.App.ViewModels;

/// <summary>
/// One of a node's buttons in the properties panel.
/// </summary>
/// <remarks>
/// When the node cannot do it - nothing to teach from yet - it says why,
/// and that is shown; when it can, the result is on the canvas and in the
/// panels, and saying so as well would only be a box to close.
/// </remarks>
public sealed class NodeActionViewModel
{
    public NodeActionViewModel(NodeAction action)
    {
        Label = action.Label;
        Description = action.Description;

        Command = new RelayCommand(() =>
        {
            if (action.Run() is { } problem)
                Dialog.Inform(problem, action.Label);
        });
    }

    public string Label { get; }

    public string Description { get; }

    public IRelayCommand Command { get; }
}
