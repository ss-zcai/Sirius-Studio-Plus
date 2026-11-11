using System.Windows;
using Sirius.Studio.App;

namespace Sirius.Builder;

/// <summary>Which pictures a station keeps when it runs.</summary>
public partial class ImageSavingWindow : Window
{
    private readonly ImageSavingViewModel _model;

    private ImageSavingWindow(ImageSavingViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _model = model;
        DataContext = model;
        Title = $"Image Saving - {model.Station.Name}";

        IntroText.Text =
            $"Which pictures {model.Station.Name} keeps when it runs - in Sirius Edge, and in a Test Run here. Each is " +
            "the picture its camera took, as taken, with what was found on it in a .sann file beside it, for Sirius IV " +
            "to show. Saving happens beside the inspection and never slows it.";
    }

    /// <summary>Shows the window; returns whether the settings were changed.</summary>
    public static bool Show(Window? owner, ImageSavingViewModel model)
    {
        var window = new ImageSavingWindow(model)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
        };

        if (window.Owner is null)
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return window.ShowDialog() == true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // A box being typed in has not committed yet.
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox box)
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();

        if (_model.Apply())
            DialogResult = true;
    }
}
