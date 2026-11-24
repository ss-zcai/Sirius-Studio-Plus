using System.IO;
using Sirius.UI.Diagnostics;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The file a program writes what it caught to - asked for when Builder
/// closed and the only record was Windows' event log.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class CrashLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-crash-" + Guid.NewGuid().ToString("N"));
    private readonly string _was = CrashLog.Folder;

    public CrashLogTests(WpfTestHost host)
    {
        _ = host;
        CrashLog.Folder = _folder;
    }

    public void Dispose()
    {
        CrashLog.Folder = _was;

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a run over.
        }
    }

    private static Exception Thrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    [Fact]
    public void A_fault_is_written_whole_and_the_same_again_is_counted_not_repeated()
    {
        var path = CrashLog.PathFor("Sirius Builder");
        Assert.Equal("sirius-builder-crash.log", Path.GetFileName(path));

        var fault = Thrown("An ItemsControl is inconsistent with its items source.");

        CrashLog.Write("Sirius Builder", fault);
        CrashLog.Write("Sirius Builder", fault);
        CrashLog.Write("Sirius Builder", fault);
        CrashLog.Write("Sirius Builder", Thrown("Something else."), "Fatal");

        var text = File.ReadAllText(path);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "inconsistent with its items source"));
        Assert.Contains("(the fault before came 2 more times)", text);
        Assert.Contains("Reported  Sirius Builder", text);
        Assert.Contains("Fatal  Sirius Builder", text);
        Assert.Contains(nameof(Thrown), text);
    }

    [Fact]
    public void A_full_log_is_moved_aside_rather_than_growing_for_ever()
    {
        var path = CrashLog.PathFor("Sirius Edge");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(path, new string('x', 1024 * 1024 + 1));

        CrashLog.Write("Sirius Edge", Thrown("After the old ones."));

        Assert.True(File.Exists(path + ".old"));
        Assert.Contains("After the old ones.", File.ReadAllText(path));
        Assert.True(new FileInfo(path).Length < 10_000);
    }
}
