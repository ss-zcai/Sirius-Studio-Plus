using Sirius.Studio.App.ViewModels;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The panel behind a profile line drawn on the image viewer.
/// </summary>
/// <remarks>
/// It has no node behind it, so nothing in the graph will re-evaluate it. The
/// viewer re-samples on request instead, which makes the request itself worth
/// pinning: miss it and changing the band silently does nothing.
/// </remarks>
public class ProfileLinePanelTests
{
    private static ProfileLinePanelViewModel Panel() => new("profileline:test", "Profile 1");

    [Fact]
    public void Changing_the_band_asks_for_a_fresh_reading()
    {
        var panel = Panel();
        var requests = 0;
        panel.ResampleRequested += (_, _) => requests++;

        panel.Thickness = 5;

        Assert.Equal(1, requests);
        Assert.Equal(5, panel.Thickness);
    }

    /// <summary>
    /// Re-selecting the same width must not re-sample. The viewer re-samples
    /// on every drag already; a redundant pass per selection change would be
    /// paid on an image of any size.
    /// </summary>
    [Fact]
    public void Setting_the_band_it_already_has_asks_for_nothing()
    {
        var panel = Panel();
        panel.Thickness = 5;

        var requests = 0;
        panel.ResampleRequested += (_, _) => requests++;

        panel.Thickness = 5;

        Assert.Equal(0, requests);
    }

    [Fact]
    public void Renaming_changes_the_title_and_tells_the_view()
    {
        var panel = Panel();
        var changed = new List<string?>();
        panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        panel.Rename("Edge width");

        Assert.Equal("Edge width", panel.Title);
        Assert.Contains(nameof(ProfileLinePanelViewModel.Title), changed);
    }

    /// <summary>
    /// The title is a tab, so a blank one would leave an unidentifiable window.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_is_refused(string name)
    {
        var panel = Panel();

        panel.Rename(name);

        Assert.Equal("Profile 1", panel.Title);
    }

    /// <summary>
    /// Identity is what the docking layout keys on, so it must not follow the
    /// title - renaming a window would otherwise detach it from its place.
    /// </summary>
    [Fact]
    public void Renaming_does_not_change_the_panel_identity()
    {
        var panel = Panel();
        var id = panel.ContentId;

        panel.Rename("Something else");

        Assert.Equal(id, panel.ContentId);
    }

    [Fact]
    public void The_band_options_are_all_odd()
    {
        // An even band has no centre pixel, so it would sit half a pixel off
        // the line it is meant to be measuring.
        Assert.All(Panel().BandOptions, width => Assert.Equal(1, width % 2));
    }
}
