using System.IO;
using System.Windows;
using Sirius.Core.Graph;
using Sirius.Core.Graph.Undo;
using Sirius.Core.Nodes;
using Sirius.Studio.App.ViewModels;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Undo as the shell wires it: the canvas, the history and the recipe.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UndoStackTests"/> covers the stack against a bare graph. The
/// join is what is left, and it is where this kind of feature usually fails:
/// the model is put back correctly and the canvas keeps showing the graph
/// that was there before, because the editor's collections were built as the
/// operator worked and nothing told them to re-read.
/// </para>
/// <para>
/// Nothing here shows a window. The editor view-model and the shell
/// view-model are both testable without one, which is the point of the split.
/// </para>
/// </remarks>
public class EditHistoryTests
{
    public EditHistoryTests() => TestCatalog.Ensure();

    private sealed record Fixture(VisionGraph Graph, UndoStack Stack, GraphEditorViewModel Editor);

    private static Fixture Build()
    {
        var graph = new VisionGraph();
        var stack = new UndoStack(graph);
        _ = new UndoRecorder(graph, stack);

        return new Fixture(graph, stack, new GraphEditorViewModel(graph, stack));
    }

    private static NodeDescriptor Descriptor(string displayName) =>
        NodeCatalog.All.First(descriptor => descriptor.DisplayName == displayName);

    private static NodeViewModel Add(Fixture fixture, string displayName, double x = 0, double y = 0) =>
        fixture.Editor.AddNode(Descriptor(displayName), new Point(x, y));

    // ---- the canvas follows the model --------------------------------------

    [Fact]
    public void Undoing_an_add_takes_the_node_off_the_canvas()
    {
        var fixture = Build();
        Add(fixture, "Threshold");

        Assert.Single(fixture.Editor.Nodes);

        fixture.Stack.Undo();
        fixture.Editor.Resync();

        Assert.Empty(fixture.Editor.Nodes);
        Assert.Empty(fixture.Graph.Nodes);
    }

    /// <summary>
    /// The regression this is here to prevent: the node is back in the model
    /// and the canvas never hears about it.
    /// </summary>
    [Fact]
    public void Undoing_a_delete_puts_the_node_back_on_the_canvas()
    {
        var fixture = Build();
        var node = Add(fixture, "Threshold");

        fixture.Editor.RemoveNode(node);
        Assert.Empty(fixture.Editor.Nodes);

        fixture.Stack.Undo();
        fixture.Editor.Resync();

        var restored = Assert.Single(fixture.Editor.Nodes);
        Assert.Same(node.Node, restored.Node);
    }

    [Fact]
    public void Undoing_a_delete_puts_the_wire_back_on_the_canvas()
    {
        var fixture = Build();
        var source = Add(fixture, "Blank Image");
        var tool = Add(fixture, "Threshold");

        fixture.Graph.TryConnect(
            (OutputPort)source.Node.Outputs[0],
            (InputPort)tool.Node.Inputs[0]);
        fixture.Editor.Resync();

        Assert.Single(fixture.Editor.Connections);

        fixture.Editor.RemoveNode(tool);
        fixture.Stack.Undo();
        fixture.Editor.Resync();

        Assert.Equal(2, fixture.Editor.Nodes.Count);
        Assert.Single(fixture.Editor.Connections);
    }

    /// <summary>
    /// Deleting a selection of several is one step back, not one per node.
    /// </summary>
    [Fact]
    public void Deleting_a_selection_is_one_step()
    {
        var fixture = Build();
        Add(fixture, "Threshold");
        Add(fixture, "Invert");
        Add(fixture, "Gaussian Blur");

        foreach (var node in fixture.Editor.Nodes)
            node.IsSelected = true;

        fixture.Stack.Clear();
        fixture.Editor.DeleteSelectionCommand.Execute(null);

        Assert.Empty(fixture.Graph.Nodes);
        Assert.Equal("Delete 3 nodes", fixture.Stack.UndoLabel);

        fixture.Stack.Undo();

        Assert.Equal(3, fixture.Graph.Nodes.Count);
        Assert.False(fixture.Stack.CanUndo, "deleting a selection left more than one step");
    }

    /// <summary>
    /// Selection survives an undo. Without it, undoing a nudge empties the
    /// properties panel - the panel is then right about the graph and wrong
    /// about what the operator was doing.
    /// </summary>
    [Fact]
    public void An_undo_keeps_the_selection()
    {
        var fixture = Build();
        Add(fixture, "Threshold");
        var second = Add(fixture, "Invert");

        foreach (var node in fixture.Editor.Nodes)
            node.IsSelected = ReferenceEquals(node, second);

        second.Node.X = 400;
        fixture.Stack.Undo();
        fixture.Editor.Resync();

        var selected = Assert.Single(fixture.Editor.Nodes.Where(node => node.IsSelected));
        Assert.Same(second.Node, selected.Node);
        Assert.Same(second.Node, fixture.Editor.SelectedNode!.Node);
    }

    // ---- the recipe --------------------------------------------------------

    /// <summary>
    /// Undoing back to where the file was saved leaves nothing to save. The
    /// dirty check re-serialises the graph rather than counting edits, so
    /// this works without undo and the save prompt knowing about each other
    /// - which is the whole reason it was built that way.
    /// </summary>
    [Fact]
    public void Undoing_back_to_the_saved_state_leaves_nothing_to_save()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sirius-undo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            OnStaThread(() =>
            {
                var model = new MainViewModel();
                var node = (ThresholdNode)model.AddNodeAt(Palette(model, "Threshold"), new Point(40, 40))!.Node;

                model.Write(Path.Combine(folder, "recipe.sgraph"));
                Assert.False(model.IsModified);

                node.Threshold = 222;
                Assert.True(model.IsModified);

                model.UndoCommand.Execute(null);

                Assert.False(model.IsModified);
            });
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception)
            {
                // A leftover temp folder is not worth failing a run over.
            }
        }
    }

    /// <summary>
    /// Opening a recipe starts a fresh history. A stack of edits to the graph
    /// that was open is not a history of this one, and undoing into it would
    /// set properties on nodes that are no longer anywhere.
    /// </summary>
    [Fact]
    public void A_new_recipe_starts_a_new_history()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sirius-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            OnStaThread(() =>
            {
                NoModalDialogs.Forget();

                var model = new MainViewModel();
                model.AddNodeAt(Palette(model, "Threshold"), new Point(40, 40));

                // Saved first, deliberately. Asking for a new project with
                // unsaved work in the old one is an unsaved-changes prompt by
                // definition, and this test is not about that prompt - the
                // first version of it opened a real modal on the test thread
                // and stopped the run until somebody noticed the dialog.
                model.Write(Path.Combine(folder, "recipe.sgraph"));

                Assert.True(model.UndoCommand.CanExecute(null));

                model.NewGraphCommand.Execute(null);

                Assert.False(model.UndoCommand.CanExecute(null));
                Assert.Equal("Undo", model.UndoText);
                Assert.Empty(NoModalDialogs.Asked);
            });
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception)
            {
                // A leftover temp folder is not worth failing a run over.
            }
        }
    }

    /// <summary>
    /// And the prompt is still there when it should be: a new project with
    /// unsaved work in the old one asks before discarding it.
    /// </summary>
    [Fact]
    public void A_new_recipe_asks_before_discarding_unsaved_work()
    {
        OnStaThread(() =>
        {
            NoModalDialogs.Forget();

            var model = new MainViewModel();
            model.AddNodeAt(Palette(model, "Threshold"), new Point(40, 40));

            model.NewGraphCommand.Execute(null);

            // Answered "No" by the test harness, so the new project went
            // ahead - but it was asked, which is the part that matters.
            Assert.Single(NoModalDialogs.Asked);
            Assert.Contains("Save", NoModalDialogs.Asked[0], StringComparison.Ordinal);
        });
    }

    // ---- what the menu says ------------------------------------------------

    /// <summary>
    /// The menu names the step, because knowing which one is next back is
    /// most of the value of having undo at all.
    /// </summary>
    [Fact]
    public void The_menu_names_the_step()
    {
        OnStaThread(() =>
        {
            var model = new MainViewModel();
            var added = model.AddNodeAt(Palette(model, "Threshold"), new Point(40, 40))!;

            Assert.Equal($"Undo Add {added.Title}", model.UndoText);

            model.UndoCommand.Execute(null);

            Assert.Equal("Undo", model.UndoText);
            Assert.Equal($"Redo Add {added.Title}", model.RedoText);
        });
    }

    [Fact]
    public void Nothing_to_undo_means_undo_cannot_run()
    {
        OnStaThread(() =>
        {
            var model = new MainViewModel();

            Assert.False(model.UndoCommand.CanExecute(null));
            Assert.False(model.RedoCommand.CanExecute(null));
        });
    }

    // ---- the clipboard and styler commands ---------------------------------

    /// <summary>
    /// Copy, cut and duplicate need something selected, and paste needs
    /// something on the clipboard. A command that is never re-asked stays
    /// greyed out for the rest of the session, which reads as the feature
    /// being broken.
    /// </summary>
    [Fact]
    public void The_edit_commands_follow_the_selection()
    {
        OnStaThread(() =>
        {
            var model = new MainViewModel();

            Assert.False(model.CopyCommand.CanExecute(null));
            Assert.False(model.CutCommand.CanExecute(null));
            Assert.False(model.DuplicateCommand.CanExecute(null));
            Assert.False(model.HasNodeSelection);

            // AddNode selects what it added, as the canvas does.
            model.AddNodeAt(Palette(model, "Threshold"), new Point(40, 40));

            Assert.True(model.CopyCommand.CanExecute(null));
            Assert.True(model.CutCommand.CanExecute(null));
            Assert.True(model.DuplicateCommand.CanExecute(null));
            Assert.True(model.HasNodeSelection);
        });
    }

    /// <summary>
    /// Duplicating leaves the copy selected, so it can be dragged into place
    /// at once - which is what anyone does next.
    /// </summary>
    [Fact]
    public void Duplicating_selects_the_copy()
    {
        OnStaThread(() =>
        {
            var model = new MainViewModel();
            var original = model.AddNodeAt(Palette(model, "Threshold"), new Point(40, 40))!;

            model.DuplicateCommand.Execute(null);

            Assert.Equal(2, model.Editor.Nodes.Count);

            var selected = Assert.Single(model.Editor.Nodes.Where(node => node.IsSelected));
            Assert.NotSame(original.Node, selected.Node);
        });
    }

    [Fact]
    public void Duplicating_undoes_as_one_step()
    {
        OnStaThread(() =>
        {
            var model = new MainViewModel();
            model.AddNodeAt(Palette(model, "Blank Image"), new Point(0, 0));
            model.AddNodeAt(Palette(model, "Threshold"), new Point(200, 0));

            model.SelectAllCommand.Execute(null);
            model.DuplicateCommand.Execute(null);

            Assert.Equal(4, model.Editor.Nodes.Count);

            model.UndoCommand.Execute(null);

            Assert.Equal(2, model.Editor.Nodes.Count);
        });
    }

    /// <summary>
    /// The toolbar's styler colours the whole selection, which is what the
    /// per-node swatches cannot do - and as one step, because marking up a
    /// branch is one action.
    /// </summary>
    [Fact]
    public void The_styler_colours_the_whole_selection_in_one_step()
    {
        OnStaThread(() =>
        {
            var model = new MainViewModel();
            model.AddNodeAt(Palette(model, "Threshold"), new Point(0, 0));
            model.AddNodeAt(Palette(model, "Invert"), new Point(200, 0));
            model.SelectAllCommand.Execute(null);

            model.SetAccentCommand.Execute(NodeAccent.Red);

            Assert.All(model.Editor.Nodes, node => Assert.Equal(NodeAccent.Red, node.Accent));

            model.UndoCommand.Execute(null);

            Assert.All(model.Editor.Nodes, node => Assert.Equal(NodeAccent.Default, node.Accent));
        });
    }

    [Fact]
    public void Select_all_selects_everything()
    {
        OnStaThread(() =>
        {
            var model = new MainViewModel();
            model.AddNodeAt(Palette(model, "Threshold"), new Point(0, 0));
            model.AddNodeAt(Palette(model, "Invert"), new Point(200, 0));

            model.SelectAllCommand.Execute(null);

            Assert.All(model.Editor.Nodes, node => Assert.True(node.IsSelected));
        });
    }

    // ---- helpers -----------------------------------------------------------

    private static PaletteItemViewModel Palette(MainViewModel model, string displayName) =>
        model.Palette
            .SelectMany(group => group.Items)
            .First(entry => entry.DisplayName == displayName);

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
}
