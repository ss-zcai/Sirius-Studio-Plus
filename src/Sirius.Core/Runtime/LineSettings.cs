using System.Text.Json;
using System.Text.Json.Serialization;
using Sirius.Core.Graph;

namespace Sirius.Core.Runtime;

/// <summary>One setting changed on the line: its published name, its value, and when.</summary>
public sealed record LineSetting(string Name, JsonElement Value, DateTime Changed)
{
    /// <summary>"140", "true", "Dark" - the value as written in the file.</summary>
    public string ValueText => Value.ValueKind switch
    {
        JsonValueKind.String => Value.GetString() ?? string.Empty,
        JsonValueKind.Number => Value.GetDouble().ToString("0.###", System.Globalization.CultureInfo.CurrentCulture),
        JsonValueKind.True => "On",
        JsonValueKind.False => "Off",
        _ => Value.GetRawText(),
    };
}

/// <summary>
/// The settings changed on the line for one recipe, kept in a small file
/// beside it and applied whenever it is loaded to run (runtime design,
/// section 0: the recipe itself is not rewritten).
/// </summary>
/// <remarks>
/// <para>
/// By published name, so a value follows the setting through a reworked
/// recipe as a screen does, and a setting no longer published is reported
/// rather than put somewhere else. Each value is checked against the
/// setting's range again when it is applied: the recipe may have narrowed it
/// since.
/// </para>
/// <para>
/// Written whole on every change, through a temporary file, so a power cut
/// leaves the old file or the new one and never half of either. With no
/// changes left the file is removed, so a recipe without one runs exactly as
/// saved.
/// </para>
/// </remarks>
public sealed class LineSettings
{
    public const int CurrentVersion = 1;

    /// <summary>Added to the recipe's name, its own extension taken off: Top.sgraph keeps Top.line.json.</summary>
    public const string Suffix = ".line.json";

    private readonly List<LineSetting> _entries = [];

    private LineSettings(string path)
    {
        Path = path;
    }

    /// <summary>The file, whether or not it exists yet.</summary>
    public string Path { get; }

    public IReadOnlyList<LineSetting> Entries => _entries;

    /// <summary>The line settings file of the recipe at <paramref name="recipePath"/>.</summary>
    public static string PathFor(string recipePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipePath);

        var folder = System.IO.Path.GetDirectoryName(recipePath) ?? string.Empty;
        return System.IO.Path.Combine(folder, System.IO.Path.GetFileNameWithoutExtension(recipePath) + Suffix);
    }

    /// <summary>The settings for the recipe at <paramref name="recipePath"/>; none when it has no file.</summary>
    /// <param name="problem">Why the file there could not be read, or null. It is then treated as empty, and replaced by the next change.</param>
    public static LineSettings ForRecipe(string recipePath, out string? problem) => Load(PathFor(recipePath), out problem);

    /// <summary>The settings in the file at <paramref name="path"/>; none when there is no file.</summary>
    public static LineSettings Load(string path, out string? problem)
    {
        var settings = new LineSettings(path);
        problem = null;

        if (!File.Exists(path))
            return settings;

        try
        {
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Options)
                           ?? throw new InvalidDataException("The file is empty.");

            if (document.Version > CurrentVersion)
            {
                throw new InvalidDataException(
                    $"it was written by a newer version (format {document.Version}, this build reads {CurrentVersion})");
            }

            foreach (var entry in document.Settings ?? [])
            {
                if (string.IsNullOrWhiteSpace(entry.Name) || settings.Find(entry.Name) is not null)
                    continue;

                settings._entries.Add(new LineSetting(entry.Name.Trim(), entry.Value.Clone(), entry.Changed));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            settings._entries.Clear();
            problem = $"The settings changed on the line ({System.IO.Path.GetFileName(path)}) could not be read: {ex.Message}. The recipe runs as saved.";
        }

        return settings;
    }

    /// <summary>The entry for <paramref name="name"/>, ignoring case, or null.</summary>
    public LineSetting? Find(string name) =>
        _entries.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Records a value for <paramref name="name"/>, replacing any - in memory; <see cref="Save"/> writes it.</summary>
    /// <param name="value">A number, true or false, or a choice's name (<see cref="Tunable.ToStored"/>).</param>
    public void Set(string name, object? value, DateTime? changed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var entry = new LineSetting(name.Trim(), JsonSerializer.SerializeToElement(value), changed ?? DateTime.Now);
        var index = _entries.FindIndex(existing => string.Equals(existing.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
            _entries[index] = entry;
        else
            _entries.Add(entry);
    }

    /// <summary>Forgets <paramref name="name"/>'s value, so the recipe's own is used.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Remove(string name) =>
        _entries.RemoveAll(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>
    /// Puts the value <paramref name="entry"/> holds into the setting of that
    /// name in <paramref name="recipe"/>: taking a value tuned on the line
    /// back into the recipe itself.
    /// </summary>
    /// <returns>Why it could not be, or null.</returns>
    public static string? ApplyTo(VisionGraph recipe, LineSetting entry)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(entry);

        if (Tunable.Find(recipe, entry.Name) is not { } setting)
            return $"The recipe no longer publishes a setting '{entry.Name}'.";

        if (setting.Convert(entry.Value, out var value) is { } problem)
            return problem;

        setting.Apply(value);
        return null;
    }

    /// <summary>Writes the file - or removes it, with nothing in it.</summary>
    public void Save()
    {
        if (_entries.Count == 0)
        {
            if (File.Exists(Path))
                File.Delete(Path);

            return;
        }

        var document = new Document
        {
            Version = CurrentVersion,
            Settings = _entries
                .Select(entry => new EntryRecord { Name = entry.Name, Value = entry.Value, Changed = entry.Changed })
                .ToList(),
        };

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);

        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options));
        File.Move(temporary, Path, overwrite: true);
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private sealed class Document
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("settings")]
        public List<EntryRecord>? Settings { get; set; }
    }

    private sealed class EntryRecord
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public JsonElement Value { get; set; }

        [JsonPropertyName("changed")]
        public DateTime Changed { get; set; }
    }
}
