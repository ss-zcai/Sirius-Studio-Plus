using Sirius.Core.Graph;
using Sirius.Core.Projects;

namespace Sirius.Core.Runtime;

/// <summary>
/// A project camera put into a station's recipe: its camera and settings
/// written into the recipe's Camera node, as loaded to run - never into the
/// recipe file (backlog F29).
/// </summary>
/// <remarks>
/// <para>
/// The node is found by its type key and set through its parameters by
/// name, so the core needs to know nothing about cameras: what the Camera
/// node does with a device and an exposure is the vision library's.
/// </para>
/// <para>
/// Done before the line's settings are applied, so a setting an operator
/// changed on the line - an exposure published and tuned - still wins.
/// </para>
/// </remarks>
public static class StationCamera
{
    /// <summary>The Camera node's type key.</summary>
    public const string CameraNodeType = "source.camera";

    /// <summary>The Camera nodes in <paramref name="recipe"/>.</summary>
    public static IReadOnlyList<VisionNode> CameraNodes(VisionGraph recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        return recipe.Nodes.Where(node => node.Descriptor.TypeKey == CameraNodeType).ToArray();
    }

    /// <summary>
    /// The Camera node <paramref name="camera"/> goes into: the recipe's only
    /// one, or of several the one named after the camera.
    /// </summary>
    /// <param name="problem">Why there is none to use.</param>
    public static VisionNode? NodeFor(VisionGraph recipe, CameraUnit camera, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(camera);

        var nodes = CameraNodes(recipe);
        problem = null;

        switch (nodes.Count)
        {
            case 0:
                problem = $"Its recipe has no Camera node, so the camera '{camera.Name}' is not used.";
                return null;

            case 1:
                return nodes[0];
        }

        var named = nodes.FirstOrDefault(node => string.Equals(node.Name, camera.Name, StringComparison.OrdinalIgnoreCase));

        if (named is null)
        {
            problem = $"Its recipe has {nodes.Count} Camera nodes: name the one the camera '{camera.Name}' is for after it, in Studio.";
        }

        return named;
    }

    /// <summary>
    /// Writes <paramref name="camera"/>'s camera, and each setting it does not
    /// leave to the recipe, into the recipe's Camera node.
    /// </summary>
    /// <returns>Why it could not be, or null when it was.</returns>
    public static string? Apply(VisionGraph recipe, CameraUnit camera)
    {
        ArgumentNullException.ThrowIfNull(camera);

        if (NodeFor(recipe, camera, out var problem) is not { } node)
            return problem;

        var setup = camera.Setup;

        if (!setup.HasDevice)
            return $"The camera '{camera.Name}' has no camera chosen: its recipe's own is used. Choose one in Builder.";

        Set(node, "Device", setup.Device);

        if (setup.Exposure is { } exposure)
            Set(node, "Exposure", exposure);

        if (setup.Gain is { } gain)
            Set(node, "Gain", gain);

        if (setup.Trigger is { } trigger)
            Set(node, "Trigger", trigger);

        if (setup.Timeout is { } timeout)
            Set(node, "Timeout", timeout);

        return null;
    }

    /// <summary>
    /// The camera and settings <paramref name="recipe"/>'s Camera node holds:
    /// what a project camera made from it starts with, or takes from it.
    /// Null when it has none, or several.
    /// </summary>
    public static CameraSetup? From(VisionGraph recipe)
    {
        var nodes = CameraNodes(recipe);

        if (nodes.Count != 1)
            return null;

        var node = nodes[0];

        return new CameraSetup(
            Get(node, "Device") as string ?? string.Empty,
            Get(node, "Exposure") is { } exposure ? Convert.ToDouble(exposure, System.Globalization.CultureInfo.InvariantCulture) : null,
            Get(node, "Gain") is { } gain ? Convert.ToDouble(gain, System.Globalization.CultureInfo.InvariantCulture) : null,
            Get(node, "Trigger")?.ToString(),
            Get(node, "Timeout") is { } timeout ? Convert.ToInt32(timeout, System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    private static object? Get(VisionNode node, string name) =>
        node.Descriptor.Parameters.FirstOrDefault(parameter => parameter.Name == name)?.GetValue(node);

    private static void Set(VisionNode node, string name, object value)
    {
        if (node.Descriptor.Parameters.FirstOrDefault(parameter => parameter.Name == name) is not { } parameter)
            return;

        var type = Nullable.GetUnderlyingType(parameter.ValueType) ?? parameter.ValueType;

        var converted = type.IsEnum
            ? Enum.Parse(type, value.ToString()!, ignoreCase: true)
            : Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);

        parameter.SetValue(node, converted);
    }
}
