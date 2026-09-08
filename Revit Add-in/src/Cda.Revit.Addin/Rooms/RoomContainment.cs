using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace Cda.Revit.Addin.Rooms;

/// <summary>Why a containment answer is what it is. There is no "unclipped" outcome.</summary>
public enum ClipOutcome
{
    /// <summary>Trimmed to the boundary. <see cref="ClipResult.From"/>/<c>To</c> say where.</summary>
    Clipped,

    /// <summary>Entirely within the room; no trim owed.</summary>
    WhollyInside,

    /// <summary>No part of it is in the room. Nothing to build.</summary>
    WhollyOutside,

    /// <summary>
    /// The room could not answer. NOT the same as outside, and NOT a licence to build:
    /// callers must handle this explicitly and say so in their report.
    /// </summary>
    Undecidable,
}

/// <summary>
/// One containment answer. <see cref="From"/> and <see cref="To"/> are distances along the
/// curve that was tested, measured from its start point, so a caller can apply its own
/// allowances before rebuilding a curve - which is why the span is returned and not only the
/// clipped curve.
/// </summary>
public readonly record struct ClipResult(
    ClipOutcome Outcome, Curve? Curve, double From, double To, string? Reason)
{
    public double Length => Math.Max(0.0, To - From);
}

/// <summary>
/// Containment answered against the room's own SOLID, not against IsPointInRoom probes and
/// not against bounding boxes.
///
/// WHY A SOLID
///   A probe is a sample. It answers for one point, it is ambiguous on any boundary plane,
///   and a false answer is indistinguishable from "outside" - so a room whose volume cannot
///   be computed reads as "everything is outside" and a probe that lands on the floor plane
///   reads as either. The room solid IS the boundary, so an intersection against it is exact
///   everywhere, including at the corners and doorways where probes are least reliable and
///   where boards actually meet.
///
/// WHY IT FAILS CLOSED
///   An undecidable answer returns <see cref="ClipOutcome.Undecidable"/> and never the input
///   curve. Geometry driven through a wall reads as deliberate and gets built on site; a
///   missing board with a reason in the report gets fixed. Returning the input on failure is
///   the single behaviour this class exists to remove.
///
/// PRECONDITIONS, both the caller's job
///   - Volume computation ON (see <see cref="VolumesComputed"/>), or the solid follows the
///     room's upper limit instead of the built ceiling.
///   - <c>Document.Regenerate()</c> after the last wall, room-limit or opening change and
///     BEFORE construction. Wall joins and opening cuts also run at COMMIT, so a clip
///     computed against pre-commit boundaries can be stale by the time the model is saved.
/// </summary>
public sealed class RoomContainment
{
    /// <summary>Kept off every boundary plane. Half a probe's worth of clearance, in feet.</summary>
    private const double Pad = 0.02;

    private readonly Solid? _room;
    private readonly string? _unavailable;
    private readonly double _minZ;
    private readonly double _maxZ;

    public RoomContainment(Document doc, Room room)
    {
        if (!SpatialElementGeometryCalculator.CanCalculateGeometry(room))
        {
            _unavailable = "room geometry cannot be computed (unenclosed, or volumes off)";
            return;
        }

        // FINISH, deliberately. The solid then stops at the face a board touches. Center
        // extends it to the wall centreline, which is half a wall thickness of licence to
        // pass through the boundary - the exact symptom this class exists to remove.
        var options = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
        };

        try
        {
            _room = new SpatialElementGeometryCalculator(doc, options)
                .CalculateSpatialElementGeometry(room)
                .GetGeometry();
        }
        catch (Exception ex)
        {
            _unavailable = $"room geometry failed: {ex.Message}";
            return;
        }

        if (_room is null || _room.Volume <= 1e-9)
        {
            _room = null;
            _unavailable = "room solid is empty";
            return;
        }

        (_minZ, _maxZ) = VerticalExtent(_room);
    }

    public bool IsUsable => _room is not null;

    /// <summary>Why not, when <see cref="IsUsable"/> is false.</summary>
    public string? Unavailable => _unavailable;

    /// <summary>
    /// Whether rooms are being computed as volumes. With this OFF a room is a prism between
    /// its base and upper limit, so every containment answer is about room PARAMETERS rather
    /// than about built space. Read-only here: enabling it writes to the document, which is
    /// <c>RoomBoundaryAdjuster.EnsureVolumes</c>'s job.
    /// </summary>
    public static bool VolumesComputed(Document doc)
    {
        try { return AreaVolumeSettings.GetAreaVolumeSettings(doc).ComputeVolumes; }
        catch { return false; }
    }

    /// <summary>
    /// A Z at which to test, given the one the caller would prefer.
    ///
    /// WHY THIS EXISTS. A run on the floor and the room solid's underside are the same plane,
    /// and an intersection along a shared plane is settled by rounding rather than by
    /// geometry - it can report either answer. Worse, a floor finish raises the solid's
    /// underside ABOVE the run, so a probe at the run's own Z is below the room entirely and
    /// nothing reads as inside. Both failures look identical from the outside: no coverage.
    ///
    /// The preferred Z is therefore pulled into the solid's own vertical extent, clear of
    /// both caps. A distance measured at one Z is the same distance at another, so nothing
    /// about the answer changes except that it can now be given.
    /// </summary>
    public double ProbeZ(double preferred)
    {
        if (_room is null) return preferred;

        var low = _minZ + Pad;
        var high = _maxZ - Pad;

        if (high <= low) return (_minZ + _maxZ) / 2.0;   // a very shallow room: its middle

        return Math.Clamp(preferred, low, high);
    }

    /// <summary>
    /// The part of <paramref name="run"/> inside the room, tested at
    /// <paramref name="probeZ"/> rather than on the run's own plane. Put the Z through
    /// <see cref="ProbeZ"/> first.
    ///
    /// Returns the FIRST-to-LAST inside stretch, so a doorway crossed mid-run does not split
    /// one board in two. Callers that want the split should read the segments themselves.
    /// </summary>
    public ClipResult ClipCurve(Line run, double probeZ)
    {
        if (_room is null) return new(ClipOutcome.Undecidable, null, 0.0, 0.0, _unavailable);

        var start = run.GetEndPoint(0);
        var end = run.GetEndPoint(1);
        var length = start.DistanceTo(end);

        if (length < 1e-9)
            return new(ClipOutcome.WhollyOutside, null, 0.0, 0.0, "degenerate run");

        Line probe;
        try
        {
            // Bound, always: IntersectWithCurve throws on an unbound curve, and that throw
            // arrives in a catch block as "no coverage".
            var lift = new XYZ(0, 0, probeZ - start.Z);
            probe = Line.CreateBound(start + lift, end + lift);
        }
        catch (Exception ex)
        {
            return new(ClipOutcome.Undecidable, null, 0.0, 0.0, $"probe unbuildable: {ex.Message}");
        }

        SolidCurveIntersection? inside;
        try
        {
            inside = _room.IntersectWithCurve(probe, new SolidCurveIntersectionOptions
            {
                ResultType = SolidCurveIntersectionMode.CurveSegmentsInside,
            });
        }
        catch (Exception ex)
        {
            return new(ClipOutcome.Undecidable, null, 0.0, 0.0, $"intersection threw: {ex.Message}");
        }

        if (inside is null || inside.SegmentCount == 0)
            return new(ClipOutcome.WhollyOutside, null, 0.0, 0.0, null);

        var origin = probe.GetEndPoint(0);
        double from = double.MaxValue, to = double.MinValue;

        for (var i = 0; i < inside.SegmentCount; i++)
        {
            try
            {
                var segment = inside.GetCurveSegment(i);

                var a = origin.DistanceTo(segment.GetEndPoint(0));
                var b = origin.DistanceTo(segment.GetEndPoint(1));

                from = Math.Min(from, Math.Min(a, b));
                to = Math.Max(to, Math.Max(a, b));
            }
            catch
            {
                // An unreadable segment contributes no coverage. If every segment is
                // unreadable the span stays empty and the guard below reports it.
            }
        }

        if (from > to)
            return new(ClipOutcome.Undecidable, null, 0.0, 0.0, "no readable inside segment");

        if (from <= 1e-6 && to >= length - 1e-6)
            return new(ClipOutcome.WhollyInside, run, 0.0, length, null);

        Curve? clipped = null;
        try
        {
            var direction = (end - start).Normalize();
            clipped = Line.CreateBound(start + direction * from, start + direction * to);
        }
        catch
        {
            // The span is still the answer; a caller applying its own allowances may not
            // need this curve at all, and one it cannot build it can refuse for itself.
        }

        return new(ClipOutcome.Clipped, clipped, from, to, null);
    }

    /// <summary>
    /// How far an extrusion may run from <paramref name="at"/> along
    /// <paramref name="direction"/> before it leaves the room: the contiguous inside stretch
    /// that CONTAINS the origin, so a second room further along the ray cannot extend the
    /// answer across the wall between them.
    /// </summary>
    public ClipResult MaxDepth(XYZ at, XYZ direction, double limit)
    {
        if (_room is null) return new(ClipOutcome.Undecidable, null, 0.0, 0.0, _unavailable);

        Line ray;
        try { ray = Line.CreateBound(at, at + direction.Normalize() * limit); }
        catch (Exception ex)
        {
            return new(ClipOutcome.Undecidable, null, 0.0, 0.0, $"ray unbuildable: {ex.Message}");
        }

        SolidCurveIntersection? inside;
        try
        {
            inside = _room.IntersectWithCurve(ray, new SolidCurveIntersectionOptions
            {
                ResultType = SolidCurveIntersectionMode.CurveSegmentsInside,
            });
        }
        catch (Exception ex)
        {
            return new(ClipOutcome.Undecidable, null, 0.0, 0.0, $"intersection threw: {ex.Message}");
        }

        if (inside is null || inside.SegmentCount == 0)
            return new(ClipOutcome.WhollyOutside, null, 0.0, 0.0, "origin is not in this room");

        for (var i = 0; i < inside.SegmentCount; i++)
        {
            try
            {
                var segment = inside.GetCurveSegment(i);

                var a = at.DistanceTo(segment.GetEndPoint(0));
                var b = at.DistanceTo(segment.GetEndPoint(1));

                // The stretch starting AT the origin. Anything further along the ray lies
                // beyond a boundary the extrusion must not cross.
                if (Math.Min(a, b) > Pad) continue;

                return new(ClipOutcome.Clipped, segment, 0.0, Math.Max(a, b), null);
            }
            catch
            {
                // Try the next segment; one unreadable result is not an answer.
            }
        }

        return new(ClipOutcome.WhollyOutside, null, 0.0, 0.0, "origin sits outside every inside run");
    }

    /// <summary>Whatever of a built solid is inside the room. Null means nothing usable is.</summary>
    public Solid? ClipSolid(Solid candidate)
    {
        if (_room is null) return null;

        try
        {
            var kept = BooleanOperationsUtils.ExecuteBooleanOperation(
                candidate, _room, BooleanOperationsType.Intersect);

            return kept is not null && kept.Volume > 1e-9 ? kept : null;
        }
        catch
        {
            return null;   // fail closed: an unverifiable solid is not built
        }
    }

    /// <summary>
    /// A solid's Z range in MODEL coordinates.
    ///
    /// Solid.GetBoundingBox returns a box in the SOLID's own coordinate system with a
    /// transform that is not always the identity - reading Min.Z and Max.Z off it directly
    /// compares two different frames and is a silent, plausible-looking error. Both corners
    /// go through the transform, and the range is taken from the results.
    /// </summary>
    private static (double Min, double Max) VerticalExtent(Solid solid)
    {
        try
        {
            var box = solid.GetBoundingBox();

            var a = box.Transform.OfPoint(box.Min);
            var b = box.Transform.OfPoint(box.Max);

            return (Math.Min(a.Z, b.Z), Math.Max(a.Z, b.Z));
        }
        catch
        {
            // ProbeZ then clamps to the caller's preference, which is what it did before
            // this class existed. No worse, and it does not fail the whole room.
            return (double.MinValue, double.MaxValue);
        }
    }
}
