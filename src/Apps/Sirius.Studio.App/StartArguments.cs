namespace Sirius.Studio.App;

/// <summary>
/// What Studio was started with: a recipe to open, and - from Sirius
/// Builder's Open in Studio - the project and station it runs in, so the
/// station's camera is used (F29).
/// </summary>
/// <remarks>
/// <c>SiriusStudioPlus.exe recipe.sgraph --project line.sproj --station Top</c>.
/// A file dropped on the program is the recipe alone.
/// </remarks>
public sealed record StartArguments(string? Recipe, string? Project, string? Station)
{
    public static StartArguments Parse(IEnumerable<string> arguments)
    {
        string? recipe = null;
        string? project = null;
        string? station = null;

        using var each = arguments.GetEnumerator();

        while (each.MoveNext())
        {
            var argument = each.Current;

            if (string.Equals(argument, "--project", StringComparison.OrdinalIgnoreCase))
                project = each.MoveNext() ? each.Current : null;
            else if (string.Equals(argument, "--station", StringComparison.OrdinalIgnoreCase))
                station = each.MoveNext() ? each.Current : null;
            else if (!argument.StartsWith("--", StringComparison.Ordinal))
                recipe ??= argument;
        }

        return new StartArguments(recipe, project, station);
    }
}
