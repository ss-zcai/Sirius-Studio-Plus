namespace Sirius.Core.Graph.Undo;

/// <summary>What an undo or redo did, so the shell can react to it.</summary>
public sealed class GraphEditEventArgs : EventArgs
{
    public GraphEditEventArgs(IGraphEdit edit, bool affectsResults)
    {
        Edit = edit;
        AffectsResults = affectsResults;
    }

    public IGraphEdit Edit { get; }

    /// <summary>Whether the graph has to be evaluated again.</summary>
    public bool AffectsResults { get; }
}

/// <summary>
/// Undo and redo over one graph.
/// </summary>
/// <remarks>
/// <para>
/// Two stacks and a guard. The guard is the part that is easy to leave out:
/// applying an edit changes node properties, the recorder is watching node
/// properties, and without <see cref="IsApplying"/> an undo records itself
/// as a new edit - after which the stack describes a history that never
/// happened and redo walks sideways.
/// </para>
/// <para>
/// Bounded, because the alternative is a session that grows until it is
/// restarted, and nobody undoes two hundred steps. The oldest edit is
/// dropped rather than refusing to record new ones: losing the far past is
/// what the operator expects, and losing the present is not.
/// </para>
/// </remarks>
public sealed class UndoStack
{
    /// <summary>How far back it remembers.</summary>
    public const int Depth = 200;

    private readonly VisionGraph _graph;
    private readonly List<IGraphEdit> _done = [];
    private readonly List<IGraphEdit> _undone = [];

    private List<IGraphEdit>? _batch;
    private string? _batchLabel;

    public UndoStack(VisionGraph graph) =>
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));

    /// <summary>
    /// True while an edit is being applied or reverted, so whatever is
    /// watching the graph knows not to record the consequences as new work.
    /// </summary>
    public bool IsApplying { get; private set; }

    public bool CanUndo => _done.Count > 0;

    public bool CanRedo => _undone.Count > 0;

    /// <summary>What Undo would undo, or null.</summary>
    public string? UndoLabel => CanUndo ? _done[^1].Label : null;

    /// <summary>What Redo would redo, or null.</summary>
    public string? RedoLabel => CanRedo ? _undone[^1].Label : null;

    /// <summary>Raised whenever what is available changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised after an edit was undone or redone.</summary>
    public event EventHandler<GraphEditEventArgs>? Applied;

    /// <summary>
    /// Records an edit that has already happened.
    /// </summary>
    /// <remarks>
    /// Already happened, rather than applied here, because most edits reach
    /// the stack from code that performs them in the course of doing
    /// something else - the editor adds a node because a node was dropped on
    /// the canvas, not because an edit was constructed.
    /// </remarks>
    public void Record(IGraphEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        if (IsApplying)
            return;

        if (_batch is not null)
        {
            _batch.Add(edit);
            return;
        }

        // A new edit makes the redo branch unreachable. Keeping it would mean
        // redo replaying a change onto a graph that has moved on.
        _undone.Clear();

        if (_done.Count > 0 && _done[^1].TryAbsorb(edit))
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _done.Add(edit);

        if (_done.Count > Depth)
            _done.RemoveAt(0);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Collects everything recorded until the returned scope is disposed into
    /// one step.
    /// </summary>
    /// <remarks>
    /// For the actions that are one thing to the operator and several to the
    /// graph: a paste, a drag that moves X and Y, deleting a selection.
    /// Nesting is counted rather than forbidden, so a batch inside a batch
    /// joins the outer one instead of quietly starting a second history.
    /// </remarks>
    public IDisposable Begin(string label)
    {
        if (_batch is not null)
            return new Scope(this, owns: false);

        _batch = [];
        _batchLabel = label;
        return new Scope(this, owns: true);
    }

    private void EndBatch()
    {
        var collected = _batch ?? [];
        var label = _batchLabel ?? "Edit";

        _batch = null;
        _batchLabel = null;

        if (collected.Count == 0)
            return;

        // One edit does not need wrapping, and wrapping it would cost the
        // label it gave itself.
        Record(collected.Count == 1
            ? collected[0]
            : new CompositeEdit(label, collected));
    }

    public bool Undo()
    {
        if (_batch is not null)
            throw new InvalidOperationException("Finish the batch before undoing.");

        if (!CanUndo)
            return false;

        var edit = _done[^1];
        _done.RemoveAt(_done.Count - 1);

        Run(() => edit.Revert(_graph));

        _undone.Add(edit);
        Changed?.Invoke(this, EventArgs.Empty);
        Applied?.Invoke(this, new GraphEditEventArgs(edit, edit.AffectsResults));
        return true;
    }

    public bool Redo()
    {
        if (_batch is not null)
            throw new InvalidOperationException("Finish the batch before redoing.");

        if (!CanRedo)
            return false;

        var edit = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);

        Run(() => edit.Apply(_graph));

        _done.Add(edit);
        Changed?.Invoke(this, EventArgs.Empty);
        Applied?.Invoke(this, new GraphEditEventArgs(edit, edit.AffectsResults));
        return true;
    }

    /// <summary>
    /// Forgets everything. Called when the graph is replaced - a history of
    /// edits to the recipe that was open is not a history of this one.
    /// </summary>
    public void Clear()
    {
        _done.Clear();
        _undone.Clear();
        _batch = null;
        _batchLabel = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Run(Action action)
    {
        IsApplying = true;
        try
        {
            action();
        }
        finally
        {
            IsApplying = false;
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly UndoStack _stack;
        private readonly bool _owns;
        private bool _closed;

        public Scope(UndoStack stack, bool owns)
        {
            _stack = stack;
            _owns = owns;
        }

        public void Dispose()
        {
            if (_closed || !_owns)
                return;

            _closed = true;
            _stack.EndBatch();
        }
    }
}
