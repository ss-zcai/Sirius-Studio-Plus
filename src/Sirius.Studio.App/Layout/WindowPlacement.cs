using System.IO;
using System.Text.Json;
using System.Windows;

namespace Sirius.Studio.App.Layout;

/// <summary>
/// Where the main window was last time, so it opens there again.
/// </summary>
/// <remarks>
/// <para>
/// Kept beside the docking layout rather than inside it: AvalonDock writes
/// where the panels are, not where the window is, and a window that opens
/// windowed every time is a thing to fix before any arrangement of panels
/// matters.
/// </para>
/// <para>
/// The size recorded is the restore size, not the size on screen. A
/// maximised window measures the whole monitor, so saving that would lose
/// the size to come back to when it is restored - and on a smaller screen
/// next time, would restore to something larger than the desktop.
/// </para>
/// </remarks>
public sealed record WindowPlacement(
    double Left, double Top, double Width, double Height, bool Maximised)
{
    /// <summary>
    /// A window narrower or shorter than this is treated as not worth
    /// restoring - it is the result of something having gone wrong rather
    /// than of anybody resizing a window that far down.
    /// </summary>
    private const double Smallest = 200;

    /// <summary>
    /// How much of the window has to fall on the desktop for the saved
    /// position to be used.
    /// </summary>
    /// <remarks>
    /// Monitors come and go - a laptop undocked from a second screen is the
    /// usual way - and a window restored onto one that is no longer there
    /// is a window that cannot be reached. Checked against the virtual
    /// screen, which spans every monitor, so a position on any of them
    /// still counts.
    /// </remarks>
    private const double MustBeVisible = 80;

    public static WindowPlacement Of(Window window)
    {
        // RestoreBounds is the windowed size even while maximised, which is
        // the one worth keeping. It is empty before the window is shown.
        var bounds = window.RestoreBounds;

        if (bounds.IsEmpty || double.IsNaN(bounds.Width))
            bounds = new Rect(window.Left, window.Top, window.Width, window.Height);

        return new WindowPlacement(
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            window.WindowState == WindowState.Maximized);
    }

    /// <summary>
    /// Whether this placement can still be used on the desktop described
    /// by <paramref name="desktop"/>.
    /// </summary>
    public bool FitsOn(Rect desktop)
    {
        if (Width < Smallest || Height < Smallest)
            return false;

        if (double.IsNaN(Left) || double.IsNaN(Top) ||
            double.IsInfinity(Left) || double.IsInfinity(Top))
        {
            return false;
        }

        var onScreen = Rect.Intersect(new Rect(Left, Top, Width, Height), desktop);

        return !onScreen.IsEmpty &&
               onScreen.Width >= MustBeVisible &&
               onScreen.Height >= MustBeVisible;
    }

    /// <summary>The desktop across every monitor.</summary>
    public static Rect Desktop => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

    /// <summary>
    /// Puts the window back, leaving it where the markup put it when the
    /// saved position no longer lands on a screen.
    /// </summary>
    public void ApplyTo(Window window, Rect desktop)
    {
        if (FitsOn(desktop))
        {
            // CenterScreen would overwrite these the moment the window is
            // shown, which is the whole reason the position is being set.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = Left;
            window.Top = Top;
            window.Width = Width;
            window.Height = Height;
        }

        // Maximised still applies when the position did not: the monitor it
        // was maximised on may be gone, but wanting it maximised is not.
        if (Maximised)
            window.WindowState = WindowState.Maximized;
    }

    public static WindowPlacement? Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path))
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Where the window was is a convenience; a damaged file means
            // opening where the markup says, not failing to open.
            return null;
        }
    }

    public void Write(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth interrupting shutdown over.
        }
    }
}
