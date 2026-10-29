namespace Sirius.Core.Nodes;

/// <summary>
/// A verdict with more than two outcomes: A, B, C or Reject, say.
/// </summary>
/// <remarks>
/// <para>
/// Pass and fail is the two-grade case, so a yes/no from a check reads as
/// a grade wherever one is expected (<see cref="From"/>): true is
/// <see cref="Pass"/>, false is <see cref="Fail"/>.
/// </para>
/// <para>
/// A grade knows its place in the list it came from and whether it is
/// acceptable, so combining several needs nothing else: the worst one is
/// the result (<see cref="Worst"/>).
/// </para>
/// </remarks>
/// <param name="Name">What it is called - "A", "Reject".</param>
/// <param name="Rank">Its place in its list, best first, counting from zero.</param>
/// <param name="Passes">Whether a part with this grade is acceptable.</param>
public sealed record Grade(string Name, int Rank, bool Passes)
{
    /// <summary>A true yes/no, as a grade.</summary>
    public static readonly Grade Pass = new("Pass", 0, true);

    /// <summary>A false yes/no, as a grade.</summary>
    public static readonly Grade Fail = new("Fail", 1, false);

    /// <summary>
    /// A grade from anything a check produces: a grade, or a yes/no. Null
    /// for anything else, and for nothing at all.
    /// </summary>
    public static Grade? From(object? value) => value switch
    {
        Grade grade => grade,
        bool pass => pass ? Pass : Fail,
        _ => null,
    };

    /// <summary>
    /// The worst of <paramref name="grades"/>, or null when there are none.
    /// </summary>
    /// <remarks>
    /// Any failing grade is worse than every passing one, whatever lists
    /// they came from; between two that both pass or both fail, the one
    /// further down its list is worse. On a tie the first is kept, so the
    /// result names the earliest input that set it.
    /// </remarks>
    public static Grade? Worst(IEnumerable<Grade> grades)
    {
        Grade? worst = null;

        foreach (var grade in grades)
        {
            if (worst is null || IsWorse(grade, worst))
                worst = grade;
        }

        return worst;
    }

    private static bool IsWorse(Grade grade, Grade than) =>
        grade.Passes != than.Passes ? !grade.Passes : grade.Rank > than.Rank;

    public override string ToString() => Name;
}
