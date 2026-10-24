using System.Windows;
using Sirius.Core.Graph;
using Sirius.Studio.App.ViewModels;

namespace Sirius.Studio.App;

/// <summary>
/// What a tool does, and what goes in and comes out of it.
/// </summary>
/// <remarks>
/// A window rather than another box in the properties panel: that panel is
/// already long on a node like Find Blobs, and this is read once when a
/// tool is new to somebody and then not again.
/// </remarks>
public partial class NodeHelpWindow : Window
{
    public NodeHelpWindow(VisionNode node)
    {
        InitializeComponent();

        DarkTitleBar.Apply(this);

        DataContext = new NodeHelpViewModel(node);
        Title = $"{node.Descriptor.DisplayName} - about this tool";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
