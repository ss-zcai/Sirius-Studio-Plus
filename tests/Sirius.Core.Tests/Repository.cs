using System.IO;
using System.Runtime.CompilerServices;

namespace Sirius.Core.Tests;

/// <summary>
/// Where the source lives, for the tests that read markup rather than run it.
/// </summary>
/// <remarks>
/// <para>
/// Several tests assert against the shipped XAML - that the canvas really
/// binds to the accent, that every parameter editor has exactly one row -
/// because the alternative is building a window, and AvalonDock keeps static
/// state that makes a second one in a process unreliable. Reading the markup
/// is a weaker check than running it, but it is a check, and it costs
/// nothing.
/// </para>
/// <para>
/// Found from this file's own compiled-in path rather than by walking up
/// from the test assembly. Walking up works until the assembly is built
/// somewhere else - which happened the first time the usual output folder
/// was locked by a copy of the application someone had running, and nine
/// tests failed for a reason that had nothing to do with what they check.
/// </para>
/// </remarks>
internal static class Repository
{
    /// <summary>The repository root.</summary>
    public static string Root { get; } = Locate();

    /// <summary>The application project's source folder.</summary>
    public static string AppSource { get; } = Path.Combine(Root, "src", "Sirius.Studio.App");

    /// <summary>A path below the repository root.</summary>
    public static string Path2(params string[] parts) =>
        Path.Combine(new[] { Root }.Concat(parts).ToArray());

    private static string Locate([CallerFilePath] string thisFile = "")
    {
        // tests/Sirius.Core.Tests/Repository.cs -> the root two levels up.
        var directory = Path.GetDirectoryName(thisFile);

        for (var depth = 0; depth < 10 && directory is not null; depth++)
        {
            if (Directory.Exists(Path.Combine(directory, "src", "Sirius.Studio.App")))
                return directory;

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            $"No repository root above {thisFile}. This file moved, or the source is not present.");
    }
}
