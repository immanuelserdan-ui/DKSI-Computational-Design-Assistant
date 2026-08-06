using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Finishes;

/// <summary>
/// Face harvesting and the thin-solid boolean clip that measures a host's real finish
/// face inside a room. Port of the geometry half of RoomFinishAreas.
/// </summary>
public sealed class FinishGeometry
{
    private readonly Document _doc;
    private readonly Dictionary<long, List<Face>> _faceCache = [];
    private readonly Dictionary<long, List<Solid>> _solidCache = [];

    public FinishGeometry(Document doc) => _doc = doc;

    // ------------------------------------------------------------------ faces

    /// <summary>
    /// EVERY face of the element's built geometry.
    ///
    /// Required instead of HostObjectUtils.GetSideFaces/GetTopFaces because those return
    /// one reference per side, COLLAPSING Split Face regions - each split region is its
    /// own Face carrying its own (painted) material, and only full-geometry harvesting
    /// preserves them. Downstream, the coplanarity filter in
    /// <see cref="ExactSubfaceArea"/> selects the room-facing faces, so harvesting
    /// everything is safe.
    ///
    /// Stacked walls: parent geometry can be empty, so member walls are harvested
    /// individually.
    /// </summary>
    public List<Face> ElementFaces(Element element)
    {
        var faces = new List<Face>();
        var parts = new List<Element?> { element };

        try
        {
            if (element is Wall { IsStackedWall: true } stacked)
                parts = [.. stacked.GetStackedWallMemberIds().Select(_doc.GetElement)];
        }
        catch
        {
            // Not a stacked wall, or the member ids are unavailable.
        }

        var options = new Options
        {
            IncludeNonVisibleObjects = false,
            DetailLevel = ViewDetailLevel.Fine,   // never coarsen split regions

            // REQUIRED for paint detection: Document.IsPainted resolves a face through
            // its Reference, which Revit only populates when ComputeReferences is true.
            // Without it every IsPainted call fails, every face silently reports as
            // unpainted, and the paint/layer split this engine exists to produce
            // collapses. Do not turn this off.
            ComputeReferences = true,
        };

        foreach (var part in parts)
        {
            if (part is null) continue;

            try
            {
                var geometry = part.get_Geometry(options);
                if (geometry is null) continue;

                foreach (var obj in geometry)
                {
                    // The Python reached for obj.Faces inside a try, so only Solids ever
                    // contributed; GeometryInstances raised and were skipped. Kept.
                    if (obj is Solid solid)
                        faces.AddRange(solid.Faces.Cast<Face>());
                }
            }
            catch
            {
                // A part whose geometry cannot be read contributes nothing.
            }
        }

        return faces;
    }

    /// <summary>
    /// <see cref="ElementFaces"/> re-extracts solid geometry on every call, so a wall
    /// hosting several doors (and already swept for its room face) is harvested once.
    /// </summary>
    public List<Face> CachedFaces(Element element)
    {
        var key = element.Id.Value;
        if (_faceCache.TryGetValue(key, out var cached)) return cached;

        var faces = ElementFaces(element);
        _faceCache[key] = faces;
        return faces;
    }

    public static (XYZ? Origin, XYZ? Normal) PlanarData(Face face)
    {
        try
        {
            if (face is PlanarFace planar) return (planar.Origin, planar.FaceNormal.Normalize());
        }
        catch
        {
            // Not planar, or the normal is degenerate.
        }

        return (null, null);
    }

    /// <summary>
    /// Planar faces pointing up (top finishes) or down (undersides), from full geometry
    /// so Split Face regions survive.
    /// </summary>
    private List<Face> DirectionalFaces(Element element, bool wantUp)
    {
        var result = new List<Face>();

        foreach (var face in ElementFaces(element))
        {
            var (origin, normal) = PlanarData(face);
            if (origin is null || normal is null) continue;

            if (wantUp && normal.Z > 0.5) result.Add(face);
            else if (!wantUp && normal.Z < -0.5) result.Add(face);
        }

        return result;
    }

    public List<Face> TopFaces(Element element) => DirectionalFaces(element, wantUp: true);

    public List<Face> BottomFaces(Element element) => DirectionalFaces(element, wantUp: false);

    // ------------------------------------------------------------- material key

    /// <summary>Material key for a face: the material plus whether it is painted.</summary>
    public MaterialKey FaceMaterialKey(Element? owner, Face face)
    {
        var painted = false;
        try
        {
            if (owner is not null && _doc.IsPainted(owner.Id, face)) painted = true;
        }
        catch
        {
            painted = false;
        }

        try
        {
            var id = face.MaterialElementId;
            if (id is null || id == ElementId.InvalidElementId)
                return MaterialKey.Named("(no material)", painted);

            return MaterialKey.Of(id, painted);
        }
        catch
        {
            return MaterialKey.Named("(no material)", painted);
        }
    }

    // ---------------------------------------------------------- the exact clip

    /// <summary>
    /// Net finish area within this room: intersect the room's boundary subface with the
    /// host's coplanar real face(s), whose geometry already excludes every cut. Each host
    /// face knows its material, so the result is keyed per material - a stacked or
    /// split-region wall (tiles below, paint above) yields separate entries per finish.
    ///
    /// Returns null when the method cannot apply, never 0. A coplanar face that matched
    /// but produced a (near-)empty intersection is a silent geometric failure - winding
    /// or orientation quirks can yield an empty boolean without throwing - NOT a real
    /// "zero finish area". Returning null lets the planar fallback engage and the report
    /// disclose it; a failed measurement must never masquerade as a confident zero.
    /// </summary>
    public (double Total, MaterialLedger Materials)? ExactSubfaceArea(
        Face roomFace, IReadOnlyList<Face> hostFaces, Element? owner)
    {
        var (roomOrigin, roomNormal) = PlanarData(roomFace);
        if (roomOrigin is null || roomNormal is null) return null;

        var total = 0.0;
        var materials = new MaterialLedger();
        var matched = false;

        foreach (var hostFace in hostFaces)
        {
            var (hostOrigin, hostNormal) = PlanarData(hostFace);
            if (hostOrigin is null || hostNormal is null) continue;

            // Same plane? Normals parallel or antiparallel, and zero offset.
            if (Math.Abs(Math.Abs(roomNormal.DotProduct(hostNormal)) - 1.0) > 0.01) continue;
            if (Math.Abs((hostOrigin - roomOrigin).DotProduct(roomNormal)) > FinishSettings.CoplanarTolerance)
                continue;

            matched = true;

            try
            {
                var roomSolid = GeometryCreationUtilities.CreateExtrusionGeometry(
                    roomFace.GetEdgesAsCurveLoops(), roomNormal, FinishSettings.ExtrudeThickness);

                var hostSolid = GeometryCreationUtilities.CreateExtrusionGeometry(
                    hostFace.GetEdgesAsCurveLoops(), roomNormal, FinishSettings.ExtrudeThickness);

                var intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                    roomSolid, hostSolid, BooleanOperationsType.Intersect);

                if (intersection is not null && intersection.Volume > 1e-9)
                {
                    var area = intersection.Volume / FinishSettings.ExtrudeThickness;
                    total += area;
                    materials.Add(FaceMaterialKey(owner, hostFace), area);
                }
            }
            catch
            {
                return null;   // any boolean failure -> fall back entirely
            }
        }

        if (!matched || total <= 1e-6) return null;

        return (total, materials);
    }

    // ------------------------------------------------------------------ solids

    /// <summary>
    /// The element's real solids in world coordinates, flattened through any nested
    /// GeometryInstances. Cached per element.
    ///
    /// Unlike <see cref="ElementFaces"/> this does NOT compute references, because its
    /// consumers do boolean work rather than paint lookups. Anything needing
    /// <see cref="Document.IsPainted"/> must go through the face harvester instead.
    /// </summary>
    public List<Solid> ElementSolids(Element element)
    {
        var key = element.Id.Value;
        if (_solidCache.TryGetValue(key, out var cached)) return cached;

        var solids = new List<Solid>();

        try
        {
            var options = new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine,
            };

            var geometry = element.get_Geometry(options);
            if (geometry is not null)
                foreach (var obj in geometry) CollectSolids(obj, solids);
        }
        catch
        {
            // No readable geometry; the caller falls back to a coarser measurement.
        }

        _solidCache[key] = solids;
        return solids;
    }

    /// <summary>
    /// Solids of a door instance - lining, frame, leaf, glass. A named alias for
    /// <see cref="ElementSolids"/>, kept because at the reveal-measuring call site the
    /// thing being harvested is the point.
    /// </summary>
    public List<Solid> DoorSolids(Element door) => ElementSolids(door);

    private static void CollectSolids(GeometryObject obj, List<Solid> output)
    {
        try
        {
            switch (obj)
            {
                case Solid solid when solid.Volume > 1e-9 && solid.Faces.Size > 0:
                    output.Add(solid);
                    break;

                case GeometryInstance instance:
                    foreach (var nested in instance.GetInstanceGeometry())
                        CollectSolids(nested, output);
                    break;
            }
        }
        catch
        {
            // Unreadable geometry object; skip it.
        }
    }

    /// <summary>A representative world point on a face, averaged from its edge vertices.</summary>
    public static XYZ? FaceCentroid(Face face)
    {
        try
        {
            var points = new List<XYZ>();
            foreach (CurveLoop loop in face.GetEdgesAsCurveLoops())
                foreach (var curve in loop)
                    points.Add(curve.GetEndPoint(0));

            if (points.Count == 0) return null;

            return new XYZ(
                points.Average(p => p.X),
                points.Average(p => p.Y),
                points.Average(p => p.Z));
        }
        catch
        {
            return null;
        }
    }

    public static bool PointInBoundingBox(XYZ? point, BoundingBoxXYZ? box, double pad)
    {
        if (point is null || box is null) return false;

        return point.X >= box.Min.X - pad && point.X <= box.Max.X + pad
            && point.Y >= box.Min.Y - pad && point.Y <= box.Max.Y + pad
            && point.Z >= box.Min.Z - pad && point.Z <= box.Max.Z + pad;
    }
}
