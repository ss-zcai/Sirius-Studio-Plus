using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Sirius.Core.Graph;
using Sirius.Core.Nodes;
using Sirius.Core.Projects;
using Sirius.Plc;
using Sirius.Plc.Workflows;
using Sirius.Vision;
using Sirius.Vision.Nodes;

namespace Sirius.Tools.ShotSample;

/// <summary>What <see cref="ShotSampleProject.Write"/> made, and where.</summary>
public sealed record ShotSampleWritten(string ProjectFile, string Images, int ImageCount);

/// <summary>
/// A sample project of several shots a part (F26): a part pictured under
/// three lights, judged once from its three pictures.
/// </summary>
/// <remarks>
/// <para>
/// The made-up part is a bright disc lit from the left, the top and the
/// right in turn. Some parts have a dark mark that one light shows and the
/// others do not - so a part is only judged right from all three - and one
/// has a mark every light shows.
/// </para>
/// <para>
/// The recipe: Pictures (Image Sequence) → Marks (Find Blobs, dark, not
/// touching the border) → No Marks (Limit Check, at most 0) → Part Verdict
/// (Collect Verdicts) - and Shot, at 3 a part, for Studio's Run Folder;
/// Collect Images' average for one picture of the part.
/// </para>
/// </remarks>
public static class ShotSampleProject
{
    public const string Name = "Shots Sample";
    public const string Station = "Top";
    public const string Controller = "Line PLC";
    public const int Shots = 3;

    /// <summary>
    /// The made-up parts: for each, the shots that show a mark - none for
    /// a good part. Parts 2, 4 and 6 fail.
    /// </summary>
    public static IReadOnlyList<int[]> Parts { get; } =
    [
        [],
        [2],
        [],
        [3],
        [],
        [1, 2, 3],
    ];

    /// <summary>
    /// Writes the project into <paramref name="parent"/>\Shots Sample, with
    /// its pictures in an Images folder inside it - or, given
    /// <paramref name="images"/>, reading those instead.
    /// </summary>
    public static ShotSampleWritten Write(string parent, string? images = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);

        VisionNodeLibrary.Register();
        PlcNodeLibrary.Register();

        var folder = Path.GetFullPath(Path.Combine(parent, Name));
        var pictures = images is null ? Path.Combine(folder, "Images") : Path.GetFullPath(images);

        if (images is not null && !Directory.Exists(pictures))
            throw new ArgumentException($"There is no folder {pictures}.");

        var project = Project.Create(Path.GetFullPath(parent), Name, Recipe(pictures), Station);
        project.SetShotsPerPart(project.Stations[0], Shots);
        project.AddController(
            Controller,
            SimulatedPlcDriver.Key,
            Controller,
            path => WorkflowFile.Save(StarterWorkflow.For(Station, Shots), path, Controller));

        var count = images is null
            ? WritePictures(pictures)
            : new ImageSequenceNode { SourcePath = pictures }.Count;

        File.WriteAllText(Path.Combine(folder, "README.txt"), Readme(project.FilePath, pictures, images is not null));
        return new ShotSampleWritten(project.FilePath, pictures, count);
    }

    /// <summary>The station's recipe: each shot checked for marks, the part judged from all three.</summary>
    public static VisionGraph Recipe(string pictures)
    {
        var graph = new VisionGraph();

        var sequence = new ImageSequenceNode { Name = "Pictures", SourcePath = pictures, X = 40, Y = 120 };
        var shot = new ShotNode { ShotsPerPart = Shots, X = 40, Y = 340 };
        var marks = new BlobFilterNode
        {
            Name = "Marks",
            Polarity = BlobPolarity.Dark,
            Segmentation = BlobSegmentation.Fixed,
            Threshold = 70,
            Area = new ValueRange(15, 20000),
            RejectTouchingBorder = true,
            X = 300,
            Y = 120,
        };
        var noMarks = new LimitCheckNode { Name = "No Marks", Kind = LimitKind.AtMost, Maximum = 0, X = 560, Y = 120 };
        var verdict = new CollectVerdictsNode { Name = "Part Verdict", X = 820, Y = 120 };
        var shotView = new ViewerNode { Name = "Shot", X = 820, Y = 300 };
        var collect = new CollectImagesNode { Name = "Three Lights", X = 300, Y = 460 };
        var partView = new ViewerNode { Name = "Part (average)", X = 560, Y = 460 };

        foreach (var node in new VisionNode[] { sequence, shot, marks, noMarks, verdict, shotView, collect, partView })
            graph.Add(node);

        Connect(graph, sequence.ImageOut, marks.ImageIn);
        Connect(graph, marks.CountOut, noMarks.FindInput(nameof(LimitCheckNode.Value))!);
        Connect(graph, marks.ShapesOut, noMarks.AnnotationIn);
        Connect(graph, noMarks.PassOut, verdict.FindInput(nameof(CollectVerdictsNode.ShotPassed))!);
        Connect(graph, sequence.ImageOut, shotView.ImageIn);
        Connect(graph, noMarks.AnnotationOut, shotView.ShapesIn);
        Connect(graph, sequence.ImageOut, collect.ImageIn);
        Connect(graph, collect.AverageOut, partView.ImageIn);

        return graph;
    }

    private static void Connect(VisionGraph graph, OutputPort from, InputPort to)
    {
        var result = graph.TryConnect(from, to);

        if (!result.Success)
            throw new InvalidOperationException($"{from} -> {to}: {result}");
    }

    /// <summary>part01_1.png … part06_3.png: each part under the left, top and right light.</summary>
    private static int WritePictures(string folder)
    {
        Directory.CreateDirectory(folder);
        var count = 0;

        for (var part = 0; part < Parts.Count; part++)
        {
            for (var shot = 1; shot <= Shots; shot++)
            {
                using var picture = Picture(part, shot);
                CvInvoke.Imwrite(Path.Combine(folder, $"part{part + 1:00}_{shot}.png"), picture);
                count++;
            }
        }

        return count;
    }

    private const int Width = 640;
    private const int Height = 480;
    private const int Radius = 170;

    /// <summary>The part lit from one side: shot 1 the left, 2 the top, 3 the right.</summary>
    private static Image<Gray, byte> Picture(int part, int shot)
    {
        var picture = new Image<Gray, byte>(Width, Height, new Gray(18));
        var random = new Random(part * 10 + shot);
        var (cx, cy) = (Width / 2, Height / 2);

        // The light's direction across the disc: brighter on its side.
        var (lx, ly) = shot switch
        {
            1 => (-1.0, 0.0),
            2 => (0.0, -1.0),
            _ => (1.0, 0.0),
        };

        var data = picture.Data;

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var (dx, dy) = ((x - cx) / (double)Radius, (y - cy) / (double)Radius);
                var noise = random.Next(-6, 7);

                if (dx * dx + dy * dy > 1)
                {
                    data[y, x, 0] = (byte)Math.Clamp(18 + noise, 0, 255);
                    continue;
                }

                var lit = 165 + 55 * (dx * lx + dy * ly);
                data[y, x, 0] = (byte)Math.Clamp(lit + noise, 0, 255);
            }
        }

        if (Parts[part].Contains(shot))
            Mark(picture, part, shot);

        return picture;
    }

    /// <summary>A dark mark: a spot, or for part 4 a scratch that only a low light from the side shows.</summary>
    private static void Mark(Image<Gray, byte> picture, int part, int shot)
    {
        var (cx, cy) = (Width / 2, Height / 2);

        if (part == 3)
        {
            CvInvoke.Line(picture, new System.Drawing.Point(cx - 40, cy + 60), new System.Drawing.Point(cx + 35, cy + 95), new MCvScalar(35), 4, LineType.AntiAlias);
            return;
        }

        var at = new System.Drawing.Point(cx + 50 - 30 * shot, cy - 60 + 10 * part);
        CvInvoke.Circle(picture, at, 9, new MCvScalar(30), -1, LineType.AntiAlias);
    }

    private static string Readme(string projectFile, string pictures, bool ownPictures)
    {
        var expected = string.Join(", ", Parts.Select((marks, at) => $"part{at + 1:00} {(marks.Length == 0 ? "PASS" : "FAIL")}"));

        return
            $"""
            Shots Sample - several pictures a part, end to end
            ==================================================

            {Name}.sproj  the project: station {Station}, {Shots} shots a part, and {Controller} -
                          a simulated PLC with the 3-shot starter workflow.
            recipes\      {Station}'s recipe.
            {(ownPictures ? $"Pictures:     {pictures} (your own)." : "Images\\       the pictures: part01_1 .. part06_3, each part under three lights.")}

            {(ownPictures
                ? "The recipe's Marks node was tuned for the made-up pictures: tune it for yours in Studio."
                : $"Expected: {expected}. Parts 2 and 4 have a mark only one light shows.")}

            1. In Studio: open {Station}'s recipe (File > Open, recipes\{Station}).
               Run Folder: "18 images, 6 parts". Shots 1 and 2 of a part are kept;
               shot 3 judges it. The Shot viewer shows each picture's marks; the
               Part (average) viewer shows the three lights blended.
               Keep the Pictures node on image 1 when saving - the first
               trigger takes the picture the recipe was saved on.

            2. In Builder: open {Name}.sproj. Top says "3 shots a part"
               (right-click > Shots per Part to change it).
               Test Run (F5). In the {Controller} window: M0.0, Bool, true,
               Set in Simulator - shot 1. Click M0.0 to flip it off, then on
               again for the next. Each rise is the next shot:
                 M0.4 pulses "shot taken" for shots 1 and 2,
                 M0.1 is the part's result and M0.2 pulses "done" with shot 3.
               No Part ID is read, so the shots are counted into parts in
               order. On a line the PLC gives the Part ID (and the Shot):
               read them into the Part gather - name one "Part ID" - and wire
               a Shot read into Inspect's Shot.

            Made by tools\ShotSample in the Sirius Vision Desktop repository:
              dotnet run --project tools\ShotSample -- <parent folder> [--images <your sets>]
            {projectFile}
            """;
    }
}
