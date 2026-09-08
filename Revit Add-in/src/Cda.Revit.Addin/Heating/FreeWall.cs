using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Linings;

namespace Cda.Revit.Addin.Heating;

/// <summary>One stretch of wall a panel could stand on, and what is standing in the way.</summary>
/// <param name="Wall">The wall the face belongs to.</param>
/// <param name="Free">Stretches of it, in wall-u, with nothing in the panel's height band.</param>
/// <param name="FaceV">Where the room-side face sits on the v axis.</param>
/// <param name="Blockers">What was subtracted, named, for the report.</param>
public sealed record WallFace(
    Wall Wall,
    WallAxis Axis,
    XYZ Tangent,
    XYZ Normal,
    double RoomSide,
    double FaceV,
    Span WallV,
    IReadOnlyList<Span> Free,
    IReadOnlyList<string> Blockers)
{
    /// <summary>v of the wall's centre plane - the plane a panel has to be mirrored about to
    /// move it from one face of the wall to the other without changing its standoff.</summary>
    public double CentreV => Seating.CentreV(WallV.Lo, WallV.Hi);


    public double LongestFree => Free.Count == 0 ? 0.0 : Free.Max(s => s.Length);

    /// <summary>World point on the face at a given distance along the wall.</summary>
    public XYZ PointAt(double u) =>
        Axis.Origin + Tangent.Multiply(u) + Normal.Multiply(FaceV);
}

/// <summary>
/// Works out how much of a wall face is actually free, in the only band that matters: the one
/// the panel itself occupies.
///
/// THE HEIGHT BAND IS THE WHOLE IDEA
///   Deciding this in plan is the intuitive approach and it is wrong in both directions. A
///   window is "on the wall" and is not in the way - it is 900 mm up, which is exactly why
///   the radiator goes there. A wall cupboard is "on the wall" and is not in the way either.
///   A door IS, and so is a base unit, and so is a column, and a plan-only test cannot tell
///   any of those four apart.
///
///   So every candidate obstruction is measured in three dimensions and kept only if it
///   overlaps the panel's band vertically AND comes close enough to the face to touch it.
///   That single test replaces a list of special cases and gets the window right for the
///   right reason rather than by being excluded by category.
///
/// WHAT IT DOES NOT DO
///   It does not look at the OTHER side of the wall. A cupboard in the next room is not an
///   obstruction, and treating a wall as one object rather than two faces is the classic way
///   to lose half the placements in a flat.
/// </summary>
public static class FreeWall
{
    /// <summary>
    /// The free stretches of one wall face inside one room, for a panel of the given height
    /// band and depth. Returns null when the room does not actually bound this wall.
    /// </summary>
    public static WallFace? On(
        Document doc,
        Room room,
        Wall wall,
        Span band,
        double depth,
        RadiatorSettings settings,
        double? knownRoomSide = null)
    {
        var axis = WallAxis.Of(wall);
        if (axis?.Direction is null) return null;

        var tangent = axis.Direction;
        var normal = Geometry.NormalOf(tangent);

        var wallExtents = Geometry.Extents(wall, tangent, normal, axis.Origin);
        if (wallExtents is null) return null;

        var segments = RoomSpans(room, wall, axis, tangent);
        if (segments.Count == 0) return null;

        var roomSide = knownRoomSide
                       ?? SideOfRoom(room, wall, axis, tangent, normal, wallExtents.Value.V);

        // No side means the room's boundary could not say which face it presents to this
        // wall. The face is dropped rather than defaulted: a made-up side sends every
        // obstruction measurement to the wrong face and puts the panel through the envelope.
        if (roomSide is null) return null;

        var faceV = Seating.FaceV(wallExtents.Value.V.Lo, wallExtents.Value.V.Hi, roomSide.Value);

        // Buildability, not comfort: a panel hard into a corner cannot be lifted off its
        // brackets and the pipe drop has nowhere to go.
        var usable = segments
            .Select(s => new Span(s.Lo + settings.CornerClearance, s.Hi - settings.CornerClearance))
            .Where(s => s.Length > 0)
            .ToList();

        var blockers = new List<Span>();
        var named = new List<string>();

        foreach (var (span, description) in Obstructions(
                     doc, room, wall, axis, tangent, normal, roomSide.Value, faceV, band, depth, settings))
        {
            blockers.Add(span);
            named.Add(description);
        }

        var free = usable
            .SelectMany(s => s.Subtract(blockers))
            .Where(s => s.Length > 0)
            .OrderByDescending(s => s.Length)
            .ToList();

        return new WallFace(
            wall, axis, tangent, normal, roomSide.Value, faceV, wallExtents.Value.V, free, named);
    }

    // ------------------------------------------------------------- obstructions

    private static IEnumerable<(Span Span, string What)> Obstructions(
        Document doc, Room room, Wall wall, WallAxis axis, XYZ tangent, XYZ normal,
        double roomSide, double faceV, Span band, double depth, RadiatorSettings settings)
    {
        var seen = new HashSet<long>();

        // 1. What is cut INTO this wall. FindInserts is the only authority on that, and the
        //    height-band test is what keeps the window out of the answer while keeping the
        //    door in it.
        foreach (var id in Inserts(wall))
        {
            if (!seen.Add(id.Value)) continue;

            var insert = doc.GetElement(id);
            if (insert is null) continue;

            var extents = Geometry.Extents(insert, tangent, normal, axis.Origin);
            if (extents is null) continue;

            if (Span.Overlap(extents.Value.Z, band) is null) continue;

            yield return (
                Grow(extents.Value.U, settings.OpeningClearance),
                $"{Category(insert)} {id.Value} {Name(insert)}");
        }

        // 2. What is STANDING against it. A separate question with a separate answer: a
        //    column set flush into the wall, a run of base units, a WC, a radiator already
        //    placed by this run - none of them are inserts, and all of them are in the way.
        foreach (var element in Nearby(doc, room, wall, band, depth, settings))
        {
            if (!seen.Add(element.Id.Value)) continue;

            var extents = Geometry.Extents(element, tangent, normal, axis.Origin);
            if (extents is null) continue;

            if (Span.Overlap(extents.Value.Z, band) is null) continue;

            // Distance out from the face, into the room. Anything sitting further out than
            // the panel's own depth plus the reach allowance is furniture in the room rather
            // than an obstruction on the wall, and the panel can sit behind it.
            var w = Seating.OutFromFace(extents.Value.V.Lo, extents.Value.V.Hi, faceV, roomSide);
            if (!Seating.Obstructs(w.Lo, w.Hi, depth, settings.BlockingReach)) continue;

            yield return (
                Grow(extents.Value.U, settings.ObstacleClearance),
                $"{Category(element)} {element.Id.Value} {Name(element)}");
        }
    }

    /// <summary>
    /// Candidates near the wall face, narrowed by bounding box before any geometry is read.
    ///
    /// The box is the deliberate part. Reading solids is the expensive operation in this
    /// whole tool, and a filter that hands the loop twenty elements instead of the model's
    /// forty thousand is the difference between a run measured in seconds and one measured in
    /// minutes.
    /// </summary>
    private static IEnumerable<Element> Nearby(
        Document doc, Room room, Wall wall, Span band, double depth, RadiatorSettings settings)
    {
        BoundingBoxXYZ? box;
        try { box = wall.get_BoundingBox(null); }
        catch { yield break; }

        if (box is null) yield break;

        var pad = wall.Width / 2.0 + depth + settings.BlockingReach + settings.ObstacleClearance;

        var outline = new Outline(
            new XYZ(box.Min.X - pad, box.Min.Y - pad, band.Lo - settings.Tolerance),
            new XYZ(box.Max.X + pad, box.Max.Y + pad, band.Hi + settings.Tolerance));

        var categories = new ElementMulticategoryFilter(settings.BlockingCategories);

        IList<Element> found;
        try
        {
            found = new FilteredElementCollector(doc)
                .WherePasses(categories)
                .WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .ToElements();
        }
        catch
        {
            yield break;
        }

        foreach (var element in found)
        {
            if (element.Id == wall.Id) continue;

            // NO ROOM MEMBERSHIP TEST HERE, and that is a removal rather than an omission.
            //
            // There was one: each candidate's location point was put to Room.IsPointInRoom to
            // keep out casework standing on the far face of the same wall, in the next flat.
            // It was redundant twice over. Something behind the room-side face is rejected by
            // the standoff test in Obstructions, and something along the same face but in the
            // next room along falls outside the boundary spans this face was clipped to.
            //
            // Redundant would be harmless. This was not: IsPointInRoom proved unreliable on
            // this model, and a wrong answer here DISCARDS a real obstruction, which is the
            // one direction that puts a radiator through a cupboard. A test that adds nothing
            // and can only fail dangerously is worse than no test.
            yield return element;
        }
    }

    // ------------------------------------------------------------- room boundary

    /// <summary>
    /// The stretches of this wall that bound this room, in wall-u.
    ///
    /// A wall commonly runs past a room - through a partition and on into the next one - and
    /// its full length is not available. The room's own boundary segments are the authority
    /// on how much of it belongs here.
    ///
    /// Finish location, not Centre: the panel stands against the finished face, and on a
    /// wall with a 12 mm board on it the two answers differ by exactly the amount that
    /// decides whether the panel clears the reveal.
    /// </summary>
    private static List<Span> RoomSpans(Room room, Wall wall, WallAxis axis, XYZ tangent)
    {
        var spans = new List<Span>();

        var options = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
        };

        IList<IList<BoundarySegment>> loops;
        try { loops = room.GetBoundarySegments(options); }
        catch { return spans; }

        foreach (var loop in loops)
        {
            foreach (var segment in loop)
            {
                if (segment.ElementId != wall.Id) continue;

                Curve curve;
                try { curve = segment.GetCurve(); }
                catch { continue; }

                var a = axis.UOf(curve.GetEndPoint(0));
                var b = axis.UOf(curve.GetEndPoint(1));

                spans.Add(new Span(Math.Min(a, b), Math.Max(a, b)));
            }
        }

        return Merge(spans);
    }

    /// <summary>Joins spans that touch, so two collinear boundary segments read as one run.</summary>
    private static List<Span> Merge(List<Span> spans)
    {
        if (spans.Count < 2) return spans;

        spans.Sort((x, y) => x.Lo.CompareTo(y.Lo));

        var merged = new List<Span> { spans[0] };

        foreach (var span in spans.Skip(1))
        {
            var last = merged[^1];

            if (span.Lo <= last.Hi + 1e-6)
                merged[^1] = new Span(last.Lo, Math.Max(last.Hi, span.Hi));
            else
                merged.Add(span);
        }

        return merged;
    }

    /// <summary>
    /// Which side of the wall the room is on, read off the room's own boundary.
    ///
    /// THE BOUNDARY CURVE IS ALREADY THE ANSWER. A Finish-location boundary segment lies ON
    /// the face the room presents to that wall, so which side the room is on is just which
    /// side of the wall's centre that curve falls. No probing, no spatial query, no second
    /// API to disagree with the first.
    ///
    /// It replaces a pair of IsPointInRoom probes that ended `return -1.0` when both failed -
    /// a silent default for the single most damaging value in this tool to get wrong, on an
    /// API this model has already been measured to disagree with. A guess is bad enough; a
    /// guess that cannot be told apart from an answer is worse.
    ///
    /// Returns null when the room does not bound this wall, so the caller can drop the face
    /// rather than inherit a made-up side.
    /// </summary>
    private static double? SideOfRoom(
        Room room, Wall wall, WallAxis axis, XYZ tangent, XYZ normal, Span wallV)
    {
        foreach (var curve in BoundaryCurvesOn(room, wall))
        {
            var at = curve.Evaluate(0.5, true);
            var v = (at - axis.Origin).DotProduct(normal);

            // Null means the boundary sits on the wall's centreline and carries no side
            // information - the location line rather than a face. Try the next segment.
            var side = Seating.SideFromBoundary(v, wallV.Lo, wallV.Hi, 1e-6);
            if (side is not null) return side;
        }

        return null;
    }

    /// <summary>The room's boundary curves that belong to this wall, at Finish location.</summary>
    private static IEnumerable<Curve> BoundaryCurvesOn(Room room, Wall wall)
    {
        IList<IList<BoundarySegment>> loops;

        try
        {
            loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
            });
        }
        catch
        {
            yield break;
        }

        foreach (var loop in loops)
        {
            foreach (var segment in loop)
            {
                if (segment.ElementId != wall.Id) continue;

                Curve? curve = null;
                try { curve = segment.GetCurve(); }
                catch { /* unreadable segment */ }

                if (curve is not null) yield return curve;
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static IList<ElementId> Inserts(Wall wall)
    {
        try
        {
            return wall.FindInserts(
                addRectOpenings: true, includeShadows: false,
                includeEmbeddedWalls: true, includeSharedEmbeddedInserts: true);
        }
        catch
        {
            return [];
        }
    }

    private static Span Grow(Span span, double by) => new(span.Lo - by, span.Hi + by);

    private static string Category(Element element)
    {
        try { return element.Category?.Name ?? "?"; }
        catch { return "?"; }
    }

    private static string Name(Element element)
    {
        try { return element.Name; }
        catch { return string.Empty; }
    }
}
