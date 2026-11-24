using System.Windows;

namespace Sirius.Studio.App;

/// <summary>
/// Shows every themed control in one window.
/// </summary>
/// <remarks>
/// Styling is reviewed by eye, and a control is easy to miss when it only
/// appears in one corner of one panel - the sliders stayed in the stock Windows
/// style for a long time for exactly that reason.
/// </remarks>
public partial class GalleryWindow : Window
{
    public GalleryWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
    }
}
