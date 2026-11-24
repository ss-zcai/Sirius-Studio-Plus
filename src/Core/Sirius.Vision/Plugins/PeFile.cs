using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

namespace Sirius.Vision.Plugins;

/// <summary>
/// What a DLL says about itself in its headers, read without loading it:
/// whether it is 64-bit, what it exports and which DLLs it needs.
/// </summary>
/// <remarks>
/// Loading a DLL to ask it runs its start-up code, and Windows reports a
/// DLL that will not load with one sentence - "the specified module could
/// not be found" - that does not name the module. Reading the file says
/// which, before and after.
/// </remarks>
internal static class PeFile
{
    /// <summary>True for a native DLL - not a .NET one - that exports <paramref name="name"/>.</summary>
    public static bool Exports(string path, string name)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);

            if (reader.HasMetadata || reader.PEHeaders.PEHeader is not { } header)
                return false;

            var directory = header.ExportTableDirectory;
            if (directory.Size < 40 || directory.RelativeVirtualAddress == 0)
                return false;

            var table = reader.GetSectionData(directory.RelativeVirtualAddress).GetReader();
            table.Offset = 24;
            var count = table.ReadInt32();
            table.Offset = 32;
            var names = table.ReadInt32();

            if (count <= 0 || names == 0)
                return false;

            var addresses = reader.GetSectionData(names).GetReader();

            for (var i = 0; i < count; i++)
            {
                if (Text(reader, addresses.ReadInt32()) == name)
                    return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The processor a DLL was built for, or null when the file is not one.
    /// </summary>
    public static Machine? MachineOf(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.PEHeaders.CoffHeader.Machine;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The version of the linker that made a DLL - for Visual C++, the
    /// version of its tools: 14.43 - or null when the file cannot be read.
    /// </summary>
    public static Version? LinkerVersion(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);

            return reader.PEHeaders.PEHeader is { } header
                ? new Version(header.MajorLinkerVersion, header.MinorLinkerVersion)
                : null;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Whether a DLL of this name is already loaded in this process.</summary>
    public static bool IsLoaded(string name) => GetModuleHandle(name) != IntPtr.Zero;

    /// <summary>The DLLs a file needs before it can load, by name.</summary>
    public static IReadOnlyList<string> Imports(string path)
    {
        var imports = new List<string>();

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);

            if (reader.PEHeaders.PEHeader is not { } header)
                return imports;

            var directory = header.ImportTableDirectory;
            if (directory.Size < 20 || directory.RelativeVirtualAddress == 0)
                return imports;

            var table = reader.GetSectionData(directory.RelativeVirtualAddress).GetReader();

            // One 20-byte entry for each DLL, ended by one of zeros.
            while (table.RemainingBytes >= 20)
            {
                table.Offset += 12;
                var name = table.ReadInt32();
                table.Offset += 4;

                if (name == 0)
                    break;

                if (Text(reader, name) is { Length: > 0 } dll)
                    imports.Add(dll);
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // What could be read is still worth having.
        }

        return imports;
    }

    /// <summary>
    /// The DLLs <paramref name="path"/> needs that cannot be found, each as
    /// "name (needed by other.dll)" - looking where Windows looks, and
    /// through the DLLs that are found for what they need in turn.
    /// </summary>
    public static IReadOnlyList<string> MissingDependencies(string path)
    {
        var folders = SearchFolders(path);
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();
        pending.Enqueue(path);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();

            foreach (var name in Imports(current))
            {
                if (!seen.Add(name) || IsProvidedByWindows(name))
                    continue;

                if (GetModuleHandle(name) != IntPtr.Zero)
                    continue;

                var found = folders.Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);

                if (found is null)
                {
                    missing.Add(string.Equals(current, path, StringComparison.OrdinalIgnoreCase)
                        ? name
                        : $"{name} (needed by {Path.GetFileName(current)})");
                }
                else if (!IsInWindows(found))
                {
                    pending.Enqueue(found);
                }
            }
        }

        return missing;
    }

    /// <summary>Where Windows looks for what a DLL loaded by its full path needs.</summary>
    private static List<string> SearchFolders(string path)
    {
        var folders = new List<string>
        {
            Path.GetDirectoryName(Path.GetFullPath(path))!,
            Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        };

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                folders.Add(Environment.ExpandEnvironmentVariables(entry.Trim('"')));
            }
            catch (ArgumentException)
            {
                // Not a folder Windows could look in either.
            }
        }

        return folders;
    }

    /// <summary>
    /// API sets - "api-ms-win-crt-runtime-l1-1-0.dll" - are names Windows
    /// maps to its own DLLs; there is no file to find.
    /// </summary>
    private static bool IsProvidedByWindows(string name) =>
        name.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase);

    private static bool IsInWindows(string path) =>
        path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);

    private static string Text(PEReader reader, int address)
    {
        if (address == 0)
            return "";

        var block = reader.GetSectionData(address).GetReader();
        var bytes = new List<byte>(64);

        while (block.RemainingBytes > 0 && block.ReadByte() is var value && value != 0)
            bytes.Add(value);

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern IntPtr GetModuleHandle(string name);
}
