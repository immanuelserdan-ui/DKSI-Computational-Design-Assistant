using Autodesk.Revit.DB;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Finishes;

/// <summary>One Split Face region: the sub-face, and what it is worth.</summary>
/// <param name="Parent">The face the region was split from. Regions of one parent share a plane.</param>
/// <param name="Region">The sub-face itself. Its Area is the region's own area, not the face's.</param>
/// <param name="Index">Position within the parent's region list, after deterministic sorting.</param>
internal sealed record FaceRegion(Face Parent, Face Region, int Index)
{
    public double AreaSqFt => Region.Area;

    /// <summary>
    /// The region's own Reference - what Paint, selection and dimensioning need.
    ///
    /// NULL UNLESS the geometry was read with ComputeReferences = true. A region harvested
    /// without it looks completely normal and then cannot be painted or selected, which is a
    /// miserable thing to debug. <see cref="SplitFaceRegions.Of"/> always sets it.
    /// </summary>
    public Reference? Reference => Region.Reference;
}

/// <summary>
/// Reads the sub-faces the Split Face tool creates, and answers "which region is this point
/// on" without ever widening to the host element.
///
/// THE API, AND WHY IT IS NOT THE OBVIOUS ONE
///   A Split Face does not add a face to the wall. It divides an existing face into REGIONS,
///   and those are reachable only through Face.HasRegions and Face.GetRegions(). Nothing else
///   exposes them:
///
///     * HostObjectUtils.GetSideFaces returns the wall's SHELL faces, one per side. A split
///       wall and an unsplit wall return the same count, so code that counts them concludes
///       the wall was never split.
///     * Solid.Faces returns the parent face, undivided, for the same reason.
///     * The FaceSplitter elements (OST_FaceSplitter) are the sketches, not the resulting
///       geometry, and expose neither area nor material.
///
///   So the region is the only object that knows its own area and its own material, and
///   Face.GetRegions() is the only way to get one.
///
/// WHY REGIONS ARE SORTED
///   GetRegions() does not promise an order, and an unstable order means a region's index -
///   and any label built from it - changes between runs for no reason the user can see.
///   Sorting by the region centroid, lowest and leftmost first, makes "region 1" mean the
///   same thing every time.
/// </summary>
internal static class SplitFaceRegions
{
    /// <summary>
    /// Every region on <paramref name="element"/>, grouped nowhere - flat, each carrying its
    /// parent so callers that need per-face grouping can do it themselves.
    ///
    /// A face with no regions contributes nothing. That is deliberate: this type answers
    /// questions about splits, and a caller that wants "the whole face if it is not split"
    /// should say so rather than have it silently mixed in with real regions.
    /// </summary>
    public static IReadOnlyList<FaceRegion> Of(Element element)
    {
        var found = new List<FaceRegion>();

        foreach (var solid in Solids(element))
        {
            foreach (Face face in solid.Faces)
            {
                bool split;

                try
                {
                    split = face.HasRegions;
                }
                catch
                {
                    continue;
                }

                if (!split) continue;

                IList<Face>? regions = null;

                try
                {
                    regions = face.GetRegions();
                }
                catch
                {
                    Log.Debug($"Face on element {element.Id.Value} claims regions but will not return them.");
                }

                if (regions is null || regions.Count <= 1) continue;

                var ordered = regions
                    .Where(r => r.Area > 0)
                    .OrderBy(Sort)
                    .ToList();

                for (var i = 0; i < ordered.Count; i++)
                    found.Add(new FaceRegion(face, ordered[i], i));
            }
        }

        return found;
    }

    /// <summary>
    /// The region a point sits on, or null.
    ///
    /// TWO TESTS, AND BOTH ARE NEEDED. Face.Project answers "where on this face's SURFACE does
    /// the point land", and every region of one face shares that surface - so projection alone
    /// returns a hit on all of them and picks whichever came first. Face.IsInside is what
    /// tests the region's own trimmed boundary. Skipping it is precisely the bug where
    /// clicking the upper region targets the lower one.
    /// </summary>
    /// <param name="within">
    /// How far the point may sit off the face and still count, in feet. A point picked on
    /// screen or taken from a room boundary is never exactly on the plane.
    /// </param>
    public static FaceRegion? At(Element element, XYZ point, double within)
    {
        FaceRegion? best = null;
        var nearest = double.MaxValue;

        foreach (var candidate in Of(element))
        {
            try
            {
                var hit = candidate.Region.Project(point);
                if (hit is null || hit.Distance > within) continue;

                // The boundary test. Without it, every region of this parent face matches.
                if (!candidate.Region.IsInside(hit.UVPoint)) continue;

                if (hit.Distance < nearest)
                {
                    nearest = hit.Distance;
                    best = candidate;
                }
            }
            catch
            {
                // Degenerate region; the next one may still answer.
            }
        }

        return best;
    }

    /// <summary>
    /// The material actually on a region, and whether it came from the Paint tool.
    ///
    /// PAINT FIRST, because Split Face and Paint are different acts with different answers.
    /// Painting a region sets a painted material that Document.GetPaintedMaterial reports and
    /// that MaterialElementId knows nothing about; assigning a material to a split region
    /// instead shows up on MaterialElementId. A takeoff that reads only one of them loses
    /// every region finished the other way.
    /// </summary>
    public static (ElementId Material, bool AsPaint) MaterialOf(
        Document doc, ElementId elementId, Face region)
    {
        try
        {
            if (doc.IsPainted(elementId, region))
            {
                var painted = doc.GetPaintedMaterial(elementId, region);
                if (painted != ElementId.InvalidElementId) return (painted, true);
            }
        }
        catch
        {
            // Fall through to the face's own material.
        }

        try
        {
            return (region.MaterialElementId, false);
        }
        catch
        {
            return (ElementId.InvalidElementId, false);
        }
    }

    /// <summary>
    /// The region's own geometry: its boundary extruded into a thin slab, ready to be used as
    /// carrier geometry, as a clash body, or as the operand of a boolean.
    ///
    /// THIS IS WHAT "OPERATES ON THE REGION, NOT THE WALL" MEANS IN PRACTICE. The loops come
    /// from the REGION face, so the slab covers exactly the patch the Split Face drew -
    /// including its diagonal under a stair - and stops at the neighbouring region's edge. A
    /// slab built from the parent face instead would cover the whole side of the wall and
    /// silently claim its neighbour's paint, which is the failure mode this type exists to
    /// prevent.
    ///
    /// Extruded AGAINST the face normal, so the slab sits just inside the material rather than
    /// floating in the room.
    /// </summary>
    /// <param name="thickness">Slab depth in feet. Keep it well under the host's thickness.</param>
    public static Solid? SolidOf(Face region, double thickness)
    {
        try
        {
            var loops = region.GetEdgesAsCurveLoops();
            if (loops is null || loops.Count == 0) return null;

            var normal = region is PlanarFace planar
                ? planar.FaceNormal
                : region.ComputeNormal(new UV(0.5, 0.5));

            if (normal.IsZeroLength()) return null;

            return GeometryCreationUtilities.CreateExtrusionGeometry(
                loops, -normal.Normalize(), thickness);
        }
        catch (Exception ex)
        {
            Log.Debug($"Region solid could not be built: {ex.Message}");
            return null;
        }
    }

    /// <summary>Lowest then leftmost then furthest back. See the class note on ordering.</summary>
    private static (long Z, long X, long Y) Sort(Face region)
    {
        try
        {
            var box = region.GetBoundingBox();
            var mid = (box.Min + box.Max) / 2.0;
            var point = region.Evaluate(new UV(mid.U, mid.V));

            static long Q(double value) => (long)Math.Round(value / Measure.FromMillimetres(1.0));

            return (Q(point.Z), Q(point.X), Q(point.Y));
        }
        catch
        {
            return (long.MaxValue, long.MaxValue, long.MaxValue);
        }
    }

    /// <summary>
    /// ComputeReferences IS REQUIRED, not a nicety. Without it every region's Reference comes
    /// back null and the regions cannot be painted, selected or dimensioned - see
    /// <see cref="FaceRegion.Reference"/>.
    /// </summary>
    private static IReadOnlyList<Solid> Solids(Element element)
    {
        var solids = new List<Solid>();

        try
        {
            var geometry = element.get_Geometry(new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine,
            });

            if (geometry is not null) Flatten(geometry, solids);
        }
        catch
        {
            Log.Debug($"Geometry unreadable on element {element.Id.Value}.");
        }

        return solids;
    }

    private static void Flatten(GeometryElement geometry, List<Solid> into)
    {
        foreach (var item in geometry)
        {
            switch (item)
            {
                case Solid solid when solid.Volume > 1e-9 && solid.Faces.Size > 0:
                    into.Add(solid);
                    break;

                case GeometryInstance instance:
                    var nested = instance.GetInstanceGeometry();
                    if (nested is not null) Flatten(nested, into);
                    break;
            }
        }
    }
}
