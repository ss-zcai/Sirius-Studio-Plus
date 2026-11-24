using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace Sirius.Plc;

/// <summary>
/// Finds driver libraries - DLLs built against <c>Sirius.Plc</c> holding
/// <see cref="IPlcDriverFactory"/> classes - and makes their drivers known.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where</b>: <c>plc-drivers\</c> beside the program, for every user, and
/// <c>%APPDATA%\SiriusStudioPlus\plc-drivers\</c>, for one. Folders are
/// searched at any depth, so <c>plc-drivers\Siemens\Acme.Drivers.Siemens.dll</c>
/// with HslCommunication.dll beside it is fine. A DLL is told to be a driver
/// library by reading its references, without loading it, so the
/// libraries it depends on are passed over.
/// </para>
/// <para>
/// <b>Isolation</b>: each library loads into a context of its own, so two
/// may use two versions of one package. <c>Sirius.Plc</c>, <c>Sirius.Core</c>
/// and <c>CommunityToolkit.Mvvm</c> always come from the program - a
/// library's <see cref="IPlcDriverFactory"/> must be the program's, or the
/// program could not recognise it. Files are read into memory, not locked,
/// so a library can be rebuilt while Builder is open; restart to use it.
/// </para>
/// <para>
/// <b>Versions</b>, checked before any of a library's code runs: the
/// <c>Sirius.Plc</c> it was built against must have the same major version
/// as the program's, and a minor no newer. Anything wrong is a sentence in
/// <see cref="PlcDrivers.Problems"/>; the library is skipped, the others load.
/// </para>
/// </remarks>
public static class PlcDriverLoader
{
    /// <summary>The folder name drivers are kept in.</summary>
    public const string FolderName = "plc-drivers";

    /// <summary>The assemblies a driver library and the program must share.</summary>
    internal static readonly IReadOnlySet<string> Shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Sirius.Plc",
        "Sirius.Core",
        "CommunityToolkit.Mvvm",
    };

    private static readonly HashSet<string> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>Loads every driver library in these folders, those that exist.</summary>
    public static void LoadFolders(params string[] folders)
    {
        foreach (var folder in folders.Where(Directory.Exists))
        {
            IEnumerable<string> files;

            try
            {
                files = Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PlcDrivers.Problem($"{folder} could not be searched for PLC drivers: {ex.Message}");
                continue;
            }

            foreach (var file in files.Where(file => Probe(file).ReferencesPlc))
                Load(file);
        }
    }

    /// <summary>
    /// Loads one driver library and makes its drivers known.
    /// </summary>
    /// <returns>The drivers it added - none when it could not be used, which is said in <see cref="PlcDrivers.Problems"/>.</returns>
    public static IReadOnlyList<IPlcDriverFactory> Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var name = Path.GetFileName(fullPath);

        lock (Gate)
        {
            if (!Loaded.Add(fullPath))
                return [];
        }

        var probe = Probe(fullPath);

        if (!probe.IsManaged)
            return Refuse($"{name} is not a .NET library.");

        if (probe.PlcVersion is not { } built)
            return Refuse($"{name} does not use Sirius.Plc, so holds no PLC driver.");

        var offered = PlcDriverInterface.Version;

        if (built.Major != offered.Major)
        {
            return Refuse(
                $"{name} was built for PLC driver interface {built.Major}.x; this program has {offered.Major}.{offered.Minor}. " +
                $"Rebuild it against {offered.Major}.x.");
        }

        if (built.Minor > offered.Minor)
        {
            return Refuse(
                $"{name} was built for PLC driver interface {built.Major}.{built.Minor}, newer than this program's " +
                $"{offered.Major}.{offered.Minor}. Update the program, or rebuild the library against {offered.Major}.{offered.Minor}.");
        }

        Assembly assembly;

        try
        {
            assembly = new DriverLoadContext(fullPath).LoadMain(fullPath);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or FileLoadException or UnauthorizedAccessException)
        {
            return Refuse($"{name} could not be loaded: {ex.Message}");
        }

        var added = new List<IPlcDriverFactory>();

        foreach (var type in Types(assembly, name))
        {
            if (type.IsAbstract || type.IsInterface || !typeof(IPlcDriverFactory).IsAssignableFrom(type))
                continue;

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                PlcDrivers.Problem($"{name}: {type.Name} needs a constructor that takes nothing.");
                continue;
            }

            try
            {
                var factory = (IPlcDriverFactory)Activator.CreateInstance(type)!;
                var problem = KeyProblem(factory.Key);

                if (problem is not null)
                {
                    PlcDrivers.Problem($"{name}: {type.Name}'s key '{factory.Key}' {problem}");
                    continue;
                }

                if (PlcDrivers.Find(factory.Key) is { } taken && taken.GetType().Assembly != assembly)
                {
                    PlcDrivers.Problem(
                        $"{name}: the key '{factory.Key}' is {taken.Name}'s already, from {taken.GetType().Assembly.GetName().Name}; " +
                        $"{type.Name} was not added.");
                    continue;
                }

                PlcDrivers.Register(factory);
                added.Add(factory);
            }
            catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                PlcDrivers.Problem($"{name}: {type.Name} could not be made: {(ex.InnerException ?? ex).Message}");
            }
        }

        if (added.Count == 0)
            PlcDrivers.Problem($"{name} uses Sirius.Plc but has no public IPlcDriverFactory class that could be made.");

        return added;
    }

    /// <summary>Why a key cannot be used, or null: lower case letters, digits and '-', starting with a letter.</summary>
    public static string? KeyProblem(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "is empty.";

        if (!char.IsAsciiLetterLower(key[0]) || key.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')))
            return "must be lower case letters, digits and '-', starting with a letter - as siemens-s7.";

        return key == SimulatedPlcDriver.Key ? "is the simulated PLC's." : null;
    }

    private static IReadOnlyList<IPlcDriverFactory> Refuse(string problem)
    {
        PlcDrivers.Problem(problem);
        return [];
    }

    private static IEnumerable<Type> Types(Assembly assembly, string name)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            PlcDrivers.Problem($"{name}: some of its classes could not be loaded - {ex.LoaderExceptions.FirstOrDefault()?.Message}");
            return ex.Types.OfType<Type>().Where(type => type.IsPublic);
        }
        catch (FileNotFoundException ex)
        {
            PlcDrivers.Problem($"{name} needs {ex.FileName}, which is not beside it.");
            return [];
        }
    }

    // ---- reading a file without loading it -----------------------------------------

    internal readonly record struct LibraryProbe(bool IsManaged, Version? PlcVersion)
    {
        public bool ReferencesPlc => PlcVersion is not null;
    }

    internal static LibraryProbe Probe(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);

            if (!reader.HasMetadata)
                return default;

            var metadata = reader.GetMetadataReader();

            if (!metadata.IsAssembly)
                return default;

            // Sirius.Plc itself, copied beside a library by mistake, is not a library.
            if (metadata.GetString(metadata.GetAssemblyDefinition().Name) == "Sirius.Plc")
                return new LibraryProbe(true, null);

            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);

                if (metadata.GetString(reference.Name) == "Sirius.Plc")
                    return new LibraryProbe(true, reference.Version);
            }

            return new LibraryProbe(true, null);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    /// <summary>Where one driver library's code lives - and its dependencies, HslCommunication among them.</summary>
    private sealed class DriverLoadContext : AssemblyLoadContext
    {
        private readonly string _directory;
        private readonly AssemblyDependencyResolver? _resolver;

        public DriverLoadContext(string mainAssemblyPath)
            : base($"PLC drivers: {Path.GetFileNameWithoutExtension(mainAssemblyPath)}")
        {
            _directory = Path.GetDirectoryName(mainAssemblyPath)!;

            try
            {
                _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
            {
                _resolver = null;
            }
        }

        public Assembly LoadMain(string path) => LoadCopy(path);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Null defers to the program's own copy: the contract, and
            // anything else the program has loaded already - a second copy
            // of one of its own assemblies confuses WPF's resources for the
            // whole program. What only the driver brings - HslCommunication
            // - is the driver's own.
            if (assemblyName.Name is null || Shared.Contains(assemblyName.Name) ||
                Default.Assemblies.Any(loaded => string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var path = _resolver?.ResolveAssemblyToPath(assemblyName);

            if (path is null)
            {
                var beside = Path.Combine(_directory, assemblyName.Name + ".dll");
                path = File.Exists(beside) ? beside : null;
            }

            return path is null ? null : LoadCopy(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);

            if (path is null)
            {
                var file = unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? unmanagedDllName : unmanagedDllName + ".dll";
                var beside = Path.Combine(_directory, file);
                path = File.Exists(beside) ? beside : null;
            }

            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }

        private Assembly LoadCopy(string path)
        {
            using var image = new MemoryStream(File.ReadAllBytes(path));

            var symbolsPath = Path.ChangeExtension(path, ".pdb");
            using var symbols = File.Exists(symbolsPath) ? new MemoryStream(File.ReadAllBytes(symbolsPath)) : null;

            return LoadFromStream(image, symbols);
        }
    }
}
