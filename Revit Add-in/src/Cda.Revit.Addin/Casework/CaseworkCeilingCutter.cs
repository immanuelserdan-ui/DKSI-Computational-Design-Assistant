using System.Globalization;
using Autodesk.Revit.DB;
using Cda.Revit.Addin.Finishes;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Casework;

/// <summary>
/// Opens any ceiling that a casework unit's own body passes through - the tall unit standing
/// higher than a low ceiling - so the two stop occupying the same space.
///
/// WHY AN OPENING AND NOT A CUT. <see cref="CaseworkVoidCutter"/> can only apply voids authored in
/// a family. Revit's solid-solid cut would use the unit's body, but it works only between family
/// instances, and a ceiling is not one - the API says so in as many words. What Revit does allow
/// is an Opening in a ceiling, so that is what this places: one per unit per ceiling, stamped as
/// this tool's own and sourced to the unit.
///
/// SHAPE. The unit's outline in its OWN axes - the rectangle it occupies inside the ceiling's
/// thickness, rotated with the unit - clipped to the ceiling's boundary. A rectangle rather than
/// the exact silhouette because a cabinet is one, because that is what a person would sketch, and
/// because booleans on a family's many touching solids (carcass, doors, handles) are exactly
/// where Revit's geometry engine fails.
///
/// KEPT IN STEP WITH THE UNIT. Measured against the ceiling's SKETCH, never its current geometry:
/// once the opening exists the ceiling has a hole there, and measuring the hole would conclude
/// the unit no longer reaches the ceiling and remove the opening on the next pass. Every pass
/// compares what the unit needs now with the openings it already has, and only replaces them
/// when they differ - so moving a unit moves its opening, and a unit deleted or lowered out of
/// the ceiling loses it.
/// </summary>
internal sealed class CaseworkCeilingCutter
{
    public const string Tool = "DKSI casework ceiling opening";

    private const char Separator = '|';

    /// <summary>~0.3 mm. Loops are chained and profiles compared within this.</summary>
    private const double Tolerance = 0.001;

    private readonly Document _doc;
    private readonly CaseworkSettings _settings;
    private readonly List<IReadOnlyList<string>> _rows;
    private readonly List<string> _warnings;
    private readonly FinishGeometry _geometry;
    private readonly double _minimum;

    private readonly Dictionary<long, Solid?> _ceilingPrisms = [];
    private readonly HashSet<long> _ceilingsWarned = [];

    public int Created { get; private set; }
    public int Removed { get; private set; }
    public int Kept { get; private set; }
    public int Refused { get; private set; }

    /// <summary>Ceilings whose openings changed this pass - their rooms' ceiling areas are now stale.</summary>
    public HashSet<ElementId> ChangedCeilings { get; } = [];

    public CaseworkCeilingCutter(
        Document doc, CaseworkSettings settings, List<IReadOnlyList<string>> rows, List<string> warnings)
    {
        _doc = doc;
        _settings = settings;
        _rows = rows;
        _warnings = warnings;
        _geometry = new FinishGeometry(doc);
        _minimum = Measure.FromMillimetres(settings.CeilingOverlapMinimumMm);
    }

    private sealed record Existing(Opening Opening, string Source, string Ceiling, string Signature);

    private sealed record Wanted(Ceiling Ceiling, string Signature, List<Curve> Profile);

    /// <param name="scope">The units to bring up to date, or null/empty for every unit in the model.</param>
    public void Run(IReadOnlyList<Element>? scope)
    {
        var wholeModel = scope is not { Count: > 0 };

        IEnumerable<Element> source = wholeModel
            ? new FilteredElementCollector(_doc)
                .WherePasses(new ElementMulticategoryFilter(_settings.FittingCategories))
                .WhereElementIsNotElementType()
            : scope!;

        var units = source
            .OfType<FamilyInstance>()
            .Where(IsManaged)
            .ToList();

        var existing = StampedOpenings();

        // Orphans first, on every pass: the unit or the ceiling is gone, so the hole describes
        // nothing. Revit removes an opening with its host ceiling, never with the unit.
        foreach (var orphan in existing.Where(e => _doc.GetElement(e.Source) is null).ToList())
        {
            if (Delete(orphan, "opening removed", "its casework unit no longer exists")) existing.Remove(orphan);
        }

        var managed = units.Select(u => u.UniqueId).ToHashSet(StringComparer.Ordinal);
        var byUnit = existing
            .Where(e => managed.Contains(e.Source))
            .GroupBy(e => e.Source, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var unit in units)
        {
            try
            {
                Reconcile(unit, Needs(unit), byUnit.GetValueOrDefault(unit.UniqueId) ?? []);
            }
            catch (Exception ex)
            {
                _warnings.Add($"Ceiling opening for casework {unit.Id.Value} could not be updated: {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------ reconcile

    private void Reconcile(FamilyInstance unit, List<Wanted> wanted, List<Existing> has)
    {
        var wantedKeys = wanted.Select(w => Key(w.Ceiling.UniqueId, w.Signature)).ToHashSet(StringComparer.Ordinal);
        var hasKeys = has.Select(h => Key(h.Ceiling, h.Signature)).ToHashSet(StringComparer.Ordinal);

        foreach (var keep in has.Where(h => wantedKeys.Contains(Key(h.Ceiling, h.Signature))))
        {
            Kept++;
            Row(unit, _doc.GetElement(keep.Ceiling), "opening kept", "already matches the unit");
        }

        foreach (var stale in has.Where(h => !wantedKeys.Contains(Key(h.Ceiling, h.Signature))))
        {
            Delete(stale, "opening removed",
                   "the unit moved, changed or no longer reaches this ceiling", unit);
        }

        foreach (var need in wanted.Where(w => !hasKeys.Contains(Key(w.Ceiling.UniqueId, w.Signature))))
            Create(unit, need);
    }

    private void Create(FamilyInstance unit, Wanted need)
    {
        if (!Worksharing.CanWrite(_doc, need.Ceiling.Id))
        {
            Refused++;
            Row(unit, need.Ceiling, "refused", "the ceiling is checked out by another user");
            return;
        }

        try
        {
            var profile = new CurveArray();
            foreach (var curve in need.Profile) profile.Append(curve);

            // False: cut vertically, straight through the ceiling, which is how a unit stands.
            var opening = _doc.Create.NewOpening(need.Ceiling, profile, false);

            // AN UNSTAMPED OPENING IS WORSE THAN NONE. Nothing could ever find it again to move
            // or remove it, so it would outlive the unit it was made for.
            if (!ElementStamp.Write(opening, Tool, Key(need.Ceiling.UniqueId, need.Signature), unit.UniqueId))
            {
                _doc.Delete(opening.Id);
                Refused++;
                Row(unit, need.Ceiling, "refused", "the opening could not be marked as this tool's, so it was not kept");
                return;
            }

            Created++;
            ChangedCeilings.Add(need.Ceiling.Id);
            Row(unit, need.Ceiling, "CEILING OPENING", "the unit passes through this ceiling");
        }
        catch (Exception ex)
        {
            Refused++;
            Row(unit, need.Ceiling, "refused", $"Revit would not open the ceiling: {Trim(ex.Message)}");
        }
    }

    private bool Delete(Existing item, string outcome, string why, Element? unit = null)
    {
        var ceiling = _doc.GetElement(item.Ceiling);

        if (!Worksharing.CanWrite(_doc, item.Opening.Id) ||
            (ceiling is not null && !Worksharing.CanWrite(_doc, ceiling.Id)))
        {
            Refused++;
            _warnings.Add($"Ceiling opening {item.Opening.Id.Value} should be removed ({why}) but is " +
                          "checked out by another user.");
            return false;
        }

        try
        {
            _doc.Delete(item.Opening.Id);
            Removed++;
            if (ceiling is not null) ChangedCeilings.Add(ceiling.Id);
            if (unit is not null) Row(unit, ceiling, outcome, why);
            else _rows.Add([string.Empty, string.Empty, string.Empty, item.Opening.Id.Value.ToString(), "ceiling opening", outcome, why]);
            return true;
        }
        catch (Exception ex)
        {
            _warnings.Add($"Ceiling opening {item.Opening.Id.Value} could not be removed: {Trim(ex.Message)}");
            return false;
        }
    }

    // ------------------------------------------------------------------ what a unit needs

    private List<Wanted> Needs(FamilyInstance unit)
    {
        var wanted = new List<Wanted>();

        var box = unit.get_BoundingBox(null);
        if (box is null) return wanted;

        var ceilings = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Ceilings)
            .WhereElementIsNotElementType()
            .WherePasses(new BoundingBoxIntersectsFilter(new Outline(box.Min, box.Max)))
            .OfType<Ceiling>();

        foreach (var ceiling in ceilings)
        {
            foreach (var profile in Profiles(unit, box, ceiling))
                wanted.Add(new Wanted(ceiling, Signature(profile), profile));
        }

        return wanted;
    }

    /// <summary>The outline(s) the unit needs cut out of one ceiling, at the ceiling's underside.</summary>
    private IEnumerable<List<Curve>> Profiles(FamilyInstance unit, BoundingBoxXYZ unitBox, Ceiling ceiling)
    {
        var ceilingBox = ceiling.get_BoundingBox(null);
        if (ceilingBox is null) yield break;

        var bottom = ceilingBox.Min.Z;
        var top = ceilingBox.Max.Z;

        // Reaching the ceiling is not the same as passing into it.
        if (unitBox.Max.Z < bottom + _minimum || unitBox.Min.Z > top - _minimum) yield break;

        var prism = CeilingPrism(ceiling, bottom, top);
        if (prism is null) yield break;

        // THE UNIT'S EXTENT INSIDE THE CEILING, IN THE UNIT'S OWN AXES.
        var toLocal = unit.GetTransform().Inverse;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

        foreach (var solid in _geometry.ElementSolids(unit))
        {
            if (solid.Volume < 1e-9) continue;

            Solid? inside;
            try
            {
                inside = BooleanOperationsUtils.ExecuteBooleanOperation(solid, prism, BooleanOperationsType.Intersect);
            }
            catch
            {
                continue;   // one unreadable part of a family does not decide the whole unit
            }

            if (inside is null || inside.Volume < 1e-9) continue;

            foreach (Edge edge in inside.Edges)
            {
                foreach (var point in edge.Tessellate())
                {
                    var local = toLocal.OfPoint(point);
                    minX = Math.Min(minX, local.X);
                    minY = Math.Min(minY, local.Y);
                    maxX = Math.Max(maxX, local.X);
                    maxY = Math.Max(maxY, local.Y);
                }
            }
        }

        if (minX > maxX || maxX - minX < _minimum || maxY - minY < _minimum) yield break;

        var toWorld = unit.GetTransform();
        var z = bottom - Tolerance;

        XYZ Corner(double x, double y)
        {
            var world = toWorld.OfPoint(new XYZ(x, y, 0));
            return new XYZ(world.X, world.Y, z);
        }

        var corners = new[] { Corner(minX, minY), Corner(maxX, minY), Corner(maxX, maxY), Corner(minX, maxY) };
        var rectangle = new CurveLoop();
        for (var i = 0; i < 4; i++) rectangle.Append(Line.CreateBound(corners[i], corners[(i + 1) % 4]));

        // Clipped to the ceiling, so the opening never reaches past the ceiling's own edge.
        Solid? overlap;
        try
        {
            var footprint = GeometryCreationUtilities.CreateExtrusionGeometry(
                [rectangle], XYZ.BasisZ, (top - bottom) + (2 * Tolerance));
            overlap = BooleanOperationsUtils.ExecuteBooleanOperation(footprint, prism, BooleanOperationsType.Intersect);
        }
        catch (Exception ex)
        {
            _warnings.Add($"Casework {unit.Id.Value} / ceiling {ceiling.Id.Value}: overlap could not be measured - {Trim(ex.Message)}");
            yield break;
        }

        if (overlap is null || overlap.Volume < 1e-9) yield break;

        var basePlane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, bottom));

        foreach (var lump in SolidUtils.SplitVolumes(overlap))
        {
            if (lump.Volume < 1e-9) continue;

            List<Curve>? outline = null;

            try
            {
                var shadow = ExtrusionAnalyzer.Create(lump, basePlane, XYZ.BasisZ).GetExtrusionBase();
                var outer = shadow.GetEdgesAsCurveLoops().OrderByDescending(PlanArea).FirstOrDefault();

                if (outer is not null && PlanArea(outer) >= _minimum * _minimum)
                    outline = [.. outer.Select(c => Flatten(c, bottom))];
            }
            catch (Exception ex)
            {
                _warnings.Add($"Casework {unit.Id.Value} / ceiling {ceiling.Id.Value}: outline could not be taken - {Trim(ex.Message)}");
            }

            if (outline is not null) yield return outline;
        }
    }

    /// <summary>
    /// The ceiling's volume as its SKETCH describes it - boundary and its own sketched holes, over
    /// its thickness - ignoring any Opening elements. See the class note on why that matters.
    /// </summary>
    private Solid? CeilingPrism(Ceiling ceiling, double bottom, double top)
    {
        if (_ceilingPrisms.TryGetValue(ceiling.Id.Value, out var cached)) return cached;

        Solid? prism = null;

        try
        {
            if (_doc.GetElement(ceiling.SketchId) is Sketch sketch)
            {
                var loops = new List<CurveLoop>();

                foreach (CurveArray array in sketch.Profile)
                {
                    var curves = new List<Curve>();
                    foreach (Curve curve in array) curves.Add(Flatten(curve, bottom - Tolerance));

                    var loop = Chain(curves);
                    if (loop is not null) loops.Add(loop);
                }

                if (loops.Count > 0)
                    prism = GeometryCreationUtilities.CreateExtrusionGeometry(
                        loops, XYZ.BasisZ, (top - bottom) + (2 * Tolerance));
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Ceiling {ceiling.Id.Value} sketch unreadable: {ex.Message}");
            prism = null;
        }

        if (prism is null && _ceilingsWarned.Add(ceiling.Id.Value))
        {
            _warnings.Add($"Ceiling {ceiling.Id.Value}: its boundary sketch could not be read, so casework " +
                          "passing through it was not checked.");
        }

        _ceilingPrisms[ceiling.Id.Value] = prism;
        return prism;
    }

    /// <summary>Sketch curves come in no promised order or direction; a CurveLoop needs both.</summary>
    private static CurveLoop? Chain(List<Curve> curves)
    {
        if (curves.Count == 0) return null;

        var remaining = new List<Curve>(curves);
        var ordered = new List<Curve> { remaining[0] };
        remaining.RemoveAt(0);

        while (remaining.Count > 0)
        {
            var end = ordered[^1].GetEndPoint(1);
            var index = remaining.FindIndex(c =>
                c.GetEndPoint(0).DistanceTo(end) < Tolerance || c.GetEndPoint(1).DistanceTo(end) < Tolerance);

            if (index < 0) return null;

            var next = remaining[index];
            remaining.RemoveAt(index);
            ordered.Add(next.GetEndPoint(0).DistanceTo(end) < Tolerance ? next : next.CreateReversed());
        }

        var loop = new CurveLoop();
        foreach (var curve in ordered) loop.Append(curve);
        return loop;
    }

    private static Curve Flatten(Curve curve, double z) =>
        curve.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, z - curve.GetEndPoint(0).Z)));

    private static double PlanArea(CurveLoop loop)
    {
        var points = loop.SelectMany(c => c.Tessellate().Take(c.Tessellate().Count - 1)).ToList();
        var area = 0.0;

        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            area += (a.X * b.Y) - (b.X * a.Y);
        }

        return Math.Abs(area) / 2.0;
    }

    /// <summary>The outline's corners to the millimetre, order-independent: same shape, same text.</summary>
    private static string Signature(List<Curve> profile) =>
        string.Join(";", profile
            .Select(c => c.GetEndPoint(0))
            .Select(p => string.Create(CultureInfo.InvariantCulture,
                $"{Math.Round(Measure.ToMillimetres(p.X))},{Math.Round(Measure.ToMillimetres(p.Y))},{Math.Round(Measure.ToMillimetres(p.Z))}"))
            .Order(StringComparer.Ordinal));

    private static string Key(string ceiling, string signature) => ceiling + Separator + signature;

    // ------------------------------------------------------------------ bookkeeping

    private List<Existing> StampedOpenings()
    {
        var found = new List<Existing>();
        var stamped = ElementStamp.Filter();
        if (stamped is null) return found;

        foreach (var opening in new FilteredElementCollector(_doc)
                     .OfClass(typeof(Opening))
                     .WherePasses(stamped)
                     .OfType<Opening>())
        {
            var tag = ElementStamp.Read(opening, Tool, Tool + ":");
            var source = ElementStamp.ReadSource(opening, Tool);
            if (tag is null || source is null) continue;

            var split = tag.IndexOf(Separator);
            if (split < 0) continue;

            found.Add(new Existing(opening, source, tag[..split], tag[(split + 1)..]));
        }

        return found;
    }

    private bool IsManaged(FamilyInstance unit)
    {
        var category = unit.Category?.Id.Value;
        if (category is null || !_settings.FittingCategories.Any(c => (long)c == category)) return false;

        if (string.IsNullOrWhiteSpace(_settings.SkipComment)) return true;

        var comments = unit.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
        return comments is null || !comments.Contains(_settings.SkipComment, StringComparison.OrdinalIgnoreCase);
    }

    private void Row(Element unit, Element? ceiling, string outcome, string detail)
    {
        var symbol = (unit as FamilyInstance)?.Symbol;

        _rows.Add(
        [
            unit.Id.Value.ToString(),
            symbol?.Family?.Name ?? string.Empty,
            symbol?.Name ?? string.Empty,
            ceiling?.Id.Value.ToString() ?? string.Empty,
            ceiling is null ? "(ceiling gone)" : _doc.GetElement(ceiling.GetTypeId())?.Name ?? ceiling.Name,
            outcome,
            detail,
        ]);
    }

    private static string Trim(string message)
    {
        var line = message.Replace("\r", " ").Replace("\n", " ").Trim();
        return line.Length <= 160 ? line : line[..157] + "...";
    }
}
