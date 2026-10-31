using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;

namespace Sirius.Studio.App.ViewModels;

/// <summary>A node as drawn on the editor canvas.</summary>
public sealed class NodeViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isEditingName;

    public NodeViewModel(VisionNode node)
    {
        Node = node;

        Inputs = new ObservableCollection<ConnectorViewModel>(
            node.Inputs.Select(port => new ConnectorViewModel(this, port)));

        Outputs = new ObservableCollection<ConnectorViewModel>(
            node.Outputs.Select(port => new ConnectorViewModel(this, port)));

        Parameters = new ObservableCollection<ParameterViewModel>(
            node.Descriptor.Parameters
                .Where(descriptor => !descriptor.Attribute.Hidden)
                .Select(descriptor => new ParameterViewModel(node, descriptor)));

        Actions = node is INodeActions actions
            ? actions.Actions.Select(action => new NodeActionViewModel(action)).ToArray()
            : [];

        node.PropertyChanged += OnNodePropertyChanged;
    }

    public VisionNode Node { get; }

    public ObservableCollection<ConnectorViewModel> Inputs { get; }

    public ObservableCollection<ConnectorViewModel> Outputs { get; }

    public ObservableCollection<ParameterViewModel> Parameters { get; }

    /// <summary>The node's own buttons - Teach, Reset - above its parameters.</summary>
    public IReadOnlyList<NodeActionViewModel> Actions { get; }

    public bool HasActions => Actions.Count > 0;

    public string Title => Node.Name;

    /// <summary>
    /// Editable node name. Distinct from <see cref="Title"/>, which is
    /// read-only for display; the inspector and the in-place editor both write
    /// through here.
    /// </summary>
    public string Name
    {
        get => Node.Name;
        set
        {
            // An empty name would leave the node and its panel tab unlabelled,
            // so a blank entry reverts rather than being stored.
            if (string.IsNullOrWhiteSpace(value) || Node.Name == value)
            {
                OnPropertyChanged();
                return;
            }

            Node.Name = value;
        }
    }

    /// <summary>True while the caption is being edited in place on the node.</summary>
    public bool IsEditingName
    {
        get => _isEditingName;
        set => SetProperty(ref _isEditingName, value);
    }

    /// <summary>Node type name, shown under the title when it has been renamed.</summary>
    public string TypeName => Node.Descriptor.DisplayName;

    public string? IconKey => Node.Descriptor.Icon;

    public string? Description => Node.Descriptor.Description;

    public NodeStatus Status => Node.Status;

    public string? StatusMessage => Node.StatusMessage;

    public bool HasParameters => Parameters.Count > 0;

    /// <summary>Evaluation time, shown on the node as e.g. "4.2 ms".</summary>
    public string TimingText =>
        Node.LastEvaluationTime > TimeSpan.Zero
            ? $"{Node.LastEvaluationTime.TotalMilliseconds:0.#} ms"
            : string.Empty;

    public bool Enabled
    {
        get => Node.Enabled;
        set
        {
            if (Node.Enabled != value)
                Node.Enabled = value;
        }
    }

    /// <summary>
    /// Canvas position. Kept on the model so it is saved with the graph - the
    /// layout is part of the recipe, not throwaway view state.
    /// </summary>
    public Point Location
    {
        get => new(Node.X, Node.Y);
        set
        {
            if (Math.Abs(Node.X - value.X) < 0.01 && Math.Abs(Node.Y - value.Y) < 0.01)
                return;

            Node.X = value.X;
            Node.Y = value.Y;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// How this node is coloured, for marking up a large recipe.
    /// </summary>
    public NodeAccent Accent
    {
        get => Node.Accent;
        set
        {
            if (Node.Accent != value)
                Node.Accent = value;
        }
    }

    /// <summary>The styling the accent resolves to on this theme.</summary>
    public NodeAccentOption AccentStyle => NodeAccents.Of(Node.Accent);

    /// <summary>
    /// The set offered on the properties panel and the canvas menu, with
    /// the current one marked so the swatches can show which is in use.
    /// </summary>
    /// <remarks>
    /// Rebuilt whenever the accent changes rather than mutated in place:
    /// eight small objects cost nothing, and a swatch that forgot to clear
    /// its own mark would show two nodes' worth of selection at once.
    /// </remarks>
    public IReadOnlyList<NodeAccentChoice> AccentChoices =>
        NodeAccents.All.Select(option => new NodeAccentChoice(
            option,
            option.Accent == Node.Accent)).ToArray();

    private void OnNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VisionNode.Name):
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Name));
                break;
            case nameof(VisionNode.Status):
                OnPropertyChanged(nameof(Status));
                break;
            case nameof(VisionNode.StatusMessage):
                OnPropertyChanged(nameof(StatusMessage));
                break;
            case nameof(VisionNode.Enabled):
                OnPropertyChanged(nameof(Enabled));
                break;
            case nameof(VisionNode.Accent):
                OnPropertyChanged(nameof(Accent));
                OnPropertyChanged(nameof(AccentStyle));
                OnPropertyChanged(nameof(AccentChoices));
                break;
            case nameof(VisionNode.LastEvaluationTime):
                OnPropertyChanged(nameof(TimingText));
                break;
            case nameof(VisionNode.X):
            case nameof(VisionNode.Y):
                OnPropertyChanged(nameof(Location));
                break;
        }
    }
}
