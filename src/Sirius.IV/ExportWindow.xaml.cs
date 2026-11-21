using System.Windows;

namespace Sirius.IV;

/// <summary>What to export as CSV: the pictures chosen or all listed, a row per picture or per shape.</summary>
public partial class ExportWindow : Window
{
    private ExportWindow(int chosen, int listed)
    {
        InitializeComponent();
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

    private void OnExport(object sender, RoutedEventArgs e) => DialogResult = true;
}
