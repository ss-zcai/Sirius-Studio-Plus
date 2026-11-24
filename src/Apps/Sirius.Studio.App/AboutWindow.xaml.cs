using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace Sirius.Studio.App;

/// <summary>
/// Who made this and which build it is.
/// </summary>
/// <remarks>
/// The version is read from the assembly, so it cannot disagree with the
/// package it shipped in - which matters most here, since this is where a
/// tester is asked to look when reporting something.
/// <para>
/// Sirius Builder shows the same window with its own name, logo and
/// assembly: each program is versioned on its own.
/// </para>
/// </remarks>
public partial class AboutWindow : Window
{
    /// <summary>Sirius Studio Plus's About.</summary>
    public AboutWindow()
        : this(null, null, null, typeof(AboutWindow).Assembly)
    {
    }

    /// <summary>Another program's About: anything left null is Studio's.</summary>
    public AboutWindow(string? product, ImageSource? logo, string? summary, Assembly program)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        if (product is not null)
        {
            Title = $"About {product}";
            ProductText.Text = product;
        }

        if (logo is not null)
            LogoImage.Source = logo;

        if (summary is not null)
            SummaryText.Text = summary;

        VersionText.Text = $"version {VersionOf(program)}";
    }

    /// <summary>The version a program reports: major.minor.build.</summary>
    public static string VersionOf(Assembly program) =>
        program.GetName().Version?.ToString(3) ?? "unknown";

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
