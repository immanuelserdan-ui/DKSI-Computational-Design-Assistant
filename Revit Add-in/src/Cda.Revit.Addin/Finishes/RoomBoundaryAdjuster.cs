using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace Cda.Revit.Addin.Finishes;

/// <summary>
/// Makes rooms actually stop where the ceilings, slabs and roofs above them are, and does
/// nothing else.
///
/// WHAT IT IS FOR
///   Two Revit behaviours conspire to leave room volumes wrong, and neither announces itself:
///
///   ONE - 'Areas and Volumes' can be off. With it off Revit does not clip rooms against
///   bounding ceilings at all, so every room runs to its Upper Limit regardless of what is
///   overhead. Turning it on is what makes a room stop at the ceiling finish.
///
///   TWO - a room's volume can NEVER rise above its Upper Limit plus Offset. Bounding ceilings
///   only clip WITHIN that allowance, they do not extend it. So a sloped ceiling that climbs
///   above the limit leaves the room sliced flat, missing the whole wedge beneath the slope,
///   and the room reports a volume and a ceiling area for a shape it does not have.
///
///   The fix for the second is to raise each room's Upper Offset just past the highest ceiling,
///   floor or roof overlapping it in plan. RAISE-ONLY and minimal: the ceiling still clips the
///   volume, so extra headroom above it changes nothing, while a lowered limit would slice a
///   room that was previously correct.
///
/// WHY IT IS ITS OWN CLASS
///   It was two private methods inside the finish engine, which meant the only way to correct a
///   model's room boundaries was to run a full measurement pass - binding parameters, writing
///   areas to every room and element, and exporting a CSV - as a side effect of wanting the
///   boundaries right. Extracted rather than copied, so the engine and the standalone command
///   cannot drift into two different ideas of where a room stops.
///
/// WHAT IT DELIBERATELY DOES NOT DO
///   No measurement, no parameter binding, no writes other than the two above. It touches
///   'Areas and Volumes' on the document and ROOM_UPPER_OFFSET on rooms it raises. Nothing
///   else in the model is read for its own sake or written at all.
/// </summary>
public sealed class RoomBoundaryAdjuster
{
    private readonly Document _doc;
    private readonly FinishSettings _settings;

    public RoomBoundaryAdjuster(Document doc, FinishSettings settings)
    {
        _doc = doc;
        _settings = settings;
    }

    public sealed class Result
    {
        /// <summary>True when 'Areas and Volumes' was off and this turned it on.</summary>
        public bool VolumesEnabled { get; init; }

        /// <summary>Rooms whose Upper Offset was raised.</summary>
        public int RoomsAdjusted { get; init; }

        /// <summary>
        /// Rooms whose Upper Offset an EARLIER run had raised too far, brought back down. See
        /// <see cref="LowerOverRaisedLimits"/> - this is the one place this class lowers
        /// anything, and it only ever lowers to the height the corrected rule asks for.
        /// </summary>
        public int RoomsLowered { get; init; }

        public List<string> Notes { get; } = [];

        public bool ChangedAnything => VolumesEnabled || RoomsAdjusted > 0 || RoomsLowered > 0;
    }

    /// <summary>
    /// Runs both corrections. Caller owns the transaction - this writes to the document.
    /// </summary>
    public Result Run()
    {
        var notes = new List<string>();

        var volumes = EnsureVolumes(notes);

        // LOWER FIRST, THEN RAISE. Order matters: the correction reads each room's CURRENT
        // offset to decide whether an earlier run inflated it, so running the raise first would
        // hand it a value this pass had just written and it could no longer tell the two apart.
        var lowered = LowerOverRaisedLimits(notes);
        var adjusted = AdjustUpperLimits(notes);

        var result = new Result
        {
            VolumesEnabled = volumes,
            RoomsAdjusted = adjusted,
            RoomsLowered = lowered,
        };
        result.Notes.AddRange(notes);

        return result;
    }

    /// <summary>
    /// Turns on volume computation if it is off, so rooms clip against bounding ceilings.
    /// Returns true only when it actually changed the setting.
    /// </summary>
    public bool EnsureVolumes(List<string> notes)
    {
        try
        {
            var settings = AreaVolumeSettings.GetAreaVolumeSettings(_doc);
            if (settings.ComputeVolumes) return false;

            settings.ComputeVolumes = true;
            _doc.Regenerate();

            notes.Add("AUTO-FIX: 'Areas and Volumes' computation was OFF and has been enabled, so " +
                      "rooms now stop at bounding ceilings (floor finish to ceiling finish). If a room " +
                      "still reports 'no top boundary', its ceiling is not Room Bounding or the room's " +
                      "upper limit stops below it.");

            return true;
        }
        catch (Exception ex)
        {
            notes.Add($"WARNING: could not verify/enable volume computation: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Raises each room's Upper Offset just above the highest ceiling, floor or roof that
    /// overlaps it in plan. See the class remarks for why raise-only is the right rule.
    /// </summary>
    public int AdjustUpperLimits(List<string> notes)
    {
        if (!_settings.AutoAdjustLimits) return 0;

        var topBoxes = new List<BoundingBoxXYZ>();

        foreach (var element in Collect(BuiltInCategory.OST_Ceilings)
                     .Concat(Collect(BuiltInCategory.OST_Floors))
                     .Concat(Collect(BuiltInCategory.OST_Roofs)))
        {
            try
            {
                var box = element.get_BoundingBox(null);
                if (box is not null) topBoxes.Add(box);
            }
            catch
            {
                // No bounding box; cannot participate.
            }
        }

        var adjusted = 0;

        foreach (var room in new FilteredElementCollector(_doc)
                     .OfCategory(BuiltInCategory.OST_Rooms)
                     .WhereElementIsNotElementType()
                     .OfType<Room>())
        {
            try
            {
                if (room.Area <= 0) continue;

                var roomBox = room.get_BoundingBox(null);
                if (roomBox is null) continue;

                var neededTop = HighestCapTop(topBoxes, room, roomBox);

                // A FLAGGED ROOM STILL GOES THROUGH, even with no cap to raise for. The
                // raise-only path has nothing to do when HighestCapTop finds nothing, but the
                // FFL branch inside RaiseUpperOffset does - it looks the slab up itself - and
                // returning early here would skip it, ignoring the user's marker with no log
                // line to show for it.
                if (neededTop is null && !RoomFfsCap.IsFlagged(room)) continue;

                if (RaiseUpperOffset(room, roomBox, neededTop ?? 0.0)) adjusted++;
            }
            catch
            {
                // One room failing must not stop the pass.
            }
        }

        if (adjusted > 0)
        {
            _doc.Regenerate();   // rebuild room volumes before anything reads them
            notes.Add($"AUTO-ADJUST: raised the upper limit of {adjusted} room(s) so sloped " +
                      "ceilings/roofs now bound the full room volume (raise-only, highest " +
                      "overlapping ceiling + margin).");
        }

        return adjusted;
    }

    /// <summary>
    /// Brings back down the limits an EARLIER run raised too far, and nothing else.
    ///
    /// WHY THIS HAS TO EXIST SEPARATELY FROM THE RAISE
    ///   <see cref="RaiseUpperOffset"/> is raise-only on purpose, and that rule is right: the
    ///   tool cannot see why a limit was set, so lowering one by default would overrule a
    ///   modelling decision it does not understand. But raise-only has a consequence nobody
    ///   had to face until <see cref="HighestCapTop"/> was found selecting the wrong cap - a
    ///   limit raised by a BUG is equally permanent. Fixing the selection stops new rooms being
    ///   inflated; it does nothing for the rooms already carrying an inflated offset, because
    ///   the corrected rule computes a LOWER value and raise-only then declines to write it.
    ///   Køkken 1 kept a 16.75 ft offset reaching an exterior canopy a storey above its own
    ///   ceiling, and would have kept it forever.
    ///
    /// HOW IT STAYS INSIDE THE RAISE-ONLY SPIRIT
    ///   It never invents a height of its own. The floor it lowers to is exactly what
    ///   <see cref="HighestCapTop"/> - the corrected rule - says this room needs, so the
    ///   ceiling above it still sits inside the envelope with the usual margin and still does
    ///   the clipping. It only acts when the current offset exceeds that by more than the
    ///   margin, so a room already within tolerance is left alone rather than nudged.
    ///
    ///   A room with nothing overhead is skipped outright: with no cap to compute against there
    ///   is no evidence the limit is wrong, and a deliberately tall limit over an unroofed
    ///   space is exactly the modelling decision raise-only exists to protect.
    ///
    ///   An FFS-flagged room is skipped too - <see cref="RoomFfsCap"/> owns that room's limit
    ///   by explicit declaration, and this must not second-guess it.
    ///
    /// RUNS ON THE AUTOMATIC PASS TOO, not only from the ribbon - and that is a deliberate
    /// reversal. It was manual-only on the reasoning that a correction which LOWERS limits
    /// should happen when somebody asked for it and can read what it did. That reasoning does
    /// not survive what the inflated limit actually costs: it is the bound the paint takeoff's
    /// ceiling-gap search uses, so every room carrying one measures shaft and dormer walls
    /// metres above its own ceiling until somebody happens to press a button. A correction
    /// nobody runs is not a safer correction, it is an absent one.
    ///
    /// What keeps it honest instead of a button: it never invents a height, it only ever
    /// lowers to what <see cref="HighestCapTop"/> says the room needs, it declines rooms with
    /// nothing overhead and FFS-flagged rooms outright, and every room it touches is named in
    /// the report with its before and after.
    /// </summary>
    public int LowerOverRaisedLimits(List<string> notes)
    {
        var topBoxes = new List<BoundingBoxXYZ>();

        foreach (var element in Collect(BuiltInCategory.OST_Ceilings)
                     .Concat(Collect(BuiltInCategory.OST_Floors))
                     .Concat(Collect(BuiltInCategory.OST_Roofs)))
        {
            try
            {
                var box = element.get_BoundingBox(null);
                if (box is not null) topBoxes.Add(box);
            }
            catch
            {
                // No bounding box; cannot participate.
            }
        }

        var lowered = 0;
        var details = new List<string>();

        foreach (var room in new FilteredElementCollector(_doc)
                     .OfCategory(BuiltInCategory.OST_Rooms)
                     .WhereElementIsNotElementType()
                     .OfType<Room>())
        {
            try
            {
                if (room.Area <= 0) continue;
                if (RoomFfsCap.IsFlagged(room)) continue;

                var roomBox = room.get_BoundingBox(null);
                if (roomBox is null) continue;

                var neededTop = HighestCapTop(topBoxes, room, roomBox);
                if (neededTop is null) continue;   // nothing overhead: no evidence, no change

                var parameter = room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET);
                if (parameter is null || parameter.IsReadOnly) continue;

                // Same datum as the raise, for the same reason: the offset is measured from
                // the Upper Limit level, which is not necessarily the room's own level.
                var referenceZ = roomBox.Min.Z;
                try
                {
                    if (room.UpperLimit is not null) referenceZ = room.UpperLimit.Elevation;
                }
                catch
                {
                    // Upper Limit unreadable on some room states; the base level is the safe datum.
                }

                var wanted = neededTop.Value - referenceZ;
                var current = parameter.AsDouble();

                // Only a MATERIAL excess, so a room already within the margin is untouched
                // rather than nudged by a millimetre on every run.
                if (current <= wanted + FinishSettings.LimitMargin) continue;

                parameter.Set(wanted);
                lowered++;

                if (details.Count < 20)
                {
                    details.Add(
                        $"  {room.Number} {room.Name}: {current * 304.8:0} mm -> {wanted * 304.8:0} mm");
                }
            }
            catch
            {
                // One room failing must not stop the pass.
            }
        }

        if (lowered > 0)
        {
            _doc.Regenerate();   // rebuild room volumes before anything reads them

            notes.Add($"CORRECTED: {lowered} room(s) had an upper limit raised too far by an " +
                      "earlier run - above their own ceiling, to something a storey up - and " +
                      "have been brought back down to just clear the ceiling that actually " +
                      "bounds them:\n" + string.Join("\n", details) +
                      (lowered > details.Count ? $"\n  ... and {lowered - details.Count} more." : string.Empty));
        }

        return lowered;
    }

    private List<Element> Collect(BuiltInCategory category) =>
        [.. new FilteredElementCollector(_doc)
            .OfCategory(category)
            .WhereElementIsNotElementType()];

    // ------------------------------------------------------------------- the rule
    //
    // THE SINGLE DEFINITION of where a room's top is, shared with
    // <see cref="RoomLimitAdjuster"/>. That class runs the same correction against a
    // handful of rooms while the user works, rather than the whole model; only its
    // SCOPE differs, so only the collection of candidates belongs to it. The rule
    // itself lives here once.
    //
    // It was written out twice, and the two copies had already diverged: this one
    // refused to write an offset lower than the room's current one, the other did not,
    // so the incremental path could LOWER a limit that the full pass would have left
    // alone - against the raise-only rule both of them documented.

    /// <summary>
    /// The top of the highest ceiling, floor or roof overlapping this room in plan, plus
    /// the margin - or null when nothing qualifies.
    /// </summary>
    /// <param name="capBoxes">
    /// Candidate bounding boxes. The caller chooses how wide to cast: the whole model for
    /// a full pass, a bounding-box query per room for the incremental one.
    /// </param>
    internal static double? HighestCapTop(
        IEnumerable<BoundingBoxXYZ> capBoxes, Room room, BoundingBoxXYZ roomBox)
    {
        var baseZ = roomBox.Min.Z;

        // Just above the room's own base - inside the room's real footprint, never inside
        // the candidate itself.
        var probeZ = baseZ + 0.5;

        // EVERY QUALIFYING CANDIDATE, not a running maximum. Which of them actually caps THIS
        // room is a question that cannot be answered one box at a time - see the selection
        // below - so the filtering and the choosing are now two separate steps.
        var qualifying = new List<(double Bottom, double Top)>();

        foreach (var box in capBoxes)
        {
            // Plan (XY) overlap with the room's BOUNDING BOX - a cheap reject before the
            // real footprint test below.
            if (box.Max.X < roomBox.Min.X || box.Min.X > roomBox.Max.X ||
                box.Max.Y < roomBox.Min.Y || box.Min.Y > roomBox.Max.Y) continue;

            // Only elements starting clearly ABOVE this room's base (excludes the room's
            // own floor slab) and within a sane band (avoids grabbing storeys far above
            // and ballooning the room upward).
            if (box.Min.Z < baseZ + 1.0 || box.Min.Z > baseZ + FinishSettings.ScanBand) continue;

            // REAL FOOTPRINT OVERLAP. A bbox-vs-bbox pass alone is not enough: an L-shaped or
            // offset room's rectangular bounding box, or a small dormer/monitor sitting near
            // one corner, can share bounding rectangles without the two shapes ever actually
            // touching. Sampling the candidate's own footprint corners (+ centre) against the
            // room's true, possibly concave boundary is what a rectangle-vs-rectangle test
            // cannot do - only a genuine hit may raise this room's limit into that element's
            // airspace. Without it, an unrelated ceiling/roof/dormer drags this room's volume
            // - and later, via MeasureInteriorSlabs/MeasureInteriorWalls, that element's OWN
            // faces too - into a room it never actually bounds.
            XYZ[] corners =
            [
                new(box.Min.X, box.Min.Y, probeZ),
                new(box.Max.X, box.Min.Y, probeZ),
                new(box.Min.X, box.Max.Y, probeZ),
                new(box.Max.X, box.Max.Y, probeZ),
                new((box.Min.X + box.Max.X) / 2.0, (box.Min.Y + box.Max.Y) / 2.0, probeZ),
            ];

            var hit = false;
            foreach (var point in corners)
            {
                try
                {
                    if (!room.IsPointInRoom(point)) continue;
                    hit = true;
                    break;
                }
                catch
                {
                    // Undecidable point; try the next one.
                }
            }

            if (!hit) continue;

            qualifying.Add((box.Min.Z, box.Max.Z));
        }

        if (qualifying.Count == 0) return null;

        // REVERTED TO THE MAXIMUM (2026-09-22), after the "nearest cap" rule below reached a
        // live model and wrote a limit BELOW a room's own ceiling.
        //
        // WHAT WENT WRONG. "Nearest cap = lowest bottom" assumes everything that qualifies is
        // ABOVE the room. It is not: an intermediate floor slab standing INSIDE a room's height
        // - a mezzanine, or the slab whose Room Bounding someone has just switched on - starts
        // lower than the ceiling and therefore wins that comparison. On Køkken 1 a slab
        // spanning z -8.858..-8.202 was selected over the real ceiling at -4.101, giving a cap
        // of -7.702 against a room whose base is -13.123. The room lost its ceiling, 40% of its
        // volume and 80% of its ceiling area.
        //
        // The band filter cannot catch it either: it only excludes candidates within 1 ft of
        // the room's base, to skip the room's own floor slab. A mezzanine 4.3 ft up sails past.
        //
        // The maximum is wrong in the other direction - it is what let an exterior canopy a
        // storey above raise this same room's limit by 5.8 ft - but that failure inflates a
        // limit, which only ever adds headroom Revit's own clipping then ignores. This one
        // REMOVED the room's ceiling. Between a rule that over-reaches and a rule that
        // truncates, the over-reaching one is the safe default to sit on while the real
        // selection is worked out.
        //
        // WHAT THE REAL FIX NEEDS, so the next attempt does not repeat this one: a cap has to
        // be distinguished from an obstruction INSIDE the room, and neither bottom-most nor
        // top-most does that. The distinguishing facts available are footprint coverage (a
        // ceiling covers the room, a mezzanine covers part of it) and the room's own computed
        // volume before anything is written. Both need measuring against real models first.
        return qualifying.Max(c => c.Top) + FinishSettings.LimitMargin;

#pragma warning disable CS0162 // Unreachable - kept for the next attempt, see above.
        // THE ROOM'S OWN CAP, NOT THE HIGHEST THING IN THE BAND.
        //
        // THE BUG THIS FIXES. This used to keep a running maximum over every qualifying
        // candidate, which is only correct when everything in the band belongs to the same
        // storey. It does not: a basement kitchen with a perfectly good flat ceiling 300 mm
        // above its head had its limit raised 5.8 ft to clear an exterior CANOPY roof
        // ("Baldakin") sitting a whole storey up at ground level, because the canopy is
        // genuinely above the room in plan and genuinely inside the 20 ft scan band. Both of
        // the earlier tests - plan overlap, then real footprint sampling - pass for it, and
        // neither is wrong; the selection was.
        //
        // Revit clips a room at the NEAREST bounding element above it, so the limit only ever
        // needs to clear THAT element. Anything starting above it is another storey's business
        // and must not drag this room's envelope up into it.
        //
        // WHY NOT SIMPLY THE LOWEST CANDIDATE. A sloped ceiling must be cleared at its HIGH
        // point or the room is sliced flat and loses the wedge beneath the slope - which is the
        // whole reason this rule exists. So the nearest cap is chosen by where it STARTS
        // (lowest bottom), and then cleared by where it ENDS (its own top).
        //
        // The zone is inclusive rather than a single element, because a ceiling is often
        // several pieces, and a stepped one has pieces starting at slightly different heights.
        // Anything starting within the nearest cap's own span plus the margin is part of the
        // same cap; anything starting above that is not.
        var nearest = qualifying.OrderBy(c => c.Bottom).First();
        var zoneTop = nearest.Top + FinishSettings.LimitMargin;

        var capTop = qualifying
            .Where(c => c.Bottom <= zoneTop)
            .Max(c => c.Top);

        return capTop + FinishSettings.LimitMargin;
#pragma warning restore CS0162
    }

    /// <summary>
    /// Raises one room's Upper Offset to clear <paramref name="neededTop"/>. Returns true
    /// only when it actually wrote.
    ///
    /// RAISE-ONLY, enforced here rather than trusted to each caller. The room's envelope
    /// top is not its limit: a room already clipped low by a ceiling can sit far below a
    /// limit deliberately set high, so deriving the offset from the cap alone and writing
    /// it unconditionally is how a correct limit gets quietly reduced.
    /// </summary>
    internal static bool RaiseUpperOffset(Room room, BoundingBoxXYZ roomBox, double neededTop)
    {
        // THE ONE CASE WHERE LOWERING IS CORRECT, checked here because this is the single
        // place a room's upper offset is written - both the full pass and the per-room
        // adjuster come through it, so the exemption cannot be applied to one and missed on
        // the other.
        //
        // A room whose Comments carry the FFS marker has been declared, by hand, to stop at
        // the slab above rather than leak through the hole in it. Raise-only exists so the
        // tool never overrules a modelling decision it cannot see the reason for; this room
        // states the reason, so the guard does not apply to it. Without this branch the
        // automation would raise the cap straight back on the next model change.
        if (RoomFfsCap.IsFlagged(room)) return RoomFfsCap.Apply(room, roomBox);

        // Already high enough. The half-margin slack stops the tool nudging the same room
        // by a millimetre on every pass, which would mark the model changed forever.
        if (roomBox.Max.Z >= neededTop - FinishSettings.LimitMargin * 0.5) return false;

        var parameter = room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET);
        if (parameter is null || parameter.IsReadOnly) return false;

        // The offset is measured from the Upper Limit level, which is not necessarily the
        // room's own level. Measuring from the wrong datum is how a room ends up a storey
        // too tall.
        var referenceZ = roomBox.Min.Z;
        try
        {
            if (room.UpperLimit is not null) referenceZ = room.UpperLimit.Elevation;
        }
        catch
        {
            // Upper Limit unreadable on some room states; the base level is the safe datum.
        }

        var offset = neededTop - referenceZ;
        if (offset <= parameter.AsDouble()) return false;

        parameter.Set(offset);
        return true;
    }
}
