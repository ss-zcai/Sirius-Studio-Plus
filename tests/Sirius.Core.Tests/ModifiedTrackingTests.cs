using System.IO;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Knowing whether a recipe has unsaved work in it.
/// </summary>
/// <remarks>
/// Answered by re-serialising and comparing rather than by a flag set from
/// change events. A flag has to be wired to every kind of edit, and the one
/// that gets forgotten loses work silently - so these tests include the edits
/// that raise no event at all.
/// </remarks>
public class ModifiedTrackingTests : IDisposable
{
    private readonly string _folder;

    public ModifiedTrackingTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "sirius-dirty-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Adds a node the way the palette does.</summary>
    private static ThresholdNode AddThreshold(MainViewModel model)
    {
        var item = model.Palette
            .SelectMany(group => group.Items)
            .First(entry => entry.DisplayName == "Threshold");

        var added = model.AddNodeAt(item, new System.Windows.Point(40, 40));

        return (ThresholdNode)added!.Node;
    }

    [Fact]
    public void A_new_project_has_nothing_to_save()
    {
        OnStaThread(() => Assert.False(new MainViewModel().IsModified));
    }

    [Fact]
    public void Adding_a_node_counts_as_a_change()
    {
        OnStaThread(() =>
        {
            var model = new MainViewModel();

            AddThreshold(model);

            Assert.True(model.IsModified);
        });
    }

    [Fact]
    public void Changing_a_parameter_counts_as_a_change()
    {
        OnStaThread(() =>
        {
            var model = new MainViewModel();
            var node = AddThreshold(model);
            model.Write(Path.Combine(_folder, "recipe.sgraph"));

            node.Threshold = 200;

            Assert.True(model.IsModified);
        });
    }

    /// <summary>
    /// Moving a node raises no invalidation - nothing recomputes - but the
    /// position is saved, so losing it without asking would still be losing
    /// work. This is the case a change-event flag would miss.
    /// </summary>
    [Fact]
    public void Moving_a_node_counts_as_a_change()
    {
        OnStaThread(() =>
        {
            var model = new MainViewModel();
            var node = AddThreshold(model);
            model.Write(Path.Combine(_folder, "recipe.sgraph"));

            node.X += 120;

            Assert.True(model.IsModified);
        });
    }

    [Fact]
    public void Renaming_a_node_counts_as_a_change()
    {
        OnStaThread(() =>
        {
            var model = new MainViewModel();
            var node = AddThreshold(model);
            model.Write(Path.Combine(_folder, "recipe.sgraph"));

            node.Name = "Board threshold";

            Assert.True(model.IsModified);
        });
    }

    [Fact]
    public void Saving_settles_it()
    {
        OnStaThread(() =>
        {
            var model = new MainViewModel();
            AddThreshold(model);
            Assert.True(model.IsModified);

            model.Write(Path.Combine(_folder, "recipe.sgraph"));

            Assert.False(model.IsModified);
        });
    }

    /// <summary>
    /// Undoing an edit by hand leaves the recipe as it was saved, and is not
    /// a change - which a one-way flag would get wrong.
    /// </summary>
    [Fact]
    public void Putting_a_value_back_is_not_a_change()
    {
        OnStaThread(() =>
        {
            var model = new MainViewModel();
            var node = AddThreshold(model);
            node.Threshold = 100;
            model.Write(Path.Combine(_folder, "recipe.sgraph"));

            node.Threshold = 200;
            Assert.True(model.IsModified);

            node.Threshold = 100;

            Assert.False(model.IsModified);
        });
    }

    [Fact]
    public void The_title_marks_unsaved_work()
    {
        OnStaThread(() =>
        {
            var model = new MainViewModel();
            Assert.DoesNotContain("*", model.Title);

            AddThreshold(model);

            Assert.StartsWith("*", model.Title);
        });
    }
}
