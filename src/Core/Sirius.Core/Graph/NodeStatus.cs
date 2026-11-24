namespace Sirius.Core.Graph;

/// <summary>
/// Execution status of a single node. Mirrors the old <c>VisionToolStatus</c>
/// so existing operator habits (and the status icon set) carry over, with an
/// extra <see cref="Idle"/> state for nodes that have never been evaluated.
/// </summary>
public enum NodeStatus
{
    /// <summary>Never evaluated, or invalidated and not yet re-run.</summary>
    Idle = 0,

    /// <summary>Evaluated successfully.</summary>
    Ready = 1,

    /// <summary>Produced a result, but something was off (e.g. empty ROI).</summary>
    Warning = 2,

    /// <summary>Evaluation threw, or a required input was missing.</summary>
    Error = 3,
}
