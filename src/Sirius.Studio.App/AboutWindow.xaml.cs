using System.Windows;

namespace Sirius.Studio.App;

/// <summary>
/// Who made this and which build it is.
/// </summary>
/// <remarks>
/// The version is read from the assembly, so it cannot disagree with the
/// package it shipped in - which matters most here, since this is where a
/// tester is asked to look when reporting something.
/// </remarks>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        var version = typeof(AboutWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        VersionText.Text = $"version {version}";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
