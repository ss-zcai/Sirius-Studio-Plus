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

    /// <summary>The settings a Camera node has, by parameter name.</summary>
    public static readonly IReadOnlyList<string> Settings = ["Device", "Exposure", "Gain", "Trigger", "Timeout"];

    /// <summary>A Camera node's setting, as the recipe had it and as the station's camera put it.</summary>
    public sealed record CameraChange(string Parameter, object? Recipe, object? Line);

    /// <summary>
    /// <see cref="Apply"/>, saying what it changed: for Studio, opened from
    /// a station (Open in Studio), to grab with the line's camera and still
    /// save the recipe's own values.
    /// </summary>
    /// <param name="node">The Camera node it went into, or null.</param>
    /// <param name="problem">Why it could not be, or null.</param>
    public static IReadOnlyList<CameraChange> Carry(VisionGraph recipe, CameraUnit camera, out VisionNode? node, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(camera);

        var found = NodeFor(recipe, camera, out problem);
        node = found;

        if (found is null)
            return [];

        var before = Settings.Select(name => GetParameter(found, name)).ToArray();

        if (Apply(recipe, camera) is { } refused)
        {
            problem = refused;
            node = null;
            return [];
        }

        var carried = found;

        return Settings
            .Select((name, i) => new CameraChange(name, before[i], GetParameter(carried, name)))
            .Where(change => !Equals(change.Recipe, change.Line))
            .ToArray();
    }

    /// <summary>
    /// The camera and settings in the Camera node <paramref name="camera"/>
    /// goes into - Take from Recipe. Null when there is none to take from.
    /// </summary>
    public static CameraSetup? From(VisionGraph recipe, CameraUnit camera) =>
        NodeFor(recipe, camera, out _) is { } node ? SetupOf(node) : null;

    /// <summary>
    /// The camera's settings it does not leave to the recipe, written into
    /// the recipe's Camera node - Give to Recipe. The camera itself is left:
    /// at the desk it may be another.
    /// </summary>
    /// <returns>The settings written, by parameter name; null with <paramref name="problem"/> when none could be.</returns>
    public static IReadOnlyList<string>? GiveTo(VisionGraph recipe, CameraUnit camera, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(camera);

        if (NodeFor(recipe, camera, out problem) is not { } node)
            return null;

        var setup = camera.Setup;
        var given = new List<string>();

        void Give(string name, object? value)
        {
            if (value is not null && !Equals(GetParameter(node, name)?.ToString(), Converted(node, name, value)?.ToString()))
            {
                SetParameter(node, name, value);
                given.Add(name);
            }
        }

        Give("Exposure", setup.Exposure);
        Give("Gain", setup.Gain);
        Give("Trigger", setup.Trigger);
        Give("Timeout", setup.Timeout);
        return given;
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

        return SetupOf(nodes[0]);
    }

    private static CameraSetup SetupOf(VisionNode node)
    {
        return new CameraSetup(
            Get(node, "Device") as string ?? string.Empty,
            Get(node, "Exposure") is { } exposure ? Convert.ToDouble(exposure, System.Globalization.CultureInfo.InvariantCulture) : null,
            Get(node, "Gain") is { } gain ? Convert.ToDouble(gain, System.Globalization.CultureInfo.InvariantCulture) : null,
            Get(node, "Trigger")?.ToString(),
            Get(node, "Timeout") is { } timeout ? Convert.ToInt32(timeout, System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    private static object? Get(VisionNode node, string name) => GetParameter(node, name);

    private static void Set(VisionNode node, string name, object value) => SetParameter(node, name, value);

    /// <summary>A node's parameter by name; null when it has none of that name.</summary>
    public static object? GetParameter(VisionNode node, string name)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Descriptor.Parameters.FirstOrDefault(parameter => parameter.Name == name)?.GetValue(node);
    }

    /// <summary>Sets a node's parameter by name, converted to its type; nothing when it has none of that name.</summary>
    public static void SetParameter(VisionNode node, string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (value is null || node.Descriptor.Parameters.FirstOrDefault(parameter => parameter.Name == name) is not { } parameter)
            return;

        parameter.SetValue(node, Converted(node, name, value));
    }

    private static object? Converted(VisionNode node, string name, object value)
    {
        if (node.Descriptor.Parameters.FirstOrDefault(parameter => parameter.Name == name) is not { } parameter)
            return value;

        var type = Nullable.GetUnderlyingType(parameter.ValueType) ?? parameter.ValueType;

        return type.IsEnum
            ? Enum.Parse(type, value.ToString()!, ignoreCase: true)
            : Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);
    }
}
