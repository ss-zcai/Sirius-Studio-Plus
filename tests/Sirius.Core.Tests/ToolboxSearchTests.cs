using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using AvalonDock;
using AvalonDock.Layout;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Serialization;
using Sirius.Studio.App;
using Sirius.Studio.App.ViewModels;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Finding a tool by typing rather than by browsing fifty of them.
/// </summary>
[Collection(WpfCollection.Name)]
public class ToolboxSearchTests
{
    public ToolboxSearchTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
    }

    private static MainViewModel Model()
    {
        var folder = CustomNodeTests.Scratch();

        return new MainViewModel(new CustomNodesViewModel(
            Path.Combine(folder, "custom-nodes.json"),
            Path.Combine(folder, "shipped"),
            Path.Combine(folder, "user")));
    }

    private static string[] Listed(MainViewModel model) =>
        model.Palette.SelectMany(group => group.Items).Select(item => item.DisplayName).ToArray();

    /// <summary>
    /// Line Profile is retired - a profile line on any image viewer does its
    /// job - so it is not offered, even by name; but a graph that has one
    /// still loads it.
    /// </summary>
    [Fact]
    public void A_retired_tool_is_not_offered_but_still_loads()
    {
        var model = Model();

        Assert.DoesNotContain("Line Profile", Listed(model));

        model.ToolboxSearch = "line profile";
        Assert.DoesNotContain("Line Profile", Listed(model));

        var graph = new VisionGraph();
        graph.Add(new Sirius.Vision.Nodes.LineProfileNode());

        var loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));

        Assert.IsType<Sirius.Vision.Nodes.LineProfileNode>(Assert.Single(loaded.Graph.Nodes));
    }

    [Fact]
    public void Typing_narrows_the_toolbox_to_what_matches()
    {
        var model = Model();

        model.ToolboxSearch = "blur";

        var listed = Listed(model);
        Assert.Contains("Gaussian Blur", listed);
        Assert.Contains("Median Blur", listed);
        Assert.DoesNotContain("Threshold", listed);
        Assert.True(model.IsSearchingToolbox);
    }

    /// <summary>
    /// The same rule as the long dropdowns: every word, anywhere, any order.
    /// "fit" also matches the Fitting group, so Find Circle is listed too -
    /// but after Fit Circle, whose own name has both words, which is what
    /// Enter adds.
    /// </summary>
    [Fact]
    public void Words_match_in_any_order_and_the_closest_name_comes_first()
    {
        var model = Model();

        model.ToolboxSearch = "circle fit";

        Assert.Equal("Fit Circle", model.FirstToolboxMatch?.DisplayName);
    }

    [Fact]
    public void Every_word_has_to_match_something()
    {
        var model = Model();

        model.ToolboxSearch = "circle zzz";

        Assert.Empty(Listed(model));
    }

    [Theory]
    [InlineData("qr", "Read Code")]
    [InlineData("ocr", "Read Text")]
    [InlineData("barcode", "Read Code")]
    public void A_tool_can_be_found_by_what_it_does(string search, string tool)
    {
        var model = Model();

        model.ToolboxSearch = search;

        Assert.Contains(tool, Listed(model));
    }

    [Fact]
    public void A_group_name_brings_up_the_whole_group()
    {
        var model = Model();

        model.ToolboxSearch = "morphology";

        var listed = Listed(model);
        Assert.Contains("Dilate", listed);
        Assert.Contains("Erode", listed);
    }

    /// <summary>
    /// A tool whose name matches is listed before one found only through
    /// its description, in every group - what was typed is most often the
    /// start of a name.
    /// </summary>
    [Fact]
    public void Name_matches_come_before_description_matches()
    {
        var model = Model();

        model.ToolboxSearch = "image";

        foreach (var group in model.Palette)
        {
            var byName = group.Items
                .Select(item => item.DisplayName.Contains("image", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            // Once an item matched by description alone appears, no name
            // match follows it.
            var firstDescriptionOnly = Array.IndexOf(byName, false);
            if (firstDescriptionOnly >= 0)
                Assert.DoesNotContain(true, byName.Skip(firstDescriptionOnly));
        }
    }

    [Fact]
    public void Every_group_with_a_match_is_open()
    {
        var model = Model();
        model.Palette.Single(group => group.Category == NodeCategory.Filtering).IsExpanded = false;

        model.ToolboxSearch = "blur";

        Assert.All(model.Palette, group => Assert.True(group.IsExpanded));
    }

    /// <summary>
    /// Searching opens every group. Clearing it must put back the groups the
    /// operator had closed, not leave everything open.
    /// </summary>
    [Fact]
    public void Clearing_the_search_puts_back_the_groups_that_were_closed()
    {
        var model = Model();
        model.Palette.Single(group => group.Category == NodeCategory.Filtering).IsExpanded = false;

        model.ToolboxSearch = "blur";
        model.ToolboxSearch = string.Empty;

        Assert.False(model.Palette.Single(group => group.Category == NodeCategory.Filtering).IsExpanded);
        Assert.True(model.Palette.Single(group => group.Category == NodeCategory.Basic).IsExpanded);
        Assert.False(model.IsSearchingToolbox);
    }

    [Fact]
    public void A_search_that_finds_nothing_says_so()
    {
        var model = Model();

        model.ToolboxSearch = "zzzz";

        Assert.Empty(model.Palette);
        Assert.True(model.ToolboxHasNoMatches);
        Assert.Null(model.FirstToolboxMatch);
    }

    [Fact]
    public void Enter_adds_the_first_tool_listed()
    {
        var model = Model();

        model.ToolboxSearch = "gaussian";

        Assert.Equal("Gaussian Blur", model.FirstToolboxMatch?.DisplayName);
    }

    /// <summary>The boundary nodes are still kept out of the recipe's own tab, search or not.</summary>
    [Fact]
    public void Searching_does_not_offer_what_the_tab_does_not()
    {
        var model = Model();

        model.ToolboxSearch = "graph input";

        Assert.DoesNotContain("Graph Input", Listed(model));
    }

    [Fact]
    public void The_search_box_stays_above_the_list_rather_than_scrolling_with_it() =>
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var window = new MainWindow();
            var docking = (DockingManager)window.FindName("Docking");

            var toolbox = docking.Layout.Descendents().OfType<LayoutAnchorable>()
                .Single(anchorable => anchorable.ContentId == "toolbox");

            var panel = Assert.IsType<DockPanel>(toolbox.Content);
            var children = panel.Children.Cast<System.Windows.UIElement>().ToArray();

            var search = (TextBox)window.FindName("ToolboxSearchBox");
            var searchRow = children.First();

            Assert.Equal(Dock.Top, DockPanel.GetDock(searchRow));
            Assert.Same(searchRow, System.Windows.LogicalTreeHelper.GetParent(search));
            Assert.IsType<ScrollViewer>(children.Last());

            Assert.Equal(
                nameof(MainViewModel.ToolboxSearch),
                BindingOperations.GetBinding(search, TextBox.TextProperty)?.Path.Path);
        });
}
