using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Sweeps;

/// <summary>A stretch of a boundary curve, in that curve's own raw parameters.</summary>
public readonly record struct Span(double Start, double End)
{
    public double Extent => End - Start;
}

/// <summary>
/// Turns one room-boundary curve into the list of stretches that actually get a board.
///
/// WHY SUBTRACT RATHER THAN CUT
///   A native wall sweep is one element that Revit visually breaks at inserts. A component
///   family cannot do that - there is no flag - so the equivalent is to place SHORTER
///   PIECES: work out which stretches survive once openings and casework are removed, and
///   place one instance per stretch.
///
///   That is better than placing a full-length board and voiding it, for a reason that only
///   shows up later: a voided board still reports its full length in a schedule. Skirting is
///   bought and priced by the metre, so a takeoff that counts the doorway is wrong in the
///   direction that costs money. Pieces carry their real lengths.
/// </summary>
public static class SkirtingRun
{
    /// <summary>
    /// Merges blocked spans that overlap or are separated by less than
    /// <paramref name="bridge"/>, so a row of adjacent units reads as one obstruction.
    ///
    /// WHY THIS IS NEEDED. Kitchen base units stand side by side with a millimetre or two
    /// between them, and each is measured separately. Subtracting them individually leaves a
    /// sliver of "free" wall in every joint, and the engine dutifully places a 20 mm board
    /// in it. There is no skirting between two cabinets - the run of joinery is continuous -
    /// so the gaps between them are part of the obstruction, not part of the wall.
    ///
    /// The tolerance is a curve PARAMETER distance. For a Line that is length, which covers
    /// every real case; on an arc the parameter is angular and the bridge is approximate.
    /// </summary>
    public static List<Span> Coalesce(IEnumerable<Span> blocked, double bridge)
    {
        var ordered = blocked.Where(s => s.Extent > 0).OrderBy(s => s.Start).ToList();
        if (ordered.Count == 0) return [];

        var merged = new List<Span> { ordered[0] };

        foreach (var span in ordered.Skip(1))
        {
            var last = merged[^1];

            if (span.Start <= last.End + bridge)
                merged[^1] = new Span(last.Start, Math.Max(last.End, span.End));
            else
                merged.Add(span);
        }

        return merged;
    }

    /// <summary>
    /// Everything left of <paramref name="whole"/> once every blocked span is removed.
    /// Blocked spans may overlap, nest, or fall outside; all are handled.
    /// </summary>
    public static List<Span> Subtract(Span whole, IEnumerable<Span> blocked)
    {
        var surviving = new List<Span> { whole };

        foreach (var block in blocked)
        {
            if (block.Extent <= 0) continue;

            var next = new List<Span>(surviving.Count + 1);

            foreach (var run in surviving)
            {
                // No overlap: the run passes through untouched.
                if (block.End <= run.Start || block.Start >= run.End)
                {
                    next.Add(run);
                    continue;
                }

                // A block landing in the middle splits one run into two; a block covering
                // an end shortens it; a block covering everything deletes it.
                if (block.Start > run.Start) next.Add(new Span(run.Start, block.Start));
                if (block.End < run.End) next.Add(new Span(block.End, run.End));
            }

            surviving = next;
        }

        return surviving;
    }

    /// <summary>
    /// The parameter range an element occupies along a curve, from its bounding box.
    ///
    /// Conservative by design: a bounding box is at least as large as the element, so a
    /// board is never left running through something. The cost is over-blocking on
    /// diagonally-placed casework, which leaves a slightly short board - visible, fixable,
    /// and much better than a board buried inside a cabinet.
    /// </summary>
    public static Span? FromBoundingBox(Curve axis, BoundingBoxXYZ box)
    {
        var corners = new[]
        {
            new XYZ(box.Min.X, box.Min.Y, box.Min.Z), new XYZ(box.Max.X, box.Min.Y, box.Min.Z),
            new XYZ(box.Min.X, box.Max.Y, box.Min.Z), new XYZ(box.Max.X, box.Max.Y, box.Min.Z),
        };

        double? low = null, high = null;

        foreach (var corner in corners)
        {
            var parameter = ProjectParameter(axis, corner);
            if (parameter is not { } t) continue;

            if (low is null || t < low) low = t;
            if (high is null || t > high) high = t;
        }

        return low is null || high is null ? null : new Span(low.Value, high.Value);
    }

    /// <summary>
    /// The span an INSERT occupies, measured from its width rather than its bounding box.
    ///
    /// This exists because a door's 3D bounding box includes the swung panel. Blocking by
    /// that box removes skirting from a metre of wall the door merely swings over, which is
    /// wrong and very visible. The width parameter describes the hole in the wall, which is
    /// the thing the board actually has to stop at.
    ///
    /// Falls back to the bounding box for inserts with no readable width - wall openings,
    /// mostly, whose box IS the hole.
    /// </summary>
    public static Span? FromInsert(Curve axis, Element insert, Document doc, double pad)
    {
        var centre = LocationOf(insert);
        var width = WidthOf(insert, doc);

        if (centre is not null && width is > 0)
        {
            var atCentre = ProjectPoint(axis, centre);

            if (atCentre is not null)
            {
                XYZ direction;
                try
                {
                    var t = ProjectParameter(axis, centre);
                    direction = t is { } p
                        ? axis.ComputeDerivatives(p, false).BasisX.Normalize()
                        : (axis.GetEndPoint(1) - axis.GetEndPoint(0)).Normalize();
                }
                catch
                {
                    direction = (axis.GetEndPoint(1) - axis.GetEndPoint(0)).Normalize();
                }

                var reach = width / 2.0 + pad;

                var a = ProjectParameter(axis, atCentre - direction * reach);
                var b = ProjectParameter(axis, atCentre + direction * reach);

                if (a is { } first && b is { } second)
                    return new Span(Math.Min(first, second), Math.Max(first, second));
            }
        }

        BoundingBoxXYZ? box;
        try { box = insert.get_BoundingBox(null); }
        catch { return null; }

        if (box is null) return null;

        var span = FromBoundingBox(axis, box);
        return span is { } s ? new Span(s.Start - pad, s.End + pad) : null;
    }

    /// <summary>
    /// Is this insert actually cut into THIS stretch of wall?
    ///
    /// THE GUARD THAT WAS MISSING, AND WHY IT COST REVEAL BOARDS.
    ///   Inserts are found per WALL, but measured against a RUN - one room's stretch of that
    ///   wall. A wall almost always runs past the room it bounds, so most of its inserts
    ///   belong to somebody else's stretch.
    ///
    ///   <c>Curve.Project</c> on a BOUND curve clamps to the nearest endpoint, so a door
    ///   thirty metres down the corridor projects silently onto this run's end and reports a
    ///   perfectly plausible parameter. Nothing downstream can tell that apart from a real
    ///   opening at the corner. The observable damage was in the reveal pass: the far door
    ///   was claimed by the wrong room, its two jambs collapsed onto one point, and the room
    ///   that actually contains the door never got a board - so reveals went missing at the
    ///   openings that needed them and appeared at corners that had none.
    ///
    /// The test is the distance from the insert's own centre to the projected point, in plan.
    /// An insert genuinely on this run sits half a wall thickness away - the run is on the
    /// face, the insert on the centreline. One elsewhere sits its true distance away, which
    /// is metres. An insert straddling the run's end reads about half its own width, which is
    /// why the width is part of the reach: it does breach this stretch and must be kept.
    /// </summary>
    public static bool OnRun(Curve axis, Element insert, Document doc, double hostThickness, double margin)
    {
        var centre = LocationOf(insert) ?? BoxCentre(insert);

        // Undecidable. Keep the insert rather than silently suppress a real opening.
        if (centre is null) return true;

        XYZ? nearest;
        try { nearest = axis.Project(centre)?.XYZPoint; }
        catch { return true; }

        if (nearest is null) return true;

        var reach = hostThickness + (WidthOf(insert, doc) / 2.0) + margin;
        var flat = new XYZ(nearest.X - centre.X, nearest.Y - centre.Y, 0);

        return flat.GetLength() <= reach;
    }

    /// <summary>
    /// The span a door's JAMB occupies, measured from the door's real geometry at skirting
    /// height rather than from any width parameter.
    ///
    /// Rough Width describes the hole cut in the wall. The jamb - lining plus architrave -
    /// is wider than that hole, because the architrave laps onto the wall face either side.
    /// Blocking by Rough Width therefore stops the board at the hole and lets it run on
    /// through the architrave, which is what the brief calls passing through the jamb.
    ///
    /// The measurement takes every vertex of the door's solids that lies BOTH within the
    /// board's height band AND within the wall's thickness plus a margin. The height band
    /// is what excludes head and transom geometry; the thickness test is what excludes a
    /// swung leaf, which stands perpendicular to the wall and is nowhere near it. What
    /// survives is lining and architrave at floor level - the outer corner of the jamb.
    /// </summary>
    public static Span? FromJambGeometry(
        Curve axis, IReadOnlyList<Solid> solids, double zLow, double zHigh, double reach)
    {
        double? low = null, high = null;

        foreach (var solid in solids)
        {
            foreach (Edge edge in solid.Edges)
            {
                IList<XYZ> points;
                try { points = edge.Tessellate(); }
                catch { continue; }

                foreach (var point in points)
                {
                    if (point.Z < zLow || point.Z > zHigh) continue;

                    // Distance from the run itself stands in for distance from the wall:
                    // the run lies on the wall's room-side face.
                    double offset;
                    try { offset = axis.Distance(new XYZ(point.X, point.Y, axis.GetEndPoint(0).Z)); }
                    catch { continue; }

                    if (offset > reach) continue;

                    var parameter = ProjectParameter(axis, point);
                    if (parameter is not { } t) continue;

                    if (low is null || t < low) low = t;
                    if (high is null || t > high) high = t;
                }
            }
        }

        return low is null || high is null || high - low <= 0 ? null : new Span(low.Value, high.Value);
    }

    // -------------------------------------------------------------------- helpers

    private static XYZ? LocationOf(Element element)
    {
        try { return (element.Location as LocationPoint)?.Point; }
        catch { return null; }
    }

    /// <summary>
    /// Centre of the element's bounding box. The fallback anchor for inserts with no
    /// LocationPoint - a wall Opening is the case that matters, and its box IS the hole.
    /// </summary>
    private static XYZ? BoxCentre(Element element)
    {
        try
        {
            var box = element.get_BoundingBox(null);
            return box is null ? null : (box.Min + box.Max) / 2.0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Rough width first: it is the hole cut in the wall, which is what the board stops at.
    /// The nominal Width is the leaf, and leaves the board running into the frame.
    /// </summary>
    private static double WidthOf(Element insert, Document doc)
    {
        var holders = new List<Element> { insert };

        try
        {
            var type = doc.GetElement(insert.GetTypeId());
            if (type is not null) holders.Add(type);
        }
        catch
        {
            // No type; the instance alone.
        }

        foreach (var name in new[] { "Rough Width", "Void Width" })
        {
            foreach (var holder in holders)
            {
                var parameter = holder.LookupParameter(name);
                if (parameter is { HasValue: true, StorageType: StorageType.Double } && parameter.AsDouble() > 0)
                    return parameter.AsDouble();
            }
        }

        foreach (var holder in holders)
        {
            var parameter = holder.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM);
            if (parameter is { HasValue: true } && parameter.AsDouble() > 0) return parameter.AsDouble();

            var named = holder.LookupParameter("Width");
            if (named is { HasValue: true, StorageType: StorageType.Double } && named.AsDouble() > 0)
                return named.AsDouble();
        }

        return 0.0;
    }

    private static double? ProjectParameter(Curve axis, XYZ point)
    {
        try { return axis.Project(point)?.Parameter; }
        catch { return null; }
    }

    private static XYZ? ProjectPoint(Curve axis, XYZ point)
    {
        try { return axis.Project(point)?.XYZPoint; }
        catch { return null; }
    }
}
