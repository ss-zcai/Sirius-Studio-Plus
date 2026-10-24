using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Controls the theme redraws must be redrawn completely.
/// </summary>
/// <remarks>
/// Styling a control's colours without replacing its template leaves the
/// stock one underneath. That is not always visible: the context menu had
/// its items templated from the day the main menu was built, and only its
/// own chrome was stock - which showed up as the pale rail Windows reserves
/// for icons sitting over the first character of every label.
/// </remarks>
[Collection(WpfCollection.Name)]
public class ThemeTemplateTests
{
    public ThemeTemplateTests(WpfTestHost host) => _ = host;

    /// <summary>
    /// A control whose look is fully replaced, and which therefore needs a
    /// template rather than a handful of brushes.
    /// </summary>
    public static TheoryData<string> Redrawn => new()
    {
        nameof(ContextMenu),
        nameof(MenuItem),
        nameof(ComboBox),
        nameof(ToolBar),
    };

    [Theory]
    [MemberData(nameof(Redrawn))]
    public void The_theme_replaces_the_template_as_well_as_the_colours(string control)
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var type = typeof(Control).Assembly.GetType($"System.Windows.Controls.{control}")!;
            var style = (Style?)Application.Current!.TryFindResource(type);

            Assert.True(style is not null, $"no implicit style for {control}");

            var templated =
                style!.Setters.OfType<Setter>().Any(s => s.Property == Control.TemplateProperty) ||
                style.Triggers.OfType<Trigger>().SelectMany(t => t.Setters).OfType<Setter>()
                    .Any(s => s.Property == Control.TemplateProperty);

            Assert.True(templated, $"{control} is coloured but still draws its stock template");
        });
    }

    /// <summary>
    /// And the template has to survive being applied - a missing part or an
    /// unresolvable resource only shows up when something is built with it.
    /// </summary>
    [Fact]
    public void A_context_menu_builds_with_the_themed_template()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "Preview In", InputGestureText = "input" });
            menu.Items.Add(new MenuItem { Header = "Preview Out", InputGestureText = "output" });

            menu.ApplyTemplate();
            menu.Measure(new Size(300, 300));
            menu.Arrange(new Rect(0, 0, 300, 300));
            menu.UpdateLayout();

            Assert.NotNull(menu.Template);
        });
    }
}
