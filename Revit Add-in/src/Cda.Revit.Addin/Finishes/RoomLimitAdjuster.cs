using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Finishes;

/// <summary>
/// Raises a room's Upper Offset so the ceiling above it can clip the volume.
///
/// The Revit rule this exists for is easy to get backwards: a room's volume can NEVER
/// rise above Upper Limit + Offset. Bounding ceilings only clip WITHIN that allowance.
/// So a limit set flush with the ceiling does not make the room stop at the ceiling —
/// it makes a sloped ceiling slice the room flat, losing the wedge underneath the slope.
///
/// The fix is counter-intuitive and deliberate: push the limit box HIGHER than the
/// ceiling, and let the ceiling do the clipping inside it. That is why a corrected room
/// still draws a tall box in section — the box is a search envelope, not the volume.
///
/// This is the same rule <see cref="RoomBoundaryAdjuster"/> applies during a full pass,
/// and it is literally the same code: <see cref="RoomBoundaryAdjuster.HighestCapTop"/> and
/// <see cref="RoomBoundaryAdjuster.RaiseUpperOffset"/>. What lives here is only the narrow
/// SCOPE - a bounding-box query per room, so correcting three rooms the moment they change
/// costs three small queries instead of a full-model sweep. That is what makes it cheap
/// enough to run while the user is still working.
///
/// The rule was previously written out again here. Sharing it is not tidiness: the two
/// copies had already drifted over whether the write is raise-only. See the remarks on
/// <see cref="RoomBoundaryAdjuster.RaiseUpperOffset"/>.
/// </summary>
internal static class RoomLimitAdjuster
{
    private static readonly BuiltInCategory[] CappingCategories =
    [
        BuiltInCategory.OST_Ceilings,
        BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_Roofs,
    ];

    /// <summary>
    /// Raises the Upper Offset of each room that needs it. Returns how many were changed.
    ///
    /// Raise-only, on purpose: lowering a limit the user deliberately set would be the
    /// tool overruling a modelling decision it cannot see the reason for.
    /// </summary>
    public static int Adjust(Document doc, IEnumerable<Room> rooms)
    {
        var adjusted = 0;

        foreach (var room in rooms)
        {
            try
            {
                if (AdjustOne(doc, room)) adjusted++;
            }
            catch (Exception ex)
            {
                // One awkward room must not stop the rest being corrected.
                Log.Warn($"Room {room.Id} upper limit not adjusted: {ex.Message}");
            }
        }

        return adjusted;
    }

    private static bool AdjustOne(Document doc, Room room)
    {
        if (room.Area <= 0) return false;                     // unplaced: nothing to bound

        var roomBox = room.get_BoundingBox(null);
        if (roomBox is null) return false;

        var neededTop = RoomBoundaryAdjuster.HighestCapTop(CapBoxesAbove(doc, roomBox), roomBox);
        if (neededTop is null) return false;

        return RoomBoundaryAdjuster.RaiseUpperOffset(room, roomBox, neededTop.Value);
    }

    /// <summary>
    /// The bounding boxes of every ceiling, slab and roof near enough to this room to
    /// matter, found with a bounding-box query rather than a full-model collection.
    ///
    /// This narrowing is the whole reason this class exists; deciding what the boxes MEAN
    /// is <see cref="RoomBoundaryAdjuster.HighestCapTop"/>'s job, and is not repeated here.
    /// The query's own band matches the one that rule applies, so it rejects early what
    /// would be rejected later anyway.
    /// </summary>
    private static List<BoundingBoxXYZ> CapBoxesAbove(Document doc, BoundingBoxXYZ roomBox)
    {
        var baseZ = roomBox.Min.Z;

        var search = new Outline(
            new XYZ(roomBox.Min.X, roomBox.Min.Y, baseZ),
            new XYZ(roomBox.Max.X, roomBox.Max.Y, baseZ + FinishSettings.ScanBand));

        var boxes = new List<BoundingBoxXYZ>();

        foreach (var category in CappingCategories)
        {
            var caps = new FilteredElementCollector(doc)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(search))
                .ToElements();

            foreach (var cap in caps)
            {
                var box = cap.get_BoundingBox(null);
                if (box is not null) boxes.Add(box);
            }
        }

        return boxes;
    }

    private const double FeetToMm = 304.8;

    /// <summary>
    /// Explains, for one room, exactly what this adjuster sees and what it would do.
    ///
    /// It deliberately calls the same <see cref="RoomBoundaryAdjuster.HighestCapTop"/> the
    /// fix uses, so the report cannot disagree with the behaviour. A diagnostic that
    /// re-derives the answer separately eventually tells you the code is fine while the
    /// code does something else.
    /// </summary>
    public static string Explain(Document doc, Room room)
    {
        var lines = new List<string>();

        var volumesOn = AreaVolumeSettings.GetAreaVolumeSettings(doc).ComputeVolumes;
        lines.Add($"Areas and Volumes: {(volumesOn ? "ON" : "OFF")}");

        if (!volumesOn)
        {
            lines.Add("  -> With volumes off, Revit computes no room volume at all and no");
            lines.Add("     ceiling clips anything. Nothing else on this list can be right.");
        }

        lines.Add($"Room: {room.Number} {room.Name}");
        lines.Add($"  Area   : {room.Area * 0.09290304:0.00} m2");
        lines.Add($"  Volume : {(volumesOn ? $"{room.Volume * 0.0283168:0.00} m3" : "not computed")}");

        var level = room.Level;
        lines.Add($"  Level  : {level?.Name ?? "?"} at {(level?.Elevation ?? 0) * FeetToMm:0} mm");

        Level? upper = null;
        try { upper = room.UpperLimit; } catch { /* unreadable on some room states */ }
        lines.Add($"  Upper Limit : {upper?.Name ?? "(same level)"} at {(upper?.Elevation ?? level?.Elevation ?? 0) * FeetToMm:0} mm");

        var offset = room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET);
        lines.Add($"  Limit Offset: {(offset is null ? "?" : $"{offset.AsDouble() * FeetToMm:0} mm")}" +
                  (offset is { IsReadOnly: true } ? "  (READ-ONLY - cannot be adjusted)" : string.Empty));

        var box = room.get_BoundingBox(null);
        if (box is null)
        {
            lines.Add("  No bounding box - the room is unplaced, so there is nothing to adjust.");
            return string.Join(Environment.NewLine, lines);
        }

        lines.Add($"  Envelope top: {box.Max.Z * FeetToMm:0} mm");
        lines.Add(string.Empty);

        var needed = RoomBoundaryAdjuster.HighestCapTop(CapBoxesAbove(doc, box), box);

        if (needed is null)
        {
            lines.Add("No ceiling, slab or roof found above this room within the scan band.");
            lines.Add($"(Scan band is {FinishSettings.ScanBand * FeetToMm / 1000:0.0} m above the room base.)");
            lines.Add("-> Nothing to raise the limit to. If there IS a ceiling above, it is");
            lines.Add("   either outside the band or its bounding box does not overlap the room.");
            return string.Join(Environment.NewLine, lines);
        }

        lines.Add($"Highest cap above (plus margin): {needed.Value * FeetToMm:0} mm");

        if (box.Max.Z >= needed - (FinishSettings.LimitMargin * 0.5))
        {
            lines.Add("-> The envelope is ALREADY high enough. No adjustment needed.");
            lines.Add("   A tall box in section is expected: the envelope must sit above the");
            lines.Add("   ceiling so the ceiling can clip the volume inside it.");
        }
        else
        {
            var referenceZ = upper?.Elevation ?? box.Min.Z;
            var wanted = needed.Value - referenceZ;

            // Mirrors the raise-only guard in RoomBoundaryAdjuster.RaiseUpperOffset, so the
            // report cannot promise a change the adjuster would decline to make.
            if (offset is not null && wanted <= offset.AsDouble())
            {
                lines.Add($"-> Computes a Limit Offset of {wanted * FeetToMm:0} mm, which is NOT higher");
                lines.Add("   than the current one, so the raise-only rule leaves the room alone.");
            }
            else
            {
                lines.Add($"-> WOULD RAISE Limit Offset to {wanted * FeetToMm:0} mm.");
            }
        }

        var notBounding = CapsNotRoomBounding(doc, [room]);
        lines.Add(string.Empty);

        if (notBounding.Count == 0)
        {
            lines.Add("All ceilings/roofs above this room are Room Bounding. Good.");
        }
        else
        {
            lines.Add($"WARNING: {notBounding.Count} ceiling/roof above this room is NOT Room Bounding.");
            lines.Add("  -> Nothing clips the room, so it fills the whole envelope and every");
            lines.Add("     finish area from it is measured against the wrong surface.");
            lines.Add("     This is the fault that looks normal in plan. Ids: " +
                      string.Join(", ", notBounding.Take(10)));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Reports ceilings and roofs sitting over rooms that are NOT room-bounding.
    ///
    /// This is the failure the upper-limit fix cannot compensate for, and the one that
    /// actually produces wrong numbers. If a ceiling is not room-bounding, nothing clips
    /// the room and it fills the whole raised envelope — so the volume, and every finish
    /// area derived from it, is measured against the wrong surface. The geometry looks
    /// perfectly normal in plan.
    /// </summary>
    public static List<ElementId> CapsNotRoomBounding(Document doc, IEnumerable<Room> rooms)
    {
        var offenders = new List<ElementId>();
        var seen = new HashSet<ElementId>();

        foreach (var room in rooms)
        {
            var roomBox = room.get_BoundingBox(null);
            if (roomBox is null) continue;

            var search = new Outline(
                new XYZ(roomBox.Min.X, roomBox.Min.Y, roomBox.Min.Z),
                new XYZ(roomBox.Max.X, roomBox.Max.Y, roomBox.Min.Z + FinishSettings.ScanBand));

            foreach (var category in new[] { BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Roofs })
            {
                var caps = new FilteredElementCollector(doc)
                    .OfCategory(category)
                    .WhereElementIsNotElementType()
                    .WherePasses(new BoundingBoxIntersectsFilter(search))
                    .ToElements();

                foreach (var cap in caps)
                {
                    if (!seen.Add(cap.Id)) continue;

                    var bounding = cap.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
                    if (bounding is not null && bounding.AsInteger() == 0)
                        offenders.Add(cap.Id);
                }
            }
        }

        return offenders;
    }
}
