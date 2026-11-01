using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sirius.Core.Nodes;

namespace Sirius.Vision.Plugins;

/// <summary>Which Python runs a library, and why that one.</summary>
/// <param name="Executable">The interpreter.</param>
/// <param name="Arguments">Anything it needs before the script - "-3" for the py launcher.</param>
/// <param name="Source">Where it was found, for messages: "from sirius.json", "on PATH".</param>
public sealed record PythonInterpreter(string Executable, IReadOnlyList<string> Arguments, string Source)
{
    /// <summary>The file beside a library that names its interpreter.</summary>
    public const string SettingsFile = "sirius.json";

    /// <summary>
    /// The interpreter for a library, or a sentence saying why there is none.
    /// </summary>
    /// <remarks>
    /// In order: <c>sirius.json</c> in the library's folder, naming an
    /// interpreter or an environment folder; a <c>.venv</c> or <c>venv</c>
    /// beside it; <c>python.exe</c> on PATH; the <c>py</c> launcher. A
    /// library that needs a particular environment - CUDA, a pinned torch -
    /// should say so in <c>sirius.json</c> rather than trust PATH.
    /// </remarks>
    public static PythonInterpreter? Find(string script, out string? problem)
    {
        problem = null;
        var folder = Path.GetDirectoryName(Path.GetFullPath(script))!;
        var settings = Path.Combine(folder, SettingsFile);

        if (File.Exists(settings))
        {
            string? named;
            try
            {
                named = JsonNode.Parse(File.ReadAllText(settings))?["python"]?.GetValue<string>();
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                problem = $"{SettingsFile} beside it could not be read: {ex.Message}";
                return null;
            }

            if (!string.IsNullOrWhiteSpace(named))
            {
                var path = Path.GetFullPath(Path.Combine(folder, Environment.ExpandEnvironmentVariables(named)));

                if (InEnvironment(path) is { } executable)
                    return new PythonInterpreter(executable, [], $"from {SettingsFile}");

                problem = $"{SettingsFile} names {path} as its Python, which does not exist.";
                return null;
            }
        }

        foreach (var name in new[] { ".venv", "venv" })
        {
            if (InEnvironment(Path.Combine(folder, name)) is { } executable)
                return new PythonInterpreter(executable, [], $"the {name} beside it");
        }

        // An app execution alias - the python.exe in WindowsApps - starts
        // Python inside a package, where AppData is virtualised: it cannot
        // see a library in the per-user plugins folder. Any real install
        // on PATH comes first; the alias only when there is nothing else.
        foreach (var alias in new[] { false, true })
        {
            if (OnPath("python.exe", alias) is { } python)
                return new PythonInterpreter(python, [], "on PATH");

            if (OnPath("py.exe", alias) is { } launcher)
                return new PythonInterpreter(launcher, ["-3"], "the py launcher");
        }

        problem = "No Python was found. Install Python and add it to PATH, or put a " +
                  $"{SettingsFile} beside the library naming the one to use: " +
                  "{ \"python\": \"C:\\\\envs\\\\ocr\\\\python.exe\" }.";
        return null;
    }

    /// <summary>An interpreter itself, or the one inside an environment folder.</summary>
    private static string? InEnvironment(string path)
    {
        if (File.Exists(path))
            return path;

        if (!Directory.Exists(path))
            return null;

        // A venv keeps it in Scripts; a conda environment at its root.
        return new[] { Path.Combine(path, "Scripts", "python.exe"), Path.Combine(path, "python.exe"), Path.Combine(path, "bin", "python") }
            .FirstOrDefault(File.Exists);
    }

    private static string? OnPath(string file, bool alias) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(folder =>
        {
            try
            {
                return Path.Combine(Environment.ExpandEnvironmentVariables(folder.Trim('"')), file);
            }
            catch (ArgumentException)
            {
                return null;
            }
        })
        .Where(path => path is not null && IsAlias(path) == alias)
        .FirstOrDefault(File.Exists);

    internal static bool IsAlias(string path) =>
        path.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Executable} ({Source})";
}

/// <summary>
/// Custom node libraries written in Python: finding them, and loading them
/// by asking Python what they declare.
/// </summary>
/// <remarks>
/// A library is a <c>.py</c> file that imports <c>sirius_nodes</c> and
/// declares at least one <c>@node</c>. It runs in its own process, so what
/// it imports and what it costs to import are its own business - until it
/// fails, when the reason is reported in the Custom Nodes window and the
/// rest of the application carries on.
/// </remarks>
public static partial class PythonLibraries
{
    /// <summary>Folders that hold environments and caches, never libraries.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".venv", "venv", "env", ".env", "site-packages", "__pycache__", ".git", "node_modules", "Lib", "Scripts",
        ".pytest_cache", ".mypy_cache",
    };

    private static readonly WorkerTable Workers = new();

    public static bool IsPythonFile(string path) =>
        string.Equals(Path.GetExtension(path), ".py", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every Python library under <paramref name="folder"/>, at any depth,
    /// passing over environments, tests and the SDK itself.
    /// </summary>
    public static IReadOnlyList<string> Find(string folder)
    {
        if (!Directory.Exists(folder))
            return [];

        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(folder);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            try
            {
                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    if (!Skipped.Contains(Path.GetFileName(child)))
                        pending.Push(child);
                }

                found.AddRange(Directory.EnumerateFiles(current, "*.py").Where(IsLibrary));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be read holds nothing we can load.
            }
        }

        return found.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Whether a file looks like a library: it imports the SDK and declares a node.</summary>
    internal static bool IsLibrary(string path)
    {
        var name = Path.GetFileName(path);

        if (name.Equals("sirius_nodes.py", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("sirius_worker.py", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("conftest.py", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("test_", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var text = File.ReadAllText(path);
            return text.Contains("sirius_nodes", StringComparison.Ordinal) && NodeDecorator().IsMatch(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts a library's worker and describes its nodes, without adding
    /// them to the catalogue. The worker is kept running for its nodes.
    /// </summary>
    public static PluginLibrary Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var name = Path.GetFileName(fullPath);

        if (!File.Exists(fullPath))
            return new PluginLibrary(fullPath, name, null, [], ["The file does not exist."]);

        if (PythonInterpreter.Find(fullPath, out var problem) is not { } interpreter)
            return new PluginLibrary(fullPath, name, null, [], [problem!]);

        var worker = Workers.For(fullPath, interpreter);
        JsonObject hello;

        try
        {
            hello = worker.Start();
        }
        catch (PythonException ex)
        {
            return new PluginLibrary(fullPath, name, null, [], [ex.Message]);
        }

        var origin = $"{name} ({worker.PythonTitle})";
        var nodes = new List<NodeDescriptor>();
        var problems = new List<string>();

        foreach (var description in hello["nodes"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (PythonNodeType.TryCreate(description, worker, origin, problems) is { } type)
                nodes.Add(type.Descriptor);
        }

        if (nodes.Count == 0 && problems.Count == 0)
            problems.Add("The library loaded, but declares no nodes.");

        if (nodes.Count == 0)
            worker.Restart();

        return new PluginLibrary(fullPath, name, worker.PythonTitle, nodes, problems);
    }

    [GeneratedRegex(@"^\s*@\s*([A-Za-z_][A-Za-z0-9_]*\.)?node\s*\(", RegexOptions.Multiline)]
    private static partial Regex NodeDecorator();

    /// <summary>One worker per library file, for as long as the application runs.</summary>
    private sealed class WorkerTable
    {
        private readonly Dictionary<string, PythonWorker> _workers = new(StringComparer.OrdinalIgnoreCase);

        public PythonWorker For(string script, PythonInterpreter interpreter)
        {
            lock (_workers)
            {
                if (!_workers.TryGetValue(script, out var worker) || !string.Equals(worker.Interpreter.Executable, interpreter.Executable, StringComparison.OrdinalIgnoreCase))
                {
                    worker?.Dispose();
                    worker = new PythonWorker(script, interpreter);
                    _workers[script] = worker;
                }

                return worker;
            }
        }
    }
}
