using Sirius.Core.Nodes;

namespace Sirius.Vision;

/// <summary>Entry point for registering the built-in node library.</summary>
public static class VisionNodeLibrary
{
    /// <summary>
    /// Adds every node in this assembly to <see cref="NodeCatalog"/>, and
    /// the sub-graph nodes from Sirius.Core. Idempotent, so calling it twice
    /// is harmless.
    /// </summary>
    /// <remarks>
    /// The sub-graph nodes live in Core because they carry no image
    /// processing - only graphs - and the scheduler and serialiser there
    /// need to know about them.
    /// </remarks>
    public static void Register()
    {
        NodeCatalog.Register(typeof(Sirius.Core.Graph.SubGraphNode).Assembly);
        NodeCatalog.Register(typeof(VisionNodeLibrary).Assembly);
    }
}
