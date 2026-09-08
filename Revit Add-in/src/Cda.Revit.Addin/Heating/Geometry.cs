using Autodesk.Revit.DB;
using Cda.Revit.Addin.Linings;

namespace Cda.Revit.Addin.Heating;

/// <summary>
/// Measuring a real element against a wall's own axes.
///
/// Everything this tool decides is decided in wall-local coordinates: u along the wall, v out
/// of its face, z up. A bounding box in world XYZ answers none of those questions on a wall
/// that is not aligned to the project grid - a 1000 mm panel on a 30-degree wall has a
/// 916 mm world-X extent, and reading that as its length is how a panel gets planned into a
/// door reveal.
///
/// The vertices come from tessellated solid edges rather than from
/// <see cref="Element.get_BoundingBox"/> for the same reason. Tessellation is approximate on
/// a curved edge and exact on the straight ones a radiator, a door frame and a cupboard are
/// made of, which is the population this measures.
/// </summary>
internal static class Geometry
{
    private static readonly Options Detail = new()
    {
        ComputeReferences = false,
        IncludeNonVisibleObjects = false,
        DetailLevel = ViewDetailLevel.Fine,
    };

    /// <summary>
    /// Extent of an element's solids along <paramref name="tangent"/>, <paramref name="normal"/>
    /// and world Z. Null when the element has no solid geometry - an annotation symbol, a
    /// family with everything switched off at Fine, a 2D-only placeholder.
    /// </summary>
    /// <param name="origin">
    /// Point the u and v spans are measured FROM. Pass the wall axis origin so every element
    /// on one wall is measured on one ruler; pass null to measure from the world origin,
    /// which is only useful for a length.
    /// </param>
    public static (Span U, Span V, Span Z)? Extents(
        Element element, XYZ tangent, XYZ normal, XYZ? origin = null)
    {
        var from = origin ?? XYZ.Zero;

        double uLo = double.MaxValue, uHi = double.MinValue;
        double vLo = double.MaxValue, vHi = double.MinValue;
        double zLo = double.MaxValue, zHi = double.MinValue;
        var any = false;

        foreach (var point in Vertices(element))
        {
            var offset = point - from;

            var u = offset.DotProduct(tangent);
            var v = offset.DotProduct(normal);

            uLo = Math.Min(uLo, u); uHi = Math.Max(uHi, u);
            vLo = Math.Min(vLo, v); vHi = Math.Max(vHi, v);
            zLo = Math.Min(zLo, point.Z); zHi = Math.Max(zHi, point.Z);

            any = true;
        }

        return any ? (new Span(uLo, uHi), new Span(vLo, vHi), new Span(zLo, zHi)) : null;
    }

    /// <summary>Every tessellated solid vertex of an element, nested instances included.</summary>
    public static IEnumerable<XYZ> Vertices(Element element)
    {
        GeometryElement? geometry;

        try { geometry = element.get_Geometry(Detail); }
        catch { yield break; }

        if (geometry is null) yield break;

        foreach (var point in Walk(geometry))
            yield return point;
    }

    private static IEnumerable<XYZ> Walk(GeometryElement geometry)
    {
        foreach (var item in geometry)
        {
            switch (item)
            {
                case Solid solid when SafeVolume(solid) > 1e-9:
                    foreach (var point in FromSolid(solid)) yield return point;
                    break;

                // A family instance's geometry arrives as an instance wrapper holding the
                // symbol's geometry in symbol coordinates. GetInstanceGeometry applies the
                // instance transform, which is what puts it where the element actually is -
                // GetSymbolGeometry would measure every radiator at the origin.
                case GeometryInstance nested:
                    GeometryElement? inner = null;
                    try { inner = nested.GetInstanceGeometry(); }
                    catch { /* unreadable nested geometry; skip it */ }

                    if (inner is null) break;
                    foreach (var point in Walk(inner)) yield return point;
                    break;
            }
        }
    }

    private static IEnumerable<XYZ> FromSolid(Solid solid)
    {
        IList<XYZ>? points = null;

        foreach (Edge edge in solid.Edges)
        {
            try { points = edge.Tessellate(); }
            catch { continue; }

            foreach (var point in points) yield return point;
        }
    }

    private static double SafeVolume(Solid solid)
    {
        try { return solid.Volume; }
        catch { return 0.0; }
    }

    /// <summary>
    /// Outward normal of a wall face, in plan. Right-handed against the tangent, so the two
    /// together with Z form a consistent frame however the wall was drawn.
    /// </summary>
    public static XYZ NormalOf(XYZ tangent)
    {
        var flat = new XYZ(tangent.X, tangent.Y, 0);
        return flat.GetLength() < 1e-9
            ? XYZ.BasisY
            : new XYZ(-flat.Y, flat.X, 0).Normalize();
    }
}
