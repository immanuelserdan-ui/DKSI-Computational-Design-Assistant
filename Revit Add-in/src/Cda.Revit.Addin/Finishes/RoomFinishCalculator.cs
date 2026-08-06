using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Finishes;

/// <summary>One per-room per-material CSV row.</summary>
public sealed record FinishCsvRow(
    string RoomNumber, string RoomName, string Surface,
    string Material, string MaterialCode, bool Painted, double AreaSqM);

public sealed class FinishResult
{
    public required IReadOnlyList<string> Report { get; init; }
    public required IReadOnlyList<FinishCsvRow> CsvRows { get; init; }
    public required int Processed { get; init; }
    public required int SkippedUnplaced { get; init; }
    public required int ElementsWritten { get; init; }

    /// <summary>Elements that received Lejlighed / Rum nr / Rum.</summary>
    public required int ElementsTagged { get; init; }

    /// <summary>Elements carrying finish for more than one room, so their identity is one of several.</summary>
    public required int SharedElements { get; init; }
}

/// <summary>
/// Port of "RoomFinishAreas 7-23-26.dyn" - room finish surface areas written to room and
/// element parameters.
///
/// HOW WALLS ARE MEASURED
///   GEOMETRIC (primary): for each wall bounding the room, the wall's real side faces are
///   harvested from its full solid geometry - the actual surfaces of the outermost finish
///   layer, whose geometry ALREADY excludes every cut (door/window openings, casework
///   voids, reveals, edited profiles). That face is clipped to the room by intersecting it
///   with the room's boundary subface. Result: exact net finish area per wall per room,
///   with no deduction parameters needed.
///
///   FALLBACK (automatic, per wall): if the geometric method fails for a wall (curved,
///   linked, non-planar face, boolean failure), that wall uses planar room-subface area
///   minus arithmetic deductions. Deductions are skipped for walls measured geometrically,
///   because those are already net.
///
/// The caller owns the transaction: this writes parameters, enables Areas and Volumes,
/// and raises room upper limits.
/// </summary>
public sealed class RoomFinishCalculator
{
    private readonly Document _doc;
    private readonly FinishSettings _settings;
    private readonly FinishGeometry _geometry;
    private readonly RevealMeasurer _reveals;

    private readonly List<string> _report = [];
    private readonly List<FinishCsvRow> _csvRows = [];

    // Per-ELEMENT finish totals, for wall/floor/ceiling schedules. A wall between two
    // rooms sums both room-side contributions.
    private readonly Dictionary<long, double> _elemWallArea = [];
    private readonly Dictionary<long, double> _elemWallPaint = [];
    private readonly Dictionary<long, double> _elemFloorArea = [];
    private readonly Dictionary<long, double> _elemFloorPaint = [];
    private readonly Dictionary<long, double> _elemCeilingArea = [];
    private readonly Dictionary<long, double> _elemCeilingPaint = [];

    /// <summary>
    /// Per element, how much finish area each room contributed to it — the evidence behind
    /// "which room does this wall belong to?".
    ///
    /// It has to be a tally rather than a single answer because a base wall bounds two
    /// rooms and gets a face from each. The element carries ONE set of Lejlighed/Rum nr/Rum
    /// values, so the room with the largest contribution wins and the rest are reported.
    /// Picking by area rather than by first-seen makes the answer independent of the order
    /// rooms happen to be collected in, which is the difference between a stable schedule
    /// and one that reshuffles every run.
    /// </summary>
    private readonly Dictionary<long, Dictionary<long, double>> _elemRoomClaims = [];

    /// <summary>Elements more than one room contributed finish area to.</summary>
    private readonly HashSet<long> _sharedElements = [];

    /// <summary>Identity parameters absent from the elements that needed them.</summary>
    private readonly HashSet<string> _identityMissing = [];

    /// <summary>Walls carrying more than one paint colour on their room face.</summary>
    private readonly HashSet<long> _multipaintWalls = [];

    /// <summary>Elements another user holds, so this pass could not write to them.</summary>
    private readonly HashSet<long> _lockedElements = [];

    private List<Element> _allOpenings = [];
    /// <summary>
    /// Openings whose host-wall returns are measured for paint: doors, and now windows.
    /// Distinct from <c>_allOpenings</c>, which is the set DEDUCTED from wall faces.
    /// </summary>
    private List<Element> _revealOpenings = [];
    private List<Element> _allCasework = [];
    private List<Element> _interiorSlabs = [];
    private List<Element> _interiorWalls = [];

    /// <summary>
    /// Answers "what is overhead?" for rooms nothing bounds from above. Built with the rest
    /// of the collections, so its candidate lists are as fresh as theirs.
    /// </summary>
    private CeilingFallbackResolver? _ceilingFallback;

    /// <summary>
    /// Rooms whose ceiling area came from the fallback chain, per source. A room that took
    /// part of its ceiling from a slab and part from the roof is counted under both, so
    /// these sum to more than <see cref="_fallbackRoomCount"/>.
    /// </summary>
    private readonly Dictionary<string, int> _fallbackRooms = [];

    private int _fallbackRoomCount;

    /// <summary>Exterior placeholder rooms skipped this pass.</summary>
    private int _exteriorRooms;

    /// <summary>
    /// Elements bounding a skipped exterior room. Collected so a value written by an EARLIER
    /// run - before those rooms were skipped - can be cleared rather than left frozen on the
    /// wall. An element in here that no interior room also claimed has no business carrying
    /// an interior finish area.
    /// </summary>
    private readonly HashSet<long> _exteriorElements = [];

    /// <summary>Cached name test per room id, so the identity tie-break is not re-resolving.</summary>
    private readonly Dictionary<long, bool> _exteriorRoomCache = [];

    /// <summary>
    /// Painted area per element PER ROOM. The parallel of <see cref="_elemRoomClaims"/> for
    /// paint, and what makes a room-consistent paint figure possible at all.
    /// </summary>
    private readonly Dictionary<long, Dictionary<long, double>> _elemRoomPaint = [];

    /// <summary>Paint measured for a room other than the one its element is attributed to.</summary>
    private double _unattributedPaint;

    private int _unattributedElements;

    private readonly ElementId _sepLineCategory = new(BuiltInCategory.OST_RoomSeparationLines);
    private readonly ElementId _ceilingCategory = new(BuiltInCategory.OST_Ceilings);
    private readonly ElementId _roofCategory = new(BuiltInCategory.OST_Roofs);
    private readonly ElementId _floorCategory = new(BuiltInCategory.OST_Floors);
    private readonly List<ElementId> _slabCategories = [];

    public RoomFinishCalculator(Document doc, FinishSettings settings)
    {
        _doc = doc;
        _settings = settings;
        _geometry = new FinishGeometry(doc);
        _reveals = new RevealMeasurer(doc, _geometry, settings);

        _slabCategories.Add(_floorCategory);

        // Structural foundation slabs (slab-on-grade). The enum member is SINGULAR and can
        // vary or be absent across API versions, so resolve it defensively.
        if (Enum.IsDefined(typeof(BuiltInCategory), BuiltInCategory.OST_StructuralFoundation))
            _slabCategories.Add(new ElementId(BuiltInCategory.OST_StructuralFoundation));
    }

    public FinishResult Run()
    {
        PreflightVolumes();
        CollectDeductibles();
        AutoAdjustUpperLimits();

        var options = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
        };
        var calculator = new SpatialElementGeometryCalculator(_doc, options);

        var rooms = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .OfType<Room>()
            .ToList();

        var processed = 0;
        var skippedUnplaced = 0;

        foreach (var room in rooms)
        {
            try
            {
                if (room.Area <= 0)
                {
                    skippedUnplaced++;
                    continue;
                }

                if (!SpatialElementGeometryCalculator.CanCalculateGeometry(room))
                {
                    _report.Add($"Skipped (geometry not computable): {RoomLabel(room)}");
                    continue;
                }

                // OUTDOORS. Measured before this existed, and what it measured was the
                // outside face of the building in interior paint. See
                // FinishSettings.ExteriorRoomPrefix.
                //
                // Still visited, though, and that is the point: the elements it bounds are
                // collected so any value a PREVIOUS run wrote onto them can be cleared. A
                // skip alone would leave the wrong numbers frozen on those walls forever,
                // because WriteElementTotals only ever writes elements the pass touched.
                if (IsExteriorRoom(room))
                {
                    _exteriorRooms++;
                    CollectExteriorElements(room, calculator);
                    continue;
                }
            }
            catch
            {
                _report.Add("Skipped (invalid element)");
                continue;
            }

            // Same guard as the element writes: a room held by another user is skipped
            // rather than allowed to abort the pass for everyone else's rooms too.
            if (!Worksharing.CanWrite(_doc, room.Id))
            {
                _lockedElements.Add(room.Id.Value);
                continue;
            }

            if (MeasureRoom(room, calculator)) processed++;
        }

        var elementsWritten = WriteElementTotals();
        var elementsTagged = WriteRoomIdentity();

        // AFTER both writes, so "did any interior room claim this element?" is answerable.
        var elementsCleared = ClearExteriorOnlyElements();

        _report.Insert(0,
            $"[v15 port] SUMMARY: measured {processed} placed room(s); ignored " +
            $"{skippedUnplaced} unplaced/unenclosed room(s). Wall mode: " +
            (_settings.UseGeometric
                ? "GEOMETRIC (real finish faces, cuts excluded) with per-wall arithmetic fallback."
                : "arithmetic only."));

        if (_unattributedElements > 0)
        {
            _report.Add(
                $"PAINT APPORTIONED TO ONE ROOM: {_unattributedElements} element(s) are painted on " +
                $"more than one room's side. Each now carries only the paint of the room named in " +
                $"'{_settings.RoomNumberParameter}', so a takeoff grouped by room no longer bills a " +
                $"room for its neighbour's paint. " +
                $"{Measure.ToSquareMetres(_unattributedPaint):0.00} m² belongs to the OTHER side(s) " +
                "and is therefore not visible in an element takeoff - it is not lost, it is on those " +
                "rooms' own parameters, which remain the authority. Modelling finishes as separate " +
                "room-side layers - the office standard - removes the split entirely, because those " +
                "elements face one room each.");
        }

        if (_exteriorRooms > 0)
        {
            _report.Add(
                $"EXTERIOR ROOMS SKIPPED: {_exteriorRooms} room(s) named '{_settings.ExteriorRoomPrefix}...' " +
                "were not measured. They are placeholder rooms enclosing a terrace, balcony or " +
                "entrance so it can be scheduled for area - their 'walls' are the OUTSIDE faces " +
                "of the building, and measuring them put exterior surfaces into the interior " +
                "paint takeoff. It also let an exterior room out-claim a small interior one and " +
                "take its wall's 'Rum' value, which is how interior paint area came to be " +
                $"hosted by '{_settings.ExteriorRoomPrefix}'. " +
                (elementsCleared > 0
                    ? $"{elementsCleared} element(s) carrying values from an earlier run were reset to zero."
                    : "No stale values from earlier runs were found."));
        }

        if (elementsWritten > 0)
        {
            _report.Add($"ELEMENT WRITE: finish-area totals written onto {elementsWritten} wall/floor/" +
                        "ceiling element(s) - visible in element schedules once the parameters are " +
                        "bound to those categories.");
        }

        if (elementsTagged > 0)
        {
            _report.Add(
                $"ROOM IDENTITY: '{_settings.ApartmentParameter}', '{_settings.RoomNumberParameter}' and " +
                $"'{_settings.RoomNameParameter}' written onto {elementsTagged} wall/floor/ceiling/roof " +
                "element(s), from the room whose finish they carry. Add them as fields to any " +
                "material takeoff and the rows can be grouped and sorted per apartment and room.");
        }

        if (_identityMissing.Count > 0)
        {
            _report.Add(
                $"MISSING IDENTITY PARAMS: {string.Join(", ", _identityMissing.Order())} - not bound as " +
                "TEXT instance parameters on Walls, Floors, Ceilings and Roofs, or bound read-only. " +
                "Run 'Set Up Finish Schedules', which binds them. Until then the areas are written " +
                "but the schedule cannot say which room they belong to.");
        }

        if (_sharedElements.Count > 0)
        {
            _report.Add(
                $"QC - ELEMENTS SERVING MORE THAN ONE ROOM: {_sharedElements.Count} element(s) carry " +
                "finish for two or more rooms - typically base walls between rooms. Each holds ONE " +
                $"'{_settings.RoomNumberParameter}', the room that contributed the most area, and its " +
                $"'{_settings.WallParameter}' is the SUM of every room's side. A takeoff grouped by " +
                "room therefore credits the whole element to one of them. The room parameters are " +
                "unaffected and remain the authority for per-room quantities. Modelling finishes as " +
                "separate room-side layers - the office standard - removes the ambiguity, because " +
                "those elements face one room each. Element Ids: " +
                string.Join(", ", _sharedElements.Take(20)));
        }

        if (_fallbackRoomCount > 0)
        {
            var breakdown = string.Join(", ", FinishSettings.CeilingSourceOrder
                .Where(_fallbackRooms.ContainsKey)
                .Select(k => $"{_fallbackRooms[k]} took area from the {k}"));

            _report.Add(
                $"CEILING FALLBACK: {_fallbackRoomCount} room(s) were bounded from above by nothing, " +
                $"so their ceiling finish came from the priority chain (ceiling > slab above > roof) " +
                $"instead of reporting zero - {breakdown}. This model contains " +
                $"{_ceilingFallback?.CountIn(FinishSettings.SourceCeiling) ?? 0} ceiling element(s), " +
                $"{_ceilingFallback?.CountIn(FinishSettings.SourceSlabAbove) ?? 0} floor(s) and " +
                $"{_ceilingFallback?.CountIn(FinishSettings.SourceRoof) ?? 0} roof(s).");

            if (_fallbackRooms.Keys.Any(k => k != FinishSettings.SourceCeiling))
            {
                _report.Add(
                    $"SCHEDULE FIX - CEILINGS: that fallback area is written onto the FLOOR or ROOF " +
                    $"element it was measured from, because no ceiling element exists to carry it. A " +
                    $"Ceiling Material Takeoff therefore still has no rows for it - the schedule is " +
                    $"empty because the CATEGORY is empty, not because the numbers are missing. " +
                    $"Schedule it as a Multi-Category Material Takeoff over Ceilings + Floors + Roofs " +
                    $"filtered '{_settings.CeilingParameter} > 0', with '{_settings.CeilingParameter}' " +
                    $"bound to all three categories. The per-room total is on the Room either way, " +
                    $"with its provenance in '{_settings.CeilingSourceParameter}'.");
            }
        }

        _report.Add(
            $"SCHEDULE FIX: to make a Wall Material Takeoff match this engine's painted CSV, schedule " +
            $"the NEW '{_settings.PaintParameter}' field (NOT '{_settings.WallParameter}') and filter " +
            "'Material: As Paint = Yes'. Summed per material it equals the CSV's Is Painted = Yes rows. " +
            $"'{_settings.WallParameter}' still holds the whole finish face (paint + substrate), so " +
            "summing IT per material over-reports paint by the unpainted layer area.");

        if (_lockedElements.Count > 0)
        {
            _report.Add(
                $"WORKSHARING: {_lockedElements.Count} element(s) are checked out by other users " +
                "and were skipped. Their finish areas are unchanged from the last pass - not zero, " +
                "just stale. Re-run after those users synchronise. Skipping them is deliberate: " +
                "writing to an element someone else owns throws, and the throw would roll back " +
                "every correct value in this pass. Element Ids: " +
                string.Join(", ", _lockedElements.Take(20)));
        }

        if (_multipaintWalls.Count > 0)
        {
            _report.Add($"QC - MULTI-PAINT WALLS: {_multipaintWalls.Count} wall(s) carry more than one " +
                        $"paint colour on their room face (split-face). '{_settings.PaintParameter}' stores " +
                        "one value per wall, so a takeoff would repeat it on each paint row and " +
                        "DOUBLE-COUNT these. Handle by hand or split the wall. Element Ids: " +
                        string.Join(", ", _multipaintWalls));
        }

        return new FinishResult
        {
            Report = _report,
            CsvRows = _csvRows,
            Processed = processed,
            SkippedUnplaced = skippedUnplaced,
            ElementsWritten = elementsWritten,
            ElementsTagged = elementsTagged,
            SharedElements = _sharedElements.Count,
        };
    }

    // ------------------------------------------------------------------ pre-flight

    /// <summary>
    /// Rooms only stop at bounding ceilings when volume computation is on. Without it the
    /// room runs to its upper limit and the ceiling area is wrong.
    /// </summary>
    private void PreflightVolumes()
    {
        try
        {
            var settings = AreaVolumeSettings.GetAreaVolumeSettings(_doc);
            if (settings.ComputeVolumes) return;

            settings.ComputeVolumes = true;
            _doc.Regenerate();

            _report.Add("AUTO-FIX: 'Areas and Volumes' computation was OFF and has been enabled, so " +
                        "rooms now stop at bounding ceilings (floor finish to ceiling finish). If a room " +
                        "still reports 'no top boundary', its ceiling is not Room Bounding or the room's " +
                        "upper limit stops below it.");
        }
        catch (Exception ex)
        {
            _report.Add($"WARNING: could not verify/enable volume computation: {ex.Message}");
        }
    }

    private List<Element> Collect(BuiltInCategory category) =>
        [.. new FilteredElementCollector(_doc)
            .OfCategory(category)
            .WhereElementIsNotElementType()];

    private void CollectDeductibles()
    {
        if (_settings.UseCeilingFallback)
            _ceilingFallback = new CeilingFallbackResolver(_doc, _geometry);

        if (_settings.SubtractOpenings)
            _allOpenings = [.. Collect(BuiltInCategory.OST_Doors), .. Collect(BuiltInCategory.OST_Windows)];

        _revealOpenings = [];
        if (_settings.CaptureDoorReveals) _revealOpenings.AddRange(Collect(BuiltInCategory.OST_Doors));
        if (_settings.CaptureWindowReveals) _revealOpenings.AddRange(Collect(BuiltInCategory.OST_Windows));

        if (_settings.SubtractCasework)
        {
            foreach (var instance in Collect(BuiltInCategory.OST_Casework))
            {
                var keep = false;

                try { keep = (instance as FamilyInstance)?.Host is Wall; }
                catch { /* not hosted */ }

                if (!keep)
                {
                    try
                    {
                        keep = InstanceVoidCutUtils.GetElementsBeingCut(instance)
                            .Any(id => _doc.GetElement(id) is Wall);
                    }
                    catch
                    {
                        // Not a void-cutting instance.
                    }
                }

                if (keep) _allCasework.Add(instance);
            }
        }

        // A mezzanine is a slab INSIDE a room, not between rooms. Correct setup: Room
        // Bounding UNCHECKED, so the room volume stays whole. But a non-bounding slab never
        // appears in boundary faces, so its finishes would be invisible.
        _interiorSlabs = [.. Collect(BuiltInCategory.OST_Floors).Where(IsNonRoomBounding)];

        // Non-room-bounding interior WALLS (hanging / partial-height / freestanding
        // partitions inside a room), invisible to the boundary sweep for the same reason.
        _interiorWalls = [.. Collect(BuiltInCategory.OST_Walls).Where(IsNonRoomBounding)];
    }

    private static bool IsNonRoomBounding(Element element)
    {
        try
        {
            var parameter = element.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
            return parameter is not null && parameter.AsInteger() == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Revit rule: a room's volume can NEVER rise above its Upper Limit + Offset - bounding
    /// ceilings only clip WITHIN that allowance. A sloped ceiling climbing above the limit
    /// leaves the room sliced flat, missing the wedge beneath the slope. Fix: raise each
    /// room's Upper Offset just above the highest overlapping ceiling/roof. Raise-only and
    /// minimal; the ceiling still clips the volume, so extra headroom is harmless.
    /// </summary>
    private void AutoAdjustUpperLimits()
    {
        if (!_settings.AutoAdjustLimits) return;

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
            _doc.Regenerate();   // rebuild room volumes BEFORE measuring
            _report.Add($"AUTO-ADJUST: raised the upper limit of {adjusted} room(s) so sloped " +
                        "ceilings/roofs now bound the full room volume (raise-only, highest " +
                        "overlapping ceiling + margin).");
        }
    }

    // ------------------------------------------------------- fallback deductions

    /// <summary>
    /// Deduction size in internal square feet. Priority, instance then type:
    /// 'Void Area' → 'Void Width' × 'Void Height' → built-in Width × Height → 'Width' × 'Height'.
    /// </summary>
    private double OpeningArea(Element instance)
    {
        var holders = new List<Element> { instance };
        try
        {
            var type = _doc.GetElement(instance.GetTypeId());
            if (type is not null) holders.Add(type);
        }
        catch
        {
            // No type; instance alone.
        }

        foreach (var holder in holders)
        {
            var area = holder.LookupParameter("Void Area");
            if (area is { HasValue: true, StorageType: StorageType.Double } && area.AsDouble() > 0)
                return area.AsDouble();
        }

        foreach (var holder in holders)
        {
            var width = holder.LookupParameter("Void Width");
            var height = holder.LookupParameter("Void Height");
            if (width is { HasValue: true, StorageType: StorageType.Double } &&
                height is { HasValue: true, StorageType: StorageType.Double })
                return width.AsDouble() * height.AsDouble();
        }

        foreach (var holder in holders)
        {
            var width = holder.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM);
            var height = holder.get_Parameter(BuiltInParameter.FAMILY_HEIGHT_PARAM);
            if (width is { HasValue: true } && height is { HasValue: true })
                return width.AsDouble() * height.AsDouble();

            var w = holder.LookupParameter("Width");
            var h = holder.LookupParameter("Height");
            if (w is { HasValue: true, StorageType: StorageType.Double } &&
                h is { HasValue: true, StorageType: StorageType.Double })
                return w.AsDouble() * h.AsDouble();
        }

        return 0.0;
    }

    private Phase? RoomPhase(Room room)
    {
        try
        {
            var parameter = room.get_Parameter(BuiltInParameter.ROOM_PHASE);
            return parameter is null ? null : _doc.GetElement(parameter.AsElementId()) as Phase;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Openings in FALLBACK-measured walls only - geometric walls are already net.</summary>
    private (double Total, int Count) DoorWindowDeduction(Room room, Phase? phase, HashSet<long> exactWalls)
    {
        var total = 0.0;
        var count = 0;

        foreach (var instance in _allOpenings.OfType<FamilyInstance>())
        {
            Room? from, to;
            try
            {
                if (instance.Host is Wall host && exactWalls.Contains(host.Id.Value)) continue;

                from = phase is not null ? instance.get_FromRoom(phase) : instance.FromRoom;
                to = phase is not null ? instance.get_ToRoom(phase) : instance.ToRoom;
            }
            catch
            {
                continue;
            }

            if (from?.Id != room.Id && to?.Id != room.Id) continue;

            var area = OpeningArea(instance);
            if (area <= 0) continue;

            total += area;
            count++;
        }

        return (total, count);
    }

    /// <summary>Casework voids in FALLBACK-measured walls only.</summary>
    private (double Total, int Count) CaseworkDeduction(Room room, Phase? phase, HashSet<long> exactWalls)
    {
        var total = 0.0;
        var count = 0;

        foreach (var instance in _allCasework.OfType<FamilyInstance>())
        {
            Room? owner;
            try { owner = phase is not null ? instance.get_Room(phase) : instance.Room; }
            catch { continue; }

            if (owner is null || owner.Id != room.Id) continue;

            var alreadyCut = false;
            try
            {
                if (instance.Host is Wall host && exactWalls.Contains(host.Id.Value)) alreadyCut = true;

                if (!alreadyCut)
                {
                    alreadyCut = InstanceVoidCutUtils.GetElementsBeingCut(instance)
                        .Any(id => exactWalls.Contains(id.Value));
                }
            }
            catch
            {
                // Not a cutting instance.
            }

            if (alreadyCut) continue;   // void already carved out of the measured face

            var area = OpeningArea(instance);
            if (area <= 0) continue;

            total += area;
            count++;
        }

        return (total, count);
    }

    // -------------------------------------------------------------- the room pass

    private bool MeasureRoom(Room room, SpatialElementGeometryCalculator calculator)
    {
        var roomMaterials = new Dictionary<(string Surface, MaterialKey Key), double>();

        void AddMaterials(string surface, MaterialLedger ledger)
        {
            foreach (var (key, area) in ledger.Areas)
            {
                var composite = (surface, key);
                roomMaterials[composite] = roomMaterials.GetValueOrDefault(composite) + area;
            }
        }

        var wallExact = 0.0;
        var grossWall = 0.0;
        var virtualSide = 0.0;
        var exactWalls = new HashSet<long>();
        int exactFaces = 0, fallbackFaces = 0, floorFallback = 0, ceilingFallback = 0;
        var netFloorSlab = 0.0;
        var roomFootprint = 0.0;
        var openBelow = 0.0;

        // Kept for the fallback chain: these span the whole footprint with holes already
        // correctly wound, which is exactly the prism the overhead search needs.
        var roomBottomFaces = new List<Face>();

        var ceilingSource = FinishSettings.CeilingSourceOrder.ToDictionary(k => k, _ => 0.0);

        try
        {
            var results = calculator.CalculateSpatialElementGeometry(room);
            var solid = results.GetGeometry();

            foreach (Face face in solid.Faces)
            {
                foreach (var subface in results.GetBoundaryFaceInfo(face))
                {
                    var subfaceFace = subface.GetSubface();
                    var area = subfaceFace.Area;
                    var element = BoundaryElement(subface);

                    switch (subface.SubfaceType)
                    {
                        case SubfaceType.Bottom:
                        {
                            // A bottom subface exists across the WHOLE room footprint,
                            // including area open to below. Only footprint backed by a real
                            // floor/foundation slab counts as net (walkable) floor area.
                            roomFootprint += area;
                            roomBottomFaces.Add(subfaceFace);

                            var hasSlab = element?.Category is { } category &&
                                          _slabCategories.Any(c => c == category.Id);

                            var measured = _settings.UseGeometric && hasSlab && element is not null
                                ? _geometry.ExactSubfaceArea(subfaceFace, _geometry.TopFaces(element), element)
                                : null;

                            if (measured is { } floor)
                            {
                                // Already clipped to the slab's real top face, so any
                                // open-to-below part is excluded here too.
                                netFloorSlab += floor.Total;
                                AddMaterials(FinishSettings.SurfaceFloor, floor.Materials);
                                Accumulate(_elemFloorArea, element!.Id, floor.Total);

                                // The finish-material subset, read off the same ledger, so the
                                // element's two numbers can never disagree with the CSV rows
                                // they were both derived from.
                                AccumulatePaint(_elemFloorPaint, element.Id, room, floor.Materials.PaintedTotal);
                                Claim(element.Id, room, floor.Total);
                            }
                            else if (hasSlab)
                            {
                                // A real slab backs this subface but the boolean clip failed
                                // (curved/edited slab). The slab exists, so its planar
                                // footprint still counts.
                                //
                                // Nothing is added to _elemFloorPaint here, deliberately:
                                // MaterialKey.Fallback is unpainted by definition, because
                                // arithmetic fallback area was never measured off a face and
                                // so has no material to identify. Counting it as finish would
                                // invent a cost basis out of a measurement failure.
                                netFloorSlab += area;
                                var ledger = new MaterialLedger();
                                ledger.Add(MaterialKey.Fallback, area);
                                AddMaterials(FinishSettings.SurfaceFloor, ledger);
                                Accumulate(_elemFloorArea, element!.Id, area);
                                Claim(element.Id, room, area);
                                floorFallback++;
                            }
                            else
                            {
                                // OPEN TO BELOW: no slab under this footprint - not walkable,
                                // not floor finish.
                                openBelow += area;
                            }

                            continue;
                        }

                        case SubfaceType.Top:
                        {
                            var measured = _settings.UseGeometric && element is not null
                                ? _geometry.ExactSubfaceArea(subfaceFace, _geometry.BottomFaces(element), element)
                                : null;

                            double amount, painted = 0.0;
                            if (measured is { } ceiling)
                            {
                                amount = ceiling.Total;
                                painted = ceiling.Materials.PaintedTotal;
                                AddMaterials(FinishSettings.SurfaceCeiling, ceiling.Materials);
                            }
                            else
                            {
                                amount = area;
                                var ledger = new MaterialLedger();
                                ledger.Add(MaterialKey.Fallback, area);
                                AddMaterials(FinishSettings.SurfaceCeiling, ledger);
                                ceilingFallback++;
                            }

                            ceilingSource[CeilingSourceKey(element)] += amount;
                            if (element is not null)
                            {
                                Accumulate(_elemCeilingArea, element.Id, amount);
                                AccumulatePaint(_elemCeilingPaint, element.Id, room, painted);
                                Claim(element.Id, room, amount);
                            }
                            continue;
                        }

                        case SubfaceType.Side:
                            break;

                        default:
                            continue;
                    }

                    if (element is null ||
                        (element.Category is not null && element.Category.Id == _sepLineCategory))
                    {
                        virtualSide += area;
                        continue;
                    }

                    // Steeply sloped ceilings/roofs can classify as Side subfaces - route
                    // them to the CEILING bucket so they never leak into the wall total.
                    var categoryId = element.Category?.Id;
                    if (categoryId == _ceilingCategory || categoryId == _roofCategory)
                    {
                        var measured = _settings.UseGeometric
                            ? _geometry.ExactSubfaceArea(subfaceFace, _geometry.BottomFaces(element), element)
                            : null;

                        double amount, painted = 0.0;
                        if (measured is { } sloped)
                        {
                            amount = sloped.Total;
                            painted = sloped.Materials.PaintedTotal;
                            AddMaterials(FinishSettings.SurfaceCeiling, sloped.Materials);
                        }
                        else
                        {
                            amount = area;
                            var ledger = new MaterialLedger();
                            ledger.Add(MaterialKey.Fallback, area);
                            AddMaterials(FinishSettings.SurfaceCeiling, ledger);
                            ceilingFallback++;
                        }

                        ceilingSource[CeilingSourceKey(element)] += amount;
                        Accumulate(_elemCeilingArea, element.Id, amount);
                        AccumulatePaint(_elemCeilingPaint, element.Id, room, painted);
                        Claim(element.Id, room, amount);
                        continue;
                    }

                    var wallResult = _settings.UseGeometric && element is Wall
                        ? _geometry.ExactSubfaceArea(subfaceFace, _geometry.CachedFaces(element), element)
                        : null;

                    if (wallResult is { } wall)
                    {
                        wallExact += wall.Total;
                        AddMaterials(FinishSettings.SurfaceWalls, wall.Materials);
                        Accumulate(_elemWallArea, element.Id, wall.Total);
                        AccumulatePaint(_elemWallPaint, element.Id, room, wall.Materials.PaintedTotal);
                        Claim(element.Id, room, wall.Total);

                        if (wall.Materials.DistinctPaintedMaterials > 1)
                            _multipaintWalls.Add(element.Id.Value);

                        exactWalls.Add(element.Id.Value);
                        exactFaces++;
                    }
                    else
                    {
                        // Fallback = arithmetic planar area, no paint resolution, so it feeds
                        // the total finish face only - never the painted (cost) parameter.
                        grossWall += area;
                        Accumulate(_elemWallArea, element.Id, area);
                        Claim(element.Id, room, area);
                        fallbackFaces++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _report.Add($"FAILED {RoomLabel(room)}: {ex.Message}");
            return false;
        }

        // ROOM BOUNDARY PRIORITY, the part Revit cannot do for us.
        //
        // Everything above came from the room's own solid, so it only ever reports a ceiling
        // that actually clipped the room volume. When nothing did - no ceiling modelled, and
        // no slab or roof bounding the room either - that is a measurement of zero, not a
        // ceiling of zero, and the fallback chain goes and looks overhead itself.
        //
        // Deliberately gated on the BOUNDARY total rather than the ledger: mezzanine
        // undersides land in the same ceiling bucket further down, and a room can perfectly
        // well have a mezzanine soffit inside it AND no ceiling over it. Testing the ledger
        // would let the first suppress the search for the second.
        var boundaryCeiling = ceilingSource.Values.Sum();
        CeilingFallbackResult? fallback = null;

        // Elements whose soffit the fallback has claimed for THIS room. The interior-slab
        // pass below must not claim them again - see the note on its parameter.
        var ceilingClaimed = new HashSet<long>();

        if (_ceilingFallback is not null && boundaryCeiling <= FinishSettings.CeilingFallbackMinimum)
        {
            fallback = _ceilingFallback.Resolve(room, roomBottomFaces);

            if (fallback is not null)
            {
                _fallbackRoomCount++;

                foreach (var hit in fallback.Hits)
                {
                    AddMaterials(FinishSettings.SurfaceCeiling, hit.Materials);
                    Accumulate(_elemCeilingArea, hit.Element, hit.Area);

                    // The PT subset. Read off the ORIGINAL element faces, so this is Revit's
                    // own paint state rather than an assumption about which layer is finish.
                    AccumulatePaint(_elemCeilingPaint, hit.Element, room, hit.Materials.PaintedTotal);
                    Claim(hit.Element, room, hit.Area);

                    ceilingClaimed.Add(hit.Element.Value);
                }

                foreach (var tier in fallback.Tiers)
                {
                    ceilingSource[tier.Source] += tier.Total;
                    _fallbackRooms[tier.Source] = _fallbackRooms.GetValueOrDefault(tier.Source) + 1;
                }
            }
        }

        var phase = RoomPhase(room);

        var (openTotal, openCount) = _settings.SubtractOpenings && fallbackFaces > 0
            ? DoorWindowDeduction(room, phase, exactWalls)
            : (0.0, 0);

        var (caseworkTotal, caseworkCount) = _settings.SubtractCasework && fallbackFaces > 0
            ? CaseworkDeduction(room, phase, exactWalls)
            : (0.0, 0);

        var (mezzFloor, mezzCeiling, mezzCount) = MeasureInteriorSlabs(room, AddMaterials, ceilingClaimed);
        var (hangingArea, hangingCount) = MeasureInteriorWalls(room, AddMaterials);
        var (revealPaint, revealCount) = MeasureReveals(room, phase, AddMaterials);

        // Fallback wall area cannot be attributed to a material - record it transparently.
        var fallbackNet = Math.Max(grossWall - openTotal - caseworkTotal, 0.0);
        if (fallbackNet > 0.005)
        {
            var ledger = new MaterialLedger();
            ledger.Add(MaterialKey.Fallback, fallbackNet);
            AddMaterials(FinishSettings.SurfaceWalls, ledger);
        }

        // Parameters are derived FROM the material ledger, so "parameter == sum of its
        // material rows" is true BY CONSTRUCTION - one ledger, no parallel arithmetic to
        // drift out of step.
        double SumSurface(string surface) =>
            roomMaterials.Where(e => e.Key.Surface == surface).Sum(e => e.Value);

        double SumPainted(string surface) =>
            roomMaterials.Where(e => e.Key.Surface == surface && e.Key.Key.Painted).Sum(e => e.Value);

        // Painted door reveals are wall finish too - they add the same amount to both.
        var netWall = SumSurface(FinishSettings.SurfaceWalls) + SumSurface(FinishSettings.SurfaceReveals);
        var paintWall = SumPainted(FinishSettings.SurfaceWalls) + SumPainted(FinishSettings.SurfaceReveals);
        var floorArea = SumSurface(FinishSettings.SurfaceFloor);
        var paintFloor = SumPainted(FinishSettings.SurfaceFloor);
        var ceilingArea = SumSurface(FinishSettings.SurfaceCeiling);
        var paintCeiling = SumPainted(FinishSettings.SurfaceCeiling);

        var ceilingSourceLabel = DescribeCeilingSource(ceilingSource, fallback);

        var missing = new List<string>();
        if (!SetArea(room, _settings.WallParameter, netWall)) missing.Add(_settings.WallParameter);
        if (!SetArea(room, _settings.PaintParameter, paintWall)) missing.Add(_settings.PaintParameter);
        if (!SetArea(room, _settings.FloorParameter, floorArea)) missing.Add(_settings.FloorParameter);
        if (!SetArea(room, _settings.FloorPaintParameter, paintFloor))
            missing.Add(_settings.FloorPaintParameter);
        if (!SetArea(room, _settings.CeilingParameter, ceilingArea)) missing.Add(_settings.CeilingParameter);
        if (!SetArea(room, _settings.CeilingPaintParameter, paintCeiling))
            missing.Add(_settings.CeilingPaintParameter);
        if (!SetArea(room, _settings.NetFloorParameter, netFloorSlab)) missing.Add(_settings.NetFloorParameter);

        if (!SetText(room, _settings.CeilingSourceParameter, ceilingSourceLabel))
            missing.Add(_settings.CeilingSourceParameter);

        _report.Add(BuildRoomLine(room, netWall, wallExact, exactFaces, grossWall, fallbackFaces,
            openCount, openTotal, caseworkCount, caseworkTotal, floorArea, ceilingArea, paintWall,
            netFloorSlab, roomFootprint, openBelow, ceilingSource, mezzCount, mezzFloor, mezzCeiling,
            hangingCount, hangingArea, revealCount, revealPaint, virtualSide, floorFallback,
            ceilingFallback, fallback, missing));

        AppendCsvRows(room, roomMaterials);
        return true;
    }

    // ------------------------------------------------------------ interior elements

    /// <summary>
    /// Mezzanine slabs inside this room's volume. Probe a point just above the slab's top
    /// (or below its bottom): if that air belongs to this room, the slab lives inside it.
    /// Real faces are summed, so voids and stair openings are already excluded.
    /// </summary>
    /// <param name="ceilingClaimed">
    /// Slabs whose underside the ceiling fallback already measured for this room.
    ///
    /// A storey slab with Room Bounding switched OFF lands in BOTH passes: nothing bounds
    /// the room from above so the fallback measures its soffit, and being non-room-bounding
    /// is also exactly the test that makes it look like a mezzanine here. Without this
    /// exclusion the room's ceiling area is counted twice - once room-clipped, once as the
    /// slab's whole underside - and the second is not even clipped to the room, so the
    /// error is larger than double.
    ///
    /// Excluding the slab outright rather than just its underside is deliberate. The
    /// mezzanine assumption is that one slab's top AND bottom both face the same room; for
    /// a storey slab the top belongs to the room above, so counting it here was wrong
    /// independently of the fallback.
    /// </param>
    private (double Floor, double Ceiling, int Count) MeasureInteriorSlabs(
        Room room, Action<string, MaterialLedger> addMaterials, HashSet<long> ceilingClaimed)
    {
        double floor = 0.0, ceiling = 0.0;
        var count = 0;

        foreach (var slab in _interiorSlabs)
        {
            try
            {
                if (ceilingClaimed.Contains(slab.Id.Value)) continue;

                var box = slab.get_BoundingBox(null);
                if (box is null) continue;

                var cx = (box.Min.X + box.Max.X) / 2.0;
                var cy = (box.Min.Y + box.Max.Y) / 2.0;

                // WHICH SIDE of the slab this room is on decides what it may claim, and the
                // two answers are independent.
                //
                // A true mezzanine sits inside one room: air above it AND below it both
                // belong to that room, so it claims both faces. A STOREY slab with Room
                // Bounding switched off reaches this same code - non-room-bounding is the
                // only test there is - but its top serves the room above and its underside
                // the room below. Collapsing both probes into one "is it near this room?"
                // gave the room below a floor finish it cannot walk on, and the room above
                // a ceiling it cannot see.
                var above = TryPointInRoom(room, new XYZ(cx, cy, box.Max.Z + 0.3));
                var below = TryPointInRoom(room, new XYZ(cx, cy, box.Min.Z - 0.3));

                if (!above && !below) continue;

                var topLedger = new MaterialLedger();
                var topArea = 0.0;
                foreach (var face in _geometry.TopFaces(slab))
                {
                    topArea += face.Area;
                    topLedger.Add(_geometry.FaceMaterialKey(slab, face), face.Area);
                }

                var bottomLedger = new MaterialLedger();
                var bottomArea = 0.0;
                foreach (var face in _geometry.BottomFaces(slab))
                {
                    bottomArea += face.Area;
                    bottomLedger.Add(_geometry.FaceMaterialKey(slab, face), face.Area);
                }

                if (topArea <= 0 && bottomArea <= 0) continue;

                if (above)
                {
                    addMaterials(FinishSettings.SurfaceFloor, topLedger);
                    floor += topArea;
                    Accumulate(_elemFloorArea, slab.Id, topArea);

                    // Same reason as the ceiling side below: a mezzanine top measured through
                    // this path would otherwise carry a floor area but no finish area, while
                    // the takeoff's own 'Material: As Paint' column says Yes on that row.
                    AccumulatePaint(_elemFloorPaint, slab.Id, room, topLedger.PaintedTotal);
                    Claim(slab.Id, room, topArea);
                }

                if (below)
                {
                    addMaterials(FinishSettings.SurfaceCeiling, bottomLedger);
                    ceiling += bottomArea;
                    Accumulate(_elemCeilingArea, slab.Id, bottomArea);
                    Claim(slab.Id, room, bottomArea);

                    // Without this a slab measured through the interior-slab path reports a
                    // ceiling area but no PT area, while the takeoff's own 'Material: As
                    // Paint' column says Yes on the same row - two answers to one question.
                    AccumulatePaint(_elemCeilingPaint, slab.Id, room, bottomLedger.PaintedTotal);
                }

                count++;
            }
            catch
            {
                // One slab failing must not stop the room.
            }
        }

        return (floor, ceiling, count);
    }

    /// <summary>
    /// Hanging / freestanding partitions inside this room. A bulkhead is usually finished
    /// on the visible side(s) only, its back concealed behind casework, so: if any side
    /// face is painted, count ONLY painted faces; otherwise count the single face whose
    /// normal points most toward the room centre. Never both - that doubles the quantity.
    /// </summary>
    private (double Area, int Count) MeasureInteriorWalls(
        Room room, Action<string, MaterialLedger> addMaterials)
    {
        var total = 0.0;
        var count = 0;

        foreach (var wall in _interiorWalls)
        {
            try
            {
                var box = wall.get_BoundingBox(null);
                if (box is null) continue;

                var centre = new XYZ(
                    (box.Min.X + box.Max.X) / 2.0,
                    (box.Min.Y + box.Max.Y) / 2.0,
                    (box.Min.Z + box.Max.Z) / 2.0);

                if (!TryPointInRoom(room, centre)) continue;

                var sideFaces = new List<(Face Face, XYZ Normal)>();
                foreach (var face in _geometry.ElementFaces(wall))
                {
                    var (origin, normal) = FinishGeometry.PlanarData(face);
                    if (origin is null || normal is null || Math.Abs(normal.Z) > 0.5) continue;
                    sideFaces.Add((face, normal));
                }

                // WHICH FACES ACTUALLY FRONT THIS ROOM.
                //
                // THE BUG THIS FIXES - paint registering to the room on the far side.
                //   The painted set used to be taken from EVERY side face of the wall, and
                //   the orientation test below only ran in the "nothing is painted" branch -
                //   which is exactly the case where it does not matter. So for a
                //   non-room-bounding wall that genuinely divides two spaces, the far face's
                //   paint was credited to the near room. One wall, both faces, one room.
                //
                // WHY THE ORIGINAL WAS NOT SIMPLY WRONG
                //   This method exists for hanging and freestanding partitions - a bulkhead
                //   standing INSIDE a room, finished on the visible sides and concealed
                //   behind casework at the back. For those, both faces really do belong to
                //   this room, and counting only the painted ones is the correct rule.
                //
                //   Probing per face keeps that intact: both sides of a freestanding
                //   bulkhead answer "yes, this room", so both survive. Only a wall that
                //   divides two spaces loses its far face - which is the whole fix.
                //
                // ASKED OF THE ROOM, NOT INFERRED FROM A CENTRE POINT. The fallback below
                // compares each normal against the room's location point, which is a
                // reasonable guess and no better than that in an L-shaped room where the
                // location point can sit behind the very wall being tested. IsPointInRoom a
                // short step off the face answers the actual question.
                var facing = new List<(Face Face, XYZ Normal)>();
                foreach (var (face, normal) in sideFaces)
                {
                    if (FaceFrontsRoom(room, face, normal)) facing.Add((face, normal));
                }

                // Undecidable for every face - a room Revit will not answer containment for.
                // Fall back to the full set rather than silently measuring nothing.
                if (facing.Count == 0) facing = sideFaces;

                var painted = new List<(Face Face, XYZ Normal)>();
                foreach (var (face, normal) in facing)
                {
                    try
                    {
                        if (_doc.IsPainted(wall.Id, face)) painted.Add((face, normal));
                    }
                    catch
                    {
                        // Unresolvable face reference; treat as unpainted.
                    }
                }

                List<Face> use;
                if (painted.Count > 0)
                {
                    use = [.. painted.Select(p => p.Face)];
                }
                else if (facing.Count > 0)
                {
                    XYZ? roomPoint = null;
                    try { roomPoint = (room.Location as LocationPoint)?.Point; }
                    catch { /* no location */ }

                    if (roomPoint is not null)
                    {
                        Face? best = null;
                        var bestDot = -2.0;

                        foreach (var (face, _) in facing)
                        {
                            var (origin, normal) = FinishGeometry.PlanarData(face);
                            if (origin is null || normal is null) continue;

                            var direction = roomPoint - origin;
                            if (direction.GetLength() < 1e-9) continue;

                            var dot = normal.DotProduct(direction.Normalize());
                            if (dot > bestDot) (bestDot, best) = (dot, face);
                        }

                        use = best is not null ? [best] : [];
                    }
                    else
                    {
                        use = [facing[0].Face];
                    }
                }
                else
                {
                    use = [];
                }

                var ledger = new MaterialLedger();
                var area = 0.0;

                foreach (var face in use)
                {
                    area += face.Area;
                    var key = _geometry.FaceMaterialKey(wall, face);
                    ledger.Add(key, face.Area);
                    Accumulate(_elemWallArea, wall.Id, face.Area);
                    Claim(wall.Id, room, face.Area);
                    if (key.Painted) AccumulatePaint(_elemWallPaint, wall.Id, room, face.Area);
                }

                if (area <= 0) continue;

                addMaterials(FinishSettings.SurfaceWalls, ledger);
                total += area;
                count++;
            }
            catch
            {
                // One wall failing must not stop the room.
            }
        }

        return (total, count);
    }

    /// <summary>
    /// Each opening's reveal paint is attributed to ONE room - its FromRoom, else ToRoom -
    /// so the model total matches Revit's native takeoff with no double counting.
    ///
    /// Covers doors AND windows. FromRoom/ToRoom is populated for both, and for an exterior
    /// window one side is simply null, which the `from ?? to` fallback already handles.
    /// </summary>
    private (double Paint, int Count) MeasureReveals(
        Room room, Phase? phase, Action<string, MaterialLedger> addMaterials)
    {
        if (_revealOpenings.Count == 0) return (0.0, 0);

        var paint = 0.0;
        var count = 0;

        foreach (var opening in _revealOpenings.OfType<FamilyInstance>())
        {
            try
            {
                if (opening.Host is not Wall host) continue;

                var from = phase is not null ? opening.get_FromRoom(phase) : opening.FromRoom;
                var to = phase is not null ? opening.get_ToRoom(phase) : opening.ToRoom;
                var owner = from ?? to;

                if (owner is null || owner.Id != room.Id) continue;

                var ledger = _reveals.Measure(host, opening);
                if (ledger.Areas.Count == 0) continue;

                addMaterials(FinishSettings.SurfaceReveals, ledger);

                foreach (var (_, area) in ledger.Areas)
                {
                    AccumulatePaint(_elemWallPaint, host.Id, room, area);

                    // The reveal is this room's finish on that wall, so it counts toward
                    // which room owns the wall — the deciding case being an internal door
                    // whose host wall is otherwise split evenly between two rooms.
                    Claim(host.Id, room, area);
                    paint += area;
                }

                count++;
            }
            catch
            {
                // One door failing must not stop the room.
            }
        }

        return (paint, count);
    }

    /// <summary>
    /// Which room owns an element, from its per-room finish claims.
    ///
    /// AN INTERIOR ROOM ALWAYS BEATS AN EXTERIOR ONE, whatever the areas say. Ordering by
    /// area alone let a large terrace out-claim the small room on the other side of the same
    /// wall, and the wall then carried 'Rum = Udvendig' - an interior wall's paint area filed
    /// under an exterior space. This is the half of that fix which survives someone setting
    /// <see cref="FinishSettings.SkipExteriorRooms"/> to false.
    ///
    /// Then by area; then by the lower room id, so a wall dead-centre between two rooms lands
    /// on the same one every run instead of flipping with dictionary order.
    /// </summary>
    /// <summary>
    /// Does this face front <paramref name="room"/>?
    ///
    /// A solid's face normals point outward, so a short step along the normal leaves the wall
    /// and enters whatever space the face looks into. Asking the room whether that point is
    /// inside it is the definitive answer - not a heuristic, and it does not care about the
    /// wall's Orientation flag, which describes how the wall was drawn rather than what is
    /// on either side of it.
    ///
    /// The step is measured from the face's own centroid rather than its origin: a face
    /// origin can sit on a corner of the wall, where a probe lands in the return of an
    /// abutting partition and the answer is about the wrong room entirely.
    /// </summary>
    private bool FaceFrontsRoom(Room room, Face face, XYZ normal)
    {
        try
        {
            var centroid = FinishGeometry.FaceCentroid(face);
            if (centroid is null) return false;

            var direction = normal.Normalize();
            if (direction.GetLength() < 1e-9) return false;

            return TryPointInRoom(room, centroid + direction * FinishSettings.FaceProbe);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Element to owning room, for every element any room claimed.</summary>
    private Dictionary<long, long> ResolveOwners()
    {
        var owners = new Dictionary<long, long>();

        foreach (var (elementId, byRoom) in _elemRoomClaims)
        {
            if (byRoom.Count == 0) continue;
            owners[elementId] = OwnerOf(byRoom);
        }

        return owners;
    }

    private long OwnerOf(Dictionary<long, double> byRoom) => byRoom
        .OrderBy(e => IsExteriorRoomId(e.Key) ? 1 : 0)
        .ThenByDescending(e => e.Value)
        .ThenBy(e => e.Key)
        .First().Key;

    /// <summary>
    /// The paint figure to write onto an element: the OWNING room's share, not the sum.
    ///
    /// THE BUG THIS FIXES
    ///   These element parameters exist to be grouped by room - that is their stated purpose,
    ///   and it is what turns a material takeoff into a Roombook. But the value was the sum
    ///   across every room the element serves, while the room label is a single winner. So a
    ///   partition between Stue and Kokken put BOTH sides' paint into one number and filed all
    ///   of it under whichever room was larger. Grouped by room, one room was billed for paint
    ///   that belongs to its neighbour.
    ///
    /// WHAT IS GIVEN UP, STATED PLAINLY
    ///   The other room's share is no longer visible in an ELEMENT takeoff. It is not lost -
    ///   the room parameters carry every room's own total and remain the authority - but a
    ///   schedule of elements no longer sums to the building's painted area. That is the right
    ///   trade for a parameter whose whole purpose is per-room grouping, and the residual is
    ///   measured and reported rather than left to be discovered.
    /// </summary>
    private double PaintShare(long elementId, double total, IReadOnlyDictionary<long, long> owners)
    {
        if (!_settings.RoomConsistentPaint) return total;

        if (!_elemRoomPaint.TryGetValue(elementId, out var byRoom)) return total;

        // Attributed to no room: nothing to apportion against, so the total stands.
        if (!owners.TryGetValue(elementId, out var owner)) return total;

        var share = byRoom.GetValueOrDefault(owner);
        var residual = total - share;

        if (residual > 1e-6)
        {
            _unattributedPaint += residual;
            _unattributedElements++;
        }

        return share;
    }

    // ------------------------------------------------------------------ exterior

    /// <summary>
    /// Is this one of the outdoor placeholder rooms?
    ///
    /// Prefix, not substring, and the same test the skirting generator and the door resolver
    /// apply: 'Udvendig', 'Udvendig 1' and 'Udvendig 3' all match, while a room called
    /// 'Trappe udvendig belysning' does not.
    /// </summary>
    private bool IsExteriorRoom(Room room) =>
        _settings.SkipExteriorRooms && IsExteriorRoomId(room.Id.Value);

    /// <summary>
    /// <see cref="IsExteriorRoom"/> by id, for the identity tie-break.
    ///
    /// Ignores <see cref="FinishSettings.SkipExteriorRooms"/> deliberately: that flag decides
    /// whether an exterior room is MEASURED, and this decides whether it may own an element.
    /// A model configured to measure terraces still must not label an interior wall with one.
    /// </summary>
    private bool IsExteriorRoomId(long roomIdValue)
    {
        if (_exteriorRoomCache.TryGetValue(roomIdValue, out var cached)) return cached;

        var prefix = _settings.ExteriorRoomPrefix;
        var result = false;

        if (!string.IsNullOrWhiteSpace(prefix) &&
            _doc.GetElement(new ElementId(roomIdValue)) is Room room)
        {
            result = RoomText(room, BuiltInParameter.ROOM_NAME)
                .TrimStart()
                .StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        _exteriorRoomCache[roomIdValue] = result;
        return result;
    }

    /// <summary>
    /// Which elements bound a skipped exterior room. Boundary faces only - no booleans, no
    /// materials, no reveals - so an exterior room costs a fraction of a measured one.
    /// </summary>
    private void CollectExteriorElements(Room room, SpatialElementGeometryCalculator calculator)
    {
        try
        {
            var results = calculator.CalculateSpatialElementGeometry(room);

            foreach (Face face in results.GetGeometry().Faces)
            {
                IList<SpatialElementBoundarySubface> subfaces;
                try { subfaces = results.GetBoundaryFaceInfo(face); }
                catch { continue; }

                if (subfaces is null) continue;

                foreach (var subface in subfaces)
                {
                    try
                    {
                        var boundary = subface.SpatialBoundaryElement;

                        if (boundary.LinkInstanceId != ElementId.InvalidElementId) continue;
                        if (boundary.HostElementId == ElementId.InvalidElementId) continue;

                        _exteriorElements.Add(boundary.HostElementId.Value);
                    }
                    catch
                    {
                        // Unreadable subface; the rest of the room still contributes.
                    }
                }
            }
        }
        catch
        {
            // An exterior room whose geometry will not compute simply contributes no
            // clean-up candidates. Nothing else in the pass depends on it.
        }
    }

    /// <summary>
    /// Zeroes finish values left on elements that ONLY an exterior room ever claimed.
    ///
    /// WHY THIS IS NOT OPTIONAL
    ///   <see cref="WriteElementTotals"/> writes only the elements a pass measured. It never
    ///   clears. So the first run after exterior rooms became skipped would stop writing the
    ///   bad numbers and leave every one of them exactly where it was - a fix that changes
    ///   nothing anybody can see, on precisely the walls that prompted it.
    ///
    ///   The condition is narrow on purpose. An exterior wall's INTERIOR face is still
    ///   measured by the room inside, so that element is claimed and is left alone; only an
    ///   element no interior room touched at all is reset. The parameters are engine outputs,
    ///   so a stale one is not user data being discarded - it is a wrong answer being
    ///   withdrawn.
    /// </summary>
    private int ClearExteriorOnlyElements()
    {
        if (_exteriorElements.Count == 0) return 0;

        var claimed = new HashSet<long>(_elemRoomClaims.Keys);

        foreach (var set in new[]
                 {
                     _elemWallArea, _elemWallPaint, _elemFloorArea,
                     _elemFloorPaint, _elemCeilingArea, _elemCeilingPaint,
                 })
        {
            foreach (var key in set.Keys) claimed.Add(key);
        }

        var areaParameters = new[]
        {
            _settings.WallParameter, _settings.PaintParameter,
            _settings.FloorParameter, _settings.FloorPaintParameter,
            _settings.CeilingParameter, _settings.CeilingPaintParameter,
        };

        var identityParameters = new[]
        {
            _settings.ApartmentParameter, _settings.RoomNumberParameter, _settings.RoomNameParameter,
        };

        var cleared = 0;

        foreach (var idValue in _exteriorElements)
        {
            if (claimed.Contains(idValue)) continue;

            var elementId = new ElementId(idValue);

            try
            {
                var element = _doc.GetElement(elementId);
                if (element is null) continue;

                if (!Worksharing.CanWrite(_doc, elementId))
                {
                    _lockedElements.Add(idValue);
                    continue;
                }

                var touched = false;

                foreach (var name in areaParameters)
                {
                    var parameter = ParameterHelper.Find(element, name);

                    // Only where there is something to clear: writing 0 over 0 on every
                    // exterior wall in the building would make a no-op pass look like work.
                    if (parameter is { IsReadOnly: false, StorageType: StorageType.Double } &&
                        Math.Abs(parameter.AsDouble()) > 1e-9)
                    {
                        parameter.Set(0.0);
                        touched = true;
                    }
                }

                foreach (var name in identityParameters)
                {
                    var parameter = ParameterHelper.Find(element, name);

                    if (parameter is { IsReadOnly: false, StorageType: StorageType.String } &&
                        !string.IsNullOrEmpty(parameter.AsString()))
                    {
                        parameter.Set(string.Empty);
                        touched = true;
                    }
                }

                if (touched) cleared++;
            }
            catch
            {
                // Element gone or parameter unwritable; nothing else depends on this one.
            }
        }

        return cleared;
    }

    // ------------------------------------------------------------------- writing

    /// <summary>
    /// Same parameter names, bound by the user to Walls / Floors / Ceilings / Roofs.
    /// Writes wherever the parameter exists and is writable; elements without it are
    /// skipped silently.
    /// </summary>
    private int WriteElementTotals()
    {
        var written = 0;

        // Resolved once, up front, because the paint figures below need to know which room
        // each element is attributed to - and WriteRoomIdentity, which used to be the only
        // thing that knew, runs after this.
        var owners = ResolveOwners();

        var sets = new (Dictionary<long, double> Values, string Parameter, bool IsPaint)[]
        {
            (_elemWallArea, _settings.WallParameter, false),
            (_elemWallPaint, _settings.PaintParameter, true),
            (_elemFloorArea, _settings.FloorParameter, false),
            (_elemFloorPaint, _settings.FloorPaintParameter, true),
            (_elemCeilingArea, _settings.CeilingParameter, false),
            (_elemCeilingPaint, _settings.CeilingPaintParameter, true),
        };

        foreach (var (values, name, isPaint) in sets)
        {
            foreach (var (id, total) in values)
            {
                // Paint is apportioned to the owning room; the finish AREAS stay as the
                // element's own full quantity, which is what they have always meant and what
                // a material takeoff of surfaces needs.
                var value = isPaint ? PaintShare(id, total, owners) : total;

                try
                {
                    var elementId = new ElementId(id);
                    var element = _doc.GetElement(elementId);
                    if (element is null) continue;

                    // OWNERSHIP BEFORE WRITING, on a workshared model.
                    //
                    // Writing to an element another user has checked out throws, and the
                    // throw rolls back the WHOLE transaction - discarding every correct
                    // value already written for every other room. One colleague with a wall
                    // open would silently cost the entire pass. Skipping the element instead
                    // degrades to a partial result with a named list.
                    if (!Worksharing.CanWrite(_doc, elementId))
                    {
                        _lockedElements.Add(id);
                        continue;
                    }

                    var parameter = ParameterHelper.Find(element, name);
                    if (parameter is { IsReadOnly: false, StorageType: StorageType.Double })
                    {
                        parameter.Set(value);
                        written++;
                    }
                }
                catch
                {
                    // Element gone or parameter unwritable; skip.
                }
            }
        }

        return written;
    }

    /// <summary>
    /// Writes Lejlighed / Rum nr / Rum onto every element the pass measured, from the room
    /// that contributed the most finish area to it.
    ///
    /// WHY THIS IS THE THING THAT MAKES A FINISH SCHEDULE USABLE
    ///   A material takeoff lists elements and materials. It has no room column and cannot
    ///   have one, because an element is not in a room — Revit has no such relationship for
    ///   walls, floors or ceilings. The engine, having just measured which room's finish
    ///   each surface is, is the only thing in the model that knows. Writing the answer down
    ///   is what turns a flat list of areas into something that can be grouped per apartment
    ///   and issued, which is what a Roombook schedule is for.
    ///
    /// THE HONEST LIMIT
    ///   One element, one set of values. A base wall between two rooms gets the room that
    ///   contributed more area, and its Wall Finish Area is still the SUM of both sides — so
    ///   a schedule grouped by Rum nr credits the whole wall to one of the two rooms. Every
    ///   such element is counted and listed in the report rather than left to be discovered
    ///   in a quantity dispute. Modelling finishes as separate room-side layers, which is
    ///   the office standard, avoids it entirely: those walls face one room each.
    /// </summary>
    private int WriteRoomIdentity()
    {
        var written = 0;

        foreach (var (elementIdValue, byRoom) in _elemRoomClaims)
        {
            try
            {
                if (byRoom.Count == 0) continue;
                if (byRoom.Count > 1) _sharedElements.Add(elementIdValue);

                var elementId = new ElementId(elementIdValue);
                var element = _doc.GetElement(elementId);
                if (element is null) continue;

                if (!Worksharing.CanWrite(_doc, elementId))
                {
                    _lockedElements.Add(elementIdValue);
                    continue;
                }

                var owner = OwnerOf(byRoom);

                if (_doc.GetElement(new ElementId(owner)) is not Room room) continue;

                // Whether a MISSING parameter here is worth reporting depends on the
                // category. A room can be bounded by a column, a curtain panel or a mass,
                // and those legitimately do not carry these parameters - reporting them
                // would tell a correctly set-up model that its setup is broken.
                var expected = IsFinishCategory(element);

                var wrote = false;
                wrote |= WriteIdentity(element, _settings.ApartmentParameter,
                    RoomText(room, BuiltInParameter.ROOM_DEPARTMENT), expected);
                wrote |= WriteIdentity(element, _settings.RoomNumberParameter,
                    RoomText(room, BuiltInParameter.ROOM_NUMBER), expected);
                wrote |= WriteIdentity(element, _settings.RoomNameParameter,
                    RoomText(room, BuiltInParameter.ROOM_NAME), expected);

                if (wrote) written++;
            }
            catch
            {
                // Element gone or parameter unwritable; skip it rather than the pass.
            }
        }

        return written;
    }

    /// <summary>
    /// One identity write. The parameter name is collected once for the report when it is
    /// absent from an element that should have had it — per name, not per element, because
    /// an unbound parameter fails on every wall in the model and one line says it.
    /// </summary>
    private bool WriteIdentity(Element element, string name, string value, bool expected)
    {
        var parameter = ParameterHelper.Find(element, name);

        if (parameter is not { IsReadOnly: false, StorageType: StorageType.String })
        {
            if (expected) _identityMissing.Add(name);
            return false;
        }

        // Skipped when unchanged: on a workshared model every Set marks the element as
        // modified, and re-issuing identical text would put the whole model into everyone
        // else's next synchronise for no reason.
        if (string.Equals(parameter.AsString() ?? string.Empty, value, StringComparison.Ordinal))
            return true;

        parameter.Set(value);
        return true;
    }

    /// <summary>Is this one of the categories the identity parameters are bound to?</summary>
    private static bool IsFinishCategory(Element element)
    {
        try
        {
            var id = element.Category?.Id;
            return id is not null &&
                   FinishParameterSetup.FinishElementCategories.Any(c => id.Value == (long)c);
        }
        catch
        {
            return false;
        }
    }

    private static string RoomText(Room room, BuiltInParameter parameter)
    {
        try { return room.get_Parameter(parameter)?.AsString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static bool SetArea(Element element, string name, double value)
    {
        var parameter = ParameterHelper.Find(element, name);
        if (parameter is null || parameter.IsReadOnly) return false;

        parameter.Set(value);
        return true;
    }

    /// <summary>
    /// StorageType is checked here but not in <see cref="SetArea"/>, because the failure it
    /// prevents is specific to text: Parameter.Set(string) on a numeric parameter throws
    /// rather than returning false, which would abort the room mid-write.
    /// </summary>
    private static bool SetText(Element element, string name, string value)
    {
        var parameter = ParameterHelper.Find(element, name);
        if (parameter is not { IsReadOnly: false, StorageType: StorageType.String }) return false;

        parameter.Set(value);
        return true;
    }

    private static void Accumulate(Dictionary<long, double> target, ElementId? id, double value)
    {
        if (id is null || value <= 0) return;
        target[id.Value] = target.GetValueOrDefault(id.Value) + value;
    }

    /// <summary>
    /// Accumulates paint onto the element total AND records which room it came from.
    ///
    /// WHY THE PER-ROOM SPLIT HAD TO BE RECORDED
    ///   The element totals are cross-room sums. A wall between two rooms carries the paint
    ///   of BOTH sides in one number, while <see cref="WriteRoomIdentity"/> gives it a single
    ///   'Rum'. A takeoff grouped by room therefore credits one room with the other's paint -
    ///   the spillover this split exists to stop.
    ///
    ///   <see cref="Claim"/> could not answer it: it deliberately records finish AREA and
    ///   never paint, because counting both would weight painted walls twice when deciding
    ///   which room owns an element. So paint needs its own ledger.
    /// </summary>
    private void AccumulatePaint(
        Dictionary<long, double> target, ElementId? id, Room room, double value)
    {
        Accumulate(target, id, value);

        if (id is null || value <= 0) return;

        if (!_elemRoomPaint.TryGetValue(id.Value, out var byRoom))
            _elemRoomPaint[id.Value] = byRoom = [];

        byRoom[room.Id.Value] = byRoom.GetValueOrDefault(room.Id.Value) + value;
    }

    /// <summary>
    /// Records that <paramref name="room"/> contributed <paramref name="area"/> of finish to
    /// this element. Called beside every AREA accumulation and never beside a paint one —
    /// paint is a subset of the same surface, so counting both would weight painted walls
    /// twice when deciding which room owns the element.
    /// </summary>
    private void Claim(ElementId? id, Room room, double area)
    {
        if (id is null || area <= 0) return;

        if (!_elemRoomClaims.TryGetValue(id.Value, out var byRoom))
            _elemRoomClaims[id.Value] = byRoom = [];

        byRoom[room.Id.Value] = byRoom.GetValueOrDefault(room.Id.Value) + area;
    }

    // ------------------------------------------------------------------ helpers

    private Element? BoundaryElement(SpatialElementBoundarySubface subface)
    {
        try { return _doc.GetElement(subface.SpatialBoundaryElement.HostElementId); }
        catch { return null; }
    }

    /// <summary>
    /// Which element served as the "ceiling" (Danish priority chain: Ceiling > slab above
    /// > roof). Revit's clipping picks the nearest bounding element; this records which one
    /// it was, so the source of every ceiling area is auditable per room.
    /// </summary>
    private string CeilingSourceKey(Element? element)
    {
        ElementId? id;
        try { id = element?.Category?.Id; }
        catch { id = null; }

        if (id == _ceilingCategory) return FinishSettings.SourceCeiling;
        if (id == _floorCategory) return FinishSettings.SourceSlabAbove;
        if (id == _roofCategory) return FinishSettings.SourceRoof;
        return FinishSettings.SourceOther;
    }

    /// <summary>
    /// The value written to "Ceiling Area Source". "(fallback)" is appended when the area
    /// came from the overhead search rather than from a boundary, because the two are not
    /// equally trustworthy: a boundary ceiling is what Revit itself clipped the room with,
    /// whereas a fallback is this add-in's reading of what happens to be overhead.
    /// </summary>
    private static string DescribeCeilingSource(
        Dictionary<string, double> source, CeilingFallbackResult? fallback)
    {
        var used = FinishSettings.CeilingSourceOrder.Where(k => source[k] > 0.005).ToList();
        if (used.Count == 0) return FinishSettings.SourceNone;

        var label = string.Join(" + ", used);
        return fallback is null ? label : label + " (fallback)";
    }

    private static bool TryPointInRoom(Room room, XYZ point)
    {
        try { return room.IsPointInRoom(point); }
        catch { return false; }
    }

    private static string RoomLabel(Room room)
    {
        try
        {
            var number = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? string.Empty;
            var name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;
            var label = $"{number} {name}".Trim();
            return label.Length > 0 ? label : room.Id.Value.ToString();
        }
        catch
        {
            return room.Id.Value.ToString();
        }
    }

    private void AppendCsvRows(Room room, Dictionary<(string Surface, MaterialKey Key), double> materials)
    {
        string number = string.Empty, name = string.Empty;
        try
        {
            number = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? string.Empty;
            name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;
        }
        catch
        {
            // Leave blank.
        }

        foreach (var ((surface, key), area) in materials)
        {
            if (area <= 0.005) continue;

            var (materialName, code, painted) = key.Describe(_doc);
            _csvRows.Add(new FinishCsvRow(number, name, surface, materialName, code, painted,
                Measure.ToSquareMetres(area)));
        }
    }

    private static string F(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string BuildRoomLine(
        Room room, double netWall, double wallExact, int exactFaces, double grossWall, int fallbackFaces,
        int openCount, double openTotal, int caseworkCount, double caseworkTotal,
        double floorArea, double ceilingArea, double paintWall,
        double netFloorSlab, double roomFootprint, double openBelow,
        Dictionary<string, double> ceilingSource,
        int mezzCount, double mezzFloor, double mezzCeiling,
        int hangingCount, double hangingArea,
        int revealCount, double revealPaint,
        double virtualSide, int floorFallback, int ceilingFallback,
        CeilingFallbackResult? ceilingChain,
        IReadOnlyList<string> missing)
    {
        var line = $"{RoomLabel(room)} | Wall net: {F(netWall)} SF" +
                   $" [geometric {F(wallExact)} SF on {exactFaces} face(s)" +
                   $" + fallback {F(grossWall)} SF on {fallbackFaces} face(s)" +
                   $" - {openCount} drs/wins {F(openTotal)} - {caseworkCount} casework {F(caseworkTotal)}]" +
                   $" | Floor: {F(floorArea)} SF | Ceiling: {F(ceilingArea)} SF";

        line += $" | paint basis: {F(paintWall)} SF painted" +
                $" + {F(Math.Max(netWall - paintWall, 0.0))} SF unpainted layer material";

        var openDeduction = Math.Max(roomFootprint - netFloorSlab, 0.0);
        line += $" | NET FLOOR (on slab): {F(netFloorSlab)} SF / {F(Measure.ToSquareMetres(netFloorSlab))} m2" +
                $" [footprint {F(roomFootprint)} SF - open-to-below {F(openDeduction)} SF /" +
                $" {F(Measure.ToSquareMetres(openDeduction))} m2]";

        if (openBelow > 0.005) line += $" (no-slab footprint {F(openBelow)} SF)";

        var sources = string.Join(", ", FinishSettings.CeilingSourceOrder
            .Where(k => ceilingSource[k] > 0.005)
            .Select(k => $"{k} {F(ceilingSource[k])} SF"));

        if (sources.Length > 0) line += " | ceiling from: " + sources;

        if (mezzCount > 0)
            line += $" | mezzanine: {mezzCount} slab(s) adding floor {F(mezzFloor)} SF + ceiling {F(mezzCeiling)} SF";

        if (hangingCount > 0)
            line += $" | interior walls: {hangingCount} adding {F(hangingArea)} SF finish";

        if (revealCount > 0)
            line += $" | door reveals: {revealCount} opening(s) adding {F(revealPaint)} SF /" +
                    $" {F(Measure.ToSquareMetres(revealPaint))} m2 painted return";

        if (virtualSide > 0) line += $" | excluded {F(virtualSide)} SF separation-line boundary";

        if (ceilingChain is not null)
        {
            line += $" | CEILING FALLBACK: nothing bounds this room from above, so its ceiling " +
                    $"finish was measured off the {ceilingChain.Source} instead - " +
                    $"{F(ceilingChain.Total)} SF / {F(Measure.ToSquareMetres(ceilingChain.Total))} m2 [" +
                    string.Join("; ", ceilingChain.Tiers.Select(t =>
                        $"{t.Source} {F(t.Total)} SF from Id " +
                        string.Join("/", t.Hits.Take(6).Select(h => h.Element.Value)))) + "]";
        }
        else if (ceilingArea == 0)
        {
            line += " | NOTE: no top boundary and nothing overhead in the fallback chain either " +
                    "(no ceiling, slab or roof within reach of this room)";
        }

        if (floorFallback > 0)
            line += $" | NOTE: {floorFallback} floor face(s) via fallback (real slab, planar area;" +
                    " openings WITHIN that slab not deducted there)";

        if (ceilingFallback > 0)
            line += $" | NOTE: {ceilingFallback} ceiling face(s) via fallback (ceiling voids NOT deducted there)";

        if (missing.Count > 0) line += " | MISSING PARAMS: " + string.Join(", ", missing);

        return line;
    }
}
