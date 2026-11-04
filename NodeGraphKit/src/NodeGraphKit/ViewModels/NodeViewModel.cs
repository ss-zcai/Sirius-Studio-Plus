using System.ComponentModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using NodeGraphKit.Model;

namespace NodeGraphKit.ViewModels;

/// <summary>A node as drawn on the canvas and edited on the properties panel.</summary>
public sealed class NodeViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isEditingName;

    public NodeViewModel(GraphNode node)
    {
        Node = node;
        Inputs = node.Inputs.Select(port => new ConnectorViewModel(this, port)).ToArray();
        Outputs = node.Outputs.Select(port => new ConnectorViewModel(this, port)).ToArray();
        Parameters = node.Definition.Parameters.Select(p => new ParameterViewModel(node, p)).ToArray();

        node.PropertyChanged += OnNodePropertyChanged;
    }

    public GraphNode Node { get; }

    public IReadOnlyList<ConnectorViewModel> Inputs { get; }

    public IReadOnlyList<ConnectorViewModel> Outputs { get; }

    public IReadOnlyList<ParameterViewModel> Parameters { get; }

    public bool HasParameters => Parameters.Count > 0;

    public string Title => Node.Name;

    /// <summary>The editable name. A blank entry puts the old name back.</summary>
    public string Name
    {
        get => Node.Name;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || Node.Name == value)
            {
                OnPropertyChanged();
                return;
            }

            Node.Name = value.Trim();
        }
    }

    /// <summary>True while the caption is being renamed in place.</summary>
    public bool IsEditingName
    {
        get => _isEditingName;
        set => SetProperty(ref _isEditingName, value);
    }

    /// <summary>The node type, shown under the name on the properties panel.</summary>
    public string TypeName => Node.Definition.DisplayName;

    public string? IconKey => Node.Definition.Icon;

    public string? ToolTipText => Node.Definition.Description;

    public NodeStatus Status => Node.Status;

    public string? StatusMessage => Node.StatusMessage;

    public bool Enabled
    {
        get => Node.Enabled;
        set => Node.Enabled = value;
    }

    /// <summary>Canvas position. Kept on the model, so the layout is saved with the graph.</summary>
    public Point Location
    {
        get => new(Node.X, Node.Y);
        set
        {
            if (Math.Abs(Node.X - value.X) < 0.01 && Math.Abs(Node.Y - value.Y) < 0.01)
                return;

            Node.X = value.X;
            Node.Y = value.Y;
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public NodeAccent Accent
    {
        get => Node.Accent;
        set => Node.Accent = value;
    }

    public NodeAccentOption AccentStyle => NodeAccents.Of(Node.Accent);

    /// <summary>The colours offered for this node, the current one marked.</summary>
    public IReadOnlyList<NodeAccentChoice> AccentChoices =>
        NodeAccents.All.Select(option => new NodeAccentChoice(option, option.Accent == Node.Accent)).ToArray();

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(GraphNode.Name):
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Name));
                break;
            case nameof(GraphNode.Status):
                OnPropertyChanged(nameof(Status));
                break;
            case nameof(GraphNode.StatusMessage):
                OnPropertyChanged(nameof(StatusMessage));
                break;
            case nameof(GraphNode.Enabled):
                OnPropertyChanged(nameof(Enabled));
                break;
            case nameof(GraphNode.Accent):
                OnPropertyChanged(nameof(Accent));
                OnPropertyChanged(nameof(AccentStyle));
                OnPropertyChanged(nameof(AccentChoices));
                break;
            case nameof(GraphNode.X):
            case nameof(GraphNode.Y):
                OnPropertyChanged(nameof(Location));
                break;
        }
    }
}

/// <summary>One parameter on the properties panel, with the editor its kind calls for.</summary>
public sealed class ParameterViewModel : ObservableObject
{
    private readonly GraphNode _node;

    public ParameterViewModel(GraphNode node, ParameterDefinition definition)
    {
        _node = node;
        Definition = definition;

        node.ParameterChanged += (_, name) =>
        {
            if (name == definition.Name)
                OnPropertyChanged(string.Empty);
        };
    }

    public ParameterDefinition Definition { get; }

    public string DisplayName => Definition.Name;

    public string? Description => Definition.Description;

    private bool IsNumeric => Definition.Kind is ParameterKind.Number or ParameterKind.Integer;

    /// <summary>A number with both ends declared: a slider and a box.</summary>
    public bool IsSlider => IsNumeric && Definition.Minimum is not null && Definition.Maximum is not null;

    /// <summary>A number with no range: a box only.</summary>
    public bool IsNumberBox => IsNumeric && !IsSlider;

    public bool IsText => Definition.Kind == ParameterKind.Text;

    public bool IsCheckBox => Definition.Kind == ParameterKind.Boolean;

    public bool IsComboBox => Definition.Kind == ParameterKind.Choice;

    public double Minimum => Definition.Minimum ?? 0;

    public double Maximum => Definition.Maximum ?? 100;

    public double Step => Definition.Step;

    public IReadOnlyList<string> Options => Definition.Options;

    public double NumericValue
    {
        get => IsNumeric ? Convert.ToDouble(_node.GetParameter(Definition.Name), CultureInfo.InvariantCulture) : 0;
        set => _node.SetParameter(Definition.Name, value);
    }

    /// <summary>The number as typed. Unreadable text puts the value back.</summary>
    public string NumericText
    {
        get => NumericValue.ToString(Definition.Kind == ParameterKind.Integer ? "0" : "0.###", CultureInfo.InvariantCulture);
        set
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number))
            {
                _node.SetParameter(Definition.Name, number);
            }

            OnPropertyChanged();
        }
    }

    public string TextValue
    {
        get => Convert.ToString(_node.GetParameter(Definition.Name), CultureInfo.InvariantCulture) ?? string.Empty;
        set => _node.SetParameter(Definition.Name, value);
    }

    public bool BoolValue
    {
        get => _node.GetParameter(Definition.Name) is true;
        set => _node.SetParameter(Definition.Name, value);
    }

    public string? SelectedOption
    {
        get => _node.GetParameter(Definition.Name) as string;
        set => _node.SetParameter(Definition.Name, value);
    }
}
