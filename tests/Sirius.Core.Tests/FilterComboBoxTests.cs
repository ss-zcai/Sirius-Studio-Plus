using System.Windows;
using System.Windows.Controls;
using Emgu.CV.CvEnum;
using Sirius.Studio.App.Controls;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Finding one entry in a list too long to read.
/// </summary>
/// <remarks>
/// OpenCV's colour conversions run to about a hundred names, all built
/// the same way - a source space, a 2, a destination space. Scrolling
/// to one means reading past every pair that shares its first letter,
/// and WPF's own type-ahead only matches the start of a name, which in
/// a list like this is the least distinctive part of it.
/// </remarks>
[Collection(WpfCollection.Name)]
public class FilterComboBoxTests
{
    public FilterComboBoxTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
    }

    // ---- what counts as a match ------------------------------------------

    [Theory]
    [InlineData("Bgr2Gray", "gray")]
    [InlineData("Bgr2Gray", "GRAY")]
    [InlineData("Bgr2Gray", "2gray")]
    [InlineData("Bgr2Gray", "bgr")]
    [InlineData("Bgr2Gray", "gray bgr")]
    [InlineData("Bgr2Hsv", "hsv")]
    public void A_name_is_found_by_any_part_of_it(string candidate, string typed) =>
        Assert.True(FilterComboBox.Matches(candidate, typed));

    [Theory]
    [InlineData("Bgr2Gray", "hsv")]
    [InlineData("Bgr2Gray", "gray lab")]
    [InlineData("Bgr2Gray", "grey")]
    public void A_name_that_does_not_contain_it_is_not(string candidate, string typed) =>
        Assert.False(FilterComboBox.Matches(candidate, typed));

    /// <summary>Nothing typed hides nothing.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_empty_search_matches_everything(string? typed) =>
        Assert.True(FilterComboBox.Matches("Bgr2Gray", typed));

    // ---- the control ------------------------------------------------------

    /// <summary>
    /// A searchable combo in a window, laid out but never shown.
    /// </summary>
    /// <remarks>
    /// The window is what makes it a real control: an element with no
    /// parent is never given its implicit style, so on its own it has no
    /// template, no text box inside it, and nothing to type into.
    /// </remarks>
    private static FilterComboBox Laid(params string[] items)
    {
        WpfTestHost.EnsureApplicationResources();

        var combo = new FilterComboBox { ItemsSource = items, IsEditable = true };
        var shell = new Window { Content = combo };

        shell.Measure(new Size(400, 200));
        shell.Arrange(new Rect(0, 0, 400, 200));
        shell.UpdateLayout();

        // An unshown window never gets round to building its content, so
        // the template is chosen but not instantiated and the control has
        // no parts.
        combo.ApplyTemplate();

        return combo;
    }

    private static string[] Showing(FilterComboBox combo) =>
        combo.Items.Cast<object>().Select(item => item.ToString()!).ToArray();

    /// <summary>
    /// Typing is wired to the text box inside the control's template. If
    /// that part is not found - a renamed part, a template that does not
    /// supply one - the filter still works when called but nothing ever
    /// calls it, and the search box silently does nothing.
    /// </summary>
    [Fact]
    public void The_editable_text_box_is_found_so_typing_reaches_the_filter() =>
        WpfTestHost.OnStaThread(() =>
        {
            var combo = Laid("Bgr2Gray", "Bgr2Hsv");

            Assert.NotNull(combo.Template);
            Assert.IsAssignableFrom<TextBox>(
                combo.Template.FindName("PART_EditableTextBox", combo));
        });

    /// <summary>
    /// And it is dressed as the others are. An implicit style is keyed on
    /// the exact type, so a derived combo box finds none and arrives in
    /// Windows' own light grey - in a dark panel, unmissable.
    /// </summary>
    [Fact]
    public void It_is_themed_like_every_other_dropdown() =>
        WpfTestHost.OnStaThread(() =>
        {
            var combo = Laid("Bgr2Gray", "Bgr2Hsv");

            var plain = new ComboBox();
            var shell = new Window { Content = plain };
            shell.Measure(new Size(400, 200));
            shell.Arrange(new Rect(0, 0, 400, 200));
            shell.UpdateLayout();

            Assert.Equal(typeof(FilterComboBox), combo.Style?.TargetType);
            Assert.Equal(plain.Background.ToString(), combo.Background.ToString());
            Assert.Equal(plain.BorderBrush.ToString(), combo.BorderBrush.ToString());
        });

    [Fact]
    public void Typing_narrows_the_list() =>
        WpfTestHost.OnStaThread(() =>
        {
            var combo = Laid("Bgr2Gray", "Bgr2Hsv", "Rgb2Gray", "Hsv2Bgr");

            combo.Filter("gray");

            Assert.Equal(["Bgr2Gray", "Rgb2Gray"], Showing(combo));
        });

    [Fact]
    public void Clearing_the_search_puts_the_list_back() =>
        WpfTestHost.OnStaThread(() =>
        {
            var combo = Laid("Bgr2Gray", "Bgr2Hsv", "Rgb2Gray");

            combo.Filter("gray");
            combo.Filter("");

            Assert.Equal(3, Showing(combo).Length);
        });

    /// <summary>
    /// A ComboBox drops a selection that is filtered out of its list, so
    /// searching for something else would quietly clear the parameter.
    /// </summary>
    [Fact]
    public void A_search_that_excludes_the_chosen_value_does_not_clear_it() =>
        WpfTestHost.OnStaThread(() =>
        {
            var combo = Laid("Bgr2Gray", "Bgr2Hsv", "Rgb2Gray");
            combo.SelectedItem = "Bgr2Hsv";

            combo.Filter("gray");

            Assert.Equal("Bgr2Hsv", combo.SelectedItem);
            Assert.Contains("Bgr2Hsv", Showing(combo));
        });

    // ---- which parameters get one ------------------------------------------

    /// <summary>
    /// The list this was built for. Checked against the real node rather
    /// than a number, so trimming the conversions on offer would show up
    /// here rather than leaving a search box over a short list.
    /// </summary>
    [Fact]
    public void The_colour_conversions_are_searchable()
    {
        var parameter = Conversion();

        Assert.True(parameter.IsSearchable);
        Assert.True(parameter.Options.Count > 50);
    }

    [Fact]
    public void A_short_list_is_left_as_an_ordinary_dropdown()
    {
        var node = new LabelNode();
        var parameter = new NodeViewModel(node)
            .Parameters.Single(p => p.DisplayName == "Typeface");

        Assert.True(parameter.IsComboBox);
        Assert.False(parameter.IsSearchable);
    }

    /// <summary>
    /// An enum may name one value twice; Enum.GetValues returns an entry
    /// per name, so a conversion appeared twice and only the first of the
    /// pair could ever show as the chosen one. OpenCV's list is 242 names
    /// over 138 conversions.
    /// </summary>
    [Fact]
    public void A_conversion_with_two_names_is_offered_once()
    {
        var options = Conversion().Options;

        Assert.Equal(options.Count, options.Distinct().Count());
        Assert.Equal(Enum.GetValues<ColorConversion>().Distinct().Count(), options.Count);
    }

    /// <summary>
    /// What merges is a conversion with two spellings, never two
    /// conversions. Gray to BGR and gray to RGB are one operation - a
    /// single channel copied three ways, where the order of three equal
    /// channels cannot differ - so OpenCV gives them one number.
    /// </summary>
    [Fact]
    public void Only_the_spellings_of_one_conversion_merge()
    {
        Assert.Equal((int)ColorConversion.Gray2Rgb, (int)ColorConversion.Gray2Bgr);
        Assert.Equal((int)ColorConversion.Gray2Rgba, (int)ColorConversion.Gray2Bgra);
    }

    /// <summary>
    /// And the ones that differ stay. Three channels out is not four
    /// channels out, whatever the names look like side by side.
    /// </summary>
    [Fact]
    public void Three_channels_and_four_remain_separate_choices()
    {
        var options = Conversion().Options.Cast<ColorConversion>().ToArray();

        Assert.NotEqual((int)ColorConversion.Gray2Rgb, (int)ColorConversion.Gray2Rgba);

        Assert.Contains(ColorConversion.Gray2Rgb, options);
        Assert.Contains(ColorConversion.Gray2Rgba, options);
    }

    private static ParameterViewModel Conversion() =>
        new NodeViewModel(new ColourConvertNode())
            .Parameters.Single(p => p.DisplayName == "Conversion");
}
