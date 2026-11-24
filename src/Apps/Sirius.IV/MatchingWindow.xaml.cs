using System.Windows;
using Sirius.Studio.App;

namespace Sirius.IV;

/// <summary>How pictures that belong together are found.</summary>
public partial class MatchingWindow : Window
{
    private readonly MatchingViewModel _model;

    internal MatchingWindow(MatchingViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _model = model;
        DataContext = model;
    }

    /// <summary>Shows the dialog; the rule chosen, or null when cancelled.</summary>
    public static MatchRule? Ask(Window? owner, BrowserViewModel browser)
    {
        var window = new MatchingWindow(new MatchingViewModel(browser.Match, browser.SetsFor))
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
        };

        if (window.Owner is null)
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return window.ShowDialog() == true ? window._model.Rule : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // A box being typed in has not committed yet.
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox box)
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();

        if (_model.Problem is null)
            DialogResult = true;
    }
}
