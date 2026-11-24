using System.Windows;

namespace Sirius.IV;

/// <summary>
/// What to export as CSV: the pictures chosen or all listed, a row per
/// picture or per shape. Or, drawn: which pictures to write with their
/// annotations drawn on.
/// </summary>
public partial class ExportWindow : Window
{
    private ExportWindow(int chosen, int listed, bool drawn = false)
    {
        InitializeComponent();

        if (drawn)
        {
            Title = "Export Pictures with Annotations Drawn";
            RowsChoice.Visibility = Visibility.Collapsed;
            Note.Text = "Each picture as a PNG, drawn as Save Image with Overlays draws it: the layers switched on, and " +
                        "measurements drawn by hand. Each keeps its name, in its own sub-folder - a station's day - " +
                        "under the folder you choose next. The pictures themselves are left as they are.";
        }

        Sirius.Studio.App.DarkTitleBar.Apply(this);

        ChosenOption.Content = chosen == 1 ? "The picture chosen in the list" : $"The {chosen:N0} pictures chosen in the list";
        ListedOption.Content = listed == 1 ? "The picture listed" : $"All {listed:N0} pictures listed, as filtered and searched";

        // Several chosen: those, as the old ICB exported what was selected. One: most likely all were meant.
        ChosenOption.IsEnabled = chosen > 0;
        ChosenOption.IsChecked = chosen > 1;
        ListedOption.IsChecked = chosen <= 1;
    }

    /// <summary>Asks; null when cancelled.</summary>
    /// <returns>Whether only the pictures chosen, and whether a row per shape.</returns>
    public static (bool ChosenOnly, bool PerShape)? Ask(Window owner, int chosen, int listed)
    {
        var window = new ExportWindow(chosen, listed) { Owner = owner };

        if (window.ShowDialog() != true)
            return null;

        return (window.ChosenOption.IsChecked == true, window.ShapeOption.IsChecked == true);
    }

    /// <summary>Asks which pictures to draw; null when cancelled, else whether only those chosen.</summary>
    public static bool? AskDrawn(Window owner, int chosen, int listed)
    {
        var window = new ExportWindow(chosen, listed, drawn: true) { Owner = owner };
        return window.ShowDialog() == true ? window.ChosenOption.IsChecked == true : null;
    }

    private void OnExport(object sender, RoutedEventArgs e) => DialogResult = true;
}
