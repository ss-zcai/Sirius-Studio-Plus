using Sirius.Core.Nodes;

namespace Sirius.Core.Graph;

/// <summary>What a recipe decided about a part, from the checks in it.</summary>
/// <param name="Passed">True or false; null when nothing in the recipe judges.</param>
/// <param name="Grade">The worst grade any final check gave, or null when none grades.</param>
/// <param name="Checks">How many checks the verdict is drawn from.</param>
/// <param name="Failed">How many of them failed.</param>
public readonly record struct Verdict(bool? Passed, Grade? Grade, int Checks, int Failed);

/// <summary>
/// Reading a recipe's verdict - for the status line, a folder run and the
/// command-line runner alike.
/// </summary>
public static class Verdicts
{
    /// <summary>
    /// The checks whose verdict nothing else judges further.
    /// </summary>
    /// <remarks>
    /// Three Limit Checks wired into Check Logic are one decision, not
    /// four: the recipe's verdict is Check Logic's, and counting the three
    /// as well would report a failure twice. So a check whose verdict
    /// reaches another enabled check, through any number of nodes, is left
    /// to that one.
    /// </remarks>
    public static IEnumerable<IJudgingNode> Final(IEnumerable<IJudgingNode> checks)
    {
        var all = checks.ToArray();

        return all.Where(check =>
        {
            var ports = check is IGradingNode grading
                ? new[] { check.VerdictPort, grading.GradePort }
                : [check.VerdictPort];

            var reached = new HashSet<VisionNode>();
            var pending = new Stack<VisionNode>(
                ports.SelectMany(port => port.Connections).Select(connection => connection.Target.Node));

            while (pending.TryPop(out var node))
            {
                if (!reached.Add(node))
                    continue;

                if (node is IJudgingNode && node.Enabled && !ReferenceEquals(node, check))
                    return false;

                foreach (var next in node.Outputs.SelectMany(output => output.Connections))
                    pending.Push(next.Target.Node);
            }

            return true;
        });
    }

    /// <summary>
    /// The verdict of <paramref name="checks"/>, reading each one's output
    /// through <paramref name="read"/>.
    /// </summary>
    /// <remarks>
    /// Anything that is not a plain true fails, which covers a check
    /// downstream of a node that errored: its verdict was never computed,
    /// and "no answer" is not a pass.
    /// </remarks>
    public static Verdict Of(IEnumerable<IJudgingNode> checks, Func<OutputPort, object?> read)
    {
        var all = checks.ToArray();

        if (all.Length == 0)
            return new Verdict(null, null, 0, 0);

        var failed = all.Count(check => read(check.VerdictPort) is not true);

        var grade = Grade.Worst(all
            .OfType<IGradingNode>()
            .Select(check => read(check.GradePort) as Grade)
            .OfType<Grade>());

        return new Verdict(failed == 0, grade, all.Length, failed);
    }

    /// <summary>The verdict of a whole recipe: its enabled final checks, in sub-graphs too.</summary>
    public static Verdict Of(VisionGraph graph, Func<OutputPort, object?> read) =>
        Of(Final(GraphTree.AllNodes(graph, enabledOnly: true).OfType<IJudgingNode>()), read);
}
