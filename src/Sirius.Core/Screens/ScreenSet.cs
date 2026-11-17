using Sirius.Core.Projects;
using Sirius.Core.Runtime;

namespace Sirius.Core.Screens;

/// <summary>A screen by its name and kind - what a button can open, as the designer lists them.</summary>
public sealed record ScreenInfo(string Name, ScreenKind Kind, double Width, double Height);

/// <summary>
/// A project's screens: the start screen, <c>screens/Main.screen</c>, and
/// any number of pages and popups beside it, each a file in the screens
/// folder named after it (decision 79).
/// </summary>
/// <remarks>
/// <para>
/// The folder is the list: a screen file copied in is part of the project,
/// and nothing in the project file names them. Only the start screen is
/// ever made from the stations (<see cref="ScreenLayout.ScreenFor"/>);
/// every other screen is the integrator's.
/// </para>
/// <para>
/// A screen is opened by its name, from a button. A page takes the place
/// of the page shown; a popup opens over it, the page dimmed behind and
/// still running. Close on a popup closes it, and on a page goes back to
/// the page shown before.
/// </para>
/// </remarks>
public static class ScreenSet
{
    /// <summary>The file of the screen called <paramref name="name"/>.</summary>
    public static string PathFor(Project project, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Path.Combine(project.Folder, ScreenLayout.ScreensFolder, name.Trim() + Screen.Extension);
    }

    /// <summary>Whether <paramref name="name"/> is the start screen's.</summary>
    public static bool IsStart(string? name) =>
        string.Equals(name?.Trim(), Screen.StartName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The project's screens by name: the start screen first, whether or not
    /// its file has been written yet, then the others by name.
    /// </summary>
    public static IReadOnlyList<string> Names(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var folder = Path.Combine(project.Folder, ScreenLayout.ScreensFolder);
        var others = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*" + Screen.Extension)
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(name => !IsStart(name))
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToArray()
            : [];

        return [Screen.StartName, .. others];
    }

    /// <summary>
    /// Every screen, ready to show: the start screen as
    /// <see cref="ScreenLayout.ScreenFor"/> gives it, then each other one as
    /// saved, named after its file. One that cannot be read is left out and
    /// said so.
    /// </summary>
    public static IReadOnlyList<Screen> Load(Project project, IReadOnlyList<StationRunner> stations, out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(project);

        var found = new List<string>();
        var start = ScreenLayout.ScreenFor(project, stations, out var startProblem);

        if (startProblem is not null)
            found.Add(startProblem);

        // The start screen opens first, as a page, whatever its file says.
        start.Name = Screen.StartName;
        start.Kind = ScreenKind.Page;

        var screens = new List<Screen> { start };

        foreach (var name in Names(project).Skip(1))
        {
            try
            {
                var screen = Screen.Load(PathFor(project, name));
                screen.Name = name;
                screens.Add(screen);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                found.Add($"The screen {ScreenLayout.ScreensFolder}/{name}{Screen.Extension} could not be read ({ex.Message}); buttons opening it do nothing.");
            }
        }

        problems = found;
        return screens;
    }

    /// <summary>What is wrong with <paramref name="name"/> for a new screen, or one renamed from <paramref name="current"/>; null if nothing.</summary>
    public static string? NameProblem(Project project, string? name, string? current = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            return "A name is needed.";

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "A screen's name cannot contain \\ / : * ? \" < > |.";

        if (trimmed.EndsWith('.'))
            return "A screen's name cannot end with a full stop.";

        if (current is not null && string.Equals(trimmed, current.Trim(), StringComparison.Ordinal))
            return null;

        if (IsStart(trimmed))
            return $"{Screen.StartName} is the start screen's name.";

        // A change of case only is a rename, not a clash.
        var taken = Names(project).Any(other =>
            string.Equals(other, trimmed, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(other, current?.Trim(), StringComparison.OrdinalIgnoreCase));

        return taken ? $"There is a screen called {trimmed} already." : null;
    }

    /// <summary>A name for a new screen not yet taken: "Settings", "Settings 2"…</summary>
    public static string Suggest(Project project, string stem)
    {
        ArgumentNullException.ThrowIfNull(project);

        var names = Names(project);

        for (var i = 1; ; i++)
        {
            var name = i == 1 ? stem : $"{stem} {i}";

            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                return name;
        }
    }

    /// <summary>The size a new popup starts at.</summary>
    public const double PopupWidth = 640;

    /// <summary>The size a new popup starts at.</summary>
    public const double PopupHeight = 420;

    /// <summary>
    /// A new screen to start from: a title at the top and the way out - a
    /// Back button on a page, Close on a popup. A page is the start
    /// screen's size; a popup, smaller, and centred over it when shown.
    /// </summary>
    public static Screen NewScreen(string name, ScreenKind kind, double startWidth = 1280, double startHeight = 800)
    {
        var popup = kind == ScreenKind.Popup;
        var width = popup ? Math.Min(PopupWidth, startWidth) : startWidth;
        var height = popup ? Math.Min(PopupHeight, startHeight) : startHeight;

        return new Screen
        {
            Name = name.Trim(),
            Kind = kind,
            Width = width,
            Height = height,
            Widgets =
            [
                new Widget
                {
                    Kind = WidgetKind.Text,
                    X = 20,
                    Y = 10,
                    Width = width - 200,
                    Height = 40,
                    Text = name.Trim(),
                    FontSize = popup ? 20 : 24,
                },
                new Widget
                {
                    Kind = WidgetKind.Button,
                    X = width - 20 - 140,
                    Y = 10,
                    Width = 140,
                    Height = 40,
                    Action = WidgetAction.Close,
                    Text = popup ? "Close" : "Back",
                },
            ],
        };
    }

    /// <summary>The caption a Close button shows unless it is given one: Close on a popup, Back on a page.</summary>
    public static string CloseCaption(ScreenKind kind) => kind == ScreenKind.Popup ? "Close" : "Back";

    /// <summary>
    /// What is wrong with the way <paramref name="screen"/> opens others:
    /// a button opening no screen, or one that is not there; a popup too big
    /// to show over the start screen.
    /// </summary>
    /// <param name="screens">Every screen of the project, this one among them.</param>
    public static IReadOnlyList<string> NavigationProblems(Screen screen, IReadOnlyList<ScreenInfo> screens)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(screens);

        var problems = new List<string>();

        foreach (var button in screen.Widgets.Where(widget => widget.Kind == WidgetKind.Button && widget.Action == WidgetAction.OpenScreen))
        {
            var label = string.IsNullOrWhiteSpace(button.Text) ? "Button" : $"Button '{button.Text}'";

            if (string.IsNullOrWhiteSpace(button.Screen))
                problems.Add($"{label}: no screen to open is chosen.");
            else if (!screens.Any(other => string.Equals(other.Name, button.Screen, StringComparison.OrdinalIgnoreCase)))
                problems.Add($"{label}: there is no screen '{button.Screen}'.");
        }

        if (screen.Kind == ScreenKind.Popup && !IsStart(screen.Name) &&
            screens.FirstOrDefault(other => IsStart(other.Name)) is { } start &&
            (screen.Width > start.Width || screen.Height > start.Height))
        {
            problems.Add($"The popup is larger than {Screen.StartName} ({start.Width:0} × {start.Height:0}): what is outside it is cut off.");
        }

        return problems;
    }

    /// <summary>
    /// Everything wrong with every screen - bindings and navigation - each
    /// line saying which screen when there is more than one.
    /// </summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<Screen> screens, IReadOnlyList<StationRunner> stations)
    {
        ArgumentNullException.ThrowIfNull(screens);
        ArgumentNullException.ThrowIfNull(stations);

        var infos = screens.Select(Info).ToArray();
        var problems = new List<string>();

        foreach (var screen in screens)
        {
            var own = ScreenLayout.Problems(screen, stations).Concat(NavigationProblems(screen, infos));
            problems.AddRange(screens.Count == 1 ? own : own.Select(problem => $"{screen.Name}: {problem}"));
        }

        return problems;
    }

    /// <summary>A screen's name, kind and size.</summary>
    public static ScreenInfo Info(Screen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        return new ScreenInfo(screen.Name, IsStart(screen.Name) ? ScreenKind.Page : screen.Kind, screen.Width, screen.Height);
    }

    /// <summary>
    /// Points every Open Screen button of <paramref name="screen"/> that
    /// opens <paramref name="from"/> at <paramref name="to"/> instead.
    /// </summary>
    /// <returns>Whether any did.</returns>
    public static bool Retarget(Screen screen, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(screen);

        var changed = false;

        foreach (var button in screen.Widgets.Where(widget => string.Equals(widget.Screen, from, StringComparison.OrdinalIgnoreCase)))
        {
            button.Screen = to;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// After a station was renamed: the widgets showing it, in every screen
    /// file but those in <paramref name="leave"/> - open in a designer,
    /// which changes its own.
    /// </summary>
    /// <returns>The screens whose widgets were changed.</returns>
    public static IReadOnlyList<string> RenameStation(Project project, string from, string to, IReadOnlyCollection<string>? leave = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        var changed = new List<string>();

        foreach (var name in Names(project))
        {
            var path = PathFor(project, name);

            if ((leave?.Contains(name, StringComparer.OrdinalIgnoreCase) ?? false) || !File.Exists(path))
                continue;

            try
            {
                var screen = Screen.Load(path);
                var showing = screen.Widgets.Where(widget => string.Equals(widget.Station, from, StringComparison.OrdinalIgnoreCase)).ToArray();

                if (showing.Length == 0)
                    continue;

                foreach (var widget in showing)
                    widget.Station = to;

                screen.Save(path);
                changed.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                // Left as it is; its check says the station is not there.
            }
        }

        return changed;
    }

    /// <summary>
    /// Renames a screen: its file, its name inside it, and the buttons in
    /// the other screens' files that open it - all but those in
    /// <paramref name="leave"/>, which the caller changes itself (open in a
    /// designer, with changes not yet saved).
    /// </summary>
    /// <returns>The screens whose buttons were pointed at the new name.</returns>
    public static IReadOnlyList<string> Rename(Project project, string from, string to, IReadOnlyCollection<string>? leave = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (IsStart(from))
            throw new ArgumentException($"{Screen.StartName} is the start screen and keeps its name.", nameof(from));

        if (NameProblem(project, to, from) is { } problem)
            throw new ArgumentException(problem, nameof(to));

        to = to.Trim();
        var source = PathFor(project, from);
        var target = PathFor(project, to);

        if (File.Exists(source))
        {
            var screen = Screen.Load(source);
            screen.Name = to;
            screen.Save(target);

            // A change of case only: the same file, already written.
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Delete(source);
        }

        var pointed = new List<string>();

        foreach (var name in Names(project))
        {
            if (string.Equals(name, to, StringComparison.OrdinalIgnoreCase) ||
                (leave?.Contains(name, StringComparer.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }

            var path = PathFor(project, name);

            if (!File.Exists(path))
                continue;

            try
            {
                var other = Screen.Load(path);

                if (Retarget(other, from, to))
                {
                    other.Save(path);
                    pointed.Add(name);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                // Left as it is; its check says the screen it opens is not there.
            }
        }

        return pointed;
    }
}
