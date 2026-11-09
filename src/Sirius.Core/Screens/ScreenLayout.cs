using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;

namespace Sirius.Core.Screens;

/// <summary>
/// Makes, finds and checks a project's screen.
/// </summary>
/// <remarks>
/// <para>
/// The MVP has one screen per project, <c>screens/Main.screen</c>. Until the
/// designer (MVP step 5) edits it, it is generated from what each station
/// publishes: a column per station - its name, its picture, PASS/FAIL and
/// counts, its own Trigger and Run buttons, then its results and settings -
/// under a title and buttons acting on every station.
/// </para>
/// </remarks>
public static class ScreenLayout
{
    /// <summary>The folder screens are kept in, inside the project's.</summary>
    public const string ScreensFolder = "screens";

    private const double Margin = 16;
    private const double Gap = 8;
    private const double Row = 34;

    /// <summary>The project's screen file.</summary>
    public static string MainScreenPath(Project project) =>
        Path.Combine(project.Folder, ScreensFolder, "Main" + Screen.Extension);

    /// <summary>
    /// The screen to show: the saved one if it was edited by hand, otherwise
    /// one generated now from what the stations publish - and saved, so
    /// there is a file to look at and, later, to edit.
    /// </summary>
    /// <param name="problem">Why a saved screen could not be read, when one was replaced by a generated one.</param>
    public static Screen ScreenFor(Project project, IReadOnlyList<StationRunner> stations, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(project);

        var path = MainScreenPath(project);
        problem = null;

        if (File.Exists(path))
        {
            try
            {
                var saved = Screen.Load(path);

                if (!saved.Generated)
                    return saved;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                problem = $"The screen {ScreensFolder}/{Path.GetFileName(path)} could not be read ({ex.Message}); a standard one is shown instead.";
                return Standard(project.Name, stations);
            }
        }

        var generated = Standard(project.Name, stations);

        try
        {
            generated.Save(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Shown all the same; there is just no file of it.
        }

        return generated;
    }

    /// <summary>The standard screen for running <paramref name="stations"/>.</summary>
    public static Screen Standard(string title, IReadOnlyList<StationRunner> stations) =>
        Standard(title, Recipes(stations));

    /// <summary>The standard screen for stations by name and recipe - what the designer resets to.</summary>
    public static Screen Standard(string title, IReadOnlyList<StationRecipe> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);

        var screen = new Screen { Generated = true };
        var widgets = screen.Widgets;

        // ---- the top: title, and buttons for every station -------------

        widgets.Add(new Widget
        {
            Kind = WidgetKind.Text,
            X = Margin,
            Y = 12,
            Width = 560,
            Height = 40,
            Text = title,
            FontSize = 24,
        });

        var actions = new (WidgetAction Action, string Caption)[]
        {
            (WidgetAction.Trigger, "Trigger All"),
            (WidgetAction.RunStop, "Run All"),
            (WidgetAction.ResetCounts, "Reset Counts"),
        };

        const double buttonWidth = 140;

        for (var i = 0; i < actions.Length; i++)
        {
            widgets.Add(new Widget
            {
                Kind = WidgetKind.Button,
                X = screen.Width - Margin - ((actions.Length - i) * buttonWidth) - ((actions.Length - i - 1) * Gap),
                Y = 14,
                Width = buttonWidth,
                Height = 36,
                Action = actions[i].Action,
                Text = actions[i].Caption,
            });
        }

        if (stations.Count == 0)
        {
            widgets.Add(new Widget
            {
                Kind = WidgetKind.Text,
                X = Margin,
                Y = 80,
                Width = screen.Width - (2 * Margin),
                Height = 40,
                Text = "This project has no stations yet.",
            });

            return screen;
        }

        // ---- a column per station --------------------------------------

        var top = 66.0;
        var bottom = screen.Height - Margin;
        var width = (screen.Width - (Margin * (stations.Count + 1))) / stations.Count;

        for (var column = 0; column < stations.Count; column++)
        {
            var station = stations[column];
            var x = Margin + (column * (width + Margin));
            var y = top;
            var items = Publishing.In(station.Recipe).Where(item => item.Found).ToArray();

            widgets.Add(new Widget
            {
                Kind = WidgetKind.Text,
                X = x,
                Y = y,
                Width = width,
                Height = 30,
                Station = station.Name,
                Text = station.Name,
                FontSize = 18,
            });

            y += 30 + Gap;

            if (PictureOf(items) is { } picture)
            {
                var height = Math.Round((bottom - top) * 0.55);

                widgets.Add(new Widget
                {
                    Kind = WidgetKind.Image,
                    X = x,
                    Y = y,
                    Width = width,
                    Height = height,
                    Station = station.Name,
                    Name = picture,
                });

                y += height + Gap;
            }

            var verdictWidth = Math.Round(width * 0.4);

            widgets.Add(new Widget
            {
                Kind = WidgetKind.Verdict,
                X = x,
                Y = y,
                Width = verdictWidth,
                Height = 60,
                Station = station.Name,
                FontSize = 26,
            });

            widgets.Add(new Widget
            {
                Kind = WidgetKind.Counter,
                X = x + verdictWidth + Gap,
                Y = y,
                Width = width - verdictWidth - Gap,
                Height = 60,
                Station = station.Name,
                FontSize = 14,
            });

            y += 60 + Gap;

            var half = (width - Gap) / 2;

            widgets.Add(new Widget
            {
                Kind = WidgetKind.Button,
                X = x,
                Y = y,
                Width = half,
                Height = 34,
                Station = station.Name,
                Action = WidgetAction.Trigger,
                Text = "Trigger",
            });

            widgets.Add(new Widget
            {
                Kind = WidgetKind.Button,
                X = x + half + Gap,
                Y = y,
                Width = half,
                Height = 34,
                Station = station.Name,
                Action = WidgetAction.RunStop,
                Text = "Run",
            });

            y += 34 + Gap;

            // Results, then settings, as many as fit.
            foreach (var item in items.Where(item => IsListed(item)).OrderBy(item => item.Kind == PublishedKind.Setting))
            {
                if (y + Row > bottom)
                    break;

                widgets.Add(new Widget
                {
                    Kind = item.Kind == PublishedKind.Setting ? WidgetKind.Setting : WidgetKind.Value,
                    X = x,
                    Y = y,
                    Width = width,
                    Height = Row - 4,
                    Station = station.Name,
                    Name = item.Name,
                    FontSize = 15,
                });

                y += Row;
            }
        }

        return screen;
    }

    /// <summary>
    /// What would be wrong with showing <paramref name="screen"/> - a
    /// station or name it binds to that is not there - "Value 'Diameter':
    /// station 'Top' publishes no 'Diameter'".
    /// </summary>
    public static IReadOnlyList<string> Problems(Screen screen, IReadOnlyList<StationRunner> stations) =>
        Problems(screen, Recipes(stations));

    /// <summary>The same, against stations by name and recipe - what the designer checks as it goes.</summary>
    public static IReadOnlyList<string> Problems(Screen screen, IReadOnlyList<StationRecipe> stations)
    {
        ArgumentNullException.ThrowIfNull(screen);

        var problems = new List<string>();

        foreach (var widget in screen.Widgets)
        {
            var label = string.IsNullOrWhiteSpace(widget.Name) ? $"{widget.Kind}" : $"{widget.Kind} '{widget.Name}'";

            if (widget.Station is null)
            {
                if (NeedsStation(widget.Kind))
                    problems.Add($"{label}: no station is named.");

                continue;
            }

            var station = stations.FirstOrDefault(s => string.Equals(s.Name, widget.Station, StringComparison.OrdinalIgnoreCase));

            if (station is null)
            {
                problems.Add($"{label}: there is no station '{widget.Station}'.");
                continue;
            }

            if (!NeedsName(widget.Kind))
                continue;

            if (string.IsNullOrWhiteSpace(widget.Name))
            {
                problems.Add($"{label} on '{station.Name}': no published name is chosen.");
                continue;
            }

            if (Publishing.Find(station.Recipe, widget.Name) is not { Found: true } item)
                problems.Add($"{label}: station '{station.Name}' publishes no '{widget.Name}'.");
            else if (widget.Kind == WidgetKind.Setting && item.Kind != PublishedKind.Setting)
                problems.Add($"{label}: on '{station.Name}' it is a {item.Kind.ToString().ToLowerInvariant()}, not a setting.");
            else if (widget.Kind == WidgetKind.Image && item.Kind == PublishedKind.Setting)
                problems.Add($"{label}: on '{station.Name}' it is a setting, not a picture.");
        }

        return problems;
    }

    private static StationRecipe[] Recipes(IReadOnlyList<StationRunner> stations) =>
        stations.Select(station => new StationRecipe(station.Name, station.Recipe)).ToArray();

    /// <summary>
    /// A new widget of <paramref name="kind"/> at (<paramref name="x"/>,
    /// <paramref name="y"/>), of a size that suits it - what the designer
    /// puts down when one is added.
    /// </summary>
    public static Widget NewWidget(WidgetKind kind, double x, double y, string? station = null)
    {
        var (width, height, fontSize) = kind switch
        {
            WidgetKind.Image => (400.0, 300.0, 16.0),
            WidgetKind.Verdict => (180.0, 60.0, 26.0),
            WidgetKind.Counter => (300.0, 60.0, 14.0),
            WidgetKind.Button => (140.0, 36.0, 16.0),
            WidgetKind.Logo => (160.0, 60.0, 16.0),
            WidgetKind.Text => (240.0, 36.0, 18.0),
            _ => (300.0, 30.0, 15.0),
        };

        return new Widget
        {
            Kind = kind,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            FontSize = fontSize,
            Station = NeedsStation(kind) || kind == WidgetKind.Button ? station : null,
            Action = kind == WidgetKind.Button ? WidgetAction.Trigger : null,
            Text = kind == WidgetKind.Text ? "Text" : null,
        };
    }

    /// <summary>A copy of <paramref name="widget"/>, sharing nothing with it.</summary>
    public static Widget Copy(Widget widget) =>
        System.Text.Json.JsonSerializer.Deserialize<Widget>(
            System.Text.Json.JsonSerializer.Serialize(widget, Screen.Options), Screen.Options)!;

    /// <summary>Whether a widget of this kind shows a station's values.</summary>
    public static bool NeedsStation(WidgetKind kind) =>
        kind is WidgetKind.Image or WidgetKind.Verdict or WidgetKind.Value or WidgetKind.Counter or WidgetKind.Setting;

    /// <summary>Whether a widget of this kind shows one published name.</summary>
    public static bool NeedsName(WidgetKind kind) =>
        kind is WidgetKind.Image or WidgetKind.Value or WidgetKind.Setting;

    /// <summary>The first published view, or else the first published image.</summary>
    private static string? PictureOf(IReadOnlyList<PublishedItem> items) =>
        items.FirstOrDefault(item => item.Kind == PublishedKind.View)?.Name ??
        items.FirstOrDefault(IsImage)?.Name;

    /// <summary>A result whose output carries an image, by the type's name - Core does not know the image type.</summary>
    internal static bool IsImage(PublishedItem item) =>
        item.Kind == PublishedKind.Result &&
        item.Node.FindOutput(item.Entry.Member)?.DataType.Name == "ImageData";

    /// <summary>Results that are not pictures, and settings, go in the list under the picture.</summary>
    private static bool IsListed(PublishedItem item) =>
        item.Kind == PublishedKind.Setting || (item.Kind == PublishedKind.Result && !IsImage(item));
}

/// <summary>A station by its name and recipe, running or not.</summary>
public sealed record StationRecipe(string Name, VisionGraph Recipe);
