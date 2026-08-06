using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Cda.Revit.Addin.Finishes;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Sweeps;

public sealed class SkirtingResult
{
    public required IReadOnlyList<string> Report { get; init; }
    public required int RoomsQualifying { get; init; }
    public required int RoomsExcluded { get; init; }
    public required int Placed { get; init; }
    public required double TotalLength { get; init; }
    public required int OpeningBreaks { get; init; }
    public required int CaseworkBreaks { get; init; }
    public required int RevealPieces { get; init; }
    public required double RevealLength { get; init; }
    public required int RoomsExterior { get; init; }
    public required IReadOnlyList<string> Problems { get; init; }
}

/// <summary>
/// Places a COMPONENT skirting family along the room-side face of every wall bounding a
/// qualifying room.
///
/// HOW BREAKING WORKS WITHOUT A WALL SWEEP
///   A native wall sweep gets break-at-openings from one flag. A component family has no
///   such flag, so the breaking is done by placing SHORTER PIECES: each boundary curve is
///   reduced to the stretches that survive once openings and casework are subtracted, and
///   one instance goes on each surviving stretch. See <see cref="SkirtingRun"/>.
///
///   The pieces are real, independent components. That means their lengths are true in a
///   schedule - which a full-length board with a void carved out of it would not be, and
///   skirting is priced by the metre.
///
/// WHICH CURVE
///   The room's own boundary segments at Finish location. Those curves already lie on the
///   room-side face of each wall, which is exactly where a skirting board goes - so no
///   offsetting from a wall centreline, and no reasoning about wall thickness or which way
///   the wall was drawn.
/// </summary>
public sealed class SkirtingGenerator
{
    private readonly Document _doc;
    private readonly SkirtingSettings _settings;

    private readonly List<string> _report = [];
    private readonly List<string> _problems = [];
    private readonly List<ElementId> _placed = [];

    private List<Element> _casework = [];
    private SkirtingPlacer? _placer;
    private readonly FinishGeometry _geometry;

    /// <summary>Pieces whose length could not be driven to match their run.</summary>
    private int _wrongLength;

    private int _revealPieces;
    private double _revealLength;

    /// <summary>Reveals suppressed because the far side is outdoors or unmodelled.</summary>
    private int _revealsOutside;

    /// <summary>Rooms skipped as 'Udvendig' exterior placeholders.</summary>
    private int _roomsExterior;

    /// <summary>Boundary segments skipped as the outside face of an envelope wall.</summary>
    private int _exteriorFaces;

    /// <summary>Inserts that never touch the board because they sit above it.</summary>
    private int _openingsAboveBoard;

    /// <summary>Run ends extended past a corner so adjacent boards meet.</summary>
    private int _cornersClosed;

    /// <summary>Rooms skipped for having no Department value.</summary>
    private int _roomsNoDepartment;

    /// <summary>Blockers refused for being M&amp;E, so the board runs behind them.</summary>
    private int _mePassedThrough;

    /// <summary>
    /// Runs too short to survive their own corner trim, dropped rather than allowed to
    /// overlap a neighbour. Required by the office standard - see BuildPiece.
    /// </summary>
    private int _droppedRatherThanOverlap;

    /// <summary>Everything that actually cut a run, so a stray gap can be traced.</summary>
    private readonly List<string> _blockedBy = [];

    /// <summary>Wall-hosted elements that are not openings, so cannot break a board.</summary>
    private int _hostedNonOpenings;

    /// <summary>Boundary segments folded into a neighbour because they were collinear.</summary>
    private int _segmentsMerged;

    private int _removed;

    /// <summary>Wall faces that ended up with no board at all, and why.</summary>
    private readonly List<string> _emptyRuns = [];

    /// <summary>List-adjacent runs that do not actually meet, so carry no corner.</summary>
    private int _detachedNeighbours;

    /// <summary>
    /// The board's real thickness off the wall, read from the first instance placed.
    ///
    /// THE ROOT CAUSE OF EVERY CORNER FAULT SO FAR. The mitre trim is
    /// depth / tan(interior/2), and depth was a hardcoded 20 mm guess while the family is
    /// called Skirtingboard_21-80mm. If the real profile is not 20 mm deep, every corner in
    /// the model is wrong by the difference and always in the same direction - a gap when
    /// the guess is too big, an overlap when too small. That is precisely the signature:
    /// corner faults that survived six rounds of changes to WHICH side gets trimmed and
    /// WHETHER it is trimmed, because none of those touched HOW MUCH.
    /// </summary>
    private double? _measuredDepth;

    /// <summary>Blocked spans absorbed into a neighbour because they nearly touched.</summary>
    private int _blockersCoalesced;

    /// <summary>Corners where the boards diverge, so no trim is owed.</summary>
    private int _reentrantCorners;

    private int _clippedToRoom;
    private int _droppedOutsideRoom;

    /// <summary>Pieces refused by the no-overlap rule. Should be zero on a clean model.</summary>
    private int _refusedOverlaps;

    /// <summary>Existing boards found to share a line with a candidate.</summary>
    private int _coverageMatches;

    /// <summary>
    /// Closest any existing board came to a candidate's line, sideways. Instrumentation:
    /// if no coverage is ever detected, this says whether the boards are metres apart or
    /// millimetres - which distinguishes "wrong geometry" from "tolerance too tight".
    /// </summary>
    private double _closestExisting = double.MaxValue;

    /// <summary>
    /// Curves already placed, for the overlap test. Held per run rather than re-queried:
    /// the alternative is a geometric intersection against every existing instance in the
    /// model for every candidate piece.
    /// </summary>
    private readonly List<Line> _placedCurves = [];
    private int _separationLines;
    private int _linkedBoundaries;
    private int _nonWallBoundaries;
    private readonly HashSet<string> _droppedKinds = [];

    /// <summary>Full per-face trace. Goes to the log file, not the dialog.</summary>
    private readonly List<string> _trace = [];

    /// <summary>One continuous stretch of wall face to be skirted.</summary>
    private sealed record BoundaryRun(Wall Wall, Curve Axis, long SegmentId);

    /// <summary>
    /// Consecutive boundary segments on the SAME wall and in line with each other, folded
    /// into one run.
    ///
    /// THE SECOND ARCHITECTURAL FAULT. Revit splits a room's boundary wherever anything
    /// meets the wall - a butting partition, a door, a change of wall join. Processing each
    /// fragment independently means every one of those splits is treated as a CORNER: the
    /// mitre logic trims the start of each fragment and extends the end of the one before,
    /// on a perfectly straight wall. The visible result is a run of short boards with
    /// slivers missing between them, which is what the plan screenshots show, and it is
    /// worst exactly where a room has most partitions meeting it.
    ///
    /// Merging first means corner handling only ever sees real changes of direction.
    /// </summary>
    private List<BoundaryRun> MergeLoop(IList<BoundarySegment> loop)
    {
        var runs = new List<BoundaryRun>();

        foreach (var segment in loop)
        {
            Wall? wall;
            Curve? curve;

            try
            {
                wall = _doc.GetElement(segment.ElementId) as Wall;
                curve = segment.GetCurve();
            }
            catch
            {
                continue;
            }

            // WHY A FACE CAN VANISH WITHOUT A TRACE.
            //
            // A boundary segment resolves to null for three quite different reasons, and
            // only one of them is benign: a room separation line (nothing to fix a board
            // to), a wall living in a LINKED model (segment.ElementId is meaningless in this
            // document - the id belongs to the link), or a boundary generated by something
            // that is not a wall at all. All three used to disappear silently, which is
            // exactly the "interior wall skipped for no apparent reason" symptom.
            if (wall is null || curve is null)
            {
                RecordDroppedSegment(segment);
                continue;
            }

            var previous = runs.Count > 0 ? runs[^1] : null;

            if (previous is not null &&
                previous.Wall.Id == wall.Id &&
                TryMerge(previous.Axis, curve) is { } merged)
            {
                runs[^1] = previous with { Axis = merged };
                _segmentsMerged++;
                continue;
            }

            runs.Add(new BoundaryRun(wall, curve, segment.ElementId.Value));
        }

        return runs;
    }

    /// <summary>
    /// Names what a boundary segment was, when it was not a wall in this document. The
    /// distinction matters: a separation line is expected, a linked wall is a real gap in
    /// coverage that needs a different fix.
    /// </summary>
    private void RecordDroppedSegment(BoundarySegment segment)
    {
        try
        {
            if (segment.LinkElementId is not null &&
                segment.LinkElementId != ElementId.InvalidElementId)
            {
                _linkedBoundaries++;
                return;
            }

            var element = _doc.GetElement(segment.ElementId);

            if (element is null)
            {
                _separationLines++;
                return;
            }

            var category = element.Category?.Name ?? "?";

            if (_droppedKinds.Count < 20) _droppedKinds.Add(category);
            _nonWallBoundaries++;
        }
        catch
        {
            _separationLines++;
        }
    }

    /// <summary>
    /// One line spanning both, when they run in the same direction and share an end.
    /// Lines only - two arcs that happen to be concentric are not worth the risk of
    /// silently reshaping a curved wall's board.
    /// </summary>
    private static Curve? TryMerge(Curve first, Curve second)
    {
        try
        {
            if (first is not Line a || second is not Line b) return null;

            var da = (a.GetEndPoint(1) - a.GetEndPoint(0)).Normalize();
            var db = (b.GetEndPoint(1) - b.GetEndPoint(0)).Normalize();

            if (da.DotProduct(db) < 0.9999) return null;   // not in line

            // WHY SLANTED WALLS BEHAVED DIFFERENTLY FROM ORTHOGONAL ONES.
            //
            // This was 1e-6 feet - 0.0003 mm. On a wall running along X or Y, Revit's
            // finish-face coordinates land on clean numbers and consecutive fragments of the
            // same wall meet exactly, so the merge succeeded. On a SLANTED wall every
            // coordinate carries a sin/cos component and the fragments land microns apart
            // from ordinary floating-point rounding, so the merge failed.
            //
            // A wall that fails to merge stays fragmented, and every fragment boundary is
            // then treated as a genuine corner: mitre computed, board trimmed, gap opened.
            // That is the whole difference between "straight units are fine" and "slanted
            // units are not" - one tolerance, five orders of magnitude too tight.
            //
            // Touches() answers the same question two methods away with 5 mm. This now uses
            // a shared constant so the two cannot disagree again.
            if (a.GetEndPoint(1).DistanceTo(b.GetEndPoint(0)) > JoinTolerance) return null;

            return Line.CreateBound(a.GetEndPoint(0), b.GetEndPoint(1));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The phase rooms are placed in, resolved once. GetRoomAtPoint without a phase returns
    /// null in any model with more than one, which would silently suppress every reveal.
    /// </summary>
    private Phase? _phase;

    private static double SafeWidth(Wall wall)
    {
        try { return wall.Width; }
        catch { return 0.0; }
    }

    private int _roomsQualifying;
    private int _roomsExcluded;
    private int _openingBreaks;
    private int _caseworkBreaks;
    private double _totalLength;

    public SkirtingGenerator(Document doc, SkirtingSettings settings)
    {
        _doc = doc;
        _settings = settings;
        _geometry = new FinishGeometry(doc);
    }

    public IReadOnlyList<ElementId> Placed => _placed;

    /// <summary>The caller owns the transaction.</summary>
    public void Run()
    {
        var symbol = ResolveSymbol();

        if (!symbol.IsActive)
        {
            // An unactivated symbol silently places nothing. This is the single most common
            // cause of "the command ran and did nothing".
            symbol.Activate();
            _doc.Regenerate();
        }

        _placer = new SkirtingPlacer(_doc, symbol);

        // Stated up front rather than discovered from odd-looking boards later. This one
        // line is what turns "why are they all 1200 mm?" into a five-second diagnosis.
        _report.Add($"FAMILY: '{SafeFamilyName(symbol)} : {SafeName(symbol)}' is " +
                    _placer.Describe());

        // Only the declared blocking categories are even collected, so mechanical and
        // electrical equipment cannot interrupt a run by any path - the board passes
        // behind a radiator or a socket, which is how it is built.
        _casework = [];

        if (_settings.BreakAtCasework)
        {
            foreach (var category in _settings.BlockingCategories)
            {
                _casework.AddRange(new FilteredElementCollector(_doc)
                    .OfCategory(category)
                    .WhereElementIsNotElementType());
            }
        }

        _report.Add(
            "BLOCKERS: only " +
            string.Join(", ", _settings.BlockingCategories.Select(c => c.ToString().Replace("OST_", ""))) +
            $" interrupt a run ({_casework.Count} instance(s) found). Mechanical and electrical " +
            "equipment are deliberately excluded - the board runs straight behind them.");

        if (!_settings.SkipExteriorWallFaces)
        {
            _report.Add(
                "INTERIOR WALLS: no wall-level exterior filter is applied. Every wall bounding a " +
                "qualifying room gets a board; exterior space is excluded at the ROOM level " +
                $"('{_settings.ExteriorRoomPrefix}' rooms, and rooms with no Department). Filtering " +
                "by wall type Function was removed because it silently dropped interior partitions " +
                "typed 'Exterior' by mistake.");
        }

        // REGENERATE, DO NOT TOP UP.
        //
        // The re-run guard keyed on room + segment + running index, and any change to how
        // boundaries are grouped renumbers every index. So a second run over an existing
        // set either skipped segments that had moved (boards missing) or stacked new pieces
        // on top of old ones (boards overlapping at corners) - and which of the two you got
        // depended on an implementation detail nobody can see from a plan view.
        //
        // Deleting first makes the output a pure function of the model and the settings.
        // Whatever is on screen after a run is what this build produces, with no residue
        // from an earlier one.
        var done = new HashSet<string>(StringComparer.Ordinal);

        _removed = DeletePrevious();

        if (_removed > 0)
            _report.Add($"REPLACED: removed {_removed} board(s) from a previous run first.");

        foreach (var room in Rooms())
        {
            if (IsExterior(room))
            {
                _roomsExterior++;
                continue;
            }

            if (IsExcluded(room))
            {
                _roomsExcluded++;
                continue;
            }

            _roomsQualifying++;
            _phase ??= PhaseOf(room);

            try { PlaceInRoom(room, done); }
            catch (Exception ex) { _problems.Add($"{Label(room)}: {ex.Message}"); }
        }

        if (_wrongLength > 0)
        {
            _problems.Add(
                $"{_wrongLength} piece(s) were placed but their length could NOT be set - the family " +
                "exposes no writable length parameter. Those boards are at the family's default " +
                "size, which looks deliberate in a view and is wrong in a schedule. Re-author the " +
                "family as line-based (CurveBased) to fix this properly.");
        }

        _report.Insert(0,
            $"SKIRTING: {_placed.Count} piece(s) totalling " +
            $"{Measure.ToMetres(_totalLength):0.00} m placed across {_roomsQualifying} qualifying room(s). " +
            $"{_roomsExcluded} room(s) excluded by name/department, " +
            $"{_roomsExterior} skipped as '{_settings.ExteriorRoomPrefix}' exterior placeholder(s). " +
            $"{_openingBreaks} break(s) at openings, {_caseworkBreaks} at casework.");

        if (_settings.WrapIntoReveals)
        {
            _report.Add(
                $"REVEALS: {_revealPieces} board(s) totalling {Measure.ToMetres(_revealLength):0.00} m wrap " +
                "across the wall thickness exposed inside openings. Each is stamped " +
                $"'{SkirtingSettings.Stamp}:reveal:<openingId>:<jamb>' in Comments, so a schedule " +
                "can separate reveal returns from wall runs, and the two rooms either side of an " +
                "opening cannot each place one.");
        }

        _report.Add(
            $"HEIGHT RULE: only things reaching below {Measure.ToMillimetres(_settings.BoardHeight):0} mm break " +
            $"the run. {_openingsAboveBoard} opening(s) were ignored for sitting entirely above " +
            "the board - windows with wall beneath them, so the board runs straight through. " +
            $"{_cornersClosed} corner(s) closed with a trim computed from the join angle " +
            "(depth / sin(interior) - the width of the rhombus where two board strips cross, " +
            "which is NOT the mitre length), applied to one side only - nothing is ever " +
            "extended, so every board stays a subset of its own boundary curve and cannot leave " +
            $"the room. {_reentrantCorners} corner(s) were RE-ENTRANT - the boundary wrapping the " +
            "end of a partition - where the boards DIVERGE instead of overlapping, so no trim is " +
            "owed and none is applied. Trimming those was opening a gap the width of the trim at " +
            $"every such junction. {_mePassedThrough} M&E element(s) passed through rather than " +
            "cut around.");

        _report.Add(
            $"CONTINUITY: {_hostedNonOpenings} wall-hosted element(s) were ignored as blockers for " +
            "not being openings - radiators, panels, sockets and hosted casework sit ON a wall " +
            "rather than breaching it, so the board runs behind them unbroken. " +
            $"{_segmentsMerged} boundary segment(s) were folded into a neighbour because they were " +
            "collinear fragments of the same wall; without that, every partition meeting a wall " +
            $"would read as a corner and leave a sliver of missing board. {_detachedNeighbours} " +
            "run pair(s) were adjacent in the boundary list but not touching in the model - " +
            "separated by a room separation line - and correctly carry no corner between them.");

        if (_placedCurves.Count > 0 || _coverageMatches > 0)
        {
            var closest = _closestExisting == double.MaxValue
                ? "no existing board shared a line with any candidate"
                : $"the closest any existing board came to a candidate's line was " +
                  $"{Measure.ToMillimetres(_closestExisting):0.0} mm sideways";

            _report.Add(
                $"COVERAGE MATCHING: {_coverageMatches} existing board(s) were matched to a " +
                $"candidate's line and counted as occupied wall; {closest}. If this run placed " +
                "duplicates, that number is the reason - a board only counts as coverage when it " +
                $"is within {Measure.ToMillimetres(JoinTolerance):0} mm sideways of the candidate " +
                "and on the same floor.");
        }

        _report.Add(
            $"NO-OVERLAP STANDARD: {_droppedRatherThanOverlap} run(s) were too short to survive " +
            "their own corner trim and were NOT placed - a piece that can only exist by growing " +
            $"into its neighbour is not placed at all. {_refusedOverlaps} further piece(s) were " +
            "refused by the final overlap check. That second number should be zero: the corner " +
            "trim is supposed to make overlap impossible, so anything above zero means a case the " +
            "trim does not model, and is worth reporting.");

        _report.Add(
            $"ADJACENT UNITS: {_blockersCoalesced} obstruction(s) were absorbed into a neighbour " +
            $"for sitting within {Measure.ToMillimetres(_settings.BlockerBridge):0} mm of it. A row of kitchen " +
            "units leaves a millimetre or two between carcasses; measured separately those joints " +
            "read as bare wall and collect 20 mm boards. Treating a close-packed row as one " +
            "obstruction is what stops the stubs.");

        _report.Add(
            $"CONTAINMENT: every board was sampled against the room with IsPointInRoom before " +
            $"placement. {_clippedToRoom} board(s) were shortened to the part actually inside the " +
            $"room; {_droppedOutsideRoom} were discarded for lying wholly outside it. Both counts " +
            "should be low - a high number means boundary curves are running past their room, " +
            "which is a modelling condition worth looking at rather than a tool setting.");

        _report.Add(
            $"BOUNDARY SEGMENTS NOT USED: {_separationLines} room separation line(s) - expected, " +
            $"nothing to fix a board to. {_linkedBoundaries} bounded by a wall in a LINKED model - " +
            "the segment's element id belongs to the link, not this document, so the wall cannot " +
            "be resolved and the face gets no board. " +
            $"{_nonWallBoundaries} bounded by something that is not a wall" +
            (_droppedKinds.Count > 0 ? " (" + string.Join(", ", _droppedKinds) + ")" : "") + ".");

        if (_emptyRuns.Count > 0)
        {
            _report.Add($"WALL FACES WITH NO BOARD - {_emptyRuns.Count} listed. Each is a face that " +
                        "qualified but produced nothing, with the reason:");

            foreach (var line in _emptyRuns) _report.Add("    " + line);
        }

        if (_blockedBy.Count > 0)
        {
            _report.Add("WHAT CUT THE RUNS - every gap in the skirting traced to the element that " +
                        "caused it. If something here should not be breaking the board, that is the " +
                        "family to re-categorise:");

            foreach (var line in _blockedBy.Distinct().Take(120)) _report.Add("    " + line);
        }

        if (_trace.Count > 0)
        {
            _report.Add(string.Empty);
            _report.Add("=== PER-FACE TRACE (log file only) ===");
            _report.AddRange(_trace);
        }

        if (_roomsNoDepartment > 0)
        {
            _report.Add(
                $"NO DEPARTMENT: {_roomsNoDepartment} room(s) skipped for having no Department " +
                "value. Counted inside the name/department exclusion total above.");
        }

        if (_exteriorFaces > 0)
        {
            _report.Add(
                $"OUTSIDE FACES: {_exteriorFaces} boundary segment(s) skipped for lying on the " +
                "exterior face of an Exterior/Foundation/Retaining wall. This catches outdoor " +
                "areas modelled as rooms that are NOT named " +
                $"'{_settings.ExteriorRoomPrefix}' - a terrace called 'Altan' looks like an " +
                "ordinary room to a name test, but its boundary is still on the outside of the " +
                "building. Interior faces of the same walls are unaffected.");
        }

        if (_revealsOutside > 0)
        {
            _report.Add(
                $"EXTERIOR EXCLUDED: {_revealsOutside} reveal(s) suppressed because the far side of " +
                "the opening is outdoors or unmodelled space. A reveal board spans the wall's whole " +
                "thickness, so without this an opening in an external wall puts skirting on the " +
                "outside of the building.");
        }
    }

    // ---------------------------------------------------------------- one room

    /// <summary>One run, measured and cut up, but not yet placed.</summary>
    private sealed record RunPlan(
        BoundaryRun Run, Curve Lifted, Span Whole, List<Span> Surviving, int Blockers);

    /// <summary>
    /// Does a board actually reach this run's far corner? False when a blocker stopped it
    /// short, or when nothing survived at all.
    /// </summary>
    /// <summary>
    /// Do these two runs actually meet? Compared in plan only - a level change between two
    /// faces is not a corner, and neither is a coincidence of list order.
    /// </summary>
    /// <summary>
    /// +1 anticlockwise, -1 clockwise, from the signed area of the loop's own endpoints
    /// (the shoelace formula).
    ///
    /// Measured rather than assumed. Revit does not guarantee a winding direction, and an
    /// inner loop - a room wrapping a column - runs opposite to the outer one, so a single
    /// hardcoded sign would be right for one and wrong for the other in the same room.
    /// </summary>
    private static double Winding(IReadOnlyList<BoundaryRun> runs)
    {
        if (runs.Count < 3) return 1.0;

        var twiceArea = 0.0;

        try
        {
            foreach (var run in runs)
            {
                var a = run.Axis.GetEndPoint(0);
                var b = run.Axis.GetEndPoint(1);
                twiceArea += (a.X * b.Y) - (b.X * a.Y);
            }
        }
        catch
        {
            return 1.0;
        }

        return twiceArea >= 0 ? 1.0 : -1.0;
    }

    /// <summary>
    /// ~5 mm. Boundary curves from adjoining faces meet exactly in principle, but
    /// finish-face geometry is rebuilt per segment and lands a hair apart - much further
    /// apart on a slanted wall, where every coordinate carries a sin/cos component.
    ///
    /// One constant for every "do these two curves meet?" test in this file. They were
    /// separately tuned before, five orders of magnitude apart, and the tight one silently
    /// disabled wall merging on any wall that was not orthogonal.
    /// </summary>
    private const double JoinTolerance = 0.016;

    private static bool Touches(Curve first, Curve second)
    {
        try
        {
            var a = first.GetEndPoint(1);
            var b = second.GetEndPoint(0);

            return Math.Abs(a.X - b.X) < JoinTolerance && Math.Abs(a.Y - b.Y) < JoinTolerance;
        }
        catch
        {
            return false;
        }
    }

    private static bool ReachesEnd(RunPlan plan) =>
        plan.Surviving.Count > 0 &&
        Math.Abs(plan.Surviving[^1].End - plan.Whole.End) < 1e-9;

    /// <summary>Everything that can be worked out about a run without writing to the model.</summary>
    private RunPlan? Plan(Room room, BoundaryRun run, double baseZ)
    {
        var lifted = LiftTo(run.Axis, baseZ + _settings.Offset);
        if (lifted is null) return null;

        if (_settings.SkipExteriorWallFaces && IsOutsideFace(run.Wall, lifted))
        {
            _exteriorFaces++;
            return null;
        }

        var blocked = new List<Span>();

        if (_settings.BreakAtOpenings) blocked.AddRange(OpeningSpans(run.Wall, lifted, baseZ));
        if (_settings.BreakAtCasework) blocked.AddRange(CaseworkSpans(room, lifted, baseZ));

        var whole = new Span(lifted.GetEndParameter(0), lifted.GetEndParameter(1));

        // Adjacent units become one obstruction before anything is subtracted, so the
        // joints between them never masquerade as places a board could go.
        var coalesced = SkirtingRun.Coalesce(blocked, _settings.BlockerBridge);
        _blockersCoalesced += blocked.Count - coalesced.Count;

        return new RunPlan(
            run, lifted, whole, SkirtingRun.Subtract(whole, coalesced), blocked.Count);
    }

    private void PlaceInRoom(Room room, HashSet<string> done)
    {
        var level = _doc.GetElement(room.LevelId) as Level;
        var baseZ = FloorElevation(room, level);

        var options = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
        };

        IList<IList<BoundarySegment>> loops;
        try { loops = room.GetBoundarySegments(options); }
        catch { return; }

        var index = 0;

        foreach (var loop in loops)
        {
            // Fragments of the same wall are folded together FIRST, so the corner logic
            // below only ever sees a genuine change of direction. Room separation lines
            // drop out here - no wall, nothing to fix a board to.
            var merged = MergeLoop(loop);

            // PASS 1 - plan every run before placing any of it.
            //
            // A corner is a two-sided agreement and neither side can be settled alone. The
            // board arriving grows across the corner; the board leaving is trimmed back by
            // the same amount so they butt instead of overlapping. That only balances if the
            // arriving board EXISTS. When a blocker stops the previous run short of the
            // corner - a cabinet, a doorway - or the previous face is a separation line with
            // no board at all, the trim still fired and cut material off a board with
            // nothing to butt against. The result is the notch in the corner screenshots.
            //
            // Deciding both sides from the planned spans removes the asymmetry entirely.
            var plans = new List<RunPlan?>();

            foreach (var run in merged)
            {
                plans.Add(Plan(room, run, baseZ));
            }

            // Which way this loop winds decides whether a left turn is convex or re-entrant.
            // Measured from the loop itself rather than assumed, because Revit's winding is
            // not guaranteed and an inner loop runs opposite to an outer one.
            var orientation = Winding(merged);

            // PASS 2 - place, with each corner settled from its neighbours' plans.
            for (var i = 0; i < plans.Count; i++)
            {
                var plan = plans[i];
                if (plan is null) continue;

                index++;

                var key = $"{SkirtingSettings.Stamp}:{room.Id.Value}:{plan.Run.SegmentId}:{index}";
                if (!done.Add(key)) continue;

                var previous = plans.Count > 1 ? plans[(i - 1 + plans.Count) % plans.Count] : null;
                var next = plans.Count > 1 ? plans[(i + 1) % plans.Count] : null;

                // A NEIGHBOUR IN THE LIST IS NOT NECESSARILY A NEIGHBOUR IN SPACE.
                //
                // MergeLoop drops boundary segments with no wall behind them - room
                // separation lines, the invisible edge across an opening into the next
                // space. Dropping them closes the gap in the LIST, so two runs on opposite
                // sides of an open doorway end up adjacent in it while being metres apart in
                // the model. The corner logic then computed a mitre between them and trimmed
                // a board back for a corner that does not exist.
                //
                // That is the inconsistency: identical walls behave differently depending on
                // whether the face next to them happens to be a separation line. Requiring
                // the two curves to actually share a point settles it.
                var previousJoins = previous is not null && Touches(previous.Lifted, plan.Lifted);
                var nextJoins = next is not null && Touches(plan.Lifted, next.Lifted);

                if (previous is not null && !previousJoins) _detachedNeighbours++;

                // TRIM ONLY. NOTHING IS EVER EXTENDED.
                //
                // Two boards meeting at a corner on the raw boundary curves OVERLAP - they
                // do not leave a notch. Put the corner at the origin with the room in the
                // positive quadrant: the board on the bottom face occupies x∈[0,W], y∈[0,d]
                // and the board on the left face occupies y∈[0,H], x∈[0,d]. Both cover the
                // square x∈[0,d], y∈[0,d].
                //
                // Earlier builds extended one board to fill a gap that was never there, and
                // that extension is precisely what pushed material past the room boundary -
                // a run can only leave the room by being made longer than its own boundary
                // curve. Trimming one side removes the overlap and cannot escape the room,
                // because the piece stays a subset of the curve it came from.
                var previousArrives = previousJoins && ReachesEnd(previous!);

                var mitreIn = previousArrives
                    ? MitreLength(previous!.Lifted, plan.Lifted, orientation)
                    : 0.0;

                // Kept only to record that a corner was handled; no geometry grows.
                if (nextJoins && ReachesEnd(plan)) _cornersClosed++;

                var placedHere = 0;
                var faceLength = Measure.ToMillimetres(plan.Lifted.Length);

                // Full trace per face, written to the LOG rather than the dialog. Every
                // number that decided this face's outcome, so a wrong board can be explained
                // from the file instead of inferred from a plan view.
                if (_trace.Count < 4000)
                {
                    _trace.Add(
                        $"{Label(room)} | wall {plan.Run.Wall.Id.Value} | face {faceLength:0} mm | " +
                        $"{plan.Blockers} blocker(s) | {plan.Surviving.Count} surviving run(s) | " +
                        $"trim-in {Measure.ToMillimetres(mitreIn):0.0} mm " +
                        $"(prev joins={previousJoins}, prev arrives={previousArrives})");
                }

                foreach (var run in plan.Surviving)
                {
                    var atSegmentStart = Math.Abs(run.Start - plan.Whole.Start) < 1e-9;

                    var piece = BuildPiece(plan.Lifted, run, atSegmentStart, mitreIn);
                    if (piece is null) continue;

                    // Last gate before placement: whatever the curve says, keep only the part
                    // the room agrees is inside it.
                    piece = ClipToRoom(room, piece);
                    if (piece is null)
                    {
                        _droppedOutsideRoom++;
                        continue;
                    }

                    // Office standard: no wall sweep may overlap another. The candidate is cut
                    // back to whatever no existing board covers - which in Regenerate is the
                    // whole thing, and in Fill Gaps is exactly the missing stretch.
                    var parts = UncoveredParts(piece);

                    if (parts.Count == 0)
                    {
                        _refusedOverlaps++;
                        continue;
                    }

                    foreach (var part in parts)
                    {
                        var instance = _placer!.Place(part, plan.Run.Wall, level, out var failure);

                        if (instance is null)
                        {
                            _problems.Add($"{Label(room)}: a {Measure.ToMetres(part.Length):0.00} m piece " +
                                          $"could not be placed - {failure}");
                            continue;
                        }

                        // Only meaningful for the strategies that do not stretch by themselves;
                        // DriveLength has already run inside Place for those.
                        if (!_placer.IsExact && !_placer.DriveLength(instance, part.Length))
                            _wrongLength++;

                        Stamp(instance, key, plan.Run.Wall);

                        _placed.Add(instance.Id);
                        if (part is Line placedLine) _placedCurves.Add(placedLine);
                        _totalLength += part.Length;
                        placedHere++;

                        if (_trace.Count < 4000)
                            _trace.Add($"      placed {Measure.ToMillimetres(part.Length):0} mm  (id {instance.Id.Value})");

                        // Measure the real profile once, from the first board that exists.
                        _measuredDepth ??= MeasureDepth(instance, part);
                    }
                }

                // A wall face that produced nothing is the symptom people actually see, and
                // guessing its cause from a plan view is what has cost the most time here.
                // Every one is now named, with the reason it came to nothing.
                if (placedHere == 0 && _emptyRuns.Count < 80)
                {
                    var length = Measure.ToMillimetres(plan.Lifted.Length);

                    var why = plan.Blockers == 0
                        ? $"nothing blocked it, so the {length:0} mm face was under the " +
                          $"{Measure.ToMillimetres(_settings.MinimumRun):0} mm minimum, or placement failed"
                        : $"{plan.Blockers} blocker(s) covered the whole {length:0} mm face";

                    _emptyRuns.Add($"{Label(room)} / wall {plan.Run.Wall.Id.Value}: {why}");
                }

                if (_settings.WrapIntoReveals)
                    PlaceReveals(room, plan.Run.Wall, plan.Lifted, level, baseZ, done);
            }
        }
    }

    /// <summary>
    /// Boards across the wall thickness exposed inside an opening.
    ///
    /// Stamped per OPENING and jamb rather than per room, so the two rooms either side of a
    /// cased opening do not each place one. The reveal is a single face shared between them;
    /// whichever room is processed first claims it.
    /// </summary>
    private void PlaceReveals(
        Room room, Wall wall, Curve axis, Level? level, double baseZ, HashSet<string> done)
    {
        var thickness = SafeWidth(wall);
        if (thickness < _settings.MinimumRun) return;

        foreach (var insert in RevealOpenings(wall, baseZ))
        {
            // Unpadded: the pad exists to hold boards clear of a frame, but the jamb line
            // itself is where the reveal face actually starts.
            var span = SkirtingRun.FromInsert(axis, insert, _doc, pad: 0.0);
            if (span is not { } jambs) continue;

            for (var jamb = 0; jamb < 2; jamb++)
            {
                var key = $"{SkirtingSettings.Stamp}:reveal:{insert.Id.Value}:{jamb}";
                if (!done.Add(key)) continue;

                var run = RevealRun(axis, room, jamb == 0 ? jambs.Start : jambs.End, thickness);
                if (run is null || run.Length < _settings.MinimumRun) continue;

                var instance = _placer!.Place(run, wall, level, out var failure);

                if (instance is null)
                {
                    _problems.Add($"{Label(room)}: reveal board at opening {insert.Id.Value} " +
                                  $"could not be placed - {failure}");
                    continue;
                }

                if (!_placer.IsExact && !_placer.DriveLength(instance, run.Length)) _wrongLength++;

                Stamp(instance, key, wall);

                _placed.Add(instance.Id);
                _revealPieces++;
                _totalLength += run.Length;
                _revealLength += run.Length;
            }
        }
    }

    /// <summary>
    /// One run turned into the curve a board is actually placed on, extended into corners.
    ///
    /// The extension is what closes the notch. Two boards turning a corner both stop on the
    /// finish face at the corner point, and a board has thickness, so the corner is left
    /// open by exactly that thickness. Pushing each one past the corner by its own depth
    /// makes them overlap there instead - which is what a mitre does, and is far less
    /// visible than a gap.
    ///
    /// Lines only. An arc's ends cannot be extended by rebuilding it from two points, and a
    /// curved wall meeting another wall at a sharp corner is rare enough not to justify the
    /// machinery.
    /// </summary>
    /// <summary>
    /// EXTEND at the outgoing corner, TRIM at the incoming one, by the same mitre length.
    ///
    /// This is the whole corner solution and both halves are required. Extending alone fills
    /// the notch but leaves the next board still starting at the corner point, so the two
    /// occupy the same square - the overlap. Trimming alone removes the overlap and reopens
    /// the notch. Doing both means the board arriving at a corner runs continuously across
    /// it, to the far face of the board leaving it, and the leaving board starts exactly at
    /// that face. One join, no gap, no shared material.
    ///
    /// It is also the answer for a wall merging into another: the through board carries on
    /// past the junction and the merging board stops against its face, which is what the
    /// brief describes as extending to the opposite face.
    /// </summary>
    /// <summary>
    /// The run turned into the curve a board is placed on, shortened at a corner so it does
    /// not overlap the board turning it.
    ///
    /// The piece is always a SUBSET of the boundary curve it came from. That is what
    /// guarantees containment: a board can only leave the room by being made longer than
    /// its own boundary, and nothing here makes anything longer.
    ///
    /// The trim is applied at the run's START, and only where the previous board genuinely
    /// arrives at that corner - which the caller has already established. An end created by
    /// subtracting a doorway is never a corner, so it never moves, which is what keeps the
    /// board tight to the jamb.
    /// </summary>
    /// <summary>
    /// The part of a board that is actually inside the room, found by asking the room.
    ///
    /// Samples along the board at a small inward offset and keeps the first-to-last
    /// contiguous stretch that reports inside. Returns null when none of it does.
    ///
    /// This is deliberately empirical. Every earlier attempt at containment reasoned about
    /// geometry - wall function, orientation, curve subsets - and each one was defeated by a
    /// case it did not model. IsPointInRoom is the definition of "in this room", so a board
    /// clipped to where that answers yes is contained by construction rather than by
    /// argument.
    /// </summary>
    /// <summary>
    /// How far the placed board actually stands off the wall, measured perpendicular to the
    /// curve it was placed on.
    ///
    /// Requires a regeneration first: an instance created in the open transaction has no
    /// geometry to read until the document catches up. One regenerate for the whole run is
    /// a fair price for a number every corner in the model depends on.
    /// </summary>
    private double? MeasureDepth(FamilyInstance instance, Curve piece)
    {
        try
        {
            _doc.Regenerate();

            var start = piece.GetEndPoint(0);
            var end = piece.GetEndPoint(1);
            var direction = (end - start).Normalize();
            var perpendicular = direction.CrossProduct(XYZ.BasisZ).Normalize();

            var deepest = 0.0;

            foreach (var solid in _geometry.ElementSolids(instance))
            {
                foreach (Edge edge in solid.Edges)
                {
                    IList<XYZ> points;
                    try { points = edge.Tessellate(); }
                    catch { continue; }

                    foreach (var point in points)
                    {
                        var offset = Math.Abs((point - start).DotProduct(perpendicular));
                        if (offset > deepest) deepest = offset;
                    }
                }
            }

            // A board deeper than 100 mm or thinner than 1 mm is not a measurement, it is a
            // family doing something unexpected. Fall back rather than propagate nonsense.
            if (deepest is < 0.003 or > 0.33) return null;

            _report.Add(
                $"PROFILE MEASURED: the board stands {Measure.ToMillimetres(deepest):0.0} mm off the wall " +
                $"(configured guess was {Measure.ToMillimetres(_settings.BoardDepth):0.0} mm). Every corner mitre " +
                "is computed from this, so a wrong value here shows up as a gap or an overlap at " +
                "EVERY corner in the model.");

            return deepest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The parts of a candidate board that no existing board already covers.
    ///
    /// THIS IS WHAT MAKES GAP-FILLING POSSIBLE. Refusing a piece outright because it
    /// overlaps something is right for a clean regenerate, but useless for repair: a run
    /// that is half covered would be thrown away and the missing half never placed. Cutting
    /// the candidate down to the uncovered remainder fills exactly the gap and nothing else.
    ///
    /// In Regenerate mode the placed list starts empty, so this returns the whole piece and
    /// costs one loop over nothing. One code path serves all three modes.
    /// </summary>
    private List<Curve> UncoveredParts(Curve candidate)
    {
        if (candidate is not Line line || _placedCurves.Count == 0) return [candidate];

        var start = line.GetEndPoint(0);
        var end = line.GetEndPoint(1);
        var direction = (end - start).Normalize();
        var length = start.DistanceTo(end);

        var covered = new List<Span>();

        foreach (var existing in _placedCurves)
        {
            try
            {
                var otherStart = existing.GetEndPoint(0);
                var otherEnd = existing.GetEndPoint(1);
                var otherDirection = (otherEnd - otherStart).Normalize();

                // Perpendicular neighbours at a corner touch; they do not cover.
                if (Math.Abs(direction.DotProduct(otherDirection)) < 0.999) continue;

                // SAME LINE? Perpendicular distance to the INFINITE line, in plan.
                //
                // This was Curve.Distance(otherStart) against the BOUND candidate, and that
                // is the bug that let duplicates through. Curve.Distance on a bound line
                // measures to the nearest ENDPOINT for any point beyond the segment - so a
                // board collinear with the candidate but offset along the wall reported a
                // distance equal to its offset, was judged "a different line", and
                // contributed no coverage. The candidate was then placed straight on top.
                //
                // Plan-only, with Z checked separately, because two boards on one wall face
                // at one floor level are the same run whether or not Revit stored their
                // location curves at exactly the same elevation - a level-based family can
                // record the curve at the level plane and carry the height in a parameter.
                var toOther = new XYZ(otherStart.X - start.X, otherStart.Y - start.Y, 0);
                var flatDirection = new XYZ(direction.X, direction.Y, 0);

                if (flatDirection.GetLength() < 1e-9) continue;
                flatDirection = flatDirection.Normalize();

                var sideways = (toOther - flatDirection * toOther.DotProduct(flatDirection)).GetLength();

                _closestExisting = Math.Min(_closestExisting, sideways);

                if (sideways > JoinTolerance) continue;

                // Same floor? A board a storey above is collinear in plan and irrelevant.
                if (Math.Abs(otherStart.Z - start.Z) > _settings.BoardHeight) continue;

                var a = (otherStart - start).DotProduct(direction);
                var b = (otherEnd - start).DotProduct(direction);

                _coverageMatches++;
                covered.Add(new Span(Math.Min(a, b), Math.Max(a, b)));
            }
            catch
            {
                // Undecidable pair contributes no coverage.
            }
        }

        if (covered.Count == 0) return [candidate];

        var parts = new List<Curve>();

        foreach (var free in SkirtingRun.Subtract(new Span(0.0, length), covered))
        {
            if (free.Extent < _settings.MinimumRun) continue;

            try
            {
                parts.Add(Line.CreateBound(
                    start + direction * free.Start,
                    start + direction * free.End));
            }
            catch
            {
                // Degenerate remainder.
            }
        }

        return parts;
    }

    /// <summary>
    /// The part of a board that is actually inside the room, found by asking the room.
    ///
    /// Samples along the board at a small inward offset and keeps the first-to-last
    /// contiguous stretch that reports inside. Returns null when none of it does.
    ///
    /// Deliberately empirical. Every earlier attempt at containment reasoned about geometry
    /// - wall function, orientation, curve subsets - and each was defeated by a case it did
    /// not model. IsPointInRoom is the definition of "in this room", so a board clipped to
    /// where that answers yes is contained by construction rather than by argument.
    /// </summary>
    private Curve? ClipToRoom(Room room, Curve piece)
    {
        if (!_settings.ConfineToRoom || piece is not Line line) return piece;

        try
        {
            var start = line.GetEndPoint(0);
            var end = line.GetEndPoint(1);
            var length = start.DistanceTo(end);

            if (length < 1e-9) return null;

            var direction = (end - start).Normalize();

            var inward = InwardNormal(room, start + direction * (length / 2.0), direction);
            if (inward is null) return piece;   // undecidable - leave the board alone

            var steps = Math.Max(2, (int)Math.Ceiling(length / _settings.ContainmentSample));

            int? first = null, last = null;

            for (var i = 0; i <= steps; i++)
            {
                var at = start + direction * (length * i / steps);

                if (!TryPointInRoom(room, at + inward * _settings.ContainmentProbe)) continue;

                first ??= i;
                last = i;
            }

            if (first is null || last is null) return null;                 // wholly outside
            if (first == 0 && last == steps) return piece;                  // wholly inside

            // REFINE, DO NOT SNAP.
            //
            // Taking the sample positions as the answer quantises the clip to the sample
            // spacing - 100 mm - so a board whose very first probe reads outside loses 100 mm
            // off its end. At a corner, where a probe can land on the boundary and answer
            // either way, that removes real board and reads as a corner gap. Bisecting
            // between the last outside sample and the first inside one brings the cut to
            // about 1.5 mm, which is below anything visible.
            var step = length / steps;

            var from = first.Value == 0
                ? 0.0
                : Refine(room, start, direction, inward,
                         (first.Value - 1) * step, first.Value * step, wantInside: true);

            var to = last.Value == steps
                ? length
                : Refine(room, start, direction, inward,
                         last.Value * step, (last.Value + 1) * step, wantInside: false);

            var clippedStart = start + direction * from;
            var clippedEnd = start + direction * to;

            if (clippedStart.DistanceTo(clippedEnd) < _settings.MinimumRun) return null;

            _clippedToRoom++;
            return Line.CreateBound(clippedStart, clippedEnd);
        }
        catch
        {
            return piece;   // a failed test must not lose a board
        }
    }

    /// <summary>
    /// Bisects between an outside sample and an inside one to find where the board actually
    /// crosses the room boundary. Six halvings take 100 mm down to about 1.5 mm.
    /// </summary>
    private double Refine(
        Room room, XYZ start, XYZ direction, XYZ inward, double low, double high, bool wantInside)
    {
        for (var i = 0; i < 6; i++)
        {
            var mid = (low + high) / 2.0;
            var inside = TryPointInRoom(room, start + direction * mid + inward * _settings.ContainmentProbe);

            // wantInside: searching for the first inside point, so the inside side is the
            // one to keep closing toward. Otherwise it is the reverse.
            if (inside == wantInside) high = mid;
            else low = mid;
        }

        return wantInside ? high : low;
    }

    /// <summary>
    /// The perpendicular that points into the room from a point on a board, or null when
    /// neither side answers - a board sitting exactly on an ambiguous boundary.
    /// </summary>
    private XYZ? InwardNormal(Room room, XYZ at, XYZ direction)
    {
        try
        {
            var perpendicular = direction.CrossProduct(XYZ.BasisZ).Normalize();
            var probe = _settings.ContainmentProbe;

            var forward = TryPointInRoom(room, at + perpendicular * probe);
            var backward = TryPointInRoom(room, at - perpendicular * probe);

            if (forward == backward) return null;

            return forward ? perpendicular : -perpendicular;
        }
        catch
        {
            return null;
        }
    }

    private Curve? BuildPiece(Curve axis, Span run, bool atSegmentStart, double mitreIn)
    {
        try
        {
            var piece = axis.Clone();
            piece.MakeBound(run.Start, run.End);

            if (piece.Length < _settings.MinimumRun) return null;

            var trim = atSegmentStart ? mitreIn : 0.0;

            if (!_settings.MitreAtCorners || trim <= 0 || piece is not Line line) return piece;

            var start = line.GetEndPoint(0);
            var end = line.GetEndPoint(1);

            // THE TRIM IS NEVER ABANDONED. NOTHING MAY OVERLAP.
            //
            // This used to place the piece untrimmed when the trim would have consumed it,
            // reasoning that a few millimetres of overlap beat a missing board. The office
            // standard says otherwise: no wall sweep may overlap another, full stop. So a
            // run too short to survive its own trim is not placed at all.
            //
            // The consequence to accept, stated plainly: a sliver of wall between two
            // corners closer together than the mitre gets no board. That is the correct
            // reading of the standard - a 10 mm piece that can only exist by growing into
            // its neighbour is not a piece anyone would cut and fit.
            var trimmed = start + (end - start).Normalize() * trim;

            if (trimmed.DistanceTo(end) < _settings.MinimumRun)
            {
                _droppedRatherThanOverlap++;
                return null;
            }

            return Line.CreateBound(trimmed, end);
        }
        catch
        {
            return null;   // a degenerate sub-range is not a board
        }
    }

    /// <summary>
    /// How far along this run the board must start so it no longer shares material with the
    /// board turning the corner: <c>depth / sin(interior)</c>.
    ///
    /// At 90° that evaluates to exactly the board depth, which is why a flat depth looks
    /// right until you meet an angled wall - at 135° interior it overshoots by 2.4x, and at
    /// a shallow join it is wildly wrong. Straight continuations return 0 because
    /// tan(90°) is infinite, which is the correct answer: nothing to close.
    ///
    /// A re-entrant corner gives a negative tangent; that means the boards diverge and need
    /// no extension, so it clamps to zero.
    /// </summary>
    /// <param name="orientation">
    /// +1 when the loop runs anticlockwise, -1 clockwise. Required to tell a convex corner
    /// from a re-entrant one - see the note in the body.
    /// </param>
    private double MitreLength(Curve current, Curve? next, double orientation)
    {
        if (next is null) return 0.0;

        try
        {
            var incoming = (current.GetEndPoint(1) - current.GetEndPoint(0)).Normalize();
            var outgoing = (next.GetEndPoint(1) - next.GetEndPoint(0)).Normalize();

            // Flatten: a level change between segments is not a plan corner.
            incoming = new XYZ(incoming.X, incoming.Y, 0);
            outgoing = new XYZ(outgoing.X, outgoing.Y, 0);

            if (incoming.GetLength() < 1e-9 || outgoing.GetLength() < 1e-9) return 0.0;

            incoming = incoming.Normalize();
            outgoing = outgoing.Normalize();

            // THE BUG THAT PRODUCED A GAP AT EVERY RE-ENTRANT JUNCTION.
            //
            // XYZ.AngleTo returns an UNSIGNED angle, 0..pi. It cannot tell a left turn from
            // a right one, so a re-entrant corner - where the boundary wraps around the end
            // of a partition - measured identically to the convex corner that mirrors it.
            //
            // The two are opposites. At a convex corner the two boards overlap by a square
            // of the board's depth, and one must be trimmed. At a re-entrant corner they
            // DIVERGE: there is no overlap, and trimming removes real board and opens a gap
            // exactly the width of the trim.
            //
            // Re-entrant corners are where partitions meet walls, at wall ends, and around
            // door returns - which is precisely where the gaps are. Every one of them was
            // being trimmed by 20 mm for an overlap that was never there.
            //
            // Atan2 of the cross product against the dot product gives the SIGNED turn, and
            // the loop's own winding says which sign turns into the room.
            var cross = incoming.CrossProduct(outgoing).Z;
            var signedTurn = Math.Atan2(cross, incoming.DotProduct(outgoing));   // (-pi, pi]
            var interior = Math.PI - orientation * signedTurn;

            // Straight on, or re-entrant. Neither has an overlap to remove.
            if (interior <= 1e-6 || interior >= Math.PI - 1e-6)
            {
                if (interior >= Math.PI - 1e-6) _reentrantCorners++;
                return 0.0;
            }

            // THE TRIM IS depth / sin(interior). IT WAS depth / tan(interior / 2).
            //
            // Those agree at exactly 90 degrees and nowhere else, which is why orthogonal
            // corners looked right while 45-degree ones overlapped.
            //
            // tan(interior/2) is the MITRE LENGTH - how far back a 45-degree cut runs along
            // a board so two mitred ends meet on the bisector. That is a different quantity
            // from the one needed here. These boards are not mitred: one runs through the
            // corner and the other butts against its side face, so the trim is however far
            // along the second board the two STRIPS stop overlapping.
            //
            // Two strips of width d crossing at interior angle a overlap in a rhombus of
            // side d / sin(a). The butting board must start beyond it:
            //
            //   90 deg  ->  d / sin(90)  = 1.00 d   (20.0 mm - unchanged, was already right)
            //   135 deg ->  d / sin(135) = 1.41 d   (28.3 mm - was 8.3 mm, 20 mm short)
            //   45 deg  ->  d / sin(45)  = 1.41 d   (28.3 mm - was 48.3 mm, 20 mm long)
            //
            // The 135-degree row is the 45-degree wall junction in the model: it was being
            // trimmed 20 mm less than it needed, so the two boards shared 20 mm of material.
            var sine = Math.Sin(interior);
            if (sine <= 1e-6) return 0.0;

            // MEASURED depth, not the configured guess. See MeasureDepth - a trim computed
            // from the wrong thickness is wrong at every corner in the model, in the same
            // direction.
            var depth = _measuredDepth ?? _settings.BoardDepth;

            // The clamp earns its place here: as the interior angle approaches 180 the
            // strips become near-parallel and the true clearance runs away to infinity. A
            // near-straight join needs a butt, not a metre-long trim.
            return Math.Clamp(depth / sine, 0.0, depth * _settings.MaxMitreFactor);
        }
        catch
        {
            return 0.0;
        }
    }

    /// <summary>
    /// A line from the room-side face straight into the wall, at one jamb.
    ///
    /// Which way is "into the wall" is settled by asking the room, not by trusting the
    /// wall's orientation: the perpendicular is taken from the curve's own tangent, both
    /// directions are probed, and the one that leaves the room is the one the reveal runs
    /// along. That works for curved walls and for walls drawn either way round.
    /// </summary>
    private Curve? RevealRun(Curve axis, Room room, double parameter, double thickness)
    {
        try
        {
            var start = axis.Evaluate(parameter, false);
            var tangent = axis.ComputeDerivatives(parameter, false).BasisX.Normalize();
            var perpendicular = tangent.CrossProduct(XYZ.BasisZ).Normalize();

            var probe = SkirtingSettings.SideProbe;

            var forwardInRoom = TryPointInRoom(room, start + perpendicular * probe);
            var backwardInRoom = TryPointInRoom(room, start - perpendicular * probe);

            // Both or neither means the jamb sits somewhere ambiguous - a corner, or a room
            // whose enclosure is broken. Guessing here drives a board through the wall.
            if (forwardInRoom == backwardInRoom) return null;

            var inward = forwardInRoom ? -perpendicular : perpendicular;
            var end = start + inward * thickness;

            // INTERIOR ONLY.
            //
            // The run spans the wall's whole thickness, so on an external wall it comes out
            // the far face and puts skirting on the outside of the building. Asking what is
            // just beyond that far face settles it: another room means a genuine internal
            // reveal shared by two rooms, nothing means open air or unmodelled space and the
            // board has left the room-bounding range entirely.
            if (_settings.InteriorRevealsOnly)
            {
                var beyond = end + inward * SkirtingSettings.SideProbe;
                var farRoom = RoomAt(beyond);

                // An 'Udvendig' room counts as OUTDOORS here, not as a room. It is a real
                // Room element, so without this test GetRoomAtPoint reports it as interior
                // space and the reveal is placed - pushing a board through an external wall
                // onto the terrace side, which is the exact failure this check exists to
                // prevent.
                if (farRoom is null || IsExterior(farRoom))
                {
                    _revealsOutside++;
                    return null;
                }
            }

            return Line.CreateBound(start, end);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The room containing a point, or null for outdoors and unmodelled space. Phase-aware,
    /// because an unphased query returns nothing in a model with more than one phase.
    /// </summary>
    private Room? RoomAt(XYZ point)
    {
        try
        {
            var phase = _phase;
            return phase is not null ? _doc.GetRoomAtPoint(point, phase) : _doc.GetRoomAtPoint(point);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Inserts that leave bare wall on show at floor level: wall openings always, doors only
    /// when they are cased openings (or when the setting says all doors). Windows fall out
    /// naturally - their sills are nowhere near the floor.
    /// </summary>
    private IEnumerable<Element> RevealOpenings(Wall wall, double baseZ)
    {
        IList<ElementId> inserts;
        try
        {
            inserts = wall.FindInserts(true, false, true, true);
        }
        catch
        {
            yield break;
        }

        foreach (var id in inserts)
        {
            Element? insert;
            try { insert = _doc.GetElement(id); }
            catch { continue; }

            if (insert is null) continue;

            BoundingBoxXYZ? box;
            try { box = insert.get_BoundingBox(null); }
            catch { continue; }

            // Must actually reach the floor, or there is no reveal at skirting height.
            if (box is null || box.Min.Z > baseZ + _settings.RevealFloorTolerance) continue;

            // Tested by CLASS, not category. A wall opening's category is
            // OST_SWallRectOpening or OST_ArcWallRectOpening depending on whether the host
            // wall is straight or curved - there is no single OST_Openings - and that split
            // is an implementation detail that could grow another member. Opening is the
            // type they all share.
            if (insert is Opening)
            {
                yield return insert;
                continue;
            }

            if (insert.Category?.Id.Value != (long)BuiltInCategory.OST_Doors) continue;
            if (!_settings.WrapIntoDoorReveals && !IsCasedOpening(insert)) continue;

            yield return insert;
        }
    }

    private bool IsCasedOpening(Element insert)
    {
        var names = new List<string> { SafeName(insert) };

        try
        {
            if (_doc.GetElement(insert.GetTypeId()) is ElementType type)
            {
                names.Add(SafeName(type));
                names.Add(type.FamilyName ?? string.Empty);
            }
        }
        catch
        {
            // Instance name alone.
        }

        foreach (var name in names)
            foreach (var hint in _settings.CasedOpeningHints)
                if (name.Contains(hint, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    // ------------------------------------------------------------- what blocks

    /// <summary>
    /// Doors, windows and wall openings, taken from the wall itself rather than from a
    /// model-wide collector. FindInserts is the authority on what is actually cut into THIS
    /// wall - a door merely standing near it is not an insert and must not break the board.
    /// </summary>
    private IEnumerable<Span> OpeningSpans(Wall wall, Curve axis, double baseZ)
    {
        IList<ElementId> inserts;
        try
        {
            inserts = wall.FindInserts(
                addRectOpenings: true,
                includeShadows: false,
                includeEmbeddedWalls: true,
                includeSharedEmbeddedInserts: true);
        }
        catch
        {
            yield break;
        }

        foreach (var id in inserts)
        {
            Element? insert;
            try { insert = _doc.GetElement(id); }
            catch { continue; }

            if (insert is null) continue;

            // ROOT CAUSE OF THE BOARDS BREAKING BEHIND RADIATORS.
            //
            // FindInserts returns every element HOSTED IN the wall, not just the ones that
            // make a hole in it - a wall-hung radiator, an electrical panel, a hosted
            // cabinet are all inserts. Blocking on all of them meant the M&E rule was
            // enforced in the casework pass and quietly bypassed here, so the one category
            // of element the brief says must never interrupt a board was interrupting it
            // through a second, unguarded path.
            //
            // Only something that genuinely breaches the wall can break the run.
            if (!IsOpeningInsert(insert))
            {
                _hostedNonOpenings++;
                continue;
            }

            if (NeverBlocks(insert))
            {
                _mePassedThrough++;
                continue;
            }

            // ONLY openings that come down into the board's own height band break the run.
            //
            // A window sitting on a metre of wall never touches an 80 mm skirting, so the
            // board runs straight through underneath it - which is how it is built on site,
            // and what stops every window in the model deleting its own width of skirting.
            if (!ReachesBoard(insert, baseZ))
            {
                _openingsAboveBoard++;
                continue;
            }

            // Real jamb geometry first - lining and architrave, which are wider than the
            // hole. Rough Width is the fallback for inserts with no readable solids.
            var span = SkirtingRun.FromJambGeometry(
                           axis,
                           _geometry.ElementSolids(insert),
                           baseZ - _settings.OpeningPad,
                           baseZ + _settings.BoardHeight,
                           // Measured from the RUN, which lies on the wall's room-side face -
                           // not from the centreline. So the reach has to be the full wall
                           // thickness plus the margin, or the far half of the lining falls
                           // outside the test and the jamb measures narrower than it is.
                           SafeWidth(wall) + _settings.JambMargin)
                       ?? SkirtingRun.FromInsert(axis, insert, _doc, _settings.OpeningPad);

            if (span is not { } blocked) continue;

            _openingBreaks++;
            Record(insert, "opening");

            // Pad is zero by default, so the board stops exactly on the frame face.
            yield return new Span(blocked.Start - _settings.OpeningPad, blocked.End + _settings.OpeningPad);
        }
    }

    /// <summary>
    /// Casework standing against this stretch of wall, at skirting height.
    ///
    /// Both filters earn their place. Without the height band a wall-hung cabinet at 1.5 m
    /// deletes the board underneath it; without the reach test a unit on the far side of the
    /// room blocks a run it never touches, because a bounding box says nothing about
    /// distance from a line.
    /// </summary>
    private IEnumerable<Span> CaseworkSpans(Room room, Curve axis, double baseZ)
    {
        foreach (var unit in _casework)
        {
            BoundingBoxXYZ? box;
            try { box = unit.get_BoundingBox(null); }
            catch { continue; }

            if (box is null) continue;

            // Mis-categorised M&E and void cutters. The board passes behind them whatever
            // category someone filed the family under.
            if (NeverBlocks(unit))
            {
                _mePassedThrough++;
                continue;
            }

            // Cheap plan and height rejection before touching geometry.
            if (box.Min.Z > baseZ + _settings.BoardHeight) continue;
            if (box.Max.Z < baseZ) continue;

            // MEASURED FROM SOLIDS, NOT THE BOUNDING BOX.
            //
            // A bounding box is a block from the family's lowest point to its highest, so a
            // wall cabinet whose family carries any low reference geometry reads as reaching
            // the floor. The report proved it: 'W ADJ. H 700 D 330' and 'W 600 H 660 D 330'
            // are wall units hung well above a skirting board, and both were cutting it.
            //
            // Real solid points, filtered to the board's height band and to within reach of
            // this run, answer the physical question instead: is there material against this
            // wall at skirting height? A wall unit has none there and stops blocking.
            var span = SkirtingRun.FromJambGeometry(
                axis,
                _geometry.ElementSolids(unit),
                baseZ,
                baseZ + _settings.BoardHeight,
                _settings.CaseworkReach);

            if (span is not { } blocked) continue;

            _caseworkBreaks++;
            Record(unit, "casework");
            yield return blocked;
        }
    }

    // --------------------------------------------------------------- resolution

    /// <summary>
    /// The family symbol, matched on type name first and family name second, because the
    /// brief names "Skirtingboard_21-80mm" and the Project Browser shows that as a TYPE
    /// under the family "Wall_Sweep V6".
    /// </summary>
    private FamilySymbol ResolveSymbol()
    {
        var symbols = new FilteredElementCollector(_doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .ToList();

        var target = ParameterHelper.Normalize(_settings.TypeName);

        foreach (var symbol in symbols)
            if (ParameterHelper.Normalize(SafeName(symbol)) == target) return symbol;

        foreach (var symbol in symbols)
            if (ParameterHelper.Normalize(SafeFamilyName(symbol)) == target) return symbol;

        foreach (var symbol in symbols)
            if (ParameterHelper.Normalize(SafeName(symbol)).Contains(target, StringComparison.Ordinal))
                return symbol;

        var near = symbols
            .Where(s => SafeName(s).Contains("kirting", StringComparison.OrdinalIgnoreCase) ||
                        SafeFamilyName(s).Contains("weep", StringComparison.OrdinalIgnoreCase))
            .Select(s => $"'{SafeFamilyName(s)} : {SafeName(s)}'")
            .Distinct()
            .Take(12)
            .ToList();

        throw new InvalidOperationException(
            $"No family type matching '{_settings.TypeName}' is loaded in this model. " +
            (near.Count > 0
                ? "Closest candidates: " + string.Join(", ", near)
                : "Nothing resembling a skirting or sweep family is loaded at all - load it first."));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Boundary curves come back at the room's own base. The board goes at floor level plus
    /// the offset, so the curve is lifted rather than the family nudged - an instance whose
    /// geometry is where it should be beats one relying on an offset parameter nobody sees.
    /// </summary>
    private static Curve? LiftTo(Curve curve, double z)
    {
        try
        {
            var dz = z - curve.GetEndPoint(0).Z;

            return Math.Abs(dz) < 1e-9
                ? curve
                : curve.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, dz)));
        }
        catch
        {
            return null;
        }
    }

    private static double FloorElevation(Room room, Level? level)
    {
        try
        {
            var baseOffset = room.get_Parameter(BuiltInParameter.ROOM_LOWER_OFFSET)?.AsDouble() ?? 0.0;
            if (level is not null) return level.Elevation + baseOffset;
        }
        catch
        {
            // Fall through to the bounding box.
        }

        try { return room.get_BoundingBox(null)?.Min.Z ?? 0.0; }
        catch { return 0.0; }
    }

    private IEnumerable<Room> Rooms() =>
        new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .OfType<Room>()
            .Where(r => SafeArea(r) > 0);

    /// <summary>
    /// An 'Udvendig' placeholder room: a terrace or entrance area enclosed with room
    /// separation lines purely so it can be scheduled. It is outdoors, so no wall bounding
    /// it takes skirting - and critically, the walls it shares with the building are that
    /// building's EXTERIOR faces.
    ///
    /// Excluding the room rather than trying to classify its walls is what makes this
    /// correct at a shared wall: boards are placed per room-boundary segment, so skipping
    /// the room removes only its own face and leaves the interior room's face untouched.
    /// </summary>
    /// <summary>
    /// Does this element come down into the board's height band, [floor, floor + board]?
    ///
    /// The one test that decides whether anything interrupts a run. Applied identically to
    /// openings and to casework, because the physical question is identical: is there
    /// something in the way at skirting height, or does the board pass underneath?
    /// </summary>
    /// <summary>
    /// M&amp;E by category or by name, whatever category the family was filed under.
    ///
    /// The category test is the reliable one; the name test is the safety net for families
    /// authored into Casework or Specialty Equipment by mistake, which happens often enough
    /// with radiators to be worth covering.
    /// </summary>
    /// <summary>
    /// Notes one element that actually cut a board.
    ///
    /// Every gap in the finished skirting now has a named cause in the report. Without it,
    /// diagnosing "why is there a break here" means guessing from a screenshot at which of
    /// four rules fired - which is how several rounds of this went.
    /// </summary>
    private void Record(Element element, string why)
    {
        if (_blockedBy.Count > 200) return;   // a report, not a database

        string category;
        try { category = element.Category?.Name ?? "?"; }
        catch { category = "?"; }

        _blockedBy.Add($"{why}: Id {element.Id.Value}, {category}, '{SafeName(element)}'");
    }

    /// <summary>
    /// Does this insert actually breach the wall? Doors, windows and wall openings do.
    /// Everything else hosted in a wall - radiators, panels, sockets, hosted casework -
    /// sits ON it, and a skirting board runs behind them.
    /// </summary>
    private static bool IsOpeningInsert(Element insert)
    {
        if (insert is Opening) return true;

        long? category;
        try { category = insert.Category?.Id.Value; }
        catch { return false; }

        return category == (long)BuiltInCategory.OST_Doors
            || category == (long)BuiltInCategory.OST_Windows;
    }

    private bool NeverBlocks(Element element)
    {
        try
        {
            var category = element.Category?.Id.Value;

            if (category is not null &&
                _settings.NeverBlockCategories.Any(c => (long)c == category))
            {
                return true;
            }
        }
        catch
        {
            // No readable category; fall through to the name test.
        }

        var names = new List<string> { SafeName(element) };

        try
        {
            if (_doc.GetElement(element.GetTypeId()) is ElementType type)
            {
                names.Add(SafeName(type));
                names.Add(type.FamilyName ?? string.Empty);
            }
        }
        catch
        {
            // Instance name alone.
        }

        foreach (var name in names)
            foreach (var hint in _settings.NeverBlockHints)
                if (name.Contains(hint, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private bool ReachesBoard(Element element, double baseZ)
    {
        try
        {
            var box = element.get_BoundingBox(null);
            if (box is null) return false;

            // Its underside must be at or below the top of the board, and it must not stop
            // before the floor.
            return box.Min.Z <= baseZ + _settings.BoardHeight && box.Max.Z >= baseZ;
        }
        catch
        {
            return false;
        }
    }

    private bool IsExterior(Room room)
    {
        var prefix = _settings.ExteriorRoomPrefix;
        if (string.IsNullOrWhiteSpace(prefix)) return false;

        return RoomText(room, BuiltInParameter.ROOM_NAME)
            .TrimStart()
            .StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Is this run on the OUTSIDE face of a wall that separates inside from outside?
    ///
    /// Two facts have to agree before a board is refused, because either alone would be
    /// wrong. The wall's type Function must say it is an envelope wall - an interior
    /// partition has no outside. And the run must lie on the face that
    /// <see cref="Wall.Orientation"/> points at, which is Revit's own definition of the
    /// exterior side and already accounts for the wall having been flipped.
    ///
    /// Requiring both is what keeps skirting on the inside face of every perimeter room.
    /// Refusing whole exterior walls would strip boards from most of the building.
    /// </summary>
    private bool IsOutsideFace(Wall wall, Curve run)
    {
        if (!IsEnvelope(wall)) return false;

        try
        {
            if (wall.Location is not LocationCurve location) return false;

            var here = run.Evaluate(0.5, true);

            var onCentreline = location.Curve.Project(here)?.XYZPoint;
            if (onCentreline is null) return false;

            // Plan only: a vertical component would come from the run sitting at floor
            // level while the centreline is measured elsewhere up the wall.
            var offset = new XYZ(here.X - onCentreline.X, here.Y - onCentreline.Y, 0);
            if (offset.GetLength() < 1e-9) return false;

            return offset.Normalize().DotProduct(wall.Orientation.Normalize()) > 0.5;
        }
        catch
        {
            return false;   // undecidable: keep the board rather than lose it silently
        }
    }

    private bool IsEnvelope(Wall wall)
    {
        try
        {
            if (_doc.GetElement(wall.GetTypeId()) is not WallType type) return false;

            return type.Function is WallFunction.Exterior
                                 or WallFunction.Foundation
                                 or WallFunction.Retaining;
        }
        catch
        {
            return false;
        }
    }

    private bool IsExcluded(Room room)
    {
        // No Department means the room has not been designed yet - a placeholder or a
        // survey artefact. Skirting it produces quantities for space that is not real.
        if (_settings.RequireDepartment &&
            RoomText(room, BuiltInParameter.ROOM_DEPARTMENT).Trim().Length == 0)
        {
            _roomsNoDepartment++;
            return true;
        }

        foreach (var text in new[]
                 {
                     RoomText(room, BuiltInParameter.ROOM_NAME),
                     RoomText(room, BuiltInParameter.ROOM_DEPARTMENT),
                 })
        {
            if (text.Length == 0) continue;

            foreach (var keyword in _settings.ExcludedRoomKeywords)
                if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Deletes every instance this tool has ever placed in the model, identified by its
    /// Comments stamp. Nothing else is touched - a board someone placed by hand carries no
    /// stamp and survives.
    /// </summary>
    private int DeletePrevious()
    {
        var ids = Generated().Select(e => e.Id).ToList();

        if (ids.Count == 0) return 0;

        // Ownership BEFORE deletion. On a central model, deleting an element another user
        // has checked out throws and rolls the whole transaction back - losing every correct
        // board already placed because one is held by a colleague.
        var ownership = Worksharing.Claim(_doc, ids);

        if (ownership.OwnedByOthers.Count > 0)
        {
            _problems.Add(
                $"{ownership.OwnedByOthers.Count} previously generated board(s) are owned by other " +
                "users and were left in place. They will co-exist with the new run until those " +
                "users relinquish them.");

            ids = [.. ownership.Writable];
            if (ids.Count == 0) return 0;
        }

        try
        {
            return _doc.Delete(ids).Count;
        }
        catch (Exception ex)
        {
            _problems.Add($"Could not remove {ids.Count} previous board(s): {ex.Message}. " +
                          "The new run will sit on top of them.");
            return 0;
        }
    }

    /// <summary>
    /// Everything this tool has placed, found by its Extensible Storage mark.
    ///
    /// This used to sweep EVERY FamilyInstance in the document and read a parameter off each
    /// one. On a real project that is tens of thousands of elements and tens of thousands of
    /// parameter reads, twice per run, to find perhaps eighty boards. An
    /// ExtensibleStorageFilter is a quick filter evaluated natively, so the cost now scales
    /// with the number of stamped elements rather than the size of the model.
    ///
    /// The class filter stays in front of it: quick filters compose, and narrowing to
    /// FamilyInstance first is free.
    /// </summary>
    private IEnumerable<Element> Generated()
    {
        var collector = new FilteredElementCollector(_doc)
            .OfClass(typeof(FamilyInstance))
            .WhereElementIsNotElementType();

        var stamped = ElementStamp.Filter();

        // No schema means an older model with Comments stamps only, or storage unavailable.
        // Fall back to the full sweep rather than silently finding nothing and duplicating
        // every board in the model.
        var candidates = stamped is null ? collector : collector.WherePasses(stamped);

        foreach (var element in candidates)
        {
            if (ElementStamp.Read(element, SkirtingSettings.Stamp, SkirtingSettings.Stamp) is not null)
                yield return element;
        }
    }

    /// <summary>
    /// Marks a placed board as ours, in Extensible Storage rather than Comments.
    ///
    /// Comments is a user-facing field. A note typed there used to destroy the mark, which
    /// meant Regenerate could no longer find the board and left an orphan behind. Storage is
    /// invisible, un-editable by hand, and survives copy/paste and synchronisation.
    ///
    /// The source wall is recorded by UniqueId, not ElementId: ids are reassigned by
    /// copy/paste between models, e-transmit and upgrade, so an ElementId written today can
    /// point at an unrelated element after the model has been through a project lifecycle.
    /// </summary>
    private static void Stamp(Element instance, string value, Element? source = null)
    {
        if (ElementStamp.Write(instance, SkirtingSettings.Stamp, value, source?.UniqueId)) return;

        // Storage unavailable. Fall back so the run is still re-runnable, and accept that a
        // user editing Comments can break it.
        try
        {
            var parameter = instance.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (parameter is { IsReadOnly: false }) parameter.Set(value);
        }
        catch
        {
            // Unstamped: the next run may duplicate this one piece.
        }
    }

    public SkirtingResult Result() => new()
    {
        Report = _report,
        RoomsQualifying = _roomsQualifying,
        RoomsExcluded = _roomsExcluded,
        Placed = _placed.Count,
        TotalLength = _totalLength,
        OpeningBreaks = _openingBreaks,
        CaseworkBreaks = _caseworkBreaks,
        RevealPieces = _revealPieces,
        RevealLength = _revealLength,
        RoomsExterior = _roomsExterior,
        Problems = _problems,
    };

    private Phase? PhaseOf(Room room)
    {
        try
        {
            var parameter = room.get_Parameter(BuiltInParameter.ROOM_PHASE);
            return parameter is null ? null : _doc.GetElement(parameter.AsElementId()) as Phase;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryPointInRoom(Room room, XYZ point)
    {
        try { return room.IsPointInRoom(point); }
        catch { return false; }
    }

    private static double SafeArea(Room room)
    {
        try { return room.Area; }
        catch { return 0.0; }
    }

    private static string SafeName(Element element)
    {
        try { return element.Name; }
        catch { return string.Empty; }
    }

    private static string SafeFamilyName(FamilySymbol symbol)
    {
        try { return symbol.Family?.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string RoomText(Room room, BuiltInParameter parameter)
    {
        try { return room.get_Parameter(parameter)?.AsString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string Label(Room room)
    {
        var label = $"{RoomText(room, BuiltInParameter.ROOM_NUMBER)} " +
                    $"{RoomText(room, BuiltInParameter.ROOM_NAME)}".Trim();

        return label.Length > 0 ? label : room.Id.Value.ToString();
    }
}
