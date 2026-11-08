using Sirius.Core.Graph;
using Sirius.Core.Graph.Undo;
using Sirius.Core.Serialization;
using Sirius.Vision.Nodes;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Published names: what a screen finds a recipe's outputs, settings and
/// viewers by (runtime design, section 0).
/// </summary>
public class PublishingTests
{
    public PublishingTests() => TestCatalog.Ensure();

    private static (VisionGraph Graph, ConstantImageNode Source, ThresholdNode Tool, ViewerNode Viewer) Recipe()
    {
        var graph = new VisionGraph();
        var source = new ConstantImageNode { Width = 32, Height = 24, Value = 200 };
        var tool = new ThresholdNode();
        var viewer = new ViewerNode();

        graph.Add(source);
        graph.Add(tool);
        graph.Add(viewer);
        graph.TryConnect(source.ImageOut, tool.ImageIn);

        return (graph, source, tool, viewer);
    }

    [Fact]
    public void A_name_is_found_by_name_whatever_the_case()
    {
        var (graph, _, tool, _) = Recipe();

        Publishing.Publish(graph, tool, PublishedKind.Result, "Out", "Binary");

        var found = Assert.IsType<PublishedItem>(Publishing.Find(graph, "binary"));

        Assert.Same(tool, found.Node);
        Assert.Equal("Binary", found.Name);
        Assert.True(found.Found);
        Assert.Equal("Threshold › Out", found.Source);
    }

    [Fact]
    public void Results_settings_and_views_are_listed_together()
    {
        var (graph, _, tool, viewer) = Recipe();

        Publishing.Publish(graph, tool, PublishedKind.Result, "Out", "Binary");
        Publishing.Publish(graph, tool, PublishedKind.Setting, nameof(ThresholdNode.Threshold), "Level");
        Publishing.Publish(graph, viewer, PublishedKind.View, string.Empty, "Main View");

        Assert.Equal(
            ["Binary", "Level", "Main View"],
            Publishing.In(graph).Select(item => item.Name).ToArray());
    }

    /// <summary>
    /// One name means one thing across the recipe and every kind, so
    /// "station › name" never needs a kind to tell two apart.
    /// </summary>
    [Fact]
    public void A_name_cannot_be_used_twice_in_a_recipe()
    {
        var (graph, source, tool, _) = Recipe();

        Publishing.Publish(graph, tool, PublishedKind.Result, "Out", "Image");

        Assert.NotNull(Publishing.Problem(graph, "IMAGE", source, PublishedKind.Result, "Out"));
        Assert.NotNull(Publishing.Problem(graph, "Image", tool, PublishedKind.Setting, nameof(ThresholdNode.Threshold)));
        Assert.Throws<ArgumentException>(() =>
            Publishing.Publish(graph, source, PublishedKind.Result, "Out", "Image"));
    }

    /// <summary>Renaming a member to what it is already called is no clash.</summary>
    [Fact]
    public void A_member_may_keep_its_own_name()
    {
        var (graph, _, tool, _) = Recipe();

        Publishing.Publish(graph, tool, PublishedKind.Result, "Out", "Binary");

        Assert.Null(Publishing.Problem(graph, "binary", tool, PublishedKind.Result, "Out"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A › B")]
    [InlineData("Tab\there")]
    public void Unusable_names_are_refused(string name)
    {
        var (graph, _, tool, _) = Recipe();

        Assert.NotNull(Publishing.Problem(graph, name, tool, PublishedKind.Result, "Out"));
    }

    [Fact]
    public void A_name_too_long_is_refused()
    {
        var (graph, _, tool, _) = Recipe();

        Assert.NotNull(Publishing.Problem(graph, new string('x', Publishing.MaxLength + 1), tool, PublishedKind.Result, "Out"));
        Assert.Null(Publishing.Problem(graph, new string('x', Publishing.MaxLength), tool, PublishedKind.Result, "Out"));
    }

    [Fact]
    public void Names_are_trimmed()
    {
        var (graph, _, tool, _) = Recipe();

        Publishing.Publish(graph, tool, PublishedKind.Result, "Out", "  Binary ");

        Assert.Equal("Binary", Assert.Single(tool.Published).Name);
    }

    /// <summary>Publishing a member again renames it rather than adding a second name.</summary>
    [Fact]
    public void Publishing_again_renames()
    {
        var (graph, _, tool, _) = Recipe();

        Publishing.Publish(graph, tool, PublishedKind.Result, "Out", "Binary");
        Publishing.Publish(graph, tool, PublishedKind.Result, "Out", "Mask");

        Assert.Equal("Mask", Assert.Single(tool.Published).Name);
    }

    [Fact]
    public void Unpublishing_takes_only_that_name()
    {
        var (graph, _, tool, _) = Recipe();

        Publishing.Publish(graph, tool, PublishedKind.Result, "Out", "Binary");
        Publishing.Publish(graph, tool, PublishedKind.Setting, nameof(ThresholdNode.Threshold), "Level");

        Publishing.Unpublish(tool, PublishedKind.Result, "Out");

        Assert.Equal("Level", Assert.Single(tool.Published).Name);
        Assert.Null(Publishing.NameOf(tool, PublishedKind.Result, "Out"));
    }

    [Fact]
    public void A_taken_name_is_suggested_with_a_number()
    {
        var (graph, source, tool, _) = Recipe();

        Assert.Equal("Out", Publishing.Suggest(graph, "Out"));

        Publishing.Publish(graph, tool, PublishedKind.Result, "Out", "Out");
        Assert.Equal("out 2", Publishing.Suggest(graph, "out"));

        Publishing.Publish(graph, source, PublishedKind.Result, "Out", "Out 2");
        Assert.Equal("Out 3", Publishing.Suggest(graph, "Out"));
    }

    // ---- saved with the recipe -----------------------------------------

    [Fact]
    public void Names_survive_saving_and_opening()
    {
        var (graph, _, tool, viewer) = Recipe();

        Publishing.Publish(graph, tool, PublishedKind.Result, "Out", "Binary");
        Publishing.Publish(graph, tool, PublishedKind.Setting, nameof(ThresholdNode.Threshold), "Level");
        Publishing.Publish(graph, viewer, PublishedKind.View, string.Empty, "Main View");

        var loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));

        Assert.Empty(loaded.Warnings);
        Assert.Equal(
            Publishing.In(graph).Select(item => (item.Node.Id, item.Entry)).ToArray(),
            Publishing.In(loaded.Graph).Select(item => (item.Node.Id, item.Entry)).ToArray());
    }

    /// <summary>A recipe that publishes nothing is written exactly as before.</summary>
    [Fact]
    public void A_recipe_publishing_nothing_writes_no_published_field()
    {
        var (graph, _, _, _) = Recipe();

        Assert.DoesNotContain("\"published\"", GraphSerializer.Serialize(graph));
    }

    /// <summary>
    /// A name on an output the node no longer has is kept - to be moved or
    /// dropped from the Published list - and reported.
    /// </summary>
    [Fact]
    public void A_name_on_a_missing_output_is_kept_and_reported()
    {
        var (graph, _, tool, _) = Recipe();

        tool.Published = [new PublishedName(PublishedKind.Result, "Gone", "Old")];

        var loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));

        Assert.Contains(loaded.Warnings, warning => warning.Contains("Old", StringComparison.Ordinal));

        var item = Assert.Single(Publishing.In(loaded.Graph));
        Assert.False(item.Found);
    }

    /// <summary>Only a hand-edited file can hold a name twice; the second goes.</summary>
    [Fact]
    public void A_name_used_twice_in_a_file_keeps_the_first()
    {
        var (graph, source, tool, _) = Recipe();

        source.Published = [new PublishedName(PublishedKind.Result, "Out", "Image")];
        tool.Published = [new PublishedName(PublishedKind.Result, "Out", "image")];

        var loaded = GraphSerializer.Deserialize(GraphSerializer.Serialize(graph));

        Assert.Contains(loaded.Warnings, warning => warning.Contains("twice", StringComparison.Ordinal));
        Assert.Equal(source.Id, Assert.Single(Publishing.In(loaded.Graph)).Node.Id);
    }

    [Fact]
    public void An_unknown_kind_is_dropped_with_a_warning()
    {
        var (graph, _, tool, _) = Recipe();

        Publishing.Publish(graph, tool, PublishedKind.Result, "Out", "Binary");

        var json = GraphSerializer.Serialize(graph).Replace("\"Result\"", "\"Hologram\"", StringComparison.Ordinal);
        var loaded = GraphSerializer.Deserialize(json);

        Assert.Empty(Publishing.In(loaded.Graph));
        Assert.Contains(loaded.Warnings, warning => warning.Contains("Binary", StringComparison.Ordinal));
    }

    /// <summary>A copy is not what a screen was bound to, and would clash with the original.</summary>
    [Fact]
    public void A_pasted_copy_publishes_nothing()
    {
        var (graph, _, tool, _) = Recipe();

        Publishing.Publish(graph, tool, PublishedKind.Result, "Out", "Binary");

        var pasted = GraphClipboard.Paste(graph, GraphClipboard.Copy(graph, [tool]));

        Assert.Empty(Assert.Single(pasted.Nodes).Published);
        Assert.Single(Publishing.In(graph));
    }

    // ---- undo, and nothing recomputed ------------------------------------

    [Fact]
    public void Publishing_is_undoable()
    {
        var (graph, _, tool, _) = Recipe();
        var stack = new UndoStack(graph);
        using var recorder = new UndoRecorder(graph, stack);

        Publishing.Publish(graph, tool, PublishedKind.Result, "Out", "Binary");
        Assert.True(stack.CanUndo);

        stack.Undo();
        Assert.Empty(tool.Published);

        stack.Redo();
        Assert.Equal("Binary", Assert.Single(tool.Published).Name);
    }

    [Fact]
    public void Publishing_does_not_invalidate()
    {
        var (graph, _, tool, _) = Recipe();

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        GraphScheduler scheduler;
        try
        {
            scheduler = new GraphScheduler(graph);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        using (scheduler)
        {
            scheduler.RunAsync().GetAwaiter().GetResult();
            var dirty = scheduler.DirtyCount;

            Publishing.Publish(graph, tool, PublishedKind.Result, "Out", "Binary");

            Assert.Equal(dirty, scheduler.DirtyCount);
        }
    }
}
