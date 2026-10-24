namespace Sirius.Core.Graph;

/// <summary>
/// A cached output value plus the one thing that makes deterministic cleanup of
/// native buffers tractable: who owns it.
/// </summary>
/// <remarks>
/// <para>
/// The ownership rule for the whole engine is:
/// <b>a node never disposes its inputs; the scheduler disposes cached values it
/// replaces or evicts.</b> Nodes allocate their outputs fresh and hand them over.
/// </para>
/// <para>
/// The exception is pass-through: a disabled node, or a node that falls back to
/// echoing its input on error, produces a value it does not own. Marking those
/// entries <see cref="IsOwned"/> = false is what prevents the scheduler from
/// disposing a buffer that actually belongs to an upstream node - the
/// double-free that the old <c>OutputImage = InputImage</c> error path was one
/// refactor away from becoming.
/// </para>
/// </remarks>
internal readonly struct PortValue
{
    public PortValue(object? value, bool isOwned)
    {
        Value = value;
        IsOwned = isOwned;
    }

    public object? Value { get; }

    /// <summary>True when the cache is responsible for disposing <see cref="Value"/>.</summary>
    public bool IsOwned { get; }

    /// <summary>Disposes the payload if this entry owns it and it is disposable.</summary>
    public void DisposeIfOwned()
    {
        if (IsOwned && Value is IDisposable disposable)
            disposable.Dispose();
    }
}
