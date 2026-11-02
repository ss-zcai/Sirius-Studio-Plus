using Sirius.Studio.App.ViewModels;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// How often a continuous run puts a frame on the screen.
/// </summary>
public class LiveDisplayTests
{
    /// <summary>
    /// Runs <paramref name="seconds"/> of frames arriving every
    /// <paramref name="frameTime"/>, each refresh taking
    /// <paramref name="refreshCost"/>, and hands back what it reported.
    /// </summary>
    private static (int Inspected, int Shown, string? Report) Run(double frameTime, double refreshCost, double seconds)
    {
        var display = new LiveDisplay(0);
        var (now, inspected, shown) = (0.0, 0, 0);
        string? report = null;

        while (now < seconds)
        {
            now += frameTime;
            display.Inspected();
            inspected++;

            if (display.IsDue(now))
            {
                display.Shown(now, now + refreshCost);
                now += refreshCost;
                shown++;
            }

            report = display.Report(now) ?? report;
        }

        return (inspected, shown, report);
    }

    [Fact]
    public void A_fast_camera_is_shown_at_about_screen_rate()
    {
        var (inspected, shown, _) = Run(frameTime: 1.0 / 400, refreshCost: 0.002, seconds: 10);

        Assert.InRange(shown / 10.0, 27, 31);
        Assert.InRange(inspected / 10.0, 350, 400);
    }

    /// <summary>
    /// Slower than the screen, every frame is shown - none skipped, so no
    /// part goes by unseen. It was once five a second whatever came.
    /// </summary>
    [Fact]
    public void A_slow_camera_has_every_frame_shown()
    {
        var (inspected, shown, _) = Run(frameTime: 1.0 / 10, refreshCost: 0.002, seconds: 10);

        Assert.Equal(inspected, shown);
    }

    /// <summary>
    /// When a refresh is slow - a large picture, thousands of blobs - it is
    /// made less often, so drawing never takes more than its share of the
    /// run.
    /// </summary>
    [Fact]
    public void An_expensive_refresh_is_made_less_often()
    {
        var (_, shown, _) = Run(frameTime: 1.0 / 400, refreshCost: 0.020, seconds: 10);

        // 20 ms a refresh at a quarter of the time is one every 80 ms.
        Assert.InRange(shown / 10.0, 11, 13.5);
    }

    [Fact]
    public void The_report_says_both_rates()
    {
        var (_, _, report) = Run(frameTime: 1.0 / 400, refreshCost: 0.002, seconds: 3);

        Assert.NotNull(report);
        Assert.Matches(@"^\d+ frames/s inspected, \d+ shown$", report);
    }

    [Fact]
    public void Rates_are_reported_twice_a_second_not_every_frame()
    {
        var display = new LiveDisplay(0);

        Assert.Null(display.Report(0.1));
        Assert.Null(display.Report(0.49));
        Assert.NotNull(display.Report(0.5));
        Assert.Null(display.Report(0.6));
    }
}
