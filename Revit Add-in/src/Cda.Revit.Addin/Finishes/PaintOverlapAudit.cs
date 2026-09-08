using System.Diagnostics;
using Autodesk.Revit.DB;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Schedules;

namespace Cda.Revit.Addin.Finishes;

/// <summary>Which tool placed a carrier. Two products place them, and that matters.</summary>
internal enum CarrierProduct
{
    /// <summary>This add-in's takeoff rows, found by extensible-storage stamp.</summary>
    Dksi,

    /// <summary>PaintedMaterialTakeoff's segment elements, found by DirectShape.ApplicationId.</summary>
    PaintedMaterialTakeoff,

    /// <summary>Right DirectShapeType, no stamp and no ApplicationId - an orphan or a hand copy.</summary>
    Untagged,
}

internal enum OverlapVerdict { Error, Review }

/// <summary>One pair of carriers that claim the same surface, and how much of it.</summary>
internal sealed record OverlapFinding(
    OverlapVerdict Verdict,
    string Check,
    long LeftId,
    long RightId,
    string LeftRoom,
    string RightRoom,
    string LeftMaterial,
    string RightMaterial,
    string LeftSegment,
    string RightSegment,
    double LeftAreaSqFt,
    double RightAreaSqFt,
    double OverlapSqFt,
    bool AreaEstimated,
    string Detail);

internal sealed class PaintOverlapResult
{
    public required IReadOnlyList<OverlapFinding> Findings { get; init; }
    public required int Carriers { get; init; }
    public required int SolidsTested { get; init; }

    /// <summary>Carriers with no geometry, which the geometric pass cannot reach.</summary>
    public required int WithoutGeometry { get; init; }

    public required int PairsTested { get; init; }
    public required int BooleanFailures { get; init; }
    public required IReadOnlyDictionary<CarrierProduct, int> ByProduct { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public required TimeSpan Elapsed { get; init; }

    public int Errors => Findings.Count(f => f.Verdict == OverlapVerdict.Error);
    public int Reviews => Findings.Count(f => f.Verdict == OverlapVerdict.Review);

    public double OverlapSqM => Measure.ToSquareMetres(
        Findings.Where(f => f.Verdict == OverlapVerdict.Error).Sum(f => f.OverlapSqFt));
}

/// <summary>
/// Finds paint takeoff carriers that claim the same surface twice - the geometric form of
/// double counting, tested against what is actually placed in the model.
///
/// WHY THIS IS NOT ALREADY COVERED
///   PaintedMaterialTakeoff ships a TakeoffValidator with an "over-attribution" check: it
///   groups paint records by (wall id, shell side) and errors when the rooms' areas sum past
///   that whole side of the wall. That is real and it stays. It cannot see three things:
///
///     * It is ARITHMETIC. Two rooms can each claim 2 m2 of the same 3 m2 patch and still
///       total under a 6 m2 wall side. The sum passes; the model still double counts.
///     * It runs on in-memory records during a takeoff, never on PLACED carriers. A carrier
///       left behind by an earlier run, or copied by hand, is invisible to it.
///     * It knows only its own product. Both add-ins place Generic Model carriers, and a
///       model that has had both run over it carries two complete sets - a 100% double count
///       neither engine can detect, because each sees only its own records.
///
/// WHAT IT DELIBERATELY DOES NOT FLAG
///   Two carriers on ONE wall are normal and correct. A wall between two rooms is painted on
///   both sides, each face belongs to the room it fronts, and the shared wall id in
///   'Paint Segment' is provenance rather than a host relationship. Measured in the test
///   model: carriers 29329476 and 29329492 both name wall 29317994, sit on planes 100 mm
///   apart, and belong to Udestue 18 and Kaelderrum 4 respectively. Correct, and this must
///   stay silent about it. That case is what the coplanarity gate exists for.
///
/// THE LIMIT WORTH KNOWING
///   This add-in's own carriers are DirectShapes with NO GEOMETRY - they are data rows, by
///   design (see PaintTakeoffBuilder). There is nothing to intersect, so the geometric pass
///   reaches PaintedMaterialTakeoff's carriers only. DKSI rows are checked by KEY instead:
///   two rows sharing room, surface and material are a duplicate whatever their geometry.
///   The result reports both counts so a clean run cannot be mistaken for a full one.
///
/// READ-ONLY. Nothing here opens a transaction or writes anything; the boolean operations
/// produce transient solids. That is deliberate - an audit that can corrupt the thing it is
/// auditing is worse than no audit, and paint data feeds a digital twin.
/// </summary>
internal sealed class PaintOverlapAudit
{
    /// <summary>DirectShape.ApplicationId PaintedMaterialTakeoff stamps on its segment elements.</summary>
    private const string PmtApplicationId = "PaintedMaterialTakeoff";

    /// <summary>The DirectShapeType both products name their carriers with.</summary>
    private const string CarrierTypeName = "Paint Takeoff Segment";

    /// <summary>
    /// How far apart two mid-planes may sit and still count as the same surface.
    ///
    /// LOAD-BEARING, and the number has to live between two real dimensions: the painted
    /// finish layer is 5 mm thick, and the thinnest wall in these models is 100 mm. Set it
    /// above ~50 mm and the two faces of one wall start comparing as coplanar, which is the
    /// exact false positive this class was written to avoid.
    /// </summary>
    private static readonly double PlaneToleranceFt = Measure.FromMillimetres(3.0);

    /// <summary>Slack on the bounding-box pre-filter, so a genuine touch is never rejected early.</summary>
    private static readonly double BoxToleranceFt = Measure.FromMillimetres(2.0);

    /// <summary>
    /// Below this an overlap is reported for Review rather than as an Error. 0.01 m2 is a
    /// 100 mm square: smaller than that on a building surface is a coincident edge, not a
    /// quantity anybody is paying for.
    /// </summary>
    private static readonly double OverlapFloorSqFt = Measure.FromSquareMetres(0.01);

    /// <summary>Numerical noise. Below this the boolean produced nothing worth naming.</summary>
    private const double VolumeEpsilon = 1e-9;

    private readonly Document _doc;
    private readonly List<string> _notes = [];

    public PaintOverlapAudit(Document doc) => _doc = doc;

    private sealed record Carrier(
        long Id,
        CarrierProduct Product,
        string Room,
        string RoomNumber,
        string Material,
        string Segment,
        double ReportedAreaSqFt);

    /// <summary>One solid of one carrier, with the plane it lies in and its world bounds.</summary>
    private sealed record Slab(
        Carrier Carrier,
        Solid Solid,
        XYZ Min,
        XYZ Max,
        XYZ? Normal,
        double Offset,
        double Thickness);

    public PaintOverlapResult Run()
    {
        var clock = Stopwatch.StartNew();

        var carrierTypeId = FindCarrierType();
        var carriers = new List<(Carrier Info, Element Element)>();

        foreach (var element in new FilteredElementCollector(_doc)
                     .OfClass(typeof(DirectShape))
                     .WhereElementIsNotElementType())
        {
            var product = Classify(element, carrierTypeId);
            if (product is null) continue;

            carriers.Add((Describe(element, product.Value), element));
        }

        var byProduct = carriers
            .GroupBy(c => c.Info.Product)
            .ToDictionary(g => g.Key, g => g.Count());

        // THE HEADLINE, AND IT COMES BEFORE ANY GEOMETRY. If both products have run over this
        // model then every surface is carried twice and no pairwise test is needed to know the
        // totals are doubled. Saying it first stops someone reading a per-pair list and missing
        // that the whole schedule is double.
        if (byProduct.ContainsKey(CarrierProduct.Dksi) &&
            byProduct.ContainsKey(CarrierProduct.PaintedMaterialTakeoff))
        {
            _notes.Add(
                $"BOTH TAKEOFFS ARE LIVE IN THIS MODEL: {byProduct[CarrierProduct.Dksi]} DKSI row(s) " +
                $"and {byProduct[CarrierProduct.PaintedMaterialTakeoff]} PaintedMaterialTakeoff " +
                "carrier(s). Any schedule collecting Generic Models by category counts every " +
                "surface twice. Clear one set before pricing from either.");
        }

        var slabs = new List<Slab>();
        var withoutGeometry = new List<Carrier>();

        foreach (var (info, element) in carriers)
        {
            var solids = new List<Solid>();

            try
            {
                Harvest(element, solids);
            }
            catch (Exception ex)
            {
                _notes.Add($"Carrier {info.Id}: geometry unreadable ({ex.Message}); not tested.");
                continue;
            }

            if (solids.Count == 0)
            {
                withoutGeometry.Add(info);
                continue;
            }

            foreach (var solid in solids)
            {
                var slab = Describe(info, solid);
                if (slab is not null) slabs.Add(slab);
            }
        }

        var findings = new List<OverlapFinding>();

        var (pairs, failures) = Intersect(slabs, findings);

        findings.AddRange(DuplicateKeys(withoutGeometry));

        if (withoutGeometry.Count > 0)
        {
            _notes.Add(
                $"{withoutGeometry.Count} carrier(s) hold no geometry, so the geometric pass could " +
                "not reach them - this add-in's own takeoff rows are data-only DirectShapes by " +
                "design. They were checked by key (room + surface + material) instead, which " +
                "catches duplicate rows but not partial overlap.");
        }

        if (failures > 0)
        {
            _notes.Add(
                $"{failures} pair(s) could not be tested: the boolean intersection failed on them. " +
                "They are listed as Review, NOT as clean - a test that did not run is not a pass.");
        }

        clock.Stop();

        var result = new PaintOverlapResult
        {
            Findings = findings
                .OrderByDescending(f => f.Verdict == OverlapVerdict.Error)
                .ThenByDescending(f => f.OverlapSqFt)
                .ToList(),
            Carriers = carriers.Count,
            SolidsTested = slabs.Count,
            WithoutGeometry = withoutGeometry.Count,
            PairsTested = pairs,
            BooleanFailures = failures,
            ByProduct = byProduct,
            Notes = _notes,
            Elapsed = clock.Elapsed,
        };

        Log.Info(
            $"Paint overlap audit: {result.Carriers} carrier(s), {result.SolidsTested} solid(s), " +
            $"{result.PairsTested} pair(s) tested, {result.Errors} error(s), {result.Reviews} review(s), " +
            $"{result.Elapsed.TotalSeconds:0.0}s.");

        return result;
    }

    // ------------------------------------------------------------------ identity

    private ElementId? FindCarrierType()
    {
        try
        {
            return new FilteredElementCollector(_doc)
                .OfClass(typeof(DirectShapeType))
                .Cast<DirectShapeType>()
                .FirstOrDefault(t => string.Equals(t.Name, CarrierTypeName, StringComparison.Ordinal))
                ?.Id;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Which product placed this, or null for a DirectShape that is not a carrier.
    ///
    /// THREE MARKS, NOT ONE. The whole point is catching a model where both products have
    /// run, so matching on either product's private mark alone would miss half the problem -
    /// and the type-name fallback catches a carrier whose stamp was lost to a copy/paste.
    /// </summary>
    private static CarrierProduct? Classify(Element element, ElementId? carrierTypeId)
    {
        try
        {
            if (element is DirectShape ds &&
                string.Equals(ds.ApplicationId, PmtApplicationId, StringComparison.Ordinal))
            {
                return CarrierProduct.PaintedMaterialTakeoff;
            }
        }
        catch
        {
            // ApplicationId throws on some DirectShapes; fall through to the other marks.
        }

        try
        {
            if (ElementStamp.Read(element, PaintTakeoffBuilder.Stamp, PaintTakeoffBuilder.Stamp) is not null)
                return CarrierProduct.Dksi;
        }
        catch
        {
            // Unreadable storage; fall through.
        }

        return carrierTypeId is not null && element.GetTypeId() == carrierTypeId
            ? CarrierProduct.Untagged
            : null;
    }

    /// <summary>
    /// Reads the row's identity. BOTH PRODUCTS' PARAMETER NAMES, first non-empty wins: PMT
    /// writes "Room Name"/"Paint Material Name", this add-in writes "Rum"/"Paint Material",
    /// and a report that showed one product's rows with blank rooms would be useless for the
    /// case the audit exists to catch.
    /// </summary>
    private static Carrier Describe(Element element, CarrierProduct product)
    {
        var settings = new FinishSettings();

        return new Carrier(
            element.Id.Value,
            product,
            Text(element, "Room Name", settings.RoomNameParameter),
            Text(element, "Room Number", settings.RoomNumberParameter),
            Text(element, "Paint Material Name", settings.PaintMaterialParameter),
            SegmentKeyOf(element, settings),
            Number(element, "Painted Surface Area", settings.PaintAreaParameter));
    }

    /// <summary>
    /// The row's surface identity, which is the duplicate key for carriers with no geometry.
    ///
    /// THE TWO PRODUCTS DO NOT STORE THIS THE SAME WAY, and taking the first non-empty of the
    /// three parameters - what this used to do - was wrong in a way that would have reported
    /// correct models as broken. PMT writes one 'Paint Segment' string that already names the
    /// host and the face. This add-in has no such parameter: it writes 'Paint Surface'
    /// ("Walls") and 'Paint Host Id' SEPARATELY. Falling through to 'Paint Surface' alone gave
    /// every wall row in a room the identical key "Walls", so a room with four painted wall
    /// faces - the exact case the DKSI takeoff exists to produce - came back as three
    /// duplicate rows. They have to be combined.
    ///
    /// With neither available the element's own id is used, which cannot collide with
    /// anything. An unidentifiable carrier should fail to a false negative, never a false
    /// positive: this tool's whole value is that a finding means something.
    /// </summary>
    private static string SegmentKeyOf(Element element, FinishSettings settings)
    {
        var segment = Text(element, "Paint Segment");
        if (!string.IsNullOrWhiteSpace(segment)) return segment;

        var surface = Text(element, settings.PaintSurfaceParameter);
        var host = Text(element, settings.PaintHostParameter);

        if (string.IsNullOrWhiteSpace(surface) && string.IsNullOrWhiteSpace(host))
            return $"?{element.Id.Value}";

        return string.IsNullOrWhiteSpace(host) ? surface : $"{surface} #{host}";
    }

    private static string Text(Element element, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var value = ParameterHelper.Find(element, name)?.AsString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch
            {
                // Try the next name.
            }
        }

        return string.Empty;
    }

    private static double Number(Element element, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var parameter = ParameterHelper.Find(element, name);

                if (parameter is not null && parameter.StorageType == StorageType.Double)
                {
                    var value = parameter.AsDouble();
                    if (value > 0) return value;
                }
            }
            catch
            {
                // Try the next name.
            }
        }

        return 0;
    }

    // ------------------------------------------------------------------ geometry

    private static void Harvest(Element element, List<Solid> into)
    {
        var options = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = false,
            DetailLevel = ViewDetailLevel.Fine,
        };

        var geometry = element.get_Geometry(options);
        if (geometry is not null) Flatten(geometry, into);
    }

    /// <summary>
    /// A carrier's shape is a LIST of GeometryObjects - DirectShape.SetShape takes one - so a
    /// single row can be several disjoint regions. Each is tested separately; collapsing them
    /// into one would lose exactly the case where one region overlaps and another does not.
    /// </summary>
    private static void Flatten(GeometryElement geometry, List<Solid> into)
    {
        foreach (var item in geometry)
        {
            switch (item)
            {
                case Solid solid when solid.Volume > VolumeEpsilon && solid.Faces.Size > 0:
                    into.Add(solid);
                    break;

                case GeometryInstance instance:
                    var nested = instance.GetInstanceGeometry();
                    if (nested is not null) Flatten(nested, into);
                    break;
            }
        }
    }

    private static Slab? Describe(Carrier carrier, Solid solid)
    {
        var bounds = WorldBounds(solid);
        if (bounds is null) return null;

        var (normal, offset, thickness) = PlaneOf(solid);

        return new Slab(carrier, solid, bounds.Value.Min, bounds.Value.Max, normal, offset, thickness);
    }

    /// <summary>
    /// World-space bounds from the solid's own edges.
    ///
    /// NOT Solid.GetBoundingBox(), which returns a box in the solid's LOCAL frame with a
    /// transform beside it. Comparing two of those directly is wrong the moment either
    /// transform is not the identity, and the failure is silent.
    /// </summary>
    private static (XYZ Min, XYZ Max)? WorldBounds(Solid solid)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        var any = false;

        try
        {
            foreach (Edge edge in solid.Edges)
            {
                foreach (var point in edge.Tessellate())
                {
                    any = true;

                    if (point.X < minX) minX = point.X;
                    if (point.Y < minY) minY = point.Y;
                    if (point.Z < minZ) minZ = point.Z;
                    if (point.X > maxX) maxX = point.X;
                    if (point.Y > maxY) maxY = point.Y;
                    if (point.Z > maxZ) maxZ = point.Z;
                }
            }
        }
        catch
        {
            return null;
        }

        return any ? (new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ)) : null;
    }

    /// <summary>
    /// The plane a slab lies in: sign-normalised normal, mid-plane offset, and thickness.
    ///
    /// Returns a null normal when no planar face dominates - a curved or irregular carrier.
    /// That is not treated as "no plane, skip"; it is treated as "plane unknown, let the
    /// boolean answer", which is the conservative direction for an audit.
    /// </summary>
    private static (XYZ? Normal, double Offset, double Thickness) PlaneOf(Solid solid)
    {
        PlanarFace? dominant = null;
        double best = 0;
        double total = 0;

        try
        {
            foreach (Face face in solid.Faces)
            {
                total += face.Area;

                if (face is PlanarFace planar && planar.Area > best)
                {
                    best = planar.Area;
                    dominant = planar;
                }
            }
        }
        catch
        {
            return (null, 0, 0);
        }

        // A slab has two big parallel faces, so the largest holds close to half the total. Well
        // under that and the shape is not a slab, and its "plane" would be a guess.
        if (dominant is null || total <= 0 || best / total < 0.30) return (null, 0, 0);

        try
        {
            var normal = SignNormalise(dominant.FaceNormal);

            // Offset from the CENTROID, not from the face origin: the centroid sits on the
            // mid-plane, which is what has to be compared. Two carriers on one wall face share
            // a mid-plane; the two faces of a 100 mm wall are 95 mm apart on it.
            var offset = normal.DotProduct(solid.ComputeCentroid());
            var thickness = best > 0 ? solid.Volume / best : 0;

            return (normal, offset, thickness);
        }
        catch
        {
            return (null, 0, 0);
        }
    }

    /// <summary>
    /// Flips a normal so parallel faces pointing opposite ways key the same. Without this,
    /// two coplanar carriers whose dominant faces happen to point away from each other give
    /// dot = -1 and offsets of opposite sign, and never compare as coplanar.
    /// </summary>
    private static XYZ SignNormalise(XYZ normal)
    {
        var unit = normal.Normalize();

        var ax = Math.Abs(unit.X);
        var ay = Math.Abs(unit.Y);
        var az = Math.Abs(unit.Z);

        var lead = ax >= ay && ax >= az ? unit.X : ay >= az ? unit.Y : unit.Z;

        return lead < 0 ? unit.Negate() : unit;
    }

    // ------------------------------------------------------------------ the test

    private (int Pairs, int Failures) Intersect(List<Slab> slabs, List<OverlapFinding> findings)
    {
        // Sweep along X so the inner loop stops early. Without it this is n^2 on every model,
        // and a storey of carriers is thousands of solids.
        var ordered = slabs.OrderBy(s => s.Min.X).ToList();

        var pairs = 0;
        var failures = 0;

        for (var i = 0; i < ordered.Count; i++)
        {
            var left = ordered[i];

            for (var j = i + 1; j < ordered.Count; j++)
            {
                var right = ordered[j];

                // Ordered by Min.X, so once one candidate starts past this one's right edge,
                // every later candidate does too.
                if (right.Min.X - BoxToleranceFt > left.Max.X) break;

                // A carrier's own solids are one row. They cannot double count against each
                // other however they sit.
                if (left.Carrier.Id == right.Carrier.Id) continue;

                if (!BoxesOverlap(left, right)) continue;
                if (!Coplanar(left, right)) continue;

                pairs++;

                Solid? intersection;

                try
                {
                    intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                        left.Solid, right.Solid, BooleanOperationsType.Intersect);
                }
                catch (Exception ex)
                {
                    failures++;

                    findings.Add(Finding(left, right, OverlapVerdict.Review, "untested", 0, false,
                        $"The boolean intersection failed ({ex.Message}). This pair was NOT proved clean."));

                    continue;
                }

                if (intersection is null || intersection.Volume <= VolumeEpsilon) continue;

                var thinner = Math.Min(
                    left.Thickness > 0 ? left.Thickness : double.MaxValue,
                    right.Thickness > 0 ? right.Thickness : double.MaxValue);

                var (area, estimated) = OverlapArea(
                    intersection,
                    left.Normal ?? right.Normal,
                    thinner == double.MaxValue ? 0 : thinner);

                if (area <= 0) continue;

                findings.Add(Classify(left, right, area, estimated));
            }
        }

        return (pairs, failures);
    }

    private static bool BoxesOverlap(Slab a, Slab b) =>
        a.Min.X - BoxToleranceFt <= b.Max.X && b.Min.X - BoxToleranceFt <= a.Max.X &&
        a.Min.Y - BoxToleranceFt <= b.Max.Y && b.Min.Y - BoxToleranceFt <= a.Max.Y &&
        a.Min.Z - BoxToleranceFt <= b.Max.Z && b.Min.Z - BoxToleranceFt <= a.Max.Z;

    /// <summary>
    /// THE GATE THAT KEEPS THE TWO FACES OF A WALL APART. Same normal, offsets 95 mm apart -
    /// not coplanar, never compared, never reported. An unknown plane on either side falls
    /// through to the boolean rather than being assumed clean.
    /// </summary>
    private static bool Coplanar(Slab a, Slab b)
    {
        if (a.Normal is null || b.Normal is null) return true;

        return Math.Abs(a.Normal.DotProduct(b.Normal)) >= 0.999 &&
               Math.Abs(a.Offset - b.Offset) <= PlaneToleranceFt;
    }

    /// <summary>
    /// The intersection is a thin slab, so its area is the area of its own biggest face in the
    /// shared plane. Volume/thickness is the fallback and is flagged as an estimate, because
    /// it is only exact for a prism.
    /// </summary>
    private static (double Area, bool Estimated) OverlapArea(Solid intersection, XYZ? normal, double thickness)
    {
        if (normal is not null)
        {
            double best = 0;

            try
            {
                foreach (Face face in intersection.Faces)
                {
                    if (face is PlanarFace planar &&
                        Math.Abs(planar.FaceNormal.Normalize().DotProduct(normal)) >= 0.999 &&
                        planar.Area > best)
                    {
                        best = planar.Area;
                    }
                }
            }
            catch
            {
                best = 0;
            }

            if (best > 0) return (best, false);
        }

        return thickness > VolumeEpsilon ? (intersection.Volume / thickness, true) : (0, true);
    }

    private static OverlapFinding Classify(Slab left, Slab right, double area, bool estimated)
    {
        var verdict = area >= OverlapFloorSqFt ? OverlapVerdict.Error : OverlapVerdict.Review;

        var a = left.Carrier;
        var b = right.Carrier;

        var sqm = Measure.ToSquareMetres(area);

        if (a.Product != b.Product)
        {
            return Finding(left, right, verdict, "two products", area, estimated,
                $"{sqm:0.###} m2 of one surface is carried by both {a.Product} and {b.Product}. " +
                "Two takeoffs are live in this model.");
        }

        var sameRoom =
            string.Equals(a.Room, b.Room, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.RoomNumber, b.RoomNumber, StringComparison.OrdinalIgnoreCase);

        if (sameRoom && string.Equals(a.Material, b.Material, StringComparison.OrdinalIgnoreCase))
        {
            return Finding(left, right, verdict, "duplicate carrier", area, estimated,
                $"Two rows for {Name(a)} carry the same {sqm:0.###} m2 in the same material. " +
                "One is stale or a copy.");
        }

        if (sameRoom)
        {
            return Finding(left, right, verdict, "two materials", area, estimated,
                $"{Name(a)} claims {sqm:0.###} m2 twice, once as '{a.Material}' and once as " +
                $"'{b.Material}'. One patch of wall, two paints.");
        }

        return Finding(left, right, verdict, "shared surface", area, estimated,
            $"{Name(a)} and {Name(b)} both claim the same {sqm:0.###} m2. " +
            "One surface, billed to two rooms.");
    }

    private static string Name(Carrier carrier) =>
        string.IsNullOrWhiteSpace(carrier.RoomNumber) && string.IsNullOrWhiteSpace(carrier.Room)
            ? $"carrier {carrier.Id}"
            : $"{carrier.RoomNumber} {carrier.Room}".Trim();

    private static OverlapFinding Finding(
        Slab left, Slab right, OverlapVerdict verdict, string check,
        double area, bool estimated, string detail) =>
        new(verdict, check,
            left.Carrier.Id, right.Carrier.Id,
            left.Carrier.Room, right.Carrier.Room,
            left.Carrier.Material, right.Carrier.Material,
            left.Carrier.Segment, right.Carrier.Segment,
            left.Carrier.ReportedAreaSqFt, right.Carrier.ReportedAreaSqFt,
            area, estimated, detail);

    // ------------------------------------------------------------------ no geometry

    /// <summary>
    /// The only check available to a carrier with no shape: two rows for the same room, the
    /// same surface and the same material are one row too many, whatever their geometry.
    ///
    /// This is a WEAKER test than the geometric one and must not be mistaken for it. It finds
    /// duplicate rows; it cannot find two rooms overlapping on part of a surface.
    /// </summary>
    private static IEnumerable<OverlapFinding> DuplicateKeys(IReadOnlyList<Carrier> carriers)
    {
        foreach (var group in carriers
                     .GroupBy(c => (
                         Room: c.Room.ToUpperInvariant(),
                         Number: c.RoomNumber.ToUpperInvariant(),
                         Segment: c.Segment.ToUpperInvariant(),
                         Material: c.Material.ToUpperInvariant()))
                     .Where(g => g.Count() > 1))
        {
            var rows = group.ToList();

            // Only against the first: n rows produce n-1 findings, not n(n-1)/2 restatements
            // of one duplicate.
            for (var i = 1; i < rows.Count; i++)
            {
                var a = rows[0];
                var b = rows[i];

                yield return new OverlapFinding(
                    OverlapVerdict.Error, "duplicate row",
                    a.Id, b.Id, a.Room, b.Room, a.Material, b.Material, a.Segment, b.Segment,
                    a.ReportedAreaSqFt, b.ReportedAreaSqFt,
                    Math.Min(a.ReportedAreaSqFt, b.ReportedAreaSqFt), true,
                    $"{Name(a)} has {rows.Count} rows for the same surface and material " +
                    $"('{a.Segment}'). Found by key: these carriers hold no geometry to intersect.");
            }
        }
    }
}
