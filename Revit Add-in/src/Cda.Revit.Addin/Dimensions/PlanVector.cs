using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Dimensions;

/// <summary>
/// The only bridge between Revit's XYZ and the plain arithmetic in <see cref="DimensionMath"/>.
///
/// Kept to two one-line conversions on purpose. Every line of judgement that lives on the
/// Revit side of this boundary is a line that cannot be tested without opening Revit, so the
/// boundary is drawn where nothing but "drop Z" and "put Z back" crosses it.
/// </summary>
internal static class PlanVector
{
    public static DimensionMath.Vec2 ToPlan(this XYZ point) => new(point.X, point.Y);

    public static XYZ ToWorld(this DimensionMath.Vec2 vector, double z) => new(vector.X, vector.Y, z);

    /// <summary>True when a direction has no plan component at all - a vertical edge.</summary>
    public static bool IsVertical(this XYZ direction) =>
        Math.Abs(direction.X) < DimensionMath.Epsilon && Math.Abs(direction.Y) < DimensionMath.Epsilon;
}
