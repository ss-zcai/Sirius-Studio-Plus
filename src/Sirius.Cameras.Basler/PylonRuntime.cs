using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Sirius.Cameras.Basler;

/// <summary>
/// Whether the pylon installed on this PC is one the shipped pylon .NET API
/// can use - and if not, what to tell the person looking for their camera.
/// </summary>
/// <remarks>
/// <para>
/// The application ships Basler.Pylon.dll, which is all managed code: every
/// call it makes goes to a native helper, Basler.Pylon.ExternC.dll, that
/// the pylon runtime installs and puts on the PATH. That helper is built
/// against one pylon release's native libraries (pylon 26.10's is linked to
/// PylonBase_v12.dll). A PC with an older pylon - 7.3, say - has no helper,
/// or an older one, and the first call into pylon fails with "Unable to
/// load DLL 'Basler.Pylon.ExternC.dll'". Caught where the drivers are
/// loaded, that used to leave the Basler driver out with nothing on screen
/// to say why: the camera list was simply empty, while pylon Viewer, using
/// its own pylon, found the camera fine.
/// </para>
/// <para>
/// No pylon type is touched here, so the check runs before Basler.Pylon's
/// own start-up can fail.
/// </para>
/// </remarks>
internal static class PylonRuntime
{
    /// <summary>The native helper the pylon .NET API calls into.</summary>
    public const string Helper = "Basler.Pylon.ExternC.dll";

    /// <summary>
    /// The pylon release this build was made with, as Basler names it
    /// ("26.10"), from the project file.
    /// </summary>
    public static string Release { get; } =
        typeof(PylonRuntime).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "PylonRelease")?.Value ?? "the version this build was made with";

    private static readonly Lazy<string?> Checked = new(() => Check(
        AppContext.BaseDirectory,
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
        InstallationFolder(),
        ShippedApiVersion(),
        Release,
        FileVersion,
        path => NativeLibrary.TryLoad(path, out _)));


    /// <summary>Why pylon cannot be used here, or null when it can.</summary>
    public static string? Problem => Checked.Value;

    /// <summary>What to do about it, said the same way everywhere.</summary>
    public static string Advice => AdviceFor(Release);

    private static string AdviceFor(string release) =>
        $"Install pylon {release} or later from baslerweb.com - the pylon Runtime is enough - and restart Sirius Studio Plus.";

    /// <summary>Throws with <see cref="Problem"/> when pylon cannot be used.</summary>
    public static void ThrowIfUnusable()
    {
        if (Problem is { } problem)
            throw new InvalidOperationException(problem);
    }

    /// <summary>
    /// The check itself, with everything it reads passed in, so a test can
    /// set up any PC.
    /// </summary>
    /// <param name="appFolder">Searched first, as .NET searches it first.</param>
    /// <param name="path">The PATH's folders, searched in order after it.</param>
    /// <param name="installFolder">Where pylon says it is installed, or null.</param>
    /// <param name="shipped">The pylon .NET API this build ships, which the helper must be at least.</param>
    /// <param name="release">The pylon release that API came with, for the message.</param>
    /// <param name="versionOf">A DLL's file version, or null.</param>
    /// <param name="loads">Whether a DLL loads, with everything it needs.</param>
    internal static string? Check(
        string appFolder,
        IEnumerable<string> path,
        string? installFolder,
        Version? shipped,
        string release,
        Func<string, Version?> versionOf,
        Func<string, bool> loads)
    {
        var advice = AdviceFor(release);

        var helper = new[] { appFolder }.Concat(path)
            .Select(folder => SafeCombine(folder, Helper))
            .FirstOrDefault(candidate => candidate is not null && File.Exists(candidate));

        if (helper is null)
        {
            var runtime = installFolder is null ? null : Path.Combine(installFolder, "Runtime", "x64");

            if (runtime is not null && File.Exists(Path.Combine(runtime, Helper)))
            {
                return $"pylon is installed in {installFolder}, but its Runtime\\x64 " +
                       "folder is not on the PATH. Restart the PC after installing pylon, or install it again.";
            }

            if (runtime is not null && Directory.Exists(runtime))
            {
                var found = Directory.EnumerateFiles(runtime, "PylonBase_v*.dll").Select(Path.GetFileName).FirstOrDefault();

                return "The pylon on this PC" +
                       (found is null ? string.Empty : $" ({found})") +
                       $" is older than this build needs - it has no {Helper}. " + advice;
            }

            return "pylon is not installed on this PC. " + advice;
        }

        var version = versionOf(helper);

        if (shipped is not null && version is not null && (version.Major, version.Minor).CompareTo((shipped.Major, shipped.Minor)) < 0)
        {
            return $"The pylon on this PC is older than this build needs - its .NET " +
                   $"helper is {version.ToString(2)}, and this build was made with {shipped.ToString(2)} (pylon {release}). " + advice;
        }

        if (!loads(helper))
        {
            return $"pylon's {Helper} in {Path.GetDirectoryName(helper)} would not " +
                   "load - pylon's runtime is incomplete, or from a different release. " + advice;
        }

        return null;
    }

    private static string? SafeCombine(string folder, string file)
    {
        try
        {
            return Path.Combine(folder.Trim().Trim('"'), file);
        }
        catch (ArgumentException)
        {
            // A PATH entry with characters no path can hold.
            return null;
        }
    }

    private static string? InstallationFolder()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Basler\pylon");
            return key?.GetValue("InstallationFolder") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>The version of the Basler.Pylon.dll beside this driver, read without loading it.</summary>
    private static Version? ShippedApiVersion()
    {
        var beside = Path.Combine(Path.GetDirectoryName(typeof(PylonRuntime).Assembly.Location) ?? AppContext.BaseDirectory, "Basler.Pylon.dll");

        try
        {
            return File.Exists(beside) ? AssemblyName.GetAssemblyName(beside).Version : null;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException)
        {
            return null;
        }
    }

    private static Version? FileVersion(string file)
    {
        var info = FileVersionInfo.GetVersionInfo(file);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }
}
