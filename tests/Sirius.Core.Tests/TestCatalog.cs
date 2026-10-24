using System.Reflection;
using Sirius.Core.Nodes;

namespace Sirius.Core.Tests;

/// <summary>
/// Registers node assemblies exactly once for the whole test run.
/// </summary>
/// <remarks>
/// <see cref="NodeCatalog"/> is process-wide and rejects duplicate type keys,
/// so registration must not race across xunit's parallel collections.
/// </remarks>
public static class TestCatalog
{
    private static readonly Lazy<bool> Initialiser = new(() =>
    {
        NodeCatalog.Register(typeof(TestCatalog).Assembly);
        NodeCatalog.Register(Assembly.Load("Sirius.Vision"));
        return true;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Ensure() => _ = Initialiser.Value;
}
