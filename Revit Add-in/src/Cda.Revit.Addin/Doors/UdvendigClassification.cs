namespace Cda.Revit.Addin.Doors;

/// <summary>
/// The decision rules behind the Udvendig exterior/interior classification, with every Revit
/// type kept out so they can be tested.
///
/// WHY THIS IS A SEPARATE FILE. The rules here are four booleans and a prefix match, and all
/// four failures they can produce are silent: a door classified interior when it is exterior is
/// billed to the same room twice by the FROM and TO schedules (see
/// <see cref="UdvendigRoomResolver"/> on why substitution makes those two identical), and a
/// door classified exterior when it is not simply vanishes out of the interior schedules. No
/// exception, no empty column, nothing to notice - just a quantity that is wrong. Rules like
/// that are worth pinning down with tests, and a test project cannot reference RevitAPI.dll
/// because it will not load outside the Revit host.
///
/// Same arrangement as DimensionMath and the heating geometry: the test project compiles THIS
/// file, not a copy of it, so the rules under test are the rules that ship.
/// </summary>
internal static class UdvendigClassification
{
    /// <summary>
    /// The flag cannot be read on this door - the parameter is absent, unreadable, or not a
    /// Yes/No.
    ///
    /// DISTINCT FROM 0 ON PURPOSE. "No exterior side" and "cannot record an answer" look
    /// identical in a Yes/No column and mean opposite things: the first is a measurement, the
    /// second is a missing measurement. Collapsing them would make every door in a model
    /// without the parameter read as a confident interior door.
    /// </summary>
    public const int FlagAbsent = -1;

    /// <summary>
    /// Does this room name mark the outside? Matched on a TRIMMED, case-insensitive prefix, so
    /// 'Udvendig', 'Udvendig 1' and 'udvendig 3' all count.
    ///
    /// THE PREFIX IS A PREFIX, NOT A CONTAINS, and that is load-bearing. Measured in
    /// FM_Template 2027V1.00_EN: door 752 runs from 'Kælderrum 4' to 'Udestue' - a conservatory,
    /// a real enclosed room that a person stands in. It shares three letters with 'Udvendig'
    /// and nothing else. A looser match would classify it exterior, drop it out of the interior
    /// door schedules, and silently lose its casing, frame and lining quantities from the
    /// takeoff - despite it being a perfectly ordinary door between two real rooms.
    /// </summary>
    public static bool IsExteriorRoomName(string? name, string prefix)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (string.IsNullOrEmpty(prefix)) return false;

        return name.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A door is exterior when EITHER side faces outside.
    ///
    /// Either, not both: a door with one Udvendig side is exactly the case the resolver
    /// substitutes, which is what leaves its FROM and TO naming the same room and makes it
    /// double-count. A door with BOTH sides exterior is still exterior - it has no interior
    /// side to bill at all, so it has even less business in an interior schedule.
    /// </summary>
    public static bool IsExteriorDoor(bool exteriorFrom, bool exteriorTo) => exteriorFrom || exteriorTo;

    /// <summary>
    /// The room the 'Ext Door * @V05' set should show: the FROM side, unless FROM is the
    /// exterior one, in which case the room opposite it.
    ///
    /// WHY IT LIVES HERE RATHER THAN IN THE RESOLVER. This is the whole of the office rule -
    /// "if the Rum field says 'Udvendig', show the room on the other side instead" - and it
    /// mentions no Revit type, so it can be tested. The resolver's Plan() cannot: it needs a
    /// Document to find a room at a point, and RevitAPI.dll does not load outside the host.
    /// Everything decidable without Revit belongs in this file for exactly that reason.
    ///
    /// ONLY THE FROM SIDE MOVES. If TO is the exterior one then FROM is already the interior
    /// room and is what the schedule wants, so it stays put.
    ///
    /// BOTH SIDES EXTERIOR RETURNS FROM UNCHANGED, deliberately. There is no interior room to
    /// name, and substituting one exterior side for the other would print 'Udvendig' while
    /// claiming it had been replaced. Leaving it visibly wrong is better than making it
    /// invisibly wrong - see the report, which warns about that door separately.
    ///
    /// A MISSING COUNTERPART IS NOT A SUBSTITUTION EITHER. Copying an empty TO over a real
    /// FROM would erase the only room the door has.
    /// </summary>
    public static (string Num, string Name) SubstitutedSide(
        bool exteriorFrom, bool exteriorTo,
        string fromNum, string fromName, string toNum, string toName)
    {
        if (!exteriorFrom) return (fromNum, fromName);
        if (exteriorTo) return (fromNum, fromName);
        if (toNum.Length == 0 && toName.Length == 0) return (fromNum, fromName);

        return (toNum, toName);
    }

    /// <summary>What the flag should read for a door.</summary>
    public static int DesiredFlag(bool isExterior) => isExterior ? 1 : 0;

    /// <summary>
    /// Whether this pass should write the flag on a door.
    ///
    /// THREE REASONS NOT TO WRITE, and they are not interchangeable:
    ///   * <paramref name="overridden"/> - a human ticked the Override, so the value is theirs.
    ///   * <see cref="FlagAbsent"/> - there is nowhere to write it.
    ///   * already correct - writing it again would dirty the model for nothing, which on an
    ///     automatic pass that runs on every edit is the difference between a quiet session and
    ///     an undo stack full of no-op transactions.
    ///
    /// Note that an interior door whose flag currently reads 1 DOES need writing: that is a
    /// stale YES from when the door faced outside, and leaving it would keep the door out of
    /// the interior schedules forever.
    /// </summary>
    public static bool NeedsFlagWrite(bool isExterior, int flagCurrent, bool overridden)
    {
        if (overridden) return false;
        if (flagCurrent == FlagAbsent) return false;

        return flagCurrent != DesiredFlag(isExterior);
    }
}
