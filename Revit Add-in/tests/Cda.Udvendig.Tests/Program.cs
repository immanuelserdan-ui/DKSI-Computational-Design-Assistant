using Cda.Revit.Addin.Doors;

namespace Cda.Udvendig.Tests;

/// <summary>
/// Runnable checks over <see cref="UdvendigClassification"/> - the rules that decide which
/// doors leave the interior schedules.
///
/// WHERE THE NAMES COME FROM
///   The six doors in FM_Template 2027V1.00_EN, read out of the live model rather than
///   invented, because the one case that matters most was not one anybody would have thought
///   to make up:
///
///     747  Exterior Door V22   Entre        -> Udvendig      exterior
///     748  Interior Door V17   Gang         -> Entre         interior
///     749  Interior Door V17   Gang         -> Vaer. 1       interior
///     750  Interior Door V17   Gang         -> Bad           interior
///     751  Exterior Door V22   Bad          -> Udvendig      exterior
///     752  Exterior Door V22   Kaelderrum 4 -> Udestue       INTERIOR
///
///   752 is the trap. It is an Exterior Door family, so anything keying off the family name
///   calls it exterior - but it runs between two real rooms, its FROM and TO differ, and it
///   belongs in the interior schedules. 'Udestue' also shares three letters with 'Udvendig',
///   so a Contains match rather than a StartsWith would throw its casing, frame and lining
///   quantities out of the takeoff.
///
/// WHAT THESE ARE GUARDING AGAINST
///   Not crashes. Every failure here produces a schedule that looks finished and totals the
///   wrong number: an exterior door left in the interior schedules is billed to one room TWICE
///   (its FROM and TO name the same room once the resolver has substituted), and an interior
///   door wrongly marked exterior disappears from them entirely. Neither throws.
/// </summary>
internal static class Program
{
    private static int _run;
    private static int _failed;

    /// <summary>The shipped default - see UdvendigSettings.ExteriorPrefix.</summary>
    private const string Prefix = "Udvendig";

    private static int Main()
    {
        Console.WriteLine("Udvendig exterior/interior classification\n");

        ExteriorRoomNames();
        TheRealModelsRooms();
        EitherSide();
        FlagWrites();
        OverrideIsHandsOff();

        Console.WriteLine($"\n{_run - _failed}/{_run} passed.");
        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ the prefix

    private static void ExteriorRoomNames()
    {
        Section("A room name marks the outside");

        Check("'Udvendig' is exterior", IsExt("Udvendig"));
        Check("'Udvendig 1' is exterior", IsExt("Udvendig 1"));
        Check("'Udvendig 3' is exterior", IsExt("Udvendig 3"));

        // Case and whitespace: room names are typed by people.
        Check("'udvendig' is exterior (case)", IsExt("udvendig"));
        Check("'UDVENDIG' is exterior (case)", IsExt("UDVENDIG"));
        Check("' Udvendig ' is exterior (trimmed)", IsExt("  Udvendig  "));

        // Absence is not exterior.
        Check("an empty name is not exterior", !IsExt(""));
        Check("whitespace is not exterior", !IsExt("   "));
        Check("null is not exterior", !IsExt(null));

        // A prefix nobody configured must not match everything.
        Check("an empty prefix matches nothing",
            !UdvendigClassification.IsExteriorRoomName("Udvendig", string.Empty));
    }

    private static void TheRealModelsRooms()
    {
        Section("The rooms actually in FM_Template 2027V1.00_EN");

        Check("'Entre' is interior", !IsExt("Entre"));
        Check("'Gang' is interior", !IsExt("Gang"));
        Check("'Bad' is interior", !IsExt("Bad"));
        Check("'Vaer. 1' is interior", !IsExt("Vaer. 1"));
        Check("'Kaelderrum 4' is interior", !IsExt("Kaelderrum 4"));

        // THE ONE THAT MATTERS. Door 752's TO room. Shares 'Ud' with the prefix and is a real
        // enclosed room a person stands in; a Contains or a two-letter prefix would lose it.
        Check("'Udestue' is INTERIOR - it is a conservatory, not the outside", !IsExt("Udestue"));

        // The near-misses either side of it, so the boundary is pinned rather than incidental.
        Check("'Ude' alone is interior", !IsExt("Ude"));
        Check("'Udv' alone is interior", !IsExt("Udv"));
        Check("'Udvending' (typo) is interior", !IsExt("Udvending"));
    }

    // ------------------------------------------------------------------ either side

    private static void EitherSide()
    {
        Section("A door is exterior when either side faces out");

        Check("neither side out -> interior",
            !UdvendigClassification.IsExteriorDoor(false, false));
        Check("TO side out -> exterior (doors 747, 751)",
            UdvendigClassification.IsExteriorDoor(false, true));
        Check("FROM side out -> exterior",
            UdvendigClassification.IsExteriorDoor(true, false));

        // No interior side at all - even less business in an interior schedule.
        Check("both sides out -> exterior",
            UdvendigClassification.IsExteriorDoor(true, true));
    }

    // ------------------------------------------------------------------ the flag

    private static void FlagWrites()
    {
        Section("When the flag needs writing");

        const int no = 0;
        const int yes = 1;
        var absent = UdvendigClassification.FlagAbsent;

        Check("exterior door reading No needs a write",
            UdvendigClassification.NeedsFlagWrite(isExterior: true, no, overridden: false));

        Check("exterior door already reading Yes does not (idempotent)",
            !UdvendigClassification.NeedsFlagWrite(isExterior: true, yes, overridden: false));

        Check("interior door already reading No does not (idempotent)",
            !UdvendigClassification.NeedsFlagWrite(isExterior: false, no, overridden: false));

        // A door that STOPPED being exterior. Leaving the stale Yes would keep it out of the
        // interior schedules forever, which is the silent half of this whole feature.
        Check("interior door reading a stale Yes needs clearing",
            UdvendigClassification.NeedsFlagWrite(isExterior: false, yes, overridden: false));

        // Nowhere to write it. Must never be reported as work owed, or every door in a model
        // without the parameter shows as needing a change on every single pass.
        Check("absent flag is never a write (exterior)",
            !UdvendigClassification.NeedsFlagWrite(isExterior: true, absent, overridden: false));
        Check("absent flag is never a write (interior)",
            !UdvendigClassification.NeedsFlagWrite(isExterior: false, absent, overridden: false));

        Check("absent is distinct from No", absent != no);
    }

    private static void OverrideIsHandsOff()
    {
        Section("The Override takes the door out of the tool's hands");

        const int no = 0;
        const int yes = 1;

        // Both directions: an override must stop the write whichever way the disagreement runs,
        // or it is not an override at all.
        Check("overridden exterior door reading No is left alone",
            !UdvendigClassification.NeedsFlagWrite(isExterior: true, no, overridden: true));

        Check("overridden interior door reading Yes is left alone",
            !UdvendigClassification.NeedsFlagWrite(isExterior: false, yes, overridden: true));

        Check("overridden door that already agrees is still left alone",
            !UdvendigClassification.NeedsFlagWrite(isExterior: true, yes, overridden: true));

        SubstitutedSide();
    }

    /// <summary>
    /// The office rule for the 'Ext Door * @V05' set: where the Rum field would say 'Udvendig',
    /// show the room on the other side instead. Cases named after the doors in FM_Template that
    /// each one is drawn from, so a failure points at something real.
    /// </summary>
    private static void SubstitutedSide()
    {
        Section("The Ext Door schedules never show 'Udvendig'");

        (string Num, string Name) Sub(bool extFrom, bool extTo,
                                      string fn, string fname, string tn, string tname) =>
            UdvendigClassification.SubstitutedSide(extFrom, extTo, fn, fname, tn, tname);

        // Door 751 measured 2026-09-02: FROM 99/Udvendig, TO 46/Bad. The whole point.
        Check("FROM is Udvendig -> the other side (door 751: 99/Udvendig -> 46/Bad)",
            Sub(true, false, "99", "Udvendig", "46", "Bad") == ("46", "Bad"));

        // Door 747 in the same run: TO is the exterior side, so FROM is already the room the
        // schedule wants and must NOT be swapped for the outdoors.
        Check("TO is Udvendig -> FROM stays put (door 747: 6/Entre)",
            Sub(false, true, "6", "Entre", "99", "Udvendig") == ("6", "Entre"));

        // Doors 748-750, 752: nothing exterior, nothing to do.
        Check("neither side exterior -> FROM unchanged",
            Sub(false, false, "7", "Gang", "10", "Vaer. 1") == ("7", "Gang"));

        // Reported as a warning by the resolver, and deliberately NOT substituted: swapping one
        // exterior side for the other would print 'Udvendig' while claiming it was replaced.
        Check("both sides exterior -> left alone rather than faked",
            Sub(true, true, "99", "Udvendig", "99", "Udvendig") == ("99", "Udvendig"));

        // Erasing the only room a door has is worse than showing 'Udvendig'.
        Check("no counterpart -> FROM kept, not blanked",
            Sub(true, false, "99", "Udvendig", "", "") == ("99", "Udvendig"));

        Check("counterpart with a number but no name still substitutes",
            Sub(true, false, "99", "Udvendig", "46", "") == ("46", ""));

        // The rule is idempotent by construction: run it on its own output and nothing moves,
        // which is what makes it safe to rewrite on every door change.
        var once = Sub(true, false, "99", "Udvendig", "46", "Bad");
        Check("idempotent - substituting an already-substituted value changes nothing",
            Sub(false, false, once.Num, once.Name, "46", "Bad") == once);
    }

    // ------------------------------------------------------------------ harness

    private static bool IsExt(string? name) =>
        UdvendigClassification.IsExteriorRoomName(name, Prefix);

    private static void Section(string title) => Console.WriteLine($"\n{title}");

    private static void Check(string what, bool passed)
    {
        _run++;

        if (passed)
        {
            Console.WriteLine($"    ok   {what}");
            return;
        }

        _failed++;
        Console.WriteLine($"    FAIL {what}");
    }
}
