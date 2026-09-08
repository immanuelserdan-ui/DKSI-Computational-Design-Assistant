using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Finishes;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Overlay;

/// <summary>
/// Draws the NET painted area of ONE element as temporary overlay geometry, and removes it.
///
/// THE PROBLEM THIS SOLVES
///   Selecting a row in a Material Takeoff schedule highlights the whole host wall. It has to:
///   a schedule row's identity is an ElementId, and a material takeoff subdivides an element's
///   REPORTING, not its identity - every material row for a wall carries the same wall id.
///   Revit's selection set is a collection of element ids and there is no face-level or
///   material-level selection state to switch on instead.
///
///   The display side has the same limit. View.SetElementOverrides is element-scoped: colour a
///   wall and you get the whole wall, both faces, full length, openings included. Revit has no
///   per-face override. The only per-face mechanism is Paint, and that is a model change, not
///   a display state.
///
///   So the only way to show exactly the 4.15 m² behind a row is to DRAW it: a thin solid with
///   the shape of the measurement, laid on the surface it came from.
///
/// WHY IT DRAWS EVERY ROOM'S SHARE OF THE ELEMENT
///   A schedule row is really a (room, element) pair - the same wall appears once per room it
///   bounds, with a different area each time. Selection gives us the ELEMENT only; nothing in
///   the API reports which row was clicked. So rather than guess a room, this draws every
///   painted region the element owns, in all the rooms it bounds. Each room's share is a
///   separate solid in a different place, so they read apart on screen anyway, and each shape
///   is NAMED with its room so the Properties palette answers "which row is this one".
///
/// WHY THIS IS STILL "TEMPORARY", DESPITE ADDING ELEMENTS
///   Nothing existing is touched. No wall, floor, ceiling, material or parameter changes - the
///   overlay is new geometry sitting beside the model, marked as ours in Extensible Storage,
///   and removed completely by disarming the toggle or selecting something else.
///
///   The honest cost, stated plainly: while it is on, the model contains extra Generic Model
///   elements. They appear in a Generic Models schedule and in a whole-model element count.
///   That is why <see cref="Clear"/> exists, why the highlight is scoped to one element rather
///   than the building, and why <see cref="PaintHighlightService"/> clears on document close.
/// </summary>
internal static class PaintHighlight
{
    /// <summary>Extensible Storage tool name. The identity that makes Clear exact.</summary>
    public const string Stamp = "DKSI paint highlight";

    /// <summary>
    /// Transaction name prefix. Every transaction opened here starts with it so
    /// <see cref="FinishAutomation"/> can recognise the write as ours and NOT queue a
    /// recalculation off it.
    ///
    /// This is the fix the QA removal reverted. Its overlay transactions were named
    /// "QA - ..." and matched nothing, so drawing an overlay queued a room recalculation and
    /// clearing one queued a full-model sweep - the tool for looking at the numbers was
    /// changing them.
    /// </summary>
    public const string TransactionPrefix = "DKSI paint highlight: ";

    private const string PatternName = "DKSI paint highlight";
    private const double PatternAngleDegrees = 45.0;
    private const double PatternSpacingMm = 3.0;

    /// <summary>How far past the element's own box to look for rooms, in feet.</summary>
    private const double RoomSearchPadding = 1.0;

    private static readonly Color WallColour = new(0, 132, 255);
    private static readonly Color FloorColour = new(0, 168, 96);
    private static readonly Color CeilingColour = new(226, 112, 0);

    public sealed class Result
    {
        public int Created { get; init; }
        public double AreaSqm { get; init; }
        public IReadOnlyList<string> Notes { get; init; } = [];
        public bool DrewSomething => Created > 0;
    }

    // ---------------------------------------------------------------------- show

    /// <summary>
    /// Replaces any existing highlight with one for <paramref name="hostId"/>. Opens its own
    /// transaction; safe to call repeatedly. Must run inside a valid API context.
    /// </summary>
    public static Result Show(UIDocument uiDoc, ElementId hostId)
    {
        var doc = uiDoc.Document;
        var view = uiDoc.ActiveGraphicalView;

        // A TAKEOFF ROW IS NOT A SURFACE, so follow it to the wall it was measured on.
        //
        // Selecting a row in 'DKSI Paint Takeoff by Room' selects a geometry-less DirectShape.
        // Without this, RoomsTouching finds nothing - the element has no bounding box to search
        // around - and the reading mode that exists to explain schedule figures drew a blank on
        // the only schedule whose figures are correct, while working fine on the Wall Material
        // Takeoff whose figures are not. That was exactly backwards.
        hostId = ResolveTakeoffRow(doc, hostId);

        var host = doc.GetElement(hostId);
        if (host is null) return ClearAndReport(uiDoc, "That element no longer exists.");

        var rooms = RoomsTouching(doc, host);
        if (rooms.Count == 0)
        {
            return ClearAndReport(uiDoc,
                "No placed room touches this element, so it contributes to no room's paint area.");
        }

        // Every painted region this element owns, gathered per room so each one can carry the
        // room's name. The extractor is room-driven because that is how the finish engine
        // measures - asking it per element would be asking a different question than the one
        // the schedule answers.
        var found = new List<(Room Room, PaintRegion Region)>();
        var notes = new List<string>();

        var extractor = new PaintSurfaceExtractor(doc);

        foreach (var room in rooms)
        {
            PaintExtractResult extract;
            try { extract = extractor.Extract(room); }
            catch (Exception ex)
            {
                Log.Warn($"Paint highlight: room {room.Id.Value} could not be extracted: {ex.Message}");
                continue;
            }

            foreach (var region in extract.Regions.Where(r => r.Host == hostId))
                found.Add((room, region));

            // Only worth repeating the extractor's own diagnosis when it concerns THIS element.
            if (extract.ClipFailedHosts.Contains(hostId.Value))
            {
                notes.Add(
                    $"Part of this element's area in {Describe(room)} could not be intersected " +
                    "with the room boundary. The engine falls back to an arithmetic measurement " +
                    "there, which has no geometry to draw - so the highlight is smaller than the " +
                    "schedule figure by that much.");
            }
        }

        if (found.Count == 0)
        {
            var bounding = rooms.Count == 1 ? "the room it bounds" : "any room it bounds";

            return ClearAndReport(uiDoc,
                $"NO PAINTED FACES on this element in {bounding}. Its faces carry the wall-type " +
                "layer material rather than a painted material, so it contributes 0 to Wall Paint " +
                "Area and there is nothing to draw. Its FINISH area is a different number and is " +
                "not zero. Paint is applied with Modify > Paint, or by giving the finish layer a " +
                "material flagged 'As Paint'.");
        }

        var created = 0;

        try
        {
            Transactions.Run(doc, TransactionPrefix + "draw", () =>
            {
                DeleteExisting(doc);

                var patternId = EnsurePattern(doc);

                foreach (var group in found.GroupBy(f => (f.Room.Id, f.Region.Kind)))
                {
                    var room = group.First().Room;
                    var kind = group.Key.Kind;

                    var ids = Place(doc, room, kind, [.. group.Select(g => g.Region)]);
                    created += ids.Count;

                    if (view is null || view.IsTemplate || patternId == ElementId.InvalidElementId)
                        continue;

                    var overrides = BuildOverrides(patternId, ColourOf(kind));

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
            Log.Warn($"Paint highlight failed: {ex.Message}");
            return new Result { Created = 0, Notes = [$"Paint highlight failed: {ex.Message}"] };
        }

        var sqm = Measure.ToSquareMetres(found.Sum(f => f.Region.Area));

        var perRoom = string.Join(", ", found
            .GroupBy(f => f.Room.Id)
            .Select(g => $"{Describe(g.First().Room)} {Measure.ToSquareMetres(g.Sum(x => x.Region.Area)):0.00}"));

        Log.Info($"Paint highlight: {created} shape(s), {sqm:0.00} m² on element " +
                 $"{hostId.Value} across {found.Select(f => f.Room.Id).Distinct().Count()} room(s) - {perRoom}");

        return new Result { Created = created, AreaSqm = sqm, Notes = notes };
    }

    private static Result ClearAndReport(UIDocument uiDoc, string note)
    {
        // Clear regardless: a stale highlight from the previous row left on screen beside a
        // "nothing to draw" outcome is the most confusing result available.
        Clear(uiDoc);
        return new Result { Created = 0, Notes = [note] };
    }

    // ---------------------------------------------------------------------- rooms

    /// <summary>
    /// The placed rooms whose bounding box touches this element's.
    ///
    /// A bounding-box filter rather than a walk over every room in the model: the extractor
    /// runs a full SpatialElementGeometryCalculator per room, which is far too expensive to do
    /// building-wide on every selection change. A wall touches two to four rooms, and the box
    /// test finds them without computing anything.
    ///
    /// Padded because a wall's box and a room's box meet exactly at the finish face, and an
    /// exact-touch test is the one case floating point will not answer reliably.
    /// </summary>
    private static List<Room> RoomsTouching(Document doc, Element host)
    {
        BoundingBoxXYZ? box;
        try { box = host.get_BoundingBox(null); }
        catch { return []; }

        if (box is null) return [];

        var pad = new XYZ(RoomSearchPadding, RoomSearchPadding, RoomSearchPadding);
        var outline = new Outline(box.Min - pad, box.Max + pad);

        try
        {
            return [.. new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .OfType<Room>()
                // An unplaced room reports Area 0 and has no geometry to calculate against.
                .Where(r => r.Area > 0.0)];
        }
        catch (Exception ex)
        {
            Log.Warn($"Paint highlight: room search failed: {ex.Message}");
            return [];
        }
    }

    private static string Describe(Room room)
    {
        try
        {
            var name = room.Name;
            return string.IsNullOrWhiteSpace(name) ? $"room {room.Id.Value}" : name;
        }
        catch
        {
            return $"room {room.Id.Value}";
        }
    }

    // ---------------------------------------------------------------------- place

    /// <summary>
    /// One DirectShape per (room, surface kind) where possible, one per region where not.
    ///
    /// Grouped first because a handful of elements is tidier in a project browser than ninety,
    /// and because the overrides then have few targets. But SetShape is all-or-nothing: a
    /// single solid Revit dislikes loses the whole group, so a failed group is retried one
    /// region at a time rather than dropped.
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

    private static ElementId? Create(Document doc, Room room, SurfaceKind kind, IList<Solid> solids)
    {
        try
        {
            var shape = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));

            shape.SetShape([.. solids.Cast<GeometryObject>()]);

            // The room is IN THE NAME on purpose. Selection cannot tell us which schedule row
            // was clicked, but a shape called "... - Alrum - Wall" lets the Properties palette
            // answer it the moment the user clicks the highlight itself.
            shape.Name = $"{Stamp} - {Describe(room)} - {kind}";

            try
            {
                shape.ApplicationId = "DKSI";
                shape.ApplicationDataId = $"paint-highlight-{room.Id.Value}-{kind}";
            }
            catch
            {
                // Informational only; the storage stamp below is what Clear relies on.
            }

            // Room recorded by UniqueId, not ElementId, for the same reason the skirting engine
            // does it: ids are reassigned by copy/paste, e-transmit and upgrade.
            //
            // WriteOrFallback, and refused if neither path takes. Clear finds this overlay by
            // exactly this stamp, so an unstamped shape can never be removed - by the toggle, by
            // a later session, or by anything else - and the overlay is real Generic Model
            // geometry that would then be saved into the model permanently. Drawing nothing is
            // the better failure for a tool whose whole job is to be looked at and then taken
            // away again.
            if (!ElementStamp.WriteOrFallback(shape, Stamp, kind.ToString(), Stamp, room.UniqueId))
            {
                Log.Warn($"Paint highlight: {kind} shape could not be stamped, so Clear would " +
                         "never find it. Removed rather than left in the model.");

                try { doc.Delete(shape.Id); } catch { /* the warning above stands */ }

                return null;
            }

            return shape.Id;
        }
        catch (Exception ex)
        {
            Log.Warn($"Paint highlight: {kind} shape rejected: {ex.Message}");
            return null;
        }
    }

    // ---------------------------------------------------------------------- clear

    /// <summary>Removes the highlight. Returns how many elements were deleted.</summary>
    public static int Clear(UIDocument uiDoc) => Clear(uiDoc.Document);

    /// <summary>Removes the highlight from a document directly. Must run in an API context.</summary>
    public static int Clear(Document doc)
    {
        var removed = 0;

        try
        {
            Transactions.Run(doc, TransactionPrefix + "clear", () => removed = DeleteExisting(doc));
        }
        catch (Exception ex)
        {
            Log.Warn($"Paint highlight clear failed: {ex.Message}");
            return 0;
        }

        if (removed > 0) Log.Info($"Paint highlight: removed {removed} shape(s).");

        return removed;
    }

    /// <summary>True if <paramref name="id"/> is one of our overlay shapes.</summary>
    /// <summary>
    /// If <paramref name="id"/> is a paint-takeoff row, the element it was measured on.
    /// Anything else is returned unchanged.
    ///
    /// Reads the row's own 'Paint Host Id' rather than trying to re-derive the host from room
    /// and material, because the row already knows: the takeoff writes it at placement time
    /// from the same measurement pass that produced the area. Re-deriving would be a second
    /// answer that could disagree with the first.
    ///
    /// Returns the row's id unchanged when the parameter is absent (a takeoff placed before
    /// this existed) or blank (the arithmetic fallback bucket, which has no single host). The
    /// caller then reports "no room touches this element", which is true of the row and is a
    /// better outcome than silently highlighting the wrong wall.
    /// </summary>
    private static ElementId ResolveTakeoffRow(Document doc, ElementId id)
    {
        try
        {
            var element = doc.GetElement(id);

            if (element is not DirectShape) return id;
            // Same two arguments the takeoff's own DeleteExisting passes: the tool name doubles
            // as the legacy Comments prefix, so a row from before Extensible Storage is still
            // recognised as ours.
            var stamp = Schedules.PaintTakeoffBuilder.Stamp;
            if (ElementStamp.Read(element, stamp, stamp) is null) return id;

            var parameter = ParameterHelper.Find(element, new FinishSettings().PaintHostParameter);
            var text = parameter?.AsString();

            if (string.IsNullOrWhiteSpace(text)) return id;
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostId))
                return id;

            var host = doc.GetElement(new ElementId(hostId));
            return host is null ? id : host.Id;
        }
        catch
        {
            return id;
        }
    }

    public static bool IsOurs(Document doc, ElementId id)
    {
        try
        {
            var element = doc.GetElement(id);
            return element is DirectShape && ElementStamp.Read(element, Stamp, Stamp) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes every overlay element this tool has placed, found by its storage mark.
    /// Caller owns the transaction.
    ///
    /// The quick filter matters for the same reason it does in the skirting engine: without it
    /// this is a sweep of every DirectShape in the document with a storage read on each.
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
            Log.Warn($"Paint highlight: delete refused: {ex.Message}");
            return 0;
        }
    }

    // ---------------------------------------------------------------------- graphics

    public static Color ColourOf(SurfaceKind kind) => kind switch
    {
        SurfaceKind.Floor => FloorColour,
        SurfaceKind.Ceiling => CeilingColour,
        _ => WallColour,
    };

    /// <summary>
    /// The hatch pattern, found or created. Caller owns the transaction.
    ///
    /// CREATED RATHER THAN LOOKED UP BY NAME. Reaching for a built-in like "Diagonal up" looks
    /// simpler and breaks on this project: pattern names are localised, so a Danish
    /// installation has different ones, and a template may not carry the pattern at all.
    /// Creating our own is deterministic, needs no fallback list, and cannot collide with a
    /// pattern the office uses for real documentation.
    /// </summary>
    private static ElementId EnsurePattern(Document doc)
    {
        try
        {
            var existing = FillPatternElement.GetFillPatternElementByName(
                doc, FillPatternTarget.Drafting, PatternName);

            if (existing is not null) return existing.Id;
        }
        catch
        {
            // Lookup failed; fall through and create.
        }

        try
        {
            var pattern = new FillPattern(
                PatternName,
                FillPatternTarget.Drafting,
                FillPatternHostOrientation.ToView,
                PatternAngleDegrees * Math.PI / 180.0,
                Measure.FromMillimetres(PatternSpacingMm));

            return FillPatternElement.Create(doc, pattern).Id;
        }
        catch (Exception ex)
        {
            Log.Warn($"Paint highlight: fill pattern could not be created: {ex.Message}");
            return ElementId.InvalidElementId;
        }
    }

    private static OverrideGraphicSettings BuildOverrides(ElementId patternId, Color colour)
    {
        var overrides = new OverrideGraphicSettings();

        overrides.SetSurfaceForegroundPatternId(patternId);
        overrides.SetSurfaceForegroundPatternColor(colour);
        overrides.SetSurfaceForegroundPatternVisible(true);

        overrides.SetSurfaceBackgroundPatternId(patternId);
        overrides.SetSurfaceBackgroundPatternColor(colour);
        overrides.SetSurfaceBackgroundPatternVisible(true);

        // Cut faces too, so the hatch survives being sliced by a section box. A 3D view is
        // almost always cut somewhere, and an overlay that vanishes at the cut looks broken.
        overrides.SetCutForegroundPatternId(patternId);
        overrides.SetCutForegroundPatternColor(colour);
        overrides.SetCutForegroundPatternVisible(true);

        overrides.SetProjectionLineColor(colour);
        overrides.SetCutLineColor(colour);

        return overrides;
    }
}
