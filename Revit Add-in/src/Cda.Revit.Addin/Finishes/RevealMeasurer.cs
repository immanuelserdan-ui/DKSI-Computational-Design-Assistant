using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Finishes;

/// <summary>
/// Painted door reveals - the wall-thickness jamb and head returns inside an opening.
///
/// The room-boundary sweep only matches wall faces COPLANAR with the room plane, so these
/// returns are never seen: their normals are roughly perpendicular to the room face. They
/// are real painted surfaces that Revit's native takeoff counts. This pass measures them
/// off the host wall and attributes each opening's reveal paint to ONE room, so the
/// model-wide total matches Revit without double counting.
/// </summary>
public sealed class RevealMeasurer
{
    private readonly Document _doc;
    private readonly FinishGeometry _geometry;
    private readonly FinishSettings _settings;

    public RevealMeasurer(Document doc, FinishGeometry geometry, FinishSettings settings)
    {
        _doc = doc;
        _geometry = geometry;
        _settings = settings;
    }

    /// <summary>
    /// Painted host-wall faces bounding this door's opening.
    ///
    /// A reveal face is (a) NOT parallel to the wall's room faces - its normal is roughly
    /// perpendicular to Wall.Orientation, so it is neither of the two big room planes;
    /// (b) painted; and (c) inside the door's world bounding box. Only painted faces are
    /// collected - this pass exists to capture paint, and Wall.Orientation cleanly
    /// separates returns from the room faces the main sweep already measured, so there is
    /// no double counting.
    /// </summary>
    public MaterialLedger Measure(Wall hostWall, Element door)
    {
        var result = new MaterialLedger();

        XYZ orientation;
        try { orientation = hostWall.Orientation.Normalize(); }
        catch { return result; }

        var box = door.get_BoundingBox(null);
        if (box is null) return result;

        var doorCentre = new XYZ(
            (box.Min.X + box.Max.X) / 2.0,
            (box.Min.Y + box.Max.Y) / 2.0,
            (box.Min.Z + box.Max.Z) / 2.0);

        var solids = _settings.DeductFrameCoverage ? _geometry.DoorSolids(door) : [];

        foreach (var face in _geometry.CachedFaces(hostWall))
        {
            var (origin, normal) = FinishGeometry.PlanarData(face);
            if (origin is null || normal is null) continue;

            // Skip the two room-facing planes - the main sweep's territory.
            if (Math.Abs(normal.DotProduct(orientation)) > 0.5) continue;

            try
            {
                if (!_doc.IsPainted(hostWall.Id, face)) continue;
            }
            catch
            {
                continue;
            }

            var centroid = FinishGeometry.FaceCentroid(face);
            if (!FinishGeometry.PointInBoundingBox(centroid, box, FinishSettings.RevealPad)) continue;

            var area = _settings.DeductFrameCoverage && solids.Count > 0 && centroid is not null
                ? VisibleRevealArea(face, normal, centroid, doorCentre, solids)
                : face.Area;

            if (area <= 0.005) continue;

            result.Add(_geometry.FaceMaterialKey(hostWall, face), area);
        }

        return result;
    }

    /// <summary>
    /// Area of a reveal face NOT covered by the door's own geometry.
    ///
    /// Extrude the face into the opening as a thin slab (toward the door centre), then
    /// subtract the door solids: the lining/frame sitting against the reveal carves out
    /// its covered footprint, leaving the visible painted return. Any boolean failure
    /// returns the full face area, so this can never over-deduct.
    /// </summary>
    private static double VisibleRevealArea(
        Face face, XYZ normal, XYZ centroid, XYZ doorCentre, IReadOnlyList<Solid> solids)
    {
        var full = face.Area;
        if (solids.Count == 0) return full;

        Solid slab;
        try
        {
            // Extrude toward the door interior so the slab overlaps the lining that sits
            // against the reveal.
            var sign = (doorCentre - centroid).DotProduct(normal) < 0 ? -1.0 : 1.0;

            slab = GeometryCreationUtilities.CreateExtrusionGeometry(
                face.GetEdgesAsCurveLoops(), normal, FinishSettings.RevealSlabThickness * sign);
        }
        catch
        {
            return full;
        }

        foreach (var solid in solids)
        {
            try
            {
                var difference = BooleanOperationsUtils.ExecuteBooleanOperation(
                    slab, solid, BooleanOperationsType.Difference);

                if (difference is not null) slab = difference;
            }
            catch
            {
                // This solid could not be subtracted; keep what we have.
            }
        }

        double visible;
        try { visible = slab.Volume / FinishSettings.RevealSlabThickness; }
        catch { return full; }

        // Clamp: booleans can leave slivers.
        return visible < 0 ? 0.0 : Math.Min(visible, full);
    }
}
