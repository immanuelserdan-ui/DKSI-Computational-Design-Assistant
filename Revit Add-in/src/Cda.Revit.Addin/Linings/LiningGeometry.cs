using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Linings;

/// <summary>Revit stores every length in decimal feet. All maths here is in feet.</summary>
public static class Units
{
    public static double ToMm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
    public static double ToFeet(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
}

/// <summary>A half-open interval, used for both the u (along wall) and z (elevation) axes.</summary>
public readonly record struct Span(double Lo, double Hi)
{
    public double Length => Hi - Lo;

    /// <summary>The shared part of two spans, or null when they do not overlap.</summary>
    public static Span? Overlap(Span a, Span b)
    {
        var lo = Math.Max(a.Lo, b.Lo);
        var hi = Math.Min(a.Hi, b.Hi);
        return hi > lo ? new Span(lo, hi) : null;
    }

    /// <summary>The pieces of this span not covered by any of <paramref name="blocks"/>.</summary>
    public IReadOnlyList<Span> Subtract(IEnumerable<Span> blocks)
    {
        var segments = new List<Span> { this };

        foreach (var block in blocks)
        {
            var next = new List<Span>();
            foreach (var segment in segments)
            {
                if (block.Hi <= segment.Lo || block.Lo >= segment.Hi)
                {
                    next.Add(segment);
                    continue;
                }

                if (block.Lo > segment.Lo) next.Add(new Span(segment.Lo, block.Lo));
                if (block.Hi < segment.Hi) next.Add(new Span(block.Hi, segment.Hi));
            }
            segments = next;
        }

        return segments;
    }
}

/// <summary>
/// A straight wall centreline, or an arc fallback keyed to a single wall. Openings are
/// measured along it so that joined collinear walls and stacked-wall members are treated
/// as one continuous run.
/// </summary>
public sealed class WallAxis
{
    public XYZ Origin { get; }

    /// <summary>Null for non-linear walls.</summary>
    public XYZ? Direction { get; }

    public Curve Curve { get; }
    public long WallId { get; }

    private WallAxis(XYZ origin, XYZ? direction, Curve curve, long wallId)
    {
        Origin = origin;
        Direction = direction;
        Curve = curve;
        WallId = wallId;
    }

    /// <summary>Distance of a point along the wall.</summary>
    public double UOf(XYZ point)
    {
        if (Direction is not null) return (point - Origin).DotProduct(Direction);

        var projection = Curve.Project(point);
        return Curve.ComputeNormalizedParameter(projection.Parameter) * Curve.Length;
    }

    public XYZ TangentAt(XYZ point)
    {
        if (Direction is not null) return Direction;

        var projection = Curve.Project(point);
        return Curve.ComputeDerivatives(projection.Parameter, false).BasisX.Normalize();
    }

    public static WallAxis? Of(Wall wall)
    {
        if (wall.Location is not LocationCurve { Curve: { } curve }) return null;

        if (curve is Line line)
        {
            var direction = line.Direction.Normalize();

            // Canonical sense, so two walls drawn in opposite directions still match.
            foreach (var component in new[] { direction.X, direction.Y, direction.Z })
            {
                if (Math.Abs(component) > 1e-9)
                {
                    if (component < 0) direction = direction.Negate();
                    break;
                }
            }

            return new WallAxis(curve.GetEndPoint(0), direction, curve, wall.Id.Value);
        }

        return new WallAxis(curve.GetEndPoint(0), null, curve, wall.Id.Value);
    }

    /// <summary>True when two axes are the same infinite line within the tolerance.</summary>
    public static bool SameLine(WallAxis a, WallAxis b, double toleranceFeet)
    {
        if (a.Direction is null || b.Direction is null) return false;
        if (Math.Abs(a.Direction.DotProduct(b.Direction)) < 0.9999) return false;

        var delta = b.Origin - a.Origin;
        var perpendicular = delta - a.Direction.Multiply(delta.DotProduct(a.Direction));
        return perpendicular.GetLength() <= toleranceFeet;
    }
}
