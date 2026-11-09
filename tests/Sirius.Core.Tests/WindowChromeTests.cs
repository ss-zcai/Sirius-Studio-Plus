using System.IO;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Every window the shell opens wears the shell's chrome.
/// </summary>
/// <remarks>
/// A title bar is non-client area, drawn by the operating system, so no
/// amount of WPF styling reaches it - each window has to ask for the dark
/// one itself. The main window did and nothing else did, which is the kind
/// of omission that is invisible until somebody opens the second window.
/// </remarks>
[Collection(WpfCollection.Name)]
public class WindowChromeTests
{
    public WindowChromeTests(WpfTestHost host) => _ = host;

    private static string Source => Repository.AppSource;

    public static TheoryData<string> Windows
    {
        get
        {
            var data = new TheoryData<string>();

            foreach (var file in Directory.GetFiles(Source, "*Window.xaml.cs"))
                data.Add(Path.GetFileName(file));

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Windows))]
    public void Every_window_asks_for_the_dark_title_bar(string file)
    {
        var text = File.ReadAllText(Path.Combine(Source, file));

        Assert.True(
            text.Contains("DarkTitleBar.Apply", StringComparison.Ordinal),
            $"{file} opens with a light title bar on a dark shell");
    }

    /// <summary>
    /// And a window exists to be found - a theory over an empty set passes
    /// without asserting anything.
    /// </summary>
    [Fact]
    public void There_are_windows_to_check() =>
        Assert.NotEmpty(Directory.GetFiles(Source, "*Window.xaml.cs"));

    /// <summary>
    /// About shows the product's own icon, at a usable size.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It showed the Run node's play triangle - a toolbar action standing in
    /// as a logo, on the one screen whose whole job is to say what this
    /// application is.
    /// </para>
    /// <para>
    /// It is the vector logo, drawn from the SVG master, so it is sharp at
    /// any size. It was the .ico, where a decoder left to choose a frame
    /// can hand back the 16-pixel one, scaled up to 40.
    /// </para>
    /// </remarks>
    [Fact]
    public void About_shows_the_application_logo() =>
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            Assert.IsType<System.Windows.Media.DrawingImage>(System.Windows.Application.Current!.FindResource("Icon.SiriusStudioPlus"));

            var xaml = File.ReadAllText(Path.Combine(Source, "AboutWindow.xaml"));

            Assert.Contains("Icon.SiriusStudioPlus", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("Icon.ph_run", xaml, StringComparison.Ordinal);
        });

    /// <summary>
    /// Each program's logo is generated from its SVG master, as its .ico is,
    /// not drawn a second time by hand.
    /// </summary>
    [Theory]
    [InlineData("Sirius.Studio.App", "Sirius Studio Plus")]
    [InlineData("Sirius.Builder", "Sirius Builder")]
    [InlineData("Sirius.Edge", "Sirius Edge")]
    public void Each_program_has_its_own_icon_from_its_svg_master(string folder, string name)
    {
        var project = File.ReadAllText(Path.Combine(Source, "..", folder, folder + ".csproj"));
        var logo = File.ReadAllText(Path.Combine(Source, "Themes", "Logo.xaml"));
        var res = Path.Combine(Source, "..", "..", "res");

        Assert.Contains($@"<ApplicationIcon>..\..\res\{name}.ico</ApplicationIcon>", project, StringComparison.Ordinal);
        Assert.Contains($@"""res/{name}.svg""", logo, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(res, name + ".svg")));
        Assert.True(File.Exists(Path.Combine(res, name + ".ico")));
    }

    /// <summary>
    /// The one dialog that was a stock MessageBox is a window now. The
    /// prompts that remain - unsaved changes, and reporting a failure - are
    /// deliberately left: they are questions the operating system asks in
    /// every application, and a hand-built modal would have to reproduce
    /// their keyboard and default-button behaviour to be no better.
    /// </summary>
    [Fact]
    public void About_is_not_a_message_box()
    {
        var text = File.ReadAllText(Path.Combine(Source, "MainWindow.xaml.cs"));
        var about = text.IndexOf("OnAboutClick", StringComparison.Ordinal);

        Assert.True(about >= 0, "the About handler has gone");

        var handler = text[about..Math.Min(text.Length, about + 300)];

        Assert.DoesNotContain("MessageBox", handler, StringComparison.Ordinal);
        Assert.Contains("AboutWindow", handler, StringComparison.Ordinal);
    }
}
