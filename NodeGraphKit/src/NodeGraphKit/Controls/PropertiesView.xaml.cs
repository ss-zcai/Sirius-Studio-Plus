using System.Windows;
using System.Windows.Controls;

namespace NodeGraphKit.Controls;

/// <summary>
/// The properties panel for the selected node. Bind its DataContext to a
/// <see cref="ViewModels.GraphEditorViewModel"/>.
/// </summary>
/// <remarks>
/// <see cref="HeaderContent"/> and <see cref="FooterContent"/> hold the
/// host's own controls; their DataContext is the selected
/// <see cref="ViewModels.NodeViewModel"/>.
/// </remarks>
public partial class PropertiesView : UserControl
{
    public static readonly DependencyProperty HeaderContentProperty = DependencyProperty.Register(
        nameof(HeaderContent), typeof(object), typeof(PropertiesView));

    public static readonly DependencyProperty FooterContentProperty = DependencyProperty.Register(
        nameof(FooterContent), typeof(object), typeof(PropertiesView));

    public PropertiesView()
    {
        InitializeComponent();
    }

    /// <summary>Shown above the parameters, below Enabled.</summary>
    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    /// <summary>Shown below the status box.</summary>
    public object? FooterContent
    {
        get => GetValue(FooterContentProperty);
        set => SetValue(FooterContentProperty, value);
    }
}
