using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using NodeGraphKit.Controls;

namespace NodeGraphKit.Tests;

/// <summary>
/// Loads the theme and the three views, so a broken key or a typo in XAML
/// fails here rather than as a blank window on someone else's PC.
/// </summary>
public class ThemeTests
{
    private static readonly object Gate = new();

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                lock (Gate)
                {
                    if (Application.Current is null)
                    {
                        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri("pack://application:,,,/NodeGraphKit;component/Themes/NodeGraphKit.xaml"),
                        });
                    }
                }

                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    /// <remarks>
    /// One test rather than three: a WPF Application belongs to the thread
    /// that made it, so everything that uses it has to run on that thread.
    /// </remarks>
    [Fact]
    public void The_theme_icons_and_views_all_load() =>
        OnSta(() =>
        {
            // The views, against the theme's keys.
            Assert.NotNull(new GraphEditorView());
            Assert.NotNull(new ToolboxView());
            Assert.NotNull(new PropertiesView());

            // Every icon draws something.
            var icons = Application.Current.Resources.MergedDictionaries[0].MergedDictionaries[0];
            Assert.NotEmpty(icons.Keys);

            foreach (var key in icons.Keys)
            {
                var image = Assert.IsAssignableFrom<DrawingImage>(icons[key]);
                Assert.True(image.Width > 0, $"{key} is empty");
            }

            // An unknown icon key shows the puzzle piece rather than a gap.
            var fallback = new IconKeyToImageConverter().Convert(
                "no_such_icon", typeof(ImageSource), null!, System.Globalization.CultureInfo.InvariantCulture);

            Assert.Same(Application.Current.FindResource("Icon.ph_custom_node"), fallback);

            // Menus drop to the right, whatever the PC's handedness setting.
            MenuAlignment.UseLeftAligned();
            Assert.False(SystemParameters.MenuDropAlignment);
        });

    /// <summary>
    /// The alignment fix writes to a private WPF field; this fails if a
    /// .NET update renames it, rather than menus quietly flipping back.
    /// </summary>
    [Fact]
    public void The_menu_alignment_fix_still_finds_its_field() =>
        Assert.True(MenuAlignment.IsSupported);
}
