namespace Sirius.Core.Nodes;

/// <summary>
/// Marks a <c>VisionNode</c> subclass as instantiable and describes how it
/// appears in the node palette.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class VisionNodeAttribute : Attribute
{
    public VisionNodeAttribute(string displayName)
    {
        DisplayName = displayName;
    }

    public string DisplayName { get; }

    public NodeCategory Category { get; set; } = NodeCategory.Basic;

    /// <summary>
    /// Key into the icon resource dictionary (e.g. "tool-icon-01"). Icons are
    /// looked up by key at display time, so new artwork can be dropped in
    /// without touching node code.
    /// </summary>
    public string? Icon { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// Stable type key used in saved graphs. Defaults to the CLR type name.
    /// Set this explicitly before shipping so classes can be renamed later
    /// without breaking existing saved files.
    /// </summary>
    public string? TypeKey { get; set; }

    /// <summary>
    /// No longer offered in the toolbox, but still loaded and run wherever a
    /// saved graph has one - so retiring a tool never breaks a recipe.
    /// </summary>
    public bool Retired { get; set; }
}

/// <summary>
/// How a parameter should be edited in the inspector.
/// </summary>
public enum ParamEditor
{
    /// <summary>Choose from the property type.</summary>
    Auto,
    Slider,
    TextBox,
    ComboBox,
    CheckBox,
    /// <summary>Rectangle, drawable directly on the image viewer.</summary>
    Region,
    /// <summary>Width/height pair.</summary>
    Size,

    /// <summary>A low and a high bound, on one track with two handles.</summary>
    Range,

    /// <summary>
    /// A rectangle with an angle, typed as one line and drawable on the
    /// image the node is given.
    /// </summary>
    Rect,

    /// <summary>
    /// An annular band, as "x, y, inner, outer" with a button to draw it
    /// on the image - a centre, the edge, then the width either side.
    /// </summary>
    Ring,
    /// <summary>Structuring-element shape + size + anchor.</summary>
    Kernel,
    /// <summary>Text box with a browse button that opens a file dialog.</summary>
    FilePath,

    /// <summary>
    /// A colour, chosen from swatches or typed as hex.
    /// </summary>
    Colour,

    /// <summary>
    /// Position within a sequence, with next and previous controls.
    /// </summary>
    /// <remarks>
    /// Only meaningful on a node implementing <see cref="ISequenceNode"/>,
    /// which is what supplies the length the position is shown against.
    /// </remarks>
    Sequence,
}

/// <summary>
/// A node that steps through a list of inputs.
/// </summary>
/// <remarks>
/// The position is an ordinary parameter, so stepping re-evaluates the graph
/// the same way moving a slider does. This interface adds only what a
/// parameter cannot carry: how many there are, and which one is current. Both
/// are derived from the node's own state rather than stored, so neither is
/// saved into a recipe where it could go stale.
/// </remarks>
public interface ISequenceNode
{
    /// <summary>How many items the sequence holds.</summary>
    int Count { get; }

    /// <summary>The current position, counting from one.</summary>
    int Index { get; set; }

    /// <summary>What the current item is called, or null when there is none.</summary>
    string? CurrentName { get; }
}

/// <summary>
/// Marks a node property as an operator-editable parameter, and carries the UI
/// hints needed to build an editor for it.
/// </summary>
/// <remarks>
/// In the old app these hints lived in <c>MainForm.toolPropertyGridControl_CustomRowCreated</c>,
/// which switched on <c>FieldName</c> string literals - so "Threshold" got a
/// 0-255 trackbar no matter which tool it belonged to, and a new tool with a
/// differently-scaled Threshold would silently inherit the wrong range.
/// Putting the range on the property removes that coupling.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ParamAttribute : Attribute
{
    public ParamAttribute(string? displayName = null)
    {
        DisplayName = displayName;
    }

    public string? DisplayName { get; }

    public string? Description { get; set; }

    /// <summary>Optional group header in the inspector.</summary>
    public string? Group { get; set; }

    public double Min { get; set; } = double.NaN;

    public double Max { get; set; } = double.NaN;

    public double Step { get; set; } = double.NaN;

    public ParamEditor Editor { get; set; } = ParamEditor.Auto;

    /// <summary>
    /// Constrain to odd values. OpenCV kernel sizes and block sizes require
    /// this; the old code hand-rolled the same snapping logic in the setters of
    /// <c>AdaptiveThresholdTool.BlockSize</c> and <c>.Param</c>.
    /// </summary>
    public bool OddOnly { get; set; }

    /// <summary>
    /// File dialog filter for <see cref="ParamEditor.FilePath"/>, in the
    /// standard "Label|*.ext;*.ext2|..." form. Declared on the parameter so the
    /// shell does not need to know which node it is editing.
    /// </summary>
    public string? Filter { get; set; }

    /// <summary>
    /// Whether either end of a range may be left off, meaning no limit.
    /// </summary>
    /// <remarks>
    /// True for a filter, where "any area" has to be expressible and cannot
    /// be the end of a track - the useful end depends on the image, so a
    /// bound saved as "the area of the frame I was set up on" quietly
    /// becomes wrong when the camera changes. False for a quantity that is
    /// bounded by its nature, like a colour channel.
    /// </remarks>
    public bool AllowsNoLimit { get; set; }

    /// <summary>
    /// Whether a slider for this should span orders of magnitude rather than
    /// a flat scale.
    /// </summary>
    /// <remarks>
    /// Blob area runs from a few pixels to a few million. On a flat track
    /// every useful setting is in the first one percent of the travel, which
    /// is a control that cannot be used. A decade per equal step can.
    /// </remarks>
    public bool Logarithmic { get; set; }

    /// <summary>
    /// A property on the node supplying this parameter's label, for one
    /// whose meaning depends on the data.
    /// </summary>
    /// <remarks>
    /// The same idea as <c>MinSource</c> and <c>MaxSource</c>, and for the
    /// same reason: a colour range's three bands are blue, green and red
    /// for a camera frame and hue, saturation and value after a conversion.
    /// A fixed label would be wrong half the time.
    /// </remarks>
    public string? NameSource { get; set; }

    /// <summary>
    /// Expose this parameter as an optional input port as well as an editor
    /// field. When a wire is attached the upstream value wins; otherwise the
    /// value typed into the inspector is used.
    /// </summary>
    /// <remarks>
    /// This is what lets a threshold be a fixed number in one recipe and be
    /// driven by a measurement in another, without needing two different nodes.
    /// Only meaningful for values another node can produce - numbers and
    /// similar - not for enums or file paths.
    /// </remarks>
    public bool Connectable { get; set; }

    /// <summary>
    /// Name of another parameter supplying this one's lower bound, instead of
    /// the fixed <see cref="Min"/>.
    /// </summary>
    /// <remarks>
    /// An attribute value is a compile-time constant, so a range declared there
    /// cannot be adjusted by the operator. Pointing at another parameter lets a
    /// node expose its own limits - a value with editable Min and Max, whose
    /// slider spans whatever those are set to.
    /// </remarks>
    public string? MinSource { get; set; }

    /// <summary>Name of another parameter supplying this one's upper bound.</summary>
    public string? MaxSource { get; set; }

    /// <summary>
    /// Name of another property supplying this one's step.
    /// </summary>
    /// <remarks>
    /// The same idea as <see cref="MinSource"/>, for the case where how
    /// finely a value can be set depends on what kind of value it is: a
    /// Number node set to whole numbers steps by one, and the same node
    /// set to decimals does not.
    /// </remarks>
    public string? StepSource { get; set; }

    public bool HasMin => !double.IsNaN(Min);

    public bool HasMax => !double.IsNaN(Max);

    public bool HasStep => !double.IsNaN(Step);
}
