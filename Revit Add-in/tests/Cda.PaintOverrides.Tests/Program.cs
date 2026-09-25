using Cda.Revit.Addin.Finishes;

namespace Cda.PaintOverrides.Tests;

/// <summary>
/// Runnable checks over <see cref="PaintOverrideOwnership"/> - which takeoff carrier a paint
/// room override is allowed to relabel.
///
/// WHERE THE DATA COMES FROM
///   The two overrides recorded in FM_Template 2027V1.00_EN, read out of the add-in's own log
///   (every takeoff run on 2026-09-24 re-applied them, "2 row(s)"):
///
///     40 Kælderrum 4 -> 13 Loftrum   1.004 m²   IV_Mål - 100mm #29317994 · Face 0.2 R3 / V9F - DKSI
///     40 Kælderrum 4 -> 13 Loftrum   1.872 m²   IV_Mål - 100mm #29330864 / TDM - DKSI
///
///   The first section pins that these keep behaving exactly as they did - the fix must not
///   move a single row in the model the office already relies on.
///
/// WHAT THE REST GUARD AGAINST
///   The defect this replaced: an override matched on key alone, and the key is shared by every
///   room's piece of one slab or ceiling. Reassigning one room's piece relabelled every room's,
///   and "remove all" crashed on the duplicate key. Neither was visible as an error in the
///   schedule - the rows simply sat under the wrong room.
/// </summary>
internal static class Program
{
    private static int _run;
    private static int _failed;

    private sealed record Ov(string Key, string From, string FromName, string To, string ToName);

    /// <summary>A takeoff carrier as the schedule sees it.</summary>
    private sealed class Carrier(string key, string number, string name)
    {
        public string Key { get; } = key;
        public string Number { get; set; } = number;
        public string Name { get; set; } = name;
        public string NaturalNumber { get; } = number;
        public string NaturalName { get; } = name;
    }

    private const string Wall1 = "IV_Mål - 100mm #29317994 · Face 0.2 R3\u001FV9F - DKSI";
    private const string Wall2 = "IV_Mål - 100mm #29330864\u001FTDM - DKSI";
    private const string Slab = "Beton 200 #30000001\u001FMaling - DKSI";

    private static int Main()
    {
        Console.WriteLine("Paint room override ownership\n");

        TheRealModelIsUnchanged();
        SharedKeyAcrossRooms();
        ReapplyIsIdempotent();
        TwoOverridesOneKey();
        RenumberedRoom();
        Identity();
        AreaDrift();
        ApplyThenClearRoundTrip();

        Console.WriteLine($"\n{_run - _failed}/{_run} passed.");
        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ FM_Template

    private static void TheRealModelIsUnchanged()
    {
        Section("FM_Template's two recorded overrides behave exactly as before");

        Ov[] overrides =
        [
            new(Wall1, "40", "Kælderrum 4", "13", "Loftrum"),
            new(Wall2, "40", "Kælderrum 4", "13", "Loftrum"),
        ];

        // Fresh from a takeoff: both carriers are in 40, their natural room.
        var c1 = new Carrier(Wall1, "40", "Kælderrum 4");
        var c2 = new Carrier(Wall2, "40", "Kælderrum 4");

        var applied = Apply(overrides, [c1, c2]);

        Check("both rows re-pointed, as the log reports every run (\"2 row(s)\")", applied == 2);
        Check("row 1 now reports under 13 Loftrum (Reassigned)",
            c1.Number == "13" && c1.Name == "Loftrum (Reassigned)");
        Check("row 2 now reports under 13 Loftrum (Reassigned)",
            c2.Number == "13" && c2.Name == "Loftrum (Reassigned)");
    }

    // ------------------------------------------------------------------ the defect

    private static void SharedKeyAcrossRooms()
    {
        Section("One slab under three rooms: only the reassigned room's piece moves");

        Ov[] overrides = [new(Slab, "101", "Stue", "201", "Trapperum")];

        var a = new Carrier(Slab, "101", "Stue");
        var b = new Carrier(Slab, "102", "Køkken");
        var c = new Carrier(Slab, "103", "Bad");

        var applied = Apply(overrides, [a, b, c]);

        Check("exactly one row re-pointed", applied == 1);
        Check("101's piece moved to 201", a.Number == "201" && a.Name == "Trapperum (Reassigned)");
        Check("102's piece left in 102, untouched", b.Number == "102" && b.Name == "Køkken");
        Check("103's piece left in 103, untouched", c.Number == "103" && c.Name == "Bad");

        // The target room's OWN piece of the slab shares the key and shows the target number,
        // but was never relabelled - it has no marker. It must not be taken for a moved row.
        var own = new Carrier(Slab, "201", "Trapperum");
        Check("the target room's own piece is not mistaken for a relabelled one",
            Owner([new(Slab, "101", "Stue", "201", "Trapperum")], own, out _) is null);
    }

    private static void ReapplyIsIdempotent()
    {
        Section("Re-apply with no takeoff in between");

        Ov[] overrides = [new(Slab, "101", "Stue", "201", "Trapperum")];
        var a = new Carrier(Slab, "101", "Stue");
        var b = new Carrier(Slab, "102", "Køkken");

        Apply(overrides, [a, b]);
        var second = Apply(overrides, [a, b]);

        Check("the already-moved row is still owned and re-applied", second == 1);
        Check("and still reads 201 Trapperum (Reassigned)", a.Number == "201" && a.Name == "Trapperum (Reassigned)");
        Check("the other room's piece is still untouched", b.Number == "102" && b.Name == "Køkken");
        Check("marker is not doubled", !a.Name.EndsWith("(Reassigned) (Reassigned)"));
    }

    private static void TwoOverridesOneKey()
    {
        Section("Two overrides may share a key when they come from different rooms");

        Ov[] overrides =
        [
            new(Slab, "101", "Stue", "201", "Trapperum"),
            new(Slab, "102", "Køkken", "202", "Gang"),
        ];

        var a = new Carrier(Slab, "101", "Stue");
        var b = new Carrier(Slab, "102", "Køkken");
        var c = new Carrier(Slab, "103", "Bad");

        Apply(overrides, [a, b, c]);

        Check("101 -> 201 by its own override", a.Number == "201");
        Check("102 -> 202 by its own override", b.Number == "202");
        Check("103 has no override and stays", c.Number == "103" && c.Name == "Bad");

        Section("...and both into the SAME room is flagged when restoring would have to guess");

        Ov[] intoOne =
        [
            new(Slab, "101", "Stue", "201", "Trapperum"),
            new(Slab, "102", "Køkken", "201", "Trapperum"),
        ];

        var moved = new Carrier(Slab, "201", "Trapperum (Reassigned)");
        var owner = Owner(intoOne, moved, out var ambiguous);

        Check("a relabelled row is still owned (re-apply writes the same thing either way)", owner is not null);
        Check("but reported ambiguous, so it is never restored to a guessed room", ambiguous);

        var fresh = new Carrier(Slab, "102", "Køkken");
        Owner(intoOne, fresh, out var freshAmbiguous);
        Check("a fresh row is never ambiguous - its room says which override", !freshAmbiguous);
    }

    private static void RenumberedRoom()
    {
        Section("A renumbered from-room stops the override rather than misfiling");

        Ov[] overrides = [new(Wall1, "40", "Kælderrum 4", "13", "Loftrum")];

        // Kælderrum 4 renumbered 40 -> 41; the takeoff now writes 41 on the same face.
        var c = new Carrier(Wall1, "41", "Kælderrum 4");
        var applied = Apply(overrides, [c]);

        Check("not applied", applied == 0);
        Check("row left in its natural room, 41", c.Number == "41" && c.Name == "Kælderrum 4");
    }

    private static void Identity()
    {
        Section("Room numbers and override identity");

        Check("numbers compare trimmed", PaintOverrideOwnership.SameRoomNumber(" 40 ", "40"));
        Check("numbers compare case-insensitively", PaintOverrideOwnership.SameRoomNumber("4a", "4A"));
        Check("different numbers differ", !PaintOverrideOwnership.SameRoomNumber("40", "41"));
        Check("null and empty are the same (an unnumbered room)", PaintOverrideOwnership.SameRoomNumber(null, ""));

        Check("same key, same from-room -> same override",
            PaintOverrideOwnership.SameOverride(Slab, "101", Slab, " 101"));
        Check("same key, different from-room -> different overrides",
            !PaintOverrideOwnership.SameOverride(Slab, "101", Slab, "102"));
        Check("key compared exactly (it embeds an element id)",
            !PaintOverrideOwnership.SameOverride(Slab, "101", Slab.ToUpperInvariant(), "101"));

        Check("marker recognised", PaintOverrideOwnership.IsRelabelled("Loftrum (Reassigned)"));
        Check("plain name is not relabelled", !PaintOverrideOwnership.IsRelabelled("Loftrum"));
        Check("null name is not relabelled", !PaintOverrideOwnership.IsRelabelled(null));

        Ov[] dupes =
        [
            new(Slab, "101", "Stue", "201", "Trapperum"),
            new(Slab, "101", "Stue", "202", "Gang"),
        ];
        var c = new Carrier(Slab, "101", "Stue");
        Check("duplicate records of one decision: the last one wins",
            Owner(dupes, c, out _)?.To == "202");
    }

    private static void AreaDrift()
    {
        Section("Area drift is reported, never enforced");

        const double sqFtPerSqM = 1 / 0.09290304;

        Check("identical area is not drift", !PaintOverrideOwnership.AreaDrifted(1.004 * sqFtPerSqM, 1.004 * sqFtPerSqM));
        Check("0.0005 m² is within tolerance", !PaintOverrideOwnership.AreaDrifted(1.0045 * sqFtPerSqM, 1.004 * sqFtPerSqM));
        Check("0.01 m² is drift", PaintOverrideOwnership.AreaDrifted(1.014 * sqFtPerSqM, 1.004 * sqFtPerSqM));
        Check("unknown current area is not drift", !PaintOverrideOwnership.AreaDrifted(0, 1.004 * sqFtPerSqM));
        Check("unknown recorded area is not drift", !PaintOverrideOwnership.AreaDrifted(1.004 * sqFtPerSqM, 0));
    }

    private static void ApplyThenClearRoundTrip()
    {
        Section("Remove all: every moved row back in its natural room, nothing else touched");

        Ov[] overrides =
        [
            new(Slab, "101", "Stue", "201", "Trapperum"),
            new(Wall1, "40", "Kælderrum 4", "13", "Loftrum"),
        ];

        Carrier[] carriers =
        [
            new(Slab, "101", "Stue"),
            new(Slab, "102", "Køkken"),     // shares the key - the old code threw here
            new(Slab, "103", "Bad"),
            new(Wall1, "40", "Kælderrum 4"),
        ];

        Apply(overrides, carriers);

        var restored = 0;
        var threw = false;

        try
        {
            restored = Clear(overrides, carriers);
        }
        catch
        {
            threw = true;
        }

        Check("does not throw on a key shared by several carriers", !threw);
        Check("restores exactly the two moved rows", restored == 2);
        Check("every carrier shows its natural room again",
            carriers.All(c => c.Number == c.NaturalNumber && c.Name == c.NaturalName));
    }

    // ------------------------------------------------------------------ the shipping loops, in miniature

    /// <summary>
    /// PaintRoomOverrides.Apply's decision, over plain data: relabel a carrier only if an
    /// override OWNS it.
    /// </summary>
    private static int Apply(IReadOnlyList<Ov> overrides, IEnumerable<Carrier> carriers)
    {
        var applied = 0;

        foreach (var carrier in carriers)
        {
            var owner = Owner(overrides, carrier, out _);
            if (owner is null) continue;

            carrier.Number = owner.To;
            carrier.Name = owner.ToName + PaintOverrideOwnership.ReassignedMarker;
            applied++;
        }

        return applied;
    }

    /// <summary>ReassignPaintRoomCommand's "remove all" loop, over plain data.</summary>
    private static int Clear(IReadOnlyList<Ov> overrides, IEnumerable<Carrier> carriers)
    {
        var restored = 0;

        foreach (var carrier in carriers)
        {
            if (!PaintOverrideOwnership.IsRelabelled(carrier.Name)) continue;

            var owner = Owner(overrides, carrier, out var ambiguous);
            if (owner is null || ambiguous) continue;

            carrier.Number = owner.From;
            carrier.Name = owner.FromName;
            restored++;
        }

        return restored;
    }

    private static Ov? Owner(IReadOnlyList<Ov> overrides, Carrier carrier, out bool ambiguous) =>
        PaintOverrideOwnership.OwnerOf(
            overrides.Where(o => o.Key == carrier.Key).ToList(),
            carrier.Number, carrier.Name, o => o.From, o => o.To, out ambiguous);

    // ------------------------------------------------------------------ harness

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
