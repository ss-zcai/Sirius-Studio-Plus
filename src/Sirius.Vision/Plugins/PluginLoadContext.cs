using System.Reflection;
using System.Runtime.Loader;

namespace Sirius.Vision.Plugins;

/// <summary>
/// Where one custom node library's code lives.
/// </summary>
/// <remarks>
/// <para>
/// Each library gets its own, so two libraries may depend on two versions of
/// the same package without either noticing. The exceptions are the
/// assemblies that make up the contract: a library's
/// <see cref="Sirius.Plugins.ICustomNode"/> has to be the application's
/// <see cref="Sirius.Plugins.ICustomNode"/>, or the application could not
/// recognise it. Those always come from the application, whatever copy a
/// library ships beside itself.
/// </para>
/// <para>
/// Managed assemblies are read into memory rather than loaded from their
/// files, so the files are not locked: somebody developing a node can
/// rebuild it with the application open, and restart to try it.
/// </para>
/// </remarks>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    /// <summary>The assemblies a library and the application must share.</summary>
    internal static readonly IReadOnlySet<string> Shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Sirius.Plugins",
        "Sirius.Core",
        "CommunityToolkit.Mvvm",
    };

    private readonly string _directory;
    private readonly AssemblyDependencyResolver? _resolver;

    public PluginLoadContext(string mainAssemblyPath)
        : base($"Custom nodes: {Path.GetFileNameWithoutExtension(mainAssemblyPath)}")
    {
        _directory = Path.GetDirectoryName(mainAssemblyPath)!;

        // The resolver reads the library's .deps.json, which is how a
        // dependency in a NuGet-style subfolder is found. Without one it
        // still works for files beside the library, which the fallback
        // below covers too.
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
        // Null defers to the application's own copy.
        if (assemblyName.Name is null || Shared.Contains(assemblyName.Name))
            return null;

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
            var name = unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? unmanagedDllName
                : unmanagedDllName + ".dll";

            var beside = Path.Combine(_directory, name);
            path = File.Exists(beside) ? beside : null;
        }

        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    private Assembly LoadCopy(string path)
    {
        using var image = new MemoryStream(File.ReadAllBytes(path));

        var symbolsPath = Path.ChangeExtension(path, ".pdb");
        using var symbols = File.Exists(symbolsPath)
            ? new MemoryStream(File.ReadAllBytes(symbolsPath))
            : null;

        return LoadFromStream(image, symbols);
    }
}
