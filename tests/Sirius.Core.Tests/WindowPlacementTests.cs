using System.IO;
using System.Windows;
using Sirius.Studio.App.Layout;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Opening the window where it was left.
/// </summary>
/// <remarks>
/// Mostly about the cases where the saved position should be thrown away.
/// Restoring one is easy; restoring one onto a monitor that is no longer
/// attached produces a window that cannot be reached, dragged or closed,
/// and the only way out is to delete the file it came from.
/// </remarks>
[Collection(WpfCollection.Name)]
public class WindowPlacementTests
{
    public WindowPlacementTests(WpfTestHost host) => _ = host;

    /// <summary>A single 1920x1080 monitor with its origin at zero.</summary>
    private static readonly Rect OneScreen = new(0, 0, 1920, 1080);

    /// <summary>That monitor with a second one to the left of it.</summary>
    private static readonly Rect TwoScreens = new(-1920, 0, 3840, 1080);

    // ---- what is restored ------------------------------------------------

    [Fact]
    public void A_window_on_the_desktop_is_restored() =>
        Assert.True(new WindowPlacement(100, 100, 1280, 800, false).FitsOn(OneScreen));

    [Fact]
    public void A_window_on_a_second_monitor_is_restored() =>
        Assert.True(new WindowPlacement(-1500, 50, 1280, 800, false).FitsOn(TwoScreens));

    /// <summary>
    /// Hanging off an edge is normal - people park windows like that - so
    /// long as enough is left to grab.
    /// </summary>
    [Fact]
    public void A_window_half_off_the_edge_is_restored() =>
        Assert.True(new WindowPlacement(1300, 100, 1280, 800, false).FitsOn(OneScreen));

    // ---- what is thrown away ---------------------------------------------

    /// <summary>
    /// The second monitor is gone - undocking a laptop is the usual way -
    /// and the window would open where nobody can reach it.
    /// </summary>
    [Fact]
    public void A_window_on_a_monitor_that_is_gone_is_not_restored() =>
        Assert.False(new WindowPlacement(-1500, 50, 1280, 800, false).FitsOn(OneScreen));

    [Theory]
    [InlineData(1900, 100)]
    [InlineData(100, 1060)]
    [InlineData(-1270, 100)]
    public void A_window_with_only_a_sliver_showing_is_not_restored(double left, double top) =>
        Assert.False(new WindowPlacement(left, top, 1280, 800, false).FitsOn(OneScreen));

    [Theory]
    [InlineData(0, 800)]
    [InlineData(1280, 0)]
    [InlineData(10, 10)]
    public void A_window_too_small_to_use_is_not_restored(double width, double height) =>
        Assert.False(new WindowPlacement(100, 100, width, height, false).FitsOn(OneScreen));

    [Fact]
    public void A_position_that_is_not_a_number_is_not_restored() =>
        Assert.False(new WindowPlacement(double.NaN, 100, 1280, 800, false).FitsOn(OneScreen));

    // ---- the file --------------------------------------------------------

    [Fact]
    public void It_round_trips_through_its_file()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "window.json");
        var placement = new WindowPlacement(120, 60, 1600, 900, Maximised: true);

        try
        {
            placement.Write(path);

            Assert.Equal(placement, WindowPlacement.Read(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void A_missing_file_simply_has_nothing_to_say() =>
        Assert.Null(WindowPlacement.Read(
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "window.json")));

    /// <summary>
    /// A half-written or hand-edited file opens the window where the
    /// markup says rather than failing to open it at all.
    /// </summary>
    [Fact]
    public void A_damaged_file_is_ignored()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        var file = Path.Combine(path, "window.json");

        try
        {
            File.WriteAllText(file, "{ not json");

            Assert.Null(WindowPlacement.Read(file));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    // ---- applying it -----------------------------------------------------

    /// <summary>
    /// Being maximised survives even when the position does not. The
    /// monitor it was maximised on may be gone; wanting it maximised is
    /// not a property of that monitor.
    /// </summary>
    [Fact]
    public void Maximised_is_restored_even_when_the_position_is_not() =>
        WpfTestHost.OnStaThread(() =>
        {
            var window = new Window { Left = 10, Top = 10, Width = 800, Height = 600 };

            new WindowPlacement(-5000, 0, 1280, 800, Maximised: true)
                .ApplyTo(window, OneScreen);

            Assert.Equal(WindowState.Maximized, window.WindowState);

            // Untouched, so the window opens where the markup put it.
            Assert.Equal(10, window.Left);
            Assert.Equal(800, window.Width);
        });

    [Fact]
    public void A_usable_placement_moves_and_sizes_the_window() =>
        WpfTestHost.OnStaThread(() =>
        {
            var window = new Window { Left = 10, Top = 10, Width = 800, Height = 600 };

            new WindowPlacement(120, 60, 1600, 900, Maximised: false)
                .ApplyTo(window, OneScreen);

            Assert.Equal(WindowStartupLocation.Manual, window.WindowStartupLocation);
            Assert.Equal(120, window.Left);
            Assert.Equal(60, window.Top);
            Assert.Equal(1600, window.Width);
            Assert.Equal(900, window.Height);
            Assert.Equal(WindowState.Normal, window.WindowState);
        });
}
