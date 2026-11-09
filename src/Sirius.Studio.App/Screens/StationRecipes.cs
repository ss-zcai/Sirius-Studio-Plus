using System.IO;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Screens;
using Sirius.Core.Serialization;

namespace Sirius.Studio.App.Screens;

/// <summary>
/// The recipes the screen designer binds widgets to, one per station.
/// </summary>
/// <remarks>
/// <para>
/// The station open in Studio's editor is answered with the editor's own
/// recipe, so a name published from the designer appears on its port at
/// once and is saved with the rest of that recipe's changes. Any other
/// station's recipe is read from its file, and a name published in it is
/// written back straight away - nothing else has it open.
/// </para>
/// <para>
/// Files read are kept until <see cref="Refresh"/>, which the designer calls
/// when it is brought back to the front: a recipe may have been saved in
/// Studio meanwhile.
/// </para>
/// </remarks>
public class StationRecipes
{
    private readonly Project _project;
    private readonly Func<Station?> _active;
    private readonly Func<VisionGraph?> _editor;
    private readonly Dictionary<Station, VisionGraph?> _read = [];

    /// <param name="active">The station whose recipe is in the editor, if any.</param>
    /// <param name="editor">The editor's recipe.</param>
    public StationRecipes(Project project, Func<Station?> active, Func<VisionGraph?> editor)
    {
        _project = project;
        _active = active;
        _editor = editor;
    }

    public Project Project => _project;

    public IReadOnlyList<string> StationNames => _project.Stations.Select(station => station.Name).ToArray();

    /// <summary>The station's recipe, or null when it cannot be read.</summary>
    public VisionGraph? Recipe(string? station)
    {
        if (station is null || _project.Find(station) is not { } found)
            return null;

        if (ReferenceEquals(found, _active()))
            return _editor();

        if (!_read.TryGetValue(found, out var recipe))
        {
            try
            {
                recipe = GraphSerializer.Load(_project.RecipePath(found)).Graph;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                recipe = null;
            }

            _read[found] = recipe;
        }

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
    /// After a name was published in <paramref name="recipe"/>: written to
    /// its file, unless it is the editor's, which is saved with the rest.
    /// </summary>
    /// <returns>Whether it was written.</returns>
    public virtual bool Published(string station, VisionGraph recipe)
    {
        if (_project.Find(station) is not { } found || ReferenceEquals(found, _active()))
            return false;

        GraphSerializer.Save(recipe, _project.RecipePath(found));
        return true;
    }

    /// <summary>Forgets the files read, so the next look reads them again.</summary>
    public void Refresh() => _read.Clear();
}
