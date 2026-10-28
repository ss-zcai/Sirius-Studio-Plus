using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Plugins;

namespace Sirius.Vision.Plugins;

/// <summary>
/// One custom node library, and what became of loading it.
/// </summary>
public sealed class PluginLibrary
{
    internal PluginLibrary(
        string path,
        string name,
        string? version,
        IReadOnlyList<NodeDescriptor> nodes,
        IReadOnlyList<string> problems)
    {
        Path = path;
        Name = name;
        Version = version;
        Nodes = nodes;
        Problems = problems;
    }

    /// <summary>The library's file.</summary>
    public string Path { get; }

    /// <summary>Its assembly name, or the file name when it could not be read.</summary>
    public string Name { get; }

    /// <summary>Its version, when it states one.</summary>
    public string? Version { get; }

    /// <summary>The nodes it added to the toolbox.</summary>
    public IReadOnlyList<NodeDescriptor> Nodes { get; }

    /// <summary>
    /// Everything that went wrong, one sentence each - a library may load
    /// some of its nodes and not others.
    /// </summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>"Name 1.2.0", for the operator.</summary>
    public string Title => Version is null ? Name : $"{Name} {Version}";

    public override string ToString() => Title;
}

/// <summary>
/// Finds, checks and loads custom node libraries.
/// </summary>
/// <remarks>
/// A library that will not load is reported and skipped. One bad DLL must
/// not stop the others, and must not stop the application: the operator
/// with a recipe to run did not write it.
/// </remarks>
public static class PluginLoader
{
    private static readonly ConcurrentDictionary<string, PluginLibrary> Installed =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object InstallGate = new();

    /// <summary>The contract version this application speaks.</summary>
    public static Version SdkVersion { get; } = typeof(ICustomNode).Assembly.GetName().Version!;

    /// <summary>The core library version this application carries.</summary>
    public static Version CoreVersion { get; } = typeof(VisionNode).Assembly.GetName().Version!;

    /// <summary>Every library installed this session, in the order they were installed.</summary>
    public static IReadOnlyList<PluginLibrary> Libraries
    {
        get
        {
            lock (InstallGate)
                return Order.ToArray();
        }
    }

    private static readonly List<PluginLibrary> Order = [];

    /// <summary>
    /// Every custom node library under <paramref name="folder"/>, at any depth.
    /// </summary>
    /// <remarks>
    /// A library is a DLL that references <c>Sirius.Plugins</c>, found by
    /// reading its metadata rather than loading it. Everything else - the
    /// libraries' own dependencies, native DLLs - is passed over, so a
    /// library can sit in a folder with whatever it needs.
    /// </remarks>
    public static IReadOnlyList<string> FindLibraries(string folder)
    {
        if (!Directory.Exists(folder))
            return [];

        return Directory
            .EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories)
            .Where(path => Probe(path).ReferencesSdk)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Loads a library and adds its nodes to the catalogue. Installing the
    /// same file twice returns the first result.
    /// </summary>
    public static PluginLibrary Install(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = System.IO.Path.GetFullPath(path);

        lock (InstallGate)
        {
            if (Installed.TryGetValue(fullPath, out var existing))
                return existing;

            var loaded = Load(fullPath);
            var nodes = new List<NodeDescriptor>();
            var problems = loaded.Problems.ToList();

            foreach (var node in loaded.Nodes)
            {
                if (NodeCatalog.FindByTypeKey(node.TypeKey) is { } taken)
                {
                    problems.Add(
                        $"{node.DisplayName}: its key \"{Unprefixed(node.TypeKey)}\" is already used by " +
                        $"{taken.DisplayName}{(taken.Origin is null ? "" : $" from {taken.Origin}")}, so it was not added.");
                    continue;
                }

                NodeCatalog.Register(node);
                nodes.Add(node);
            }

            var library = new PluginLibrary(loaded.Path, loaded.Name, loaded.Version, nodes, problems);
            Installed[fullPath] = library;
            Order.Add(library);
            return library;
        }
    }

    /// <summary>
    /// Loads a library and describes its nodes, without adding them to the
    /// catalogue.
    /// </summary>
    public static PluginLibrary Load(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var fileName = System.IO.Path.GetFileNameWithoutExtension(fullPath);
        var problems = new List<string>();

        if (!File.Exists(fullPath))
            return Failed(fullPath, fileName, null, "The file does not exist.");

        var probe = Probe(fullPath);

        if (!probe.IsManaged)
            return Failed(fullPath, fileName, null, "This is not a .NET library.");

        var name = probe.Name ?? fileName;

        if (!probe.ReferencesSdk)
        {
            return Failed(fullPath, name, probe.Version,
                "This library does not reference Sirius.Plugins, so it contains no custom nodes.");
        }

        if (VersionProblem(probe.SdkVersion, SdkVersion, probe.CoreVersion, CoreVersion) is { } versionProblem)
            return Failed(fullPath, name, probe.Version, versionProblem);

        Assembly assembly;
        try
        {
            assembly = new PluginLoadContext(fullPath).LoadMain(fullPath);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
        {
            return Failed(fullPath, name, probe.Version, $"The library could not be loaded: {ex.Message}");
        }

        var version = VersionOf(assembly) ?? probe.Version;
        var origin = version is null ? name : $"{name} {version}";
        var nodes = new List<NodeDescriptor>();

        foreach (var type in CandidateTypes(assembly, problems))
        {
            if (CustomNodeType.TryCreate(type, origin, problems) is { } described)
                nodes.Add(described.Descriptor);
        }

        if (nodes.Count == 0 && problems.Count == 0)
            problems.Add("The library loaded, but contains no custom nodes.");

        return new PluginLibrary(fullPath, name, version, nodes, problems);
    }

    /// <summary>
    /// Why a library built against one version cannot run on another, or
    /// null when it can.
    /// </summary>
    /// <remarks>
    /// The contract's major version must match: 2.0 means something a 1.x
    /// library relies on has gone. The core library only has to be no newer
    /// than the application's - an older one binds to the application's
    /// copy, and a newer one may use something this build does not have.
    /// Saying so here is a sentence; finding out later is a
    /// <see cref="MissingMethodException"/> from inside somebody's algorithm.
    /// </remarks>
    internal static string? VersionProblem(Version? sdkWanted, Version sdkHost, Version? coreWanted, Version coreHost)
    {
        if (sdkWanted is not null && sdkWanted.Major != sdkHost.Major)
        {
            return $"This library was built for version {sdkWanted.ToString(2)} of the custom node interface; " +
                   $"this application provides {sdkHost.ToString(2)}. Rebuild it against Sirius.Plugins {sdkHost.Major}.x.";
        }

        if (sdkWanted is not null && sdkWanted > sdkHost)
        {
            return $"This library needs version {sdkWanted.ToString(3)} of the custom node interface; " +
                   $"this application provides {sdkHost.ToString(3)}. Update the application.";
        }

        if (coreWanted is not null && coreWanted > coreHost)
        {
            return $"This library was built against a newer Sirius Studio Plus ({coreWanted.ToString(3)}) " +
                   $"than this one ({coreHost.ToString(3)}). Update the application.";
        }

        return null;
    }

    /// <summary>A saved key without the prefix custom nodes are filed under.</summary>
    public static string Unprefixed(string typeKey) =>
        typeKey.StartsWith(CustomNodeType.KeyPrefix, StringComparison.Ordinal)
            ? typeKey[CustomNodeType.KeyPrefix.Length..]
            : typeKey;

    /// <summary>Whether a saved key names a custom node.</summary>
    public static bool IsCustomKey(string typeKey) =>
        typeKey.StartsWith(CustomNodeType.KeyPrefix, StringComparison.Ordinal);

    // ---- reading a file without loading it ----------------------------------

    internal readonly record struct LibraryProbe(
        bool IsManaged, string? Name, string? Version, Version? SdkVersion, Version? CoreVersion)
    {
        public bool ReferencesSdk => SdkVersion is not null;
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

            var definition = metadata.GetAssemblyDefinition();
            Version? sdk = null;
            Version? core = null;

            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                var referenceName = metadata.GetString(reference.Name);

                if (referenceName == "Sirius.Plugins")
                    sdk = reference.Version;
                else if (referenceName == "Sirius.Core")
                    core = reference.Version;
            }

            return new LibraryProbe(
                true,
                metadata.GetString(definition.Name),
                definition.Version.ToString(3),
                sdk,
                core);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    // ---- helpers --------------------------------------------------------------

    private static IEnumerable<Type> CandidateTypes(Assembly assembly, ICollection<string> problems)
    {
        Type[] types;

        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types load and some do not - usually a dependency that is
            // missing from beside the library. Report the first few causes;
            // twenty copies of the same one help nobody.
            foreach (var message in ex.LoaderExceptions.OfType<Exception>().Select(e => e.Message).Distinct().Take(3))
                problems.Add($"Part of the library could not be loaded: {message}");

            types = ex.Types.OfType<Type>().ToArray();
        }

        return types.Where(type =>
            type is { IsClass: true, IsAbstract: false } &&
            (type.IsPublic || type.IsNestedPublic) &&
            typeof(ICustomNode).IsAssignableFrom(type));
    }

    private static string? VersionOf(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // The SDK appends "+<commit>" to a version built from source control.
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+')[0];

        return assembly.GetName().Version?.ToString(3);
    }

    private static PluginLibrary Failed(string path, string name, string? version, string problem) =>
        new(path, name, version, [], [problem]);
}
