using System.Globalization;

namespace Sirius.Studio.App.ViewModels;

/// <summary>
/// When a continuous run puts a frame on the screen, and how fast it is
/// inspecting and showing.
/// </summary>
/// <remarks>
/// <para>
/// Every frame is inspected; not every frame can be shown. Showing one -
/// the picture converted for the screen, its shapes rebuilt, the panels
/// laid out - has to happen between two inspections, while the frame's
/// buffer still exists, so the run waits for it. Showing all four hundred
/// frames a second a simulated camera gives would spend the run drawing.
/// </para>
/// <para>
/// So the latest frame is shown at about the rate a screen can show
/// anything, and less often when showing one is slow: never more than
/// <see cref="Share"/> of the run goes on the display. It was once a fixed
/// five times a second, which looked like a stalled camera and, on a line
/// faster than that, never showed some parts at all.
/// </para>
/// <para>
/// Times are seconds from any fixed start, passed in rather than read
/// here, so the pacing can be checked without a clock.
/// </para>
/// </remarks>
internal sealed class LiveDisplay
{
    /// <summary>The fastest the screen is refreshed: about the rate a monitor shows frames.</summary>
    public const double Fastest = 1.0 / 30;

    /// <summary>The most of the run's time that showing frames may take.</summary>
    public const double Share = 0.25;

    /// <summary>How often the rates are written out - often enough to follow, slowly enough to read.</summary>
    public const double ReportEvery = 0.5;

    /// <summary>How much each new refresh moves the running cost - enough to follow a change, not one hiccup.</summary>
    private const double Smoothing = 0.3;

    private double _cost = double.NaN;
    private double _lastShown = double.NegativeInfinity;
    private double _windowStart;
    private int _inspected;
    private int _shown;

    public LiveDisplay(double now) => _windowStart = now;

    /// <summary>The time between refreshes, as the cost of the last few allows.</summary>
    public double Interval =>
        double.IsNaN(_cost) ? Fastest : Math.Max(Fastest, _cost / Share);

    /// <summary>Whether a frame inspected at <paramref name="now"/> should be shown.</summary>
    public bool IsDue(double now) => now - _lastShown >= Interval;

    /// <summary>Counts a frame inspected.</summary>
    public void Inspected() => _inspected++;

    /// <summary>Counts a frame shown, and what showing it cost.</summary>
    public void Shown(double started, double finished)
    {
        var cost = Math.Max(0, finished - started);

        _cost = double.IsNaN(_cost) ? cost : _cost + (Smoothing * (cost - _cost));
        _lastShown = started;
        _shown++;
    }

    /// <summary>
    /// The rates since the last report - "412 frames/s inspected, 30
    /// shown" - once <see cref="ReportEvery"/> has passed, otherwise null.
    /// </summary>
    public string? Report(double now)
    {
        var elapsed = now - _windowStart;

        if (elapsed < ReportEvery)
            return null;

        var text = string.Format(
            CultureInfo.CurrentCulture,
            "{0} frames/s inspected, {1} shown",
            Rate(_inspected / elapsed),
            Rate(_shown / elapsed));

        _windowStart = now;
        _inspected = 0;
        _shown = 0;

        return text;
    }

    /// <summary>A whole number once it is large enough that a decimal is noise.</summary>
    private static string Rate(double perSecond) =>
        perSecond.ToString(perSecond >= 10 ? "0" : "0.0", CultureInfo.CurrentCulture);
}
