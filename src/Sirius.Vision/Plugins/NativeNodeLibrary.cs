using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sirius.Core.Nodes;

namespace Sirius.Vision.Plugins;

/// <summary>
/// Something a C++ library could not do, already worded for the operator.
/// </summary>
public sealed class NativeNodeException : Exception
{
    public NativeNodeException(string message)
        : base(message)
    {
    }
}

// ---- the interface, as sirius_node.h lays it out ---------------------------------

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SiriusImage
{
    public int Width;
    public int Height;
    public int Channels;
    public int Pixels;
    public int Layout;
    public int Bits;
    public long Stride;
    public void* Data;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SiriusMetric
{
    public byte* Name;
    public double Value;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SiriusShape
{
    public int Kind;
    public int Closed;
    public fixed double V[5];
    public double* Points;
    public int PointCount;
    public int MetricCount;
    public SiriusMetric* Metrics;
    public byte* Label;
    public uint Colour;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SiriusHost
{
    public int Size;
    public int Reserved;
    public void* Context;

    public delegate* unmanaged[Cdecl]<void*, byte*, int> Has;
    public delegate* unmanaged[Cdecl]<void*, byte*, double*, int> GetNumber;
    public delegate* unmanaged[Cdecl]<void*, byte*, long*, int> GetInteger;
    public delegate* unmanaged[Cdecl]<void*, byte*, int*, int> GetBool;
    public delegate* unmanaged[Cdecl]<void*, byte*, byte**, int> GetText;
    public delegate* unmanaged[Cdecl]<void*, byte*, SiriusImage*, int> GetImage;
    public delegate* unmanaged[Cdecl]<void*, byte*, int*, int> GetShapeCount;
    public delegate* unmanaged[Cdecl]<void*, byte*, int, SiriusShape*, int> GetShape;

    public delegate* unmanaged[Cdecl]<void*, byte*, double, int> SetNumber;
    public delegate* unmanaged[Cdecl]<void*, byte*, long, int> SetInteger;
    public delegate* unmanaged[Cdecl]<void*, byte*, int, int> SetBool;
    public delegate* unmanaged[Cdecl]<void*, byte*, byte*, int> SetText;
    public delegate* unmanaged[Cdecl]<void*, byte*, SiriusImage*, int> SetImage;
    public delegate* unmanaged[Cdecl]<void*, byte*, SiriusShape*, int> AddShape;

    public delegate* unmanaged[Cdecl]<void*, byte*, void> Warn;
    public delegate* unmanaged[Cdecl]<void*, int> Cancelled;
    public delegate* unmanaged[Cdecl]<void*, byte*> LastError;
    public delegate* unmanaged[Cdecl]<void*, byte*> LibraryFolder;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SiriusLibrary
{
    public int ApiVersion;
    public int Size;

    public delegate* unmanaged[Cdecl]<byte**, byte*, int, int> Describe;
    public delegate* unmanaged[Cdecl]<byte*, void**, byte*, int, int> Create;
    public delegate* unmanaged[Cdecl]<void*, void> Destroy;
    public delegate* unmanaged[Cdecl]<void*, SiriusHost*, byte*, int, int> Load;
    public delegate* unmanaged[Cdecl]<void*, SiriusHost*, byte*, int, int> Run;
    public delegate* unmanaged[Cdecl]<void*, void*, long, int> Copy;
}

/// <summary>
/// One C++ node library, loaded into this process.
/// </summary>
/// <remarks>
/// <para>
/// It is never unloaded: its nodes may hold threads, GPU memory and
/// whatever its own libraries hold, and there is no asking all of them to
/// let go.
/// </para>
/// <para>
/// Every call into it is made holding <see cref="Gate"/>, so the library is
/// never in two calls at once - two stations running the same node do not
/// have to trust that somebody's model is safe to share.
/// </para>
/// <para>
/// After a hardware fault inside it, it is stopped: nothing calls into it
/// again this session. The fault was caught, but what it did to the
/// library's memory first is not known, and a wrong answer from an
/// inspection is worse than no answer.
/// </para>
/// </remarks>
internal sealed unsafe partial class NativeNodeLibrary
{
    /// <summary>The interface version this application speaks: SIRIUS_API_VERSION.</summary>
    public const int ApiVersion = 1;

    public const string EntryPoint = "sirius_node_library";

    private const int ErrorSize = 2048;
    private const int Ok = 0;
    private const int Crashed = 2;

    private readonly SiriusLibrary* _table;
    private readonly ConcurrentQueue<IntPtr> _released = new();

    private NativeNodeLibrary(string path, SiriusLibrary* table)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        _table = table;
        Folder = (byte*)Marshal.StringToCoTaskMemUTF8(System.IO.Path.GetDirectoryName(path));
    }

    public string Path { get; }

    /// <summary>The file's name, for messages.</summary>
    public string Name { get; }

    /// <summary>The library's folder as the library is told it; lives as long as the library.</summary>
    public byte* Folder { get; }

    /// <summary>Held for every call into the library.</summary>
    public object Gate { get; } = new();

    /// <summary>Why the library is no longer called, once it has faulted.</summary>
    public string? Stopped { get; private set; }

    /// <summary>Whether a block's bytes can be copied without trusting its pointer.</summary>
    public bool HasGuardedCopy => _table->Size >= sizeof(SiriusLibrary) && _table->Copy != null;

    // ---- loading ----------------------------------------------------------------

    /// <summary>
    /// Loads a library's DLL and finds its entry points, or says why not.
    /// </summary>
    public static NativeNodeLibrary? Open(string path, out string? problem)
    {
        problem = null;

        switch (PeFile.MachineOf(path))
        {
            case null:
                problem = "This file is not a DLL.";
                return null;

            case Machine.Amd64:
                break;

            case var other:
                problem = $"This DLL was built for {MachineName(other.Value)}; Sirius Studio Plus is 64-bit (x64). " +
                          "Build the library for x64.";
                return null;
        }

        if (AddDllFolders(path) is { } settingsProblem)
        {
            problem = settingsProblem;
            return null;
        }

        foreach (var runtime in RuntimeToLend(
                     PeFile.Imports(path), System.IO.Path.GetDirectoryName(path)!, AppContext.BaseDirectory,
                     PeFile.IsLoaded, File.Exists))
        {
            NativeLibrary.TryLoad(runtime, out _);
        }

        IntPtr module;
        try
        {
            // By its full path, so Windows looks for what it needs in its own
            // folder first, then in Windows' and along PATH.
            module = NativeLibrary.Load(path);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or FileLoadException)
        {
            problem = LoadProblem(path, ex);
            return null;
        }

        if (!NativeLibrary.TryGetExport(module, EntryPoint, out var entry))
        {
            problem = $"This DLL does not export {EntryPoint}, so it contains no nodes.";
            return null;
        }

        var table = ((delegate* unmanaged[Cdecl]<SiriusLibrary*>)entry)();

        if (table == null)
        {
            problem = $"{EntryPoint} returned nothing.";
            return null;
        }

        if (table->ApiVersion != ApiVersion)
        {
            problem = $"This library was built for version {table->ApiVersion} of the C++ node interface; this " +
                      $"application provides version {ApiVersion}. Rebuild it with the sirius_node.h in this " +
                      "application's cpp folder.";
            return null;
        }

        if (table->Describe == null || table->Create == null || table->Destroy == null || table->Run == null)
        {
            problem = "The library leaves out an entry point it must provide: describe, create, destroy and run.";
            return null;
        }

        return new NativeNodeLibrary(path, table);
    }

    /// <summary>What the library says its nodes are, as JSON.</summary>
    public JsonArray Describe()
    {
        lock (Gate)
        {
            var error = stackalloc byte[ErrorSize];
            error[0] = 0;
            byte* json = null;

            if (_table->Describe(&json, error, ErrorSize) != Ok || json == null)
            {
                throw new NativeNodeException(
                    Message(error) is { Length: > 0 } said ? said : "The library could not describe its nodes.");
            }

            try
            {
                return JsonNode.Parse(Marshal.PtrToStringUTF8((IntPtr)json) ?? "[]") as JsonArray
                       ?? throw new NativeNodeException("The library's description of its nodes is not a list.");
            }
            catch (JsonException ex)
            {
                throw new NativeNodeException($"The library's description of its nodes could not be read: {ex.Message}");
            }
        }
    }

    // ---- nodes --------------------------------------------------------------------

    /// <summary>Makes the object behind one canvas node. Call holding <see cref="Gate"/>.</summary>
    public IntPtr Create(string key, string nodeName)
    {
        ThrowIfStopped();
        FreeReleased();

        var error = stackalloc byte[ErrorSize];
        error[0] = 0;
        void* node = null;
        var keyText = (byte*)Marshal.StringToCoTaskMemUTF8(key);

        try
        {
            var status = _table->Create(keyText, &node, error, ErrorSize);

            if (status == Crashed)
                throw Stop(nodeName, "being created", Message(error));

            if (status != Ok || node == null)
            {
                throw new NativeNodeException(
                    $"could not be created: {(Message(error) is { Length: > 0 } said ? said : "the library did not say why")}");
            }

            return (IntPtr)node;
        }
        finally
        {
            Marshal.FreeCoTaskMem((IntPtr)keyText);
        }
    }

    /// <summary>Calls a node's load. Call holding <see cref="Gate"/>.</summary>
    public void Load(IntPtr node, SiriusHost* host, string nodeName)
    {
        ThrowIfStopped();

        if (_table->Load == null)
            return;

        var error = stackalloc byte[ErrorSize];
        error[0] = 0;

        var status = _table->Load((void*)node, host, error, ErrorSize);

        if (status == Crashed)
            throw Stop(nodeName, "loading", Message(error));

        if (status != Ok)
            throw new NativeNodeException($"load() failed: {Said(error)}");
    }

    /// <summary>Runs a node once. Call holding <see cref="Gate"/>.</summary>
    public void Run(IntPtr node, SiriusHost* host, string nodeName)
    {
        ThrowIfStopped();

        var error = stackalloc byte[ErrorSize];
        error[0] = 0;

        var status = _table->Run((void*)node, host, error, ErrorSize);

        if (status == Crashed)
            throw Stop(nodeName, "running", Message(error));

        if (status != Ok)
            throw new NativeNodeException(Said(error));
    }

    /// <summary>
    /// Hands back a node that nothing uses any more. Safe from any thread:
    /// it is freed the next time the library is called.
    /// </summary>
    public void Release(IntPtr node) => _released.Enqueue(node);

    /// <summary>Copies bytes through the library's guard when it has one.</summary>
    /// <returns>False when the source could not be read.</returns>
    public bool Copy(void* destination, void* source, long bytes)
    {
        if (!HasGuardedCopy)
        {
            Buffer.MemoryCopy(source, destination, bytes, bytes);
            return true;
        }

        return _table->Copy(destination, source, bytes) == Ok;
    }

    public void ThrowIfStopped()
    {
        if (Stopped is { } why)
            throw new NativeNodeException(why);
    }

    private void FreeReleased()
    {
        while (_released.TryDequeue(out var node))
            _table->Destroy((void*)node);
    }

    // ---- faults -------------------------------------------------------------------

    /// <summary>
    /// Stops the library after a fault, and words the fault for the node
    /// it happened in.
    /// </summary>
    private NativeNodeException Stop(string nodeName, string doing, string report)
    {
        var what = DescribeFault(report, Path);

        Stopped = $"{Name} was stopped earlier, when {nodeName} crashed, and is not used again until Sirius Studio " +
                  "Plus is restarted.";

        return new NativeNodeException(
            $"crashed while {doing}: {what}. {Name} has been stopped, because its memory can no longer be trusted - " +
            "save your work and restart Sirius Studio Plus.");
    }

    /// <summary>
    /// "it read through a null pointer, in its own code (dark_spots.dll+0x1A2B)"
    /// from what the guard in sirius_node.hpp reports.
    /// </summary>
    internal static string DescribeFault(string report, string libraryPath)
    {
        var match = FaultReport().Match(report);

        if (!match.Success ||
            !uint.TryParse(match.Groups["code"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
        {
            return string.IsNullOrWhiteSpace(report) ? "a hardware fault" : report;
        }

        var what = code switch
        {
            0xC0000005 => Access(match),
            0xC0000006 => "it used memory that Windows could not page in",
            0xC00000FD => "it ran out of stack - recursion that does not end, or a very large local array",
            0xC0000094 => "it divided an integer by zero",
            0xC000001D => "it ran an instruction this processor does not have - built for a newer processor, " +
                          "with AVX-512 perhaps",
            0xC0000409 or 0xC0000374 => "it overran a buffer",
            _ => $"Windows stopped it with exception 0x{code:X8}",
        };

        return TryPointer(match.Groups["at"].Value, out var at) ? $"{what}, {Where(at, libraryPath)}" : what;
    }

    private static string Access(Match match)
    {
        if (!match.Groups["access"].Success || !TryPointer(match.Groups["target"].Value, out var target))
            return "it used memory that is not its own";

        var verb = match.Groups["access"].Value switch
        {
            "1" => "wrote",
            "8" => "ran code",
            _ => "read",
        };

        // The first 64 KB is never mapped: an address there is a null
        // pointer, or a field a little way into one.
        return target < 0x10000
            ? $"it {verb} through a null pointer"
            : $"it {verb} at 0x{target:X}, which is not memory it can use - a pointer to something already freed, " +
              "or past the end of an image";
    }

    /// <summary>Which DLL an address is in, and how far in - what a map file or a debugger wants.</summary>
    private static string Where(ulong address, string libraryPath)
    {
        // Asked of Windows for this one address. Listing every module of
        // the process to look through fails now and then while another
        // thread is loading one - and this is read at the worst of times.
        if (GetModuleHandleEx(FromAddress | UnchangedRefCount, (IntPtr)(long)address, out var module) && module != IntPtr.Zero)
        {
            var buffer = new char[1024];
            var length = (int)GetModuleFileName(module, buffer, (uint)buffer.Length);

            if (length > 0)
            {
                // A module's handle is the address it was loaded at.
                var file = new string(buffer, 0, length);
                var name = System.IO.Path.GetFileName(file);
                var place = $"{name}+0x{address - (ulong)(long)module:X}";

                return string.Equals(file, libraryPath, StringComparison.OrdinalIgnoreCase)
                    ? $"in its own code ({place})"
                    : $"inside {name}, which it uses ({place})";
            }
        }

        // The address alone is still something.
        return $"at 0x{address:X}";
    }

    private const uint UnchangedRefCount = 0x2;
    private const uint FromAddress = 0x4;

    [DllImport("kernel32", SetLastError = true, EntryPoint = "GetModuleHandleExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleHandleEx(uint flags, IntPtr address, out IntPtr module);

    [DllImport("kernel32", CharSet = CharSet.Unicode, EntryPoint = "GetModuleFileNameW")]
    private static extern uint GetModuleFileName(IntPtr module, [Out] char[] name, uint size);

    private static bool TryPointer(string text, out ulong value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];

        return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    [GeneratedRegex(@"^fault code=(?<code>[0-9A-Fa-f]+) at=(?<at>[0-9A-Fa-fx]+)(?: access=(?<access>\d+) target=(?<target>[0-9A-Fa-fx]+))?")]
    private static partial Regex FaultReport();

    // ---- helpers -------------------------------------------------------------------

    private static string Message(byte* error) => (Marshal.PtrToStringUTF8((IntPtr)error) ?? "").Trim();

    private static string Said(byte* error) =>
        Message(error) is { Length: > 0 } said ? said : "The library did not say what went wrong.";

    private static string MachineName(Machine machine) => machine switch
    {
        Machine.I386 => "32-bit Windows (x86)",
        Machine.Arm64 => "ARM64",
        Machine.Arm or Machine.ArmThumb2 => "32-bit ARM",
        _ => machine.ToString(),
    };

    /// <summary>
    /// Why Windows would not load a DLL - naming the DLL that is missing,
    /// which Windows does not.
    /// </summary>
    private static string LoadProblem(string path, Exception ex)
    {
        var missing = PeFile.MissingDependencies(path);

        if (missing.Count > 0)
        {
            return $"It could not be loaded: it needs {string.Join(", ", missing.Take(6))}" +
                   (missing.Count > 6 ? $" and {missing.Count - 6} more" : "") +
                   ", which Windows could not find. Put what is missing beside the library, or add its folder to " +
                   $"PATH, or name the folder in a {PythonInterpreter.SettingsFile} beside the library: " +
                   "{ \"dllFolders\": [\"C:\\\\opencv\\\\build\\\\x64\\\\vc16\\\\bin\"] }.";
        }

        return $"Windows could not load it: {ex.Message}";
    }

    /// <summary>
    /// The application's own copies of the Visual C++ runtime DLLs a
    /// library needs and would otherwise not find.
    /// </summary>
    /// <remarks>
    /// A library is loaded by its path, so Windows looks beside it and in
    /// Windows' own folders - not in the application's, where the runtime
    /// the application brings is. On a PC with the runtime installed that
    /// does not matter, and the installed one, which is the newer, is left
    /// to win. On a line PC with none, the library would fail for want of
    /// DLLs that are sitting in the application's folder: those are loaded
    /// first, by path, and Windows then gives the library the ones already
    /// loaded.
    /// </remarks>
    internal static IReadOnlyList<string> RuntimeToLend(
        IEnumerable<string> imports, string libraryFolder, string applicationFolder,
        Func<string, bool> isLoaded, Func<string, bool> exists)
    {
        var lend = new List<string>();

        foreach (var name in imports.Where(name => RuntimeName().IsMatch(name)))
        {
            if (isLoaded(name) ||
                exists(System.IO.Path.Combine(libraryFolder, name)) ||
                exists(System.IO.Path.Combine(Environment.SystemDirectory, name)))
            {
                continue;
            }

            // Published flat beside the executable; under runtimes in a build folder.
            var ours = new[]
            {
                System.IO.Path.Combine(applicationFolder, name),
                System.IO.Path.Combine(applicationFolder, "runtimes", "win-x64", "native", name),
            }.FirstOrDefault(exists);

            if (ours is not null)
                lend.Add(ours);
        }

        return lend;
    }

    [GeneratedRegex(@"^(msvcp140|vcruntime140|concrt140|vcomp140)[\w]*\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeName();

    /// <summary>
    /// Adds the folders a library's <c>sirius.json</c> names to this
    /// process's PATH, so Windows finds the DLLs in them - HALCON's, CUDA's,
    /// an OpenCV build's. Returns a sentence when the file cannot be read.
    /// </summary>
    private static string? AddDllFolders(string path)
    {
        var folder = System.IO.Path.GetDirectoryName(path)!;
        var settings = System.IO.Path.Combine(folder, PythonInterpreter.SettingsFile);

        if (!File.Exists(settings))
            return null;

        string[] named;
        try
        {
            named = (JsonNode.Parse(File.ReadAllText(settings))?["dllFolders"] as JsonArray ?? [])
                .Select(item => item?.GetValue<string>())
                .OfType<string>()
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, Environment.ExpandEnvironmentVariables(item))))
                .ToArray();
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            return $"{PythonInterpreter.SettingsFile} beside it could not be read: {ex.Message}";
        }

        if (named.FirstOrDefault(item => !Directory.Exists(item)) is { } absent)
            return $"{PythonInterpreter.SettingsFile} names {absent} in dllFolders, which does not exist.";

        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        var entries = current.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var added = named.Where(item => !entries.Contains(item, StringComparer.OrdinalIgnoreCase)).ToArray();

        if (added.Length > 0)
            Environment.SetEnvironmentVariable("PATH", string.Join(System.IO.Path.PathSeparator, added.Append(current)));

        return null;
    }
}

/// <summary>
/// Custom node libraries written in C++: finding them, and loading them by
/// asking them what they declare.
/// </summary>
/// <remarks>
/// A library is a native DLL that exports <c>sirius_node_library</c> - see
/// <c>sirius_node.h</c>. It runs in this process, so an image reaches it as
/// a pointer and it is as fast as a built-in tool; and so what goes wrong
/// in it is caught at the boundary, by <c>sirius_node.hpp</c> on its side
/// and here on this one.
/// </remarks>
public static class NativeNodeLibraries
{
    private static readonly Dictionary<string, NativeNodeLibrary> Loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a file is a C++ node library, read from its headers without loading it.</summary>
    public static bool IsLibrary(string path) =>
        string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase) &&
        PeFile.Exports(path, NativeNodeLibrary.EntryPoint);

    /// <summary>
    /// Loads a library and describes its nodes, without adding them to the
    /// catalogue. The library stays loaded for its nodes.
    /// </summary>
    public static PluginLibrary Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var name = Path.GetFileName(fullPath);

        if (!File.Exists(fullPath))
            return new PluginLibrary(fullPath, name, null, [], ["The file does not exist."]);

        NativeNodeLibrary? library;

        lock (Loaded)
        {
            if (!Loaded.TryGetValue(fullPath, out library))
            {
                library = NativeNodeLibrary.Open(fullPath, out var problem);

                if (library is null)
                    return new PluginLibrary(fullPath, name, null, [], [problem!]);

                Loaded[fullPath] = library;
            }
        }

        var version = VersionOf(fullPath);
        var origin = version is null ? name : $"{name} {version}";
        var nodes = new List<NodeDescriptor>();
        var problems = new List<string>();

        if (RuntimeNote(PeFile.LinkerVersion(fullPath), LoadedRuntime(fullPath)) is { } note)
            problems.Add(note);

        try
        {
            foreach (var description in library.Describe().OfType<JsonObject>())
            {
                if (NativeNodeType.TryCreate(description, library, origin, problems) is { } type)
                    nodes.Add(type.Descriptor);
            }
        }
        catch (NativeNodeException ex)
        {
            problems.Add(ex.Message);
        }

        if (nodes.Count == 0 && problems.Count == 0)
            problems.Add("The library loaded, but declares no nodes.");

        return new PluginLibrary(fullPath, name, version, nodes, problems);
    }

    private const string CppRuntime = "msvcp140.dll";

    /// <summary>
    /// The version of the Visual C++ runtime this process has loaded, when
    /// the library uses it - the one every DLL in the process shares,
    /// whichever asked for it first.
    /// </summary>
    private static Version? LoadedRuntime(string library)
    {
        if (!PeFile.Imports(library).Contains(CppRuntime, StringComparer.OrdinalIgnoreCase))
            return null;

        try
        {
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                if (string.Equals(module.ModuleName, CppRuntime, StringComparison.OrdinalIgnoreCase))
                    return new Version(module.FileVersionInfo.FileMajorPart, module.FileVersionInfo.FileMinorPart);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or IOException)
        {
            // Not knowing is not a problem to report.
        }

        return null;
    }

    /// <summary>
    /// A caution for a library built with newer Visual C++ tools than the
    /// runtime it will run on, or null when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// Microsoft promises that code runs on a runtime as new as its tools
    /// or newer - not older. A process has one runtime, the first one
    /// loaded, and the application brings its own; a library built with a
    /// later Visual Studio then runs on an earlier runtime than it was
    /// built for. It usually loads, and then faults somewhere ordinary: in
    /// 2024 it was locking a std::mutex. Saying so when the library loads
    /// costs a sentence; finding it from the fault costs a day.
    /// </remarks>
    internal static string? RuntimeNote(Version? builtWith, Version? runtime)
    {
        if (builtWith is null || runtime is null || builtWith.Major != 14 || runtime.Major != 14 || builtWith <= runtime)
            return null;

        return $"A caution: it was built with Visual C++ {builtWith.ToString(2)} tools, and the C++ runtime it runs on " +
               $"here is {runtime.ToString(2)}, which is older. Code built for a newer runtime can crash on an older " +
               $"one. Build it with tools no newer than {runtime.ToString(2)}, or update Sirius Studio Plus.";
    }

    /// <summary>The version in the DLL's properties, when it has one.</summary>
    private static string? VersionOf(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var version = string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
