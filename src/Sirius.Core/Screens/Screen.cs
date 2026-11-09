using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sirius.Core.Screens;

/// <summary>What a widget on a screen is.</summary>
public enum WidgetKind
{
    /// <summary>Fixed text: a title, a label, an instruction.</summary>
    Text,

    /// <summary>A station's picture: a published view with what is drawn on it, or a published image result.</summary>
    Image,

    /// <summary>A station's PASS or FAIL, and its grade.</summary>
    Verdict,

    /// <summary>One published result: a number, a code read, a count.</summary>
    Value,

    /// <summary>A station's counts: total, passed, failed, yield.</summary>
    Counter,

    /// <summary>Does something: trigger, run or stop, reset counts.</summary>
    Button,

    /// <summary>A published setting, shown and - unless made read-only - changed on the line, within its limits.</summary>
    Setting,

    /// <summary>A picture file from the project: a logo.</summary>
    Logo,
}

/// <summary>What a button does.</summary>
public enum WidgetAction
{
    /// <summary>Inspects once.</summary>
    Trigger,

    /// <summary>Starts inspecting part after part, or stops.</summary>
    RunStop,

    /// <summary>Starts the counts again from zero.</summary>
    ResetCounts,
}

/// <summary>
/// One thing on a screen, where it sits and what it shows.
/// </summary>
/// <remarks>
/// Positions are in the screen's own units (<see cref="Screen.Width"/> by
/// <see cref="Screen.Height"/>), scaled as a whole to whatever display it is
/// shown on, so a layout made on one monitor looks the same on another.
/// </remarks>
public sealed class Widget
{
    [JsonPropertyName("kind")]
    public WidgetKind Kind { get; set; }

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }

    /// <summary>The station it shows or acts on; for a button, none means every station.</summary>
    [JsonPropertyName("station")]
    public string? Station { get; set; }

    /// <summary>The published name it shows: a view, a result, a setting.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>A Text widget's text; a Value's or Setting's label; a button's caption. Blank for the default.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("action")]
    public WidgetAction? Action { get; set; }

    [JsonPropertyName("fontSize")]
    public double FontSize { get; set; } = 16;

    /// <summary>How a number is written: "0.00", "0.#". Blank for up to three decimals.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    /// <summary>Written after a number: "mm", "px".</summary>
    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    /// <summary>A Logo's picture, relative to the project folder.</summary>
    [JsonPropertyName("file")]
    public string? File { get; set; }

    /// <summary>A Setting shown but not changed from this screen.</summary>
    [JsonPropertyName("readOnly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReadOnly { get; set; }
}

/// <summary>
/// A screen an operator sees: widgets laid out on a canvas, bound to what
/// the project's stations publish (runtime design, sections 0 and 7).
/// </summary>
/// <remarks>
/// <para>
/// Our own layout, as JSON, rather than XAML: a screen is checked against
/// the published names when the project opens, survives a version of this
/// application changing its controls, and can do nothing a screen should
/// not.
/// </para>
/// <para>
/// A screen <see cref="Generated"/> from the project is made again whenever
/// it is shown, so it follows stations and names as they are added; once it
/// has been edited by hand it is kept as it is.
/// </para>
/// </remarks>
public sealed class Screen
{
    public const int CurrentVersion = 1;

    /// <summary>A screen file's extension.</summary>
    public const string Extension = ".screen";

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Main";

    /// <summary>The canvas width in the screen's own units.</summary>
    [JsonPropertyName("width")]
    public double Width { get; set; } = 1280;

    [JsonPropertyName("height")]
    public double Height { get; set; } = 800;

    /// <summary>Made from the project rather than by hand, and made again each time it is shown.</summary>
    [JsonPropertyName("generated")]
    public bool Generated { get; set; }

    [JsonPropertyName("widgets")]
    public List<Widget> Widgets { get; set; } = [];

    public static Screen Load(string path)
    {
        var screen = JsonSerializer.Deserialize<Screen>(System.IO.File.ReadAllText(path), Options)
                     ?? throw new InvalidDataException("The screen file is empty.");

        if (screen.Version > CurrentVersion)
        {
            throw new InvalidDataException(
                $"This screen was saved by a newer version of Sirius Studio Plus " +
                $"(format {screen.Version}, this build reads {CurrentVersion}).");
        }

        return screen;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var temporary = path + ".tmp";
        System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
        System.IO.File.Move(temporary, path, overwrite: true);
    }

    /// <summary>How screens are written: indented, enums by name.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
