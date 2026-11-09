using System.IO;
using System.Windows;
using Sirius.Studio.App.ViewModels;

namespace Sirius.Studio.App;

/// <summary>A station's settings changed on the line: seen, taken into the recipe, or forgotten.</summary>
public partial class LineSettingsWindow : Window
{
    private readonly LineSettingsViewModel _model;

    private LineSettingsWindow(LineSettingsViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _model = model;
        Title = $"Line Settings - {model.StationName}";

        IntroText.Text =
            $"Settings changed from a screen while {model.StationName} ran. They are kept beside the recipe and " +
            "used whenever it runs, over the recipe's own values. Take them into the recipe to make them its own, " +
            "or forget them to go back to the recipe's." +
            (model.ChangesEditor ? " This recipe is open in the editor: what is taken in is saved with it." : string.Empty);

        Refresh();

        if (model.Problem is { } problem)
            Say(problem);
    }

    /// <summary>Shows the window; returns once it is closed.</summary>
    public static void Show(Window? owner, LineSettingsViewModel model)
    {
        var window = new LineSettingsWindow(model)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
        };

        if (window.Owner is null)
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        window.ShowDialog();
    }

    private IReadOnlyList<LineSettingRow> Chosen =>
        Items.SelectedItems.Count > 0 ? Items.SelectedItems.OfType<LineSettingRow>().ToArray() : _model.Rows;

    private void OnTake(object sender, RoutedEventArgs e)
    {
        try
        {
            var problems = _model.TakeIntoRecipe(Chosen);
            Refresh();
            Say(problems.Count == 0
                ? _model.ChangesEditor ? "Taken into the recipe in the editor. Save it to keep them." : null
                : string.Join("\n", problems));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Say($"Could not change the recipe: {ex.Message}");
        }
    }

    private void OnForget(object sender, RoutedEventArgs e)
    {
        var chosen = Chosen;

        if (chosen.Count == 0)
            return;

        if (!Dialog.Confirm(
                chosen.Count == 1
                    ? $"Forget the line's value of {chosen[0].Name}? The recipe's own is used from the next start."
                    : $"Forget the line's values of these {chosen.Count} settings? The recipe's own are used from the next start.",
                Title,
                this))
        {
            return;
        }

        try
        {
            _model.Forget(chosen);
            Refresh();
            Say(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say($"Could not change the line settings file: {ex.Message}");
        }
    }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateButtons();

    private void Refresh()
    {
        Items.ItemsSource = _model.Rows;
        EmptyText.Visibility = _model.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var any = _model.Rows.Count > 0;
        TakeButton.IsEnabled = any;
        ForgetButton.IsEnabled = any;

        var all = Items.SelectedItems.Count == 0;
        TakeButton.Content = all ? "_Take All into Recipe" : "_Take into Recipe";
        ForgetButton.Content = all ? "_Forget All" : "_Forget";
    }

    private void Say(string? text)
    {
        ProblemText.Text = text ?? string.Empty;
        ProblemText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
