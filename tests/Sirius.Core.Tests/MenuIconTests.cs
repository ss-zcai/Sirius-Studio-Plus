using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Sirius.Studio.App;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The icon beside a menu item, and the mark on the current one.
/// </summary>
/// <remarks>
/// The Style menu sets an Icon on every item - a swatch of the colour it
/// applies - and ticks the one already in use. Neither appeared: the
/// template left a 22-unit margin where an icon would go and drew
/// nothing in it, so the menu named eight colours and showed none, and
/// the current one was marked where the mark could not be seen.
/// </remarks>
[Collection(WpfCollection.Name)]
public class MenuIconTests
{
    public MenuIconTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
    }

    /// <summary>
    /// A menu item under the submenu template, laid out and built.
    /// </summary>
    /// <remarks>
    /// The template is set by hand because the one a MenuItem picks
    /// depends on its Role, and an item only takes the submenu role once
    /// its parent's popup has opened - which needs a menu on screen.
    /// </remarks>
    private static MenuItem Laid(object? icon, bool isChecked)
    {
        WpfTestHost.EnsureApplicationResources();

        var item = new MenuItem
        {
            Header = "Green",
            Icon = icon,
            IsCheckable = true,
            IsChecked = isChecked,
            Template = (ControlTemplate)Application.Current.FindResource("MenuSubmenuItemTemplate"),
        };

        var shell = new Window { Content = item };
        shell.Measure(new Size(400, 200));
        shell.Arrange(new Rect(0, 0, 400, 200));
        shell.UpdateLayout();
        item.ApplyTemplate();

        // The template is built but its presenters have not expanded
        // their content until something measures them.
        item.Measure(new Size(400, 40));
        item.Arrange(new Rect(0, 0, 400, 40));
        item.UpdateLayout();

        return item;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;

            foreach (var further in Descendants(child))
                yield return further;
        }
    }

    private static Border Gutter(MenuItem item) =>
        (Border)item.Template.FindName("Gutter", item);

    // ---- the icon ---------------------------------------------------------

    [Fact]
    public void An_item_draws_the_icon_it_was_given() =>
        WpfTestHost.OnStaThread(() =>
        {
            var swatch = new Border { Width = 14, Height = 12, Background = Brushes.Green };
            var item = Laid(swatch, isChecked: false);

            Assert.Contains(swatch, Descendants(item).OfType<Border>());
        });

    [Fact]
    public void An_item_without_one_draws_nothing_there() =>
        WpfTestHost.OnStaThread(() =>
        {
            var item = Laid(icon: null, isChecked: false);

            Assert.Empty(Descendants(Gutter(item)).OfType<Border>());
        });

    // ---- the current one --------------------------------------------------

    /// <summary>
    /// A framed gutter rather than a tick over the swatch: the colour is
    /// the point of the item, and a mark on top of it would cover the
    /// thing being chosen.
    /// </summary>
    [Fact]
    public void The_current_item_is_framed() =>
        WpfTestHost.OnStaThread(() =>
        {
            var swatch = new Border { Width = 14, Height = 12, Background = Brushes.Green };

            var current = Laid(swatch, isChecked: true);
            var other = Laid(new Border(), isChecked: false);

            Assert.NotEqual(
                Gutter(other).BorderBrush.ToString(),
                Gutter(current).BorderBrush.ToString());
        });

    /// <summary>
    /// An item with no icon gets an ordinary tick, so this works for the
    /// checkable items that are not about a colour.
    /// </summary>
    [Fact]
    public void A_checked_item_with_no_icon_shows_a_tick() =>
        WpfTestHost.OnStaThread(() =>
        {
            var ticked = (System.Windows.Shapes.Path)Laid(null, isChecked: true)
                .Template.FindName("Tick", Laid(null, isChecked: true));

            Assert.NotNull(ticked);
        });

    // ---- what the Style menu actually builds -------------------------------

    /// <summary>
    /// Every accent arrives with a swatch of its own colour. Read off the
    /// real menu, so this says the two halves meet rather than that each
    /// works alone.
    /// </summary>
    [Fact]
    public void Every_style_offered_carries_its_colour() =>
        WpfTestHost.OnStaThread(() =>
        {
            var node = new NodeViewModel(new ColourConvertNode());

            var menu = (MenuItem)typeof(MainWindow)
                .GetMethod("BuildAccentMenu", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [node])!;

            var items = menu.Items.OfType<MenuItem>().ToArray();

            Assert.Equal(NodeAccents.All.Count, items.Length);

            Assert.All(items, item =>
            {
                var swatch = Assert.IsType<Border>(item.Icon);

                Assert.NotNull(swatch.Background);
                Assert.NotNull(swatch.BorderBrush);
            });

            // Exactly one is the node's own, and it is the one ticked.
            var current = Assert.Single(items.Where(i => i.IsChecked));

            Assert.Equal(NodeAccents.Of(node.Node.Accent).Name, current.Header);
        });
}
