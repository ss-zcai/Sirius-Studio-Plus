using Sirius.Core.Nodes;
using Sirius.Core.Shapes;

namespace Sirius.Plugins;

/// <summary>
/// Marks a class as a custom node, and says how it appears in the toolbox.
/// </summary>
/// <remarks>
/// A class implementing <see cref="ICustomNode"/> without this is not added,
/// and the Custom Nodes window says so - which is what makes a forgotten
/// attribute a message rather than a mystery.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CustomNodeAttribute : Attribute
{
    /// <param name="displayName">The node's name in the toolbox and on the canvas.</param>
    public CustomNodeAttribute(string displayName)
    {
        DisplayName = displayName;
    }

    /// <summary>The node's name in the toolbox and on the canvas.</summary>
    public string DisplayName { get; }

    /// <summary>
    /// The key saved into recipes. Required, and must never change once a
    /// recipe uses the node.
    /// </summary>
    /// <remarks>
    /// Written as <c>vendor.name</c> - <c>acme.flatfield</c> - so that two
    /// teams choosing the same short name do not collide. A recipe that
    /// names a key no loaded library provides opens with that node missing,
    /// so a renamed key silently breaks every recipe that used the old one.
    /// </remarks>
    public string? TypeKey { get; set; }

    /// <summary>Which toolbox group the node appears in.</summary>
    public NodeGroup Group { get; set; } = NodeGroup.Custom;

    /// <summary>
    /// One of the application's icon keys, such as <c>ph_brightness</c>. A
    /// key that does not exist falls back to the custom node icon.
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>A sentence or two for the tooltip and the node reference.</summary>
    public string? Description { get; set; }
}

/// <summary>Toolbox groups a custom node may appear in.</summary>
public enum NodeGroup
{
    /// <summary>Where images and data enter a recipe.</summary>
    Source,

    /// <summary>Thresholds, conversions, crops and resizes.</summary>
    Basic,

    /// <summary>Operations taking more than one image or shape set.</summary>
    Combine,

    /// <summary>Smoothing, sharpening and edge filters.</summary>
    Filtering,

    /// <summary>Erosion, dilation and their combinations.</summary>
    Morphology,

    /// <summary>Finding and measuring connected regions.</summary>
    Blob,

    /// <summary>Reducing located points to the figure underneath.</summary>
    Fitting,

    /// <summary>Geometric transforms.</summary>
    Transformation,

    /// <summary>Measurement, statistics and judgement.</summary>
    Analysis,

    /// <summary>Pixel-to-unit scales.</summary>
    Calibration,

    /// <summary>Text and code reading.</summary>
    TextAndCodeReading,

    /// <summary>Where results leave a recipe.</summary>
    Output,

    /// <summary>A group of their own, for nodes that fit nowhere else. The default.</summary>
    Custom,
}

/// <summary>
/// Marks a public property of a custom node as a parameter the operator
/// edits in the properties panel, and is saved with the recipe.
/// </summary>
/// <remarks>
/// The property needs a public getter and setter, and a type from
/// <see cref="CustomNodeTypes.SettingTypes"/> or any <c>enum</c>. Its value
/// when the class is constructed is the default an operator starts from.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute : Attribute
{
    /// <param name="displayName">
    /// What the properties panel calls it. Defaults to the property name
    /// split into words - <c>BlockSize</c> becomes "Block Size".
    /// </param>
    public SettingAttribute(string? displayName = null)
    {
        DisplayName = displayName;
    }

    /// <summary>What the properties panel calls it.</summary>
    public string? DisplayName { get; }

    /// <summary>The tooltip, and the text in the node reference.</summary>
    public string? Description { get; set; }

    /// <summary>A heading the setting is grouped under in the properties panel.</summary>
    public string? Group { get; set; }

    /// <summary>The lowest value. With <see cref="Max"/>, a number gets a slider.</summary>
    public double Min { get; set; } = double.NaN;

    /// <summary>The highest value.</summary>
    public double Max { get; set; } = double.NaN;

    /// <summary>How far one step of the slider moves.</summary>
    public double Step { get; set; } = double.NaN;

    /// <summary>The editor, when the one the type suggests is not wanted.</summary>
    public SettingEditor Editor { get; set; } = SettingEditor.Auto;

    /// <summary>Snap to odd values, as kernel and block sizes need.</summary>
    public bool OddOnly { get; set; }

    /// <summary>
    /// A slider spanning orders of magnitude, a decade per equal step, for a
    /// quantity like an area that runs from tens to millions.
    /// </summary>
    public bool Logarithmic { get; set; }

    /// <summary>The file dialog filter for a <see cref="SettingEditor.FilePath"/>, as "Label|*.ext".</summary>
    public string? Filter { get; set; }

    /// <summary>
    /// For a <see cref="SettingEditor.Range"/>: whether either end may be
    /// left off, meaning no limit.
    /// </summary>
    public bool AllowsNoLimit { get; set; }

    /// <summary>
    /// Also give the node an input for this, so another node can drive it.
    /// </summary>
    /// <remarks>
    /// The input appears below the node's own, named after the setting.
    /// Read the setting through <see cref="IRunContext.Resolve{T}"/> during a
    /// run, which returns the wired value when there is one. Not available
    /// for an <c>enum</c>.
    /// </remarks>
    public bool Connectable { get; set; }
}

/// <summary>How a setting is edited.</summary>
public enum SettingEditor
{
    /// <summary>
    /// Chosen from the type: a check box for <c>bool</c>, a dropdown for an
    /// <c>enum</c>, a slider for a number with both bounds, the drawable
    /// editor for a rectangle, ring or range, and a text box otherwise.
    /// </summary>
    Auto,

    /// <summary>A slider. Needs <see cref="SettingAttribute.Min"/> and <see cref="SettingAttribute.Max"/>.</summary>
    Slider,

    /// <summary>A text box. A <see cref="Point2"/> is typed as "x, y".</summary>
    TextBox,

    /// <summary>A dropdown, for an <c>enum</c>. Twelve or more entries can be filtered by typing.</summary>
    ComboBox,

    /// <summary>A check box, for a <c>bool</c>.</summary>
    CheckBox,

    /// <summary>A <c>string</c> path, with a browse button.</summary>
    FilePath,

    /// <summary>A <c>string</c> colour as "#RRGGBB", with swatches.</summary>
    Colour,

    /// <summary>An <see cref="OrientedRect"/>, typed or drawn on the input image.</summary>
    Rect,

    /// <summary>A <see cref="Core.Nodes.Ring"/>, typed or drawn on the input image.</summary>
    Ring,

    /// <summary>A <see cref="ValueRange"/>: two handles on one track.</summary>
    Range,
}

/// <summary>
/// The types a custom node's ports and settings may carry.
/// </summary>
/// <remarks>
/// A closed set, because each type is something the application knows how to
/// show, save and wire. Checked when a library loads.
/// </remarks>
public static class CustomNodeTypes
{
    /// <summary>What a port may carry.</summary>
    public static IReadOnlyList<Type> PortTypes { get; } =
    [
        typeof(Picture),
        typeof(ShapeSet),
        typeof(double),
        typeof(int),
        typeof(bool),
        typeof(string),
        typeof(Calibration),
        typeof(Point2),
        typeof(NumberMatrix),
        typeof(Bounds),
        typeof(OrientedRect),
        typeof(ValueRange),
        typeof(Core.Nodes.Ring),
    ];

    /// <summary>What a setting may be, as well as any <c>enum</c>.</summary>
    public static IReadOnlyList<Type> SettingTypes { get; } =
    [
        typeof(double),
        typeof(float),
        typeof(int),
        typeof(long),
        typeof(bool),
        typeof(string),
        typeof(Point2),
        typeof(OrientedRect),
        typeof(ValueRange),
        typeof(Core.Nodes.Ring),
    ];

    /// <summary>Whether a port may carry <paramref name="type"/>.</summary>
    public static bool IsPortType(Type type) => PortTypes.Contains(type);

    /// <summary>Whether a setting may be <paramref name="type"/>.</summary>
    public static bool IsSettingType(Type type) => type.IsEnum || SettingTypes.Contains(type);

    internal static bool IsNumeric(Type type) =>
        type == typeof(double) || type == typeof(float) || type == typeof(int) || type == typeof(long);

    internal static string Describe(IEnumerable<Type> types) =>
        string.Join(", ", types.Select(type => type.Name));
}
