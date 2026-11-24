using System.IO;

namespace Sirius.UI.Diagnostics;

/// <summary>
/// Every fault a program catches at the top, and the one that ends it,
/// written to a file - so there is something to read after the fact.
/// </summary>
/// <remarks>
/// <para>
/// Asked for on 20 November 2026: Builder closed during a Test Run, and the
/// only record was Windows' event log, which cuts the exception short. The
/// file has it whole, with its stack.
/// </para>
/// <para>
/// <c>%APPDATA%\SiriusStudioPlus\logs\&lt;program&gt;-crash.log</c>, one per
/// program. Past a megabyte it is moved aside as <c>.old</c>, so it never
/// grows without end. The same fault again within a few seconds - a layout
/// pass throwing on every pass - is counted, not written again. Silent if
/// anything goes wrong: a crash log must never cause one.
/// </para>
/// </remarks>
public static class CrashLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly TimeSpan Repeat = TimeSpan.FromSeconds(5);
    private static readonly object Gate = new();

    private static string? _last;
    private static DateTime _lastAt;
    private static int _repeats;

    /// <summary>The folder the logs are in.</summary>
    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SiriusStudioPlus",
        "logs");

    /// <summary>The log of <paramref name="program"/>: "Sirius Builder" is sirius-builder-crash.log.</summary>
    public static string PathFor(string program) =>
        Path.Combine(Folder, $"{FileName(program)}-crash.log");

    /// <summary>
    /// Writes every fault this process does not catch - on any thread, and
    /// tasks whose faults nobody looked at - to <paramref name="program"/>'s log.
    /// </summary>
    public static void Watch(string program)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Write(program, ex, e.IsTerminating ? "Fatal" : "Unhandled");
        };

        TaskScheduler.UnobservedTaskException += (_, e) => Write(program, e.Exception, "Unobserved task");
    }

    /// <summary>Writes a fault: what it was, where, and when.</summary>
    /// <param name="how">"Reported", "Fatal" - how it was met.</param>
    public static void Write(string program, Exception exception, string how = "Reported")
    {
        try
        {
            var text = exception.ToString();
            var now = DateTime.Now;

            lock (Gate)
            {
                if (text == _last && now - _lastAt < Repeat)
                {
                    _repeats++;
                    _lastAt = now;
                    return;
                }

                var path = PathFor(program);
                Directory.CreateDirectory(Folder);

                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".old", overwrite: true);

                var entry = (_repeats > 0 ? $"(the fault before came {_repeats} more times){Environment.NewLine}" : string.Empty) +
                            $"{now:yyyy-MM-dd HH:mm:ss.fff}  {how}  {program} {typeof(CrashLog).Assembly.GetName().Version}" +
                            $"{Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}";

                File.AppendAllText(path, entry);

                _last = text;
                _lastAt = now;
                _repeats = 0;
            }
        }
        catch (Exception)
        {
            // Nothing useful to do, and nothing worth failing over.
        }
    }

    private static string FileName(string program) =>
        new string(program.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
}
