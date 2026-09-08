namespace Cda.Revit.Addin.Dimensions;

/// <summary>
/// The arithmetic of the room dimensioner, in plain doubles with NO REVIT TYPE ANYWHERE.
///
/// WHY THIS FILE IS SEPARATE FROM THE CODE THAT USES IT
///   Same reason as Heating/Seating.cs, and for the same class of bug. Everything that can go
///   silently wrong in this tool is a SIGN CONVENTION - which side of a wall the room is on,
///   which way a boundary loop winds, which wall a dimension hugs. None of those produce an
///   exception. They produce a drawing that looks finished and measures the wrong thing, and
///   the only way to catch them is to assert on the numbers.
///
///   RevitAPI.dll is a managed wrapper over native code and will not load outside the Revit
///   host, so any file mentioning XYZ can only be tested by opening Revit and looking. Keeping
///   the conventions here means tests/Cda.Dimensions.Tests compiles THIS FILE - not a copy of
///   it - and runs on a build machine, in CI, or while Revit is open with the model loaded.
///
/// UNIT-AGNOSTIC. Nothing here knows about feet or millimetres; it operates on whatever the
/// caller passes. The add-in passes Revit's internal units, the tests pass millimetres, and
/// both are correct because no constant in this file has a unit attached to it.
/// </summary>
internal static class DimensionMath
{
    /// <summary>A direction or position in the plan, with Z discarded.</summary>
    internal readonly record struct Vec2(double X, double Y)
    {
        public double Length => Math.Sqrt((X * X) + (Y * Y));

        public Vec2 Normalized()
        {
            var length = Length;
            return length < Epsilon ? new Vec2(1, 0) : new Vec2(X / length, Y / length);
        }

        public double Dot(Vec2 other) => (X * other.X) + (Y * other.Y);

        public Vec2 Negated() => new(-X, -Y);

        public Vec2 Scaled(double factor) => new(X * factor, Y * factor);

        public Vec2 Plus(Vec2 other) => new(X + other.X, Y + other.Y);
    }

    public const double Epsilon = 1e-9;

    /// <summary>
    /// Ninety degrees anticlockwise, which is the plan-view meaning of Z cross d.
    /// This one function carries the tool's entire handedness; if it is wrong, every
    /// dimension in every room measures to the far side of its wall.
    /// </summary>
    public static Vec2 LeftOf(Vec2 direction) => new(-direction.Y, direction.X);

    /// <summary>
    /// The direction pointing INTO the room from a boundary segment running <paramref
    /// name="direction"/> on a loop of the given winding.
    ///
    /// The room interior is on the LEFT of travel for an anticlockwise loop. Revit documents
    /// outer boundary loops as anticlockwise and holes as clockwise, but this takes the
    /// winding as an argument rather than assuming it, because a loop that came back the other
    /// way round would otherwise put every dimension on the wrong face with nothing to show
    /// for it.
    /// </summary>
    public static Vec2 Inward(Vec2 direction, bool clockwise)
    {
        var left = LeftOf(direction.Normalized());
        return clockwise ? left.Negated() : left;
    }

    /// <summary>
    /// Shoelace over a closed loop's segment endpoints. Positive is anticlockwise seen from
    /// above, which is the only thing the caller needs and the reason this returns the signed
    /// value rather than a bool - the magnitude is a useful sanity check in tests.
    /// </summary>
    public static double SignedArea(IReadOnlyList<(Vec2 A, Vec2 B)> loop)
    {
        var total = 0.0;

        foreach (var (a, b) in loop)
            total += (a.X * b.Y) - (b.X * a.Y);

        return total / 2.0;
    }

    /// <summary>
    /// One direction per axis, so a run and its reverse are the same run.
    ///
    /// The tie-break on X near zero is not decoration. Without it a wall running due north
    /// and one running due south canonicalise to opposite vectors, land in different groups,
    /// and a plain rectangular room reports four axes instead of two.
    /// </summary>
    public static Vec2 Canonical(Vec2 direction)
    {
        var flat = direction.Normalized();

        if (flat.X < -Epsilon) return flat.Negated();
        if (Math.Abs(flat.X) <= Epsilon && flat.Y < 0) return flat.Negated();

        return flat;
    }

    public static bool SameAxis(Vec2 a, Vec2 b, double tolerance = 1e-6)
    {
        var ca = Canonical(a);
        var cb = Canonical(b);

        return Math.Abs(ca.X - cb.X) <= tolerance && Math.Abs(ca.Y - cb.Y) <= tolerance;
    }

    /// <summary>
    /// The axis the room runs along, weighted by how much WALL points each way.
    ///
    /// NOT BY FACE COUNT. A room with one long facade and four short returns off it has five
    /// faces, four of which agree with each other and none of which is the wall a person would
    /// say the room runs along. Summing length answers the question that was asked; counting
    /// faces answers a different one and is right only by coincidence.
    /// </summary>
    public static Vec2 PrincipalAxis(IReadOnlyList<(Vec2 Along, double Length)> runs)
    {
        if (runs.Count == 0) return new Vec2(1, 0);

        var axes = new List<(Vec2 Axis, double Total)>();

        foreach (var (along, length) in runs)
        {
            var axis = Canonical(along);
            var index = axes.FindIndex(x => SameAxis(x.Axis, axis));

            if (index < 0) axes.Add((axis, length));
            else axes[index] = (axes[index].Axis, axes[index].Total + length);
        }

        var best = axes[0];
        foreach (var candidate in axes)
            if (candidate.Total > best.Total) best = candidate;

        return best.Axis;
    }

    /// <summary>
    /// True when a face with this inward normal is one of the ENDS of a run along
    /// <paramref name="run"/> - and therefore has a position on it that can be dimensioned.
    ///
    /// A face whose normal is perpendicular to the run lies ALONGSIDE the dimension and has no
    /// position on it at all. Absolute value, because both ends count: the near face's normal
    /// points along the run and the far face's points back down it.
    /// </summary>
    public static bool IsSquareTo(Vec2 inwardNormal, Vec2 run, double cosineTolerance) =>
        Math.Abs(inwardNormal.Normalized().Dot(run.Normalized())) >= cosineTolerance;

    /// <summary>The four extreme face positions of a room in its own (u, v) frame.</summary>
    internal readonly record struct Extent(double MinU, double MaxU, double MinV, double MaxV)
    {
        public double AlongU => MaxU - MinU;
        public double AlongV => MaxV - MinV;
    }

    public static Extent Measure(IReadOnlyList<Vec2> facePoints, Vec2 u, Vec2 v)
    {
        double minU = double.MaxValue, maxU = double.MinValue;
        double minV = double.MaxValue, maxV = double.MinValue;

        foreach (var point in facePoints)
        {
            var pu = point.Dot(u);
            var pv = point.Dot(v);

            minU = Math.Min(minU, pu);
            maxU = Math.Max(maxU, pu);
            minV = Math.Min(minV, pv);
            maxV = Math.Max(maxV, pv);
        }

        return new Extent(minU, maxU, minV, maxV);
    }

    /// <summary>Where one dimension string sits, and which way the room is from it.</summary>
    /// <param name="Across">The dimension LINE's coordinate on the across axis.</param>
    /// <param name="Inward">
    /// Unit vector from the hugged wall INTO the room. Carried because Revit's own default is
    /// no help: it draws dimension text on the LEFT of the line's direction, and for a string
    /// hugging a wall that side is the wall. Everything about text placement downstream is
    /// measured along this vector rather than left to Revit to choose.
    /// </param>
    /// <param name="WallAlongInward">
    /// The hugged wall's finish face, as a coordinate along <paramref name="Inward"/>. Text
    /// clearance is specified from the WALL, not from the dimension line, so the wall's own
    /// position has to travel with the placement.
    /// </param>
    internal readonly record struct Placement(
        double AlongMin, double AlongMax, double Across, Vec2 Inward, double WallAlongInward);

    /// <summary>
    /// The along-the-room string, drawn hard against the FAR wall - the 3700 across the top
    /// of Entre. Offset is subtracted, so the line lands inside the room and not in the wall,
    /// and the room is therefore in the MINUS v direction from it.
    /// </summary>
    /// <summary>
    /// One dimension string measuring along <paramref name="axis"/>.
    ///
    /// THE GENERAL CASE, and the reason this replaced a hardcoded pair of runs. A room used to
    /// get exactly two strings - one along its principal axis and one across - which silently
    /// assumed every wall in every room is square to one of those two. Any wall that is not,
    /// and a splayed corner or an angled partition is not, produced no dimension at all and no
    /// message saying so. The wall was simply absent from the drawing.
    ///
    /// Every run hugs the FAR side along its own perpendicular and is offset back into the
    /// room. That single rule reproduces both of the old runs exactly - the along-run hugging
    /// the top wall and the across-run hugging the left one are the same rule seen from two
    /// axes - and extends to any number of them without a special case.
    /// </summary>
    /// <param name="perpendicularMax">
    /// The room's furthest extent along LeftOf(axis) - the wall this string hugs.
    /// </param>
    public static Placement Run(
        Vec2 axis, double alongMin, double alongMax, double perpendicularMax, double offset)
    {
        var perpendicular = LeftOf(axis);

        return new Placement(
            alongMin, alongMax,
            perpendicularMax - offset,
            perpendicular.Negated(),
            -perpendicularMax);
    }

    /// <summary>
    /// The distinct axes a room's wall normals fall on, most wall first.
    ///
    /// Clustered at the SQUARENESS TOLERANCE, not exactly. Walls drawn by hand are a fraction
    /// of a degree off each other, and treating those as separate axes would put two nearly
    /// identical dimension strings on top of one another - each holding the faces the other
    /// should have had.
    /// </summary>
    public static List<Vec2> WallAxes(
        IReadOnlyList<(Vec2 Normal, double Length)> faces, double cosineTolerance)
    {
        var axes = new List<(Vec2 Axis, double Total)>();

        foreach (var (normal, length) in faces)
        {
            var axis = Canonical(normal);
            var index = axes.FindIndex(x => IsSquareTo(x.Axis, axis, cosineTolerance));

            if (index < 0) axes.Add((axis, length));
            else axes[index] = (axes[index].Axis, axes[index].Total + length);
        }

        return axes.OrderByDescending(x => x.Total).Select(x => x.Axis).ToList();
    }

    /// <summary>Compass bearing of an axis in degrees, for naming a run in the report.</summary>
    public static double BearingOf(Vec2 axis)
    {
        var canonical = Canonical(axis);
        var degrees = Math.Atan2(canonical.Y, canonical.X) * 180.0 / Math.PI;

        return degrees < 0 ? degrees + 180 : degrees;
    }

    public static Placement AlongRun(Extent extent, double offset, Vec2 u, Vec2 v) =>
        Run(u, extent.MinU, extent.MaxU, extent.MaxV, offset);

    /// <summary>
    /// The across-the-room string, drawn hard against the NEAR wall - the 1450 down the left
    /// of Entre. Offset is added here and subtracted above; that asymmetry is the whole reason
    /// the two strings end up on adjacent walls instead of crossing in the middle of the room.
    /// </summary>
    public static Placement AcrossRun(Extent extent, double offset, Vec2 u, Vec2 v) =>
        Run(v, extent.MinV, extent.MaxV, -extent.MinU, offset);

    // ---- corner to corner --------------------------------------------------------

    /// <summary>
    /// A wall face that RUNS ALONG the measured axis - the face a corner-to-corner dimension
    /// is drawn against, and whose two ends it measures.
    /// </summary>
    /// <param name="Offset">The face's own coordinate on the perpendicular, LeftOf(axis).</param>
    /// <param name="InwardSign">+1 when the room lies along +LeftOf(axis) from this face.</param>
    internal readonly record struct FaceRun(
        int Index, double SpanMin, double SpanMax, double Offset, double InwardSign);

    /// <summary>A face SQUARE to the measured axis - one of the corners a run terminates at.</summary>
    internal readonly record struct FaceEnd(int Index, double At);

    /// <summary>One corner-to-corner dimension: a face, and the two faces that end it.</summary>
    internal readonly record struct CornerRun(
        int StartFace, int EndFace, int AlongFace,
        double StartAt, double EndAt, double FaceOffset, double InwardSign)
    {
        public double Clear => EndAt - StartAt;
    }

    /// <summary>
    /// One dimension per wall face, measured from the corner where it starts to the corner
    /// where it ends.
    ///
    /// WHY THIS REPLACED SIGHTLINE PAIRING, AND WHY IT IS THE SIMPLER RULE
    ///   The requirement is that every wall face carries a dimension. Corner-to-corner
    ///   satisfies it BY CONSTRUCTION - every face has two ends, so no face can be missed -
    ///   whereas measuring across the room only dimensions a face if something happens to be
    ///   opposite it with enough overlap and nothing in between. All of that machinery, and
    ///   every edge case it brought, exists only to answer a question this rule never asks.
    ///
    ///   It also settles what looked like two bugs. Vaer. 1's 1900 and 1950 were wrong as
    ///   CLEAR dimensions, because a clear dimension crosses the room and both of those cross
    ///   a partition. As the corner-to-corner lengths of the lower-leg ceiling and the Gang
    ///   partition they are exactly right, and both appear in the reference drawing.
    ///
    /// ONE PER PAIR OF CORNERS, NOT ONE PER FACE. A rectangular room's top and bottom faces
    /// both run between the same two side walls and measure the same distance; drawing both
    /// would put two identical numbers on one room. The reference drawing shows Entre with
    /// 3700 once, so the duplicate is dropped and the survivor is the face further along the
    /// perpendicular - the top and the left, which is where the reference puts them.
    /// </summary>
    public static List<CornerRun> CornerToCorner(
        IReadOnlyList<FaceRun> runs, IReadOnlyList<FaceEnd> ends, double matchTolerance)
    {
        var found = new List<CornerRun>();

        foreach (var run in runs)
        {
            var start = Nearest(ends, run.SpanMin, matchTolerance);
            var end = Nearest(ends, run.SpanMax, matchTolerance);

            if (start is null || end is null) continue;          // an open end - caller reports it
            if (end.Value.At - start.Value.At <= matchTolerance) continue;

            Keep(found, new CornerRun(
                start.Value.Index, end.Value.Index, run.Index,
                start.Value.At, end.Value.At, run.Offset, run.InwardSign));
        }

        return found;
    }

    private static FaceEnd? Nearest(IReadOnlyList<FaceEnd> ends, double at, double tolerance)
    {
        FaceEnd? best = null;
        var bestDistance = double.MaxValue;

        foreach (var end in ends)
        {
            var distance = Math.Abs(end.At - at);

            if (distance > tolerance || distance >= bestDistance) continue;

            bestDistance = distance;
            best = end;
        }

        return best;
    }

    /// <summary>
    /// Keeps one dimension per pair of corners: the one drawn against the face furthest along
    /// the perpendicular, which is the top of a room and its left-hand side.
    /// </summary>
    private static void Keep(List<CornerRun> found, CornerRun candidate)
    {
        for (var i = 0; i < found.Count; i++)
        {
            if (found[i].StartFace != candidate.StartFace) continue;
            if (found[i].EndFace != candidate.EndFace) continue;

            if (candidate.FaceOffset > found[i].FaceOffset) found[i] = candidate;

            return;
        }

        found.Add(candidate);
    }
    /// <summary>
    /// Where the text must be anchored so its BORDER stands <paramref name="offset"/> off the
    /// wall's interior finish face.
    ///
    /// ONE NUMBER GOVERNS THIS, and it is the same one that positions the dimension line. The
    /// office standard is 150 mm off the finish face; that is what the line does and what the
    /// text border does, so there is nothing to keep in step and nothing that can drift.
    ///
    /// TEXTPOSITION IS THE CENTRE OF THE TEXT, established from the model rather than from
    /// documentation that does not say either way. An earlier version branched on which way the
    /// text grows, on the theory that the anchor was the baseline. The drawing refuted it: one
    /// branch put the border half a text height INSIDE the wall and the other half a height too
    /// far out - equal and opposite errors, which is exactly what adding a whole height to a
    /// symmetric anchor produces.
    ///
    /// A centred anchor needs no branch at all. The box straddles it evenly whichever way the
    /// text reads, so offset plus half a text height puts the border at offset in every
    /// orientation and at every view scale. Having no branch is itself the guarantee: there is
    /// no second path for one orientation to drift away from the others later.
    /// </summary>
    public static double TextCentreOffWall(
        double wallAlongInward, double offset, double textHeight) =>
        wallAlongInward + offset + (textHeight / 2);

    /// <summary>
    /// Where the text's border ends up, as a distance from the wall's finish face. It should
    /// equal the offset asked for; negative would be text printing over the wall.
    /// </summary>
    public static double BorderOffsetFromWall(
        double centreAlongInward, double wallAlongInward, double textHeight) =>
        centreAlongInward - (textHeight / 2) - wallAlongInward;

    // ---- text fitting ------------------------------------------------------------

    /// <summary>Glyph width as a fraction of text height, before the type's own width factor.</summary>
    public const double GlyphAspect = 0.6;

    /// <summary>
    /// How wide a dimension's number will print, in model units.
    ///
    /// ESTIMATED, because Revit exposes no text extents anywhere in the API - not on the
    /// dimension, not on the segment, not on the type. Deliberately pessimistic at 0.6 of the
    /// text height per glyph, which is wider than most dimension fonts actually set digits.
    ///
    /// Being pessimistic is the right way round to be wrong. A too-generous estimate moves a
    /// number that would have fitted and someone drags it back in ten seconds; a too-tight one
    /// leaves overlapping text on a drawing that has already been issued.
    /// </summary>
    public static double TextWidth(int characters, double textHeightOnPaper, double widthFactor, int viewScale) =>
        characters * textHeightOnPaper * viewScale * GlyphAspect * widthFactor;

    /// <summary>Whether a number fits between its own two ticks, with clear space either side.</summary>
    public static bool FitsBetweenTicks(double segmentLength, double textWidth, double gap) =>
        segmentLength >= textWidth + (2 * gap);

    /// <summary>
    /// The signed nudge for band n: 1 up, 1 down, 2 up, 2 down, and so on.
    ///
    /// Alternating matters. A row of cramped segments all pushed the same way stacks into a
    /// single column that is its own collision, which is the failure the arranger exists to
    /// prevent rather than a smaller version of it.
    /// </summary>
    public static double BandOffset(int band, double step) =>
        band == 0 ? 0 : (band % 2 == 1 ? 1 : -1) * ((band + 1) / 2) * step;

    // ---- collision ---------------------------------------------------------------

    /// <summary>An axis-aligned rectangle in the view's own right/up frame.</summary>
    internal readonly record struct Box(double MinA, double MaxA, double MinB, double MaxB)
    {
        public bool Overlaps(Box other) =>
            MinA < other.MaxA && other.MinA < MaxA &&
            MinB < other.MaxB && other.MinB < MaxB;
    }

    /// <summary>
    /// The axis-aligned box, in the view's right/up frame, of a text rectangle rotated to sit
    /// along its dimension line.
    ///
    /// Projecting both half-extents onto each view axis and adding them is EXACT for a rotated
    /// rectangle - no sampling and no approximation. The approximation in this tool is the
    /// text width that goes in, not the box that comes out.
    /// </summary>
    public static Box RotatedBox(
        Vec2 centre, Vec2 along, Vec2 across, Vec2 right, Vec2 up, double width, double height)
    {
        var halfWidth = width / 2.0;
        var halfHeight = height / 2.0;

        var halfA = (Math.Abs(along.Dot(right)) * halfWidth) +
                    (Math.Abs(across.Dot(right)) * halfHeight);

        var halfB = (Math.Abs(along.Dot(up)) * halfWidth) +
                    (Math.Abs(across.Dot(up)) * halfHeight);

        var a = centre.Dot(right);
        var b = centre.Dot(up);

        return new Box(a - halfA, a + halfA, b - halfB, b + halfB);
    }

    /// <summary>
    /// The first band at which the text clears everything already placed, or -1 when it never
    /// does within <paramref name="maximumBands"/>.
    ///
    /// Returning -1 rather than the last band tried is the point. A number pushed six text
    /// heights off its own dimension line is no longer obviously attached to it, and an overlap
    /// a human can see and fix beats a leader wandering into open space.
    /// </summary>
    /// <param name="slide">
    /// The direction a colliding text is moved along, and it is the DIMENSION LINE rather
    /// than away from the wall.
    ///
    /// Moving away from the wall used to be how this resolved a clash, and it quietly broke
    /// the one rule the office states in millimetres: every text stands a fixed distance off
    /// its wall. One collision put a text 210 mm further in than its neighbours at 1:100, on a
    /// drawing where that distance is the specification. Sliding ALONG the line keeps the
    /// distance exact and moves the text somewhere it is still obviously part of the same
    /// dimension.
    /// </param>
    public static int FirstClearBand(
        Vec2 home, Vec2 along, Vec2 across, Vec2 slide, Vec2 right, Vec2 up,
        double width, double height, double step,
        bool mustMove, IReadOnlyList<Box> occupied, int maximumBands)
    {
        for (var band = mustMove ? 1 : 0; band <= maximumBands; band++)
        {
            var centre = home.Plus(slide.Scaled(BandOffset(band, step)));
            var box = RotatedBox(centre, along, across, right, up, width, height);

            var clear = true;
            foreach (var other in occupied)
                if (box.Overlaps(other)) { clear = false; break; }

            if (clear) return band;
        }

        return -1;
    }
}
