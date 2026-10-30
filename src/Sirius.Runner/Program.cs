using System.Globalization;
using System.Text.Json;
using Sirius.Core.Graph;
using Sirius.Core.Serialization;
using Sirius.Vision;
using Sirius.Vision.Nodes;
using Sirius.Vision.Plugins;

namespace Sirius.Runner;

/// <summary>
/// Runs a recipe over every image of its Image Sequence - or a folder named
/// on the command line - and reports each verdict.
/// </summary>
/// <remarks>
/// <para>
/// The same run as the application's Run Folder (<see cref="FolderRun"/>):
/// each image an inspection, so the recipe's Counter counts it and its
/// Result Log writes it.
/// </para>
/// <para>
/// Exit code 0 when every image judged passed (or nothing in the recipe
/// judges), 1 when any failed, 2 when the run could not be made at all - a
/// bad argument, a recipe that would not load, OpenCV that would not.
/// </para>
/// </remarks>
public static class Program
{
    private const int AllPassed = 0;
    private const int SomeFailed = 1;
    private const int CouldNotRun = 2;

    private const string Usage =
        "Usage: sirius-run <recipe.sgraph> [--folder <folder>] [--quiet]\n\n" +
        "Runs the recipe over every image of its Image Sequence, each one an inspection,\n" +
        "and prints each verdict and a summary.\n\n" +
        "  --folder <folder>  Run over this folder instead of the one the recipe names.\n" +
        "  --quiet            Print the summary only.\n\n" +
        "Exit code: 0 all passed, 1 some failed, 2 could not run.";

    public static async Task<int> Main(string[] args)
    {
        if (!TryParse(args, out var recipe, out var folder, out var quiet, out var problem))
        {
            Console.Error.WriteLine(problem is null ? Usage : $"{problem}\n\n{Usage}");
            return problem is null ? AllPassed : CouldNotRun;
        }

        try
        {
            return await RunAsync(recipe!, folder, quiet).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not run: {ExceptionText.Describe(ex)}");
            return CouldNotRun;
        }
    }

    private static async Task<int> RunAsync(string recipe, string? folder, bool quiet)
    {
        if (NativeLibraries.Problem() is { } native)
        {
            Console.Error.WriteLine(native);
            return CouldNotRun;
        }

        VisionNodeLibrary.Register();
        LoadCustomNodes();

        var loaded = GraphSerializer.Load(recipe);

        foreach (var warning in loaded.Warnings)
            Console.Error.WriteLine($"warning: {warning}");

        var graph = loaded.Graph;

        if (FolderRun.SequenceIn(graph) is not { } sequence)
        {
            Console.Error.WriteLine("The recipe has no Image Sequence to run over. Add one in place of the Image File.");
            return CouldNotRun;
        }

        if (folder is not null)
        {
            if (sequence is not ImageSequenceNode images)
            {
                Console.Error.WriteLine("--folder needs the recipe's sequence to be an Image Sequence node.");
                return CouldNotRun;
            }

            images.SourcePath = folder;
        }

        if (sequence.Count == 0)
        {
            Console.Error.WriteLine("The Image Sequence has no images.");
            return CouldNotRun;
        }

        using var cancellation = new CancellationTokenSource();

        // Ctrl+C stops after the image in hand, so the log and the summary
        // still say what was done.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        using var scheduler = new GraphScheduler(graph);

        var summary = await FolderRun.RunAsync(
            graph,
            scheduler,
            sequence,
            item =>
            {
                if (!quiet)
                    Console.WriteLine(Line(item));

                return Task.CompletedTask;
            },
            cancellation.Token).ConfigureAwait(false);

        Console.WriteLine(summary.Describe());

        return summary.Failed > 0 ? SomeFailed : AllPassed;
    }

    /// <summary>"  12/120  part_012.png  FAIL  Reject".</summary>
    internal static string Line(FolderRunItem item)
    {
        var width = item.Of.ToString(CultureInfo.InvariantCulture).Length;

        var verdict = item.Verdict.Passed switch
        {
            true => "PASS",
            false => "FAIL",
            null => "-",
        };

        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/{1}  {2}  {3}",
            item.Index.ToString(CultureInfo.InvariantCulture).PadLeft(width),
            item.Of,
            item.Name ?? "?",
            verdict);

        if (item.Verdict.Grade is { } grade)
            line += $"  {grade.Name}";

        if (item.NodeErrors > 0)
            line += $"  ({item.NodeErrors} node error{(item.NodeErrors == 1 ? string.Empty : "s")})";

        return line;
    }

    /// <summary>
    /// The custom node libraries the application would load: shipped beside
    /// it, in this user's folder, and those added by hand.
    /// </summary>
    private static void LoadCustomNodes()
    {
        var settings = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SiriusStudioPlus");

        var paths = PluginLoader.FindLibraries(Path.Combine(AppContext.BaseDirectory, "plugins"))
            .Concat(PluginLoader.FindLibraries(Path.Combine(settings, "plugins")))
            .Concat(Remembered(Path.Combine(settings, "custom-nodes.json")))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            var library = PluginLoader.Install(path);

            foreach (var problem in library.Problems)
                Console.Error.WriteLine($"warning: {library.Title}: {problem}");
        }
    }

    private static IEnumerable<string> Remembered(string settingsFile)
    {
        try
        {
            if (!File.Exists(settingsFile))
                return [];

            using var document = JsonDocument.Parse(File.ReadAllText(settingsFile));

            return document.RootElement.TryGetProperty("Libraries", out var libraries)
                ? libraries.EnumerateArray().Select(entry => entry.GetString()).OfType<string>().ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"warning: could not read {settingsFile}: {ex.Message}");
            return [];
        }
    }

    internal static bool TryParse(
        string[] args, out string? recipe, out string? folder, out bool quiet, out string? problem)
    {
        recipe = null;
        folder = null;
        quiet = false;
        problem = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help" or "/?":
                    return false;

                case "--quiet":
                    quiet = true;
                    break;

                case "--folder" when i + 1 < args.Length:
                    folder = args[++i];
                    break;

                case "--folder":
                    problem = "--folder needs a folder after it.";
                    return false;

                case var other when other.StartsWith('-'):
                    problem = $"Unknown option {other}.";
                    return false;

                case var path when recipe is null:
                    recipe = path;
                    break;

                default:
                    problem = $"Only one recipe can be run at a time; {args[i]} is a second.";
                    return false;
            }
        }

        if (recipe is null)
            return false;

        if (!File.Exists(recipe))
        {
            problem = $"No recipe at {recipe}.";
            return false;
        }

        if (folder is not null && !Directory.Exists(folder))
        {
            problem = $"No folder at {folder}.";
            return false;
        }

        return true;
    }
}
