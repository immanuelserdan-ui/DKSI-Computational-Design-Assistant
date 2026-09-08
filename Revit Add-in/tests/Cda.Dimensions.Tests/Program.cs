using Cda.Revit.Addin.Dimensions;

using Vec2 = Cda.Revit.Addin.Dimensions.DimensionMath.Vec2;

namespace Cda.Dimensions.Tests;

/// <summary>
/// Runnable checks over <see cref="DimensionMath"/> - every sign convention in the room
/// dimensioner.
///
/// WHY THE NUMBERS ARE WHAT THEY ARE
///   They are the rooms off the reference plan, in millimetres, taken from the drawing the
///   tool is meant to reproduce:
///
///     Entre    3700 x 1450, dimensioned 3700 across the TOP and 1450 down the LEFT
///     Vaer. 1  3700 x 2950
///     Gang     1800 and 1900 chained across, 1950 down
///
///   The offset is 150 mm, as specified. Everything is unit-agnostic, so working in
///   millimetres here rather than Revit's decimal feet costs nothing and makes a wrong
///   assertion obvious on sight.
///
/// WHAT THESE ARE GUARDING AGAINST
///   Not crashes. Every failure this file can catch produces a drawing that looks finished
///   and measures the wrong thing: a dimension to the far side of a partition, two strings
///   crossing in the middle of a room instead of hugging its walls, a rotated wing dimensioned
///   on world axes. None of those throw.
/// </summary>
internal static class Program
{
    private static int _run;
    private static int _failed;

    // Entre, off the reference plan.
    private const double Width = 3700;
    private const double Depth = 1450;
    // The shipped settings: dimension line 150 mm off the finish face, text just past it.
    private const double Offset = 150;

    private const double Tol = 1e-9;

    private static int Main()
    {
        Console.WriteLine("Room dimension conventions\n");

        ShippedStandard();
        Winding();
        InwardNormals();
        Axes();
        Placement();
        BorderOffTheFinishFace();
        EveryWallAxis();
        EveryFaceMeasured();
        RotatedWing();
        Chaining();
        TextFitting();
        Collision();

        Console.WriteLine($"\n{_run - _failed}/{_run} passed.");

        if (_failed > 0) Console.WriteLine($"{_failed} FAILED.");

        return _failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- the conventions

    /// <summary>
    /// The numbers this tool actually ships with, as opposed to the numbers passed into the
    /// arithmetic by the tests below.
    ///
    /// These exist because the distinction cost two rounds of rework. Every other assertion in
    /// this file takes the offset as a parameter, so the suite passed 81/81 while the shipping
    /// offset was still 50 after being changed to 100. A formula that is right about a number
    /// nobody uses is not much of a guarantee.
    /// </summary>
    private static void ShippedStandard()
    {
        Section("the shipped office standard");

        Near("the dimension line stands 150 mm off the finish face",
            RoomDimensionDefaults.OffsetMillimetres, 150, Tol);

        True("the office dimension style is named, not left to Revit's default",
            RoomDimensionDefaults.DimensionTypeName == "_EM white 1.5mm");

        True("the exterior placeholder is excluded",
            RoomDimensionDefaults.ExteriorPlaceholderRoom == "Udvendig");

        // Asserted as VALUES, not as ranges. These are compile-time constants, so a range
        // check folds to 'true' before it ever runs - the compiler said so, CS8793 - and a
        // test that cannot fail is worse than no test because it reads like cover.
        // Squareness must stay well under 45, or a face can be claimed by the wrong axis.
        Near("squareness tolerance is 15 degrees",
            RoomDimensionDefaults.SquarenessToleranceDegrees, 15, Tol);

        // Coincident-face thinning: smaller than anything worth drawing, or it eats real
        // segments; larger than Revit's rounding, or it lets a zero-length one through.
        Near("coincident tolerance is 2 mm",
            RoomDimensionDefaults.CoincidentFaceToleranceMillimetres, 2, Tol);
    }

    private static void Winding()
    {
        Section("boundary winding");

        var ccw = Rectangle(0, 0, Width, Depth, clockwise: false);
        var cw = Rectangle(0, 0, Width, Depth, clockwise: true);

        Near("anticlockwise loop has positive area", DimensionMath.SignedArea(ccw), Width * Depth, 1e-6);
        Near("clockwise loop has negative area", DimensionMath.SignedArea(cw), -Width * Depth, 1e-6);

        // The magnitude is the room's actual area, which is the cheap sanity check that the
        // shoelace is a shoelace and not something that merely has the right sign.
        Near("magnitude is the room area", Math.Abs(DimensionMath.SignedArea(ccw)), 5_365_000, 1e-6);
    }

    private static void InwardNormals()
    {
        Section("inward normals");

        var centre = new Vec2(Width / 2, Depth / 2);
        var loop = Rectangle(0, 0, Width, Depth, clockwise: false);

        // THE test. Every one of the four walls must produce a normal pointing at the middle
        // of the room. Get this backwards and every dimension in the model measures to the
        // far side of its wall - out by the wall thickness, silently.
        foreach (var (a, b) in loop)
        {
            var direction = new Vec2(b.X - a.X, b.Y - a.Y);
            var inward = DimensionMath.Inward(direction, clockwise: false);
            var midpoint = new Vec2((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            var toCentre = new Vec2(centre.X - midpoint.X, centre.Y - midpoint.Y);

            True($"wall at ({midpoint.X:0},{midpoint.Y:0}) points into the room",
                inward.Dot(toCentre) > 0);
        }

        // Explicit handedness, so a future edit to LeftOf fails here rather than on a drawing.
        var left = DimensionMath.LeftOf(new Vec2(1, 0));
        Near("left of east is north (x)", left.X, 0, Tol);
        Near("left of east is north (y)", left.Y, 1, Tol);

        // A hole winds the other way, and the interior is then on the RIGHT of travel.
        var outer = DimensionMath.Inward(new Vec2(1, 0), clockwise: false);
        var hole = DimensionMath.Inward(new Vec2(1, 0), clockwise: true);
        Near("a clockwise loop flips the normal", outer.Dot(hole), -1, Tol);
    }

    private static void Axes()
    {
        Section("principal axis");

        Vec2 CanonicalOf(double x, double y) => DimensionMath.Canonical(new Vec2(x, y));

        Near("west canonicalises to east (x)", CanonicalOf(-1, 0).X, 1, Tol);
        Near("south canonicalises to north (y)", CanonicalOf(0, -1).Y, 1, Tol);

        True("due north and due south are one axis",
            DimensionMath.SameAxis(new Vec2(0, 1), new Vec2(0, -1)));

        True("east and north are not one axis",
            !DimensionMath.SameAxis(new Vec2(1, 0), new Vec2(0, 1)));

        // Entre runs east-west: 2 x 3700 of wall that way against 2 x 1450 the other.
        var entre = new List<(Vec2, double)>
        {
            (new Vec2(1, 0), Width), (new Vec2(1, 0), Width),
            (new Vec2(0, 1), Depth), (new Vec2(0, 1), Depth),
        };

        Near("Entre's long axis is east-west", DimensionMath.PrincipalAxis(entre).X, 1, Tol);

        // WEIGHTED BY LENGTH, NOT COUNT. One long facade against four short returns: counting
        // faces answers 'north-south' 4 to 1 and is wrong.
        var facade = new List<(Vec2, double)>
        {
            (new Vec2(1, 0), 12000),
            (new Vec2(0, 1), 900), (new Vec2(0, 1), 900),
            (new Vec2(0, 1), 900), (new Vec2(0, 1), 900),
        };

        Near("one long facade beats four short returns",
            DimensionMath.PrincipalAxis(facade).X, 1, Tol);

        True("...and counting faces would have said otherwise", facade.Count(f => f.Item1.Y > 0) == 4);
    }

    private static void Placement()
    {
        Section("150 mm placement");

        var u = new Vec2(1, 0);
        var v = DimensionMath.LeftOf(u);
        var extent = DimensionMath.Measure(FaceMidpoints(Width, Depth), u, v);

        Near("extent spans the room east-west", extent.AlongU, Width, Tol);
        Near("extent spans the room north-south", extent.AlongV, Depth, Tol);

        var along = DimensionMath.AlongRun(extent, Offset, u, v);
        var across = DimensionMath.AcrossRun(extent, Offset, u, v);

        Near("the along string measures 3700", along.AlongMax - along.AlongMin, Width, Tol);
        Near("the across string measures 1450", across.AlongMax - across.AlongMin, Depth, Tol);

        // Placement.Across is a coordinate on the RUN'S OWN perpendicular, not on world u or
        // v. For the across-string that perpendicular points west, so its numeric sign is not
        // the world x sign and comparing the two directly is meaningless - which is exactly
        // the mistake these three assertions used to make. Rebuild the world position instead.
        var alongLine = WorldCoordinate(along, v);
        var acrossLine = WorldCoordinate(across, u);

        // Hard against the TOP wall, offset inside it - the 3700 on the reference plan.
        Near("along string sits 150 mm off the far wall", extent.MaxV - alongLine, Offset, Tol);
        Near("along string is at y = 1300", alongLine, Depth - Offset, Tol);

        // Hard against the LEFT wall, offset inside it - the 1450 on the reference plan.
        Near("across string sits 150 mm off the near wall", acrossLine - extent.MinU, Offset, Tol);
        Near("across string is at x = 150", acrossLine, Offset, Tol);

        // Both INSIDE the room. An offset applied the wrong way round buries the line in the
        // wall, which reads as "the tool did nothing" rather than as an error.
        True("along string is inside the room", alongLine > extent.MinV && alongLine < extent.MaxV);
        True("across string is inside the room", acrossLine > extent.MinU && acrossLine < extent.MaxU);

        // Each run hugs the FAR side along its own perpendicular. Seen in world terms that
        // puts the two strings on adjacent walls rather than crossing mid-room.
        True("the two strings hug different walls",
            Math.Abs(alongLine - extent.MaxV) < Math.Abs(alongLine - extent.MinV) &&
            Math.Abs(acrossLine - extent.MinU) < Math.Abs(acrossLine - extent.MaxU));

        // Vaer. 1, same plan, deeper room - the offset must not scale with the room.
        var vaer = DimensionMath.Measure(FaceMidpoints(3700, 2950), u, v);
        Near("a deeper room still gets 150 mm",
            vaer.MaxV - DimensionMath.AlongRun(vaer, Offset, u, v).Across, Offset, Tol);

        // The inward vector points AWAY from the wall each string hugs. Revit's own default
        // is the opposite of this - left of the line's direction, which is the wall - and that
        // is what printed 1450 and 1800/1900 on the masonry.
        Near("the along string's room is south of it (y)", along.Inward.Y, -1, Tol);
        Near("...not north, which is where Revit would have put the text",
            DimensionMath.LeftOf(u).Y, 1, Tol);
        Near("the across string's room is east of it (x)", across.Inward.X, 1, Tol);
    }

    /// <summary>
    /// The text's BORDER sits on the wall's interior finish face, in every orientation.
    ///
    /// TWO WRONG ANSWERS GOT US HERE, and both are worth keeping in view because each looked
    /// convincing on half a drawing:
    ///
    ///   1. Computing a clearance and adding textHeight/2, on the assumption TextPosition is
    ///      the centre. Tops and lefts came out tight, bottoms and rights a text height out.
    ///   2. Concluding from that the anchor was the BASELINE, and branching on which way the
    ///      text grows. That put one orientation half a height inside the wall and the other
    ///      half a height too far out - equal and opposite, which is the signature of adding a
    ///      whole height to something already symmetric.
    ///
    /// The second failure is what identified the anchor: only a CENTRED anchor turns a
    /// whole-height correction into two half-height errors pointing opposite ways. So the rule
    /// is half a text height off the face, with no branch - and the absence of a branch is the
    /// real guarantee, because there is no second path for one orientation to drift down.
    /// </summary>
    private static void BorderOffTheFinishFace()
    {
        Section("text border 150 mm off the finish face");

        const double Wall = 0;

        // 1.5 mm text at three scales, so nothing here can depend on the height by accident.
        foreach (var height in new[] { 75.0, 150.0, 300.0 })
        {
            var centre = DimensionMath.TextCentreOffWall(Wall, Offset, height);

            Near($"a {height:0} mm text stands its border 150 mm off the face",
                DimensionMath.BorderOffsetFromWall(centre, Wall, height), Offset, Tol);

            True($"...and never inside the wall at {height:0} mm",
                DimensionMath.BorderOffsetFromWall(centre, Wall, height) >= -Tol);
        }

        // THE REGRESSION. Two opposite walls, each in its own inward frame where the room is
        // always the + direction. One expression, so they cannot disagree - which is the whole
        // point, since the previous two attempts disagreed by half a height and by a height.
        const double Height = 150;

        var oneWall = DimensionMath.TextCentreOffWall(Wall, Offset, Height);
        var theWallOpposite = DimensionMath.TextCentreOffWall(Wall, Offset, Height);

        Near("opposite orientations put their border in the same place",
            DimensionMath.BorderOffsetFromWall(oneWall, Wall, Height) -
            DimensionMath.BorderOffsetFromWall(theWallOpposite, Wall, Height),
            0, Tol);

        // The anchor sits the offset plus half a height in. Asserted directly so a change to
        // the rule fails here and not only through the border.
        Near("the anchor is the offset plus half a text height",
            oneWall - Wall, Offset + (Height / 2), Tol);

        // ONE NUMBER GOVERNS BOTH. The dimension line stands at the offset too, so the text
        // border and the line are the same distance off the face and move together.
        Near("the line and the text border are the same distance off the face",
            DimensionMath.BorderOffsetFromWall(oneWall, Wall, Height),
            RoomDimensionDefaults.OffsetMillimetres, Tol);

        // A wall elsewhere in the model, so nothing depends on the face being at zero.
        const double Far = 28908;

        Near("the rule holds for a wall away from the origin",
            DimensionMath.BorderOffsetFromWall(
                DimensionMath.TextCentreOffWall(Far, Offset, Height), Far, Height),
            Offset, Tol);
    }

    /// <summary>
    /// The regression for "some areas don't have a dimension".
    ///
    /// The generator used to place exactly TWO strings per room - one along the principal axis
    /// and one across it - which silently assumed every wall in every room is square to one of
    /// those two. A splayed corner or an angled partition is not, so it was dropped from the
    /// drawing with no message. These assert the precondition for the fix: that a room's
    /// actual axes are all found, so a run can be placed on each or the miss can be reported.
    /// </summary>
    private static void EveryWallAxis()
    {
        Section("every wall axis");

        var cos15 = Math.Cos(15 * Math.PI / 180);

        // A plain rectangular room. Four walls, two axes - not four.
        var rectangle = new List<(Vec2, double)>
        {
            (new Vec2(0, 1), Width), (new Vec2(0, -1), Width),
            (new Vec2(1, 0), Depth), (new Vec2(-1, 0), Depth),
        };

        var axes = DimensionMath.WallAxes(rectangle, cos15);
        True("a rectangular room has exactly two axes", axes.Count == 2);

        // Most wall first: 2 x 3700 of north-south-facing wall beats 2 x 1450 the other way.
        Near("the axis carrying most wall comes first", DimensionMath.BearingOf(axes[0]), 90, 1e-9);
        Near("...and the shorter one second", DimensionMath.BearingOf(axes[1]), 0, 1e-9);

        // THE CASE THAT USED TO VANISH. A splayed corner puts faces on a third axis, 45
        // degrees off both of the old runs and therefore square to neither.
        var splayed = new List<(Vec2, double)>(rectangle)
        {
            (new Vec2(-Math.Sqrt(0.5), -Math.Sqrt(0.5)), 1200),
        };

        var splayedAxes = DimensionMath.WallAxes(splayed, cos15);
        True("a splayed corner adds a third axis", splayedAxes.Count == 3);
        True("...at 45 degrees",
            splayedAxes.Any(a => Math.Abs(DimensionMath.BearingOf(a) - 45) < 1e-9));

        // Walls a fraction of a degree apart are ONE axis. Two would put near-identical
        // strings on top of each other, each holding faces the other should have had.
        var wobbly = new List<(Vec2, double)>
        {
            (new Vec2(0, 1), 3000),
            (new Vec2(Math.Sin(0.5 * Math.PI / 180), Math.Cos(0.5 * Math.PI / 180)), 3000),
        };

        True("walls half a degree apart are one axis",
            DimensionMath.WallAxes(wobbly, cos15).Count == 1);

        // Twenty degrees apart is a real angled partition, and gets its own run.
        var angled = new List<(Vec2, double)>
        {
            (new Vec2(0, 1), 3000),
            (new Vec2(Math.Sin(0.349), Math.Cos(0.349)), 3000),
        };

        True("walls twenty degrees apart are two axes",
            DimensionMath.WallAxes(angled, cos15).Count == 2);

        // A wall and the wall opposite it name the same run, so a room does not get one
        // string per FACE.
        Near("north and south name the same run",
            DimensionMath.BearingOf(new Vec2(0, 1)),
            DimensionMath.BearingOf(new Vec2(0, -1)), 1e-9);

        // A run on an arbitrary axis obeys the same rules as the two hardcoded ones did: it
        // hugs the far wall along its OWN perpendicular, offset back into the room.
        var diagonal = DimensionMath.Canonical(new Vec2(1, 1));
        var run = DimensionMath.Run(diagonal, alongMin: 0, alongMax: 2000,
            perpendicularMax: 5000, offset: Offset);

        var perpendicular = run.Inward.Negated();
        var lineAlongInward = perpendicular.Scaled(run.Across).Dot(run.Inward);

        Near("a 45 degree run stands 150 mm off the wall it hugs",
            lineAlongInward - run.WallAlongInward, Offset, 1e-9);

    }


    /// <summary>
    /// The core rule: EVERY wall face carries a dimension.
    ///
    /// Vaer. 1's six faces, measured out of the live model (millimetres, relative to the
    /// room's bottom-left inner corner). Gang is cut out of the top-right, so the room is
    /// L-shaped and the partition between them is 100 mm thick, x 1800 to 1900:
    ///
    ///   left exterior     x = 0,    y 0    - 2950     -> 2950
    ///   bottom            y = 0,    x 0    - 3700     -> 3700
    ///   right, lower leg  x = 3700, y 0    - 1000     -> 1000
    ///   lower-leg ceiling y = 1000, x 1800 - 3700     -> 1900
    ///   Gang partition    x = 1800, y 1000 - 2950     -> 1950
    ///   top wall          y = 2950, x 0    - 1800     -> 1800
    ///
    /// Those six lengths are exactly the six numbers on the reference drawing - INCLUDING the
    /// 1900 and 1950 that were wrong as clear dimensions. A clear dimension crosses the room
    /// and both of those cross a partition; as the lengths of their own faces they are right.
    /// </summary>
    private static void EveryFaceMeasured()
    {
        Section("every wall face carries a dimension (Vaer. 1)");

        const double Match = 25;

        // Faces square to the east-west axis are its corners; faces running along it are what
        // an east-west dimension measures. Index numbers match the table above.
        var eastWestEnds = new List<DimensionMath.FaceEnd>
        {
            new(0, At: 0),        // left exterior
            new(2, At: 3700),     // right, lower leg
            new(4, At: 1800),     // Gang partition
        };

        var eastWestRuns = new List<DimensionMath.FaceRun>
        {
            new(1, SpanMin: 0,    SpanMax: 3700, Offset: 0,    InwardSign: 1),   // bottom
            new(3, SpanMin: 1800, SpanMax: 3700, Offset: 1000, InwardSign: -1),  // lower ceiling
            new(5, SpanMin: 0,    SpanMax: 1800, Offset: 2950, InwardSign: -1),  // top wall
        };

        var eastWest = DimensionMath.CornerToCorner(eastWestRuns, eastWestEnds, Match)
            .Select(c => Math.Round(c.Clear)).ToList();

        True("the bottom wall reads 3700", eastWest.Contains(3700));
        True("the lower-leg ceiling reads 1900", eastWest.Contains(1900));
        True("the top wall reads 1800", eastWest.Contains(1800));
        True("every east-west face got one", eastWest.Count == 3);

        var northSouthEnds = new List<DimensionMath.FaceEnd>
        {
            new(1, At: 0),        // bottom
            new(3, At: 1000),     // lower-leg ceiling
            new(5, At: 2950),     // top wall
        };

        var northSouthRuns = new List<DimensionMath.FaceRun>
        {
            new(0, SpanMin: 0,    SpanMax: 2950, Offset: 0,     InwardSign: 1),   // left exterior
            new(2, SpanMin: 0,    SpanMax: 1000, Offset: -3700, InwardSign: 1),   // right, lower leg
            new(4, SpanMin: 1000, SpanMax: 2950, Offset: -1800, InwardSign: 1),   // partition
        };

        var northSouth = DimensionMath.CornerToCorner(northSouthRuns, northSouthEnds, Match)
            .Select(c => Math.Round(c.Clear)).ToList();

        True("the left exterior reads 2950", northSouth.Contains(2950));
        True("the lower leg reads 1000", northSouth.Contains(1000));
        True("the Gang partition reads 1950", northSouth.Contains(1950));
        True("every north-south face got one", northSouth.Count == 3);

        // SIX FACES, SIX DIMENSIONS. The core rule, stated as a count.
        True("all six faces of Vaer. 1 are measured", eastWest.Count + northSouth.Count == 6);

        // A rectangle's opposite faces measure the same thing between the same two corners.
        // Drawing both would print 3700 twice on Entre; the reference prints it once.
        var entreEnds = new List<DimensionMath.FaceEnd> { new(0, At: 0), new(1, At: 3700) };
        var entreRuns = new List<DimensionMath.FaceRun>
        {
            new(2, SpanMin: 0, SpanMax: 3700, Offset: 0,    InwardSign: 1),    // bottom
            new(3, SpanMin: 0, SpanMax: 3700, Offset: 1450, InwardSign: -1),   // top
        };

        var entre = DimensionMath.CornerToCorner(entreRuns, entreEnds, Match);

        True("Entre gets 3700 once, not twice", entre.Count == 1);
        Near("...and it reads 3700", entre[0].Clear, 3700, Tol);
        Near("...drawn against the top wall, as the reference has it", entre[0].FaceOffset, 1450, Tol);

        // A face whose end lands on no corner is not silently dropped to zero - it produces
        // nothing, and the generator reports it. Better a missing dimension that is named than
        // a wrong one that is not.
        var orphanEnds = new List<DimensionMath.FaceEnd> { new(0, At: 0) };
        var orphanRuns = new List<DimensionMath.FaceRun>
        {
            new(1, SpanMin: 0, SpanMax: 2400, Offset: 0, InwardSign: 1),
        };

        True("a face with only one corner produces nothing",
            DimensionMath.CornerToCorner(orphanRuns, orphanEnds, Match).Count == 0);

        // Corners are matched within a tolerance, because finish faces in a real model meet
        // within a few millimetres rather than exactly.
        var slack = new List<DimensionMath.FaceEnd> { new(0, At: -3), new(1, At: 3702) };
        var slackRun = new List<DimensionMath.FaceRun>
        {
            new(2, SpanMin: 0, SpanMax: 3700, Offset: 0, InwardSign: 1),
        };

        True("corners a few millimetres out still match",
            DimensionMath.CornerToCorner(slackRun, slack, Match).Count == 1);
    }

    private static void RotatedWing()
    {
        Section("rotated wing");

        // The same Entre, turned 30 degrees. World axes would dimension it diagonally.
        const double Angle = 30 * Math.PI / 180;

        var u = DimensionMath.Canonical(new Vec2(Math.Cos(Angle), Math.Sin(Angle)));
        var v = DimensionMath.LeftOf(u);

        var runs = new List<(Vec2, double)>
        {
            (u, Width), (u, Width),
            (v, Depth), (v, Depth),
        };

        var axis = DimensionMath.PrincipalAxis(runs);
        Near("the axis follows the wing, not the world (x)", axis.X, Math.Cos(Angle), 1e-9);
        Near("the axis follows the wing, not the world (y)", axis.Y, Math.Sin(Angle), 1e-9);

        // Face midpoints, rotated with it.
        var points = FaceMidpoints(Width, Depth)
            .Select(p => new Vec2(
                (p.X * Math.Cos(Angle)) - (p.Y * Math.Sin(Angle)),
                (p.X * Math.Sin(Angle)) + (p.Y * Math.Cos(Angle))))
            .ToList();

        var extent = DimensionMath.Measure(points, u, v);

        Near("a rotated room still measures 3700", extent.AlongU, Width, 1e-9);
        Near("a rotated room still measures 1450", extent.AlongV, Depth, 1e-9);
    }

    private static void Chaining()
    {
        Section("coincident faces and squareness");

        // Square-to test: only the ends of a run have a position on it.
        var cosine = Math.Cos(15 * Math.PI / 180);
        var run = new Vec2(1, 0);

        True("the near end is square to the run", DimensionMath.IsSquareTo(new Vec2(1, 0), run, cosine));
        True("the far end is square to the run too", DimensionMath.IsSquareTo(new Vec2(-1, 0), run, cosine));
        True("a side wall is not", !DimensionMath.IsSquareTo(new Vec2(0, 1), run, cosine));
        True("10 degrees off still counts",
            DimensionMath.IsSquareTo(new Vec2(Math.Cos(0.175), Math.Sin(0.175)), run, cosine));
        True("20 degrees off does not",
            !DimensionMath.IsSquareTo(new Vec2(Math.Cos(0.349), Math.Sin(0.349)), run, cosine));
    }

    private static void TextFitting()
    {
        Section("text fitting");

        // '_EM white 1.5mm' at 1:50. Four digits: 4 x 1.5 x 50 x 0.6 = 180 mm of model.
        var at50 = DimensionMath.TextWidth(4, textHeightOnPaper: 1.5, widthFactor: 1.0, viewScale: 50);
        Near("'1950' is 180 mm of model at 1:50", at50, 180, Tol);

        var gap50 = 1.0 * 50;
        True("1950 fits between its ticks at 1:50", DimensionMath.FitsBetweenTicks(1950, at50, gap50));
        True("1000 fits too", DimensionMath.FitsBetweenTicks(1000, at50, gap50));
        True("a 200 mm segment does not", !DimensionMath.FitsBetweenTicks(200, at50, gap50));

        // The same room at 1:200. Scale is the whole point: nothing about the model changed.
        var at200 = DimensionMath.TextWidth(4, 1.5, 1.0, 200);
        var gap200 = 1.0 * 200;
        Near("the same text is 720 mm at 1:200", at200, 720, Tol);
        True("1000 no longer fits at 1:200", !DimensionMath.FitsBetweenTicks(1000, at200, gap200));
        True("1950 still fits at 1:200", DimensionMath.FitsBetweenTicks(1950, at200, gap200));

        // Bands alternate above and below. All one way and a row of cramped segments stacks
        // into a single column, which is the collision this is supposed to prevent.
        Near("band 0 does not move", DimensionMath.BandOffset(0, 100), 0, Tol);
        Near("band 1 goes up one step", DimensionMath.BandOffset(1, 100), 100, Tol);
        Near("band 2 goes down one step", DimensionMath.BandOffset(2, 100), -100, Tol);
        Near("band 3 goes up two steps", DimensionMath.BandOffset(3, 100), 200, Tol);
        Near("band 4 goes down two steps", DimensionMath.BandOffset(4, 100), -200, Tol);
    }

    private static void Collision()
    {
        Section("text collision");

        var east = new Vec2(1, 0);
        var north = new Vec2(0, 1);

        var flat = DimensionMath.RotatedBox(new Vec2(0, 0), east, north, east, north, 180, 75);
        Near("a horizontal text is 180 wide", flat.MaxA - flat.MinA, 180, Tol);
        Near("...and 75 tall", flat.MaxB - flat.MinB, 75, Tol);

        // The same text on a vertical dimension swaps its extents. This is what makes the
        // 1450 down the left of Entre collide with anything the 3700 across the top does not.
        var upright = DimensionMath.RotatedBox(new Vec2(0, 0), north, east.Negated(), east, north, 180, 75);
        Near("a vertical text is 75 wide", upright.MaxA - upright.MinA, 75, 1e-9);
        Near("...and 180 tall", upright.MaxB - upright.MinB, 180, 1e-9);

        var apart = DimensionMath.RotatedBox(new Vec2(1000, 0), east, north, east, north, 180, 75);
        True("boxes 1000 apart do not overlap", !flat.Overlaps(apart));

        var touching = DimensionMath.RotatedBox(new Vec2(180, 0), east, north, east, north, 180, 75);
        True("boxes that exactly touch do not overlap", !flat.Overlaps(touching));

        var overlapping = DimensionMath.RotatedBox(new Vec2(90, 0), east, north, east, north, 180, 75);
        True("boxes half on top of each other do overlap", flat.Overlaps(overlapping));

        // Band search.
        var clear = new List<DimensionMath.Box>();
        Near("nothing in the way means stay put",
            DimensionMath.FirstClearBand(new Vec2(0, 0), east, north, slide: east, east, north,
                180, 75, step: 100, mustMove: false, clear, maximumBands: 6),
            0, Tol);

        Near("too narrow means move even with nothing in the way",
            DimensionMath.FirstClearBand(new Vec2(0, 0), east, north, slide: east, east, north,
                180, 75, step: 100, mustMove: true, clear, maximumBands: 6),
            1, Tol);

        var blocked = new List<DimensionMath.Box> { flat };
        var band = DimensionMath.FirstClearBand(new Vec2(0, 0), east, north, slide: east, east, north,
            180, 75, step: 100, mustMove: false, blocked, maximumBands: 6);

        True("standing on an occupied box forces a move", band >= 1);

        // Boxed in at every reachable band: give up rather than wander off. The obstacles are
        // stacked ALONG the slide direction, because that is the only direction a colliding
        // text now travels - it never moves away from its wall.
        var walled = new List<DimensionMath.Box>();
        for (var i = -6; i <= 6; i++)
            walled.Add(DimensionMath.RotatedBox(new Vec2(i * 100, 0), east, north, east, north, 180, 75));

        Near("nowhere clear returns -1",
            DimensionMath.FirstClearBand(new Vec2(0, 0), east, north, slide: east, east, north,
                180, 75, step: 100, mustMove: false, walled, maximumBands: 6),
            -1, Tol);
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>
    /// A placement's dimension line, as a coordinate on a world axis.
    ///
    /// Placement.Across lives on the run's own perpendicular, which is Inward reversed. Taking
    /// that vector, scaling it by the stored coordinate and projecting onto the world axis is
    /// what turns a run-local number into something comparable with the room's extent.
    /// </summary>
    private static double WorldCoordinate(DimensionMath.Placement placement, Vec2 axis) =>
        placement.Inward.Negated().Scaled(placement.Across).Dot(axis);

    /// <summary>A rectangular room's boundary loop, in the winding asked for.</summary>
    private static List<(Vec2 A, Vec2 B)> Rectangle(
        double x, double y, double width, double depth, bool clockwise)
    {
        var corners = new List<Vec2>
        {
            new(x, y),
            new(x + width, y),
            new(x + width, y + depth),
            new(x, y + depth),
        };

        if (clockwise) corners.Reverse();

        var loop = new List<(Vec2, Vec2)>();
        for (var i = 0; i < corners.Count; i++)
            loop.Add((corners[i], corners[(i + 1) % corners.Count]));

        return loop;
    }

    /// <summary>
    /// The four face midpoints a rectangular room produces - which is what the extent is
    /// measured from, not the corners. The midpoint of the top wall carries the top FACE's
    /// position exactly, which is why this is the right input and a bounding box is not.
    /// </summary>
    private static List<Vec2> FaceMidpoints(double width, double depth) =>
    [
        new(width / 2, 0),
        new(width, depth / 2),
        new(width / 2, depth),
        new(0, depth / 2),
    ];

    // ---------------------------------------------------------------- harness

    private static void Section(string title) => Console.WriteLine($"  {title}");

    private static void Near(string what, double actual, double expected, double tolerance)
    {
        _run++;

        if (Math.Abs(actual - expected) <= tolerance)
        {
            Console.WriteLine($"    ok   {what}");
            return;
        }

        _failed++;
        Console.WriteLine($"    FAIL {what}: expected {expected}, got {actual}");
    }

    private static void True(string what, bool condition)
    {
        _run++;

        if (condition)
        {
            Console.WriteLine($"    ok   {what}");
            return;
        }

        _failed++;
        Console.WriteLine($"    FAIL {what}");
    }
}
