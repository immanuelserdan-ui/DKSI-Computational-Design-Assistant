using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Cda.Revit.Addin.Finishes;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Qa;

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

    public double AreaOf(SurfaceKind kind) =>
        Regions.Where(r => r.Kind == kind).Sum(r => r.Area);

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

                var before = result.Regions.Count;

                Intersect(subfaceGeometry, owner, kind, result, ref booleanFailures);

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
        Face roomFace, Element owner, SurfaceKind kind, PaintExtractResult result, ref int failures)
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
                    continue;
                }

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
            }
        }
    }

    /// <summary>
    /// Which paint parameter a room face feeds, from its normal.
    ///
    /// The room solid's normals point OUT of the room, so its underside points down and its
    /// top points up - the floor is the face whose normal is -Z. Same convention as
    /// <see cref="RoomFinishInspector"/>, and it has to stay that way: a hatch that called
    /// the ceiling a floor would colour the room upside down.
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
