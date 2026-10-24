using Sirius.Core.Nodes;

namespace Sirius.Vision;

/// <summary>Entry point for registering the built-in node library.</summary>
public static class VisionNodeLibrary
{
    /// <summary>
    /// Adds every node in this assembly to <see cref="NodeCatalog"/>.
    /// Idempotent, so calling it twice is harmless.
    /// </summary>
    public static void Register() => NodeCatalog.Register(typeof(VisionNodeLibrary).Assembly);
}
