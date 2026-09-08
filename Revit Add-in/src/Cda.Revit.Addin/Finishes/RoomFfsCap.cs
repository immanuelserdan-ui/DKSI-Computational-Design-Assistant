using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Finishes;

/// <summary>
/// Caps a room at the finished top of the slab above it, for rooms whose volume would
/// otherwise escape upward through a hole in that slab.
///
/// THE CASE, MEASURED
///   Kælderrum 4 sits under the Terræn slab, whose top is at -3.281 ft. Its own volume
///   reaches +2.461 ft - 1.75 m ABOVE that slab - because the stair opening leaves a hole the
///   slab cannot bound across, so the room leaks up through it to its Upper Limit. Capping at
///   the slab's top makes the room exactly 3.000 m, which is what it is.
///
/// WHY A COMMENT AND NOT A RULE
///   Because leaking upward is sometimes CORRECT. A double-height space, a void, an atrium -
///   all reach past the slab above on purpose, and a tool that capped every room it found
///   above a slab would quietly flatten them. There is nothing in the geometry that separates
///   "leaked through a stair hole" from "deliberately open to above": both are a room whose
///   box passes a slab. So the distinction is a human decision, typed once, per room.
///
///   Comments is the right field for it. This add-in's own marks live in Extensible Storage
///   precisely BECAUSE users type in Comments (see ElementStamp) - and that is the point here:
///   the direction is reversed. This is the user instructing the tool, which is what Comments
///   is for, and the same convention the casework cutter already uses with '#nocut-auto'.
///
/// WHY IT OVERRIDES RAISE-ONLY
///   RoomBoundaryAdjuster.RaiseUpperOffset never lowers a limit, deliberately: lowering one a
///   user set would be the tool overruling a modelling decision it cannot see the reason for.
///   A room carrying this comment IS that reason, stated explicitly - so it is the one case
///   where lowering is not a guess. Without the override, the automation would raise the cap
///   back on the next model change and the comment would appear to do nothing.
/// </summary>
internal static class RoomFfsCap
{
    /// <summary>
    /// How far above a room's base to look for the slab that caps it. Reuses the finish
    /// engine's scan band so this agrees with what the rest of the room logic considers
    /// "above this room" rather than inventing a second answer.
    /// </summary>
    private static double ScanBand => FinishSettings.ScanBand;

    /// <summary>The spelling shown in tooltips and reports. Recognition is wider - see below.</summary>
    public const string CanonicalMarker = "Align at FFL";

    /// <summary>
    /// True when the user has asked for this room to be capped at the slab above.
    ///
    /// MATCHED LOOSELY, ON PURPOSE. A literal "Align at FFS" was the first attempt and it
    /// failed on the first real use: the marker typed was "Align in FFL" - a different
    /// preposition AND a different acronym - so nothing matched and the room silently kept its
    /// old limit. That is the worst outcome for a feature driven by hand-typed text, because
    /// there is nothing to see: no error, no log line, just no effect.
    ///
    /// The rule is therefore "the word ALIGN, plus FFL or FFS". It does not care about the
    /// preposition, the order, the case, or what else the comment says. FFL - finished floor
    /// LEVEL - is the usual term in Danish practice and is what the reports print; FFS is
    /// accepted because it was the first spelling used here and models may already carry it.
    ///
    /// It stays specific enough not to fire by accident: an ordinary comment that happens to
    /// contain "align" will not also contain "FFL".
    /// </summary>
    public static bool IsFlagged(Element room, FinishSettings? _ = null)
    {
        try
        {
            var comments = room
                .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();

            if (string.IsNullOrWhiteSpace(comments)) return false;

            if (!comments.Contains("align", StringComparison.OrdinalIgnoreCase)) return false;

            return comments.Contains("FFL", StringComparison.OrdinalIgnoreCase) ||
                   comments.Contains("FFS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The finished top of the lowest floor above the room, and the floor it came from.
    ///
    /// THE BOUNDING BOX TOP IS THE FINISHED TOP, and that is not a shortcut. A floor's box
    /// encloses its whole compound structure, so its Max.Z is the top of the topmost layer -
    /// the finish where there is one, the structure where there is not. Reading the layer
    /// table instead would give the same answer on this model and a different one the moment
    /// a floor is edited in place.
    ///
    /// LOWEST ABOVE, not highest: the room is capped by the first slab over it. Taking the
    /// highest would cap a basement at the roof.
    /// </summary>
    public static (double TopZ, ElementId Floor)? Datum(Document doc, BoundingBoxXYZ roomBox)
    {
        var baseZ = roomBox.Min.Z;

        var search = new Outline(
            new XYZ(roomBox.Min.X, roomBox.Min.Y, baseZ),
            new XYZ(roomBox.Max.X, roomBox.Max.Y, baseZ + ScanBand));

        (double TopZ, ElementId Floor)? best = null;

        // The SOFFIT of the winner so far, kept separately because "lowest slab above" is
        // decided by undersides while the answer returned is a top. Comparing a candidate's
        // underside against the incumbent's TOP - which this did - mixes the two: a thin slab
        // sitting higher could displace a thick one sitting lower, purely because the thick
        // one's top reached above the thin one's underside. It gave the right answer on this
        // model only because one candidate survives the filters.
        var bestSoffit = double.MaxValue;

        foreach (var category in new[]
                 {
                     BuiltInCategory.OST_Floors,
                     BuiltInCategory.OST_StructuralFoundation,
                 })
        {
            IList<Element> candidates;

            try
            {
                candidates = new FilteredElementCollector(doc)
                    .OfCategory(category)
                    .WhereElementIsNotElementType()
                    .WherePasses(new BoundingBoxIntersectsFilter(search))
                    .ToElements();
            }
            catch
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                var box = candidate.get_BoundingBox(null);
                if (box is null) continue;

                // Must sit ABOVE the room's base, or the room's own floor slab would cap it
                // at its own feet and collapse the volume to nothing.
                if (box.Min.Z <= baseZ + FinishSettings.LimitMargin) continue;

                // Must actually be over the room in plan. A slab alongside is not a cap.
                if (!OverlapsInPlan(box, roomBox)) continue;

                if (box.Min.Z >= bestSoffit) continue;

                bestSoffit = box.Min.Z;
                best = (box.Max.Z, candidate.Id);
            }
        }

        return best;
    }

    /// <summary>
    /// Sets the room's Upper Offset so its cap lands on the slab's finished top. Returns true
    /// only when it wrote. Caller owns the transaction.
    /// </summary>
    public static bool Apply(Room room, BoundingBoxXYZ roomBox)
    {
        var doc = room.Document;

        var datum = Datum(doc, roomBox);

        if (datum is null)
        {
            Log.Warn($"Room {room.Id.Value} is marked for FFS capping but no slab was found " +
                     "above it; its limit is unchanged.");
            return false;
        }

        var parameter = room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET);
        if (parameter is null || parameter.IsReadOnly) return false;

        // The offset is measured from the Upper Limit LEVEL, which is not necessarily the
        // room's own. Measuring from the wrong datum is how a room ends up a storey too tall -
        // the same trap RaiseUpperOffset documents.
        var referenceZ = roomBox.Min.Z;

        try
        {
            if (room.UpperLimit is not null) referenceZ = room.UpperLimit.Elevation;
        }
        catch
        {
            // Unreadable on some room states; the base is the safe datum.
        }

        var offset = datum.Value.TopZ - referenceZ;

        // ONE MILLIMETRE, not RaiseUpperOffset's half-margin. That one is 76 mm, which is the
        // right amount of slack for "is this room tall enough to clear its ceiling" and far
        // too much for "does this cap sit ON the slab": an alignment could be 70 mm out and
        // the tool would call it done.
        //
        // Tight is safe here because both sides are stable model values - the slab's top and
        // the level's elevation - so the computed offset is identical on every pass and
        // cannot oscillate the way a geometry-derived figure might.
        if (Math.Abs(offset - parameter.AsDouble()) <= Measure.FromMillimetres(1.0)) return false;

        var wasTop = roomBox.Max.Z;

        parameter.Set(offset);

        Log.Info(
            $"Room {room.Id.Value} capped at FFS: top {Measure.ToMillimetres(wasTop):0} mm -> " +
            $"{Measure.ToMillimetres(datum.Value.TopZ):0} mm, from floor {datum.Value.Floor.Value}.");

        return true;
    }

    private static bool OverlapsInPlan(BoundingBoxXYZ cap, BoundingBoxXYZ room) =>
        cap.Min.X < room.Max.X && room.Min.X < cap.Max.X &&
        cap.Min.Y < room.Max.Y && room.Min.Y < cap.Max.Y;
}
