using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Dimensions;

/// <summary>One room-facing wall face, with everything the dimensioner needs to place it.</summary>
/// <param name="Reference">
/// What actually goes into the ReferenceArray. A dimension is not a measurement between two
/// points - it is a live association to two faces, and it updates when they move. Anything
/// derived from coordinates would produce a number that looked right and then went stale the
/// first time somebody nudged a wall.
/// </param>
/// <param name="Inward">Unit normal, in plan, pointing INTO the room.</param>
/// <param name="Point">A point known to lie on the face - the boundary segment's midpoint.</param>
/// <param name="Length">
/// Length of the boundary run along this face. Carried so the room's principal axis can be
/// chosen by how much WALL points each way rather than by how many faces do - a room with one
/// long facade and four short returns off it has its long axis decided by the facade, and
/// counting faces would answer with the returns.
/// </param>
/// <param name="HostId">The bounding element, for reporting.</param>
internal sealed record RoomFace(
    Reference Reference, XYZ Inward, XYZ Point, XYZ Start, XYZ End, double Length, ElementId HostId);

/// <summary>
/// Turns a room's boundary into references to the wall faces that bound it.
///
/// WHY THIS IS THE HARD PART
///   BoundarySegment gives a CURVE and an ElementId, and neither is a Reference. You cannot
///   dimension to a curve. So every segment has to be matched back to the actual face of the
///   actual wall that produced it, and the match has to survive the two things that make a
///   naive version wrong:
///
///     * A wall between two rooms has TWO parallel side faces. Picking by proximity alone
///       picks the neighbour's face about half the time, and the dimension then measures to
///       the far side of the partition - out by the wall thickness, on a drawing where being
///       out by the wall thickness is the entire point of the exercise.
///
///     * A wall face is one face no matter how many boundary segments run along it. Feeding
///       Revit the same reference twice produces a zero-length segment and the whole
///       dimension string fails, not just that segment.
///
///   The first is solved by direction: the room-side face's outward normal and the room's
///   inward direction at that segment are the same vector. The second by de-duplicating on
///   the reference's stable representation, which is the only string that identifies a face
///   the way Revit itself does.
/// </summary>
internal static class RoomFaceReferences
{
    /// <summary>
    /// How far off a face's plane the room boundary may sit and still be THAT face.
    ///
    /// THIS IS THE FINISH-FACE GUARANTEE, and it used to be 300 mm, which quietly gave it
    /// away. The boundary is requested at SpatialElementBoundaryLocation.Finish, so the curve
    /// lies ON the room-side finish face - that is what makes matching by plane the right test
    /// at all. At 300 mm the test cannot distinguish the finish face from the core face behind
    /// it, nor from a separate finish wall's back face, and it silently accepts whichever came
    /// first out of the geometry. The resulting dimension is out by a finish thickness: right
    /// to within 13 mm, wrong on a drawing whose whole purpose is those 13 mm.
    ///
    /// 5 mm is loose enough for Revit's own rounding and tight enough that nothing but the
    /// finish face can satisfy it.
    /// </summary>
    private static readonly double PlaneTolerance = Measure.FromMillimetres(5);

    /// <summary>
    /// Used only when nothing matches at <see cref="PlaneTolerance"/>, and always reported.
    ///
    /// A room bounded by something the boundary generaliser smoothed still deserves a
    /// dimension, and one measured to a face 40 mm from the boundary beats none at all. What
    /// it does not deserve is silence: this path is the one that can put a wrong number on a
    /// drawing, so every use of it earns a line in the report naming the room and the element.
    /// </summary>
    private static readonly double LoosePlaneTolerance = Measure.FromMillimetres(150);

    private static readonly Options GeometryOptions = new()
    {
        ComputeReferences = true,          // without this every Face.Reference comes back null
        IncludeNonVisibleObjects = false,
        DetailLevel = ViewDetailLevel.Fine,
    };

    /// <summary>
    /// Every distinct room-facing wall face of the room. Faces already seen are dropped;
    /// anything that could not be resolved is described in notes rather than dropped silently.
    /// </summary>
    public static List<RoomFace> Collect(Document doc, Room room, List<string> notes)
    {
        var faces = new List<RoomFace>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var curvedReported = false;

        IList<IList<BoundarySegment>> loops;
        try
        {
            loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
            }) ?? [];
        }
        catch (Exception ex)
        {
            notes.Add($"{Describe(room)}: boundary unreadable ({ex.Message}).");
            return faces;
        }

        foreach (var loop in loops)
        {
            // The room interior is on the LEFT of travel for a counter-clockwise loop and on
            // the right for a clockwise one. Revit documents outer loops as CCW and holes as
            // CW, but measuring the loop rather than trusting that costs one pass and cannot
            // be wrong about a loop that came back the other way round.
            var clockwise = DimensionMath.SignedArea(Endpoints(loop)) < 0;

            foreach (var segment in loop)
            {
                if (segment.GetCurve() is not Line line)
                {
                    // Curved boundary. A linear dimension has nothing to measure between two
                    // arcs, so this is a real limitation and not a failure - say so once.
                    if (!curvedReported)
                    {
                        notes.Add($"{Describe(room)}: curved boundary skipped (radial " +
                                  "dimensioning is not part of this tool).");
                        curvedReported = true;
                    }

                    continue;
                }

                var direction = line.Direction;
                if (direction.IsVertical()) continue;           // vertical segment, not a plan wall

                var inward = DimensionMath.Inward(direction.ToPlan(), clockwise).ToWorld(0);
                var midpoint = line.Evaluate(0.5, true);

                var host = doc.GetElement(segment.ElementId);
                if (host is null) continue;                     // room separation line: no face to hold

                var reference = FaceThrough(host, midpoint, inward, PlaneTolerance);

                if (reference is null)
                {
                    reference = FaceThrough(host, midpoint, inward, LoosePlaneTolerance);

                    if (reference is not null)
                        notes.Add($"{Describe(room)}: no face on {Describe(host)} lies on the " +
                                  "room boundary, so the nearest parallel face was used. CHECK " +
                                  "THIS DIMENSION - it may be measured to the core rather than " +
                                  "the finish.");
                }

                if (reference is null)
                {
                    notes.Add($"{Describe(room)}: no usable face on {Describe(host)} " +
                              "- that run is not dimensioned.");
                    continue;
                }

                // Stable representation, not ElementId or coordinates: it is the only key that
                // says "this exact face of this exact element" the way Revit means it.
                var key = reference.ConvertToStableRepresentation(doc);
                if (!seen.Add(key)) continue;

                faces.Add(new RoomFace(
                    reference, inward, midpoint,
                    line.GetEndPoint(0), line.GetEndPoint(1),
                    line.Length, host.Id));
            }
        }

        return faces;
    }

    /// <summary>
    /// The face of the host that passes through the point with its normal pointing inward.
    ///
    /// Walls go through HostObjectUtils first. That API hands back the shell faces directly
    /// and is exact for the ordinary case; walking the solid is the fallback for the ones it
    /// does not answer for - in-place walls, stacked walls, and columns, which are bounding
    /// elements too and would otherwise be silently ignored.
    /// </summary>
    private static Reference? FaceThrough(Element host, XYZ point, XYZ inward, double tolerance)
    {
        if (host is Wall wall)
        {
            var fromShell = FromSideFaces(wall, point, inward, tolerance);
            if (fromShell is not null) return fromShell;
        }

        return FromSolids(host, point, inward, tolerance);
    }

    private static Reference? FromSideFaces(Wall wall, XYZ point, XYZ inward, double tolerance)
    {
        Reference? best = null;
        var bestDistance = double.MaxValue;

        foreach (var shell in new[] { ShellLayerType.Interior, ShellLayerType.Exterior })
        {
            IList<Reference> candidates;
            try { candidates = HostObjectUtils.GetSideFaces(wall, shell); }
            catch { continue; }

            foreach (var candidate in candidates)
            {
                PlanarFace? face;
                try { face = wall.GetGeometryObjectFromReference(candidate) as PlanarFace; }
                catch { continue; }

                if (face is null) continue;

                var distance = Score(face, point, inward, tolerance);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
        }

        return best;
    }

    private static Reference? FromSolids(Element host, XYZ point, XYZ inward, double tolerance)
    {
        GeometryElement? geometry;
        try { geometry = host.get_Geometry(GeometryOptions); }
        catch { return null; }

        if (geometry is null) return null;

        Reference? best = null;
        var bestDistance = double.MaxValue;

        foreach (var solid in Solids(geometry))
        {
            foreach (Face face in solid.Faces)
            {
                if (face is not PlanarFace planar || planar.Reference is null) continue;

                var distance = Score(planar, point, inward, tolerance);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = planar.Reference;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Distance from the boundary point to the face's plane, or MaxValue when the face is not
    /// a candidate at all. Direction is a gate, not a tiebreak - a face pointing the other way
    /// is the neighbouring room's face and must never win on being closer.
    /// </summary>
    private static double Score(PlanarFace face, XYZ point, XYZ inward, double tolerance)
    {
        var normal = face.FaceNormal;
        if (normal.DotProduct(inward) < 0.966) return double.MaxValue;      // >15 degrees off

        var distance = Math.Abs((point - face.Origin).DotProduct(normal));
        return distance > tolerance ? double.MaxValue : distance;
    }

    private static IEnumerable<Solid> Solids(GeometryElement geometry)
    {
        foreach (var item in geometry)
        {
            switch (item)
            {
                case Solid solid when solid.Faces.Size > 0:
                    yield return solid;
                    break;

                case GeometryInstance instance:
                    // Columns and most families put their geometry one level down, in the
                    // instance. get_Geometry alone returns the empty wrapper.
                    foreach (var nested in Solids(instance.GetInstanceGeometry()))
                        yield return nested;
                    break;
            }
        }
    }

    /// <summary>The loop's segment endpoints in plan, for the winding test.</summary>
    private static List<(DimensionMath.Vec2 A, DimensionMath.Vec2 B)> Endpoints(IList<BoundarySegment> loop)
    {
        var points = new List<(DimensionMath.Vec2, DimensionMath.Vec2)>(loop.Count);

        foreach (var segment in loop)
        {
            var curve = segment.GetCurve();
            if (curve is null) continue;

            points.Add((curve.GetEndPoint(0).ToPlan(), curve.GetEndPoint(1).ToPlan()));
        }

        return points;
    }

    private static string Describe(Room room) => RoomNaming.Describe(room);

    private static string Describe(Element element) =>
        $"{element.Category?.Name ?? "element"} {element.Id.Value}";
}
