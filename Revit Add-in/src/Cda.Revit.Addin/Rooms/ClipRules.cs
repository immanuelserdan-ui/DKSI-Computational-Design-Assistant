namespace Cda.Revit.Addin.Rooms;

/// <summary>What to do with a board once the room boundary has been located.</summary>
public enum ClipAction
{
    /// <summary>Build it as drawn. Either wholly inside, or short enough to be all corner.</summary>
    KeepWhole,

    /// <summary>Build the span in the decision.</summary>
    Trim,

    /// <summary>Build nothing.</summary>
    Refuse,
}

/// <summary>An action and, for <see cref="ClipAction.Trim"/>, the span to build.</summary>
public readonly record struct ClipDecision(ClipAction Action, double From, double To)
{
    public double Length => Math.Max(0.0, To - From);
}

/// <summary>
/// Whether the room ending is a reason to shorten a board, and by how much.
///
/// NO REVIT TYPE IN THIS FILE, DELIBERATELY. Locating the boundary needs Revit; deciding
/// what to do about it is arithmetic over four numbers, and every containment bug this tool
/// has shipped has been in that arithmetic rather than in the geometry - a corner forgiven at
/// the wrong end, a sliver built below the minimum run, a whole board discarded because it
/// was short enough to be nothing but corner. Keeping the decision here is what lets
/// Cda.Containment.Tests exercise the SHIPPING rules outside the Revit host.
///
/// It is also the one copy. The solid path and the sampling fallback both used to carry this
/// arithmetic inline, so a fix to one silently left the other on the old rule.
/// </summary>
public static class ClipRules
{
    /// <summary>
    /// The decision for a board whose inside span has been measured.
    /// </summary>
    /// <param name="length">The board's full length.</param>
    /// <param name="from">Where the room starts, as a distance from the board's start.</param>
    /// <param name="to">Where the room ends, likewise.</param>
    /// <param name="forgiveStart">
    /// How much apparent escape to forgive at the start before cutting, and likewise
    /// <paramref name="forgiveEnd"/>. Non-zero only at an end that is a genuine corner: there
    /// the board legitimately occupies space the room does not, because the boundary is a
    /// line and a board has thickness, so where walls converge the room is narrower than the
    /// board long before the apex. Clipping there is what opens corner gaps.
    /// </param>
    /// <param name="minimumRun">Below this, a surviving span is a sliver and not a board.</param>
    public static ClipDecision ForSpan(
        double length, double from, double to,
        double forgiveStart, double forgiveEnd, double minimumRun)
    {
        if (length < 1e-9) return new(ClipAction.Refuse, 0.0, 0.0);

        // A span that arrives inverted or off the end of the board is not an answer. It
        // becomes a refusal rather than a curve built from nonsense.
        if (to < from) return new(ClipAction.Refuse, 0.0, 0.0);

        from = Math.Clamp(from, 0.0, length);
        to = Math.Clamp(to, 0.0, length);

        // THE CORNER ALLOWANCE. Applied to each end independently, because a board can reach
        // a genuine corner at one end and a doorway jamb at the other, and the jamb end must
        // stay tight.
        if (from <= forgiveStart) from = 0.0;
        if (length - to <= forgiveEnd) to = length;

        if (from <= 0.0 && to >= length) return new(ClipAction.KeepWhole, 0.0, length);

        if (to - from < minimumRun) return new(ClipAction.Refuse, 0.0, 0.0);

        return new(ClipAction.Trim, from, to);
    }

    /// <summary>
    /// The decision for a board no part of which measured as inside.
    ///
    /// Usually it really is outside - but a short piece pinned between two corners reads the
    /// same way and is still real: in the throat of a sharp corner the room is narrower than
    /// the probe, so no part of the board has room to be inside. It is kept when it is short
    /// enough to be nothing but corner.
    /// </summary>
    public static ClipDecision ForNothingInside(double length, double forgiveStart, double forgiveEnd)
        => length <= forgiveStart + forgiveEnd
            ? new(ClipAction.KeepWhole, 0.0, length)
            : new(ClipAction.Refuse, 0.0, 0.0);
}
