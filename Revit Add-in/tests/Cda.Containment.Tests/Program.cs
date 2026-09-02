using Cda.Revit.Addin.Rooms;

namespace Cda.Containment.Tests;

/// <summary>
/// Runnable checks over <see cref="ClipRules"/> - every containment rule in the skirting
/// tool.
///
/// WHY THE NUMBERS ARE WHAT THEY ARE
///   The shipped settings, converted to millimetres so a wrong assertion is obvious on sight:
///
///     BoardDepth      0.066 ft = 20 mm
///     MaxMitreFactor  6, so a corner is forgiven 120 mm of apparent escape
///     MinimumRun      0.05 ft = 15 mm
///
///   Everything here is unit-agnostic, so working in millimetres rather than Revit's decimal
///   feet costs nothing.
///
/// WHAT THESE ARE GUARDING AGAINST
///   Not crashes. Every failure this file can catch produces a model that looks finished and
///   is wrong on site: a board driven through a partition into the next room, a corner opened
///   up because the board that fills it was clipped away, a 6 mm sliver placed in a doorway
///   and scheduled as a board. None of those throw.
///
///   The regression that prompted the file is FAIL-OPEN: containment used to return the board
///   unchanged whenever it could not decide, so a board crossing a boundary and a board that
///   passed the test were the same event. Nothing here may quietly widen a board.
/// </summary>
internal static class Program
{
    private static int _run;
    private static int _failed;

    /// <summary>A corner's worth of forgiveness: BoardDepth x MaxMitreFactor, in mm.</summary>
    private const double Corner = 120;

    /// <summary>Below this a surviving span is a sliver, not a board. In mm.</summary>
    private const double Minimum = 15;

    /// <summary>A partition-to-partition run off the reference plan.</summary>
    private const double Board = 3700;

    private const double Tol = 1e-9;

    private static int Main()
    {
        Console.WriteLine("Skirting containment rules\n");

        WhollyInside();
        NoCorner();
        AtACorner();
        EndsAreIndependent();
        Slivers();
        NothingInside();
        OrderOfOperations();
        Nonsense();
        NeverWidens();

        Console.WriteLine($"\n{_run - _failed}/{_run} passed.");

        if (_failed > 0) Console.WriteLine($"{_failed} FAILED.");

        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------- the rules

    private static void WhollyInside()
    {
        Section("A board the room agrees with is built as drawn");

        var whole = ClipRules.ForSpan(Board, 0, Board, 0, 0, Minimum);

        Is("full span keeps the whole board", whole.Action, ClipAction.KeepWhole);
        Near("and reports its full length", whole.Length, Board, Tol);
    }

    private static void NoCorner()
    {
        Section("Away from a corner, the room's end is the board's end");

        // A board running 200 mm past its room at the start - through a partition, into the
        // next room. Nothing to forgive here: neither end is a corner.
        var trimmed = ClipRules.ForSpan(Board, 200, Board, 0, 0, Minimum);

        Is("a board past its room is trimmed", trimmed.Action, ClipAction.Trim);
        Near("cut starts where the room starts", trimmed.From, 200, Tol);
        Near("and runs to the far end", trimmed.To, Board, Tol);
        Near("so it loses exactly the escape", trimmed.Length, Board - 200, Tol);
    }

    private static void AtACorner()
    {
        Section("At a genuine corner the board fills what the room does not");

        // 80 mm of apparent escape at a corner. The room is narrower than the board there
        // because the boundary is a line and the board has thickness - clipping this is what
        // opened the corner gaps.
        var forgiven = ClipRules.ForSpan(Board, 80, Board, Corner, 0, Minimum);

        Is("escape within a corner's reach is forgiven", forgiven.Action, ClipAction.KeepWhole);
        Near("the board keeps its full length", forgiven.Length, Board, Tol);

        // Exactly at the allowance: forgiven, because the test is <=.
        var exact = ClipRules.ForSpan(Board, Corner, Board, Corner, 0, Minimum);
        Is("escape of exactly a corner's reach is forgiven", exact.Action, ClipAction.KeepWhole);

        // Past it: a corner cannot excuse 200 mm, and this is the guard that stops the
        // allowance becoming a licence to cross a boundary.
        var beyond = ClipRules.ForSpan(Board, 200, Board, Corner, 0, Minimum);
        Is("escape beyond a corner's reach is still cut", beyond.Action, ClipAction.Trim);
        Near("and cut to the room, not to the allowance", beyond.From, 200, Tol);
    }

    private static void EndsAreIndependent()
    {
        Section("A corner at one end forgives nothing at the other");

        // The case that matters on a real wall: the board meets a mitred corner at its start
        // and a door jamb at its end. The corner end is forgiven, the jamb end must stay
        // tight or the board runs into the doorway.
        var mixed = ClipRules.ForSpan(Board, 80, Board - 200, Corner, 0, Minimum);

        Is("mixed ends trim", mixed.Action, ClipAction.Trim);
        Near("corner end forgiven to the board's start", mixed.From, 0, Tol);
        Near("jamb end held at the room", mixed.To, Board - 200, Tol);

        // And the mirror image, so the two allowances cannot be swapped without a failure.
        var mirrored = ClipRules.ForSpan(Board, 200, Board - 80, 0, Corner, Minimum);

        Is("mirrored ends trim", mirrored.Action, ClipAction.Trim);
        Near("jamb end held at the room", mirrored.From, 200, Tol);
        Near("corner end forgiven to the board's end", mirrored.To, Board, Tol);
    }

    private static void Slivers()
    {
        Section("What survives must be a board, not a sliver");

        // 10 mm of room left between two obstructions. Placing this schedules a board nobody
        // will ever cut.
        var sliver = ClipRules.ForSpan(Board, 490, 500, 0, 0, Minimum);
        Is("a span below the minimum run is refused", sliver.Action, ClipAction.Refuse);

        // Exactly the minimum survives: the test is <, not <=.
        var exact = ClipRules.ForSpan(Board, 490, 490 + Minimum, 0, 0, Minimum);
        Is("a span of exactly the minimum run survives", exact.Action, ClipAction.Trim);
        Near("at its measured length", exact.Length, Minimum, Tol);
    }

    private static void NothingInside()
    {
        Section("A board with no inside span at all");

        // A 40 mm return in the throat of a sharp corner: the room there is narrower than the
        // probe, so nothing reads as inside, and the board is still real.
        var pinned = ClipRules.ForNothingInside(40, Corner, Corner);
        Is("a short piece between two corners is kept", pinned.Action, ClipAction.KeepWhole);
        Near("whole", pinned.Length, 40, Tol);

        // A full-length board that measured nothing inside is NOT a corner case. This is the
        // fail-open regression: it must never come back as a whole board.
        var outside = ClipRules.ForNothingInside(Board, Corner, Corner);
        Is("a full board with nothing inside is refused", outside.Action, ClipAction.Refuse);

        // Exactly the combined allowance is still all corner.
        var edge = ClipRules.ForNothingInside(Corner * 2, Corner, Corner);
        Is("a piece exactly two corners long is kept", edge.Action, ClipAction.KeepWhole);

        // One millimetre more is not.
        var over = ClipRules.ForNothingInside((Corner * 2) + 1, Corner, Corner);
        Is("a millimetre longer is refused", over.Action, ClipAction.Refuse);

        // With no corner at either end there is nothing to forgive.
        var bare = ClipRules.ForNothingInside(40, 0, 0);
        Is("with no corner, nothing inside means nothing built", bare.Action, ClipAction.Refuse);
    }

    private static void OrderOfOperations()
    {
        Section("Allowances are applied before the minimum run is judged");

        // 20 mm of measured room in a 100 mm piece, both ends corners. If the minimum run
        // were judged on the measured span this would be refused; the allowances extend it to
        // the full board first, and a whole board is not a sliver.
        var extended = ClipRules.ForSpan(100, 40, 60, Corner, Corner, Minimum);

        Is("allowances run first", extended.Action, ClipAction.KeepWhole);
        Near("so the whole piece survives", extended.Length, 100, Tol);
    }

    private static void Nonsense()
    {
        Section("An answer that is not an answer becomes a refusal");

        Is("a zero-length board is refused",
            ClipRules.ForSpan(0, 0, 0, 0, 0, Minimum).Action, ClipAction.Refuse);

        Is("an inverted span is refused",
            ClipRules.ForSpan(Board, 900, 400, 0, 0, Minimum).Action, ClipAction.Refuse);

        // A span reaching past the board is clamped rather than built long - see NeverWidens
        // for why that matters.
        var over = ClipRules.ForSpan(Board, 200, Board + 500, 0, 0, Minimum);
        Is("a span past the board's end is clamped and trimmed", over.Action, ClipAction.Trim);
        Near("to the board's own end", over.To, Board, Tol);
    }

    private static void NeverWidens()
    {
        Section("No rule may make a board longer than it was drawn");

        // The property that matters most, swept rather than argued: whatever the measured
        // span and whatever the allowances, the built length is never more than the board.
        // A rule that widens a board pushes it into the next room, which is the fault this
        // whole class exists to prevent.
        double[] froms = [-500, -1, 0, 1, 80, 200, 1800, Board - 1, Board, Board + 500];
        double[] tos = [-1, 0, 1, 200, 1800, Board - 1, Board, Board + 500];
        double[] allowances = [0, 1, Minimum, Corner, Board];

        var widened = 0;
        var checkedCount = 0;

        foreach (var from in froms)
        foreach (var to in tos)
        foreach (var start in allowances)
        foreach (var end in allowances)
        {
            var decision = ClipRules.ForSpan(Board, from, to, start, end, Minimum);
            checkedCount++;

            if (decision.Action == ClipAction.Refuse) continue;

            var built = decision.Action == ClipAction.KeepWhole ? Board : decision.Length;

            if (built > Board + Tol) widened++;

            // A trim must also stay on the board: a span outside [0, length] is a curve
            // built somewhere the board never was.
            if (decision.Action == ClipAction.Trim &&
                (decision.From < -Tol || decision.To > Board + Tol)) widened++;
        }

        True($"no rule widened a board across {checkedCount} combinations", widened == 0);
    }

    // ------------------------------------------------------------------- harness

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

    private static void Is(string what, ClipAction actual, ClipAction expected)
    {
        _run++;

        if (actual == expected)
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
