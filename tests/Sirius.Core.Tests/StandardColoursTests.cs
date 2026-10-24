using System.Windows.Media;
using Sirius.Studio.App.ViewModels;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The palette offered wherever something is drawn over an image.
/// </summary>
/// <remarks>
/// One list serves the viewer's measurement picker and every Colour
/// parameter, so a measurement and an annotation set to the same green
/// actually match. These pin the properties that sharing depends on.
/// </remarks>
public class StandardColoursTests
{
    [Fact]
    public void Every_colour_parses()
    {
        Assert.All(
            StandardColours.All,
            option => Assert.IsType<Color>(ColorConverter.ConvertFromString(option.Hex)));
    }

    [Fact]
    public void Names_and_values_are_both_distinct()
    {
        var names = StandardColours.All.Select(option => option.Name).ToList();
        var values = StandardColours.All.Select(option => option.Hex.ToUpperInvariant()).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(values.Count, values.Distinct().Count());
    }

    /// <summary>
    /// A stored hex has to find its swatch, or the picker would show nothing
    /// selected for a colour it offers.
    /// </summary>
    [Fact]
    public void A_stored_value_finds_its_swatch()
    {
        foreach (var option in StandardColours.All)
        {
            Assert.Same(option, StandardColours.Find(option.Hex));
            Assert.Same(option, StandardColours.Find(option.Hex.ToLowerInvariant()));
        }
    }

    /// <summary>
    /// Null rather than a nearest match: a hex typed by hand should stay what
    /// was typed, and showing a near swatch would suggest it had been changed.
    /// </summary>
    [Theory]
    [InlineData("#123456")]
    [InlineData("")]
    [InlineData(null)]
    public void A_colour_outside_the_palette_matches_nothing(string? hex)
    {
        Assert.Null(StandardColours.Find(hex));
    }

    /// <summary>
    /// Black's swatch is black, but its name cannot be - it would vanish into
    /// the panel behind it.
    /// </summary>
    [Fact]
    public void Black_is_named_in_a_colour_that_can_be_read()
    {
        var black = StandardColours.All.Single(option => option.Name == "Black");

        Assert.NotEqual(
            ((SolidColorBrush)black.Swatch).Color,
            ((SolidColorBrush)black.Text).Color);
    }

    [Fact]
    public void Every_other_colour_is_named_in_itself()
    {
        foreach (var option in StandardColours.All.Where(o => o.Name != "Black"))
        {
            Assert.Equal(
                ((SolidColorBrush)option.Swatch).Color,
                ((SolidColorBrush)option.Text).Color);
        }
    }
}
