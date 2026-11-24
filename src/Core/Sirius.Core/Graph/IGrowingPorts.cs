namespace Sirius.Core.Graph;

/// <summary>
/// A node whose input list grows to fit what is wired into it.
/// </summary>
/// <remarks>
/// <para>
/// An input takes one connection, and that rule is worth keeping: the
/// scheduler, cycle detection, serialisation and every node in the library
/// read <c>InputPort.Connection</c> as a single thing. What was actually
/// wanted was not several wires in one socket but several sockets, and a
/// node can simply make more of them - <c>_inputs</c> is a list, and
/// <c>EnsureParameterPorts</c> already appends to it after construction.
/// </para>
/// <para>
/// So a growing node keeps one spare socket at the end. Fill it and another
/// appears; free one up and the spares collapse back to a single empty slot.
/// The count is never configured and never saved - it is derived from the
/// connections, which is the one description of it that cannot go stale.
/// </para>
/// </remarks>
public interface IGrowingPorts
{
    /// <summary>
    /// Adds or removes trailing spare inputs to leave exactly one free.
    /// </summary>
    /// <returns>True when the port list changed, so the canvas needs rebuilding.</returns>
    /// <remarks>
    /// Must never remove a connected port, and must be safe to call after
    /// any change at all - the caller does not know whether this node was
    /// involved.
    /// </remarks>
    bool Fit();

    /// <summary>
    /// Makes sure an input of this name exists, creating it if the name is
    /// one this node would have grown.
    /// </summary>
    /// <remarks>
    /// For loading. A saved recipe names the port each connection lands on,
    /// and the ports beyond the first spare do not exist on a freshly built
    /// node - so without this a recipe would open having silently dropped
    /// every wire past the second.
    /// </remarks>
    /// <returns>The port, or null when the name is not one this node makes.</returns>
    InputPort? EnsureInput(string name);
}
