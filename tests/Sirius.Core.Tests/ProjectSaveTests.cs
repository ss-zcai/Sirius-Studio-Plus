using System.IO;
using Sirius.Studio.App.ViewModels;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Saving a recipe, and the panel arrangement that belongs with it.
/// </summary>
/// <remarks>
/// Reopening a project used to bring every viewer back in one tab stack,
/// because the layout was restored once at startup - before the graph that
/// owns those panels existed, so there was nothing to place.
/// </remarks>
public class ProjectSaveTests : IDisposable
{
    private readonly string _folder;

    public ProjectSaveTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "sirius-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a test run over.
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The view-model drives WPF, so it is built on a single-threaded
    /// apartment as the application builds it.
    /// </summary>
    private static void OnStaThread(Action action)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void Saving_writes_the_recipe()
    {
        OnStaThread(() =>
        {
            var path = Path.Combine(_folder, "recipe.sgraph");

            new MainViewModel().Write(path);

            Assert.True(File.Exists(path));
        });
    }

    /// <summary>
    /// The arrangement goes beside the recipe, not into a single shared file,
    /// so two projects can be laid out differently.
    /// </summary>
    [Fact]
    public void Saving_asks_for_the_layout_beside_it()
    {
        OnStaThread(() =>
        {
            var model = new MainViewModel();
            string? requested = null;
            model.LayoutSaveRequested += (_, path) => requested = path;

            model.Write(Path.Combine(_folder, "recipe.sgraph"));

            Assert.Equal(Path.Combine(_folder, "recipe.layout"), requested);
        });
    }

    /// <summary>
    /// Save now writes over the open file. The title following the path is
    /// what tells it there is one, so a later Save goes straight there
    /// instead of asking again.
    /// </summary>
    [Fact]
    public void Saving_adopts_the_file_as_the_open_one()
    {
        OnStaThread(() =>
        {
            var model = new MainViewModel();

            Assert.DoesNotContain("recipe", model.Title);

            model.Write(Path.Combine(_folder, "recipe.sgraph"));

            Assert.Contains("recipe.sgraph", model.Title);
        });
    }

    // A test for the failure path belongs here, but Write reports a failed
    // save with a MessageBox, and a modal dialog on a test thread takes the
    // whole host down rather than failing. Testing it would mean routing
    // errors somewhere the view-model does not own, which is a bigger change
    // than the coverage is worth today.
}
