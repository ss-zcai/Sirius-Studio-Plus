using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Shapes;

namespace Sirius.Core.Runtime;

/// <summary>What one image viewer in the recipe showed: its picture and the shapes drawn on it.</summary>
/// <param name="Name">The viewer node's name.</param>
/// <param name="Image">Its picture - the station's own buffer, valid only while the record is being taken.</param>
/// <param name="Shapes">Every set of shapes wired into it, in socket order.</param>
public sealed record ViewerRecord(string Name, object? Image, IReadOnlyList<ShapeSet> Shapes);

/// <summary>
/// An inspection, for keeping: what the station published, the picture it
/// took, and what each viewer drew.
/// </summary>
/// <param name="Acquired">
/// The picture the recipe's camera, image sequence or image file produced -
/// the station's own buffer, valid only while <see cref="IInspectionRecorder.Record"/>
/// runs. Null when the recipe has no such source.
/// </param>
public sealed record InspectionRecord(
    InspectionSnapshot Snapshot,
    string? Recipe,
    object? Acquired,
    IReadOnlyList<ViewerRecord> Viewers);

/// <summary>
/// Keeps a station's inspections - its pictures and their annotations - as
/// its <see cref="ImageSaving"/> says.
/// </summary>
/// <remarks>
/// <see cref="Record"/> is called on the station's thread after each
/// inspection the recorder <see cref="Wants"/>, and must be quick: it copies
/// what it needs and writes later, on a thread of its own, so a slow disk
/// never slows the line.
/// </remarks>
public interface IInspectionRecorder : IDisposable
{
    /// <summary>Whether an inspection with this verdict is kept.</summary>
    bool Wants(Verdict verdict, int nodeErrors);

    /// <summary>Takes a copy of what is to be kept, and returns.</summary>
    void Record(InspectionRecord record);

    /// <summary>Pictures written so far.</summary>
    long Saved { get; }

    /// <summary>What has stopped it saving, or made it skip pictures; null while all is well.</summary>
    string? Problem { get; }
}

/// <summary>Where the runtime finds a recorder for a station that saves its images.</summary>
/// <remarks>
/// Writing pictures needs the vision library, which the core does not
/// reference; it puts its recorder here when it registers.
/// </remarks>
public static class InspectionRecorders
{
    /// <summary>Makes the recorder for a station of a project, or null.</summary>
    public static Func<Project, Station, IInspectionRecorder?>? Factory { get; set; }
}
