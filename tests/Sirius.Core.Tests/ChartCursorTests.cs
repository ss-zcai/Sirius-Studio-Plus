using System.Windows;
using Sirius.Studio.App.Controls;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The readout cursor on the charts.
/// </summary>
/// <remarks>
/// Only the mapping from a pointer position to a sample is tested. It is
/// the part that can be quietly wrong - a cursor one sample out still looks
/// like a working cursor - and the only part that does not need a mouse.
/// </remarks>
public class ChartCursorTests
{
    private static readonly Rect Plot = new(40, 10, 200, 100);

    [Fact]
    public void The_cursor_snaps_to_the_nearest_sample()
    {
        // 101 samples over 200 pixels: two pixels each.
        Assert.Equal(0, ProfileControl.SampleAt(Plot.Left, Plot, 101));
        Assert.Equal(50, ProfileControl.SampleAt(Plot.Left + 100, Plot, 101));
        Assert.Equal(100, ProfileControl.SampleAt(Plot.Right, Plot, 101));
    }

    /// <summary>
    /// Rounded rather than truncated. Truncating puts the readout a sample
    /// behind the line across the whole right half of every step, which
    /// reads as lag rather than as an error.
    /// </summary>
    [Fact]
    public void A_position_past_the_halfway_point_takes_the_next_sample()
    {
        Assert.Equal(1, ProfileControl.SampleAt(Plot.Left + 1.1, Plot, 101));
        Assert.Equal(0, ProfileControl.SampleAt(Plot.Left + 0.9, Plot, 101));
    }

    [Fact]
    public void A_position_outside_the_plot_clamps_to_an_end()
    {
        Assert.Equal(0, ProfileControl.SampleAt(-500, Plot, 101));
        Assert.Equal(100, ProfileControl.SampleAt(5000, Plot, 101));
    }

    /// <summary>
    /// An empty or single-sample profile has nothing to divide by, and a
    /// chart that throws while the pointer crosses it is worse than one
    /// without a cursor.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_profile_too_short_to_plot_reports_the_first_sample(int count) =>
        Assert.Equal(0, ProfileControl.SampleAt(100, Plot, count));

    [Fact]
    public void An_unmeasured_plot_does_not_divide_by_its_width() =>
        Assert.Equal(0, ProfileControl.SampleAt(100, new Rect(0, 0, 0, 0), 101));
}
