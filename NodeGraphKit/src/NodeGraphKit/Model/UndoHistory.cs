namespace NodeGraphKit.Model;

/// <summary>
/// Undo and redo, as a list of whole-graph snapshots.
/// </summary>
/// <remarks>
/// <para>
/// Each step stores the graph as it was afterwards, so undo is "put back the
/// one before". A snapshot cannot disagree with the graph the way a list of
/// hand-written reverse operations can, and a graph of a few hundred nodes is
/// a few hundred kilobytes of JSON - <see cref="Capacity"/> steps of that is
/// nothing.
/// </para>
/// <para>
/// Steps with the same merge key that follow each other closely become one:
/// dragging a slider is one step back, not two hundred.
/// </para>
/// </remarks>
public sealed class UndoHistory
{
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(1.5);

    private readonly List<Step> _steps = [];
    private int _index = -1;
    private int _savedIndex = -1;
    private DateTime _lastRecorded;

    /// <summary>How many steps back are kept.</summary>
    public int Capacity { get; set; } = 200;

    public bool CanUndo => _index > 0;

    public bool CanRedo => _index < _steps.Count - 1;

    /// <summary>"Undo Move", "Undo Delete Add" - what the next undo takes back.</summary>
    public string UndoText => CanUndo ? $"Undo {_steps[_index].Label}" : "Undo";

    public string RedoText => CanRedo ? $"Redo {_steps[_index + 1].Label}" : "Redo";

    /// <summary>Whether anything changed since <see cref="MarkSaved"/>.</summary>
    public bool IsDirty => _index != _savedIndex;

    public event EventHandler? Changed;

    /// <summary>Forgets every step and starts again from this state - a new or opened file.</summary>
    public void Reset(string state)
    {
        _steps.Clear();
        _steps.Add(new Step("Open", state, null));
        _index = 0;
        _savedIndex = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records the state after an edit.</summary>
    /// <param name="mergeKey">Steps with the same key within a second and a half are merged.</param>
    public void Record(string label, string state, string? mergeKey = null)
    {
        if (_index >= 0 && _steps[_index].State == state)
            return;

        // Anything ahead of here was undone, and a new edit forks from it.
        if (_index < _steps.Count - 1)
            _steps.RemoveRange(_index + 1, _steps.Count - _index - 1);

        var now = DateTime.UtcNow;
        var merge = mergeKey is not null &&
                    _index > 0 &&
                    _index != _savedIndex &&
                    _steps[_index].MergeKey == mergeKey &&
                    now - _lastRecorded < MergeWindow;

        if (merge)
        {
            _steps[_index] = new Step(label, state, mergeKey);
        }
        else
        {
            _steps.Add(new Step(label, state, mergeKey));
            _index++;

            if (_steps.Count > Capacity)
            {
                _steps.RemoveAt(0);
                _index--;
                _savedIndex--;
            }
        }

        _lastRecorded = now;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Steps back, returning the state to restore.</summary>
    public string Undo()
    {
        if (!CanUndo)
            throw new InvalidOperationException("Nothing to undo.");

        _index--;
        Changed?.Invoke(this, EventArgs.Empty);
        return _steps[_index].State;
    }

    public string Redo()
    {
        if (!CanRedo)
            throw new InvalidOperationException("Nothing to redo.");

        _index++;
        Changed?.Invoke(this, EventArgs.Empty);
        return _steps[_index].State;
    }

    /// <summary>The current state is what is on disk.</summary>
    public void MarkSaved()
    {
        _savedIndex = _index;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed record Step(string Label, string State, string? MergeKey);
}
