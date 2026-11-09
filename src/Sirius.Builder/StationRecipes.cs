using System.IO;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Screens;
using Sirius.Core.Serialization;

namespace Sirius.Builder;

/// <summary>
/// The recipes the screen designer binds widgets to, one per station - read
/// from their files, never written.
/// </summary>
/// <remarks>
/// <para>
/// Recipes are Studio's (decision 71): Builder binds only to what a recipe
/// publishes, so two programs never write the same file. A name missing is
/// published in Studio, and appears here once the recipe is saved there.
/// </para>
/// <para>
/// Files read are kept until <see cref="Refresh"/>, which Builder calls when
/// it is brought back to the front: a recipe may have been saved in Studio
/// meanwhile.
/// </para>
/// </remarks>
public sealed class StationRecipes
{
    private readonly Project _project;
    private readonly Dictionary<Station, (VisionGraph? Recipe, DateTime Written)> _read = [];

    public StationRecipes(Project project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
    }

    public Project Project => _project;

    public IReadOnlyList<string> StationNames => _project.Stations.Select(station => station.Name).ToArray();

    /// <summary>The station called <paramref name="name"/>, or null.</summary>
    public Station? Station(string? name) => name is null ? null : _project.Find(name);

    /// <summary>The station's recipe, or null when it cannot be read.</summary>
    public VisionGraph? Recipe(string? station)
    {
        if (Station(station) is not { } found)
            return null;

        var path = _project.RecipePath(found);
        var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

        if (_read.TryGetValue(found, out var cached) && cached.Written == written)
            return cached.Recipe;

        VisionGraph? recipe;

        try
        {
            recipe = GraphSerializer.Load(path).Graph;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            recipe = null;
        }

        _read[found] = (recipe, written);
        return recipe;
    }

    /// <summary>Every station with a readable recipe, for checking a screen.</summary>
    public IReadOnlyList<StationRecipe> All() =>
        _project.Stations
            .Select(station => (station.Name, Recipe: Recipe(station.Name)))
            .Where(pair => pair.Recipe is not null)
            .Select(pair => new StationRecipe(pair.Name, pair.Recipe!))
            .ToArray();

    /// <summary>
    /// Whether a recipe has been saved since it was read - in Studio,
    /// usually. The next look reads it again either way.
    /// </summary>
    public bool Refresh()
    {
        var changed = _read.Any(pair =>
        {
            var path = _project.RecipePath(pair.Key);
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            return written != pair.Value.Written;
        });

        _read.Clear();
        return changed;
    }
}
