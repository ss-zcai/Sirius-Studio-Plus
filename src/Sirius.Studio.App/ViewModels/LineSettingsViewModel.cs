using System.Globalization;
using System.IO;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Core.Serialization;

namespace Sirius.Studio.App.ViewModels;

/// <summary>One setting changed on the line, beside what the recipe has.</summary>
public sealed record LineSettingRow(LineSetting Entry, string Source, string InRecipe)
{
    public string Name => Entry.Name;

    public string OnLine => Entry.ValueText;

    public string Changed => Entry.Changed.ToString("g", CultureInfo.CurrentCulture);
}

/// <summary>
/// A station's settings changed on the line, for the engineer: seen beside
/// the recipe's own values, taken into the recipe, or forgotten (runtime MVP
/// step 6).
/// </summary>
/// <remarks>
/// <para>
/// Without this, a recipe opened in Studio would show one threshold while the
/// line runs another. Taking a value into the recipe changes the recipe and
/// forgets the line's entry, so the two agree again.
/// </para>
/// <para>
/// The station open in the editor takes it into the editor's recipe, to be
/// saved with the rest of its changes - and undone like any of them. Any
/// other station's recipe file is changed and saved at once.
/// </para>
/// </remarks>
public sealed class LineSettingsViewModel
{
    private readonly Project _project;
    private readonly Station _station;
    private readonly VisionGraph? _editor;
    private LineSettings _line;

    /// <param name="editor">The editor's recipe when it is this station's; null otherwise.</param>
    public LineSettingsViewModel(Project project, Station station, VisionGraph? editor)
    {
        _project = project;
        _station = station;
        _editor = editor;
        _line = Load();
        Rows = BuildRows();
    }

    public string StationName => _station.Name;

    /// <summary>Why the file could not be read, or null.</summary>
    public string? Problem { get; private set; }

    public IReadOnlyList<LineSettingRow> Rows { get; private set; }

    /// <summary>Whether the recipe being changed is the editor's, so needs saving there.</summary>
    public bool ChangesEditor => _editor is not null;

    /// <summary>
    /// Puts the line's values of <paramref name="rows"/> into the recipe and
    /// forgets them on the line.
    /// </summary>
    /// <returns>What could not be taken, one line each; empty when all were.</returns>
    public IReadOnlyList<string> TakeIntoRecipe(IEnumerable<LineSettingRow> rows)
    {
        var problems = new List<string>();
        var recipe = _editor ?? GraphSerializer.Load(_project.RecipePath(_station)).Graph;
        var taken = 0;

        foreach (var row in rows)
        {
            if (LineSettings.ApplyTo(recipe, row.Entry) is { } problem)
            {
                problems.Add($"{row.Name}: {problem}");
                continue;
            }

            _line.Remove(row.Name);
            taken++;
        }

        if (taken > 0)
        {
            if (_editor is null)
                GraphSerializer.Save(recipe, _project.RecipePath(_station));

            _line.Save();
        }

        Rows = BuildRows();
        return problems;
    }

    /// <summary>Forgets the line's values of <paramref name="rows"/>: the recipe's own are used from the next start.</summary>
    public void Forget(IEnumerable<LineSettingRow> rows)
    {
        foreach (var row in rows)
            _line.Remove(row.Name);

        _line.Save();
        Rows = BuildRows();
    }

    private LineSettings Load()
    {
        var line = LineSettings.ForRecipe(_project.RecipePath(_station), out var problem);
        Problem = problem;
        return line;
    }

    private IReadOnlyList<LineSettingRow> BuildRows()
    {
        VisionGraph? recipe = _editor;

        if (recipe is null)
        {
            try
            {
                recipe = GraphSerializer.Load(_project.RecipePath(_station)).Graph;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                recipe = null;
            }
        }

        return _line.Entries
            .Select(entry =>
            {
                var setting = recipe is null ? null : Tunable.Find(recipe, entry.Name);
                var inRecipe = setting is null ? "not published" : Show(setting.Value);
                return new LineSettingRow(entry, setting?.Source ?? string.Empty, inRecipe);
            })
            .ToArray();
    }

    private static string Show(object? value) => value switch
    {
        null => "—",
        bool on => on ? "On" : "Off",
        double or float or decimal => Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("0.###", CultureInfo.CurrentCulture),
        _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? "—",
    };

    /// <summary>How many settings a station has changed on the line - for the Project panel.</summary>
    public static int CountFor(Project project, Station station)
    {
        var path = LineSettings.PathFor(project.RecipePath(station));

        if (!File.Exists(path))
            return 0;

        return LineSettings.Load(path, out _).Entries.Count;
    }
}
