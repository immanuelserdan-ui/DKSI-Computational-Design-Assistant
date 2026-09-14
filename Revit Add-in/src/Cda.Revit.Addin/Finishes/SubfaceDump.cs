using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Finishes;

/// <summary>Which mechanism is losing the paint in the slab-thickness band.</summary>
public enum BandVerdict
{
    /// <summary>Nothing caps this room, so there is no band to examine.</summary>
    NoSlabAbove,

    /// <summary>Room volumes are switched off AND the slab has no inner loops - cannot tell.</summary>
    VolumesUnavailable,

    /// <summary>The slab above is solid over this room. The band is correctly buried.</summary>
    NoOpening,

    /// <summary>
    /// There is an opening, and NO vertical subface reaches into the band: the room solid
    /// never gets there, so no amount of re-routing can help. Needs a new measuring pass.
    /// </summary>
    BandEmpty,

    /// <summary>
    /// There is an opening and a vertical band subface exists, but it resolves to something
    /// that is not a Wall - so the exact path declines it and the arithmetic fallback, which
    /// is unpainted by definition, swallows it. Needs a routing change.
    /// </summary>
    BandFallbackRouted,

    /// <summary>
    /// There is an opening and a vertical band subface resolves to a Wall. The engine should
    /// already be measuring this, so the loss is further down - in the coplanar match or the
    /// boolean - and neither of the two headline fixes is the right one.
    /// </summary>
    BandWallResolved,
}

/// <summary>One boundary subface, reduced to the facts that decide the verdict.</summary>
public sealed class SubfaceRow
{
    public required long RoomId { get; init; }
    public required string RoomLabel { get; init; }
    public required int FaceIndex { get; init; }
    public required int SubfaceIndex { get; init; }
    public required string Type { get; init; }
    public required double AreaSqFt { get; init; }
    public required double ZMin { get; init; }
    public required double ZMax { get; init; }
    public required double NormalZ { get; init; }
    public required bool Vertical { get; init; }
    public required long? HostId { get; init; }
    public required string HostCategory { get; init; }
    public required string HostClass { get; init; }
    public required bool IsWall { get; init; }
    public required bool InBand { get; init; }
    public required double BandOverlapFt { get; init; }

    /// <summary>A coplanar face of the host carries paint - so losing this face loses paint.</summary>
    public bool HostPainted { get; set; }
}

/// <summary>What one room says about the band above it.</summary>
public sealed class RoomBandFinding
{
    public required long RoomId { get; init; }
    public required string Label { get; init; }
    public required double BaseZ { get; init; }
    public required double RoomTopZ { get; init; }

    public long? SlabId { get; set; }
    public string SlabName { get; set; } = string.Empty;
    public double SoffitZ { get; set; }
    public double SlabTopZ { get; set; }
    public double RoomVolume { get; set; }
    public bool VolumesOff { get; set; }

    /// <summary>Plan area of slab opening over this room, derived from the volume excess.</summary>
    public double OpenArea { get; set; }

    /// <summary>Inner boundary loops on the slab bottom face - an independent read of the same hole.</summary>
    public int HoleLoops { get; set; }

    public BandVerdict Verdict { get; set; } = BandVerdict.NoSlabAbove;

    /// <summary>Subfaces overlapping the band, whatever their orientation.</summary>
    public List<SubfaceRow> Band { get; } = [];

    public double BandHeight => SlabTopZ - SoffitZ;

    /// <summary>Band subfaces that are vertical AND carry paint - the area actually at risk.</summary>
    public IEnumerable<SubfaceRow> PaintedBand => Band.Where(r => r.Vertical && r.HostPainted);
}

public sealed class SubfaceDumpResult
{
    public List<RoomBandFinding> Rooms { get; } = [];
    public List<SubfaceRow> Subfaces { get; } = [];
    public List<string> Notes { get; } = [];
    public int SkippedUnplaced { get; set; }

    public IEnumerable<RoomBandFinding> WithOpening =>
        Rooms.Where(r => r.Verdict is BandVerdict.BandEmpty
                              or BandVerdict.BandFallbackRouted
                              or BandVerdict.BandWallResolved);

    public int Count(BandVerdict verdict) => Rooms.Count(r => r.Verdict == verdict);
}

/// <summary>
/// What a room's boundary subfaces actually are, in the band of wall that a slab opening
/// exposes - the strip between the bounding floor's soffit and its finished top.
///
/// THE QUESTION THIS EXISTS TO SETTLE, AND WHY NOTHING ELSE COULD
///   Measured in FM_Template: every carrier on every wall bounding the stair shaft stops at
///   the Terraen slab soffit (-3.937 ft) and restarts at its top (-3.281 ft). A 200 mm band -
///   exactly the slab thickness - carries no paint. Where the slab is solid that is correct:
///   the band is buried in the floor build-up. Where the stair opening is, that band is
///   exposed to the shaft, is painted in the real building, and the takeoff reports it as a
///   confident zero.
///
///   Two mechanisms produce that identical symptom and need opposite fixes:
///
///     (1) ROUTING. The band's Side subface EXISTS, but SpatialBoundaryElement resolves to
///         the Floor rather than to a Wall - so the exact path in RoomFinishCalculator, gated
///         on `element is Wall`, declines it and it falls to the arithmetic fallback bucket,
///         which is unpainted BY DEFINITION. Fix: widen the routing.
///
///     (2) DOMAIN. The room solid never reaches into the band at all, so no subface is ever
///         offered and there is nothing to route. Fix: a new pass that measures the opening
///         reveal and the wall band directly.
///
///   The per-room Wall Finish minus Wall Paint delta CANNOT separate them, and that is worth
///   stating because it looks like it should. That delta is fallback area PLUS genuinely
///   unpainted layer material on faces that measured perfectly - the finish-areas report
///   prints the two apart ("paint basis: X painted + Y unpainted layer material") for exactly
///   this reason. Run across FM_Template it ranks the stair room FOURTH, behind three rooms
///   with no opening anywhere near them. It is an upper bound on fallback, not a measurement
///   of it.
///
///   So the only instrument that answers the question is the subface list itself.
///
/// READ-ONLY, AND THAT COSTS ONE THING WORTH KNOWING
///   RoomFinishCalculator.Run calls EnsureVolumes and AdjustUpperLimits BEFORE it builds its
///   calculator, so the engine measures rooms whose limits it has just corrected. This cannot:
///   a diagnostic that edits the model until its own reading comes out is not a diagnostic.
///   It therefore reads the limits AS THEY STAND - the right answer on a model the engine has
///   already run against, because the FFL caps it applied are persisted on the rooms, and a
///   different one on a model it has never touched. The report says which it saw.
///
///   Room volumes are the same story. Volume is zero unless the model has volume computation
///   switched on, and switching it on is a write. Rooms without it are reported as
///   VolumesUnavailable rather than silently scoring zero open area and reading as innocent.
/// </summary>
public sealed class SubfaceDump
{
    private readonly Document _doc;
    private readonly FinishGeometry _geometry;

    /// <summary>
    /// Tighter than FinishSettings.LimitMargin (0.5 ft) on purpose: the band under test is a
    /// 200 mm slab thickness - 0.656 ft - so a half-foot margin would swallow the very thing
    /// being measured. This is the engine's own coplanar tolerance, about 6 mm.
    /// </summary>
    private const double Tolerance = FinishSettings.CoplanarTolerance;

    public SubfaceDump(Document doc)
    {
        _doc = doc;
        _geometry = new FinishGeometry(doc);
    }

    public SubfaceDumpResult Run(IReadOnlyCollection<ElementId>? scopeRoomIds = null)
    {
        var result = new SubfaceDumpResult();

        var options = new SpatialElementBoundaryOptions
        {
            // IDENTICAL to RoomFinishCalculator's. A dump taken at a different boundary
            // location answers a different question while looking like an answer to this one.
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
        };

        var calculator = new SpatialElementGeometryCalculator(_doc, options);

        var rooms = (scopeRoomIds is null || scopeRoomIds.Count == 0
                ? new FilteredElementCollector(_doc)
                : new FilteredElementCollector(_doc, scopeRoomIds.ToList()))
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .OfType<Room>()
            .ToList();

        foreach (var room in rooms)
        {
            try
            {
                if (room.Area <= 0)
                {
                    result.SkippedUnplaced++;
                    continue;
                }

                if (!SpatialElementGeometryCalculator.CanCalculateGeometry(room))
                {
                    result.Notes.Add($"Skipped (geometry not computable): {Label(room)}");
                    continue;
                }

                var finding = Examine(room, calculator, result);
                if (finding is not null) result.Rooms.Add(finding);
            }
            catch (Exception ex)
            {
                result.Notes.Add($"FAILED {Label(room)}: {ex.Message}");
                Log.Warn($"SubfaceDump: room {room.Id.Value} failed: {ex.Message}");
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ per room

    private RoomBandFinding? Examine(
        Room room, SpatialElementGeometryCalculator calculator, SubfaceDumpResult result)
    {
        var roomBox = SafeBox(room);
        if (roomBox is null) return null;

        var finding = new RoomBandFinding
        {
            RoomId = room.Id.Value,
            Label = Label(room),
            BaseZ = roomBox.Min.Z,
            RoomTopZ = roomBox.Max.Z,
        };

        // The slab that caps this room, found the way RoomFfsCap finds it - lowest floor
        // above, decided by undersides - so this agrees with the tool that already acts on
        // that answer instead of inventing a second one.
        var datum = RoomFfsCap.Datum(_doc, roomBox);
        if (datum is not { } cap)
        {
            finding.Verdict = BandVerdict.NoSlabAbove;
            return finding;
        }

        var slab = _doc.GetElement(cap.Floor);
        var slabBox = slab is null ? null : SafeBox(slab);
        if (slab is null || slabBox is null)
        {
            finding.Verdict = BandVerdict.NoSlabAbove;
            return finding;
        }

        finding.SlabId = slab.Id.Value;
        finding.SlabName = SafeName(slab);
        finding.SoffitZ = slabBox.Min.Z;
        finding.SlabTopZ = cap.TopZ;
        finding.HoleLoops = HoleLoops(slab);

        if (finding.BandHeight <= Tolerance)
        {
            finding.Verdict = BandVerdict.NoSlabAbove;
            return finding;
        }

        // OPEN AREA FROM VOLUME, the cheapest honest test of "is there a hole above this
        // room". A room fully capped by its slab has Volume == Area x (soffit - base)
        // exactly; every cubic foot past that leaked up through an opening, and dividing the
        // excess by the band height hands the opening's plan area back.
        //
        // Cross-checked against HoleLoops - the slab's own inner boundary loops - because the
        // two are independent readings: one interrogates the room, the other the slab. They
        // should agree, and a room where they disagree is worth opening by hand.
        finding.RoomVolume = SafeVolume(room);
        if (finding.RoomVolume <= 0)
        {
            finding.VolumesOff = true;
        }
        else
        {
            var capped = room.Area * (finding.SoffitZ - finding.BaseZ);
            finding.OpenArea = Math.Max((finding.RoomVolume - capped) / finding.BandHeight, 0.0);
        }

        var geometry = calculator.CalculateSpatialElementGeometry(room);
        var solid = geometry.GetGeometry();

        var faceIndex = -1;

        foreach (Face face in solid.Faces)
        {
            faceIndex++;
            var subIndex = -1;

            foreach (var subface in geometry.GetBoundaryFaceInfo(face))
            {
                subIndex++;

                var row = Describe(room, subface, faceIndex, subIndex, finding);
                if (row is null) continue;

                result.Subfaces.Add(row);
                if (row.InBand) finding.Band.Add(row);
            }
        }

        finding.Verdict = Decide(finding);
        return finding;
    }

    private SubfaceRow? Describe(
        Room room, SpatialElementBoundarySubface subface, int faceIndex, int subIndex,
        RoomBandFinding finding)
    {
        Face inner;
        try { inner = subface.GetSubface(); }
        catch { return null; }

        var z = ZRange(inner);
        if (z is not { } range) return null;

        var normal = NormalOf(inner);

        var element = BoundaryElement(subface);
        var linked = element is null && IsLinkedBoundary(subface);

        // OVERLAP, not containment. A wall face spanning the whole storey AND the band counts
        // as reaching the band - which is the point. The question is whether any geometry
        // gets in there at all, not whether something sits only there.
        var overlap = Math.Min(range.Max, finding.SlabTopZ) - Math.Max(range.Min, finding.SoffitZ);
        var inBand = overlap > Tolerance;

        var row = new SubfaceRow
        {
            RoomId = room.Id.Value,
            RoomLabel = finding.Label,
            FaceIndex = faceIndex,
            SubfaceIndex = subIndex,
            Type = SafeType(subface),
            AreaSqFt = SafeArea(inner),
            ZMin = range.Min,
            ZMax = range.Max,
            NormalZ = normal?.Z ?? double.NaN,
            Vertical = normal is not null && Math.Abs(normal.Z) <= 0.5,
            HostId = element?.Id.Value,
            HostCategory = SafeCategory(element),
            HostClass = element?.GetType().Name ?? (linked ? "(linked)" : "(virtual)"),
            IsWall = element is Wall,
            InBand = inBand,
            BandOverlapFt = inBand ? overlap : 0.0,
        };

        // THE FACT THAT MAKES A LOST FACE A DEFECT RATHER THAN A BLANK. An unpainted face
        // measuring zero is correct; a PAINTED face measuring zero is the bug. Same test the
        // engine already applies before recording _paintedUnmeasured, so a row flagged here
        // is one the engine would flag too if it ever reached that branch.
        if (element is not null)
        {
            try
            {
                row.HostPainted = _geometry.HasPaintedCoplanarFace(
                    inner, _geometry.CachedFaces(element), element);
            }
            catch
            {
                // Unresolvable face reference; leave false rather than guess upward.
            }
        }

        return row;
    }

    /// <summary>
    /// The verdict, which is the only line most readers need.
    ///
    /// Decided on VERTICAL band subfaces alone, deliberately. A Top subface sitting in the
    /// band is the slab's own underside and says nothing about the wall; counting it would
    /// let every ordinary room answer "covered" and the diagnostic would only ever agree
    /// with itself.
    /// </summary>
    private static BandVerdict Decide(RoomBandFinding finding)
    {
        if (finding.VolumesOff && finding.HoleLoops == 0) return BandVerdict.VolumesUnavailable;

        var hasOpening = finding.OpenArea > Tolerance || finding.HoleLoops > 0;
        if (!hasOpening) return BandVerdict.NoOpening;

        var vertical = finding.Band.Where(r => r.Vertical).ToList();
        if (vertical.Count == 0) return BandVerdict.BandEmpty;

        return vertical.Any(r => r.IsWall)
            ? BandVerdict.BandWallResolved
            : BandVerdict.BandFallbackRouted;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Openings in the slab, counted as the inner loops of its bottom face. The largest loop
    /// is the outer boundary; every other one is a hole.
    /// </summary>
    private int HoleLoops(Element slab)
    {
        var holes = 0;

        foreach (var face in _geometry.BottomFaces(slab))
        {
            try
            {
                var loops = face.GetEdgesAsCurveLoops();
                if (loops.Count > 1) holes += loops.Count - 1;
            }
            catch
            {
                // A face whose loops cannot be read contributes nothing.
            }
        }

        return holes;
    }

    /// <summary>
    /// Z extent via triangulation rather than the face's own bounding box, because
    /// Face.GetBoundingBox is in UV parameter space and says nothing about world Z.
    /// </summary>
    private static (double Min, double Max)? ZRange(Face face)
    {
        var min = double.MaxValue;
        var max = double.MinValue;

        try
        {
            var mesh = face.Triangulate();
            if (mesh is null) return null;

            for (var i = 0; i < mesh.Vertices.Count; i++)
            {
                var z = mesh.Vertices[i].Z;
                if (z < min) min = z;
                if (z > max) max = z;
            }
        }
        catch
        {
            return null;
        }

        return min <= max ? (min, max) : null;
    }

    private static XYZ? NormalOf(Face face)
    {
        try
        {
            var normal = face is PlanarFace planar
                ? planar.FaceNormal
                : face.ComputeNormal(new UV(0.5, 0.5));

            return normal.IsZeroLength() ? null : normal.Normalize();
        }
        catch
        {
            return null;
        }
    }

    private Element? BoundaryElement(SpatialElementBoundarySubface subface)
    {
        try { return _doc.GetElement(subface.SpatialBoundaryElement.HostElementId); }
        catch { return null; }
    }

    private static bool IsLinkedBoundary(SpatialElementBoundarySubface subface)
    {
        try { return subface.SpatialBoundaryElement.LinkInstanceId != ElementId.InvalidElementId; }
        catch { return false; }
    }

    private static string SafeType(SpatialElementBoundarySubface subface)
    {
        try { return subface.SubfaceType.ToString(); }
        catch { return "(unknown)"; }
    }

    private static double SafeArea(Face face)
    {
        try { return face.Area; }
        catch { return 0.0; }
    }

    private static double SafeVolume(Room room)
    {
        try { return room.Volume; }
        catch { return 0.0; }
    }

    private static string SafeCategory(Element? element)
    {
        try { return element?.Category?.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string SafeName(Element element)
    {
        try { return element.Name; }
        catch { return string.Empty; }
    }

    private static BoundingBoxXYZ? SafeBox(Element element)
    {
        try { return element.get_BoundingBox(null); }
        catch { return null; }
    }

    private static string Label(Room room)
    {
        try
        {
            var number = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? string.Empty;
            var name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;
            var label = $"{number} {name}".Trim();
            return label.Length > 0 ? label : room.Id.Value.ToString();
        }
        catch
        {
            return room.Id.Value.ToString();
        }
    }
}
