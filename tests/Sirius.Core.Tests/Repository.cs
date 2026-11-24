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

    /// <summary>Studio Plus's own source folder: its window and start-up.</summary>
    public static string AppSource { get; } = Project("Sirius.Studio");

    /// <summary>The shared UI's source folder: the theme, controls, view-models, screens.</summary>
    public static string UiSource { get; } = Project("Sirius.UI");

    /// <summary>
    /// A project's folder by its name, wherever under src/ it is - Core,
    /// Drivers, Apps - so a project moved does not move every test with it.
    /// </summary>
    public static string Project(string name) =>
        Directory.GetDirectories(Path.Combine(Root, "src"), name, SearchOption.AllDirectories).FirstOrDefault()
        ?? throw new DirectoryNotFoundException($"No project folder '{name}' under {Path.Combine(Root, "src")}.");

    /// <summary>A path below the repository root.</summary>
    public static string Path2(params string[] parts) =>
        Path.Combine(new[] { Root }.Concat(parts).ToArray());

    private static string Locate([CallerFilePath] string thisFile = "")
    {
        // tests/Sirius.Core.Tests/Repository.cs -> the root two levels up.
        var directory = Path.GetDirectoryName(thisFile);

        for (var depth = 0; depth < 10 && directory is not null; depth++)
        {
            if (Directory.Exists(Path.Combine(directory, "src")) && Directory.Exists(Path.Combine(directory, "tests")))
                return directory;

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            $"No repository root above {thisFile}. This file moved, or the source is not present.");
    }
}
