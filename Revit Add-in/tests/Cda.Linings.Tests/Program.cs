using Cda.Revit.Addin.Linings;

namespace Cda.Linings.Tests;

/// <summary>
/// Runnable checks over <see cref="LiningSync"/> - the rules that keep a door and the
/// windows it touches in step for "Lining YN" and the material code.
///
/// WHAT THESE ARE GUARDING AGAINST
///   Not crashes. Every failure here is a schedule that looks finished and is wrong: a window
///   edit silently reverted to the door's value, a conflict resolved door-wins without anyone
///   deciding it, or a first run on an existing model behaving differently from before.
/// </summary>
internal static class Program
{
    private static int _run;
    private static int _failed;

    private const long D1 = 101, D2 = 102, W1 = 201, W2 = 202, W3 = 203;

    private static readonly MasterLiningRule Master = new(mirrorBothWays: true);
    private static readonly MasterLiningRule UncheckOnly = new(mirrorBothWays: false);

    private static readonly MaterialRule Material = new("D", "W",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["DSPECIAL"] = "WX" });

    private static int Main()
    {
        Console.WriteLine("Door/window Lining YN and material sync\n");

        FirstRunIsTheOldBehaviour();
        DoorEdits();
        WindowEdits();
        Conflicts();
        Chains();
        UntrackedAndOutOfScope();
        Materials();

        Console.WriteLine($"\n{_run - _failed}/{_run} passed.");
        return _failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- first run

    private static void FirstRunIsTheOldBehaviour()
    {
        Section("Nothing recorded yet: doors drive windows, exactly as before");

        var r = Solve(Master, [Door(D1, "0"), Window(W1, "1")], (D1, W1));
        Check("window follows door off", Only(r, W1, "0", SyncDirection.DoorToWindow));
        Check("nothing held back", r.Unsettled.Count == 0);

        r = Solve(Master, [Door(D1, "1"), Window(W1, "0")], (D1, W1));
        Check("window follows door on (mirror)", Only(r, W1, "1", SyncDirection.DoorToWindow));

        r = Solve(UncheckOnly, [Door(D1, "1"), Window(W1, "0")], (D1, W1));
        Check("uncheck-only never turns a window on", r.Writes.Count == 0);

        r = Solve(Master, [Door(D1, "1"), Window(W1, "1")], (D1, W1));
        Check("agreeing pair: no writes", r.Writes.Count == 0 && r.Issues.Count == 0);

        // A pasted copy reads as "never recorded" - it must not look like an edit.
        r = Solve(Master, [Door(D1, "1", stored: "1"), Window(W1, "0", stored: null)], (D1, W1));
        Check("unrecorded window is not an edit", Only(r, W1, "1", SyncDirection.DoorToWindow));
    }

    // --------------------------------------------------------------- door edits

    private static void DoorEdits()
    {
        Section("Door edited since the last apply");

        var r = Solve(Master, [Door(D1, "0", stored: "1"), Window(W1, "1", stored: "1")], (D1, W1));
        Check("door off -> window off", Only(r, W1, "0", SyncDirection.DoorToWindow));

        r = Solve(Master, [Door(D1, "1", stored: "0"), Window(W1, "0", stored: "0")], (D1, W1));
        Check("door on -> window on", Only(r, W1, "1", SyncDirection.DoorToWindow));
    }

    // ------------------------------------------------------------- window edits

    private static void WindowEdits()
    {
        Section("Window edited since the last apply: the door follows");

        var r = Solve(Master, [Door(D1, "1", stored: "1"), Window(W1, "0", stored: "1")], (D1, W1));
        Check("window off -> door off", Only(r, D1, "0", SyncDirection.WindowToDoor));
        Check("driver is the window", r.Writes.Single().DriverId == W1);
        Check("window itself not rewritten", r.Writes.All(w => w.Id != W1));

        r = Solve(Master, [Door(D1, "0", stored: "0"), Window(W1, "1", stored: "0")], (D1, W1));
        Check("window on -> door on", Only(r, D1, "1", SyncDirection.WindowToDoor));
        Check("...and not reverted by the forward pass", r.Writes.All(w => w.Id != W1));

        r = Solve(UncheckOnly, [Door(D1, "0", stored: "0"), Window(W1, "1", stored: "0")], (D1, W1));
        Check("uncheck-only: window on does not turn the door on", r.Writes.Count == 0);

        r = Solve(Master, [Door(D1, "1", stored: "1"), Window(W1, "0", stored: "1"), Door(D2, "1", stored: "1")],
            (D1, W1), (D2, W1));
        Check("a window touching two doors drives both",
            r.Writes.Count == 2 && r.Writes.All(w => w.Value == "0" && w.Direction == SyncDirection.WindowToDoor));
    }

    // ---------------------------------------------------------------- conflicts

    private static void Conflicts()
    {
        Section("Both sides edited and disagreeing: warn, write nothing");

        var r = Solve(Master, [Door(D1, "0", stored: "1"), Window(W1, "1", stored: "0")], (D1, W1));
        Check("no writes", r.Writes.Count == 0);
        Check("one warning", r.Issues.Count == 1);
        Check("both held back for next run", r.Unsettled.SetEquals([D1, W1]));

        r = Solve(Master, [Door(D1, "0", stored: "1"), Window(W1, "0", stored: "1")], (D1, W1));
        Check("both edited to the SAME value is not a conflict",
            r.Writes.Count == 0 && r.Issues.Count == 0 && r.Unsettled.Count == 0);

        // Two windows on one door, edited different ways.
        r = Solve(Master,
            [Door(D1, "1", stored: "1"), Window(W1, "0", stored: "1"), Window(W2, "1", stored: "0")],
            (D1, W1), (D1, W2));
        Check("windows disagreeing: door untouched", r.Writes.Count == 0);
        Check("both windows held back", r.Unsettled.SetEquals([W1, W2]));

        // "Write nothing" means nothing: a window in conflict at D1 must not move D2 either.
        r = Solve(Master,
            [Door(D1, "0", stored: "1"), Door(D2, "0", stored: "0"), Window(W1, "1", stored: "0")],
            (D1, W1), (D2, W1));
        Check("conflicted window pushes to no door", r.Writes.Count == 0);
        Check("window held back", r.Unsettled.Contains(W1));
    }

    // ------------------------------------------------------------------- chains

    private static void Chains()
    {
        Section("A door moved by a window drives its other windows in the same pass");

        var r = Solve(Master,
            [Door(D1, "1", stored: "1"), Window(W1, "0", stored: "1"), Window(W2, "1", stored: "1")],
            (D1, W1), (D1, W2));
        Check("door follows W1", r.Writes.Any(w => w.Id == D1 && w.Value == "0"));
        Check("W2 follows the door", r.Writes.Any(w => w.Id == W2 && w.Value == "0" && w.DriverId == D1));
        Check("exactly two writes", r.Writes.Count == 2);

        // Door conflicted by two windows; an unedited third window follows the door as it is.
        r = Solve(Master,
            [Door(D1, "0", stored: "0"), Window(W1, "0", stored: "1"), Window(W2, "1", stored: "0"),
             Window(W3, "1", stored: "1")],
            (D1, W1), (D1, W2), (D1, W3));
        Check("unedited window follows the door's current value",
            r.Writes.Count == 1 && r.Writes[0].Id == W3 && r.Writes[0].Value == "0");
    }

    // ------------------------------------------------------- untracked / scope

    private static void UntrackedAndOutOfScope()
    {
        Section("A door that cannot be written holds the window's edit back");

        var r = Solve(Master,
            [Door(D1, "1", tracked: false), Window(W1, "0", stored: "1")], (D1, W1));
        Check("door with no Lining YN: no writes", r.Writes.Count == 0);
        Check("window edit kept pending", r.Unsettled.Contains(W1));
        Check("warned", r.Issues.Count == 1);

        r = Solve(Master,
            [Door(D1, "1", stored: "1", writable: false), Window(W1, "0", stored: "1")], (D1, W1));
        Check("door out of scope: no writes, pending", r.Writes.Count == 0 && r.Unsettled.Contains(W1));

        r = Solve(Master,
            [Door(D1, "0", stored: "0"), Window(W1, "1", stored: "0", tracked: false, writable: false)], (D1, W1));
        Check("left-alone window neither drives nor follows", r.Writes.Count == 0);
    }

    // ---------------------------------------------------------------- materials

    private static void Materials()
    {
        Section("Material codes, both directions");

        var r = Solve(Material, [Door(D1, "DDL"), Window(W1, "")], (D1, W1));
        Check("DDL -> WDL", Only(r, W1, "WDL", SyncDirection.DoorToWindow));

        r = Solve(Material, [Door(D1, "DDL", stored: "DDL"), Window(W1, "WYL", stored: "WDL")], (D1, W1));
        Check("window WYL -> door DYL", Only(r, D1, "DYL", SyncDirection.WindowToDoor));

        r = Solve(Material, [Door(D1, "DDL", stored: "DDL"), Window(W1, "wyl", stored: "WDL")], (D1, W1));
        Check("prefix is case-insensitive", Only(r, D1, "Dyl", SyncDirection.WindowToDoor));

        r = Solve(Material, [Door(D1, "DDL", stored: "DDL"), Window(W1, "XYZ", stored: "WDL")], (D1, W1));
        Check("unconvertible window code: no write", r.Writes.Count == 0);
        Check("...warned and held back", r.Issues.Count == 1 && r.Unsettled.Contains(W1));

        r = Solve(Material, [Door(D1, "DDL", stored: "DDL"), Window(W1, "", stored: "WDL")], (D1, W1));
        Check("blanking a window does not blank the door", r.Writes.Count == 0 && r.Unsettled.Count == 0);

        r = Solve(Material, [Door(D1, "DSPECIAL"), Window(W1, "")], (D1, W1));
        Check("override forward", Only(r, W1, "WX", SyncDirection.DoorToWindow));

        r = Solve(Material, [Door(D1, "DDL", stored: "DDL"), Window(W1, "WX", stored: "WDL")], (D1, W1));
        Check("override reverse", Only(r, D1, "DSPECIAL", SyncDirection.WindowToDoor));

        r = Solve(Material, [Door(D1, "DDL"), Door(D2, "DYL"), Window(W1, "WDL")], (D1, W1), (D2, W1));
        Check("doors disagree, window unedited: left alone, warned",
            r.Writes.Count == 0 && r.Issues.Count == 1);

        r = Solve(Material,
            [Door(D1, "DDL", stored: "DDL"), Door(D2, "DYL", stored: "DYL"), Window(W1, "WYL", stored: "WDL")],
            (D1, W1), (D2, W1));
        Check("editing the window settles two disagreeing doors",
            r.Writes.Count == 1 && r.Writes[0].Id == D1 && r.Writes[0].Value == "DYL");
    }

    // ------------------------------------------------------------------ helpers

    private static SyncNode Door(long id, string value, string? stored = null, bool tracked = true, bool writable = true) =>
        new() { Id = id, IsDoor = true, Current = value, Stored = stored, Tracked = tracked, Writable = writable };

    private static SyncNode Window(long id, string value, string? stored = null, bool tracked = true, bool writable = true) =>
        new() { Id = id, IsDoor = false, Current = value, Stored = stored, Tracked = tracked, Writable = writable };

    private static SyncResult Solve(SyncRule rule, SyncNode[] nodes, params (long Door, long Window)[] contacts) =>
        LiningSync.Solve(nodes, contacts, rule);

    private static bool Only(SyncResult r, long id, string value, SyncDirection direction) =>
        r.Writes.Count == 1 && r.Writes[0].Id == id && r.Writes[0].Value == value && r.Writes[0].Direction == direction;

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
