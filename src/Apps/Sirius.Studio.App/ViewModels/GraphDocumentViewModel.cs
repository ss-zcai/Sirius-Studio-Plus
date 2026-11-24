using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sirius.Core.Graph;
using Sirius.Core.Graph.Undo;

namespace Sirius.Studio.App.ViewModels;

/// <summary>
/// One graph open for editing, in a tab of its own: the recipe, or the
/// inside of a sub-graph.
/// </summary>
/// <remarks>
/// <para>
/// Each has its own editor and its own undo history, the way two open
/// documents in any editor do. Undo in a sub-graph's tab undoes what was
/// done in that tab, not whatever happened last anywhere.
/// </para>
/// <para>
/// A sub-graph's document outlives its tab: closing the tab and opening it
/// again finds the same history, so closing a tab is not a way to lose the
/// ability to undo.
/// </para>
/// </remarks>
public sealed class GraphDocumentViewModel : ObservableObject, IDisposable
{
    public GraphDocumentViewModel(VisionGraph graph, SubGraphNode? owner = null)
    {
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Owner = owner;

        Undo = new UndoStack(graph);
        Recorder = new UndoRecorder(graph, Undo);
        Editor = new GraphEditorViewModel(graph, Undo);

        if (owner is not null)
            owner.PropertyChanged += OnOwnerPropertyChanged;
    }

    public VisionGraph Graph { get; }

    /// <summary>The sub-graph node this is the inside of, or null for the recipe itself.</summary>
    public SubGraphNode? Owner { get; }

    public UndoStack Undo { get; }

    public UndoRecorder Recorder { get; }

    public GraphEditorViewModel Editor { get; }

    public bool IsSubGraph => Owner is not null;

    /// <summary>The tab's caption, following the sub-graph node's name.</summary>
    public string Title => Owner?.Name ?? "Graph";

    /// <summary>The docking layout's identity for the tab.</summary>
    public string ContentId => Owner is null ? "graph" : $"subgraph:{Owner.Id}";

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VisionNode.Name))
            OnPropertyChanged(nameof(Title));
    }

    public void Dispose()
    {
        Recorder.Dispose();

        if (Owner is not null)
            Owner.PropertyChanged -= OnOwnerPropertyChanged;
    }
}
