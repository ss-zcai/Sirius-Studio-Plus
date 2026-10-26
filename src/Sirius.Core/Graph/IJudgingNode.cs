namespace Sirius.Core.Graph;

/// <summary>
/// A node that passes or fails the part being looked at.
/// </summary>
/// <remarks>
/// <para>
/// What lets a verdict leave the recipe. A check publishes a boolean like
/// any other value, and a boolean on a wire is only worth having if
/// something reads it; nothing in the graph does, because the thing that
/// wants to know whether the part passed is outside the graph - the status
/// bar today, a line in a log or a signal on a machine later.
/// </para>
/// <para>
/// An interface rather than a check against the node's type, so the shell
/// does not have to know the node library: the one implementor today is
/// Limit Check, and anything that judges - a count, a presence test, a
/// match score - joins by implementing this rather than by being added to
/// a list somewhere.
/// </para>
/// </remarks>
public interface IJudgingNode
{
    /// <summary>The port carrying the verdict, read after a run.</summary>
    OutputPort VerdictPort { get; }
}
