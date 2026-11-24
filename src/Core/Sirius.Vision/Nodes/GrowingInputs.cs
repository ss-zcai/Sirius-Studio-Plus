using Sirius.Core.Graph;

namespace Sirius.Vision.Nodes;

/// <summary>
/// A row of input sockets that keeps exactly one spare at the end: wire the
/// last and another appears, unwire one and the extras go.
/// </summary>
/// <remarks>
/// <para>
/// What Image Viewer does for its shapes sockets, kept in one place for the
/// nodes that came after it. The sockets are named for their position -
/// "In", "In 2", "In 3" - so a saved recipe names the one each wire lands
/// on, and <see cref="Ensure"/> rebuilds up to it when the recipe loads.
/// </para>
/// <para>
/// Adding and removing sockets is the node's to do (they are protected on
/// <see cref="VisionNode"/>), so the node hands those in.
/// </para>
/// </remarks>
internal sealed class GrowingInputs
{
    private readonly string _first;
    private readonly int _limit;
    private readonly Func<IReadOnlyList<InputPort>> _sockets;
    private readonly Action<InputPort, string> _insertAfter;
    private readonly Func<InputPort, bool> _remove;
    private readonly Func<string, InputPort?> _find;

    /// <param name="first">The first socket's name; the rest are "<paramref name="first"/> 2", "... 3".</param>
    /// <param name="limit">The most sockets there will ever be.</param>
    /// <param name="sockets">The sockets in the row, in order.</param>
    /// <param name="insertAfter">Adds a socket of the given name just after the given one.</param>
    /// <param name="remove">Removes an unconnected socket.</param>
    /// <param name="find">Looks a socket up by name.</param>
    public GrowingInputs(
        string first,
        int limit,
        Func<IReadOnlyList<InputPort>> sockets,
        Action<InputPort, string> insertAfter,
        Func<InputPort, bool> remove,
        Func<string, InputPort?> find)
    {
        _first = first;
        _limit = limit;
        _sockets = sockets;
        _insertAfter = insertAfter;
        _remove = remove;
        _find = find;
    }

    /// <summary>The sockets that have a wire, in order.</summary>
    public IEnumerable<InputPort> Connected => _sockets().Where(port => port.IsConnected);

    /// <summary>Leaves exactly one spare socket at the end. True when the row changed.</summary>
    public bool Fit()
    {
        var changed = false;
        var sockets = _sockets();
        var spare = sockets.Count(port => !port.IsConnected);

        // Trimmed from the end: a gap in the middle stays, because renumbering
        // would move a wire below it to a different socket.
        for (var i = sockets.Count - 1; i > 0 && spare > 1; i--)
        {
            if (sockets[i].IsConnected || !_remove(sockets[i]))
                continue;

            spare--;
            changed = true;
        }

        if (spare == 0 && sockets.Count < _limit)
        {
            Grow();
            changed = true;
        }

        return changed;
    }

    /// <summary>Builds sockets up to the one a saved recipe names, or null when the name is not one of these.</summary>
    public InputPort? Ensure(string name)
    {
        if (!TryReadSlot(name, out var slot) || slot > _limit)
            return null;

        while (_sockets().Count < slot)
            Grow();

        return _find(name);
    }

    private void Grow()
    {
        var sockets = _sockets();
        _insertAfter(sockets[^1], $"{_first} {sockets.Count + 1}");
    }

    /// <summary>Reads "In 3" as 3, and the bare "In" as 1.</summary>
    private bool TryReadSlot(string name, out int slot)
    {
        slot = 0;

        if (string.Equals(name, _first, StringComparison.Ordinal))
        {
            slot = 1;
            return true;
        }

        if (!name.StartsWith(_first + " ", StringComparison.Ordinal))
            return false;

        return int.TryParse(name[(_first.Length + 1)..], out slot) && slot > 1;
    }
}
