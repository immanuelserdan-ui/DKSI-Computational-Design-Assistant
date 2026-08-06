using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Sweeps;

namespace Cda.Revit.Addin.Qa;

/// <summary>
/// Scans every qualifying room for interior wall faces that should carry skirting and do not.
///
/// HOW IT DECIDES, IN ONE PARAGRAPH
///   For each room that qualifies for skirting under the SAME rules the generator uses, walk
///   its finish-level boundary. For each stretch of boundary that lies on a wall, work out
///   which parts of it are legitimately exempt - a doorway, a run of base units - and
///   subtract those. What is left is wall that SHOULD have a board. Then subtract the boards
///   that are actually there. Anything still standing is a finding.
///
/// WHY IT REUSES SkirtingRun
///   The interval arithmetic - merge these blocked spans, subtract them from that run - is
///   the same problem the generator solves, and it is already written and proven in
///   <see cref="SkirtingRun"/>. Reusing it has one real cost, worth stating plainly: a bug in
///   the interval maths would be invisible to this check, because the check would make the
///   same mistake. That is an accepted trade. The maths is the least likely part to be wrong,
///   and a second hand-rolled copy that drifts out of step with the first would produce false
///   alarms continuously, which is the failure mode that gets a QA tool switched off.
///
///   What is NOT shared is the part that matters: coverage is measured from the boards
///   actually in the model, never from the generator's own record of what it placed. The
///   check can therefore catch a board that was placed and later deleted, which is precisely
///   the case a re-run of the generator would paper over.
/// </summary>
public sealed class SweepInspector
{
    private readonly Document _doc;
    private readonly QaSettings _settings;

    private readonly List<QaFinding> _findings = [];
    private readonly List<string> _scope = [];

    private int _roomsExamined;
    private int _roomsSkippedWet;
    private int _roomsSkippedExterior;
    private int _roomsSkippedNoDepartment;
    private int _roomsSkippedUnplaced;
    private int _facesChecked;
    private int _facesCovered;

    public SweepInspector(Document doc, QaSettings settings)
    {
        _doc = doc;
        _settings = settings;
    }

    public QaScanResult Run(IProgress<string>? progress = null)
    {
        var boards = SweepInventory.Collect(_doc, _settings);

        var rooms = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .OfType<Room>()
            .ToList();

        var options = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
        };

        var index = 0;

        foreach (var room in rooms)
        {
            index++;

            if (index % 10 == 0)
                progress?.Report($"Room {index} of {rooms.Count}…");

            try
            {
                if (!Qualifies(room)) continue;

                _roomsExamined++;
                Examine(room, boards, options);
            }
            catch (Exception ex)
            {
                // One unmeasurable room must not end the scan for the other two hundred.
                Log.Warn($"QA sweep check: room {room.Id.Value} failed: {ex.Message}");

                _findings.Add(new QaFinding
                {
                    Severity = QaSeverity.Info,
                    Kind = "Not checked",
                    TargetId = room.Id,
                    TargetUniqueId = SafeUniqueId(room),
                    TargetDescription = $"Room {RoomLabel(room)}",
                    Room = RoomLabel(room),
                    Level = LevelName(room),
                    Detail = $"This room could not be checked: {ex.Message}",
                });
            }
        }

        BuildScope(boards, rooms.Count);

        return new QaScanResult
        {
            // Problems first, then by room, so the grid opens on what matters. Ordinal
            // comparison keeps Danish room names in a stable order rather than a
            // locale-dependent one.
            Findings = [.. _findings
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.Level, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Room, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Kind, StringComparer.Ordinal)],
            Scope = _scope,
            RoomsExamined = _roomsExamined,
            RoomsSkipped = _roomsSkippedWet + _roomsSkippedExterior +
                           _roomsSkippedNoDepartment + _roomsSkippedUnplaced,
            SweepsFound = boards.Count,
        };
    }

    // ---------------------------------------------------------------- room filtering

    /// <summary>
    /// The same qualification the generator applies, restated here because the generator's
    /// copy is private.
    ///
    /// It has to be the same. A check that reports missing skirting in a bathroom is
    /// reporting the generator for obeying its brief, and a list full of those is a list
    /// that gets ignored - taking the real findings with it.
    /// </summary>
    private bool Qualifies(Room room)
    {
        if (room.Area <= 0 || room.Location is null)
        {
            _roomsSkippedUnplaced++;
            return false;
        }

        var name = Text(room, BuiltInParameter.ROOM_NAME);
        var department = Text(room, BuiltInParameter.ROOM_DEPARTMENT);

        var prefix = _settings.Skirting.ExteriorRoomPrefix;
        if (!string.IsNullOrWhiteSpace(prefix) &&
            name.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            _roomsSkippedExterior++;
            return false;
        }

        if (_settings.Skirting.RequireDepartment && department.Trim().Length == 0)
        {
            _roomsSkippedNoDepartment++;
            return false;
        }

        foreach (var keyword in _settings.Skirting.ExcludedRoomKeywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                department.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                _roomsSkippedWet++;
                return false;
            }
        }

        return true;
    }

    // -------------------------------------------------------------------- the check

    private void Examine(Room room, IReadOnlyList<SweepBoard> boards, SpatialElementBoundaryOptions options)
    {
        IList<IList<BoundarySegment>> loops;
        try { loops = room.GetBoundarySegments(options); }
        catch { return; }

        if (loops is null || loops.Count == 0) return;

        var floor = FloorElevation(room);
        var roomBox = SafeBox(room);

        // Narrow the whole model's boards down to the ones near this room ONCE, rather than
        // re-walking the full list for every boundary segment. On a model with 4000 boards
        // and 300 rooms that is the difference between a scan you wait for and one you
        // abandon.
        var nearby = boards
            .Where(b => b.MinZ <= floor + _settings.BandAbove &&
                        b.MaxZ >= floor - _settings.BandBelow)
            .Where(b => roomBox is null || Overlaps(roomBox, b.Box))
            .ToList();

        foreach (var loop in loops)
        {
            foreach (var segment in loop)
            {
                if (_doc.GetElement(segment.ElementId) is not Wall wall) continue;

                Curve curve;
                try { curve = segment.GetCurve(); }
                catch { continue; }

                if (curve is null || curve.Length < _settings.MinReportableGap) continue;

                CheckFace(room, wall, curve, nearby, floor);
            }
        }
    }

    /// <summary>
    /// One room-side wall face: what should be covered, what is, and what that leaves.
    /// </summary>
    private void CheckFace(
        Room room, Wall wall, Curve curve, IReadOnlyList<SweepBoard> nearby, double floor)
    {
        _facesChecked++;

        var whole = new Span(curve.GetEndParameter(0), curve.GetEndParameter(1));
        if (whole.Extent <= 0) return;

        // Parameter units are arc length on a Line and radians on an Arc. One scale factor
        // converts either back to feet, so every length reported to the user is real.
        var scale = curve.Length / whole.Extent;

        // ---- what is legitimately exempt -------------------------------------------

        var blocked = new List<Span>();
        blocked.AddRange(OpeningSpans(wall, curve, floor));
        blocked.AddRange(CaseworkSpans(curve, floor));

        var exempt = SkirtingRun.Coalesce(blocked, _settings.Skirting.BlockerBridge / scale);
        var shouldHaveBoard = SkirtingRun.Subtract(whole, exempt);

        var expected = shouldHaveBoard.Sum(s => s.Extent);
        if (expected * scale < _settings.MinReportableGap) return;

        // ---- what is actually there -------------------------------------------------

        var covered = CoverageSpans(curve, nearby);

        if (covered.Count > 0) _facesCovered++;

        var bare = new List<Span>();
        foreach (var run in shouldHaveBoard)
            bare.AddRange(SkirtingRun.Subtract(run, covered));

        var bareLength = bare.Sum(s => s.Extent) * scale;
        if (bareLength < _settings.MinReportableGap) return;

        // ---- classify ---------------------------------------------------------------

        var fraction = expected <= 0 ? 1.0 : bare.Sum(s => s.Extent) / expected;

        if (covered.Count == 0 || fraction >= _settings.MissingThreshold)
        {
            _findings.Add(new QaFinding
            {
                Severity = QaSeverity.Problem,
                Kind = "No skirting",
                TargetId = wall.Id,
                TargetUniqueId = SafeUniqueId(wall),
                TargetDescription = Describe(wall),
                Room = RoomLabel(room),
                Level = LevelName(room),
                Length = Measure.ToMetres(bareLength),
                Detail =
                    $"{Measure.ToMetres(bareLength):0.00} m of this wall's room-side face has no " +
                    "skirting, after allowing for openings and casework. Run Place Skirting, or " +
                    "confirm this face is meant to be bare.",
            });

            return;
        }

        if (!_settings.ReportGaps) return;

        foreach (var gap in bare)
        {
            var length = gap.Extent * scale;
            if (length < _settings.MinReportableGap) continue;

            var reach = _settings.CornerReach / scale;
            var atStart = gap.Start - whole.Start <= reach;
            var atEnd = whole.End - gap.End <= reach;
            var atCorner = atStart || atEnd;

            _findings.Add(new QaFinding
            {
                Severity = QaSeverity.Warning,
                Kind = atCorner ? "Gap at corner" : "Gap in run",
                TargetId = NearestBoard(curve, gap, nearby) ?? wall.Id,
                TargetUniqueId = null,
                TargetDescription = Describe(wall),
                Room = RoomLabel(room),
                Level = LevelName(room),
                Length = Measure.ToMetres(length),
                Detail = atCorner
                    ? $"{Millimetres(length)} of bare wall where this face meets the " +
                      "next one. Two runs that should mitre into each other are not meeting - the " +
                      "known symptom on non-orthogonal walls."
                    : $"{Millimetres(length)} of bare wall mid-run, with skirting " +
                      "either side. Usually something blocked the run that should not have - a " +
                      "radiator or a void family filed as casework.",
            });
        }
    }

    // ------------------------------------------------------------------- exemptions

    /// <summary>
    /// Doorways and other openings that reach the floor.
    ///
    /// The height test is the whole point, and it is taken straight from the generator's
    /// reasoning: a window with a metre of wall under it does not interrupt skirting, so
    /// blocking on every insert would silently exempt a window's width of bare wall on every
    /// elevation and hide real findings behind it.
    /// </summary>
    private IEnumerable<Span> OpeningSpans(Wall wall, Curve curve, double floor)
    {
        ICollection<ElementId> inserts;

        try { inserts = wall.FindInserts(true, false, true, true); }
        catch { yield break; }

        foreach (var id in inserts)
        {
            var insert = _doc.GetElement(id);
            if (insert is null) continue;

            BoundingBoxXYZ? box;
            try { box = insert.get_BoundingBox(null); }
            catch { continue; }

            if (box is null) continue;

            // Does it come down into the skirting band?
            if (box.Min.Z > floor + _settings.Skirting.RevealFloorTolerance) continue;

            var span = SkirtingRun.FromInsert(curve, insert, _doc, _settings.Skirting.OpeningPad);
            if (span is { } s && s.Extent > 0) yield return s;
        }
    }

    /// <summary>
    /// Base units standing against this face.
    ///
    /// Only <see cref="SkirtingSettings.BlockingCategories"/> can block, and the
    /// never-block name hints still apply - a radiator filed as casework is wall-hung above
    /// the board, and exempting the wall behind it would hide a genuinely missing run.
    /// </summary>
    private IEnumerable<Span> CaseworkSpans(Curve curve, double floor)
    {
        var categories = _settings.Skirting.BlockingCategories;
        if (categories.Length == 0) yield break;

        var mid = curve.Evaluate(0.5, true);
        var half = curve.Length / 2.0 + 1.0;

        var outline = new Outline(
            new XYZ(mid.X - half, mid.Y - half, floor - _settings.BandBelow),
            new XYZ(mid.X + half, mid.Y + half, floor + _settings.BandAbove));

        IEnumerable<Element> candidates;

        try
        {
            candidates = new FilteredElementCollector(_doc)
                .WherePasses(new ElementMulticategoryFilter(categories))
                .WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .ToElements();
        }
        catch
        {
            yield break;
        }

        foreach (var element in candidates)
        {
            if (IsNeverBlocking(element)) continue;

            BoundingBoxXYZ? box;
            try { box = element.get_BoundingBox(null); }
            catch { continue; }

            if (box is null) continue;

            // Must actually stand against THIS face, not merely be in the room.
            var centre = (box.Min + box.Max) / 2.0;
            var flat = new XYZ(centre.X, centre.Y, curve.GetEndPoint(0).Z);

            double distance;
            try { distance = curve.Distance(flat); }
            catch { continue; }

            if (distance > _settings.Skirting.CaseworkReach + HalfDiagonal(box)) continue;

            var span = SkirtingRun.FromBoundingBox(curve, box);
            if (span is { } s && s.Extent > 0) yield return s;
        }
    }

    private bool IsNeverBlocking(Element element)
    {
        try
        {
            var category = element.Category?.Id.Value ?? 0;

            foreach (var never in _settings.Skirting.NeverBlockCategories)
                if (category == (long)never) return true;

            var name = element.Name ?? string.Empty;
            var typeName = (_doc.GetElement(element.GetTypeId()) as ElementType)?.Name ?? string.Empty;

            foreach (var hint in _settings.Skirting.NeverBlockHints)
            {
                if (name.Contains(hint, StringComparison.OrdinalIgnoreCase) ||
                    typeName.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    // --------------------------------------------------------------------- coverage

    /// <summary>
    /// The stretches of this face that a board actually occupies.
    ///
    /// THE PROXIMITY TEST IS LOAD-BEARING. A wall between two rooms carries a board on each
    /// face, and both project onto the same stretch of the same boundary line. Without the
    /// distance check this face would read as covered by the NEIGHBOUR's board while this
    /// side is bare - a false pass, and a QA tool's worst possible error.
    /// </summary>
    private List<Span> CoverageSpans(Curve curve, IReadOnlyList<SweepBoard> nearby)
    {
        var spans = new List<Span>();
        var z = curve.GetEndPoint(0).Z;

        foreach (var board in nearby)
        {
            var flat = new XYZ(board.Centre.X, board.Centre.Y, z);

            double distance;
            try { distance = curve.Distance(flat); }
            catch { continue; }

            // Half the board's own plan diagonal, so a long board whose CENTRE is far along
            // the wall from the nearest point of a short segment is not rejected on that
            // account alone. The face test is about offset from the wall, not position along it.
            if (distance > _settings.FaceProximity + HalfPlanLength(board.Box)) continue;

            var span = SkirtingRun.FromBoundingBox(curve, board.Box);
            if (span is { } s && s.Extent > 0) spans.Add(s);
        }

        // Bridge zero: adjacent pieces from the generator butt against each other, and a
        // sub-millimetre seam between two of them is not a gap anybody can see or fix.
        return SkirtingRun.Coalesce(spans, Measure.FromMillimetres(2));
    }

    private ElementId? NearestBoard(Curve curve, Span gap, IReadOnlyList<SweepBoard> nearby)
    {
        var mid = (gap.Start + gap.End) / 2.0;

        XYZ point;
        try { point = curve.Evaluate(mid, false); }
        catch { return null; }

        ElementId? best = null;
        var bestDistance = double.MaxValue;

        foreach (var board in nearby)
        {
            var distance = board.Centre.DistanceTo(point);
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            best = board.Id;
        }

        // Only if it is genuinely adjacent. Jumping to a board on the far side of the room
        // is worse than jumping to the wall.
        return bestDistance < 10.0 ? best : null;
    }

    // ------------------------------------------------------------------------ scope

    private void BuildScope(IReadOnlyList<SweepBoard> boards, int roomCount)
    {
        _scope.Add($"Model contains {roomCount} room(s); {_roomsExamined} qualified for skirting.");

        _scope.Add(
            $"Skipped: {_roomsSkippedWet} wet room(s) by name/department, " +
            $"{_roomsSkippedExterior} '{_settings.Skirting.ExteriorRoomPrefix}' exterior " +
            $"placeholder(s), {_roomsSkippedNoDepartment} with no Department, " +
            $"{_roomsSkippedUnplaced} unplaced or unenclosed.");

        _scope.Add($"{_facesChecked} wall face(s) examined; {_facesCovered} carry some skirting.");

        if (boards.Count == 0)
        {
            _scope.Add(
                "NO SKIRTING OF ANY KIND WAS FOUND IN THIS MODEL - not as native wall sweeps, " +
                "not as stamped components, not by type name. Every qualifying face is therefore " +
                "reported as missing, which is correct but not informative. Run Place Skirting " +
                "first, then re-run this check.");
        }
        else
        {
            var byOrigin = boards
                .GroupBy(b => b.Origin)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count()} {g.Key}");

            _scope.Add($"{boards.Count} skirting element(s) found: {string.Join(", ", byOrigin)}.");
        }

        _scope.Add(
            $"Gaps shorter than {Millimetres(_settings.MinReportableGap)} are not " +
            "reported. Coverage is measured from the boards actually in the model, not from any " +
            "record of what the generator placed - so a board that was placed and later deleted " +
            "shows up here.");
    }

    // ---------------------------------------------------------------------- helpers

    /// <summary>
    /// A short internal length as millimetres, for the sentence in a finding.
    ///
    /// Millimetres rather than metres deliberately: these are gaps, and "0.04 m" reads as a
    /// rounding artefact where "42 mm" reads as a hole somebody has to close.
    /// </summary>
    private static string Millimetres(double internalLength) =>
        $"{Measure.ToMillimetres(internalLength):0} mm";

    private static double HalfDiagonal(BoundingBoxXYZ box) =>
        box.Min.DistanceTo(box.Max) / 2.0;

    private static double HalfPlanLength(BoundingBoxXYZ box)
    {
        var dx = box.Max.X - box.Min.X;
        var dy = box.Max.Y - box.Min.Y;
        return Math.Sqrt(dx * dx + dy * dy) / 2.0;
    }

    private static bool Overlaps(BoundingBoxXYZ a, BoundingBoxXYZ b) =>
        a.Min.X - 1.0 <= b.Max.X && a.Max.X + 1.0 >= b.Min.X &&
        a.Min.Y - 1.0 <= b.Max.Y && a.Max.Y + 1.0 >= b.Min.Y;

    private BoundingBoxXYZ? SafeBox(Element element)
    {
        try { return element.get_BoundingBox(null); }
        catch { return null; }
    }

    /// <summary>
    /// The room's floor level in internal units - level elevation plus the room's own base
    /// offset, which is not always zero and is what a sunken or raised room is modelled with.
    /// </summary>
    private static double FloorElevation(Room room)
    {
        var elevation = 0.0;

        try { elevation = room.Level?.Elevation ?? 0.0; }
        catch { /* no level */ }

        try
        {
            var offset = room.get_Parameter(BuiltInParameter.ROOM_LOWER_OFFSET);
            if (offset is { HasValue: true }) elevation += offset.AsDouble();
        }
        catch
        {
            // Base offset unreadable; the level alone is close enough for a height band
            // that is already 400 mm wide.
        }

        return elevation;
    }

    private string Describe(Wall wall)
    {
        try
        {
            var type = _doc.GetElement(wall.GetTypeId())?.Name ?? "Wall";
            return $"Wall · {type} [{wall.Id.Value}]";
        }
        catch
        {
            return $"Wall [{wall.Id.Value}]";
        }
    }

    private static string RoomLabel(Room room)
    {
        var number = Text(room, BuiltInParameter.ROOM_NUMBER);
        var name = Text(room, BuiltInParameter.ROOM_NAME);

        return string.IsNullOrWhiteSpace(number) ? name : $"{number} — {name}";
    }

    private static string LevelName(Room room)
    {
        try { return room.Level?.Name ?? "-"; }
        catch { return "-"; }
    }

    private static string Text(Element element, BuiltInParameter parameter)
    {
        try { return element.get_Parameter(parameter)?.AsString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string? SafeUniqueId(Element element)
    {
        try { return element.UniqueId; }
        catch { return null; }
    }
}
