namespace Cda.Revit.Addin.Finishes;

/// <summary>
/// Decides which paint room override, if any, owns one takeoff carrier - with every Revit type
/// kept out so the rule can be tested.
///
/// WHY THE KEY ALONE IS NOT ENOUGH
///   An override is keyed by "Paint Segment" + material, and that key is NOT unique across
///   rooms. For floors, ceilings and roofs the segment is just "Type #elementId", so one slab
///   under three rooms produces three carriers - one per room - with the identical key. For
///   walls it carries a boundary-segment index, which is per room and can coincide between the
///   two rooms either side of a wall, and which renumbers when a boundary is edited.
///
///   Matching on the key alone relabelled EVERY carrier with that key: reassigning room 101's
///   piece of a slab to room 201 moved 102's and 103's pieces too, and "remove all" crashed on
///   the duplicate key before restoring anything. Paint feeds a digital twin; a row filed under
///   the wrong room is the one failure that cannot be tolerated.
///
/// THE RULE
///   An override owns a carrier only when the carrier is in the override's FROM room - the
///   room it was naturally reported under when the override was recorded - or is already
///   showing this override's relabel (TO room number, "(Reassigned)" name). Anything else that
///   happens to share the key is left exactly as the takeoff wrote it, and is reported.
///
///   An override is therefore identified by key AND from-room. Two overrides may share a key
///   as long as they come from different rooms; the stored format is unchanged, because every
///   record already carries its from-room.
///
/// Same arrangement as UdvendigClassification and DimensionMath: the test project compiles
/// THIS file, not a copy of it, so the rule under test is the rule that ships.
/// </summary>
internal static class PaintOverrideOwnership
{
    /// <summary>
    /// Appended to a reassigned row's Room Name. See PaintRoomOverrides.ReassignedMarker for why
    /// it is plain text.
    /// </summary>
    public const string ReassignedMarker = " (Reassigned)";

    /// <summary>
    /// How far a carrier's area may drift from the area recorded with its override before the
    /// report calls it out: 0.001 m², in square feet. Report-only - a drift never stops an
    /// override applying, because legitimate geometry edits change area too.
    /// </summary>
    public const double AreaDriftToleranceSqFt = 0.001 / 0.09290304;

    /// <summary>
    /// Room numbers compared the way the Reassign command already compares them: trimmed and
    /// case-insensitive, because the number is typed by people.
    /// </summary>
    public static bool SameRoomNumber(string? a, string? b) =>
        string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a carrier's Room Name shows an override's relabel rather than the takeoff's.</summary>
    public static bool IsRelabelled(string? carrierRoomName) =>
        (carrierRoomName ?? string.Empty).EndsWith(ReassignedMarker, StringComparison.Ordinal);

    /// <summary>True when two overrides describe the same decision: same row key, same from-room.</summary>
    public static bool SameOverride(string keyA, string fromA, string keyB, string fromB) =>
        string.Equals(keyA, keyB, StringComparison.Ordinal) && SameRoomNumber(fromA, fromB);

    /// <summary>
    /// The override that owns a carrier, out of the overrides sharing that carrier's key, or
    /// null when none does.
    ///
    /// A carrier fresh from the takeoff is owned by the override whose FROM room it is in. A
    /// carrier already relabelled (a re-apply with no takeoff in between) is owned by the
    /// override whose TO room it shows. Several candidates means several records of the same
    /// decision; the LAST one wins, matching how the table has always resolved duplicates.
    ///
    /// <paramref name="ambiguous"/> is set when a relabelled carrier could have come from more
    /// than one from-room - two overrides moving different rooms' pieces of one surface into the
    /// same room. Relabelling it again gives the same answer either way, but restoring it would
    /// have to guess its natural room, so callers that restore must not act on it.
    /// </summary>
    public static T? OwnerOf<T>(
        IReadOnlyList<T> sameKey,
        string? carrierRoomNumber,
        string? carrierRoomName,
        Func<T, string> fromRoomNumber,
        Func<T, string> toRoomNumber,
        out bool ambiguous)
        where T : class
    {
        ambiguous = false;

        if (IsRelabelled(carrierRoomName))
        {
            var relabelledBy = sameKey
                .Where(o => SameRoomNumber(toRoomNumber(o), carrierRoomNumber))
                .ToList();

            ambiguous = relabelledBy
                .Select(o => (fromRoomNumber(o) ?? string.Empty).Trim().ToUpperInvariant())
                .Distinct()
                .Count() > 1;

            return relabelledBy.Count > 0 ? relabelledBy[^1] : null;
        }

        var natural = sameKey
            .Where(o => SameRoomNumber(fromRoomNumber(o), carrierRoomNumber))
            .ToList();

        return natural.Count > 0 ? natural[^1] : null;
    }

    /// <summary>
    /// True when a carrier's area has moved from the one recorded with its override - the sign
    /// that the key may now name a different face (a boundary edit renumbering segments) or the
    /// face itself changed. Unknown areas (zero or less) never count as drift.
    /// </summary>
    public static bool AreaDrifted(double currentSqFt, double recordedSqFt) =>
        currentSqFt > 0 && recordedSqFt > 0 &&
        Math.Abs(currentSqFt - recordedSqFt) > AreaDriftToleranceSqFt;
}
