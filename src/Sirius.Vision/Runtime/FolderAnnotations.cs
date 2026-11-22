using Emgu.CV.CvEnum;
using Sirius.Core.Annotations;
using Sirius.Core.Runtime;

namespace Sirius.Vision.Runtime;

/// <summary>
/// A folder run's annotation files (F20a): Run Folder and
/// <c>sirius-run.exe</c> write a <c>.sann</c> beside each picture they run,
/// as a station writes one beside each picture it saves - so Sirius IV
/// shows what the recipe found on a folder judged at the desk too.
/// </summary>
/// <remarks>
/// <para>
/// A picture with a <c>.sann</c> already - a station's, being run again -
/// keeps what is about the picture: which station took it, its inspection,
/// when, the part, the bits it carries, and measurements drawn by hand.
/// What this run found replaces the rest: the recipe, the verdict, the
/// results and the shapes.
/// </para>
/// <para>
/// Written as each picture is judged, on the run's own thread: a file of a
/// few kilobytes, not worth a queue as a station's pictures are.
/// </para>
/// </remarks>
public sealed class FolderAnnotations
{
    private readonly string? _recipe;
    private readonly List<string> _problems = [];

    /// <param name="recipe">The recipe's file name, written into each.</param>
    public FolderAnnotations(string? recipe) => _recipe = recipe;

    /// <summary>Annotation files written.</summary>
    public int Written { get; private set; }

    /// <summary>What could not be written, and why - a read-only folder, say.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>"Annotations: 120 .sann files written beside the pictures." - or what went wrong.</summary>
    public string Describe() => _problems.Count == 0
        ? $"Annotations: {Written} .sann file{(Written == 1 ? string.Empty : "s")} written beside the pictures."
        : $"Annotations: {Written} written; {_problems.Count} could not be - {_problems[0]}";

    /// <summary>Writes the annotation file for one picture of the run; what goes wrong is kept, not thrown.</summary>
    public void Write(InspectionRecord record, string picture)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(picture);

        if (record.Acquired is not ImageData image)
            return;

        try
        {
            var found = ImageRecorder.Describe(record, image);
            var path = AnnotationFile.PathFor(picture);
            var before = File.Exists(path) ? AnnotationFile.TryLoad(path, out _) : null;

            // A 12-bit picture read back as 16: the file it had says 12.
            var bits = before is { Bits: > 0 } && image.Mat.Depth == DepthType.Cv16U && found.Bits == 16 ? before.Bits : found.Bits;

            new AnnotationFile
            {
                Image = Path.GetFileName(picture),
                Width = found.Width,
                Height = found.Height,
                Bits = bits,
                Station = before?.Station,
                Recipe = _recipe,
                Serial = before?.Serial ?? 0,
                Time = before?.Time ?? found.Time,
                Passed = found.Passed,
                Grade = found.Grade,
                NodeErrors = found.NodeErrors,
                Results = found.Results,
                Part = before?.Part ?? [],
                Layers = found.Layers,
                Measurements = before?.Measurements ?? [],
            }.Save(path);

            Written++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _problems.Add($"{Path.GetFileName(picture)}: {ex.Message}");
        }
    }
}
