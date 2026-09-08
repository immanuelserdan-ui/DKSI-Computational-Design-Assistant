using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Finishes;

/// <summary>Which of the three paint parameters a region contributes to.</summary>
public enum SurfaceKind
{
    Wall,
    Floor,
    Ceiling,
}

/// <summary>One net painted region: the exact geometry behind part of a paint area.</summary>
public sealed class PaintRegion
{
    public required Solid Solid { get; init; }
    public required SurfaceKind Kind { get; init; }
    public required ElementId Host { get; init; }

    /// <summary>Square feet, internal units. Sums to the room's paint parameter.</summary>
    public required double Area { get; init; }
}

public sealed class PaintExtractResult
{
    public List<PaintRegion> Regions { get; } = [];
    public List<string> Notes { get; } = [];

    /// <summary>
    /// Hosts that produced a boundary subface - the ones Revit considers to BOUND the room.
    ///
    /// Recorded because its complement is the interesting set: an element in the room's
    /// enclosure that is absent from here bounds nothing, so the finish engine never measured
    /// it and no amount of paint on it reaches the room's parameters. That is the difference
    /// between "this face has no paint" and "this face was never looked at", and only the
    /// second is a modelling error.
    /// </summary>
    public HashSet<long> BoundingHosts { get; } = [];

    /// <summary>Hosts with at least one coplanar face that <c>IsPainted</c> reported true for.</summary>
    public HashSet<long> PaintedHosts { get; } = [];

    /// <summary>Hosts where a painted face was found but the boolean clip failed.</summary>
    public HashSet<long> ClipFailedHosts { get; } = [];

    public double AreaOf(SurfaceKind kind) =>
        Regions.Where(r => r.Kind == kind).Sum(r => r.Area);

    public double AreaOfHost(ElementId id) =>
        Regions.Where(r => r.Host == id).Sum(r => r.Area);

    public bool Any => Regions.Count > 0;
}

/// <summary>
/// Rebuilds the EXACT surfaces behind Wall / Floor / Ceiling Paint Area, as solids.
///
/// WHY THIS DUPLICATES FinishGeometry.ExactSubfaceArea
///   That method computes precisely the right thing and then throws away the only part this
///   needs. It intersects the room's boundary subface with the host's real face - geometry
///   that already excludes every cut, so openings and recesses are gone - and returns the
///   AREA of the result. The intersection solid itself, which is the shape of the paint, is
///   discarded on the next line.
///
///   So the same boolean is run here with the same inputs, the same coplanar test and the
///   same tolerances, and the solid is kept. Deliberately not refactored into a shared
///   method: the finish engine is the production measurement path, and a QA overlay is not a
///   good enough reason to change the signature of the code that produces the numbers on the
///   drawings. If the two ever disagree, the engine is right by definition.
///
/// WHAT MAKES THIS "PAINT" RATHER THAN "FINISH"
///   One line: Document.IsPainted. A host face carries paint only where someone painted it,
///   and the engine's paint total is the sum over exactly those faces
///   (MaterialLedger.PaintedTotal). Faces that are merely finished - the wall-type layer
///   material - contribute to Wall Finish Area and NOT to Wall Paint Area. Hatching the
///   finish faces instead would draw a larger region than the number it claims to explain.
///
/// THE CONSEQUENCE WORTH SAYING OUT LOUD
///   In a model where nobody has used Paint, every paint area is legitimately zero and this
///   returns nothing. That is a correct answer, and an empty overlay looks exactly like a
///   broken tool - so the caller reports the distinction rather than showing a blank view.
/// </summary>
public sealed class PaintSurfaceExtractor
{
    private readonly Document _doc;
    private readonly FinishGeometry _geometry;

    public PaintSurfaceExtractor(Document doc)
    {
        _doc = doc;
        _geometry = new FinishGeometry(doc);
    }

    public PaintExtractResult Extract(Room room)
    {
        var result = new PaintExtractResult();

        if (!SpatialElementGeometryCalculator.CanCalculateGeometry(room))
        {
            result.Notes.Add("This room's geometry cannot be computed, so no paint surface exists to draw.");
            return result;
        }

        var options = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
        };

        SpatialElementGeometryResults results;

        try
        {
            results = new SpatialElementGeometryCalculator(_doc, options)
                .CalculateSpatialElementGeometry(room);
        }
        catch (Exception ex)
        {
            result.Notes.Add($"Room geometry failed: {ex.Message}");
            return result;
        }

        var unpaintedHosts = 0;
        var booleanFailures = 0;

        foreach (Face roomFace in results.GetGeometry().Faces)
        {
            var kind = Classify(roomFace);

            IList<SpatialElementBoundarySubface> subfaces;
            try { subfaces = results.GetBoundaryFaceInfo(roomFace); }
            catch { continue; }

            if (subfaces is null) continue;

            foreach (var subface in subfaces)
            {
                Element? owner;
                Face? subfaceGeometry;

                try
                {
                    var boundary = subface.SpatialBoundaryElement;

                    // Linked hosts cannot be measured from here, and the finish engine does
                    // not measure them either - so they contribute to no paint area and
                    // drawing them would show area the parameter does not contain.
                    if (boundary.LinkInstanceId != ElementId.InvalidElementId) continue;
                    if (boundary.HostElementId == ElementId.InvalidElementId) continue;

                    owner = _doc.GetElement(boundary.HostElementId);
                    subfaceGeometry = subface.GetSubface();
                }
                catch
                {
                    continue;
                }

                if (owner is null || subfaceGeometry is null) continue;

                // Reaching here means this element genuinely bounds the room, whatever comes
                // of the paint test below.
                result.BoundingHosts.Add(owner.Id.Value);

                var before = result.Regions.Count;

                Intersect(subfaceGeometry, owner, kind, room, result, ref booleanFailures);

                if (result.Regions.Count == before) unpaintedHosts++;
            }
        }

        if (booleanFailures > 0)
        {
            result.Notes.Add(
                $"{booleanFailures} painted face(s) could not be intersected with the room " +
                "boundary. The finish engine falls back to an arithmetic measurement for those, " +
                "which has no geometry to draw - so the overlay is smaller than the parameter " +
                "by that much.");
        }

        if (!result.Any)
        {
            result.Notes.Add(
                unpaintedHosts > 0
                    ? "NO PAINTED FACES in this room. Every bounding face carries its wall-type " +
                      "layer material rather than a painted material, so Wall/Floor/Ceiling Paint " +
                      "Area are legitimately zero and there is nothing to hatch. The FINISH areas " +
                      "are a different number and are not zero. This is a modelling fact, not a " +
                      "failure of the check - paint is applied with Modify > Paint, or by giving " +
                      "the finish layer a material flagged 'As Paint'."
                    : "No bounding faces could be measured for this room.");
        }

        return result;
    }

    // ----------------------------------------------------------------- the boolean

    /// <summary>
    /// The room subface clipped to the host's PAINTED real faces. Same coplanar test, same
    /// extrusion thickness and same tolerances as <see cref="FinishGeometry.ExactSubfaceArea"/>,
    /// so the geometry drawn is the geometry that was measured.
    /// </summary>
    private void Intersect(
        Face roomFace, Element owner, SurfaceKind kind, Room room, PaintExtractResult result, ref int failures)
    {
        var (roomOrigin, roomNormal) = FinishGeometry.PlanarData(roomFace);
        if (roomOrigin is null || roomNormal is null) return;

        List<Face> hostFaces;
        try { hostFaces = _geometry.CachedFaces(owner); }
        catch { return; }

        foreach (var hostFace in hostFaces)
        {
            var (hostOrigin, hostNormal) = FinishGeometry.PlanarData(hostFace);
            if (hostOrigin is null || hostNormal is null) continue;

            if (Math.Abs(Math.Abs(roomNormal.DotProduct(hostNormal)) - 1.0) > 0.01) continue;
            if (Math.Abs((hostOrigin - roomOrigin).DotProduct(roomNormal)) > FinishSettings.CoplanarTolerance)
                continue;

            // THE PAINT TEST. Everything above finds the finish face; this is what narrows it
            // to the painted part, and it is the whole difference between this overlay and one
            // that draws a bigger region than the number it explains.
            bool painted;
            try { painted = _doc.IsPainted(owner.Id, hostFace); }
            catch { painted = false; }

            if (!painted) continue;

            result.PaintedHosts.Add(owner.Id.Value);

            try
            {
                var roomSolid = GeometryCreationUtilities.CreateExtrusionGeometry(
                    roomFace.GetEdgesAsCurveLoops(), roomNormal, FinishSettings.ExtrudeThickness);

                var hostSolid = GeometryCreationUtilities.CreateExtrusionGeometry(
                    hostFace.GetEdgesAsCurveLoops(), roomNormal, FinishSettings.ExtrudeThickness);

                var intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                    roomSolid, hostSolid, BooleanOperationsType.Intersect);

                if (intersection is null || intersection.Volume <= 1e-9)
                {
                    failures++;
                    result.ClipFailedHosts.Add(owner.Id.Value);
                    continue;
                }

                // OCCLUSION - the QA-overlay half of the same fix FinishGeometry.ExactSubfaceArea
                // already carries. Kept as its own copy rather than a shared call for the exact
                // reason the rest of this file duplicates that method: this overlay must draw
                // the same shape the finish engine measured, but the engine's signature is not
                // this class's to change. A mezzanine or hanging wall standing against this face
                // means the highlighted region would otherwise be drawn INSIDE that element's own
                // solid - visually exactly what the reference screenshots show as wrong - even
                // once the engine's own number is already correct.
                foreach (var occluder in OccludingElements(owner, room))
                {
                    List<Solid> occluderSolids;
                    try { occluderSolids = _geometry.ElementSolids(occluder); }
                    catch { continue; }

                    foreach (var solid in occluderSolids)
                    {
                        if (solid.Volume <= 1e-9) continue;

                        try
                        {
                            var reduced = BooleanOperationsUtils.ExecuteBooleanOperation(
                                intersection, solid, BooleanOperationsType.Difference);

                            // A FAILED CUT LEAVES THE SHAPE UNCHANGED, same discipline as the
                            // engine's own copy: an overlay that occasionally draws slightly too
                            // much is honest about a geometry edge case; one that vanishes on a
                            // boolean failure looks like the tool is broken.
                            if (reduced is not null) intersection = reduced;
                        }
                        catch
                        {
                            // Left unresolved; this occluder simply does not reduce the shape.
                        }

                        if (intersection.Volume <= 1e-9) break;
                    }

                    if (intersection.Volume <= 1e-9) break;
                }

                // FULLY OCCLUDED: nothing left to draw here, and unlike a genuinely failed
                // boolean above, this is not counted as a clip failure - the paint IS behind
                // the occluder, which is exactly the case this fix exists to stop showing.
                if (intersection.Volume <= 1e-9) continue;

                var area = intersection.Volume / FinishSettings.ExtrudeThickness;

                // The sliver is built INSIDE the host, because both extrusions run along the
                // room face's outward normal - which points away from the room and into the
                // wall. Sliding it back by its own thickness puts it in the room, sitting on
                // the surface it describes, where it can actually be seen.
                var display = SolidUtils.CreateTransformed(
                    intersection,
                    Transform.CreateTranslation(-roomNormal * FinishSettings.ExtrudeThickness));

                result.Regions.Add(new PaintRegion
                {
                    Solid = display,
                    Kind = kind,
                    Host = owner.Id,
                    Area = area,
                });
            }
            catch
            {
                failures++;
                result.ClipFailedHosts.Add(owner.Id.Value);
            }
        }
    }

    /// <summary>
    /// Interior slabs and hanging walls near <paramref name="host"/> - the same candidate
    /// shape as <c>RoomFinishCalculator.OccludingElements</c>, queried live rather than from
    /// a whole-model cache. This overlay runs on demand for one element at a time, not across
    /// every room in the model, so the collector this method pays for is a live query - see
    /// the class doc for why this is its own copy rather than a shared call.
    /// </summary>
    private List<Element> OccludingElements(Element host, Room room)
    {
        var found = new List<Element>();

        try
        {
            var hostBox = host.get_BoundingBox(null);
            if (hostBox is null) return found;

            var roomBox = SafeBoundingBox(room);

            // UNFILTERED BY ROOM BOUNDING, DELIBERATELY - see RoomFinishCalculator's copy of
            // this method for the full reasoning. Room Bounding = No is this office's own
            // convention for a mezzanine/hanging wall and is accepted unconditionally, exactly
            // as before. Room Bounding = Yes is the convention PaintedMaterialTakeoff's own
            // InteriorElementCalculator REQUIRES, and is accepted only when FloatsWithinRoom
            // confirms the candidate stands clear of the room's own vertical extent - without
            // that test this would also catch every ordinary wall corner and every room's own
            // floor touching its own walls' base.
            var candidates = new FilteredElementCollector(_doc)
                .WherePasses(new LogicalOrFilter(
                    new ElementCategoryFilter(BuiltInCategory.OST_Floors),
                    new ElementCategoryFilter(BuiltInCategory.OST_Walls)))
                .WhereElementIsNotElementType();

            foreach (var candidate in candidates)
            {
                if (candidate.Id == host.Id) continue;

                try
                {
                    var box = candidate.get_BoundingBox(null);
                    if (box is null || !BoxesOverlap(hostBox, box)) continue;

                    var eligible = IsNonRoomBounding(candidate) ||
                                   (roomBox is not null && FloatsWithinRoom(candidate, roomBox, box));

                    if (eligible) found.Add(candidate);
                }
                catch
                {
                    // One candidate's box failing must not cost the rest of the list.
                }
            }
        }
        catch
        {
            // No box on the host itself - nothing to filter against.
        }

        return found;
    }

    private static BoundingBoxXYZ? SafeBoundingBox(Element element)
    {
        try { return element.get_BoundingBox(null); }
        catch { return null; }
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

    /// <summary>Same test as RoomFinishCalculator.FloatsWithinRoom - see that copy for the
    /// full reasoning. In short: walls need clearance on EITHER side (OR); floors need it on
    /// BOTH (AND), because a floor is inherently thin and one side always has huge
    /// clearance regardless - the room's own base floor is clear of the ceiling, and a floor
    /// serving as the room's own ceiling (overlapping the wall-tops it joins, by design) is
    /// clear of the base. Only real air on both sides is a genuine floating mezzanine.</summary>
    private static bool FloatsWithinRoom(Element candidate, BoundingBoxXYZ roomBox, BoundingBoxXYZ candidateBox)
    {
        const double margin = 1.0;   // feet

        var clearBelow = candidateBox.Min.Z - roomBox.Min.Z > margin;
        var clearAbove = roomBox.Max.Z - candidateBox.Max.Z > margin;

        return candidate is Floor ? clearBelow && clearAbove : clearBelow || clearAbove;
    }

    private static bool BoxesOverlap(BoundingBoxXYZ a, BoundingBoxXYZ b) =>
        a.Min.X <= b.Max.X && a.Max.X >= b.Min.X &&
        a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y &&
        a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;

    /// <summary>
    /// Which paint parameter a room face feeds, from its normal.
    ///
    /// The room solid's normals point OUT of the room, so its underside points down and its
    /// top points up - the floor is the face whose normal is -Z. That convention has to stay
    /// that way: a hatch that called the ceiling a floor would colour the room upside down.
    /// </summary>
    private static SurfaceKind Classify(Face face)
    {
        try
        {
            var box = face.GetBoundingBox();
            var mid = new UV(
                (box.Min.U + box.Max.U) / 2.0,
                (box.Min.V + box.Max.V) / 2.0);

            var normal = face.ComputeNormal(mid);

            if (normal.Z > 0.7) return SurfaceKind.Ceiling;
            if (normal.Z < -0.7) return SurfaceKind.Floor;
            return SurfaceKind.Wall;
        }
        catch
        {
            return SurfaceKind.Wall;
        }
    }
}
