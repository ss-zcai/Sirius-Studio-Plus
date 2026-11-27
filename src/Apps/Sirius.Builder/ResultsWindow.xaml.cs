using System.Windows;
using Sirius.UI;

namespace Sirius.Builder;

/// <summary>The results a station gives its parts (F33).</summary>
public partial class ResultsWindow : Window
{
    private readonly ResultsViewModel _model;

    private ResultsWindow(ResultsViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _model = model;
        DataContext = model;
        Title = $"Results - {model.Station.Name}";

        IntroText.Text =
            $"The results {model.Station.Name} gives its parts, best first. A recipe's grade with a result's name is that " +
            "result; otherwise a part it passes is the first that counts as a pass, and one it fails the first that counts " +
            "as a fail. A result called ERROR, if there is one, is given when a node goes wrong. Inspect sends the PLC " +
            "the result's code; what each one means for the part is the PLC's.";
    }

    /// <summary>The window, made and not shown - for a test.</summary>
    internal static ResultsWindow Unshown(ResultsViewModel model) => new(model);

    /// <summary>Shows the window; returns whether the results were changed.</summary>
    public static bool Show(Window? owner, ResultsViewModel model)
    {
        var window = new ResultsWindow(model)
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
