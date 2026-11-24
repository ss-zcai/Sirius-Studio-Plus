using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Sirius.Studio.App;

/// <summary>
/// Asks Windows to draw a window's title bar dark.
/// </summary>
/// <remarks>
/// <para>
/// The caption is non-client area, owned by the operating system, so no
/// WPF styling reaches it. Without this a window is dark everywhere except
/// the one strip along the top, which reads as an unfinished application
/// rather than a deliberate choice.
/// </para>
/// <para>
/// Shared rather than living on the main window, because it was there and
/// every other window the shell opens went without it - the help, the
/// gallery, and anything added later would have done the same.
/// </para>
/// </remarks>
public static class DarkTitleBar
{
    /// <summary>Attribute id from Windows 10 20H1 onwards.</summary>
    private const int UseImmersiveDarkMode = 20;

    /// <summary>The id the same attribute had on earlier Windows 10 builds.</summary>
    private const int UseImmersiveDarkModeLegacy = 19;

    /// <summary>
    /// Applies it as soon as the window has a handle, whether or not it has
    /// one yet.
    /// </summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!TrySet(window))
            window.SourceInitialized += (_, _) => TrySet(window);
    }

    private static bool TrySet(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;

            if (handle == IntPtr.Zero)
                return false;

            var enabled = 1;

            // Both ids are tried because the value changed in Windows 10
            // 20H1, and an older build silently ignores the newer one.
            if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, UseImmersiveDarkModeLegacy, ref enabled, sizeof(int));

            return true;
        }
        catch (Exception)
        {
            // Cosmetic only: a light caption is not worth failing over.
            return true;
        }
    }

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr handle, int attribute, ref int value, int size);
}
