namespace Sirius.Core.Graph;

/// <summary>
/// Which picture of a part an inspection is: the item it is of, its number in
/// the part's set, and whether it is the set's last (backlog F26).
/// </summary>
/// <remarks>
/// <para>
/// A part can take several pictures - one camera under different lights, two
/// cameras behind a beam splitter - judged together once all are taken
/// (`runtime-design.md` 14.2, decision 93). The PLC triggers each picture
/// and says which it is; the recipe runs on each as it comes, and a Collect
/// node keeps what each gave until the set is complete.
/// </para>
/// <para>
/// A picture with no shot - Studio editing, a single-picture station - is a
/// set of one: its own last.
/// </para>
/// </remarks>
/// <param name="Item">The part's ID: a new one starts a new set.</param>
/// <param name="Number">The picture's number in the set, from 1.</param>
/// <param name="IsLast">The set is complete with this picture: what follows a Collect runs.</param>
public sealed record ShotInfo(string Item, int Number, bool IsLast)
{
    /// <summary>A picture that is a set of its own.</summary>
    public static ShotInfo Single(string item = "") => new(item, 1, true);

    public override string ToString() => IsLast ? $"shot {Number}, the last, of {Item}" : $"shot {Number} of {Item}";
}

/// <summary>
/// A node that says how many shots make a part when the recipe runs at the
/// desk - Studio's Run Folder and Run Continuously - with no station to give
/// each inspection its shot: the Shot node's Shots per Part.
/// </summary>
public interface IShotSource
{
    /// <summary>Shots in a part, at the desk; 1 for a picture each.</summary>
    int ShotsPerPart { get; }
}
