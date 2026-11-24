using System.IO;

namespace Sirius.Studio.App.Diagnostics;

/// <summary>
/// An opt-in trace file, for faults that only appear in the running shell.
/// </summary>
/// <remarks>
/// <para>
/// Some chains cross the control, the view-model, the scheduler and the
/// docking layout, and break in a way that looks identical from the outside -
/// a panel that never appears, a chart that stays empty. Each layer can be
/// tested on its own and pass; what cannot be tested is which of them was
/// never asked. This records the chain so the break can be found rather than
/// guessed at.
/// </para>
/// <para>
/// Off unless <c>SIRIUS_PANEL_LOG=1</c>, because writing a file on every
/// launch is noise, and silent if anything goes wrong: diagnostics must never
/// break the feature they are observing.
/// </para>
/// </remarks>
internal static class Log
{
    private static readonly object Gate = new();

    public static bool IsEnabled { get; } =
        Environment.GetEnvironmentVariable("SIRIUS_PANEL_LOG") == "1";

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SiriusStudioPlus",
        "panels.log");

    /// <summary>Records a line. <paramref name="always"/> ignores the switch.</summary>
    public static void Write(string message, bool always = false)
    {
        if (!always && !IsEnabled)
            return;

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(
                    Path,
                    $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Nothing useful to do, and nothing worth failing over.
        }
    }

    /// <summary>
    /// A short, stable name for an object, so the log can show when two
    /// instances are in play where there should be one.
    /// </summary>
    public static string Id(object? instance) =>
        instance is null ? "<null>" : $"#{instance.GetHashCode() & 0xFFFF:X4}";
}
