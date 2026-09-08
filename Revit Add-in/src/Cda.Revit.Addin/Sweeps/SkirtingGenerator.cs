using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Cda.Revit.Addin.Finishes;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Rooms;

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

    /// <summary>
    /// Every door, window and opening family in the model, for the geometry-avoidance pass.
    /// Collected once: the per-run cost is a bounding-box rejection, not a query.
    /// </summary>
    private List<Element> _openingModels = [];
    private SkirtingPlacer? _placer;
    private readonly FinishGeometry _geometry;

    /// <summary>Pieces whose length could not be driven to match their run.</summary>
    private int _wrongLength;

    private int _revealPieces;
    private double _revealLength;

    /// <summary>Reveals suppressed because the far side is outdoors or unmodelled.</summary>
    private int _revealsOutside;

    /// <summary>Openings skipped by a run because they are cut into another part of the wall.</summary>
    private int _revealsElsewhere;

    /// <summary>Jambs given no board because the family's own lining already fills the reveal.</summary>
    private int _revealsLined;

    /// <summary>Jambs whose board was cut back to the depth the family leaves bare.</summary>
    private int _revealsPartlyLined;

    /// <summary>Runs cut back off an opening family's own solids, whichever wall hosts it.</summary>
    private int _openingGeometryBreaks;

    /// <summary>Boundary faces skirted whose host is not a wall - columns, piers.</summary>
    private int _nonWallHosts;

    /// <summary>Rooms with no Finish boundary at all, skirted from their Center boundary.</summary>
    private int _fellBackToCentre;

    /// <summary>Runs whose room lies to the RIGHT of travel - see SeatAgainstWall.</summary>
    private int _roomOnRight;

    /// <summary>
    /// One line per jamb: what happened to it and why.
    ///
    /// A COUNT IS NOT AN AUDIT. "0 jamb boards" is indistinguishable from "the jamb pass is
    /// broken" until every jamb can be named with its outcome. Both suppressions here are
    /// deliberate - a lined family would double up, an opening onto outdoors would put a
    /// board through the external face - and both are indistinguishable from a fault unless
    /// they are stated per opening.
    /// </summary>
    private readonly List<string> _jambAudit = [];

    /// <summary>Openings that reached the jamb pass on at least one run.</summary>
    private readonly HashSet<long> _revealOpeningsSeen = [];

    /// <summary>Openings that produced at least one audit line.</summary>
    private readonly HashSet<long> _revealOpeningsAudited = [];

    private void RecordJamb(Element insert, int jamb, double length, string outcome)
    {
        try { _revealOpeningsAudited.Add(insert.Id.Value); } catch { /* id unreadable */ }

        if (_jambAudit.Count >= 200) return;

        string name;
        try { name = SafeName(insert); }
        catch { name = "?"; }

        var side = jamb == 0 ? "A" : "B";
        var measured = length > 0 ? $"{Measure.ToMillimetres(length):0} mm - " : string.Empty;

        _jambAudit.Add($"    {insert.Id.Value} '{name}' jamb {side}: {measured}{outcome}");
    }

    /// <summary>Inserts skipped by the geometry pass because the jamb pass already measured them.</summary>
    private int _openingsAlreadyMeasured;

    /// <summary>Candidate spans removed because another board already stands there.</summary>
    private int _overlapsTrimmed;

    /// <summary>Boards whose side came from the host wall because the room could not say.</summary>
    private int _inwardFromHost;

    /// <summary>Boards whose side could not be determined at all - these escape the overlap check.</summary>
    private int _inwardUnknown;

    /// <summary>The ids of everything cut into a wall, cached per wall for the run.</summary>
    private readonly Dictionary<long, HashSet<long>> _insertCache = [];

    /// <summary>
    /// Everything cut into this wall. Empty for a non-wall host, which has no inserts.
    /// </summary>
    private IReadOnlySet<long> InsertsOf(Wall? wall)
    {
        if (wall is null) return new HashSet<long>();

        if (_insertCache.TryGetValue(wall.Id.Value, out var cached)) return cached;

        var ids = new HashSet<long>();

        try
        {
            foreach (var id in wall.FindInserts(true, false, true, true)) ids.Add(id.Value);
        }
        catch
        {
            // Unreadable; the geometry pass then measures them, which is the safe direction.
        }

        _insertCache[wall.Id.Value] = ids;
        return ids;
    }

    /// <summary>Whether the family exposes writable end-angle parameters. Decides the corner strategy.</summary>
    private bool _canMitre;

    /// <summary>Board ends actually cut to a corner angle.</summary>
    private int _endsMitred;

    /// <summary>Ends the family refused to cut after reporting it could. Should be zero.</summary>
    private int _mitreRefused;

    /// <summary>
    /// The end-cut angle for a corner, in radians, measured from the plane perpendicular to
    /// the board's axis: <c>90 - interior/2</c> degrees.
    ///
    /// One formula covers every corner. At 90 degrees interior it gives 45 - an ordinary
    /// mitre. At 135 it gives 22.5. Past 180, at an external corner, it goes negative, which
    /// is the same cut in the opposite hand - so partition ends need no special case at all.
    /// Both boards meeting at the corner take the same value; which end it is applied to is
    /// what mirrors it.
    ///
    /// Clamped to +/-60 degrees. Beyond that the cut runs longer than the board is deep and
    /// the family would be asked for geometry it cannot make; such a corner is butted.
    /// </summary>
    private static double? MitreAngle(double interior)
    {
        if (double.IsNaN(interior) || interior <= 1e-6) return null;

        var angle = (Math.PI / 2.0) - (interior / 2.0);

        return Math.Abs(angle) > (Math.PI / 3.0) ? null : angle;
    }

    /// <summary>Boards whose location curve had to be written back to the intended one.</summary>
    private int _conformed;

    /// <summary>Boards whose built length was compared with the length asked for.</summary>
    private int _lengthChecked;

    /// <summary>Boards Revit built at a different length than the curve given.</summary>
    private int _lengthMismatched;

    /// <summary>Total signed difference, so a consistent bias is visible as one number.</summary>
    private double _lengthDrift;

    /// <summary>The largest single discrepancy seen.</summary>
    private double? _lengthWorst;

    /// <summary>Corners filled to the apex rather than trimmed, because they are not square.</summary>
    private int _cornersFilled;

    /// <summary>Filled corners where Revit resolved the shared volume with a join.</summary>
    private int _cornersJoined;

    /// <summary>Filled corners Revit refused to join - a genuine remaining overlap.</summary>
    private int _joinRefused;

    /// <summary>The piece reaching the previous face's far corner, waiting to be joined.</summary>
    private ElementId? _previousEndPiece;

    /// <summary>
    /// Hands a filled corner to Revit to resolve.
    ///
    /// JoinGeometry makes exactly one of the two elements own the volume they share, so a
    /// corner that is deliberately over-filled stops being two boards occupying one space and
    /// becomes one continuous mitred run. It is the only mechanism that satisfies "no gaps"
    /// and "no overlaps" at the same corner, because a square-cut component cannot.
    ///
    /// Revit does not document which element types may be joined - only that it throws when
    /// they cannot - so this attempts the join and counts the refusals rather than assuming.
    /// A refusal is a real remaining overlap and is reported as one.
    /// </summary>
    private void JoinAtCorner(ElementId? arriving, ElementId? leaving)
    {
        if (!_settings.JoinAtCorners || arriving is null || leaving is null) return;
        if (arriving == leaving) return;

        try
        {
            if (JoinGeometryUtils.AreElementsJoined(_doc, _doc.GetElement(arriving), _doc.GetElement(leaving)))
            {
                _cornersJoined++;
                return;
            }

            JoinGeometryUtils.JoinGeometry(_doc, _doc.GetElement(arriving), _doc.GetElement(leaving));
            _cornersJoined++;
        }
        catch
        {
            // "The elements cannot be joined." The corner stays filled, which honours the
            // no-gap rule, and the shared material is reported instead of hidden.
            _joinRefused++;
        }
    }

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

    /// <summary>
    /// Openings genuinely hosted in a wall, but not on THIS room's short stretch of it -
    /// see the guard in <see cref="OpeningSpans"/>. Confirmed 2026-09-04 against a door
    /// hosted on wall 29307987 (shared by five rooms) that connects Bad and Gang, over 2 m
    /// from Entre's own 1450 mm stretch of that same wall: Curve.Project clamped every point
    /// of its geometry onto Entre's boundary curve anyway, manufacturing a blocker at Entre's
    /// corner that had nothing to do with Entre. Same defect class PlaceReveals' OnRun guard
    /// already protects against - this is the same fix, for the pass that matters more.
    /// </summary>
    private int _openingsElsewhere;

    /// <summary>Boundary segments folded into a neighbour because they were collinear.</summary>
    private int _segmentsMerged;

    private int _removed;

    /// <summary>Wall faces that ended up with no board at all, and why.</summary>
    private readonly List<string> _emptyRuns = [];

    /// <summary>List-adjacent runs that do not actually meet, so carry no corner.</summary>
    private int _detachedNeighbours;

    /// <summary>
    /// The profile's FULL width across, measured before any board is placed.
    ///
    /// THE ROOT CAUSE OF EVERY CORNER FAULT SO FAR, and it took measuring the model to find.
    /// The old value was the largest OFFSET FROM THE INSERTION LINE, and the profile turns
    /// out to be centred on that line - so it returned the HALF width, 20 mm of a 40 mm
    /// section. The corner trim then used it as though it were the board's full depth, and
    /// under-trimmed every corner by the other half. That is exactly the 20 mm x 20 mm square
    /// found shared between two boards at a corner in T05.
    ///
    /// It also explains why six rounds of changing WHICH side is trimmed and WHETHER it is
    /// trimmed never fixed it: none of them touched HOW MUCH, and how much was half of what
    /// it should have been the whole time.
    /// </summary>
    private double _profileWidth;

    /// <summary>How far the profile hangs behind its own insertion line. See CalibrateProfile.</summary>
    private double _profileOverhang;

    /// <summary>The depth every corner is computed from: the real width, or the guess.</summary>
    private double CornerDepth => _profileWidth > 0.003 ? _profileWidth : _settings.BoardDepth;

    /// <summary>The measured height of the board's solid. Zero until calibration has run.</summary>
    private double _profileHeight;

    /// <summary>
    /// The height band that decides whether something interrupts a run - measured where
    /// possible, configured otherwise. See CalibrateProfile: the configured 80 mm came from
    /// the type name and the solid is 63 mm, and the difference is pure over-blocking.
    /// </summary>
    private double BoardBand => _profileHeight > 0.003 ? _profileHeight : _settings.BoardHeight;

    /// <summary>Blocked spans absorbed into a neighbour because they nearly touched.</summary>
    private int _blockersCoalesced;

    /// <summary>Corners where the boards diverge, so no trim is owed.</summary>
    private int _reentrantCorners;

    /// <summary>External corners - partition ends - where a board was carried round the outside.</summary>
    private int _externalCornersWrapped;

    /// <summary>
    /// Corner pairs whose footprints genuinely intersect. The number the no-overlap standard
    /// actually turns on, and nothing measured it until now - the coverage test only ever
    /// compared parallel boards, so every corner in the model went unchecked.
    /// </summary>
    private int _cornerOverlaps;

    private int _clippedToRoom;
    private int _droppedOutsideRoom;

    /// <summary>
    /// The room solid the containment clip is answered against, rebuilt per room. Null when
    /// this room has none, which is the only case that falls back to sampling.
    /// </summary>
    private RoomContainment? _containment;

    /// <summary>
    /// Rooms whose solid could not be built, so their boards were clipped by IsPointInRoom
    /// sampling instead. The sampling path is the one that cannot distinguish "outside" from
    /// "could not tell", so this number is how much of the run is still exposed to that.
    /// </summary>
    private int _roomsWithoutSolid;

    /// <summary>
    /// Boards REFUSED because containment could not answer for them.
    ///
    /// THE NUMBER THAT DID NOT EXIST. Every failure in the old clip returned the board
    /// unchanged - an undecidable side, an exception, anything - so a board driven through a
    /// wall and a board legitimately left alone were the same event and neither was counted.
    /// A board through a wall reads as deliberate and gets built; a refused board with a
    /// reason attached gets fixed. This is now a refusal, and it is counted.
    /// </summary>
    private int _clipUndecidable;

    /// <summary>
    /// The subset of <see cref="_clipUndecidable"/> that was actually REFUSED - the solid
    /// path. The rest were placed unverified, because their room had no solid to refuse them
    /// against. Kept separate so the report can say which of the discarded boards were
    /// outside the room and which were merely unprovable; they are different faults with
    /// different fixes, and one number cannot carry both.
    /// </summary>
    private int _clipRefused;

    /// <summary>
    /// Boards placed with NO containment test at all, because the setting is off or the
    /// piece is not a straight line. Not a failure - but it was previously indistinguishable
    /// from a board that passed the test.
    /// </summary>
    private int _clipNotAttempted;

    /// <summary>Distinct reasons containment declined, for the problem list.</summary>
    private readonly HashSet<string> _clipReasons = new(StringComparer.Ordinal);

    /// <summary>
    /// Containments for rooms on the far side of an opening, built once each. The same few
    /// rooms sit across every opening in a unit, and a spatial element calculation per jamb
    /// would cost more than the whole rest of the run.
    /// </summary>
    private readonly Dictionary<long, RoomContainment?> _farContainment = [];

    /// <summary>
    /// How far past the wall's own Width a measured far face may sit and still be believed.
    /// Half a wall again is generous enough for finishes the Width parameter does not carry,
    /// and tight enough to reject a ray that found the room beyond a cavity.
    /// </summary>
    private const double MaxRevealDepthFactor = 1.5;

    /// <summary>Jambs whose room side was settled by comparing two ray lengths.</summary>
    private int _revealSideFromRay;

    /// <summary>
    /// Jambs where neither the ray nor the probes could say which side the room is on. These
    /// get no reveal board at all, and the number was previously invisible.
    /// </summary>
    private int _revealSideUnknown;

    /// <summary>Reveals whose far end was measured to a face other than wall Width.</summary>
    private int _revealDepthMeasured;

    /// <summary>The worst of those disagreements, in internal units.</summary>
    private double _revealDepthWorst;

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
    /// Every board line already standing, indexed by elevation, for the pre-creation
    /// collision check. Boards this run placed and boards it found and could not remove both
    /// go in here - a board occupies its line whoever put it there.
    ///
    /// BUCKETED BY HEIGHT, NOT BY HOST. It was one flat list of every board in the model,
    /// scanned in full for every candidate: quadratic, and on a real project the boards
    /// vastly outnumber anything else.
    ///
    /// Height is the right key and the host id is not, which is worth being explicit about
    /// because the host looks like the obvious choice. Two boards on DIFFERENT hosts collide
    /// routinely - a column set flush into a wall presents its own boundary segment along the
    /// same line as the wall's, and both would be skirted. Bucketing by host puts those two
    /// in different buckets and never compares them, which is precisely the overlap along a
    /// joined path that has to be caught. Elevation separates the only thing that genuinely
    /// cannot interact, which is one storey from another.
    /// </summary>
    private readonly Dictionary<long, List<PlacedBoard>> _occupied = [];

    /// <summary>
    /// A board that is standing, with the direction its material actually went.
    ///
    /// The inward normal has to be REMEMBERED, not recomputed. A board occupies the room side
    /// of its line and which side that is depends on the room it was placed for - a fact that
    /// is available at placement and gone by the time a later candidate is compared with it.
    /// Guessing it from the line alone is what made the corner-overlap check meaningless: it
    /// used a fixed perpendicular, so half the boards were tested on the wrong side.
    /// </summary>
    private readonly record struct PlacedBoard(Line Line, XYZ? Inward);

    /// <summary>
    /// Height of one bucket, 1 foot. Comfortably finer than a storey and comfortably coarser
    /// than the <see cref="SkirtingSettings.BoardHeight"/> test that follows it, so querying
    /// a cell and its two neighbours cannot miss a board the fine test would have matched.
    /// </summary>
    private const double OccupancyCell = 1.0;
    private int _separationLines;
    private int _linkedBoundaries;
    private int _nonWallBoundaries;
    private readonly HashSet<string> _droppedKinds = [];

    /// <summary>Full per-face trace. Goes to the log file, not the dialog.</summary>
    private readonly List<string> _trace = [];

    /// <summary>
    /// One continuous stretch of room-bounding face to be skirted.
    ///
    /// <paramref name="Host"/> is whatever bounds the room - usually a wall, but a column or
    /// any other host object presents a face at floor level just the same.
    /// <paramref name="Wall"/> is that same element when it IS a wall, and null otherwise:
    /// inserts, reveals and thickness are wall-only questions, and null is the honest answer
    /// for a column rather than a cast that throws.
    /// </summary>
    private sealed record BoundaryRun(Element Host, Wall? Wall, Curve Axis, long SegmentId);

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
            Element? host;
            Curve? curve;

            try
            {
                host = _doc.GetElement(segment.ElementId);
                curve = segment.GetCurve();
            }
            catch
            {
                continue;
            }

            // WHY A FACE CAN VANISH WITHOUT A TRACE.
            //
            // A boundary segment yields no skirtable host for several quite different
            // reasons, and only some are benign: a room separation line (nothing to fix a
            // board to), a wall living in a LINKED model (segment.ElementId is meaningless in
            // this document - the id belongs to the link), or an element that is not a wall.
            //
            // THAT LAST ONE WAS A HARD CAST, AND IT WAS WRONG. 'as Wall' discarded columns,
            // piers and every other host object presenting a face to the room, and the
            // discard was invisible except as a counter in the report. A room with a column
            // in it simply lost that face. Only elements with genuinely nothing behind them
            // are refused now - see IsSkirtableBoundary.
            if (host is null || curve is null || !IsSkirtableBoundary(host))
            {
                RecordDroppedSegment(segment);
                continue;
            }

            var wall = host as Wall;
            var previous = runs.Count > 0 ? runs[^1] : null;

            if (previous is not null &&
                previous.Host.Id == host.Id &&
                TryMerge(previous.Axis, curve) is { } merged)
            {
                runs[^1] = previous with { Axis = merged };
                _segmentsMerged++;
                continue;
            }

            if (wall is null) _nonWallHosts++;

            runs.Add(new BoundaryRun(host, wall, curve, segment.ElementId.Value));
        }

        // THE LOOP IS A RING, AND THE LIST IS NOT.
        //
        // A boundary loop has no first segment - Revit just has to start somewhere, and
        // where it starts is usually the middle of a wall. Merging only forwards therefore
        // leaves the seam unmerged: the last run and the first are collinear fragments of one
        // wall, held apart purely by where the list was cut.
        //
        // Left alone that seam behaves as a corner. It is a straight join, so the mitre
        // resolves to zero and no board is trimmed - but the two fragments are planned,
        // clipped and collision-checked independently, and a blocker landing on the seam is
        // measured twice. Folding the ring shut removes the artefact entirely.
        if (runs.Count > 1)
        {
            var last = runs[^1];
            var first = runs[0];

            if (last.Host.Id == first.Host.Id && TryMerge(last.Axis, first.Axis) is { } closed)
            {
                runs[0] = first with { Axis = closed };
                runs.RemoveAt(runs.Count - 1);
                _segmentsMerged++;
            }
        }

        return runs;
    }

    /// <summary>
    /// Is there a physical face behind this boundary, for a board to be fixed to?
    ///
    /// Walls always qualify. Everything else qualifies unless it is on the refused list or is
    /// a bare curve - a room separation line is a real element with a real boundary curve and
    /// nothing at all behind it, and skirting one puts a board across the open side of a
    /// room.
    /// </summary>
    private bool IsSkirtableBoundary(Element host)
    {
        if (host is Wall) return true;
        if (!_settings.SkirtNonWallBoundaries) return false;

        // A model line or symbolic curve carries a boundary but no material.
        if (host is CurveElement) return false;

        try
        {
            var category = host.Category?.Id.Value;
            if (category is null) return false;

            return !_settings.NonSkirtableBoundaryCategories.Any(c => (long)c == category);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Names what a boundary segment was, when it produced no skirtable face in this
    /// document. The distinction matters: a separation line is expected, a linked wall is a
    /// real gap in coverage that needs a different fix.
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

        // Before anything is planned. Seating and every corner trim depend on the profile,
        // so measuring it after the first board is placed is measuring it too late.
        CalibrateProfile();

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
                // DirectShapes excluded here for the same reason as the opening sweep below:
                // generated geometry is not furniture. Casework alone is unaffected today,
                // but BlockingCategories is configurable, and adding Generic Models to it
                // would otherwise reproduce the whole-face suppression exactly.
                _casework.AddRange(new FilteredElementCollector(_doc)
                    .OfCategory(category)
                    .WhereElementIsNotElementType()
                    .Where(e => e is not DirectShape));
            }
        }

        _openingModels = [];
        var carriersIgnored = 0;

        if (_settings.AvoidOpeningGeometry)
        {
            foreach (var category in new[]
                     {
                         BuiltInCategory.OST_Doors,
                         BuiltInCategory.OST_Windows,
                         BuiltInCategory.OST_GenericModel,
                     })
            {
                var found = new FilteredElementCollector(_doc)
                    .OfCategory(category)
                    .WhereElementIsNotElementType()
                    .ToList();

                // DIRECTSHAPES ARE NEVER OPENING GEOMETRY, and letting them in here stopped
                // this tool placing a single wall board.
                //
                // Paint takeoff carriers - ours and PaintedMaterialTakeoff's alike - are
                // DirectShapes in Generic Models, one per painted wall face, occupying that
                // face exactly. This sweep exists to cut boards back off a door's architrave
                // and lining, so a carrier lying flat on the wall reads as an architrave the
                // width of the entire wall, and every run gets trimmed to nothing. The run
                // that found this placed 6 jamb reveals totalling 0.30 m and no wall boards at
                // all, while reporting "0 problem(s)" - the faces were all accounted for as
                // 'covered', which is exactly what a full-face obstruction means.
                //
                // The distinction is not a heuristic: an architrave, a lining and a cased
                // opening are FAMILY INSTANCES. A DirectShape is generated geometry that some
                // other tool put in the model, and none of it is something a skirting board
                // has to dodge.
                carriersIgnored += found.Count(e => e is DirectShape);

                _openingModels.AddRange(found.Where(e => e is not DirectShape));
            }
        }

        if (carriersIgnored > 0)
            _report.Add(
                $"GENERATED GEOMETRY IGNORED: {carriersIgnored} DirectShape(s) were excluded from " +
                "the opening-geometry pass. Paint takeoff carriers sit flat on a wall face in " +
                "Generic Models, and treating one as an obstruction suppresses the whole face - " +
                "which is why a model with a paint takeoff in it used to come back with jamb " +
                "reveals and no wall boards. Only family instances are dodged; generated " +
                "geometry another tool placed is not something a board collides with.");

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

        if (_clipReasons.Count > 0)
        {
            _problems.Add(
                $"{_clipUndecidable} board(s) could not be verified against their room. Distinct " +
                "reasons follow - each one is a place where a board's position rests on nothing " +
                "having been measured:");

            // Capped: a systemic fault produces one reason per room, and a problem list
            // nobody reads to the end is a problem list that hides the other entries.
            foreach (var reason in _clipReasons.Take(20)) _problems.Add($"    {reason}");

            if (_clipReasons.Count > 20)
                _problems.Add($"    ... and {_clipReasons.Count - 20} more, in the log.");
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
                $"JAMBS AND REVEALS: {_revealPieces} board(s) totalling " +
                $"{Measure.ToMetres(_revealLength):0.00} m wrap across the wall thickness exposed " +
                "inside openings - door jambs, cased openings and wall openings, wherever the " +
                "opening reaches the floor. Each is stamped " +
                $"'{SkirtingSettings.Stamp}:reveal:<openingId>:<jamb>' in Extensible Storage, so a " +
                "schedule can separate reveal returns from wall runs, and the two rooms either " +
                "side of an opening cannot each place one. " +
                $"{_revealsElsewhere} opening(s) were skipped by a given run for being cut into " +
                "another part of the same wall - they are picked up by the run that actually " +
                "contains them, which is what stops a jamb board appearing at a corner.");

            if (_jambAudit.Count > 0)
            {
                _report.Add(
                    $"EVERY JAMB, ACCOUNTED FOR - {_jambAudit.Count} listed, which should be twice " +
                    "the number of openings that REACH THE FLOOR. A window sitting on a sill has " +
                    "no jamb at skirting height and is correctly absent - it never enters this " +
                    "pass at all, so do not read its absence as a miss. What this list is for is " +
                    "the openings that do reach the floor: each appears exactly once per jamb, " +
                    "with what it got and why. A bare count cannot tell a deliberate suppression " +
                    "from a missed segment; this can:");

                _report.AddRange(_jambAudit);
            }

            // RECONCILIATION. The audit's whole value is that absence means "never reached",
            // and that only holds if absence is impossible for an opening the pass actually
            // handled. An opening offered to several runs and refused by all of them - each
            // refusal correct on its own terms, because the opening is not on THAT stretch -
            // leaves no line anywhere. This names those, and it is the only entry in the
            // report that indicates a defect rather than a decision.
            var unaudited = _revealOpeningsSeen.Except(_revealOpeningsAudited).ToList();

            _report.Add(
                unaudited.Count == 0
                    ? $"JAMB RECONCILIATION: every one of the {_revealOpeningsSeen.Count} opening(s) " +
                      "that reached the jamb pass produced an audit line above. Nothing was dropped " +
                      "silently."
                    : $"JAMB RECONCILIATION - {unaudited.Count} OPENING(S) DROPPED WITH NO AUDIT " +
                      $"LINE: id(s) {string.Join(", ", unaudited.Take(20))}. These reached the jamb " +
                      "pass and were then refused by every run they were offered to, so they appear " +
                      "nowhere above. THIS IS A DEFECT, not a rule: each refusal is individually " +
                      "correct - the opening is not on that stretch of wall - but no run ever " +
                      "claimed them, so their jambs were never considered by anything.");

            _report.Add(
                "NO DOUBLING UP AT A LINED DOOR: each jamb is intersected with the opening " +
                "family's own solids and a board is placed only on what the family leaves bare. " +
                $"{_revealsLined} jamb(s) got NO board because the family lines the reveal in " +
                $"full; {_revealsPartlyLined} were cut back to the uncovered depth. That is " +
                "measured per family rather than configured per project, because whether a " +
                "reveal is already covered is a property of the door type and not of the job - " +
                "a lined door, a frame at one face and a bare cased opening all occur in the " +
                "same model, and any single switch is wrong for two of the three. It is also " +
                "what guarantees nothing here touches the door model: the door is subtracted " +
                "before an instance exists, so a board is never created in its space." +
                (_settings.HostRevealsOnWall
                    ? " Jamb boards are hosted on the wall face."
                    : " Jamb boards are placed UNHOSTED - they cross the wall's side faces at " +
                      "right angles and belong to the opening, so they take no host " +
                      "relationship with the wall and stay clear of the door's lining logic. " +
                      "Wall sweeps remain wall-hosted."));
        }

        _report.Add(
            $"HEIGHT RULE: only things reaching below {Measure.ToMillimetres(BoardBand):0} mm break " +
            $"the run. {_openingsAboveBoard} opening(s) were ignored for sitting entirely above " +
            "the board - windows with wall beneath them, so the board runs straight through. " +
            $"{_mePassedThrough} M&E element(s) passed through rather than cut around.");

        _report.Add(
            $"ON THIS RUN: {_openingsElsewhere} insert(s) hosted in a wall were NOT measured " +
            "against a run because they sit on a different room's stretch of the same wall - " +
            "walls shared by several rooms host every one of those rooms' doors, and Curve.Project " +
            "clamps a far-away point onto the nearest end of a bound curve rather than rejecting " +
            "it. Unguarded, that turns a door on the OTHER side of a shared wall into a phantom " +
            "blocker sitting exactly at this run's corner - a notch with no element anywhere near " +
            "it. Same guard as PlaceReveals' OnRun, applied here to the run-subtraction pass " +
            "instead of the jamb pass, which is where it costs a whole board rather than a return.");

        _report.Add(
            $"DOOR GEOMETRY AVOIDED: {_openingGeometryBreaks} run(s) were cut back off the real " +
            $"solids of a door, window or opening family ({_openingModels.Count} considered). This " +
            "is separate from the opening breaks above and catches what those cannot: openings " +
            "are found per WALL, so a door hosted in the wall around the corner never appears in " +
            "this wall's insert list, and its architrave and lining sit on this wall's face " +
            "regardless. That is the collision at partition ends and wall junctions next to a " +
            "doorway. Measured against solids rather than bounding boxes, so a swung leaf costs " +
            $"no board. {_openingsAlreadyMeasured} insert(s) hosted in THIS run's own wall were " +
            "skipped here rather than measured twice - the jamb pass already subtracted them, " +
            "and re-subtracting a door's full frame and swung leaf on top of its own opening is " +
            "what once cost one 2370 mm face 992 mm of board across four doors it had already " +
            "accounted for. This number should track the doors actually hosted in the run's own " +
            "wall; a run of zero on a wall known to carry doors means the two passes have stopped " +
            "agreeing on which openings belong to it, and the double count is back.");

        _report.Add(
            $"MITRE JOINS: {_cornersClosed} corner(s) resolved. The board ARRIVING at a corner " +
            "runs through it to the apex - the point where the two boundary faces genuinely " +
            "cross, which is not always where either curve stops - and the board LEAVING starts " +
            "where its own strip clears the arriving one, depth / sin(interior). That is the " +
            "width of the rhombus two board strips of that depth cut out of each other, and it " +
            "is NOT the mitre length depth / tan(interior/2): the two agree at 90 degrees and " +
            "nowhere else. One continuous board turning the corner, one landing on its face, no " +
            $"gap and no shared material. {_reentrantCorners} corner(s) were EXTERNAL - the " +
            "boundary turning round the end of a partition - and those are the mirror case: the " +
            "boards there do not overlap, they fail to meet, and a notch the size of the board " +
            "wrapped round the corner is left behind. Trimming is wrong for them and so is doing " +
            $"nothing, which is what used to happen. {_externalCornersWrapped} were carried round " +
            "the outside by depth / tan(solid / 2), which closes the notch to zero and, because " +
            "the board runs out over a face the other board does not occupy, shares no material " +
            "with it.");

        _report.Add(
            $"CONTINUITY: {_hostedNonOpenings} wall-hosted element(s) were ignored as blockers for " +
            "not being openings - radiators, panels, sockets and hosted casework sit ON a wall " +
            "rather than breaching it, so the board runs behind them unbroken. " +
            $"{_segmentsMerged} boundary segment(s) were folded into a neighbour because they were " +
            "collinear fragments of the same host - including the seam where the boundary loop " +
            "was cut, which is a ring and not a list. Without that, every partition meeting a " +
            $"wall would read as a corner and leave a sliver of missing board. {_detachedNeighbours} " +
            "run pair(s) were adjacent in the boundary list but not touching in the model - " +
            "separated by a room separation line - and correctly carry no corner between them. " +
            $"{_nonWallHosts} boundary face(s) were skirted whose host is NOT a wall - a column " +
            "or pier standing in a room presents a face at floor level like any other, and a " +
            "hard cast to Wall used to discard every one of them silently.");

        _report.Add(
            $"OVERLAP PREVENTION: every candidate was checked against the boards already " +
            $"standing on its own host before it was created. {_coverageMatches} match(es) were " +
            "found and the candidate cut back to the stretch nothing covered; " +
            $"{_refusedOverlaps} piece(s) were left with nothing to place and refused outright. " +
            (_closestExisting == double.MaxValue
                ? "No existing board ever shared a line with a candidate. "
                : $"The closest any existing board came to a candidate's line was " +
                  $"{Measure.ToMillimetres(_closestExisting):0.0} mm sideways. ") +
            $"A board counts as coverage within {Measure.ToMillimetres(JoinTolerance):0} mm " +
            "sideways of the candidate's line and on the same floor, whatever it is hosted on - " +
            "a column set flush into a wall is skirted along the same line as the wall, and both " +
            "sides of that must see each other. Boards a previous run left behind because " +
            "another user owns them are included. " +
            $"{_droppedRatherThanOverlap} further run(s) were too short to survive their own " +
            "corner trim and were not placed: a piece that can only exist by growing into its " +
            "neighbour is not a piece anyone would cut and fit.");

        _report.Add(
            _canMitre
                ? $"CORNER MODE - MITRED: the family exposes writable end-angle parameters, so both " +
                  "boards at a corner run to the apex and their ends are cut on the bisector at " +
                  "90 - interior/2 degrees. The corner is completely filled, so there is no gap, " +
                  "and complementary angled cuts occupy disjoint volumes, so there is no shared " +
                  $"material. {_endsMitred} board end(s) were cut. {_mitreRefused} were refused " +
                  "after the family reported it could cut them - THAT NUMBER MUST BE ZERO: a board " +
                  "run to the apex and then not cut overlaps its neighbour, which is worse than " +
                  "the step it was trying to avoid."
                : "CORNER MODE - BUTTED: the family exposes no writable end-angle parameter, so " +
                  "corners cannot be mitred and are butted instead - one board runs through, the " +
                  "next is trimmed clear of it. That never overlaps, and it is exact at 90 " +
                  "degrees, but it always leaves a step one board deep where the square end meets " +
                  "the face, and away from 90 degrees it must leave a gap. This is a limit of the " +
                  "FAMILY, not of the placement: LocationCurve.JoinType is walls-only, " +
                  "JoinGeometry is refused for this category, and a square end cannot follow a " +
                  "slanted joint line. Add 'Angle Start' and 'Angle End' instance parameters " +
                  "driving void cuts at each end of the extrusion and this run switches to " +
                  "MITRED automatically, with no change here.");

        _report.Add(
            $"QUANTITY BASIS: every length in this report is the length Revit BUILT, read back " +
            $"from each instance after placement - not the length the rules asked for. " +
            $"{_conformed} board(s) had their location curve written back because the placement " +
            "had moved them off it: a line-based instance whose end sits short of a wall corner " +
            "gets extended onto it, which silently undoes the corner trim and adds material that " +
            "is not in the design. For a digital twin that difference is the whole point - a " +
            "takeoff that reports intent rather than geometry describes a building that was " +
            "never built.");

        _report.Add(
            $"LENGTH FIDELITY: {_lengthMismatched} of {_lengthChecked} board(s) were built at a " +
            "different length than the curve they were placed on" +
            (_lengthMismatched == 0
                ? " - the geometry follows the placement curve, so the corner rules mean what " +
                  "they say."
                : $", averaging {Measure.ToMillimetres(_lengthDrift / Math.Max(1, _lengthMismatched)):+0.0;-0.0} mm " +
                  $"and worst {Measure.ToMillimetres(_lengthWorst ?? 0):+0.0;-0.0} mm. READ THIS " +
                  "BEFORE ANY CORNER NUMBER BELOW. Every corner rule here acts on the placement " +
                  "curve; if the instance does not take that curve's length, the trims are " +
                  "computed correctly and then thrown away by the family. A board built longer " +
                  "than its curve grows back over the corner it was trimmed clear of and " +
                  "overlaps its neighbour by the difference - and no check downstream can see " +
                  "it, because they all test the curves, which butt correctly. Fix the family's " +
                  "length constraint before judging anything else in this report."));

        _report.Add(
            $"CORNERS FILLED AND JOINED: {_cornersFilled} corner(s) were filled rather than trimmed " +
            "was trimmed there - both boards run into the corner and it is completely filled. " +
            $"{_cornersJoined} of those were then JOINED, which is how the material they share " +
            "stops being an overlap: Revit gives the shared volume to exactly one of them, the " +
            "pair reads as a single mitred run, and it is counted once in a schedule. " +
            $"{_joinRefused} join(s) were REFUSED by Revit - those corners are filled but still " +
            "share material, and that number is the only place the two rules are not both met. " +
            "Square corners are not in this count: there the trim gives an exact butt with no " +
            "gap and no shared material, and needs no join.");

        _report.Add(
            $"BOARD SIDE: {_roomOnRight} run(s) had the room to the RIGHT of the boundary's own " +
            "direction. This should be zero. A line-based family lays its profile to the LEFT " +
            "of the placement direction, so on those runs a profile seated on one side is " +
            "modelled INSIDE the wall - and containment then discards the boards as outside " +
            "the room, which looks like missing skirting rather than a direction fault. It did " +
            "not matter while the profile was symmetric about its line. It does now.");

        _report.Add(
            $"BOARD SIDE RESOLUTION: {_inwardFromHost} board(s) had their side taken from the host " +
            "wall because IsPointInRoom could not decide - which happens at corners and doorways, " +
            $"exactly where boards meet. {_inwardUnknown} could not be resolved at all AND THAT " +
            "NUMBER MUST BE ZERO: a board with no known side cannot have a footprint built for " +
            "it, so it is invisible to the overlap check and free to occupy another board's " +
            "space. That is how two boards ended up sharing a corner while the report claimed " +
            "no overlaps.");

        _report.Add(
            $"CORNER OVERLAPS TRIMMED: {_overlapsTrimmed} candidate(s) were cut back because " +
            "another board already stood in that space at an angle to them. This used to be a " +
            "COUNT and nothing more - the overlap test only ever subtracted PARALLEL boards, so " +
            "every corner in the model was exempt from the no-overlap rule by construction, and " +
            "the report said zero while the model had clashes. It now clips the two footprints " +
            "against each other and subtracts what they genuinely share. " +
            "It asks nothing about rooms, loops or which face came first, because a board " +
            "occupying another board's space is wrong however it got there - the corner trim " +
            "cannot cover this, since it only ever compares faces adjacent within one boundary " +
            "loop and two boards meeting from different loops are never paired.");

        _report.Add(
            $"ADJACENT UNITS: {_blockersCoalesced} obstruction(s) were absorbed into a neighbour " +
            $"for sitting within {Measure.ToMillimetres(_settings.BlockerBridge):0} mm of it. A row of kitchen " +
            "units leaves a millimetre or two between carcasses; measured separately those joints " +
            "read as bare wall and collect 20 mm boards. Treating a close-packed row as one " +
            "obstruction is what stops the stubs.");

        _report.Add(
            $"CONTAINMENT: every board was measured against the room's own SOLID before " +
            $"placement. {_clippedToRoom} board(s) were shortened to the part actually inside the " +
            $"room; {Math.Max(0, _droppedOutsideRoom - _clipRefused)} were discarded for lying " +
            $"wholly outside it, and {_clipRefused} were REFUSED because containment could not " +
            "answer for them at all - a different fault with a different fix. The first two counts " +
            "should be low - a high number means boundary curves are running past their room, " +
            "which is a modelling condition worth looking at rather than a tool setting. " +
            "An end that meets a genuine corner is forgiven up to " +
            $"{Measure.ToMillimetres((CornerDepth) * _settings.MaxMitreFactor):0} mm " +
            "of apparent escape, because at a sharp corner the board has to occupy space the " +
            "room does not: the boundary is a line, a board has thickness, and where the walls " +
            "converge the room is narrower than the board long before the apex. Clipping there " +
            "is what was removing the material that fills a corner, and it is why the gaps were " +
            "worst on the slanted walls.");

        _report.Add(
            $"CONTAINMENT NOT VERIFIED: {_clipUndecidable} board(s) could not be checked - " +
            $"{_clipRefused} of them refused and not built, {_clipUndecidable - _clipRefused} " +
            $"PLACED ANYWAY in rooms with no solid. {_clipNotAttempted} were not checked at all " +
            "(containment off, or a curved piece). " +
            $"{_roomsWithoutSolid} room(s) had no computable solid and fell back to IsPointInRoom " +
            "sampling. THESE ARE THE NUMBERS THAT MATTER FOR BOARDS CROSSING A BOUNDARY, and " +
            "none of them existed before: every containment failure used to return the board " +
            "unchanged, so a board driven through a wall and a board that passed the test were " +
            "the same event and the report moved for neither. Where the room has a solid an " +
            "unanswerable board is now REFUSED; where it has none the board is still placed, " +
            "because dropping every board in an unenclosed room is worse - so a non-zero room " +
            "count here is the part of the model where a board can still cross a boundary." +
            (RoomContainment.VolumesComputed(_doc)
                ? string.Empty
                : " 'Areas and Volumes' computation is OFF for this document, which is why rooms " +
                  "have no solids: run the finish tools' boundary fix, or turn it on in Area and " +
                  "Volume Computations, and re-run."));

        _report.Add(
            $"BOUNDARY SEGMENTS NOT USED: {_separationLines} room separation line(s) - expected, " +
            $"nothing to fix a board to. {_linkedBoundaries} bounded by a wall in a LINKED model - " +
            "the segment's element id belongs to the link, not this document, so the wall cannot " +
            "be resolved and the face gets no board. " +
            $"{_nonWallBoundaries} refused as having no physical face" +
            (_droppedKinds.Count > 0 ? " (" + string.Join(", ", _droppedKinds) + ")" : "") + ". " +
            "Anything else that bounds a room now gets a board whether or not it is a wall; only " +
            "the categories on the refused list and bare curves are skipped." +
            (_fellBackToCentre > 0
                ? $" {_fellBackToCentre} room(s) had NO Finish boundary at all and were skirted " +
                  "from their Center boundary instead - listed individually under problems."
                : string.Empty));

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

        _report.Add(
            $"REVEAL SIDE: {_revealSideFromRay} jamb(s) had their room side settled by measuring " +
            $"how far a ray runs into the room each way; {_revealSideUnknown} could not be settled " +
            "at all and got no board. It was a pair of yes/no probes 30 mm either side, which " +
            "agree with each other - and so decide nothing - at a corner, in a doorway throat, " +
            "and anywhere the enclosure is marginal. That is where openings are, so the reveals " +
            "were being lost exactly where they exist. Two lengths can be compared; two coin " +
            "flips cannot.");

        if (_revealDepthMeasured > 0)
        {
            _report.Add(
                $"REVEAL DEPTH MEASURED: {_revealDepthMeasured} reveal(s) were cut to the far " +
                "room's own finish face rather than to the host wall's Width parameter, the " +
                $"largest disagreement being {Measure.ToMillimetres(_revealDepthWorst):0} mm. " +
                "Width off the near face is exact only when the near curve is at Finish location; " +
                "a room that fell back to its Center boundary starts the reveal on the wall " +
                "CENTRELINE, where Width overshoots the far face by half a wall and puts a board " +
                "through it. A large number here means Center fallbacks, not a measuring fault - " +
                "check the rooms listed under problems.");
        }
    }

    // ---------------------------------------------------------------- one room

    /// <summary>One run, measured and cut up, but not yet placed.</summary>
    private sealed record RunPlan(
        BoundaryRun Run, Curve Lifted, Span Whole, List<Span> Surviving, int Blockers,
        List<string> BlockerNames);

    /// <summary>Names of what blocked the run currently being planned. See Record.</summary>
    private readonly List<string> _runBlockers = [];

    /// <summary>A short, readable list of what covered a face. Used for empty faces.</summary>
    private static string DescribeBlockers(RunPlan plan) =>
        plan.BlockerNames.Count == 0
            ? $"{plan.Blockers} blocker(s)"
            : string.Join(", ", plan.BlockerNames.Distinct().Take(4));

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

    /// <summary>
    /// ~1 mm of slack below the floor when testing whether geometry reaches skirting height.
    ///
    /// Everything the jamb and casework measurements rely on sits exactly ON the floor plane,
    /// so a band starting exactly at it is decided by floating-point noise. Small enough that
    /// nothing genuinely above the board sneaks in, large enough that nothing resting on the
    /// floor falls out.
    /// </summary>
    private const double ZTolerance = 0.003;

    /// <summary>
    /// Do these two runs actually meet? Compared in plan only - a level change between two
    /// faces is not a corner, and neither is a coincidence of list order.
    /// </summary>
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

    /// <summary>
    /// Does a board actually reach this run's far corner? False when a blocker stopped it
    /// short, or when nothing survived at all - and then no corner is owed, because a corner
    /// is an agreement between two boards and one of them does not exist.
    /// </summary>
    private static bool ReachesEnd(RunPlan plan) =>
        plan.Surviving.Count > 0 &&
        Math.Abs(plan.Surviving[^1].End - plan.Whole.End) < EndTolerance;

    /// <summary>
    /// How close to a face's end a board must finish to count as reaching the corner, ~2 mm.
    ///
    /// THIS WAS 1e-9 FEET - a third of a nanometre - and that is the reason corners fail
    /// beside doorways. "Does the board reach the corner" decides everything about that
    /// corner: whether the arriving board is carried to the apex, whether an external corner
    /// is wrapped, and whether the leaving board is trimmed. At exact equality a blocker
    /// ending a hundredth of a millimetre before the face end answers NO, and the corner is
    /// abandoned - no apex, no wrap, no trim, and a notch left in the model.
    ///
    /// Blockers now come from measured solid intersections rather than round parameters, so
    /// a span landing microns short of a face end is the normal case and not the exception.
    /// Two millimetres is far below anything that is a real gap in a board and far above the
    /// noise the measurement produces.
    /// </summary>
    private const double EndTolerance = 0.0066;

    /// <summary>
    /// Where two boundary lines actually cross - the corner's true apex - or null when they
    /// are parallel or the crossing is nowhere near the join.
    ///
    /// WHY THE APEX IS NOT SIMPLY THE CURVE'S OWN ENDPOINT. In principle two boundary curves
    /// meeting at a corner share a point, so the apex IS the endpoint and this is a no-op. In
    /// practice finish-face geometry is rebuilt per segment and the two land a fraction of a
    /// millimetre apart - much further on a slanted wall, where every coordinate carries a
    /// sin/cos component. <see cref="Touches"/> already tolerates that when deciding whether
    /// a corner exists; this is what closes it, by running the arriving board to the point
    /// the two faces genuinely cross rather than to wherever its own curve happened to stop.
    ///
    /// This is the only extension in the engine and it is bounded by construction: the result
    /// is rejected unless it lies within <see cref="JoinTolerance"/> of the end it replaces,
    /// so it can move a board end by millimetres and never by metres.
    /// </summary>
    private static XYZ? Apex(Curve arriving, Curve leaving)
    {
        try
        {
            if (arriving is not Line a || leaving is not Line b) return null;

            var p = a.GetEndPoint(0);
            var r = a.GetEndPoint(1) - p;
            var q = b.GetEndPoint(0);
            var s = b.GetEndPoint(1) - q;

            // Plan only: a level change between two faces is not a corner in plan.
            var rz = new XYZ(r.X, r.Y, 0);
            var sz = new XYZ(s.X, s.Y, 0);

            var cross = (rz.X * sz.Y) - (rz.Y * sz.X);

            // Parallel, or a degenerate segment. A straight continuation has no apex.
            if (Math.Abs(cross) < 1e-9) return null;

            var t = (((q.X - p.X) * sz.Y) - ((q.Y - p.Y) * sz.X)) / cross;

            var at = new XYZ(p.X + (rz.X * t), p.Y + (rz.Y * t), a.GetEndPoint(1).Z);

            // Bounded: only ever a nudge onto the true crossing, never a reach across a room.
            return at.DistanceTo(a.GetEndPoint(1)) > JoinTolerance ? null : at;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Runs a piece's far end on by a fixed distance along its own direction.</summary>
    private static Curve RunOn(Curve piece, double extra)
    {
        try
        {
            if (piece is not Line line || extra <= 0) return piece;

            var start = line.GetEndPoint(0);
            var end = line.GetEndPoint(1);
            var direction = (end - start).Normalize();

            return Line.CreateBound(start, end + (direction * extra));
        }
        catch
        {
            return piece;
        }
    }

    private static Curve ExtendToApex(Curve piece, XYZ apex)
    {
        try
        {
            if (piece is not Line line) return piece;

            var start = line.GetEndPoint(0);
            var end = line.GetEndPoint(1);

            // Only outwards. An apex behind the current end would SHORTEN the board, and
            // shortening at a corner is the fault this whole change exists to remove.
            var direction = (end - start).Normalize();
            if ((apex - end).DotProduct(direction) <= 0) return piece;

            var extended = Line.CreateBound(start, new XYZ(apex.X, apex.Y, end.Z));
            return extended.Length < 1e-9 ? piece : extended;
        }
        catch
        {
            return piece;
        }
    }

    /// <summary>Everything that can be worked out about a run without writing to the model.</summary>
    private RunPlan? Plan(Room room, BoundaryRun run, double baseZ)
    {
        _runBlockers.Clear();

        var lifted = LiftTo(run.Axis, baseZ + _settings.Offset);
        if (lifted is null) return null;

        // SEAT THE BOARD AGAINST THE WALL BEFORE ANYTHING ELSE IS DECIDED.
        //
        // The profile is centred on its insertion line, so a board placed on the boundary
        // curve straddles the wall face: half of it buried in the wall, half showing. That
        // was measured in the model, not assumed - a board's centre landed exactly on its
        // wall's face, to the micron.
        //
        // Everything downstream - corner apex, trims, containment, the overlap check - works
        // on this curve, so the correction belongs HERE and nowhere else. Shift it into the
        // room by the profile's own half width and the board's back face lands on the wall,
        // the corner apex becomes the crossing of the two boards' actual back faces, and the
        // trims are computed for where the boards really are.
        //
        // If the family is ever re-authored to sit proud of its line, the measured overhang
        // becomes zero and this shifts nothing. Correct either way, which is what stops it
        // becoming a second thing to keep in step with the family.
        lifted = SeatAgainstWall(room, lifted);
        if (lifted is null) return null;

        if (_settings.SkipExteriorWallFaces && run.Wall is not null && IsOutsideFace(run.Wall, lifted))
        {
            _exteriorFaces++;
            return null;
        }

        // TWO KINDS OF BLOCKER, AND THEY MUST NOT SHARE A BRIDGE TOLERANCE.
        //
        // BlockerBridge is 150 mm and it exists for ONE thing: a row of kitchen carcasses,
        // which stand a millimetre or two apart and would otherwise collect a 20 mm board in
        // every joint. It is a statement about joinery, not about geometry.
        //
        // Applying it to precisely-measured spans is what over-blocks. A door's solids come
        // back as several separate pieces - lining, stop, architrave, leaf - and once those
        // are in the same list as the casework, the 150 mm bridge welds the door to whatever
        // stands within 150 mm of it and deletes the real board between them. The last run
        // says so plainly: obstructions absorbed jumped from 13 to 59 the moment door
        // geometry joined the list, and that is board being quietly thrown away.
        //
        // So precise spans bridge only against each other, and only by the minimum run -
        // enough to close the hairline between two parts of one frame, nowhere near enough to
        // swallow a board that belongs between a door and a cupboard.
        var precise = new List<Span>();
        var bulky = new List<Span>();

        // Inserts are a wall question. A column has none, and asking it for them throws.
        var jambPassRan = _settings.BreakAtOpenings && run.Wall is not null;

        if (jambPassRan) precise.AddRange(OpeningSpans(run.Wall!, lifted, baseZ));

        // Frame in the way is NOT a wall question - see OpeningGeometrySpans. Runs for every
        // host, including columns, because a door beside a pier fouls its board just as much.
        //
        // Anything the jamb pass ALREADY measured is excluded, or the same door is subtracted
        // twice and the second cut is the wider of the two. The exclusion is conditional on
        // that pass having actually run: with BreakAtOpenings off it does not, and excluding
        // its inserts anyway would leave doors blocking nothing at all.
        precise.AddRange(OpeningGeometrySpans(
            lifted, baseZ, jambPassRan ? InsertsOf(run.Wall) : new HashSet<long>()));
        if (_settings.BreakAtCasework) bulky.AddRange(CaseworkSpans(lifted, baseZ));

        var whole = new Span(lifted.GetEndParameter(0), lifted.GetEndParameter(1));

        var merged = SkirtingRun.Coalesce(precise, _settings.MinimumRun);
        merged.AddRange(SkirtingRun.Coalesce(bulky, _settings.BlockerBridge));

        // One last pass with NO bridge, purely to resolve overlaps between the two sets.
        var coalesced = SkirtingRun.Coalesce(merged, 0.0);

        var raw = precise.Count + bulky.Count;
        _blockersCoalesced += raw - coalesced.Count;

        return new RunPlan(
            run, lifted, whole, SkirtingRun.Subtract(whole, coalesced), raw, [.. _runBlockers]);
    }

    private void PlaceInRoom(Room room, HashSet<string> done)
    {
        var level = _doc.GetElement(room.LevelId) as Level;
        var baseZ = FloorElevation(room, level);

        // The boundary every board in this room is measured against, built ONCE per room -
        // a spatial element calculation is far too expensive to run per board. See
        // RoomContainment for why the clip is answered against a solid rather than by
        // probing points, and why an unanswerable clip now refuses the board.
        _containment = new RoomContainment(_doc, room);

        if (!_containment.IsUsable)
        {
            _roomsWithoutSolid++;

            if (_containment.Unavailable is not null)
                _clipReasons.Add($"{Label(room)}: {_containment.Unavailable}");

            _containment = null;   // sampling fallback for this room only
        }

        // Finish is where a board actually goes - the curve already lies on the room-side
        // face of each wall, so there is no offsetting from a centreline and no reasoning
        // about wall thickness or which way the wall was drawn.
        //
        // It can also come back EMPTY, and that is the whole-room version of the missing
        // segment. Finish boundaries are derived geometry: a room bounded by a wall with no
        // resolvable finish face - some composite and stacked types, and rooms whose
        // enclosure Revit considers marginal - yields nothing at all, and the room silently
        // produced no skirting. Falling back to Center gets a board on every face; it sits on
        // the wall centreline rather than the finish face, which ConfineToRoom then clips
        // back into the room. A board a finish thickness out of position beats no board.
        var loops = BoundaryLoops(room, SpatialElementBoundaryLocation.Finish);

        if (loops.Count == 0)
        {
            loops = BoundaryLoops(room, SpatialElementBoundaryLocation.Center);

            if (loops.Count > 0)
            {
                _fellBackToCentre++;
                _problems.Add(
                    $"{Label(room)}: no Finish boundary was available, so the room was skirted from " +
                    "its Center boundary instead. Those boards sit on the wall centreline rather " +
                    "than the finish face - check them, and check the wall types bounding this room.");
            }
        }

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

            // Corners are joined between consecutive faces of ONE loop, so the carry-over
            // must not leak from the end of one loop into the start of the next.
            _previousEndPiece = null;

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

                // THE MITRE JOIN, AND WHY BOTH HALVES ARE NEEDED.
                //
                // Two boards meeting at a corner on the raw boundary curves OVERLAP - they do
                // not leave a notch. Put the corner at the origin with the room in the
                // positive quadrant: the board on the bottom face occupies x∈[0,W], y∈[0,d]
                // and the board on the left face occupies y∈[0,H], x∈[0,d]. Both cover the
                // square x∈[0,d], y∈[0,d].
                //
                // So the ARRIVING board runs through the corner to the apex and fills it, and
                // the LEAVING board starts where its own strip clears the arriving one -
                // depth / sin(interior), the width of the rhombus where two strips of the
                // board's depth cross. That is a joiner's mitre expressed as a butt: one
                // continuous board turning the corner, the other landing on its face. No gap,
                // no shared material, and it holds at any angle rather than only at 90.
                //
                // The half that was missing is the arriving board actually REACHING the apex.
                // Two things stopped it, and between them they are the corner gaps in the
                // screenshots: the boundary curves land a fraction apart so the board stopped
                // short of the true crossing, and containment then clipped it further back.
                // Both are handled below - ExtendToApex closes the first, the corner
                // allowance passed to ClipToRoom closes the second.
                var previousArrives = previousJoins && ReachesEnd(previous!);

                // SQUARE CORNERS ARE TRIMMED. EVERY OTHER CORNER IS FILLED AND JOINED.
                //
                // The trim produces an exact butt at 90 degrees and only there. At any other
                // angle the interface between two square-ended boards is a slanted line that
                // a square cut cannot follow, so trimming buys "no overlap" by paying with a
                // gap - and the brief now asks for neither.
                //
                // So away from square, nothing is trimmed: both boards run into the corner and
                // it is completely filled. The material they then share is resolved by
                // JoinGeometry after placement, which is Revit's own answer to exactly this -
                // one element owns the intersection, the pair reads as a single mitred run,
                // and the volume is counted once.
                // THE CORNER STRATEGY, AND IT IS DECIDED BY WHAT THE FAMILY CAN DO.
                //
                // MITRE: the family exposes end-angle parameters, so both boards run to the
                // apex and their ends are cut on the bisector. No gap, because the corner is
                // completely filled; no overlap, because complementary angled cuts occupy
                // disjoint volumes. This is the only arrangement that satisfies both rules at
                // an angle other than 90 degrees, and at 90 it removes the one-board-deep
                // step a butt joint always shows.
                //
                // BUTT: it does not, so the leaving board is trimmed clear instead. That is
                // exact at 90 degrees and leaves a visible step; away from 90 it must show a
                // gap. It is the honest fallback and it never overlaps.
                var startAngleForThis = (double?)null;

                if (previousArrives)
                {
                    var interior = Interior(previous!.Lifted, plan.Lifted, orientation);

                    if (_canMitre && interior is { } value) startAngleForThis = MitreAngle(value);
                }

                // ONE BOARD ALWAYS GIVES WAY, AT EVERY ANGLE.
                //
                // This used to trim only corners within a degree of square, on the plan that
                // everything else would be filled to the apex and have its shared material
                // resolved by JoinGeometry. Revit refuses that join for this family - six
                // attempts, six refusals - so the fill had nothing to resolve it and both
                // boards simply ran into the corner and through each other. Square corners
                // still looked right, every other angle did not, which is exactly the
                // difference between the two screenshots.
                //
                // So unless the ends are genuinely being CUT (mitred), the leaving board is
                // trimmed clear of the arriving one whatever the angle. MitreLength already
                // returns zero for straight joins and for external corners - those are
                // wrapped instead - so this only ever bites where two boards would otherwise
                // occupy the same corner.
                var mitreIn = startAngleForThis is null && previousArrives
                    ? MitreLength(previous!.Lifted, plan.Lifted, orientation)
                    : 0.0;

                if (previousArrives && startAngleForThis is not null) _cornersFilled++;

                // The cut owed to this face's FAR end, for the piece that reaches it.
                var endAngleForThis = (double?)null;

                if (_canMitre && nextJoins && ReachesEnd(plan) &&
                    Interior(plan.Lifted, next!.Lifted, orientation) is { } outgoing)
                {
                    endAngleForThis = MitreAngle(outgoing);
                }

                // The apex this run's far end should carry through to, when the next face
                // genuinely turns off it. Null for a straight continuation or a detached
                // neighbour - neither is a corner and neither is owed anything.
                var apex = nextJoins && ReachesEnd(plan) ? Apex(plan.Lifted, next!.Lifted) : null;

                // How far past that apex the board must carry on to wrap an EXTERNAL corner.
                // Zero at an internal corner, where the trim above is the answer instead.
                var externalRun = nextJoins && ReachesEnd(plan)
                    ? ExternalRun(plan.Lifted, next!.Lifted, orientation)
                    : 0.0;

                if (externalRun > 0) _externalCornersWrapped++;

                if (nextJoins && ReachesEnd(plan)) _cornersClosed++;

                // HOW MUCH APPARENT ESCAPE A CORNER IS FORGIVEN.
                //
                // At a sharp corner the board legitimately occupies space the ROOM does not.
                // The room boundary is a line; a board has thickness; where the walls
                // converge the wedge is narrower than the board long before the apex. Probing
                // 10 mm into the room therefore reads OUTSIDE for the last 10mm/tan(angle) of
                // every acute corner - 17 mm at 30 degrees, 57 mm at 10 - and the containment
                // clip duly removed exactly the material that fills the corner.
                //
                // That is the second half of the corner gap, and it is why the gaps were
                // worst on the slanted walls. The allowance is the same quantity the mitre is
                // clamped to, so containment can never eat more than a corner's worth, and
                // only ever at an end where a corner genuinely is.
                var cornerAllowance = (CornerDepth) * _settings.MaxMitreFactor;

                var placedHere = 0;
                var faceLength = Measure.ToMillimetres(plan.Lifted.Length);

                // The two pieces of this face that take part in a corner: the one starting at
                // the face's start, and the one reaching its end. Remembered so the corner
                // can be joined once both sides of it exist.
                ElementId? startPiece = null;
                ElementId? endPiece = null;

                // Full trace per face, written to the LOG rather than the dialog. Every
                // number that decided this face's outcome, so a wrong board can be explained
                // from the file instead of inferred from a plan view.
                if (_trace.Count < 4000)
                {
                    _trace.Add(
                        $"{Label(room)} | {(plan.Run.Wall is null ? "host" : "wall")} " +
                        $"{plan.Run.Host.Id.Value} | face {faceLength:0} mm | " +
                        $"{plan.Blockers} blocker(s) | {plan.Surviving.Count} surviving run(s) | " +
                        $"trim-in {Measure.ToMillimetres(mitreIn):0.0} mm " +
                        $"(prev joins={previousJoins}, prev arrives={previousArrives}, " +
                        $"apex={(apex is null ? "no" : "yes")})");
                }

                foreach (var run in plan.Surviving)
                {
                    var atSegmentStart = Math.Abs(run.Start - plan.Whole.Start) < 1e-9;
                    var atSegmentEnd = Math.Abs(run.End - plan.Whole.End) < 1e-9;

                    var piece = BuildPiece(plan.Lifted, run, atSegmentStart, mitreIn);
                    if (piece is null) continue;

                    // EVERY STAGE THAT CAN CHANGE A LENGTH, RECORDED.
                    //
                    // A board came back 20 mm longer than the curve the trace said it was
                    // placed on, but ONLY on faces that were trimmed by 20 mm - untrimmed
                    // boards are exact to the millimetre. Two things predict that equally
                    // well: Revit not honouring the curve, or this pipeline giving the 20 mm
                    // back at the far end. They need different fixes and cannot be told apart
                    // from the finished geometry, so each stage reports what it did.
                    var afterBuild = piece.Length;

                    // Carry the far end through to the true corner crossing. Only the piece
                    // that actually reaches the end of the face is owed this - a piece whose
                    // end was made by subtracting a doorway must stay tight to the jamb.
                    if (atSegmentEnd && apex is not null) piece = ExtendToApex(piece, apex);

                    var afterApex = piece.Length;

                    // Carry it round the outside of a partition end. Only the board that
                    // reaches the corner is extended; the one leaving starts at the apex
                    // untrimmed, so the two meet along a face and share no material.
                    if (atSegmentEnd && externalRun > 0) piece = RunOn(piece, externalRun);

                    var afterRunOn = piece.Length;

                    // Last gate before placement: whatever the curve says, keep only the part
                    // the room agrees is inside it - forgiving a corner's worth at an end
                    // where a corner genuinely is.
                    piece = ClipToRoom(
                        room,
                        piece,
                        forgiveStart: atSegmentStart && previousJoins ? cornerAllowance : 0.0,
                        forgiveEnd: atSegmentEnd && nextJoins ? cornerAllowance : 0.0);

                    if (piece is null)
                    {
                        _droppedOutsideRoom++;
                        continue;
                    }

                    // Which way this board's material goes. Carried into the occupancy record
                    // so a later candidate can be compared against where this board actually
                    // IS, rather than against a guessed side.
                    var boardInward = BoardInward(room, piece, plan.Run);

                    // Office standard: no wall sweep may overlap another. The candidate is cut
                    // back to whatever no board already standing covers - see UncoveredParts.
                    var parts = UncoveredParts(piece, boardInward);

                    if (parts.Count == 0)
                    {
                        _refusedOverlaps++;
                        continue;
                    }

                    foreach (var part in parts)
                    {
                        var instance = _placer!.Place(part, plan.Run.Host, level, out var failure);

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

                        Stamp(instance, key, plan.Run.Host);

                        // Put it exactly on its curve, then total what the MODEL contains
                        // rather than what was asked for - see ConformToCurve.
                        var built = ConformToCurve(instance, part);

                        _placed.Add(instance.Id);
                        Occupy(part, boardInward);
                        _totalLength += built;
                        placedHere++;

                        if (atSegmentStart) startPiece ??= instance.Id;
                        if (atSegmentEnd) endPiece = instance.Id;

                        // Cut the ends this piece is owed. Only the piece that genuinely
                        // reaches a corner gets that corner's angle - an end made by
                        // subtracting a doorway is square and must stay square.
                        ApplyMitre(
                            instance,
                            atSegmentStart ? startAngleForThis : null,
                            atSegmentEnd ? endAngleForThis : null);

                        VerifyPlacedLength(instance, part.Length);

                        // The whole chain on one line, so the 20 mm can be attributed to the
                        // stage that actually moved it rather than guessed at afterwards.
                        if (_trace.Count < 4000 && Math.Abs(afterBuild - part.Length) > 0.0016)
                        {
                            _trace.Add(
                                $"        chain: face {Measure.ToMillimetres(plan.Lifted.Length):0.0}" +
                                $" -> build {Measure.ToMillimetres(afterBuild):0.0}" +
                                $" -> apex {Measure.ToMillimetres(afterApex):0.0}" +
                                $" -> runOn {Measure.ToMillimetres(afterRunOn):0.0}" +
                                $" -> clip/uncovered {Measure.ToMillimetres(part.Length):0.0} mm" +
                                $" (trim-in {Measure.ToMillimetres(mitreIn):0.0}," +
                                $" externalRun {Measure.ToMillimetres(externalRun):0.0})");
                        }

                        if (_trace.Count < 4000)
                            _trace.Add($"      placed {Measure.ToMillimetres(part.Length):0} mm  (id {instance.Id.Value})");

                    }
                }

                // A wall face that produced nothing is the symptom people actually see, and
                // guessing its cause from a plan view is what has cost the most time here.
                // Every one is now named, with the reason it came to nothing.
                if (placedHere == 0 && _emptyRuns.Count < 80)
                {
                    var length = Measure.ToMillimetres(plan.Lifted.Length);

                    // NAME THE BLOCKERS, DO NOT JUST COUNT THEM. "2 blocker(s) covered the
                    // whole 3225 mm face" reads as a fault and is usually not one - the
                    // commonest cause by far is floor-height glazing, which correctly gets no
                    // skirting. Counting sends someone to the model to find out which; naming
                    // answers it in the report.
                    var why = plan.Blockers == 0
                        ? $"nothing blocked it, so the {length:0} mm face was under the " +
                          $"{Measure.ToMillimetres(_settings.MinimumRun):0} mm minimum, or placement failed"
                        : $"covered by {DescribeBlockers(plan)} - if those reach the floor " +
                          "(full-height glazing, a doorway) this face correctly has no board";

                    _emptyRuns.Add($"{Label(room)} / host {plan.Run.Host.Id.Value}: {length:0} mm face {why}");
                }

                // Only a MITRED corner has shared material for a join to resolve. A trimmed
                // corner has none by construction, and asking Revit to join two elements that
                // merely touch is a refusal for nothing.
                if (previousArrives && startAngleForThis is not null)
                    JoinAtCorner(_previousEndPiece, startPiece);

                _previousEndPiece = endPiece;

                if (_settings.WrapIntoReveals && plan.Run.Wall is not null)
                    PlaceReveals(room, plan.Run.Wall, plan.Lifted, level, baseZ, done);
            }
        }
    }

    /// <summary>
    /// A room's boundary loops at one location, or an empty list when it has none there.
    /// </summary>
    private static IList<IList<BoundarySegment>> BoundaryLoops(
        Room room, SpatialElementBoundaryLocation location)
    {
        try
        {
            var options = new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = location,
            };

            return room.GetBoundarySegments(options) ?? [];
        }
        catch
        {
            return [];
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
            // IS THIS OPENING EVEN ON THIS STRETCH OF WALL?
            //
            // Inserts come from the WALL and are measured against a RUN - one room's piece of
            // it. Project clamps to the run's ends, so without this test a door further along
            // the wall reports a perfectly plausible parameter at this run's corner. Both its
            // jambs then collapse onto that one point, the key below is claimed by a room
            // that cannot place it, and the room that actually contains the door never gets a
            // board. That is missing reveals at real openings and stray boards at corners,
            // from a single unchecked projection.
            // Every opening that gets this far is one the jamb pass is responsible for. Noted
            // BEFORE the on-run test, because an opening rejected by every run it is offered
            // to disappears without trace otherwise - see the reconciliation in the report.
            try { _revealOpeningsSeen.Add(insert.Id.Value); } catch { /* id unreadable */ }

            if (!SkirtingRun.OnRun(axis, insert, _doc, thickness, _settings.JambMargin))
            {
                // NOT AUDITED HERE, DELIBERATELY LEFT AS IS. A first version of this fix
                // called RecordJamb on this path too, and that was wrong: this branch runs
                // once per (room, wall) pass for every opening the WALL hosts, so a wall
                // shared by several rooms would log "not on this run" from every room that
                // correctly does NOT own a given opening - turning "18 listed, twice the
                // openings that reach the floor" (see the report's own EVERY JAMB,
                // ACCOUNTED FOR line) into noise that no longer matches that count, for no
                // diagnostic gain: the reconciliation below already flags an opening that
                // NEVER gets audited by anything as "THIS IS A DEFECT, not a rule", loudly,
                // in the always-shown summary - it does not need a line per rejection to do
                // that. The actual cause found 2026-09-04 (a wide leafless Opening whose
                // WidthOf read 0, starving OnRun's own reach budget) is fixed at the source
                // in ReachWidthOf; that reconciliation warning is what would catch a
                // different cause of this same symptom in the future.
                _revealsElsewhere++;
                continue;
            }

            // Unpadded: the pad exists to hold boards clear of a frame, but the jamb line
            // itself is where the reveal face actually starts.
            var span = SkirtingRun.FromInsert(axis, insert, _doc, pad: 0.0);

            if (span is not { } jambs)
            {
                // UNMEASURABLE IS NOT THE SAME AS ABSENT, AND IT MUST NOT LOOK THE SAME.
                //
                // This returned silently, so an opening whose span could not be measured
                // vanished from the audit entirely - and the audit reads absence as "never
                // reached", which is the one thing it promises to distinguish. A wall Opening
                // is exactly the case that lands here: it has no LocationPoint and no width
                // parameter, so the measurement falls back to its bounding box and can fail
                // outright.
                RecordJamb(insert, 0, 0.0, "no board - the opening's extent could not be measured on this run");
                RecordJamb(insert, 1, 0.0, "no board - the opening's extent could not be measured on this run");

                continue;
            }

            // Two jambs that measure to the same point are not two jambs. A real opening is
            // at least its own width across; anything less is a projection artefact.
            if (jambs.Extent < _settings.MinimumRun)
            {
                _revealsElsewhere++;

                // Audited, or these two jambs vanish from the list entirely and read as
                // never reached - which the audit calls a fault. A degenerate measurement is
                // a deliberate skip and has to say so.
                RecordJamb(insert, 0, 0.0, "no board - the opening measured degenerate on this run");
                RecordJamb(insert, 1, 0.0, "no board - the opening measured degenerate on this run");

                continue;
            }

            for (var jamb = 0; jamb < 2; jamb++)
            {
                var key = $"{SkirtingSettings.Stamp}:reveal:{insert.Id.Value}:{jamb}";

                // CLAIMED ONLY ON SUCCESS. This was Add() up front, which spends the claim
                // whether or not a board follows: one ambiguous probe in the first room to
                // reach the opening suppressed the reveal permanently, because the room on
                // the other side - which could have placed it - found the key already taken.
                // A reveal is one shared face, so the first room to actually BUILD it wins.
                if (done.Contains(key)) continue;

                var run = RevealRun(axis, room, jamb == 0 ? jambs.Start : jambs.End, thickness);

                if (run is null || run.Length < _settings.MinimumRun)
                {
                    // RevealRun refuses for two quite different reasons and only one is a
                    // decision: the far side is outdoors or unmodelled (correct - a reveal
                    // board spans the whole wall and would come out the other face), or the
                    // jamb sits somewhere the room cannot answer for. Both are recorded, or a
                    // jamb that should have had a board is indistinguishable from one that
                    // correctly did not.
                    RecordJamb(insert, jamb, 0.0,
                        run is null
                            ? "no board - the far side of the opening is outdoors or unmodelled, " +
                              "so a reveal board would emerge on the outside face"
                            : $"no board - the reveal measured only " +
                              $"{Measure.ToMillimetres(run.Length):0} mm, under the minimum");

                    continue;
                }

                // THE FAMILY'S OWN LINING COMES OUT FIRST. Whatever the door already fills is
                // not reveal to be skirted, and a board placed there would double up with it.
                var bare = UnlinedParts(run, insert, baseZ);

                if (bare.Count == 0)
                {
                    // Fully lined. Claim the key: this is one face shared by two rooms and the
                    // room on the other side would measure the same lining and reach the same
                    // answer, so there is nothing to be gained by letting it try.
                    _revealsLined++;
                    done.Add(key);

                    RecordJamb(insert, jamb, run.Length,
                        "no board - the family lines this reveal in full, so a board here would " +
                        "double up with it");

                    continue;
                }

                if (bare.Count > 1 || bare[0].Length < run.Length - 1e-9) _revealsPartlyLined++;

                var placedAny = false;

                foreach (var part in bare)
                {
                    // UNHOSTED, unless explicitly configured otherwise. A jamb board crosses
                    // the wall's side faces at right angles and belongs to the opening, not to
                    // either face of the wall - see HostRevealsOnWall. This is the line that
                    // keeps the wall-sweep placement and the jamb placement separate.
                    var host = _settings.HostRevealsOnWall ? wall : null;

                    var instance = _placer!.Place(part, host, level, out var failure);

                    if (instance is null)
                    {
                        _problems.Add($"{Label(room)}: jamb board at opening {insert.Id.Value} " +
                                      $"could not be placed - {failure}");
                        continue;
                    }

                    if (!_placer.IsExact && !_placer.DriveLength(instance, part.Length)) _wrongLength++;

                    Stamp(instance, key, insert);

                    // Same conformance as a wall run: a jamb board that Revit stretched onto
                    // the wall face would put reveal quantity into the twin that is not there.
                    var builtJamb = ConformToCurve(instance, part);
                    VerifyPlacedLength(instance, part.Length);

                    _placed.Add(instance.Id);

                    // A jamb board runs INTO the wall, so it has no room-side normal in the
                    // sense a wall run does. Null, which means it takes part in the collinear
                    // duplicate check but is never reported as a corner overlap - it turns no
                    // corner.
                    Occupy(part, null);
                    _revealPieces++;
                    _totalLength += builtJamb;
                    _revealLength += builtJamb;
                    placedAny = true;

                    RecordJamb(insert, jamb, builtJamb,
                        bare.Count > 1 || part.Length < run.Length - 1e-9
                            ? "board on the depth the family leaves bare"
                            : "board on the full reveal");
                }

                if (placedAny) done.Add(key);
            }
        }
    }

    /// <summary>
    /// The parts of a candidate board that nothing already standing covers. The pre-creation
    /// collision check: it runs before the instance exists, so an overlapping board is never
    /// created and then cleaned up - it is never created.
    ///
    /// WHY CUT BACK RATHER THAN REFUSE. Refusing a piece outright because something overlaps
    /// it is right only when the overlap is total. A face reached twice is usually reached
    /// differently the second time - a longer run against a shorter one, a wall run against
    /// the stub a flush column contributed - and throwing the whole candidate away loses the
    /// stretch that genuinely had no board. Cutting it down to the uncovered remainder places
    /// exactly what is missing and nothing else, which is the only version that satisfies
    /// "no overlaps" and "no gaps" at the same time.
    /// </summary>
    private List<Curve> UncoveredParts(Curve candidate, XYZ? inward)
    {
        if (!_settings.PreventOverlaps || candidate is not Line line) return [candidate];

        var against = OccupantsNear(line.GetEndPoint(0).Z);
        if (against.Count == 0) return [candidate];

        var start = line.GetEndPoint(0);
        var end = line.GetEndPoint(1);
        var direction = (end - start).Normalize();
        var length = start.DistanceTo(end);

        var covered = new List<Span>();

        foreach (var occupant in against)
        {
            try
            {
                var existing = occupant.Line;
                var otherStart = existing.GetEndPoint(0);
                var otherEnd = existing.GetEndPoint(1);
                var otherDirection = (otherEnd - otherStart).Normalize();

                // NOT PARALLEL - so this is a corner pair, and coverage is the wrong question:
                // a board turning a corner does not "cover" the one leaving it. But the
                // no-overlap rule still applies, and until now nothing checked it. The
                // collinear test below is a coverage test, and it silently skipped every
                // corner in the model - so "no wall sweep may overlap another" was enforced
                // along walls and not at the one place the boards actually meet.
                //
                // The footprints are measured and any genuine intersection is REPORTED rather
                // than trimmed. Trimming here would fight the corner logic that just placed
                // them, and the corner rule is a decision about the office standard rather
                // than a bug to patch silently - see the mitre note in the report.
                // NOT PARALLEL - a corner pair. This used to COUNT the clash and let the
                // board through, which is why the report said zero overlaps while the model
                // had them: a detector was written where a trimmer was needed.
                //
                // The corner trim cannot cover this case either. It only ever compares faces
                // adjacent within ONE boundary loop, so two boards meeting at a corner from
                // different loops - or different rooms - are never paired and both run into
                // it. Measured: 29351297 and 29351285 sharing a 20 x 20 mm square.
                //
                // The rule must not depend on which room a board came from, so this does not
                // ask. It clips the two footprints against each other and subtracts whatever
                // they genuinely share, whatever the angle and whoever placed it.
                if (Math.Abs(direction.DotProduct(otherDirection)) < 0.999)
                {
                    var shared = SharedSpan(line, inward, occupant, direction, start);

                    if (shared is { } clash)
                    {
                        _cornerOverlaps++;
                        _overlapsTrimmed++;
                        covered.Add(clash);
                    }

                    continue;
                }

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
                if (Math.Abs(otherStart.Z - start.Z) > BoardBand) continue;

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
    /// Measures the profile ONCE, from a throwaway instance, before a single real board is
    /// placed.
    ///
    /// WHY A PROBE RATHER THAN THE FIRST REAL BOARD. The profile decides where every board
    /// sits and how far every corner is trimmed, so it has to be known BEFORE the first
    /// placement, not after it. Measuring the first real board means that board - and every
    /// corner it takes part in - was positioned from a guess, and the guess and the
    /// measurement then disagree by exactly the amount that shows up as a corner fault.
    ///
    /// One instance, created on a line away from any geometry, measured, deleted.
    /// </summary>
    private void CalibrateProfile()
    {
        var level = new FilteredElementCollector(_doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .FirstOrDefault();

        if (level is null || _placer is null) return;

        var origin = new XYZ(0, 0, level.Elevation);

        FamilyInstance? probe;
        try
        {
            probe = _placer.Place(Line.CreateBound(origin, origin + (XYZ.BasisX * 3.0)), null, level, out _);
        }
        catch
        {
            return;
        }

        if (probe is null) return;

        try
        {
            _doc.Regenerate();

            // CAN THIS FAMILY BE MITRED? Asked of a real instance, before a single board is
            // placed, because the whole corner strategy turns on the answer. If it can, the
            // boards run to the apex and are cut. If it cannot, they must be butted instead -
            // a board that runs to the apex expecting a cut it never receives overlaps its
            // neighbour, which is the one outcome worse than a visible step.
            _canMitre = _settings.MitreCorners &&
                        _placer.SupportsMitre(
                            probe,
                            _settings.MitreStartParameterNames,
                            _settings.MitreEndParameterNames);

            // Signed offsets from the placement line, along its own perpendicular.
            var perpendicular = XYZ.BasisX.CrossProduct(XYZ.BasisZ).Normalize();

            double low = double.MaxValue, high = double.MinValue;
            double bottom = double.MaxValue, top = double.MinValue;

            foreach (var solid in _geometry.ElementSolids(probe))
            {
                foreach (Edge edge in solid.Edges)
                {
                    IList<XYZ> points;
                    try { points = edge.Tessellate(); }
                    catch { continue; }

                    foreach (var point in points)
                    {
                        var offset = (point - origin).DotProduct(perpendicular);
                        low = Math.Min(low, offset);
                        high = Math.Max(high, offset);

                        var height = point.Z - origin.Z;
                        bottom = Math.Min(bottom, height);
                        top = Math.Max(top, height);
                    }
                }
            }

            if (low < high && high - low is > 0.003 and < 0.66)
            {
                _profileWidth = high - low;

                // How far the profile hangs on the far side of its own line. For a centred
                // profile that is half the width, and it is exactly what has to be taken out
                // of the wall and given back to the room. Zero for a profile already seated
                // against its line, which is what a correctly authored family gives.
                _profileOverhang = Math.Max(0.0, Math.Min(Math.Abs(low), Math.Abs(high)));

                var seated = _profileOverhang <= 0.003;

                _report.Add(
                    $"PROFILE MEASURED: {Measure.ToMillimetres(_profileWidth):0.0} mm wide, sitting " +
                    $"{Measure.ToMillimetres(_profileOverhang):0.0} mm behind its own insertion line " +
                    (seated ? "- seated against it, which is correct. " : "- straddling it. ") +
                    "Measured from a throwaway instance before any board was placed, because the " +
                    "seating and every corner trim depend on it. The configured guess was " +
                    $"{Measure.ToMillimetres(_settings.BoardDepth):0.0} mm and the type is named " +
                    $"'{_settings.TypeName}' - neither is trusted over the geometry." +
                    (seated
                        ? " Nothing is shifted."
                        : $" Boards are shifted {Measure.ToMillimetres(_profileOverhang):0.0} mm into " +
                          "the room so the back face lands on the wall rather than inside it."));
            }

            // THE HEIGHT IS A GUESS TOO, AND IT DECIDES WHAT BLOCKS A RUN.
            //
            // BoardHeight is the band that says whether something is in the board's way, and
            // it was set to 80 mm from the type NAME while the solid is 63 mm tall. Every
            // millimetre of the difference is over-blocking: anything sitting between the top
            // of the real board and the guess cuts a run it never touches. Measuring it here
            // costs nothing, because the probe is already built and read.
            if (bottom < top && top - bottom is > 0.003 and < 1.64)
            {
                _profileHeight = top - bottom;

                if (Math.Abs(_profileHeight - _settings.BoardHeight) > 0.003)
                {
                    _report.Add(
                        $"BOARD HEIGHT MEASURED: {Measure.ToMillimetres(_profileHeight):0.0} mm, against a " +
                        $"configured {Measure.ToMillimetres(_settings.BoardHeight):0.0} mm. The measured value " +
                        "is used. This is the band that decides what interrupts a run, so a guess " +
                        "that is too tall makes things block boards they stand clear of.");
                }
            }
        }
        catch
        {
            // Fall back to the configured guess.
        }
        finally
        {
            try { _doc.Delete(probe.Id); } catch { /* a stray probe is better than a lost run */ }
        }
    }

    /// <summary>
    /// Moves a boundary curve into the room by the profile's overhang, so a board placed on
    /// it sits against the wall rather than half buried in it.
    /// </summary>
    private Curve? SeatAgainstWall(Room room, Curve lifted)
    {
        if (lifted is not Line line) return lifted;

        try
        {
            var start = line.GetEndPoint(0);
            var end = line.GetEndPoint(1);
            var direction = (end - start).Normalize();

            var inward = InwardNormal(room, start + (direction * (line.Length / 2.0)), direction);

            // Undecidable which side the room is on. Leave the curve where the boundary put
            // it rather than shift a board the wrong way, into the wall.
            if (inward is null) return lifted;

            // WHICH SIDE OF THE RUN THE ROOM IS ON, AND WHY IT NOW MATTERS.
            //
            // A line-based family lays its own +Y to the LEFT of the placement direction. A
            // profile centred on its line does not care - it is symmetric, so the board looks
            // the same either way. A profile seated on ONE side does care: get the direction
            // wrong and the whole board is modelled inside the wall.
            //
            // Room boundary loops should always present the room on the left, outer loops and
            // holes alike. Should. This counts the exceptions rather than assuming there are
            // none, because a silent one puts every board on that run inside a wall, and the
            // containment clip would then quietly discard them as "outside the room" - which
            // reads as missing skirting, not as a direction fault.
            var left = XYZ.BasisZ.CrossProduct(direction);

            if (left.GetLength() > 1e-9 && inward.DotProduct(left.Normalize()) < 0) _roomOnRight++;

            if (!_settings.SeatProfileAgainstWall || _profileOverhang <= 0.003) return lifted;

            var shift = inward * _profileOverhang;

            return Line.CreateBound(start + shift, end + shift);
        }
        catch
        {
            return lifted;
        }
    }

    /// <summary>
    /// Compares the length Revit actually built with the length it was asked for.
    ///
    /// WHY THIS HAS TO BE MEASURED RATHER THAN ASSUMED. Every corner rule in this engine acts
    /// on the placement CURVE - trim the curve, extend the curve, clip the curve. All of it
    /// is worthless if the instance Revit produces is not the length of that curve, and in
    /// this model it is not: boards handed a 2230 mm curve came back 2250 mm, and boards
    /// handed 1655 mm came back 1675 mm. Twenty millimetres too long, every time, which is
    /// exactly the corner trim - so every trimmed board grows back to the full face, reaches
    /// the apex it was trimmed away from, and overlaps its neighbour by the trim.
    ///
    /// That is why the total has not moved across four builds of corner changes. The curves
    /// were right and the geometry never followed them. Nothing downstream of placement can
    /// detect it either, because the overlap checks test the curves - which butt correctly.
    ///
    /// So the discrepancy is measured per board and reported. A consistent non-zero figure
    /// means the family is not honouring its placement curve, and no amount of arithmetic
    /// here will fix a corner until that is resolved.
    /// </summary>
    /// <summary>
    /// Forces a placed instance onto the exact curve it was meant to occupy, and reports the
    /// built length so quantities are the model rather than the intent.
    ///
    /// WHY THE CURVE HAS TO BE SET AGAIN AFTER PLACEMENT. Boards on trimmed faces came back
    /// 20 mm longer than the curve handed to NewFamilyInstance, while boards on untrimmed
    /// faces were exact - measured across ten instances. A trimmed board's end sits a board's
    /// depth short of the wall corner, and Revit extends a line-based instance onto nearby
    /// geometry rather than leaving it short. The corner trim is therefore computed correctly
    /// and then undone by the placement itself, which is why four builds of corner changes
    /// never moved the result by a millimetre.
    ///
    /// Writing LocationCurve back is the API's own way of saying "this curve, exactly". It
    /// costs one property set per board and it closes the loop: whatever the placement did,
    /// the instance ends up on the curve the rules produced.
    ///
    /// The returned length is what the model actually contains, and that is what quantities
    /// are totalled from - a takeoff that reports intent rather than geometry is wrong in the
    /// direction that costs money, and worse in a digital twin, where the number is supposed
    /// to BE the building.
    /// </summary>
    /// <summary>
    /// Cuts a board's ends to their corner angles, counting anything the family refuses.
    ///
    /// A refusal after <see cref="SkirtingPlacer.SupportsMitre"/> reported the family capable
    /// is the dangerous case, and the reason it is counted rather than swallowed: the board
    /// was run to the apex on the promise of a cut, and without the cut it overlaps its
    /// neighbour. That number belongs in the report, not in a catch block.
    /// </summary>
    private void ApplyMitre(FamilyInstance instance, double? startAngle, double? endAngle)
    {
        if (!_canMitre || (startAngle is null && endAngle is null)) return;

        try
        {
            if (_placer!.SetEndAngles(
                    instance,
                    startAngle,
                    endAngle,
                    _settings.MitreStartParameterNames,
                    _settings.MitreEndParameterNames))
            {
                if (startAngle is not null) _endsMitred++;
                if (endAngle is not null) _endsMitred++;
                return;
            }

            _mitreRefused++;
        }
        catch
        {
            _mitreRefused++;
        }
    }

    private double ConformToCurve(FamilyInstance instance, Curve intended)
    {
        var requested = intended.Length;

        try
        {
            if (instance.Location is LocationCurve location)
            {
                var current = location.Curve;

                if (current is null || Math.Abs(current.Length - requested) > 0.0016)
                {
                    location.Curve = intended;
                    _conformed++;
                }
            }
        }
        catch
        {
            // Not curve-driven, or Revit refused the curve. VerifyPlacedLength will say so.
        }

        return BuiltLength(instance) ?? requested;
    }

    /// <summary>The length Revit actually built, or null when the family exposes none.</summary>
    private static double? BuiltLength(FamilyInstance instance)
    {
        try
        {
            var parameter = instance.get_Parameter(BuiltInParameter.INSTANCE_LENGTH_PARAM);

            return parameter is { HasValue: true, StorageType: StorageType.Double }
                ? parameter.AsDouble()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void VerifyPlacedLength(FamilyInstance instance, double requested)
    {
        try
        {
            var parameter = instance.get_Parameter(BuiltInParameter.INSTANCE_LENGTH_PARAM);

            if (parameter is not { HasValue: true, StorageType: StorageType.Double }) return;

            var actual = parameter.AsDouble();
            var difference = actual - requested;

            _lengthChecked++;

            // Half a millimetre. Below that is rounding, above it is the family not following
            // the curve it was placed on.
            if (Math.Abs(difference) <= 0.0016) return;

            _lengthMismatched++;
            _lengthDrift += difference;

            if (_lengthWorst is null || Math.Abs(difference) > Math.Abs(_lengthWorst.Value))
                _lengthWorst = difference;

            if (_trace.Count < 4000)
            {
                _trace.Add(
                    $"      LENGTH MISMATCH: asked {Measure.ToMillimetres(requested):0.0} mm, " +
                    $"Revit built {Measure.ToMillimetres(actual):0.0} mm " +
                    $"({Measure.ToMillimetres(difference):+0.0;-0.0} mm) (id {instance.Id.Value})");
            }
        }
        catch
        {
            // No readable length; nothing to compare.
        }
    }

    /// <summary>
    /// Records a board as occupying its line, so nothing later lands on top of it.
    /// </summary>
    /// <summary>
    /// Which way a board's material goes, with a fallback that cannot return null for a
    /// wall-hosted board.
    ///
    /// THIS IS WHY OVERLAPS SURVIVED THE OVERLAP CHECK. The check compares two board
    /// footprints, and a footprint cannot be built without knowing which side of its line the
    /// board occupies. IsPointInRoom answers that most of the time and returns null when both
    /// probes agree - at a corner, in a doorway, anywhere the room boundary is ambiguous. A
    /// null on EITHER board made the pair unjudgeable, so it was skipped, and the two were
    /// left occupying the same space.
    ///
    /// Corners are exactly where that probe is least reliable and exactly where boards meet,
    /// so the failure concentrated in the one place it mattered most. Measured on the live
    /// model: 29354701 and 29354717 sharing a 20 x 20 mm square, both ending at the same
    /// corner, neither trimmed.
    ///
    /// The fallback needs no room at all. A board lies on the room side of its host wall, so
    /// the direction from the wall's centreline out to the board's own line IS the inward
    /// normal - a fact about where the board was put, not about what the room thinks.
    /// </summary>
    private XYZ? BoardInward(Room room, Curve piece, BoundaryRun run)
    {
        try
        {
            var direction = (piece.GetEndPoint(1) - piece.GetEndPoint(0)).Normalize();
            var mid = piece.Evaluate(0.5, true);

            var asked = InwardNormal(room, mid, direction);
            if (asked is not null) return asked;

            _inwardFromHost++;

            if (run.Wall?.Location is LocationCurve location)
            {
                var onCentreline = location.Curve.Project(mid)?.XYZPoint;

                if (onCentreline is not null)
                {
                    var offset = new XYZ(mid.X - onCentreline.X, mid.Y - onCentreline.Y, 0);
                    if (offset.GetLength() > 1e-9) return offset.Normalize();
                }
            }

            _inwardUnknown++;
            return null;
        }
        catch
        {
            _inwardUnknown++;
            return null;
        }
    }

    private void Occupy(Curve placed, XYZ? inward)
    {
        if (placed is not Line line) return;

        var cell = (long)Math.Floor(line.GetEndPoint(0).Z / OccupancyCell);

        if (!_occupied.TryGetValue(cell, out var bucket))
        {
            bucket = [];
            _occupied[cell] = bucket;
        }

        bucket.Add(new PlacedBoard(line, inward));
    }

    /// <summary>
    /// Do two boards' plan footprints genuinely intersect? Each board is a rectangle: its
    /// line, widened to the measured profile depth on the room side.
    ///
    /// Separating-axis test on the two rectangles. Four candidate axes - each rectangle's two
    /// edge directions - and a gap on any one of them means no overlap. A shared edge is not
    /// an overlap, which is what the tolerance is for: a correctly butted corner has the two
    /// boards touching along exactly one face and must not be reported.
    /// </summary>
    /// <summary>
    /// The stretch of a candidate that another board genuinely occupies, in the candidate's
    /// own distance-from-start units - or null when they do not share space.
    ///
    /// Both boards are rectangles in plan: their line, widened to the profile depth on the
    /// side their material actually went. The two are clipped against each other and whatever
    /// survives is projected back onto the candidate's axis. That is the exact stretch which
    /// must not be built, and it is derived from geometry alone - no rooms, no loops, no
    /// pairing, so it holds for corners the corner logic never sees.
    /// </summary>
    private Span? SharedSpan(Line candidate, XYZ? inward, PlacedBoard occupant, XYZ direction, XYZ start)
    {
        try
        {
            if (inward is null || occupant.Inward is null) return null;

            var depth = CornerDepth;

            var a = Footprint(candidate, inward, depth);
            var b = Footprint(occupant.Line, occupant.Inward, depth);

            if (a is null || b is null) return null;

            var region = Clip(a, b);
            if (region.Count < 3) return null;

            double low = double.MaxValue, high = double.MinValue;

            foreach (var point in region)
            {
                var along = (point - new XYZ(start.X, start.Y, 0)).DotProduct(direction);
                low = Math.Min(low, along);
                high = Math.Max(high, along);
            }

            // A shared edge is a butt joint, not an overlap.
            return high - low <= 0.0016 ? null : new Span(low, high);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sutherland-Hodgman: the convex region common to two convex plan polygons. Both board
    /// footprints are rectangles, so the result is the exact shared area rather than an
    /// approximation of it.
    /// </summary>
    private static List<XYZ> Clip(IReadOnlyList<XYZ> subject, IReadOnlyList<XYZ> window)
    {
        var output = new List<XYZ>(subject);

        for (var i = 0; i < window.Count && output.Count > 0; i++)
        {
            var edgeFrom = window[i];
            var edgeTo = window[(i + 1) % window.Count];

            var edge = edgeTo - edgeFrom;
            var normal = new XYZ(-edge.Y, edge.X, 0);   // inward for an anticlockwise window

            var input = output;
            output = [];

            for (var j = 0; j < input.Count; j++)
            {
                var current = input[j];
                var previous = input[(j + input.Count - 1) % input.Count];

                var currentIn = (current - edgeFrom).DotProduct(normal) >= -1e-12;
                var previousIn = (previous - edgeFrom).DotProduct(normal) >= -1e-12;

                if (currentIn)
                {
                    if (!previousIn && Cross(previous, current, edgeFrom, edgeTo) is { } entering)
                        output.Add(entering);

                    output.Add(current);
                }
                else if (previousIn && Cross(previous, current, edgeFrom, edgeTo) is { } leaving)
                {
                    output.Add(leaving);
                }
            }
        }

        return output;
    }

    /// <summary>Where segment a-b crosses the infinite line through p-q, in plan.</summary>
    private static XYZ? Cross(XYZ a, XYZ b, XYZ p, XYZ q)
    {
        var r = b - a;
        var s = q - p;

        var denominator = (r.X * s.Y) - (r.Y * s.X);
        if (Math.Abs(denominator) < 1e-12) return null;

        var t = (((p.X - a.X) * s.Y) - ((p.Y - a.Y) * s.X)) / denominator;

        return new XYZ(a.X + (r.X * t), a.Y + (r.Y * t), 0);
    }

    private bool FootprintsOverlap(Line first, XYZ? firstInward, PlacedBoard second)
    {
        try
        {
            // Without both normals the rectangles cannot be built on the sides the boards
            // actually occupy, and a guess here is worse than no answer - it is what made
            // this count meaningless before. Undecidable pairs are not reported.
            if (firstInward is null || second.Inward is null) return false;

            var depth = CornerDepth;

            var a = Footprint(first, firstInward, depth);
            var b = Footprint(second.Line, second.Inward, depth);

            if (a is null || b is null) return false;

            return !SeparatedOn(a, b) && !SeparatedOn(b, a);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The four plan corners of a board: its line widened by the profile depth, on the side
    /// the board's material actually went - which is the room side, not a fixed handedness.
    /// </summary>
    private static XYZ[]? Footprint(Line line, XYZ inward, double depth)
    {
        var start = line.GetEndPoint(0);
        var end = line.GetEndPoint(1);

        var along = new XYZ(end.X - start.X, end.Y - start.Y, 0);
        if (along.GetLength() < 1e-9) return null;

        var flat = new XYZ(inward.X, inward.Y, 0);
        if (flat.GetLength() < 1e-9) return null;

        var side = flat.Normalize() * depth;

        return
        [
            new XYZ(start.X, start.Y, 0),
            new XYZ(end.X, end.Y, 0),
            new XYZ(end.X + side.X, end.Y + side.Y, 0),
            new XYZ(start.X + side.X, start.Y + side.Y, 0),
        ];
    }

    /// <summary>
    /// Is there a gap between the two rectangles along one of <paramref name="axes"/>'s own
    /// edge normals? A board sits on the room side of its line and which side that is depends
    /// on the wall, so both offsets are tested - the rectangle is used as measured.
    /// </summary>
    private static bool SeparatedOn(XYZ[] axes, XYZ[] other)
    {
        // A touching face is a butt joint, not an overlap. Half a millimetre of slack.
        const double touching = 0.0016;

        for (var i = 0; i < 4; i++)
        {
            var edge = axes[(i + 1) % 4] - axes[i];
            var length = edge.GetLength();
            if (length < 1e-9) continue;

            var normal = edge.Normalize().CrossProduct(XYZ.BasisZ);

            double lowA = double.MaxValue, highA = double.MinValue;
            double lowB = double.MaxValue, highB = double.MinValue;

            foreach (var p in axes)
            {
                var d = p.DotProduct(normal);
                lowA = Math.Min(lowA, d);
                highA = Math.Max(highA, d);
            }

            foreach (var p in other)
            {
                var d = p.DotProduct(normal);
                lowB = Math.Min(lowB, d);
                highB = Math.Max(highB, d);
            }

            if (highB <= lowA + touching || highA <= lowB + touching) return true;
        }

        return false;
    }

    /// <summary>Boards to check a candidate at this height against: its cell and both neighbours.</summary>
    private List<PlacedBoard> OccupantsNear(double z)
    {
        var cell = (long)Math.Floor(z / OccupancyCell);
        var near = new List<PlacedBoard>();

        for (var offset = -1L; offset <= 1L; offset++)
            if (_occupied.TryGetValue(cell + offset, out var bucket)) near.AddRange(bucket);

        return near;
    }

    /// <summary>
    /// Seeds the collision check with boards this run could not remove.
    ///
    /// Only ever the ones another user owns on a central model: everything else was deleted
    /// before placement began. Without this the run places a second board along every line
    /// they occupy - coplanar, invisible in any view, and double in every schedule. The old
    /// report said "the new run will sit on top of them", and it did.
    /// </summary>
    private void SeedStanding(IEnumerable<Element> survivors)
    {
        foreach (var element in survivors)
        {
            try
            {
                // No room context for someone else's leftover board, so no normal. It still
                // blocks a duplicate on its own line, which is what it is here for.
                if ((element.Location as LocationCurve)?.Curve is Line line) Occupy(line, null);
            }
            catch
            {
                // No readable location; it cannot be matched, so it cannot be avoided.
            }
        }
    }

    /// <summary>
    /// The part of a board that is actually inside the room, or null when none of it is.
    ///
    /// TWO PATHS, AND WHICH ONE RAN IS COUNTED. The room's own solid answers this wherever
    /// one can be built, because a solid IS the boundary and an intersection against it is
    /// exact at the corners and doorways where point probes are least reliable. Where the
    /// solid cannot be built - an unenclosed room, volumes off - the older sampling path
    /// still runs, and <see cref="_roomsWithoutSolid"/> says how many rooms that was.
    ///
    /// WHAT CHANGED. Every failure used to return the board UNCHANGED: an undecidable side,
    /// a thrown intersection, anything. That is not a neutral default - it places a board
    /// through whatever it was crossing, and it looks exactly like a board that passed the
    /// test, so no number in the report ever moved. The solid path refuses instead, and
    /// counts the refusal.
    /// </summary>
    private Curve? ClipToRoom(Room room, Curve piece, double forgiveStart, double forgiveEnd)
    {
        if (!_settings.ConfineToRoom || piece is not Line line)
        {
            _clipNotAttempted++;
            return piece;
        }

        return _containment is not null
            ? ClipToRoomSolid(line, forgiveStart, forgiveEnd)
            : ClipBySampling(room, line, forgiveStart, forgiveEnd);
    }

    /// <summary>
    /// Containment answered against the room solid. See <see cref="RoomContainment"/>.
    ///
    /// The corner allowance is applied here rather than inside the containment class, which
    /// is why that class returns the SPAN and not only a curve: where the room ends is a
    /// question about geometry, whether that is a reason to shorten the board is a question
    /// about corners, and the two must not be decided in the same place.
    /// </summary>
    private Curve? ClipToRoomSolid(Line piece, double forgiveStart, double forgiveEnd)
    {
        var containment = _containment;
        if (containment is null) return piece;

        try
        {
            var start = piece.GetEndPoint(0);
            var end = piece.GetEndPoint(1);
            var length = start.DistanceTo(end);

            if (length < 1e-9) return null;

            // Mid-board height, pulled into the solid's own extent. A board's line and the
            // room solid's underside are the same plane, and an intersection along a shared
            // plane is settled by rounding rather than by geometry.
            var result = containment.ClipCurve(piece, containment.ProbeZ(start.Z + (BoardBand / 2.0)));

            switch (result.Outcome)
            {
                case ClipOutcome.WhollyInside:
                    return piece;

                case ClipOutcome.Undecidable:
                    _clipUndecidable++;
                    _clipRefused++;
                    if (result.Reason is not null) _clipReasons.Add(result.Reason);
                    return null;   // FAIL CLOSED. An unverifiable board is not built.

                case ClipOutcome.WhollyOutside:
                    return Apply(
                        piece,
                        ClipRules.ForNothingInside(length, forgiveStart, forgiveEnd));
            }

            return Apply(
                piece,
                ClipRules.ForSpan(
                    length, result.From, result.To,
                    forgiveStart, forgiveEnd, _settings.MinimumRun));
        }
        catch (Exception ex)
        {
            _clipUndecidable++;
            _clipRefused++;
            _clipReasons.Add($"clip failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Turns a decision back into a curve. The only place a containment clip becomes
    /// geometry, so the count and the cut cannot disagree.
    /// </summary>
    private Curve? Apply(Line piece, ClipDecision decision)
    {
        if (decision.Action == ClipAction.KeepWhole) return piece;
        if (decision.Action == ClipAction.Refuse) return null;

        try
        {
            var start = piece.GetEndPoint(0);
            var direction = (piece.GetEndPoint(1) - start).Normalize();

            var clipped = Line.CreateBound(
                start + direction * decision.From,
                start + direction * decision.To);

            _clippedToRoom++;
            return clipped;
        }
        catch (Exception ex)
        {
            // The rules said trim and the trim cannot be built. Refusing is the only honest
            // outcome: returning the whole board would build the length the rules just cut.
            _clipUndecidable++;
            _clipRefused++;
            _clipReasons.Add($"clipped span unbuildable: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Containment by sampling. The fallback, for rooms with no computable solid.
    ///
    /// Samples along the board at a small inward offset and keeps the first-to-last
    /// contiguous stretch that reports inside. Returns null when none of it does.
    ///
    /// Deliberately empirical. Every earlier attempt at containment reasoned about geometry
    /// - wall function, orientation, curve subsets - and each was defeated by a case it did
    /// not model. IsPointInRoom is the definition of "in this room", so a board clipped to
    /// where that answers yes is contained by construction rather than by argument.
    /// </summary>
    /// <param name="forgiveStart">
    /// How much apparent escape to tolerate at the piece's start before cutting it, and
    /// likewise <paramref name="forgiveEnd"/>. Non-zero only at an end that is a genuine
    /// corner. See the note at the call site: at a sharp corner the board legitimately
    /// occupies space the room does not, and clipping there is what opened the corner gaps.
    /// </param>
    private Curve? ClipBySampling(Room room, Line line, double forgiveStart, double forgiveEnd)
    {
        var piece = (Curve)line;

        try
        {
            var start = line.GetEndPoint(0);
            var end = line.GetEndPoint(1);
            var length = start.DistanceTo(end);

            if (length < 1e-9) return null;

            var direction = (end - start).Normalize();

            var inward = InwardNormal(room, start + direction * (length / 2.0), direction);

            if (inward is null)
            {
                // Undecidable. The board is left alone here, unlike the solid path, because
                // this room has no solid to refuse it against and dropping every board in an
                // unenclosed room would be worse than placing them. It is COUNTED now, which
                // it never was - these are the boards free to cross a boundary.
                _clipUndecidable++;
                _clipReasons.Add($"{Label(room)}: no room solid, and IsPointInRoom could not " +
                                 "decide which side a board is on");
                return piece;
            }

            var steps = Math.Max(2, (int)Math.Ceiling(length / _settings.ContainmentSample));

            int? first = null, last = null;

            for (var i = 0; i <= steps; i++)
            {
                var at = start + direction * (length * i / steps);

                if (!TryPointInRoom(room, at + inward * _settings.ContainmentProbe)) continue;

                first ??= i;
                last = i;
            }

            // WHOLLY OUTSIDE - but a short piece pinned between two corners is exactly the
            // case that reads that way and is still real. A 40 mm return in the throat of a
            // sharp corner has no interior sample at all, because the room there is narrower
            // than the probe. Keep it when it is short enough to be nothing but corner.
            if (first is null || last is null)
                return Apply(line, ClipRules.ForNothingInside(length, forgiveStart, forgiveEnd));

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

            // Everything above measures where the ROOM ends; ClipRules decides whether that
            // is a reason to shorten the BOARD. Shared with the solid path, so the corner
            // allowance cannot drift between the two.
            return Apply(
                line,
                ClipRules.ForSpan(
                    length, from, to, forgiveStart, forgiveEnd, _settings.MinimumRun));
        }
        catch (Exception ex)
        {
            // A failed test must not lose a board in a room that has no solid to check it
            // against - but it is a board nothing verified, and it is counted as one.
            _clipUndecidable++;
            _clipReasons.Add($"{Label(room)}: sampled clip failed: {ex.Message}");
            return piece;
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

    /// <summary>
    /// The run turned into the curve a board is placed on, shortened at its start so it does
    /// not overlap the board turning the corner into it.
    ///
    /// The trim is applied at the START only, and only where the previous board genuinely
    /// arrives at that corner - which the caller has already established. An end created by
    /// subtracting a doorway is never a corner, so it never moves, and that is what keeps the
    /// board tight to the jamb.
    /// </summary>
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
    /// <summary>
    /// How far a board must run PAST the apex to wrap an external corner - the boundary
    /// turning around the end of a partition. <c>depth / tan(solid / 2)</c>, where the solid
    /// angle is what the partition occupies, 360 minus the room's interior angle.
    ///
    /// THIS CORNER WAS DOING NOTHING AT ALL, ON A REASON THAT SOUNDED RIGHT AND WAS NOT.
    /// The engine classed interior angles over 180 as re-entrant, said "the boards DIVERGE
    /// instead of overlapping, so no trim is owed", and applied none. Diverging is true and
    /// the conclusion does not follow: they diverge and they also never meet. Round the
    /// outside of a partition end neither face's board reaches, and what is left is a notch
    /// the size of the board wrapped round the corner - measured at 314 mm^2 for a 20 mm
    /// board on a square partition, which is the whole corner missing.
    ///
    /// The fix is the mirror of the internal case: internal corners OVERLAP and one side is
    /// trimmed, external corners GAP and one side is extended. Extending is safe here in a
    /// way it is not internally - the board runs out over the far face's own line, where the
    /// other board is not, so it closes the notch to zero without sharing any material. The
    /// other board still starts at the apex untrimmed, which is what makes them meet.
    /// </summary>
    /// <summary>
    /// The interior angle at a corner, or null when there is no corner to speak of.
    /// Shared by the trim, the external wrap and the square test so all three agree.
    /// </summary>
    private static double? Interior(Curve current, Curve? next, double orientation)
    {
        if (next is null) return null;

        try
        {
            var incoming = Flatten(current.GetEndPoint(1) - current.GetEndPoint(0));
            var outgoing = Flatten(next.GetEndPoint(1) - next.GetEndPoint(0));

            if (incoming is null || outgoing is null) return null;

            var signedTurn = Math.Atan2(incoming.CrossProduct(outgoing).Z,
                                        incoming.DotProduct(outgoing));

            return Math.PI - (orientation * signedTurn);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Is this corner square enough that a trim gives an exact butt?
    ///
    /// Only at a right angle do two square-ended boards meet with no gap AND no shared
    /// material. Everywhere else the corner is filled and joined instead - see
    /// <see cref="SkirtingSettings.JoinAtCorners"/>.
    /// </summary>
    private bool IsSquareCorner(Curve current, Curve? next, double orientation) =>
        Interior(current, next, orientation) is { } interior &&
        Math.Abs(interior - (Math.PI / 2.0)) <= _settings.SquareCornerTolerance;

    private double ExternalRun(Curve current, Curve? next, double orientation)
    {
        if (next is null) return 0.0;

        try
        {
            var incoming = Flatten(current.GetEndPoint(1) - current.GetEndPoint(0));
            var outgoing = Flatten(next.GetEndPoint(1) - next.GetEndPoint(0));

            if (incoming is null || outgoing is null) return 0.0;

            var signedTurn = Math.Atan2(incoming.CrossProduct(outgoing).Z,
                                        incoming.DotProduct(outgoing));
            var interior = Math.PI - (orientation * signedTurn);

            // Internal or straight. Those are the trim case, handled by MitreLength.
            if (interior <= Math.PI + 1e-6) return 0.0;

            var solid = (2.0 * Math.PI) - interior;
            if (solid <= 1e-6) return 0.0;

            var tangent = Math.Tan(solid / 2.0);
            if (tangent <= 1e-6) return 0.0;

            var depth = CornerDepth;

            // Same clamp as the internal trim: a partition ending in a needle would otherwise
            // send the wrap to infinity.
            return Math.Clamp(depth / tangent, 0.0, depth * _settings.MaxMitreFactor);
        }
        catch
        {
            return 0.0;
        }
    }

    private static XYZ? Flatten(XYZ direction)
    {
        var flat = new XYZ(direction.X, direction.Y, 0);
        return flat.GetLength() < 1e-9 ? null : flat.Normalize();
    }

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
            var depth = CornerDepth;

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
    /// The stretches of a reveal run that the opening's own family does NOT already fill.
    ///
    /// The whole answer to "prevent door reveals doubling up when a family incorporates a
    /// full lining", and it is an answer by measurement. The run is intersected with the
    /// insert's real solids; whatever comes back is material the family already provides, and
    /// it is subtracted. A fully lined door returns nothing and gets no board. A door with a
    /// frame at one face returns that frame's depth and gets a board on the rest. A cased
    /// opening has nothing in the reveal and gets the whole run.
    ///
    /// WHY THE PROBE IS LIFTED. The reveal run lies on the floor plane, and so does the
    /// bottom face of every lining. Intersecting a curve with a solid along their shared
    /// plane is decided by rounding rather than by geometry, and it can report either answer.
    /// The test is therefore run on a copy of the line at the board's mid-height, where it is
    /// unambiguously inside or outside the lining. The two lines differ only in Z, so a
    /// distance measured along one is the same distance along the other.
    ///
    /// This is also the guarantee that nothing here touches the door model: every part of the
    /// run that the door occupies is removed BEFORE any instance is created.
    /// </summary>
    private List<Curve> UnlinedParts(Curve reveal, Element insert, double baseZ)
    {
        if (!_settings.SkipLinedReveals || reveal is not Line line) return [reveal];

        var solids = _geometry.ElementSolids(insert);
        if (solids.Count == 0) return [reveal];

        var start = line.GetEndPoint(0);
        var end = line.GetEndPoint(1);
        var length = start.DistanceTo(end);

        if (length < 1e-9) return [];

        Line probe;
        try
        {
            var lift = new XYZ(0, 0, baseZ + (BoardBand / 2.0) - start.Z);
            probe = Line.CreateBound(start + lift, end + lift);
        }
        catch
        {
            return [reveal];
        }

        var options = new SolidCurveIntersectionOptions
        {
            ResultType = SolidCurveIntersectionMode.CurveSegmentsInside,
        };

        var probeStart = probe.GetEndPoint(0);
        var lined = new List<Span>();

        foreach (var solid in solids)
        {
            SolidCurveIntersection? inside;
            try { inside = solid.IntersectWithCurve(probe, options); }
            catch { continue; }

            if (inside is null) continue;

            for (var i = 0; i < inside.SegmentCount; i++)
            {
                try
                {
                    var segment = inside.GetCurveSegment(i);

                    var a = probeStart.DistanceTo(segment.GetEndPoint(0));
                    var b = probeStart.DistanceTo(segment.GetEndPoint(1));

                    lined.Add(new Span(Math.Min(a, b), Math.Max(a, b)));
                }
                catch
                {
                    // Unreadable segment contributes no coverage.
                }
            }
        }

        if (lined.Count == 0) return [reveal];

        // A lining is rarely one solid - lining, stop, architrave, glazing bead all arrive
        // separately and meet with a fraction of a millimetre between them. Bridging those
        // joints stops a sliver of "bare" reveal collecting a board between two parts of the
        // same frame, exactly as it does for a row of kitchen units.
        var direction = (end - start).Normalize();
        var parts = new List<Curve>();

        foreach (var bare in SkirtingRun.Subtract(
                     new Span(0.0, length),
                     SkirtingRun.Coalesce(lined, _settings.MinimumRun)))
        {
            if (bare.Extent < _settings.MinimumRun) continue;

            try
            {
                parts.Add(Line.CreateBound(
                    start + direction * bare.Start,
                    start + direction * bare.End));
            }
            catch
            {
                // Degenerate remainder.
            }
        }

        return parts;
    }

    /// <summary>
    /// A line from the room-side face straight into the wall, at one jamb.
    ///
    /// Which way is "into the wall" is settled by asking the room, not by trusting the
    /// wall's orientation: the perpendicular is taken from the curve's own tangent, both
    /// directions are probed, and the one that leaves the room is the one the reveal runs
    /// along. That works for curved walls and for walls drawn either way round.
    ///
    /// WHERE IT ENDS IS MEASURED, NOT ASSUMED. The far end used to be the wall's Width taken
    /// off the near face. That is exactly right for a room whose boundary came back at Finish
    /// location and wrong for one that fell back to Center, where the near face IS the
    /// centreline and Width overshoots the far face by half a wall. The far room's own solid
    /// knows where its finish face is, so the reveal is cut to it. See MeasuredFarFace.
    /// </summary>
    private Curve? RevealRun(Curve axis, Room room, double parameter, double thickness)
    {
        try
        {
            var start = axis.Evaluate(parameter, false);
            var tangent = axis.ComputeDerivatives(parameter, false).BasisX.Normalize();
            var perpendicular = tangent.CrossProduct(XYZ.BasisZ).Normalize();

            var inward = RevealDirection(room, start, perpendicular, thickness);
            if (inward is null) return null;

            var end = start + inward * thickness;

            // INTERIOR ONLY.
            //
            // The run spans the wall's whole thickness, so on an external wall it comes out
            // the far face and puts skirting on the outside of the building. Asking what is
            // just beyond that far face settles it: another room means a genuine internal
            // reveal shared by two rooms, nothing means open air or unmodelled space and the
            // board has left the room-bounding range entirely.
            //
            // THIS CANNOT BE ANSWERED BY THE RAY. The reveal leaves this room at its first
            // step, so this room's solid says nothing about what is on the other side of the
            // wall, and the far side is a different room that has to be found before it can
            // be asked. The ray measures the far face; only GetRoomAtPoint can say whose it
            // is - which is also why the 'Udvendig' test below is still needed.
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

                var measured = MeasuredFarFace(farRoom, beyond, inward, start, thickness);
                if (measured is not null) end = measured;
            }

            return Line.CreateBound(start, end);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Which way the reveal runs into the wall, or null when the jamb sits somewhere nothing
    /// can answer for.
    ///
    /// BY RAY, NOT BY A PAIR OF YES/NO PROBES. It was two IsPointInRoom probes 30 mm either
    /// side, and a reveal was abandoned whenever they agreed - which happens at a corner, in
    /// a doorway throat, and anywhere the enclosure is marginal, so the reveals were lost
    /// exactly where openings are. A ray returns a LENGTH, and the room side is simply the
    /// side the ray runs further into. Two lengths can be compared where two coin flips
    /// cannot be.
    ///
    /// The probes are also lifted off the floor plane, for the same reason the containment
    /// clip is: the boundary curve and the room solid's underside are the same plane, and a
    /// point there is on the boundary rather than inside or outside it.
    /// </summary>
    private XYZ? RevealDirection(Room room, XYZ start, XYZ perpendicular, double thickness)
    {
        var containment = _containment;

        if (containment is not null)
        {
            // Far enough to clear the wall, so a ray that starts in open room space is not
            // cut short by an obstruction a few centimetres in.
            var reach = Math.Max(thickness, SkirtingSettings.SideProbe * 4.0);

            var origin = new XYZ(start.X, start.Y, containment.ProbeZ(start.Z + (BoardBand / 2.0)));

            var forward = containment.MaxDepth(origin, perpendicular, reach).Length;
            var backward = containment.MaxDepth(origin, perpendicular.Negate(), reach).Length;

            // A tie means the ray settled nothing - the jamb is not on this room's face at
            // all, which is what happens on a Center-boundary fallback where the curve runs
            // inside the wall. Fall through to the probes rather than guess from noise.
            if (RevealRules.RaySettles(forward, backward, SkirtingSettings.SideProbe))
            {
                _revealSideFromRay++;

                // INTO THE WALL is away from the room, so it is the SHORTER ray's direction.
                return RevealRules.RevealRunsForward(forward, backward)
                    ? perpendicular
                    : perpendicular.Negate();
            }
        }

        var probe = SkirtingSettings.SideProbe;

        var forwardInRoom = TryPointInRoom(room, start + perpendicular * probe);
        var backwardInRoom = TryPointInRoom(room, start - perpendicular * probe);

        // Both or neither means the jamb sits somewhere ambiguous - a corner, or a room
        // whose enclosure is broken. Guessing here drives a board through the wall.
        if (forwardInRoom == backwardInRoom)
        {
            _revealSideUnknown++;
            return null;
        }

        return forwardInRoom ? perpendicular.Negate() : perpendicular;
    }

    /// <summary>
    /// The far room's finish face on this wall, or null when it cannot be measured.
    ///
    /// <paramref name="beyond"/> is already known to be inside the far room - the interior
    /// test just resolved it there - so a ray from it back TOWARDS the wall leaves that room
    /// exactly at the face the reveal should stop on. Whatever the near curve's location and
    /// whatever the wall's Width parameter says, that face is where the wall ends.
    ///
    /// Refused rather than trusted when the result is not a sane reveal: behind the start,
    /// or more than half a wall past the assumed end. A measurement that disagrees with the
    /// wall that much is a sign the ray found something else, not a reason to build a board
    /// there.
    /// </summary>
    private XYZ? MeasuredFarFace(Room farRoom, XYZ beyond, XYZ inward, XYZ start, double thickness)
    {
        var containment = ContainmentFor(farRoom);
        if (containment is null) return null;

        var back = inward.Negate();
        var depth = containment.MaxDepth(beyond, back, thickness + (SkirtingSettings.SideProbe * 2.0));

        if (depth.Outcome != ClipOutcome.Clipped) return null;

        var face = beyond + (back * depth.Length);

        var reach = (face - start).DotProduct(inward);

        // Behind the jamb, or improbably deep. Either way the wall's own Width is the better
        // answer than a ray that found something the wall is not.
        if (!RevealRules.DepthIsBelievable(
                reach, thickness, _settings.MinimumRun, MaxRevealDepthFactor))
        {
            return null;
        }

        var drift = Math.Abs(reach - thickness);

        if (drift > 1e-6)
        {
            _revealDepthMeasured++;
            if (drift > _revealDepthWorst) _revealDepthWorst = drift;
        }

        return face;
    }

    /// <summary>
    /// A containment for a room other than the one being skirted, built once and kept.
    ///
    /// A spatial element calculation per jamb would be ruinous - the same handful of rooms
    /// sit on the far side of every opening in a unit.
    /// </summary>
    private RoomContainment? ContainmentFor(Room room)
    {
        long id;
        try { id = room.Id.Value; }
        catch { return null; }

        if (_farContainment.TryGetValue(id, out var cached)) return cached;

        RoomContainment? containment;
        try { containment = new RoomContainment(_doc, room); }
        catch { containment = null; }

        if (containment is not null && !containment.IsUsable) containment = null;

        _farContainment[id] = containment;
        return containment;
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

            long? category;
            try { category = insert.Category?.Id.Value; }
            catch { continue; }

            if (category is null) continue;

            // WIDENED TO THE DECLARED REVEAL CATEGORIES. It was doors alone, which left the
            // jamb of a floor-height window or a glazed door - the same physical reveal, the
            // same board on site - unmodelled and unmeasured. The floor-reach gate above is
            // what keeps ordinary windows out; there is no need for the category list to do
            // that job as well, and doing it there was silently excluding real jambs.
            if (!_settings.RevealCategories.Any(c => (long)c == category)) continue;

            // A door with a full lining is the one case where a reveal board doubles up with
            // family geometry, so it stays behind a setting - now on by default, because the
            // brief asks for door jambs. A cased opening has no lining and is included
            // regardless of that setting.
            if (category == (long)BuiltInCategory.OST_Doors &&
                !_settings.WrapIntoDoorReveals &&
                !IsCasedOpening(insert))
            {
                continue;
            }

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

            // IS THIS INSERT ACTUALLY ON THIS RUN? Same guard PlaceReveals already uses
            // (SkirtingRun.OnRun), and for the identical reason: FindInserts returns every
            // insert hosted anywhere in the WALL, and a wall is very often shared by several
            // rooms along its length. Without this, a door hosted near one room's stretch of
            // a long shared wall still gets measured against every OTHER room's short axis
            // segment too - and Curve.Project on a bound curve clamps a far-away point onto
            // the segment's nearest end rather than rejecting it, which manufactures a
            // blocker sitting exactly at that other room's corner. Confirmed 2026-09-04: a
            // door on wall 29307987 connecting Bad and Gang, measured against Entre's 1450 mm
            // stretch of the same wall over 2 m away, cut ~455 mm off Entre's run at the
            // corner nearest that door - a notch with no element anywhere near it, because
            // the "blocker" was never really there.
            //
            // The reach formula is the same as PlaceReveals' too, deliberately: an insert
            // that genuinely straddles this run's own end must still be kept (see OnRun's own
            // doc comment), so this only rejects inserts that are actually elsewhere on the
            // wall, not ones this run legitimately shares a corner with.
            if (!SkirtingRun.OnRun(axis, insert, _doc, SafeWidth(wall), _settings.JambMargin))
            {
                _openingsElsewhere++;
                continue;
            }

            // Real jamb geometry first - lining and architrave, which are wider than the
            // hole. Rough Width is the fallback for inserts with no readable solids.
            var span = SkirtingRun.FromJambGeometry(
                           axis,
                           _geometry.ElementSolids(insert),
                           // A REAL VERTICAL TOLERANCE, NOT THE HORIZONTAL PAD.
                           //
                           // This was baseZ - OpeningPad, and OpeningPad is a sideways
                           // clearance that defaults to zero - so the band started at exactly
                           // the floor. Every vertex this measurement depends on is AT the
                           // floor: a lining's bottom edge sits on it, and the vertical edges
                           // of the jamb tessellate to one point there and one at the head.
                           // Any rounding a hair below the floor dropped the lot, the whole
                           // jamb measurement returned null, and it fell back silently to
                           // Rough Width - which stops the board at the hole and lets it run
                           // on through the architrave, the exact fault this pass exists to
                           // prevent.
                           baseZ - ZTolerance,
                           baseZ + BoardBand,
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
    /// Stretches of a run occupied by the actual solids of a door, window or opening family -
    /// whichever wall hosts it.
    ///
    /// This is the companion to <see cref="OpeningSpans"/> and it answers a different
    /// question. OpeningSpans asks "where is the hole in THIS wall", from that wall's own
    /// insert list, and it is right to. This asks "is there frame in the way", and the answer
    /// does not care which wall the frame belongs to. A door in the wall around the corner
    /// puts its architrave on this wall's face just the same, and that door will never appear
    /// in this wall's inserts.
    ///
    /// Measured by intersecting the run with the family's real solids, so the swung leaf that
    /// makes a door's bounding box a metre wider than its frame costs nothing here.
    /// </summary>
    /// <param name="alreadyHandled">
    /// Inserts of the run's OWN wall, which <see cref="OpeningSpans"/> has already measured.
    ///
    /// WITHOUT THIS THE SAME DOOR IS SUBTRACTED TWICE, AND THE SECOND CUT IS WIDER. The two
    /// passes answer different questions - where is the hole in this wall, versus is there
    /// frame in the way - and for a door in THIS wall the answer is the same door, measured
    /// two different ways. The jamb pass measures the opening; the geometry pass measures
    /// every solid the family owns near the run, which includes architrave lapped onto the
    /// face and a leaf swung across it.
    ///
    /// The measured cost of the overlap was severe: eight blockers on one 2370 mm face with
    /// four doors listed twice, and 992 mm of board missing from it. The geometry pass exists
    /// for doors hosted in OTHER walls, which the insert list cannot see. Doors in this wall
    /// belong to the jamb pass alone.
    /// </param>
    private IEnumerable<Span> OpeningGeometrySpans(
        Curve axis, double baseZ, IReadOnlySet<long> alreadyHandled)
    {
        if (!_settings.AvoidOpeningGeometry || axis is not Line line) yield break;

        var probe = LiftedProbe(line, baseZ + (BoardBand / 2.0));
        if (probe is null) yield break;

        foreach (var insert in _openingModels)
        {
            if (alreadyHandled.Contains(insert.Id.Value))
            {
                _openingsAlreadyMeasured++;
                continue;
            }

            if (!WithinReach(insert, line)) continue;

            var hits = SolidSpansAlong(axis, probe, _geometry.ElementSolids(insert));
            if (hits.Count == 0) continue;

            _openingGeometryBreaks++;
            Record(insert, "opening geometry");

            foreach (var span in hits) yield return span;
        }
    }

    /// <summary>
    /// A copy of the run at the board's mid-height. Intersecting on the floor plane itself is
    /// decided by rounding - every lining's underside and the run both lie on it.
    /// </summary>
    private static Line? LiftedProbe(Line line, double z)
    {
        try
        {
            var start = line.GetEndPoint(0);
            var lift = new XYZ(0, 0, z - start.Z);

            return Line.CreateBound(start + lift, line.GetEndPoint(1) + lift);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Cheap rejection before any geometry is read: could this element's bounding box reach
    /// the run at all? A model has hundreds of doors and a run is next to at most a few.
    /// </summary>
    private bool WithinReach(Element element, Line run)
    {
        try
        {
            var box = element.get_BoundingBox(null);
            if (box is null) return false;

            var reach = _settings.OpeningGeometryReach;

            var a = run.GetEndPoint(0);
            var b = run.GetEndPoint(1);

            var lowX = Math.Min(a.X, b.X) - reach;
            var highX = Math.Max(a.X, b.X) + reach;
            var lowY = Math.Min(a.Y, b.Y) - reach;
            var highY = Math.Max(a.Y, b.Y) + reach;

            return box.Max.X >= lowX && box.Min.X <= highX &&
                   box.Max.Y >= lowY && box.Min.Y <= highY;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Where a set of solids crosses the run, in the run's own parameters. The probe carries
    /// the height; the parameters come back off the run itself so they compose with every
    /// other blocked span.
    /// </summary>
    private List<Span> SolidSpansAlong(Curve axis, Line probe, IReadOnlyList<Solid> solids)
    {
        var spans = new List<Span>();
        if (solids.Count == 0) return spans;

        var options = new SolidCurveIntersectionOptions
        {
            ResultType = SolidCurveIntersectionMode.CurveSegmentsInside,
        };

        var z = axis.GetEndPoint(0).Z;

        foreach (var solid in solids)
        {
            SolidCurveIntersection? inside;
            try { inside = solid.IntersectWithCurve(probe, options); }
            catch { continue; }

            if (inside is null) continue;

            for (var i = 0; i < inside.SegmentCount; i++)
            {
                try
                {
                    var segment = inside.GetCurveSegment(i);

                    var a = ParameterOn(axis, segment.GetEndPoint(0), z);
                    var b = ParameterOn(axis, segment.GetEndPoint(1), z);

                    if (a is { } first && b is { } second && Math.Abs(second - first) > 1e-9)
                        spans.Add(new Span(Math.Min(first, second), Math.Max(first, second)));
                }
                catch
                {
                    // Unreadable segment blocks nothing.
                }
            }
        }

        return spans;
    }

    private static double? ParameterOn(Curve axis, XYZ point, double z)
    {
        try { return axis.Project(new XYZ(point.X, point.Y, z))?.Parameter; }
        catch { return null; }
    }

    /// <summary>
    /// Casework standing against this stretch of wall, at skirting height.
    ///
    /// Both filters earn their place. Without the height band a wall-hung cabinet at 1.5 m
    /// deletes the board underneath it; without the reach test a unit on the far side of the
    /// room blocks a run it never touches, because a bounding box says nothing about
    /// distance from a line.
    /// </summary>
    private IEnumerable<Span> CaseworkSpans(Curve axis, double baseZ)
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
            if (box.Min.Z > baseZ + BoardBand) continue;
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
            // Same floor-level tolerance as the jamb pass, and for the same reason: a plinth
            // sits ON the floor, so its lowest vertices are exactly at baseZ.
            var span = SkirtingRun.FromJambGeometry(
                axis,
                _geometry.ElementSolids(unit),
                baseZ - ZTolerance,
                baseZ + BoardBand,
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
            .Where(IsPlaced);

    /// <summary>
    /// Is this a real room in the model, or a schedule placeholder?
    ///
    /// AREA IS NOT THE TEST, AND USING IT COST HALF THE MODEL. Measured in T05: Koekken,
    /// Alrum and Entre are all placed, enclosed, carry a Department and a Level and have real
    /// bounding boxes - and every one of them reports Area = 0, along with Volume and
    /// Perimeter. Revit does not always compute those, and a room with no computed area is
    /// still a room with walls around it.
    ///
    /// Filtering on Area therefore threw rooms away BEFORE any rule was applied, so they
    /// never appeared in the qualifying count, the excluded count, or anywhere in the report -
    /// they simply were not there, and the missing skirting had no trace to follow.
    ///
    /// The honest test is the one the engine needs anyway: an unplaced room has no Location,
    /// and a room worth skirting returns boundary segments. Anything that answers both is
    /// real, whatever its Area says.
    /// </summary>
    private static bool IsPlaced(Room room)
    {
        try
        {
            if (room.Location is null) return false;
        }
        catch
        {
            return false;
        }

        return BoundaryLoops(room, SpatialElementBoundaryLocation.Finish).Count > 0 ||
               BoundaryLoops(room, SpatialElementBoundaryLocation.Center).Count > 0;
    }

    /// <summary>
    /// Notes one element that actually cut a board.
    ///
    /// Every gap in the finished skirting now has a named cause in the report. Without it,
    /// diagnosing "why is there a break here" means guessing from a screenshot at which of
    /// four rules fired - which is how several rounds of this went.
    /// </summary>
    private void Record(Element element, string why)
    {
        string category;
        try { category = element.Category?.Name ?? "?"; }
        catch { category = "?"; }

        // Per-run first and ALWAYS - it is short-lived, it is what names an empty face, and
        // it must not be silenced by the model-wide list filling up.
        _runBlockers.Add($"{category} '{SafeName(element)}' (id {element.Id.Value})");

        if (_blockedBy.Count > 200) return;   // a report, not a database

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

    /// <summary>
    /// M&amp;E by category or by name, whatever category the family was filed under.
    ///
    /// The category test is the reliable one; the name test is the safety net for families
    /// authored into Casework or Specialty Equipment by mistake, which happens often enough
    /// with radiators to be worth covering.
    /// </summary>
    private bool NeverBlocks(Element element)
    {
        // A HOLE IN THE WALL ALWAYS BLOCKS, WHATEVER IT IS CALLED.
        //
        // Opening is the API's own class for a void cut through a host - there is nothing to
        // fix a board to and nothing to run behind. It can never be exempt, and testing it by
        // name is how it became exempt: Revit names these elements "Rectangular Straight Wall
        // Opening", and NeverBlockHints carries "opening" to catch void-cutter FAMILIES like
        // 'Floor Void'. The substring matched the API's own name and every wall opening in
        // the model stopped breaking the run, so boards were placed straight across holes.
        //
        // Class beats name. The hints exist for families filed in the wrong category, which
        // is a naming problem; an Opening is not a naming problem.
        if (element is Opening) return false;

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

    /// <summary>
    /// Does this element come down into the board's height band, [floor, floor + board]?
    ///
    /// The one test that decides whether anything interrupts a run. Applied identically to
    /// openings and to casework, because the physical question is identical: is there
    /// something in the way at skirting height, or does the board pass underneath?
    /// </summary>
    private bool ReachesBoard(Element element, double baseZ)
    {
        try
        {
            var box = element.get_BoundingBox(null);
            if (box is null) return false;

            // Its underside must be at or below the top of the board, and it must not stop
            // before the floor.
            return box.Min.Z <= baseZ + BoardBand && box.Max.Z >= baseZ;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// An 'Udvendig' placeholder room: a terrace or entrance area enclosed with room
    /// separation lines purely so it can be scheduled. It is outdoors, so no wall bounding it
    /// takes skirting - and critically, the walls it shares with the building are that
    /// building's EXTERIOR faces.
    ///
    /// Excluding the room rather than trying to classify its walls is what makes this correct
    /// at a shared wall: boards are placed per room-boundary segment, so skipping the room
    /// removes only its own face and leaves the interior room's face untouched.
    /// </summary>
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
                "users and were left in place. New boards will be trimmed around them rather than " +
                "laid on top, so nothing overlaps - but those stretches keep the previous run's " +
                "geometry until those users relinquish them.");

            // They survive, so they occupy their lines. Feeding them to the collision check
            // is what turns "the new run will sit on top of them" into a trim around them.
            SeedStanding(ownership.OwnedByOthers.Select(id => _doc.GetElement(id)).OfType<Element>());

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
    private void Stamp(Element instance, string value, Element? source = null)
    {
        // The Comments fallback that used to live here has moved into ElementStamp, so the
        // radiator and dimension tools get the same behaviour instead of silently having none.
        if (ElementStamp.WriteOrFallback(
                instance, SkirtingSettings.Stamp, value, SkirtingSettings.Stamp, source?.UniqueId))
            return;

        // Neither path took. Reported rather than swallowed: this board cannot be found by the
        // next Regenerate, so it will be left standing while a second one is placed over it.
        _problems.Add(
            $"Board {instance.Id.Value} could not be stamped, so a later Regenerate will not " +
            "recognise it as this tool's work - it will be left in place and a second board " +
            "laid over it. Delete it by hand, or check that Comments is writable on this type.");
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
