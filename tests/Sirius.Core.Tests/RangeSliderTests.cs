using System.Windows;
using Sirius.Studio.App.Controls;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The two-handled slider.
/// </summary>
/// <remarks>
/// Laid out for real, because what is worth testing is the arithmetic
/// between a pixel and a value, and that depends on the control's measured
/// width. Mouse input is not simulated: without a window there is no device
/// to read a position from, so the handle-picking and the pixel mapping are
/// tested directly instead of through a synthetic click that would prove
/// nothing.
/// </remarks>
[Collection(WpfCollection.Name)]
public class RangeSliderTests
{
    public RangeSliderTests(WpfTestHost host) => _ = host;

    /// <summary>Handle radius 7 either side, so the track is 255 wide.</summary>
    private const double Width = 255 + 14;

    private static void Laid(Action<RangeSlider> test, Action<RangeSlider>? configure = null)
    {
        WpfTestHost.OnStaThread(() =>
        {
            var slider = new RangeSlider { Minimum = 0, Maximum = 255, Low = 0, High = 255 };
            configure?.Invoke(slider);

            slider.Measure(new Size(Width, 20));
            slider.Arrange(new Rect(0, 0, Width, 20));
            slider.UpdateLayout();

            test(slider);
        });
    }

    private static void WithBand(Action<RangeSlider> test) =>
        Laid(test, slider =>
        {
            slider.Low = 40;
            slider.High = 90;
        });

    [Fact]
    public void It_keeps_the_range_it_was_given() =>
        WithBand(slider =>
        {
            Assert.Equal(40, slider.Low);
            Assert.Equal(90, slider.High);
        });

    /// <summary>
    /// The handles must not cross. Swapping them mid-drag loses track of
    /// which is under the cursor and the range jitters as the mouse passes
    /// over, so the moving one stops instead.
    /// </summary>
    [Fact]
    public void The_low_handle_stops_at_the_high_one() =>
        WithBand(slider =>
        {
            slider.Low = 200;

            Assert.Equal(90, slider.High);
            Assert.True(slider.Low <= slider.High, $"low {slider.Low} passed high {slider.High}");
        });

    [Fact]
    public void The_high_handle_stops_at_the_low_one() =>
        WithBand(slider =>
        {
            slider.High = 10;

            Assert.True(slider.Low <= slider.High, $"high {slider.High} passed low {slider.Low}");
        });

    [Fact]
    public void Values_are_held_inside_the_bounds() =>
        Laid(slider =>
        {
            slider.Low = -40;
            slider.High = 400;

            Assert.Equal(0, slider.Low);
            Assert.Equal(255, slider.High);
        });

    // ---- pixels and values --------------------------------------------------

    [Fact]
    public void The_ends_of_the_range_sit_at_the_ends_of_the_track() =>
        Laid(slider =>
        {
            Assert.Equal(7, slider.PositionOf(0), 1);
            Assert.Equal(Width - 7, slider.PositionOf(255), 1);
        });

    [Fact]
    public void A_position_maps_back_to_the_value_that_drew_it() =>
        Laid(slider =>
        {
            foreach (var value in new double[] { 0, 1, 64, 128, 200, 255 })
                Assert.Equal(value, slider.ValueAt(slider.PositionOf(value)), 1);
        });

    /// <summary>
    /// Off either end the value saturates rather than running past the
    /// bounds - a drag does not stop at the edge of the control.
    /// </summary>
    [Fact]
    public void Dragging_past_the_end_saturates() =>
        Laid(slider =>
        {
            Assert.Equal(0, slider.ValueAt(-500), 1);
            Assert.Equal(255, slider.ValueAt(5000), 1);
        });

    /// <summary>
    /// A range collapsed onto a point must still be openable. With both
    /// handles in one place the low one takes the press, so it can be
    /// widened downwards as well as up.
    /// </summary>
    [Fact]
    public void A_collapsed_range_gives_the_press_to_the_low_handle() =>
        Laid(
            slider => Assert.True(
                slider.PrefersLowHandle(slider.PositionOf(128)),
                "a collapsed range could only be widened upwards"),
            slider =>
            {
                slider.Low = 128;
                slider.High = 128;
            });

    [Fact]
    public void A_press_takes_hold_of_the_nearer_handle() =>
        WithBand(slider =>
        {
            Assert.True(slider.PrefersLowHandle(slider.PositionOf(45)));
            Assert.False(slider.PrefersLowHandle(slider.PositionOf(85)));
        });

    /// <summary>
    /// A zero-width slider is what the first layout pass measures, and
    /// dividing by it would put every value at infinity.
    /// </summary>
    [Fact]
    public void An_unmeasured_slider_does_not_divide_by_its_width()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var slider = new RangeSlider { Minimum = 0, Maximum = 255 };

            Assert.Equal(0, slider.ValueAt(50));
            Assert.True(double.IsFinite(slider.PositionOf(128)));
        });
    }

    // ---- the logarithmic scale ---------------------------------------------

    private static void Logarithmic(Action<RangeSlider> test) =>
        Laid(test, slider =>
        {
            slider.Minimum = 0;
            slider.Maximum = 10_000;
            slider.IsLogarithmic = true;
        });

    /// <summary>
    /// The reason it exists. Blob area runs from a few pixels to a few
    /// million, and on a flat track every useful setting falls inside the
    /// first one percent of the travel.
    /// </summary>
    [Fact]
    public void A_flat_track_puts_small_areas_in_the_first_percent() =>
        Laid(
            slider => Assert.True(
                slider.FractionOf(100) < 0.02,
                "an area of 100 was not crushed against the start, so this test is wrong"),
            slider =>
            {
                slider.Minimum = 0;
                slider.Maximum = 10_000;
            });

    [Fact]
    public void A_log_track_gives_each_decade_equal_travel() =>
        Logarithmic(slider =>
        {
            var decades = new[] { 1.0, 10, 100, 1000, 10_000 }
                .Select(slider.FractionOf)
                .ToArray();

            for (var i = 1; i < decades.Length; i++)
                Assert.Equal(0.25, decades[i] - decades[i - 1], 2);
        });

    [Fact]
    public void A_log_position_maps_back_to_its_value() =>
        Logarithmic(slider =>
        {
            foreach (var value in new double[] { 1, 25, 400, 9999 })
                Assert.Equal(value, slider.ValueAt(slider.PositionOf(value)), 0);
        });

    /// <summary>
    /// A size filter starts at zero, and the logarithm of zero is not a
    /// number - which would place the handle nowhere and take the track
    /// with it.
    /// </summary>
    [Fact]
    public void A_log_track_tolerates_a_zero_bound() =>
        Logarithmic(slider =>
        {
            Assert.True(double.IsFinite(slider.PositionOf(0)));
            Assert.Equal(0, slider.FractionOf(0), 3);
        });
}
