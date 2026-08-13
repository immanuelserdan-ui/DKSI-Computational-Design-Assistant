using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace Cda.Revit.Addin.Sweeps;

/// <summary>How this family can be put in the model. Resolved once, from the family itself.</summary>
public enum SkirtingStrategy
{
    /// <summary>Line-based: one instance stretches along the whole run. The good case.</summary>
    CurveDriven,

    /// <summary>Face-based, positioned by a line on the wall's room-side face.</summary>
    FaceLine,

    /// <summary>Face-based, positioned by a point. Length driven by parameter if it has one.</summary>
    FacePoint,

    /// <summary>Free-standing point placement, rotated to the run. The weak case.</summary>
    PointAndRotate,
}

/// <summary>
/// Puts one skirting instance on one run, adapting to how the family was authored.
///
/// WHY THIS IS A SEPARATE CLASS
///   "Place a component along a line" is four different API calls depending on whether the
///   family is line-based, face-based, or free-standing, and a skirting family could
///   plausibly be any of them. Rather than demand the family be authored one way - or ask
///   and stall - the strategy is read off <see cref="Family.FamilyPlacementType"/> and the
///   right call made. The engine above stays about WHERE boards go; this is about HOW.
///
/// THE HONEST RANKING
///   CurveDriven is the only one that needs no compensation: the instance IS the run. The
///   others place something and then try to make it the right length, which depends on the
///   family exposing a length parameter. Where that fails, the report says so rather than
///   leaving a wrong-length board looking deliberate.
/// </summary>
public sealed class SkirtingPlacer
{
    private readonly Document _doc;
    private readonly FamilySymbol _symbol;

    /// <summary>
    /// Room-side face references, resolved on demand and reused.
    ///
    /// KEYED ON THE WALL AND THE SIDE, NOT THE WALL ALONE. A wall between two qualifying
    /// rooms gets a board on each face - that is the brief - so a cache keyed on the wall
    /// handed the second room the face reference resolved for the first. With a face-hosted
    /// family that puts every board in one of the two rooms on the wrong side of the wall,
    /// displaced by its full thickness, while the distance test that would have caught it ran
    /// exactly once per wall and never again.
    /// </summary>
    private readonly Dictionary<(long Wall, int Side), Reference?> _faceCache = [];

    /// <summary>Length parameter names seen in the wild, English and Danish.</summary>
    private static readonly string[] LengthNames =
        ["Length", "Længde", "Laengde", "Sweep Length", "Board Length", "Run Length"];

    public SkirtingPlacer(Document doc, FamilySymbol symbol)
    {
        _doc = doc;
        _symbol = symbol;
        Strategy = Resolve(symbol);
    }

    public SkirtingStrategy Strategy { get; }

    /// <summary>True when the strategy needs no length compensation.</summary>
    public bool IsExact => Strategy == SkirtingStrategy.CurveDriven ||
                           Strategy == SkirtingStrategy.FaceLine;

    private static SkirtingStrategy Resolve(FamilySymbol symbol)
    {
        FamilyPlacementType placement;
        try { placement = symbol.Family.FamilyPlacementType; }
        catch { return SkirtingStrategy.PointAndRotate; }

        return placement switch
        {
            FamilyPlacementType.CurveBased => SkirtingStrategy.CurveDriven,

            // Both host onto a face. Revit accepts a Line position for either, and a
            // line-driven face family is the common way a skirting board is authored, so
            // FaceLine is tried first and falls back to FacePoint per placement.
            FamilyPlacementType.WorkPlaneBased => SkirtingStrategy.FaceLine,
            FamilyPlacementType.OneLevelBasedHosted => SkirtingStrategy.FaceLine,

            _ => SkirtingStrategy.PointAndRotate,
        };
    }

    /// <summary>
    /// Places one board. Returns the instance, or null with the reason in
    /// <paramref name="failure"/>.
    /// </summary>
    /// <param name="host">
    /// What the board is being placed against. A wall for the face strategies to host onto;
    /// null, a column or any other boundary element falls through to point placement, which
    /// is how a room bounded by something other than a wall still gets its board.
    /// </param>
    public FamilyInstance? Place(Curve run, Element? host, Level? level, out string? failure)
    {
        failure = null;

        try
        {
            var instance = Strategy switch
            {
                SkirtingStrategy.CurveDriven => PlaceOnCurve(run, level),
                SkirtingStrategy.FaceLine => PlaceOnFace(run, host, level),
                SkirtingStrategy.FacePoint => PlaceOnFace(run, host, level),
                _ => PlaceAndRotate(run, level),
            };

            if (instance is null)
            {
                failure = "Revit returned no instance.";
                return null;
            }

            if (!IsExact) DriveLength(instance, run.Length);

            return instance;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return null;
        }
    }

    // ------------------------------------------------------------------ strategies

    private FamilyInstance? PlaceOnCurve(Curve run, Level? level)
    {
        if (level is null) return null;

        return _doc.Create.NewFamilyInstance(run, _symbol, level, StructuralType.NonStructural);
    }

    /// <summary>
    /// Hosts on the wall's room-side face. Tries the Line overload first - that is what
    /// stretches a line-driven face family across the run - and drops to the point overload
    /// for families that only accept a location.
    /// </summary>
    private FamilyInstance? PlaceOnFace(Curve run, Element? host, Level? level)
    {
        // Only a wall exposes side faces to host onto. A column bounding a room is placed by
        // point and rotation instead - a board on it rather than no board at all.
        var face = host is Wall wall ? RoomSideFace(wall, run) : null;

        if (face is null)
        {
            // No usable face reference: fall back rather than lose the board entirely.
            return PlaceAndRotate(run, level);
        }

        if (Strategy == SkirtingStrategy.FaceLine && run is Line line)
        {
            try
            {
                return _doc.Create.NewFamilyInstance(face, line, _symbol);
            }
            catch
            {
                // Family will not take a line position; the point overload below still works.
            }
        }

        var start = run.GetEndPoint(0);
        var direction = (run.GetEndPoint(1) - start).Normalize();

        return _doc.Create.NewFamilyInstance(face, run.Evaluate(0.5, true), direction, _symbol);
    }

    /// <summary>
    /// Last resort: place at the run's midpoint and spin it to match the run's direction.
    /// Only correct if the family's own length can then be driven to match.
    /// </summary>
    private FamilyInstance? PlaceAndRotate(Curve run, Level? level)
    {
        var mid = run.Evaluate(0.5, true);

        var instance = level is not null
            ? _doc.Create.NewFamilyInstance(mid, _symbol, level, StructuralType.NonStructural)
            : _doc.Create.NewFamilyInstance(mid, _symbol, StructuralType.NonStructural);

        if (instance is null) return null;

        var direction = run.GetEndPoint(1) - run.GetEndPoint(0);
        var angle = Math.Atan2(direction.Y, direction.X);

        if (Math.Abs(angle) > 1e-9)
        {
            // Families are placed along +X. Rotating about the insertion point keeps the
            // board centred on the run rather than swinging its end away.
            var axis = Line.CreateBound(mid, mid + XYZ.BasisZ);
            ElementTransformUtils.RotateElement(_doc, instance.Id, axis, angle);
        }

        return instance;
    }

    // ----------------------------------------------------------------- face lookup

    /// <summary>
    /// The wall face the run lies on, chosen by distance rather than by reasoning about
    /// which shell layer faces the room.
    ///
    /// Interior/Exterior in Revit's sense is about the wall's own orientation, not about
    /// rooms - a wall's "Interior" face is outdoors as often as not. The run curve already
    /// sits on the correct face, having come from the room's Finish boundary, so the honest
    /// test is simply which candidate face the run is nearest to.
    /// </summary>
    private Reference? RoomSideFace(Wall wall, Curve run)
    {
        var key = (wall.Id.Value, SideOf(wall, run));

        if (_faceCache.TryGetValue(key, out var cached) && cached is not null) return cached;

        Reference? best = null;
        var bestDistance = double.MaxValue;
        var probe = run.Evaluate(0.5, true);

        foreach (var side in new[] { ShellLayerType.Interior, ShellLayerType.Exterior })
        {
            IList<Reference> references;
            try { references = HostObjectUtils.GetSideFaces(wall, side); }
            catch { continue; }

            foreach (var reference in references)
            {
                try
                {
                    if (_doc.GetElement(reference)?.GetGeometryObjectFromReference(reference) is not Face face)
                        continue;

                    var result = face.Project(probe);
                    if (result is null) continue;

                    if (result.Distance < bestDistance)
                    {
                        bestDistance = result.Distance;
                        best = reference;
                    }
                }
                catch
                {
                    // Unresolvable face reference; try the next.
                }
            }
        }

        _faceCache[key] = best;
        return best;
    }

    /// <summary>
    /// Which side of the wall this run lies on: +1 towards <see cref="Wall.Orientation"/>,
    /// -1 away from it. Only ever used to keep the two sides of one wall in separate cache
    /// entries, so the labelling matters less than the fact that it separates them.
    /// </summary>
    private static int SideOf(Wall wall, Curve run)
    {
        try
        {
            if (wall.Location is not LocationCurve location) return 0;

            var here = run.Evaluate(0.5, true);
            var onCentreline = location.Curve.Project(here)?.XYZPoint;

            if (onCentreline is null) return 0;

            var offset = new XYZ(here.X - onCentreline.X, here.Y - onCentreline.Y, 0);
            if (offset.GetLength() < 1e-9) return 0;

            return offset.Normalize().DotProduct(wall.Orientation.Normalize()) > 0 ? 1 : -1;
        }
        catch
        {
            return 0;
        }
    }

    // --------------------------------------------------------------- length driving

    /// <summary>
    /// Makes a non-curve-driven instance the right length, if the family lets us.
    ///
    /// Silent failure here is the dangerous outcome: a board placed at its family's default
    /// length looks entirely deliberate in a view and is simply wrong in a schedule. The
    /// caller reports whenever this could not be done.
    /// </summary>
    public bool DriveLength(FamilyInstance instance, double length)
    {
        var builtIn = instance.get_Parameter(BuiltInParameter.INSTANCE_LENGTH_PARAM);

        if (builtIn is { IsReadOnly: false, StorageType: StorageType.Double })
        {
            try
            {
                builtIn.Set(length);
                return true;
            }
            catch
            {
                // Not writable on this family after all.
            }
        }

        foreach (var name in LengthNames)
        {
            var parameter = instance.LookupParameter(name);

            if (parameter is { IsReadOnly: false, StorageType: StorageType.Double })
            {
                try
                {
                    parameter.Set(length);
                    return true;
                }
                catch
                {
                    // Try the next candidate name.
                }
            }
        }

        return false;
    }

    // ------------------------------------------------------------------- mitring

    /// <summary>
    /// Writes the end-cut angles that turn a butt joint into a mitre.
    ///
    /// Returns false when the family exposes no writable angle parameter, which is the
    /// difference between "this family can mitre" and "this family cannot" - and the caller
    /// must know, because the corner strategy depends on it. A board that runs to the apex
    /// expecting to be cut, and is not, OVERLAPS its neighbour. Silence here would trade a
    /// visible step for an invisible clash, which is worse.
    /// </summary>
    /// <param name="startAngle">Radians, or null to leave that end square.</param>
    /// <param name="endAngle">Radians, or null to leave that end square.</param>
    public bool SetEndAngles(
        FamilyInstance instance,
        double? startAngle,
        double? endAngle,
        IReadOnlyList<string> startNames,
        IReadOnlyList<string> endNames)
    {
        var wroteStart = startAngle is null || Write(instance, startNames, startAngle.Value);
        var wroteEnd = endAngle is null || Write(instance, endNames, endAngle.Value);

        return wroteStart && wroteEnd;
    }

    /// <summary>
    /// Can this family be mitred at all? Answered from a real instance, because a parameter
    /// can exist on the type and still be read-only on the instance.
    /// </summary>
    public bool SupportsMitre(
        FamilyInstance probe, IReadOnlyList<string> startNames, IReadOnlyList<string> endNames) =>
        Find(probe, startNames) is not null && Find(probe, endNames) is not null;

    private static bool Write(FamilyInstance instance, IReadOnlyList<string> names, double radians)
    {
        var parameter = Find(instance, names);
        if (parameter is null) return false;

        try
        {
            return parameter.Set(radians);
        }
        catch
        {
            // Constrained or driven by a formula; the caller falls back to butting.
            return false;
        }
    }

    /// <summary>
    /// The first writable angle parameter matching any candidate name. Angles are stored in
    /// RADIANS in the API regardless of how the family displays them.
    /// </summary>
    private static Parameter? Find(FamilyInstance instance, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            Parameter? parameter;
            try { parameter = instance.LookupParameter(name); }
            catch { continue; }

            if (parameter is { IsReadOnly: false, StorageType: StorageType.Double }) return parameter;
        }

        return null;
    }

    /// <summary>What to tell the user about how this family will behave.</summary>
    public string Describe() => Strategy switch
    {
        SkirtingStrategy.CurveDriven =>
            "line-based: each instance stretches along its run, so lengths are exact.",

        SkirtingStrategy.FaceLine =>
            "face-based: each instance is hosted on the wall face along its run.",

        SkirtingStrategy.FacePoint =>
            "face-based point placement: length is driven by parameter where the family exposes one.",

        _ =>
            "point-based: instances are placed at each run's midpoint and rotated to match. " +
            "Lengths depend on the family exposing a writable length parameter - re-author it " +
            "as a line-based family if the boards come out the wrong size.",
    };
}
