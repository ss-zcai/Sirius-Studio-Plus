namespace Sirius.Core.Graph;

/// <summary>
/// A node's colouring on the canvas, chosen from a fixed set.
/// </summary>
/// <remarks>
/// <para>
/// For marking up a large recipe: the three nodes somebody is working on,
/// the branch that is only there for comparison, the one that fails on a
/// bad part. A fifty-node graph in one shade of grey makes that impossible
/// to say, and a comment node does not help because the thing being
/// commented on still looks like everything else.
/// </para>
/// <para>
/// A fixed set rather than two colour pickers. A picker invites a pale
/// background on a dark canvas with near-white text still inherited onto it,
/// which is how annotation turns into an unreadable graph; and anything that
/// can be chosen freely will eventually be chosen badly by somebody in a
/// hurry. Each member here names an intent, and what it looks like is the
/// theme's business - so the same recipe stays readable if a light theme is
/// ever added, which hand-picked hex would not.
/// </para>
/// <para>
/// A presentation choice, so it is held here but changes nothing about
/// evaluation. It is saved with the recipe, because marking up a graph is
/// work.
/// </para>
/// </remarks>
public enum NodeAccent
{
    /// <summary>The theme's ordinary node. What every node starts as.</summary>
    Default,

    /// <summary>
    /// Dimmed: still there, deliberately not interesting. For a branch kept
    /// for comparison, or a stage that is settled and in the way.
    /// </summary>
    Muted,

    /// <summary>Blue. No fixed meaning - the general-purpose marker.</summary>
    Blue,

    /// <summary>Teal.</summary>
    Teal,

    /// <summary>Green. Conventionally: known good, verified.</summary>
    Green,

    /// <summary>Amber. Conventionally: needs attention, in progress.</summary>
    Amber,

    /// <summary>Red. Conventionally: the problem, or the one not to touch.</summary>
    Red,

    /// <summary>Purple.</summary>
    Purple,
}
