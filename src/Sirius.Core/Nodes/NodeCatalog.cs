using System.Collections.Concurrent;
using System.Reflection;
using Sirius.Core.Graph;

namespace Sirius.Core.Nodes;

/// <summary>
/// Registry of available node types, and the cache behind
/// <see cref="VisionNode.Descriptor"/>.
/// </summary>
/// <remarks>
/// The old app discovered tools with
/// <c>AppDomain.CurrentDomain.GetAssemblies().SelectMany(s =&gt; s.GetTypes())</c>
/// and then instantiated every match just to populate the toolbox grid. That
/// scanned unrelated assemblies, had no guard against abstract types, and
/// constructed throwaway instances on the UI thread. Registration here is
/// explicit per assembly, descriptors are metadata-only, and instances are
/// created solely when a node is actually added to a graph.
/// </remarks>
public static class NodeCatalog
{
    private static readonly ConcurrentDictionary<Type, NodeDescriptor> Descriptors = new();
    private static readonly List<NodeDescriptor> Registered = new();
    private static readonly Dictionary<string, NodeDescriptor> ByTypeKey = new(StringComparer.Ordinal);
    private static readonly HashSet<Assembly> ScannedAssemblies = new();
    private static readonly object Gate = new();

    /// <summary>All instantiable node types, in palette order.</summary>
    public static IReadOnlyList<NodeDescriptor> All
    {
        get
        {
            lock (Gate)
                return Registered.ToArray();
        }
    }

    /// <summary>
    /// Scans an assembly for <see cref="VisionNodeAttribute"/>-marked nodes.
    /// Idempotent, so plugin assemblies can be offered more than once.
    /// </summary>
    public static void Register(Assembly assembly)
    {
        lock (Gate)
        {
            if (!ScannedAssemblies.Add(assembly))
                return;

            foreach (var type in GetLoadableTypes(assembly))
            {
                if (type.IsAbstract || !typeof(VisionNode).IsAssignableFrom(type))
                    continue;

                if (type.GetCustomAttribute<VisionNodeAttribute>() is null)
                    continue;

                var descriptor = Describe(type);
                if (!descriptor.IsInstantiable)
                    continue;

                if (ByTypeKey.TryGetValue(descriptor.TypeKey, out var existing))
                {
                    throw new InvalidOperationException(
                        $"Duplicate node TypeKey '{descriptor.TypeKey}': " +
                        $"'{existing.NodeType.FullName}' and '{type.FullName}'. " +
                        "TypeKeys must be unique because saved graphs reference them.");
                }

                ByTypeKey.Add(descriptor.TypeKey, descriptor);
                Registered.Add(descriptor);
            }

            SortRegistered();
        }
    }

    /// <summary>
    /// Adds one node type that was not found by scanning an assembly.
    /// </summary>
    /// <remarks>
    /// For descriptors built by hand - a custom node from a plugin library
    /// is wrapped rather than subclassed, so scanning for
    /// <see cref="VisionNodeAttribute"/> would never find it. Offering the
    /// same descriptor twice is harmless; a different one with the same
    /// key is refused, for the same reason <see cref="Register(Assembly)"/>
    /// refuses it.
    /// </remarks>
    public static void Register(NodeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        lock (Gate)
        {
            if (ByTypeKey.TryGetValue(descriptor.TypeKey, out var existing))
            {
                if (ReferenceEquals(existing, descriptor))
                    return;

                throw new InvalidOperationException(
                    $"Duplicate node TypeKey '{descriptor.TypeKey}': " +
                    $"'{existing.NodeType.FullName}' and '{descriptor.NodeType.FullName}'. " +
                    "TypeKeys must be unique because saved graphs reference them.");
            }

            ByTypeKey.Add(descriptor.TypeKey, descriptor);
            Registered.Add(descriptor);
            SortRegistered();
        }
    }

    private static void SortRegistered() =>
        Registered.Sort(static (a, b) =>
        {
            var byCategory = a.Category.CompareTo(b.Category);
            return byCategory != 0
                ? byCategory
                : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture);
        });

    /// <summary>Looks up a node type by its stable saved-file key.</summary>
    public static NodeDescriptor? FindByTypeKey(string typeKey)
    {
        lock (Gate)
            return ByTypeKey.GetValueOrDefault(typeKey);
    }

    /// <summary>
    /// Returns cached metadata for a node type, building it on first use.
    /// Safe to call for unregistered and abstract types.
    /// </summary>
    public static NodeDescriptor Describe(Type nodeType) =>
        Descriptors.GetOrAdd(nodeType, static type =>
        {
            var attribute = type.GetCustomAttribute<VisionNodeAttribute>();

            var parameters = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => (property, attr: property.GetCustomAttribute<ParamAttribute>()))
                .Where(pair => pair.attr is not null && pair.property.CanRead && pair.property.CanWrite)
                .Select(pair => new ParamDescriptor(pair.property, pair.attr!))
                .ToArray();

            return new NodeDescriptor(type, attribute, parameters);
        });

    /// <summary>
    /// Tolerates partially loadable assemblies - a plugin built against a
    /// different dependency version should not take down discovery.
    /// </summary>
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static t => t is not null)!;
        }
    }
}
