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

        public List<string> Notes { get; } = [];

        public bool ChangedAnything => VolumesEnabled || RoomsAdjusted > 0;
    }

    /// <summary>
    /// Runs both corrections. Caller owns the transaction - this writes to the document.
    /// </summary>
    public Result Run()
    {
        var notes = new List<string>();

        var volumes = EnsureVolumes(notes);
        var adjusted = AdjustUpperLimits(notes);

        var result = new Result { VolumesEnabled = volumes, RoomsAdjusted = adjusted };
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

                var baseZ = roomBox.Min.Z;
                double? neededTop = null;

                foreach (var box in topBoxes)
                {
                    // Plan (XY) overlap with the room?
                    if (box.Max.X < roomBox.Min.X || box.Min.X > roomBox.Max.X ||
                        box.Max.Y < roomBox.Min.Y || box.Min.Y > roomBox.Max.Y) continue;

                    // Only elements starting clearly ABOVE this room's base (excludes the
                    // room's own floor slab) and within a sane band (avoids grabbing
                    // storeys far above and ballooning the room upward).
                    if (box.Min.Z < baseZ + 1.0 || box.Min.Z > baseZ + FinishSettings.ScanBand) continue;

                    var top = box.Max.Z + FinishSettings.LimitMargin;
                    if (neededTop is null || top > neededTop) neededTop = top;
                }

                if (neededTop is null) continue;
                if (roomBox.Max.Z >= neededTop - FinishSettings.LimitMargin * 0.5) continue;

                var parameter = room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET);
                if (parameter is null || parameter.IsReadOnly) continue;

                var referenceZ = baseZ;
                try
                {
                    if (room.UpperLimit is not null) referenceZ = room.UpperLimit.Elevation;
                }
                catch
                {
                    // No upper limit level; measure from the room base.
                }

                var offset = neededTop.Value - referenceZ;
                if (offset > parameter.AsDouble())
                {
                    parameter.Set(offset);
                    adjusted++;
                }
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

    private List<Element> Collect(BuiltInCategory category) =>
        [.. new FilteredElementCollector(_doc)
            .OfCategory(category)
            .WhereElementIsNotElementType()];
}
