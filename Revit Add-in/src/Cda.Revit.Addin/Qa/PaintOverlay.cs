using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Qa;

/// <summary>
/// Draws the net calculated paint areas as temporary overlay geometry, and removes it again.
///
/// WHY ELEMENT OVERRIDES WERE NOT ENOUGH
///   The first version of this hatch used <see cref="View.SetElementOverrides"/>, which is
///   the right tool for "colour this wall" and the wrong one for "colour the 4.2 m² of this
///   wall that was actually measured". An override applies to an ELEMENT: it colours the
///   whole wall, both faces, full length, opening or no opening. On screen that reads as a
///   much larger area than the parameter it claims to explain, which is worse than no
///   overlay - it makes a correct number look wrong.
///
///   Revit has no per-face override. The per-face mechanism is Paint, and that is a model
///   change. So the only way to draw exactly the measured region is to draw it: a thin solid
///   with the shape of the measurement, laid on the surface it came from.
///
/// WHY THIS IS STILL "TEMPORARY", DESPITE ADDING ELEMENTS
///   Nothing existing is touched. No wall, floor, ceiling, material or parameter changes -
///   the overlay is new geometry sitting beside the model, marked as ours in Extensible
///   Storage, and removed completely by one command. The same pattern the skirting engine
///   uses to own what it places, for the same reason: a tool that cannot find exactly what it
///   made cannot clean up after itself.
///
///   The honest cost, stated plainly: while it is on, the model contains extra Generic Model
///   elements. They will appear in a Generic Models schedule and in a whole-model element
///   count. That is why Clear exists and why the overlay is applied per room rather than
///   across the building.
/// </summary>
public static class PaintOverlay
{
    /// <summary>Extensible Storage tool name. The identity that makes Clear exact.</summary>
    public const string Stamp = "DKSI QA paint overlay";

    public sealed class Result
    {
        public int Created { get; init; }

        /// <summary>
        /// The shapes just placed. Returned rather than re-queried because the caller has to
        /// fold them into the view's temporary isolation, and an isolation that does not
        /// include them hides the overlay the instant it is drawn.
        /// </summary>
        public IReadOnlyList<ElementId> Ids { get; init; } = [];
        public double WallArea { get; init; }
        public double FloorArea { get; init; }
        public double CeilingArea { get; init; }
        public required string Message { get; init; }
    }

    // ---------------------------------------------------------------------- apply

    /// <summary>
    /// Replaces any existing overlay with one for <paramref name="room"/>. Opens its own
    /// transaction; safe to call repeatedly.
    /// </summary>
    public static Result Apply(UIDocument uiDoc, Room room)
    {
        var doc = uiDoc.Document;
        var view = uiDoc.ActiveGraphicalView;

        var extract = new PaintSurfaceExtractor(doc).Extract(room);

        if (!extract.Any)
        {
            // Clear anyway: a stale overlay from the previous room left on screen next to a
            // "nothing to draw" message is the most confusing outcome available.
            Clear(uiDoc);

            return new Result
            {
                Created = 0,
                Message = string.Join(" ", extract.Notes),
            };
        }

        var created = 0;
        var placed = new List<ElementId>();

        try
        {
            Transactions.Run(doc, "QA - paint area overlay", () =>
            {
                DeleteExisting(doc);

                var patternId = RoomHatch.EnsurePattern(doc);

                foreach (var group in extract.Regions.GroupBy(r => r.Kind))
                {
                    var ids = Place(doc, room, group.Key, [.. group]);
                    created += ids.Count;
                    placed.AddRange(ids);

                    if (view is null || view.IsTemplate || patternId == ElementId.InvalidElementId)
                        continue;

                    var overrides = RoomHatch.Build(patternId, RoomHatch.ColourOf(group.Key));

                    foreach (var id in ids)
                    {
                        try { view.SetElementOverrides(id, overrides); }
                        catch { /* view template controls graphics; the shape still shows */ }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"QA paint overlay failed: {ex.Message}");

            return new Result
            {
                Created = 0,
                Message = $"Paint overlay failed: {ex.Message}",
            };
        }

        var wall = Measure.ToSquareMetres(extract.AreaOf(SurfaceKind.Wall));
        var floor = Measure.ToSquareMetres(extract.AreaOf(SurfaceKind.Floor));
        var ceiling = Measure.ToSquareMetres(extract.AreaOf(SurfaceKind.Ceiling));

        Log.Info($"QA paint overlay: {created} shape(s), wall {wall:0.00}, floor {floor:0.00}, " +
                 $"ceiling {ceiling:0.00} m² for room {room.Id.Value}");

        var notes = extract.Notes.Count > 0 ? " " + string.Join(" ", extract.Notes) : string.Empty;

        return new Result
        {
            Created = created,
            Ids = placed,
            WallArea = wall,
            FloorArea = floor,
            CeilingArea = ceiling,
            Message =
                $"Paint overlay drawn on the NET measured areas only - openings and unpainted " +
                $"substrate excluded. Wall {wall:0.00} m², floor {floor:0.00} m², ceiling " +
                $"{ceiling:0.00} m². These should equal the room's Paint Area parameters above; " +
                $"a difference means part of the area came from the engine's arithmetic fallback, " +
                $"which has no geometry to draw. Clear scope removes it." + notes,
        };
    }

    /// <summary>
    /// One DirectShape per surface kind where possible, one per region where not.
    ///
    /// Grouped first because three elements are tidier in a project browser than ninety, and
    /// because the overrides then have three targets. But SetShape is all-or-nothing: a single
    /// solid Revit dislikes loses the whole group, so a failed group is retried one region at
    /// a time rather than dropped.
    /// </summary>
    private static List<ElementId> Place(
        Document doc, Room room, SurfaceKind kind, IReadOnlyList<PaintRegion> regions)
    {
        var ids = new List<ElementId>();

        var grouped = Create(doc, room, kind, [.. regions.Select(r => r.Solid)]);

        if (grouped is not null)
        {
            ids.Add(grouped);
            return ids;
        }

        foreach (var region in regions)
        {
            var single = Create(doc, room, kind, [region.Solid]);
            if (single is not null) ids.Add(single);
        }

        return ids;
    }

    private static ElementId? Create(
        Document doc, Room room, SurfaceKind kind, IList<Solid> solids)
    {
        try
        {
            var shape = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));

            shape.SetShape([.. solids.Cast<GeometryObject>()]);
            shape.Name = $"{Stamp} - {kind}";

            try
            {
                shape.ApplicationId = "DKSI";
                shape.ApplicationDataId = $"qa-paint-{room.Id.Value}-{kind}";
            }
            catch
            {
                // Informational only; the storage stamp below is what Clear relies on.
            }

            // Room recorded by UniqueId, not ElementId, for the same reason the skirting
            // engine does it: ids are reassigned by copy/paste, e-transmit and upgrade.
            ElementStamp.Write(shape, Stamp, kind.ToString(), room.UniqueId);

            return shape.Id;
        }
        catch (Exception ex)
        {
            Log.Warn($"QA paint overlay: {kind} shape rejected: {ex.Message}");
            return null;
        }
    }

    // ---------------------------------------------------------------------- clear

    /// <summary>Removes the overlay. Returns how many elements were deleted.</summary>
    public static int Clear(UIDocument uiDoc)
    {
        var doc = uiDoc.Document;
        var removed = 0;

        try
        {
            Transactions.Run(doc, "QA - clear paint overlay", () => removed = DeleteExisting(doc));
        }
        catch (Exception ex)
        {
            Log.Warn($"QA paint overlay clear failed: {ex.Message}");
            return 0;
        }

        if (removed > 0) Log.Info($"QA paint overlay: removed {removed} shape(s).");

        return removed;
    }

    /// <summary>
    /// Deletes every overlay element this tool has placed, found by its storage mark.
    /// Caller owns the transaction.
    ///
    /// The quick filter matters for the same reason it does in the skirting engine: without
    /// it this is a sweep of every DirectShape in the document with a storage read on each.
    /// </summary>
    private static int DeleteExisting(Document doc)
    {
        var collector = new FilteredElementCollector(doc)
            .OfClass(typeof(DirectShape))
            .WhereElementIsNotElementType();

        var stamped = ElementStamp.Filter();
        var candidates = stamped is null ? collector : collector.WherePasses(stamped);

        var ours = new List<ElementId>();

        foreach (var element in candidates)
        {
            try
            {
                if (ElementStamp.Read(element, Stamp, Stamp) is not null) ours.Add(element.Id);
            }
            catch
            {
                // Unreadable storage; not ours as far as we can tell, so leave it.
            }
        }

        if (ours.Count == 0) return 0;

        try
        {
            doc.Delete(ours);
            return ours.Count;
        }
        catch (Exception ex)
        {
            Log.Warn($"QA paint overlay: delete refused: {ex.Message}");
            return 0;
        }
    }
}
