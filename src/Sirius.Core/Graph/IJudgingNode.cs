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

/// <summary>
/// A judging node whose verdict is a grade - A, B, C or Reject - as well
/// as a pass or a fail.
/// </summary>
public interface IGradingNode : IJudgingNode
{
    /// <summary>The port carrying the <see cref="Nodes.Grade"/>, read after a run.</summary>
    OutputPort GradePort { get; }
}

/// <summary>
/// A node that keeps something across inspections - a count, a log - and so
/// must tell an inspection from a re-run after an edit.
/// </summary>
/// <remarks>
/// <para>
/// Moving a slider re-runs the recipe as often as the slider moves. A
/// counter that counted those runs would count the tuning, not the parts;
/// a log that logged them would fill with rows nobody inspected. So these
/// nodes act only on an inspection - a Run, a folder run, a camera frame -
/// which <see cref="EvalContext.IsInspection"/> says, and on any other run
/// publish what they already hold.
/// </para>
/// <para>
/// An inspection re-evaluates every one of them even when nothing upstream
/// changed: running the same image twice is two inspections.
/// </para>
/// </remarks>
public interface IInspectionNode
{
}
