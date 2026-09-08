using Cda.Revit.Addin.Heating;

namespace Cda.Heating.Tests;

/// <summary>
/// Runnable checks over <see cref="Seating"/> - every sign convention in the radiator tool.
///
/// WHY THE NUMBERS ARE WHAT THEY ARE
///   The headline cases are not invented. They are the real measurements taken out of
///   634-0001-036-DDG-Heliosvaenget after the first two builds went wrong, in Revit's own
///   internal units (decimal feet):
///
///     wall 28306146   EM_Ext/Int - 290mm, y 47.027528 .. 47.978972
///     room  Kokken 2  starts at y 47.978972, so the room is on the HIGH-y side
///     correct panel   y 47.978972 .. 48.392811   (measured on 28386096, which is right)
///     wrong panel     y 46.613689 .. 47.027528   (measured on 28386090, which was outside)
///
///   The wrong panel is the correct one mirrored about the wall centreline, which is what a
///   bad flip looks like. A regression suite built on invented numbers would not have caught
///   it; these are the numbers that did.
/// </summary>
internal static class Program
{
    private static int _run;
    private static int _failed;

    // The DDG wall and the two panel positions, in feet.
    private const double WallLo = 47.027528095872206;
    private const double WallHi = 47.978971665426090;
    private const double GoodLo = 47.978971665426060;
    private const double GoodHi = 48.392810888641320;
    private const double BadLo = 46.613688872656990;
    private const double BadHi = 47.027528095872250;

    private const double Mm = 0.0032808398950131;   // one millimetre, in feet
    private const double Tol = 1e-9;

    private static int Main()
    {
        Console.WriteLine("Seating conventions\n");

        MeasuredFromTheModel();
        SideFromBoundary();
        FaceAndStandoffAgree();
        Obstruction();
        MirrorLever();
        Symmetry();

        Console.WriteLine($"\n{_run - _failed}/{_run} passed.");

        if (_failed > 0) Console.WriteLine($"{_failed} FAILED.");

        return _failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- the real cases

    private static void MeasuredFromTheModel()
    {
        Section("measured from the DDG model");

        // The room is on the high-y side, so the room-side face is the wall's high-y face.
        var side = 1.0;
        var face = Seating.FaceV(WallLo, WallHi, side);

        Near("room-side face is the wall's high-y face", face, WallHi);

        // The panel that is right sits flush on that face: zero standoff, not negative.
        var good = Seating.Standoff(GoodLo, GoodHi, face, side);
        Near("correct panel stands flush on the face", good, 0.0);
        True("correct panel is accepted", good >= -Tol);

        // The panel that was wrong is a whole wall thickness behind it.
        var bad = Seating.Standoff(BadLo, BadHi, face, side);
        True("mirrored panel is rejected", bad < -Tol);

        // Standoff is measured to the panel's NEAR edge, so a panel mirrored across the wall
        // is behind the room face by the wall thickness PLUS its own depth - its far edge is
        // what lands flush on the far face. The first version of this assertion said "one
        // wall thickness" and was wrong by exactly the depth of the panel; the code was right
        // and the expectation was sloppy about what mirrored means.
        var thickness = WallHi - WallLo;
        var depth = GoodHi - GoodLo;

        Near("mirrored panel is a wall thickness plus its own depth behind",
            bad, -(thickness + depth), 1e-12);

        Near("...which is 290 + 126 mm", -bad / Mm, 416.0, 0.5);
        Near("its far edge lands flush on the wall's far face", BadHi, WallLo, 1e-9);

        // THE REGRESSION THAT MATTERS. Build one accepted the mirrored panel because it
        // consulted FacingOrientation instead of measuring. Nothing in Seating can express
        // that mistake: the only input is where the solids ended up.
        True("the two panels cannot both be accepted", (good >= -Tol) != (bad >= -Tol));
    }

    private static void SideFromBoundary()
    {
        Section("which side the room is on");

        // Kokken 2's boundary lies on the wall's high-y face.
        Equal("boundary on the high face reads +1",
            Seating.SideFromBoundary(WallHi, WallLo, WallHi, 1e-6), 1.0);

        Equal("boundary on the low face reads -1",
            Seating.SideFromBoundary(WallLo, WallLo, WallHi, 1e-6), -1.0);

        // A boundary on the centreline is the location line, not a face. It must return null
        // so the caller drops the wall - the old code returned -1.0 here, and a defaulted
        // side is indistinguishable from a measured one.
        var centre = (WallLo + WallHi) / 2.0;
        Equal("boundary on the centreline reads null",
            Seating.SideFromBoundary(centre, WallLo, WallHi, 1e-6), null);

        Equal("just inside tolerance of the centreline still reads null",
            Seating.SideFromBoundary(centre + 5e-7, WallLo, WallHi, 1e-6), null);

        Equal("just outside tolerance commits to a side",
            Seating.SideFromBoundary(centre + 2e-6, WallLo, WallHi, 1e-6), 1.0);
    }

    private static void FaceAndStandoffAgree()
    {
        Section("face and standoff agree on both sides");

        // Same wall, room on the LOW-y side instead. A panel standing into that room occupies
        // v below the low face, and must read as flush.
        var side = -1.0;
        var face = Seating.FaceV(WallLo, WallHi, side);

        Near("room-side face is the wall's low-y face", face, WallLo);

        var depth = GoodHi - GoodLo;
        var panelLo = WallLo - depth;

        Near("panel standing into the low-side room is flush",
            Seating.Standoff(panelLo, WallLo, face, side), 0.0);

        True("a panel on the WRONG side of a low-side room is rejected",
            Seating.Standoff(GoodLo, GoodHi, face, side) < -Tol);

        // A panel held off the face by a standoff bracket reads as positive, not negative.
        Near("25 mm off the face reads +25 mm",
            Seating.Standoff(WallLo - depth - 25 * Mm, WallLo - 25 * Mm, face, side) / Mm,
            25.0, 0.001);
    }

    private static void Obstruction()
    {
        Section("what obstructs a panel");

        const double depth = 0.4138;              // the measured panel depth, feet
        const double reach = 100 * Mm;
        var face = Seating.FaceV(WallLo, WallHi, 1.0);

        // A base unit against the wall, 600 mm deep, standing in the room.
        var unit = Seating.OutFromFace(WallHi, WallHi + 600 * Mm, face, 1.0);
        True("a base unit against the wall obstructs",
            Seating.Obstructs(unit.Lo, unit.Hi, depth, reach));

        // A cupboard in the NEXT flat, on the far face of the same wall.
        var next = Seating.OutFromFace(WallLo - 600 * Mm, WallLo, face, 1.0);
        True("a cupboard on the far face does not obstruct",
            !Seating.Obstructs(next.Lo, next.Hi, depth, reach));

        // A console table standing clear of the wall, further out than the panel is deep.
        var clear = Seating.OutFromFace(
            WallHi + depth + 200 * Mm, WallHi + depth + 800 * Mm, face, 1.0);
        True("furniture standing clear of the panel does not obstruct",
            !Seating.Obstructs(clear.Lo, clear.Hi, depth, reach));

        // Something just grazing the reach allowance still counts - the boundary is inclusive
        // on purpose, because a blocker missed is a radiator through a cupboard.
        var grazing = Seating.OutFromFace(
            WallHi + depth + reach, WallHi + depth + reach + 1.0, face, 1.0);
        True("an obstruction exactly at the reach limit still counts",
            Seating.Obstructs(grazing.Lo, grazing.Hi, depth, reach));

        // OutFromFace must not swap Lo and Hi on the negative side.
        var low = Seating.OutFromFace(WallLo - 600 * Mm, WallLo, WallLo, -1.0);
        True("OutFromFace keeps Lo below Hi on the low side", low.Lo <= low.Hi);
        Near("near edge of a low-side blocker is zero", low.Lo, 0.0);
    }

    /// <summary>
    /// The claim the new mirror lever rests on: reflecting about the wall's CENTRE plane
    /// rescues a mirrored panel, and reflecting about the room-side face does not.
    ///
    /// Worth asserting rather than arguing, because "mirror it to the other side" sounds
    /// equally true of either plane and only one of them puts the panel against the wall.
    /// </summary>
    private static void MirrorLever()
    {
        Section("mirroring across the wall");

        var side = 1.0;
        var face = Seating.FaceV(WallLo, WallHi, side);
        var far = Seating.FarV(WallLo, WallHi, side);
        var centre = Seating.CentreV(WallLo, WallHi);

        Near("the far face is the wall's low-y face", far, WallLo);
        Near("the centre plane is halfway through the wall", centre, (WallLo + WallHi) / 2.0);

        // The real mirrored panel out of the model, reflected about the centre plane.
        var lo = Seating.MirrorThrough(BadHi, centre);
        var hi = Seating.MirrorThrough(BadLo, centre);

        Near("mirroring about the centre plane lands it flush on the room face",
            Seating.Standoff(lo, hi, face, side), 0.0, 1e-9);

        True("...and that panel is accepted",
            Seating.Standoff(lo, hi, face, side) >= -Tol);

        // The tempting alternative. It gets the SIDE right and the position wrong, which is
        // the failure that would look correct in plan and absurd in section.
        var faceLo = Seating.MirrorThrough(BadHi, face);
        var faceHi = Seating.MirrorThrough(BadLo, face);
        var floating = Seating.Standoff(faceLo, faceHi, face, side);

        True("mirroring about the room face also gets the side right", floating >= -Tol);

        Near("...but leaves it floating a full wall thickness off the wall",
            floating / Mm, (WallHi - WallLo) / Mm, 0.5);

        Near("...which is 290 mm out into the room", floating / Mm, 290.0, 0.5);

        // Mirroring is its own inverse. If the lever ever runs twice, it must return the panel
        // to where it started rather than walking it across the model.
        Near("mirroring twice is a no-op",
            Seating.MirrorThrough(Seating.MirrorThrough(BadLo, centre), centre), BadLo, 1e-9);
    }

    private static void Symmetry()
    {
        Section("the two sides are mirror images");

        // Whatever holds on one side must hold on the other with every sign flipped. This is
        // the property the FacingOrientation bug violated, and it is worth asserting directly
        // rather than trusting two hand-written branches to stay in step.
        var rng = new Random(20260815);

        for (var i = 0; i < 2000; i++)
        {
            var lo = rng.NextDouble() * 100 - 50;
            var thickness = rng.NextDouble() * 2 + 0.1;
            var hi = lo + thickness;
            var depth = rng.NextDouble() * 1 + 0.05;
            var offset = rng.NextDouble() * 2 - 1;

            var high = Seating.Standoff(hi + offset, hi + offset + depth,
                Seating.FaceV(lo, hi, 1.0), 1.0);

            var mirrored = Seating.Standoff(lo - offset - depth, lo - offset,
                Seating.FaceV(lo, hi, -1.0), -1.0);

            if (Math.Abs(high - mirrored) > 1e-9)
            {
                Fail($"standoff is not symmetric at i={i}: {high} vs {mirrored}");
                return;
            }
        }

        Pass("standoff is symmetric across 2000 random walls");

        for (var i = 0; i < 2000; i++)
        {
            var lo = rng.NextDouble() * 100 - 50;
            var hi = lo + rng.NextDouble() * 2 + 0.1;
            var v = lo + rng.NextDouble() * (hi - lo);

            var side = Seating.SideFromBoundary(v, lo, hi, 1e-6);
            if (side is null) continue;

            // The face chosen for a side must be the end of the wall the boundary was nearer.
            var face = Seating.FaceV(lo, hi, side.Value);
            var nearer = Math.Abs(v - hi) < Math.Abs(v - lo) ? hi : lo;

            if (Math.Abs(face - nearer) > 1e-9)
            {
                Fail($"face does not follow the boundary at i={i}");
                return;
            }
        }

        Pass("the chosen face always follows the boundary, across 2000 random walls");
    }

    // ------------------------------------------------------------------- harness

    private static void Section(string name) => Console.WriteLine($"  {name}");

    private static void Near(string what, double actual, double expected, double tolerance = 1e-9)
    {
        _run++;
        if (Math.Abs(actual - expected) <= tolerance) Pass(what);
        else Fail($"{what}: expected {expected}, got {actual}");
    }

    private static void Equal(string what, double? actual, double? expected)
    {
        _run++;
        if (Nullable.Equals(actual, expected)) Pass(what);
        else Fail($"{what}: expected {Show(expected)}, got {Show(actual)}");
    }

    private static void True(string what, bool condition)
    {
        _run++;
        if (condition) Pass(what);
        else Fail(what);
    }

    private static void Pass(string what) => Console.WriteLine($"    ok    {what}");

    private static void Fail(string what)
    {
        _failed++;
        Console.WriteLine($"    FAIL  {what}");
    }

    private static string Show(double? value) => value?.ToString() ?? "null";
}
