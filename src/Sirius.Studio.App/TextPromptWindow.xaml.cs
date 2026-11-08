using System.Windows;
using System.Windows.Controls;

namespace Sirius.Studio.App;

/// <summary>Asks for one line of text, in the shell's colours.</summary>
public partial class TextPromptWindow : Window
{
    private readonly Func<string, string?>? _problem;

    private TextPromptWindow(string title, string prompt, string initial, Func<string, string?>? problem)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _problem = problem;

        Title = title;
        PromptText.Text = prompt;
        AnswerBox.Text = initial;

        Loaded += (_, _) =>
        {
            AnswerBox.Focus();
            AnswerBox.SelectAll();
        };
    }

    /// <summary>
    /// Shows the prompt and returns the trimmed answer, or null when it was
    /// cancelled.
    /// </summary>
    /// <param name="problem">Says why an answer cannot be accepted, or null when it can; OK waits for it.</param>
    public static string? Ask(Window? owner, string title, string prompt, string initial, Func<string, string?>? problem = null)
    {
        owner ??= Application.Current?.MainWindow;

        var window = new TextPromptWindow(title, prompt, initial, problem)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
        };

        if (window.Owner is null)
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return window.ShowDialog() == true ? window.AnswerBox.Text.Trim() : null;
    }

    private void OnAnswerChanged(object sender, TextChangedEventArgs e)
    {
        var problem = _problem?.Invoke(AnswerBox.Text);

        ProblemText.Text = problem ?? string.Empty;
        ProblemText.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = problem is null;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
