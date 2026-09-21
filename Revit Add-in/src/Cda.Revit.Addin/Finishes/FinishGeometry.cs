using Autodesk.Revit.DB;
using Cda.Revit.Addin.Infrastructure;

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
    public (double Total, MaterialLedger Materials, double OccludedArea, int OcclusionBooleanFailures)? ExactSubfaceArea(
        Face roomFace, IReadOnlyList<Face> hostFaces, Element? owner, IReadOnlyList<Element>? occluders = null)
    {
        var (roomOrigin, roomNormal) = PlanarData(roomFace);
        if (roomOrigin is null || roomNormal is null) return null;

        var total = 0.0;
        var materials = new MaterialLedger();
        var matched = false;
        var occludedArea = 0.0;
        var occlusionFailures = 0;

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

                // CAPTURED BEFORE OCCLUSION RUNS, and this distinction is load-bearing. A
                // near-zero or null intersection HERE is the pre-existing "boolean produced
                // nothing usable" case that RegionArea below already exists to recover from -
                // occlusion must never be allowed to look like the cause of that, or a room
                // with no occluders anywhere near it would silently lose the RegionArea
                // fallback it always had. Only once THIS is true can a later zero be trusted
                // as "genuinely occluded" rather than "boolean failed".
                var realIntersection = intersection is not null && intersection.Volume > 1e-9;

                // OCCLUSION: a mezzanine slab or hanging wall standing directly against this
                // face means part of it is not really there to paint - see the doc comment
                // on RoomFinishCalculator.OccludingElements for why this exists at all.
                //
                // Subtracted from the REAL 3D intersection, before it is ever converted to an
                // area - not as after-the-fact arithmetic - so the material ledger below is
                // built from geometry that already excludes the occluded part. A face with
                // two paint colours where an occluder only covers one of them keeps the other
                // colour's full area; subtracting square metres from a total could not do
                // that.
                //
                // A FAILED SUBTRACTION LEAVES THE FACE UNCHANGED, NOT ZEROED. The candidate
                // list is already filtered to occluders whose bounding box overlaps the host
                // (see OccludingElements), so most calls here have nothing to subtract and
                // this loop costs one empty check. When a boolean genuinely fails, the choice
                // matches every other boolean in this file: a failed measurement must never
                // masquerade as a confident zero, so the host keeps its un-occluded area
                // rather than losing it to a geometry edge case.
                if (realIntersection && occluders is { Count: > 0 })
                {
                    // A NON-NULLABLE LOCAL, DELIBERATELY, rather than reassigning the outer
                    // `intersection` (Solid?) through the loop. realIntersection already
                    // proved it non-null at this point; carrying that proof in the type
                    // itself is what lets every read below skip a null check it cannot fail,
                    // instead of asking the compiler to trust a loop-and-catch shape it
                    // cannot follow.
                    var current = intersection!;

                    foreach (var occluder in occluders)
                    {
                        List<Solid> solids;
                        try { solids = ElementSolids(occluder); }
                        catch { continue; }

                        foreach (var solid in solids)
                        {
                            if (solid.Volume <= 1e-9) continue;

                            try
                            {
                                var before = current.Volume;
                                var reduced = BooleanOperationsUtils.ExecuteBooleanOperation(
                                    current, solid, BooleanOperationsType.Difference);

                                if (reduced is null)
                                {
                                    occlusionFailures++;
                                    continue;
                                }

                                occludedArea += (before - reduced.Volume) / FinishSettings.ExtrudeThickness;
                                current = reduced;
                            }
                            catch
                            {
                                occlusionFailures++;
                            }

                            if (current.Volume <= 1e-9) break;
                        }

                        if (current.Volume <= 1e-9) break;
                    }

                    intersection = current;
                }

                if (intersection is not null && intersection.Volume > 1e-9)
                {
                    var area = intersection.Volume / FinishSettings.ExtrudeThickness;
                    total += area;
                    materials.Add(FaceMaterialKey(owner, hostFace), area);
                }
                else if (realIntersection)
                {
                    // realIntersection can only be true here if occlusion ran (see the guard
                    // above) and reduced a genuinely non-empty intersection down to nothing -
                    // the host face is fully occluded, which is a real, deliberate zero rather
                    // than a failed measurement. Does NOT fall through to RegionArea: that
                    // recovery exists for a boolean that never produced usable geometry in the
                    // first place, which this is the opposite of.
                }
                else if (RegionArea(roomFace, hostFace) is { } direct)
                {
                    total += direct;
                    materials.Add(FaceMaterialKey(owner, hostFace), direct);
                }
                else if (ClippedToRoomZRange(roomFace, hostFace, roomNormal) is { } bandDirect)
                {
                    total += bandDirect;
                    materials.Add(FaceMaterialKey(owner, hostFace), bandDirect);
                }
                else
                {
                    materials.Drop();
                }
            }
            catch
            {
                // ONE REGION, NOT THE WALL. This used to `return null`, which sent the whole
                // element to the arithmetic fallback where MaterialKey.Fallback is unpainted
                // BY DEFINITION - so a wall carrying four paint colours reported none of
                // them, as a confident zero. A split face presents one coplanar face PER
                // REGION, so the number of booleans scales with the number of colours: the
                // more split-face work a wall carried, the likelier it was to lose all of it.
                //
                // MeasureInteriorWalls cannot hit this at all - it takes face.Area directly
                // and runs no boolean - which is the whole reason hanging walls separate
                // their colours and regular walls did not.
                if (RegionArea(roomFace, hostFace) is { } recovered)
                {
                    total += recovered;
                    materials.Add(FaceMaterialKey(owner, hostFace), recovered);
                }
                else if (ClippedToRoomZRange(roomFace, hostFace, roomNormal) is { } bandRecovered)
                {
                    total += bandRecovered;
                    materials.Add(FaceMaterialKey(owner, hostFace), bandRecovered);
                }
                else
                {
                    materials.Drop();
                }
            }
        }

        if (!matched) return null;

        // total <= 1e-6 ALONE is no longer enough to mean "measurement failed, let the
        // caller fall back". Every caller's fallback is arithmetic that knows nothing about
        // occlusion - it would simply re-add the full, un-occluded area, undoing the
        // subtraction above. occludedArea > 0 is proof this face's geometry WAS found and
        // measured, and a genuine occluder is why the total is small or zero - a confident,
        // deliberate answer, not a failure. Only a face that never produced real geometry at
        // all (occludedArea still zero) falls through to null, exactly as before.
        if (total <= 1e-6 && occludedArea <= 1e-6) return null;

        return (total, materials, occludedArea, occlusionFailures);
    }

    /// <summary>
    /// A region's area taken the way <see cref="RoomFinishCalculator"/>'s interior-wall pass
    /// takes it - <c>face.Area</c>, straight off the face - for use when the boolean clip
    /// declines to measure it.
    ///
    /// WHY THIS IS THE HANGING-WALL PATTERN
    ///   MeasureInteriorWalls never runs a boolean. It reads face.Area per face, and that is
    ///   exactly why a bulkhead separates its paint colours reliably: there is no step that
    ///   can fail and take the other regions with it. The clip exists on the bounding path
    ///   for one reason only - a wall face can be shared between rooms, and the room's
    ///   subface is what decides how much of it belongs HERE. Where a region does not
    ///   straddle that boundary, the clip and face.Area agree, and face.Area cannot fail.
    ///
    /// THE GUARD IS THE WHOLE POINT, AND IT IS DELIBERATELY CONSERVATIVE.
    ///   Every edge vertex of the region must project INSIDE the room's subface. A region
    ///   with one vertex outside might be shared with the next room, and returning its full
    ///   area would bill this room for paint the neighbour also gets - double counting, which
    ///   is worse than the under-count it would be fixing. Such a region is declined and
    ///   counted in <see cref="MaterialLedger.DroppedRegions"/> instead, so the shortfall is
    ///   named rather than silently invented.
    ///
    ///   A centroid test would be cheaper and wrong: a region straddling the boundary has its
    ///   centroid inside about half the time.
    /// </summary>
    /// <returns>The region's own area, or null when it cannot be shown to lie wholly inside.</returns>
    private static double? RegionArea(Face roomFace, Face region)
    {
        try
        {
            var inspected = 0;

            foreach (CurveLoop loop in region.GetEdgesAsCurveLoops())
            {
                foreach (var curve in loop)
                {
                    // Both endpoints: a loop's start points alone miss a vertex on a
                    // single-curve loop, and it costs nothing to be exact here.
                    for (var end = 0; end <= 1; end++)
                    {
                        var point = curve.GetEndPoint(end);

                        var hit = roomFace.Project(point);
                        if (hit is null) return null;

                        if (!roomFace.IsInside(hit.UVPoint)) return null;

                        inspected++;
                    }
                }
            }

            // No vertices means nothing was actually verified; an unbounded or degenerate
            // face must not pass a containment test by saying nothing.
            if (inspected == 0) return null;

            var area = region.Area;
            return area > 1e-9 ? area : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// RULE 1, applied where RegionArea structurally cannot be: the part of an ordinary
    /// wall's face that overlaps the room's real boundary extent on this plane, measured as
    /// a genuine PARTIAL overlap rather than an all-or-nothing containment test.
    ///
    /// WHY RegionArea DECLINES EVERY CALL THIS RECOVERS
    ///   RegionArea's own guard is "every vertex of the HOST region must already project
    ///   inside the room's subface" - built for a small split-face region that genuinely
    ///   might straddle a room boundary, where returning its whole area on a false positive
    ///   would double-bill a neighbour. That guard is correct for that case and wrong for
    ///   this one: an ORDINARY, un-split wall's own face spans the wall's FULL height, while
    ///   the room subface being measured against it is a THIN band - a slab's own thickness,
    ///   commonly under 200 mm - so virtually every vertex of the wall's face sits above or
    ///   below the band and RegionArea declines on the first one it checks, every time. The
    ///   wall is not shared with a neighbouring room in that band; it simply extends past it
    ///   on both sides, exactly the case RULE 1 requires a partial measurement for: any part
    ///   of a painted face inside the room's boundary volume counts, any part outside it does
    ///   not, and "outside" here is the wall's OWN extent beyond the band - not another room.
    ///
    /// WHY A NEW CLIP SOLID RATHER THAN RETRYING THE SAME TWO LOOPS
    ///   The primary attempt above already intersected roomFace's own loop against hostFace's
    ///   own loop and failed - retrying identical inputs fails identically. A band whose
    ///   height is a single slab's thickness, run the wall's full length, with its top or
    ///   bottom edge sitting exactly on a real seam (the slab's own soffit or top plane - the
    ///   very thing that BOUNDS the band), is close to the shape most likely to defeat
    ///   Revit's boolean solver on coincident-edge geometry.
    ///
    ///   So this builds a SIMPLE, always-well-formed clip solid instead: a four-straight-line
    ///   rectangle spanning the room subface's OWN already-established Z-range - read
    ///   directly off its geometry, not re-derived from a slab or opening lookup, so this
    ///   stays correct for whatever put that boundary there - padded generously in plan off
    ///   the HOST face's own extent so only Z ever does the clipping. This is the exact
    ///   technique RoomFinishCalculator.ClippedToRoomHeight already uses in production, for
    ///   the same reason: a clean box succeeds where two independently-derived loops do not.
    ///
    /// VERTICAL WALLS ONLY, ON PURPOSE. A flat Z-range box is the right clip for an ordinary
    /// wall's horizontal normal; it is the WRONG clip for a sloped or raking face - a stair
    /// soffit, say - where the same box would silently include or exclude area that does not
    /// track the real boundary. Declining there is correct: Rule 1 forbids inventing area as
    /// firmly as it forbids losing it, and a technique whose assumptions do not hold must
    /// decline rather than guess.
    ///
    /// CANNOT MAKE A WORKING MEASUREMENT WORSE. This only ever runs after BOTH the primary
    /// boolean and RegionArea have already declined - a face measured successfully, or
    /// already recovered by RegionArea, never reaches this. It can only recover area that
    /// was previously falling to the unpainted arithmetic bucket; it cannot reduce or corrupt
    /// an already-successful measurement.
    /// </summary>
    private static double? ClippedToRoomZRange(Face roomFace, Face hostFace, XYZ normal)
    {
        if (Math.Abs(normal.Z) > 0.1) return null;

        var roomBounds = FaceBounds(roomFace);
        if (roomBounds is not { } rb) return null;

        var clipHeight = rb.Max.Z - rb.Min.Z;
        if (clipHeight <= FinishSettings.CoplanarTolerance) return null;

        var hostBounds = FaceBounds(hostFace);
        if (hostBounds is not { } hb) return null;

        var padding = Measure.FromMillimetres(500.0);
        var z0 = rb.Min.Z;

        try
        {
            CurveLoop clipLoop = new();
            XYZ[] corners =
            [
                new XYZ(hb.Min.X - padding, hb.Min.Y - padding, z0),
                new XYZ(hb.Max.X + padding, hb.Min.Y - padding, z0),
                new XYZ(hb.Max.X + padding, hb.Max.Y + padding, z0),
                new XYZ(hb.Min.X - padding, hb.Max.Y + padding, z0),
            ];

            for (var i = 0; i < corners.Length; i++)
                clipLoop.Append(Line.CreateBound(corners[i], corners[(i + 1) % corners.Length]));

            var hostSolid = GeometryCreationUtilities.CreateExtrusionGeometry(
                hostFace.GetEdgesAsCurveLoops(), normal, FinishSettings.ExtrudeThickness);

            var clipSolid = GeometryCreationUtilities.CreateExtrusionGeometry(
                [clipLoop], XYZ.BasisZ, clipHeight);

            var clipped = BooleanOperationsUtils.ExecuteBooleanOperation(
                hostSolid, clipSolid, BooleanOperationsType.Intersect);

            if (clipped is null || clipped.Volume <= 1e-9) return null;

            var area = clipped.Volume / FinishSettings.ExtrudeThickness;
            return area > 1e-9 ? area : null;
        }
        catch
        {
            // A failed clip must fall through to the caller's own materials.Drop(), the
            // same honest "could not measure" as every other boolean in this file - never
            // a confident zero standing in for an area that was never actually computed.
            return null;
        }
    }

    /// <summary>
    /// The portion of <paramref name="face"/>'s area that actually sits inside
    /// <paramref name="roomSolid"/> - the room's own real, already-clipped volume from
    /// <see cref="SpatialElementGeometryCalculator"/> - rather than spilling past it in ANY
    /// direction, plan or height.
    ///
    /// USED FOR NON-ROOM-BOUNDING ELEMENTS (mezzanine slabs, freestanding walls), whose faces
    /// reach this method through a coarse bounding-box / point-in-room pre-filter upstream
    /// that only proves the element is SOMEWHERE near the room - never that the whole face
    /// lies inside it. Without this clip, an element that only partly overlaps the room - a
    /// slab or partition continuing past the room's real boundary into a stairwell, a
    /// neighbouring room, or a non-bounding dormer/monitor whose footprint only grazes this
    /// one - has its FULL face area billed to this one room. That is the "painted area
    /// outside the room boundary" leak.
    ///
    /// STRADDLES THE FACE SYMMETRICALLY (+/- thickness/2 along its own normal), the same
    /// technique <see cref="ExactSubfaceArea"/>'s own boolean uses, so the clip is correct
    /// regardless of which way the face happens to point.
    ///
    /// CHEAP REJECT BEFORE THE BOOLEAN, mirroring <see cref="ClippedToRoomZRange"/>: Revit's
    /// boolean engine can throw on a pair of solids that do not overlap at all, which the
    /// catch below would otherwise answer the wrong way - a face nowhere near the room
    /// falling back to its own full area instead of zero. A plain bounding-box test catches
    /// that case cheaply and correctly before any geometry is touched.
    ///
    /// FALLS BACK TO face.Area when <paramref name="roomBox"/> is unavailable, or when the
    /// boolean ITSELF fails (an exception) - never when it cleanly returns nothing. An
    /// unclipped area is the number this method replaces, so a genuine failure here is never
    /// worse than before this fix existed - the same discipline <see cref="ClippedToRoomZRange"/>
    /// already applies.
    /// </summary>
    public double ClipFaceToRoomSolid(
        Face face, Solid roomSolid, BoundingBoxXYZ? roomBox, double thickness = FinishSettings.ExtrudeThickness)
    {
        if (roomBox is null) return face.Area;

        var (origin, normal) = PlanarData(face);
        if (origin is null || normal is null) return face.Area;

        var faceBounds = FaceBounds(face);
        if (faceBounds is { } fb &&
            (fb.Max.X < roomBox.Min.X || fb.Min.X > roomBox.Max.X ||
             fb.Max.Y < roomBox.Min.Y || fb.Min.Y > roomBox.Max.Y ||
             fb.Max.Z < roomBox.Min.Z || fb.Min.Z > roomBox.Max.Z))
        {
            return 0.0;   // no overlap at all - cheap, certain, and safe before any boolean
        }

        try
        {
            var loops = face.GetEdgesAsCurveLoops();
            var slabPos = GeometryCreationUtilities.CreateExtrusionGeometry(loops, normal, thickness / 2.0);
            var slabNeg = GeometryCreationUtilities.CreateExtrusionGeometry(loops, normal, -thickness / 2.0);
            var slab = BooleanOperationsUtils.ExecuteBooleanOperation(slabPos, slabNeg, BooleanOperationsType.Union);
            if (slab is null) return 0.0;

            var intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                slab, roomSolid, BooleanOperationsType.Intersect);
            if (intersection is null || intersection.Volume <= 1e-9) return 0.0;

            var area = intersection.Volume / thickness;
            return area < face.Area ? area : face.Area;
        }
        catch
        {
            return face.Area;
        }
    }

    /// <summary>
    /// World-space min/max corner of a face, via triangulation - Face.GetBoundingBox is in
    /// UV parameter space and says nothing about world coordinates, the same reason the Room
    /// Subface Dump diagnostic reads a face's Z-range this way rather than trusting it.
    /// </summary>
    private static (XYZ Min, XYZ Max)? FaceBounds(Face face)
    {
        XYZ? min = null;
        XYZ? max = null;

        try
        {
            var mesh = face.Triangulate();
            if (mesh is null) return null;

            for (var i = 0; i < mesh.Vertices.Count; i++)
            {
                var v = mesh.Vertices[i];

                min = min is null
                    ? v
                    : new XYZ(Math.Min(min.X, v.X), Math.Min(min.Y, v.Y), Math.Min(min.Z, v.Z));

                max = max is null
                    ? v
                    : new XYZ(Math.Max(max.X, v.X), Math.Max(max.Y, v.Y), Math.Max(max.Z, v.Z));
            }
        }
        catch
        {
            return null;
        }

        return min is not null && max is not null ? (min, max) : null;
    }

    /// <summary>
    /// Does this element carry PAINT on a face that fronts the room, when
    /// <see cref="ExactSubfaceArea"/> has already declined to measure it?
    ///
    /// WHY A SEPARATE, CHEAPER TEST
    ///   The exact clip returns null for a whole family of reasons - curved or non-planar
    ///   faces, edited profiles, a boolean that fails or comes back empty - and every one of
    ///   them sends the face to the arithmetic fallback, where <see cref="MaterialKey.Fallback"/>
    ///   is unpainted BY DEFINITION. That is the right call for a cost basis: fallback area was
    ///   never measured off a face, so it has no material to name and inventing one would price
    ///   a measurement failure.
    ///
    ///   But it makes the two possible causes indistinguishable in the output. "This face has
    ///   no paint on it" and "this face is painted and we could not measure it" both leave the
    ///   paint takeoff with no row, and only the second is a defect. This answers which.
    ///
    /// DELIBERATELY NOT AN AREA
    ///   It returns a bool, not a quantity, because the only number available here is the
    ///   room subface's gross planar area - openings not deducted, split regions not resolved.
    ///   Reporting that as painted area would put an upper bound into a schedule that is read
    ///   as a measurement. The caller flags the element and names it; the quantity stays out
    ///   until the modelling is fixed and the exact path can measure it properly.
    ///
    /// Same coplanar test and tolerance as the exact clip, so it selects exactly the faces
    /// that clip would have measured - minus the boolean, which is the expensive part and the
    /// part that just failed.
    /// </summary>
    public bool HasPaintedCoplanarFace(
        Face roomFace, IReadOnlyList<Face> hostFaces, Element? owner)
    {
        if (owner is null) return false;

        var (roomOrigin, roomNormal) = PlanarData(roomFace);
        if (roomOrigin is null || roomNormal is null) return false;

        foreach (var hostFace in hostFaces)
        {
            var (hostOrigin, hostNormal) = PlanarData(hostFace);
            if (hostOrigin is null || hostNormal is null) continue;

            if (Math.Abs(Math.Abs(roomNormal.DotProduct(hostNormal)) - 1.0) > 0.01) continue;
            if (Math.Abs((hostOrigin - roomOrigin).DotProduct(roomNormal)) > FinishSettings.CoplanarTolerance)
                continue;

            try
            {
                if (_doc.IsPainted(owner.Id, hostFace)) return true;
            }
            catch
            {
                // A face whose reference will not resolve cannot be proven painted.
            }
        }

        return false;
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
