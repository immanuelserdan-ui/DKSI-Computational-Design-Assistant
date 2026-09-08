using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace Cda.Revit.Addin.Finishes;

/// <summary>One overhead element's measured contribution to a room's ceiling finish.</summary>
public sealed record CeilingFallbackHit(ElementId Element, double Area, MaterialLedger Materials);

/// <summary>What one tier of the priority chain contributed.</summary>
public sealed record CeilingFallbackTier(
    string Source, double Total, IReadOnlyList<CeilingFallbackHit> Hits);

/// <summary>Which tier(s) of the priority chain answered, and what they measured.</summary>
public sealed class CeilingFallbackResult
{
    public required IReadOnlyList<CeilingFallbackTier> Tiers { get; init; }

    public double Total => Tiers.Sum(t => t.Total);

    /// <summary>Tier names in priority order, e.g. "slab above + roof".</summary>
    public string Source => string.Join(" + ", Tiers.Select(t => t.Source));

    public IEnumerable<CeilingFallbackHit> Hits => Tiers.SelectMany(t => t.Hits);
}

/// <summary>
/// Ceiling finish area for rooms that have NO top boundary at all.
///
/// WHY THIS EXISTS
///   The room-boundary sweep in <see cref="RoomFinishCalculator"/> can only report a
///   ceiling that Revit itself used to clip the room volume. A model with no ceiling
///   elements modelled yet - the normal state of an early-stage or shell-and-core model -
///   therefore yields a ceiling area of exactly 0 for every room, and the finish takeoff
///   is silently missing a whole surface.
///
///   The office rule is that the ceiling surface of a room is whatever is actually
///   overhead, in priority order:
///
///     1. a Ceiling element
///     2. failing that, the underside of the Floor slab above
///     3. failing that, the underside of the Roof
///
///   Revit's own clipping already implements 1 > 2 > 3 for rooms that ARE bounded,
///   because it stops the volume at the nearest bounding element. This class covers the
///   case Revit cannot: nothing bounds the room, so nothing is reported.
///
/// HOW IT MEASURES
///   The room footprint is extruded upward into a prism spanning the search band, then
///   intersected with each candidate's real solid. The DOWNWARD-facing faces of that
///   intersection are the candidate's real underside, clipped to the room - so slab
///   openings, shafts, sloped roof planes and edited profiles are all already correct,
///   for the same reason the wall measurement is: the geometry is the answer, not a
///   parameter about the geometry.
///
///   A tier claims only the footprint it actually covers. Whatever it covered is cut out
///   of the search prism before the next tier runs, so the roof over a slab is never
///   double-counted, while a room half under a slab and half open to the roof gets both -
///   each over its own part. A tier that covers everything ends the chain by leaving no
///   prism for the next one.
/// </summary>
public sealed class CeilingFallbackResolver
{
    private readonly FinishGeometry _geometry;

    /// <summary>Candidates per tier, collected once for the whole pass.</summary>
    private readonly Dictionary<string, List<Element>> _candidates = [];

    public CeilingFallbackResolver(Document doc, FinishGeometry geometry)
    {
        _geometry = geometry;

        foreach (var (source, category) in FinishSettings.CeilingFallbackTiers)
        {
            _candidates[source] =
            [
                .. new FilteredElementCollector(doc)
                    .OfCategory(category)
                    .WhereElementIsNotElementType()
            ];
        }
    }

    /// <summary>How many elements exist in each tier. Drives the summary report.</summary>
    public int CountIn(string source) => _candidates.TryGetValue(source, out var list) ? list.Count : 0;

    /// <summary>
    /// Walks the priority chain, each tier measuring only the footprint left uncovered by
    /// the ones before it. Returns null when nothing at all sits over this room.
    /// </summary>
    /// <param name="roomBottomFaces">
    /// The room solid's Bottom subfaces. These span the WHOLE footprint including area open
    /// to below, which is exactly right here: a room open to below still has a ceiling over
    /// it. Reusing them rather than re-deriving boundary curve loops also inherits their
    /// correct hole orientation.
    /// </param>
    public CeilingFallbackResult? Resolve(Room room, IReadOnlyList<Face> roomBottomFaces)
    {
        if (roomBottomFaces.Count == 0) return null;

        var baseZ = double.MaxValue;
        foreach (var face in roomBottomFaces)
        {
            var (origin, _) = FinishGeometry.PlanarData(face);
            if (origin is not null && origin.Z < baseZ) baseZ = origin.Z;
        }

        if (baseZ == double.MaxValue) return null;   // no planar bottom face to work from

        var footprint = BuildFootprintPrisms(roomBottomFaces);
        if (footprint.Count == 0) return null;

        BoundingBoxXYZ? roomBox;
        try { roomBox = room.get_BoundingBox(null); }
        catch { roomBox = null; }

        var remaining = footprint;
        var tiers = new List<CeilingFallbackTier>();

        foreach (var (source, _) in FinishSettings.CeilingFallbackTiers)
        {
            if (remaining.Count == 0) break;   // fully covered by a higher-priority tier

            // THE STAIR ACCESS RULE. A stair this room can walk onto is its stair, not its
            // ceiling, so it is struck out of the candidate list for THIS room only - the same
            // stair remains a perfectly good ceiling source for the store room a storey below.
            var pool = source == FinishSettings.SourceStair
                ? _candidates[source].Where(s => !HasAccessTo(s, baseZ)).ToList()
                : _candidates[source];

            var (hits, covered) = Measure(pool, remaining, roomBox, baseZ);
            var total = hits.Sum(h => h.Area);

            // Below the threshold the tier is treated as having found nothing at all, so its
            // sliver is neither counted nor cut out of the prism the next tier searches.
            if (total <= FinishSettings.CeilingFallbackMinimum) continue;

            tiers.Add(new CeilingFallbackTier(source, total, hits));
            remaining = Remainder(remaining, covered);
        }

        return tiers.Count == 0 ? null : new CeilingFallbackResult { Tiers = tiers };
    }

    // --------------------------------------------------------------- stair access

    /// <summary>
    /// Whether a room whose floor is at <paramref name="roomFloorZ"/> can walk onto this stair.
    ///
    /// True when the stair STARTS at that floor - its bottom tread is within a step of it, so
    /// you step on and go up - or FINISHES at it, arriving from the storey below. Either way
    /// the flight belongs to this room and its underside is not this room's ceiling.
    ///
    /// ANSWERED FROM THE GEOMETRY, NOT FROM A ROOM API. Room.IsPointInRoom and GetRoomAtPoint
    /// give opposite answers on this model - see the note in RadiatorGenerator.SeatOnRoomSide,
    /// where one of them refused 37 of 51 windows that were correctly placed. "Does this flight
    /// meet this floor" is a question about two elevations, and both are already in hand.
    ///
    /// NO PLAN TEST, deliberately. A stair on the far side of the building starting at the same
    /// elevation is struck out of this room's candidates too, and that costs nothing: it does
    /// not overlap the room's footprint, so it could never have been measured for this room
    /// anyway. Adding a plan test would buy no accuracy and one more thing to be wrong.
    /// </summary>
    private static bool HasAccessTo(Element stair, double roomFloorZ)
    {
        BoundingBoxXYZ? box;
        try { box = stair.get_BoundingBox(null); }
        catch { return false; }

        if (box is null) return false;

        var band = FinishSettings.StairAccessBand;

        var startsHere = Math.Abs(box.Min.Z - roomFloorZ) <= band;
        var arrivesHere = Math.Abs(box.Max.Z - roomFloorZ) <= band;

        return startsHere || arrivesHere;
    }

    // ------------------------------------------------------------------ footprint

    /// <summary>
    /// A vertical prism per bottom face, spanning the search band above the room. Built by
    /// lifting the face's own curve loops and extruding back DOWN along -Z, which keeps the
    /// loop-orientation-versus-direction convention that already works in
    /// <see cref="FinishGeometry.ExactSubfaceArea"/>: a bottom face's loops are wound for a
    /// downward extrusion.
    /// </summary>
    private static List<Solid> BuildFootprintPrisms(IReadOnlyList<Face> bottomFaces)
    {
        var prisms = new List<Solid>();
        var lift = Transform.CreateTranslation(new XYZ(0, 0, FinishSettings.ScanBand));

        foreach (var face in bottomFaces)
        {
            try
            {
                var loops = face.GetEdgesAsCurveLoops()
                    .Select(loop => CurveLoop.CreateViaTransform(loop, lift))
                    .ToList();

                if (loops.Count == 0) continue;

                var prism = GeometryCreationUtilities.CreateExtrusionGeometry(
                    loops, XYZ.BasisZ.Negate(), FinishSettings.ScanBand);

                if (prism is not null && prism.Volume > 1e-9) prisms.Add(prism);
            }
            catch
            {
                // A footprint face that cannot be extruded contributes nothing; the other
                // faces of a stepped room still do.
            }
        }

        return prisms;
    }

    /// <summary>
    /// The search prism with everything a tier already covered cut out of it.
    ///
    /// The subtracted shape is the covered face swept vertically THROUGH the whole band,
    /// not the slab itself. Subtracting a 200 mm slab would only punch a 200 mm slice out
    /// of the prism and leave the air above it, so the roof over that slab would still be
    /// found through the hole - which is the exact double count this prevents.
    /// </summary>
    private static List<Solid> Remainder(IReadOnlyList<Solid> prisms, IReadOnlyList<Face> covered)
    {
        if (covered.Count == 0) return [.. prisms];

        var shadows = new List<Solid>();
        var lift = Transform.CreateTranslation(new XYZ(0, 0, FinishSettings.ScanBand));

        foreach (var face in covered)
        {
            try
            {
                var loops = face.GetEdgesAsCurveLoops()
                    .Select(loop => CurveLoop.CreateViaTransform(loop, lift))
                    .ToList();

                if (loops.Count == 0) continue;

                // Twice the band, starting a band high: whatever the covered face's own
                // elevation, the shadow spans the prism completely in Z.
                var shadow = GeometryCreationUtilities.CreateExtrusionGeometry(
                    loops, XYZ.BasisZ.Negate(), FinishSettings.ScanBand * 2.0);

                if (shadow is not null && shadow.Volume > 1e-9) shadows.Add(shadow);
            }
            catch
            {
                // An unextrudable covered face cannot be cut out. The next tier then sees
                // that strip as uncovered, which over-reports rather than silently losing
                // area - the failure that gets noticed instead of the one that does not.
            }
        }

        if (shadows.Count == 0) return [.. prisms];

        var result = new List<Solid>();

        foreach (var prism in prisms)
        {
            Solid? current = prism;

            foreach (var shadow in shadows)
            {
                if (current is null) break;

                try
                {
                    current = BooleanOperationsUtils.ExecuteBooleanOperation(
                        current, shadow, BooleanOperationsType.Difference);
                }
                catch
                {
                    // Keep what survived so far rather than dropping the prism entirely.
                }
            }

            if (current is not null && current.Volume > 1e-9) result.Add(current);
        }

        return result;
    }

    // -------------------------------------------------------------------- measure

    /// <summary>
    /// Every candidate's underside within the prism, plus the clipped faces themselves so
    /// the caller can cut what was covered out of the search for the next tier.
    /// </summary>
    private (List<CeilingFallbackHit> Hits, List<Face> Covered) Measure(
        IReadOnlyList<Element> candidates, IReadOnlyList<Solid> footprint,
        BoundingBoxXYZ? roomBox, double baseZ)
    {
        var hits = new List<CeilingFallbackHit>();
        var covered = new List<Face>();

        foreach (var candidate in candidates)
        {
            try
            {
                BoundingBoxXYZ? box;
                try { box = candidate.get_BoundingBox(null); }
                catch { continue; }

                if (box is null) continue;

                // Clearly ABOVE this room's floor, and within the same band the upper-limit
                // fix scans. The 1 ft skirt is what stops a room's own floor slab from being
                // counted as its own ceiling, and the band stops a storey three levels up
                // from being dragged in.
                if (box.Min.Z < baseZ + FinishSettings.CeilingFallbackFloorSkirt ||
                    box.Min.Z > baseZ + FinishSettings.ScanBand) continue;

                // Cheap plan rejection before the expensive boolean.
                if (roomBox is not null &&
                    (box.Max.X < roomBox.Min.X || box.Min.X > roomBox.Max.X ||
                     box.Max.Y < roomBox.Min.Y || box.Min.Y > roomBox.Max.Y)) continue;

                // Harvested from the ELEMENT, with references intact, so paint can still be
                // resolved. The boolean result's faces have no usable Reference.
                var reference = _geometry.BottomFaces(candidate);

                var ledger = new MaterialLedger();
                var area = 0.0;

                foreach (var solid in _geometry.ElementSolids(candidate))
                {
                    foreach (var prism in footprint)
                    {
                        Solid? clipped;
                        try
                        {
                            clipped = BooleanOperationsUtils.ExecuteBooleanOperation(
                                solid, prism, BooleanOperationsType.Intersect);
                        }
                        catch
                        {
                            continue;   // one failed clip must not lose the element
                        }

                        if (clipped is null || clipped.Volume <= 1e-9) continue;

                        foreach (Face face in clipped.Faces)
                        {
                            var (_, normal) = FinishGeometry.PlanarData(face);

                            // Undersides only. The prism's own walls are vertical and its
                            // caps point the other way, so this single test isolates the
                            // candidate's real soffit - flat or sloped, its true surface
                            // area rather than a plan projection.
                            if (normal is null || normal.Z > -0.5) continue;

                            var faceArea = face.Area;
                            if (faceArea <= 1e-6) continue;

                            area += faceArea;
                            covered.Add(face);
                            ledger.Add(MatchMaterial(candidate, face, reference), faceArea);
                        }
                    }
                }

                if (area > 1e-6) hits.Add(new CeilingFallbackHit(candidate.Id, area, ledger));
            }
            catch
            {
                // One candidate failing must not stop the tier.
            }
        }

        return (hits, covered);
    }

    /// <summary>
    /// The material of a boolean-result face, recovered from the element's own faces.
    ///
    /// A boolean result carries no usable Reference, so <see cref="Document.IsPainted"/>
    /// cannot be asked about it directly and the paint flag would be lost. Matching the
    /// result face back to the original coplanar face restores both the material and the
    /// paint state. Where several original faces share the plane - a Split Face soffit -
    /// the result face's centroid picks between them.
    /// </summary>
    private MaterialKey MatchMaterial(Element owner, Face clipped, IReadOnlyList<Face> reference)
    {
        var (origin, normal) = FinishGeometry.PlanarData(clipped);

        if (origin is not null && normal is not null)
        {
            var coplanar = new List<Face>();

            foreach (var face in reference)
            {
                var (hostOrigin, hostNormal) = FinishGeometry.PlanarData(face);
                if (hostOrigin is null || hostNormal is null) continue;

                if (Math.Abs(Math.Abs(hostNormal.DotProduct(normal)) - 1.0) > 0.01) continue;
                if (Math.Abs((hostOrigin - origin).DotProduct(normal)) > FinishSettings.CoplanarTolerance)
                    continue;

                coplanar.Add(face);
            }

            if (coplanar.Count == 1) return _geometry.FaceMaterialKey(owner, coplanar[0]);

            if (coplanar.Count > 1)
            {
                var centroid = FinishGeometry.FaceCentroid(clipped);

                if (centroid is not null)
                {
                    foreach (var face in coplanar)
                    {
                        try
                        {
                            if (face.Project(centroid) is not null)
                                return _geometry.FaceMaterialKey(owner, face);
                        }
                        catch
                        {
                            // Unprojectable face; try the next split region.
                        }
                    }
                }

                return _geometry.FaceMaterialKey(owner, coplanar[0]);
            }
        }

        // No coplanar original (a curved or re-cut soffit). The material id usually survives
        // the boolean even though the reference does not, so it is worth asking - but paint
        // cannot be confirmed, so this key is never marked painted.
        try
        {
            var id = clipped.MaterialElementId;
            if (id is not null && id != ElementId.InvalidElementId) return MaterialKey.Of(id, false);
        }
        catch
        {
            // Fall through to the anonymous bucket.
        }

        return MaterialKey.Fallback;
    }
}
