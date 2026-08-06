using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Qa;

/// <summary>How a wall was found to belong to a room. Reported so a surprise is explainable.</summary>
public enum WallSource
{
    /// <summary>A face the room's finish area is measured on. The authoritative case.</summary>
    MeasuredFace,

    /// <summary>On the room's plan boundary loop, but contributing no measured face.</summary>
    BoundaryLoop,

    /// <summary>Physically inside the room's volume, whether Revit calls it bounding or not.</summary>
    IntersectsVolume,

    /// <summary>Not inside, but standing against a boundary face within tolerance.</summary>
    TouchesBoundary,
}

/// <summary>
/// Finds walls that ENCLOSE a room, as opposed to walls Revit considers to BOUND it.
///
/// WHY THE TWO ARE NOT THE SAME THING, AND WHY THE DIFFERENCE LEAVES HOLES
///   Both of the earlier passes ask Revit's room model: the finish faces the area was
///   measured on, and the plan boundary loop. Both answer "which walls does this room's
///   definition reference". Neither answers "which walls physically stand around this space",
///   and a wall can be the second without being the first:
///
///     * ROOM BOUNDING IS OFF. The single most common cause. A wall with
///       WALL_ATTR_ROOM_BOUNDING unchecked is invisible to every room query in the API while
///       standing in plain sight in the model - and it is unchecked routinely, on purpose,
///       to stop a wall splitting a space into two rooms.
///     * A ROOM SEPARATION LINE IS DOING THE BOUNDING. The boundary segment then reports the
///       separation line, which is not a Wall, so the wall drawn along it is never named.
///     * THE WALL BELONGS TO THE NEIGHBOUR. It touches this room's boundary face without
///       bounding it - a stub, a chase, a wall stopping against the party wall.
///
///   In every one of those the wall is there, encloses the room, and gets hidden by an
///   isolation built from boundaries alone. That is the hole.
///
/// THE RULE THIS IMPLEMENTS
///   A wall stays visible if ANY part of it lies inside the room's volume, or if it touches a
///   boundary face within tolerance. Geometry decides, not the room's own bookkeeping.
///
/// WHICH WAY TO BE WRONG
///   Deliberately biased toward INCLUDING. A wall wrongly included is visible, obviously
///   adjacent, and cropped away by the section box a moment later. A wall wrongly excluded is
///   a hole in the enclosure that reads as a modelling error - which is exactly the bug this
///   class exists to fix, and it cost a round trip to find.
/// </summary>
public sealed class RoomWallFinder
{
    private readonly Document _doc;
    private readonly double _touchTolerance;

    public RoomWallFinder(Document doc, double touchToleranceMm)
    {
        _doc = doc;
        _touchTolerance = Measure.FromMillimetres(touchToleranceMm);
    }

    /// <summary>
    /// Walls enclosing <paramref name="room"/> that are not already in
    /// <paramref name="known"/>, each with the reason it qualified.
    /// </summary>
    /// <param name="roomSolid">
    /// The room volume from <see cref="SpatialElementGeometryCalculator"/>. Null is handled -
    /// the touch test still runs off the boundary curves, so an unmeasurable room degrades to
    /// a weaker answer rather than no answer.
    /// </param>
    public IReadOnlyList<(ElementId Id, WallSource Source)> Find(
        Room room, Solid? roomSolid, IReadOnlyCollection<ElementId> known)
    {
        var found = new List<(ElementId, WallSource)>();
        var seen = new HashSet<long>(known.Select(id => id.Value));

        var candidates = Candidates(room);
        if (candidates.Count == 0) return found;

        // ---- inside the volume ------------------------------------------------------

        var inside = new HashSet<long>();

        if (roomSolid is not null)
        {
            try
            {
                // Scoped to the candidate ids, never run over the whole model.
                // ElementIntersectsSolidFilter is a SLOW filter - it opens real geometry on
                // every element it is offered - so the bounding-box pass in front of it is
                // not an optimisation, it is what makes this usable on a real project.
                var ids = new FilteredElementCollector(_doc, candidates)
                    .WherePasses(new ElementIntersectsSolidFilter(roomSolid))
                    .ToElementIds();

                foreach (var id in ids) inside.Add(id.Value);
            }
            catch (Exception ex)
            {
                // Some room solids are not valid arguments to the filter - self-intersecting
                // shells from unenclosed rooms, mostly. The touch test below still runs.
                Log.Warn($"QA: solid intersection test failed for room {room.Id.Value}: {ex.Message}");
            }
        }

        // ---- touching a boundary face -----------------------------------------------

        var boundaries = BoundaryCurves(room);

        foreach (var id in candidates)
        {
            if (seen.Contains(id.Value)) continue;

            if (inside.Contains(id.Value))
            {
                seen.Add(id.Value);
                found.Add((id, WallSource.IntersectsVolume));
                continue;
            }

            if (_doc.GetElement(id) is not Wall wall) continue;

            if (!Touches(wall, boundaries)) continue;

            seen.Add(id.Value);
            found.Add((id, WallSource.TouchesBoundary));
        }

        return found;
    }

    // ------------------------------------------------------------------- candidates

    /// <summary>
    /// Every wall whose bounding box comes near the room. The cheap pass that keeps the
    /// expensive ones honest.
    /// </summary>
    private ICollection<ElementId> Candidates(Room room)
    {
        BoundingBoxXYZ? box;
        try { box = room.get_BoundingBox(null); }
        catch { return []; }

        if (box is null) return [];

        // Grown by a generous margin rather than by the touch tolerance alone: a wall's
        // bounding box is axis-aligned, so a diagonal wall's box can sit further from the
        // room than the wall itself does. Over-generous here costs a few extra candidates
        // and nothing else - the exact tests below still decide.
        var margin = Measure.FromMillimetres(600);

        try
        {
            var outline = new Outline(
                new XYZ(box.Min.X - margin, box.Min.Y - margin, box.Min.Z - margin),
                new XYZ(box.Max.X + margin, box.Max.Y + margin, box.Max.Z + margin));

            return new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .ToElementIds();
        }
        catch (Exception ex)
        {
            Log.Warn($"QA: wall candidate query failed for room {room.Id.Value}: {ex.Message}");
            return [];
        }
    }

    // ------------------------------------------------------------------------ touch

    /// <summary>
    /// The room's boundary at the FINISH face - the surface the brief measures "touching"
    /// against, and the same location the finish areas are taken from.
    /// </summary>
    private List<Curve> BoundaryCurves(Room room)
    {
        var curves = new List<Curve>();

        var options = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
        };

        IList<IList<BoundarySegment>> loops;
        try { loops = room.GetBoundarySegments(options); }
        catch { return curves; }

        if (loops is null) return curves;

        foreach (var loop in loops)
        {
            foreach (var segment in loop)
            {
                try
                {
                    var curve = segment.GetCurve();
                    if (curve is not null) curves.Add(curve);
                }
                catch
                {
                    // Unreadable segment; the others still describe the room.
                }
            }
        }

        return curves;
    }

    /// <summary>
    /// Does this wall stand against one of the room's boundary curves?
    ///
    /// Measured centreline-to-boundary and compared against HALF THE WALL'S THICKNESS plus
    /// the tolerance, because a wall touching a boundary face has its centreline exactly that
    /// far away. Comparing the centreline distance to the tolerance alone - the obvious
    /// version - would only ever match walls whose centreline is ON the boundary, which is
    /// the one arrangement that does not occur.
    ///
    /// Planar throughout: both curves are flattened to a common Z before measuring. A wall's
    /// location curve sits at its base and a boundary curve at the room's, and on a sunken
    /// room the difference between them is larger than any sane touch tolerance.
    /// </summary>
    private bool Touches(Wall wall, List<Curve> boundaries)
    {
        if (boundaries.Count == 0) return false;

        Curve? axis;
        try { axis = (wall.Location as LocationCurve)?.Curve; }
        catch { axis = null; }

        // No location curve: in-place families, stacked wall members, some curtain systems.
        // Fall back to the bounding box, which over-includes and is the right way to be wrong.
        if (axis is null) return TouchesByBox(wall, boundaries);

        var half = HalfThickness(wall);
        var reach = half + _touchTolerance;

        foreach (var boundary in boundaries)
        {
            try
            {
                var z = boundary.GetEndPoint(0).Z;

                // Sampling both directions: projecting the wall onto the boundary alone
                // misses a long boundary meeting a short wall end-on, and vice versa.
                if (MinDistance(axis, boundary, z) <= reach) return true;
                if (MinDistance(boundary, axis, z) <= reach) return true;
            }
            catch
            {
                // Undecidable pair; try the next boundary.
            }
        }

        return false;
    }

    /// <summary>
    /// Smallest distance from points sampled along <paramref name="from"/> to
    /// <paramref name="to"/>, both flattened to <paramref name="z"/>.
    ///
    /// Sampled rather than solved: Curve.Distance answers point-to-curve, and there is no
    /// curve-to-curve minimum in the API. Eleven samples resolves a metre to under 100 mm,
    /// which is far finer than the thing being decided.
    /// </summary>
    private static double MinDistance(Curve from, Curve to, double z)
    {
        const int samples = 10;
        var best = double.MaxValue;

        for (var i = 0; i <= samples; i++)
        {
            XYZ point;
            try { point = from.Evaluate(i / (double)samples, true); }
            catch { continue; }

            var flat = new XYZ(point.X, point.Y, z);

            try
            {
                var distance = to.Distance(flat);
                if (distance < best) best = distance;
            }
            catch
            {
                // Point outside this curve's usable range; the other samples still count.
            }
        }

        return best;
    }

    private double HalfThickness(Wall wall)
    {
        try
        {
            if (wall.Width > 0) return wall.Width / 2.0;
        }
        catch
        {
            // Curtain walls and some stacked walls have no meaningful Width.
        }

        return Measure.FromMillimetres(150);
    }

    private bool TouchesByBox(Wall wall, List<Curve> boundaries)
    {
        BoundingBoxXYZ? box;
        try { box = wall.get_BoundingBox(null); }
        catch { return false; }

        if (box is null) return false;

        var pad = _touchTolerance;

        foreach (var boundary in boundaries)
        {
            for (var i = 0; i <= 10; i++)
            {
                XYZ point;
                try { point = boundary.Evaluate(i / 10.0, true); }
                catch { continue; }

                if (point.X >= box.Min.X - pad && point.X <= box.Max.X + pad &&
                    point.Y >= box.Min.Y - pad && point.Y <= box.Max.Y + pad)
                    return true;
            }
        }

        return false;
    }
}
