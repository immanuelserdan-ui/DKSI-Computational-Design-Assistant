using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Finishes;

/// <summary>
/// Watches the model and marks rooms whose finish areas are now out of date.
///
/// It does NOT recalculate anything, and that restraint is the whole point.
///
/// An IUpdater runs INSIDE the user's transaction — the one that commits when they
/// finish dragging a wall. Whatever it does is added to the cost of that edit. The
/// finish calculation does solid boolean operations, ray casts, room clipping and two
/// document regenerations across every room in the model; on a real project that is
/// seconds to minutes. Putting it here would mean every wall drag freezing Revit for
/// as long as a full recalculation takes, and there is no progress bar and no cancel
/// inside a transaction.
///
/// So this does one cheap thing — set a Yes/No parameter on the affected rooms — and
/// the expensive work happens later, at a moment the user expects a pause. See
/// <see cref="FinishAutomation"/>.
/// </summary>
public sealed class FinishStaleUpdater : IUpdater
{
    /// <summary>
    /// Permanent identity for this updater, generated once. Changing it makes Revit
    /// treat it as a different updater and orphan the triggers already stored in any
    /// model that has seen it.
    /// </summary>
    private static readonly Guid UpdaterGuid = new("336bb40b-9f9c-4ee2-a0e0-187204fb7c07");

    /// <summary>
    /// How far beyond a changed element's bounding box to look for rooms it affects,
    /// in feet. A wall bounds the rooms on both of its faces, and its bounding box
    /// stops at those faces, so a query with no margin finds neither.
    /// </summary>
    private const double RoomSearchMarginFeet = 2.0;

    private readonly UpdaterId _id;

    public FinishStaleUpdater(AddInId addInId) => _id = new UpdaterId(addInId, UpdaterGuid);

    /// <summary>
    /// Guards against the updater reacting to its own writes. Setting the stale flag
    /// modifies a room, and rooms are one of the categories being watched.
    /// </summary>
    [ThreadStatic]
    private static bool _reentrant;

    public UpdaterId GetUpdaterId() => _id;

    public string GetUpdaterName() => "Finish area staleness";

    public string GetAdditionalInformation() =>
        "Marks rooms whose wall, floor, ceiling or opening geometry has changed, so their " +
        "finish areas can be recalculated before the model is saved.";

    /// <summary>
    /// Runs after Revit has finished its own room/space bookkeeping, so room boundaries
    /// are settled by the time this reads them.
    /// </summary>
    public ChangePriority GetChangePriority() => ChangePriority.RoomsSpacesZones;

    public void Execute(UpdaterData data)
    {
        // The automation is writing to the model itself — reacting to those writes would
        // schedule another pass to fix what was just fixed.
        if (FinishAutomation.Suppressed) return;

        if (_reentrant) return;

        try
        {
            _reentrant = true;
            MarkAffectedRooms(data);
        }
        catch (Exception ex)
        {
            // An exception escaping an updater aborts the USER'S transaction — their wall
            // edit disappears with no explanation. Staleness tracking is never worth that.
            Log.Error("Finish staleness updater failed; the user's edit was left alone.", ex);
        }
        finally
        {
            _reentrant = false;
        }
    }

    private static void MarkAffectedRooms(UpdaterData data)
    {
        var doc = data.GetDocument();

        var changed = data.GetAddedElementIds()
            .Concat(data.GetModifiedElementIds())
            .ToList();

        // A deleted element cannot be measured — it is already gone, so there is no
        // bounding box to search around. Rather than guess which rooms it touched, fall
        // back to marking the whole model. Deletions are rare next to edits.
        var everything = data.GetDeletedElementIds().Count > 0;

        var rooms = everything
            ? AllPlacedRooms(doc)
            : RoomsNear(doc, changed);

        var marked = 0;
        foreach (var room in rooms)
            if (FinishAutomation.MarkStale(room)) marked++;

        if (marked > 0)
            Log.Info($"Finish staleness: {marked} room(s) marked out of date.");
    }

    private static IEnumerable<Element> AllPlacedRooms(Document doc) =>
        new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .Where(r => r.Area > 0)          // unplaced rooms have no geometry to be stale about
            .Cast<Element>();

    /// <summary>
    /// Finds the rooms a changed element could plausibly affect, by bounding box.
    ///
    /// Deliberately approximate. Marking a room stale that did not need it costs one
    /// extra room in the next recalculation; missing one leaves a wrong area in a
    /// schedule, which is the failure that reaches a drawing.
    /// </summary>
    private static IEnumerable<Element> RoomsNear(Document doc, IReadOnlyCollection<ElementId> changed)
    {
        var found = new Dictionary<ElementId, Element>();

        foreach (var id in changed)
        {
            var element = doc.GetElement(id);
            if (element is null) continue;

            // Skip the rooms themselves: a room is marked because something bounding it
            // moved, not because it was the thing that moved.
            if (element is Room) continue;

            var box = element.get_BoundingBox(null);
            if (box is null) continue;

            var outline = new Outline(
                new XYZ(box.Min.X - RoomSearchMarginFeet,
                        box.Min.Y - RoomSearchMarginFeet,
                        box.Min.Z - RoomSearchMarginFeet),
                new XYZ(box.Max.X + RoomSearchMarginFeet,
                        box.Max.Y + RoomSearchMarginFeet,
                        box.Max.Z + RoomSearchMarginFeet));

            var nearby = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .ToElements();

            foreach (var room in nearby)
                found[room.Id] = room;
        }

        return found.Values;
    }

    /// <summary>
    /// The categories worth watching: anything whose geometry changes a room's finish
    /// surfaces. Furniture and annotation are excluded — they move constantly and
    /// change nothing this tool measures.
    ///
    /// PAINT CHANGES NEED NO CATEGORY OF THEIR OWN. Painting a face, unpainting it, or
    /// swapping the material on it are all modifications of the HOST element — the wall,
    /// floor or ceiling — so they arrive through the categories already listed, on the
    /// <c>GetChangeTypeAny</c> trigger registered in FinishAutomation. There is no
    /// "paint changed" change type in the API to subscribe to separately.
    ///
    /// THE VOID CUTTERS BELOW ARE BELT AND BRACES, not the primary path. When a void cuts
    /// a wall, Revit normally reports the WALL as modified and the wall is already watched.
    /// They are here for the case where the cutting family moves and the host is not
    /// flagged — a missed cut leaves a painted area measured over a hole that is no longer
    /// there, and a wrong area in a schedule is the failure that reaches a drawing. The
    /// cost of being wrong the other way is one extra room in the next recalculation.
    /// </summary>
    public static ElementMulticategoryFilter TriggerFilter() =>
        new(
        [
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_Floors,
            BuiltInCategory.OST_Ceilings,
            BuiltInCategory.OST_Roofs,
            BuiltInCategory.OST_Doors,
            BuiltInCategory.OST_Windows,
            BuiltInCategory.OST_Rooms,
            BuiltInCategory.OST_SWallRectOpening,   // wall openings cut without a family
            BuiltInCategory.OST_ArcWallRectOpening,
            BuiltInCategory.OST_ShaftOpening,
            BuiltInCategory.OST_FloorOpening,
            BuiltInCategory.OST_CeilingOpening,
            BuiltInCategory.OST_RoofOpening,
            BuiltInCategory.OST_Casework,           // affects the arithmetic fallback path
            BuiltInCategory.OST_GenericModel,       // in-place and loaded void cutters
            BuiltInCategory.OST_Mass,               // masses used as cutting geometry
        ]);
}
