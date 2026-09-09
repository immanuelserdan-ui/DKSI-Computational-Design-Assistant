using Cda.Revit.Addin.Schedules;

namespace Cda.Schedules.Tests;

/// <summary>
/// Runnable checks over <see cref="ScheduleFilter"/> - the rules behind the Export
/// Schedules dialog.
///
/// The model here is a small but realistic set of schedules off a housing project: door
/// and window schedules on sheets, a room finish schedule that is not, a key schedule, a
/// material takeoff, and one schedule whose table would not open. Between them they cover
/// every branch the filter has.
/// </summary>
internal static class Program
{
    private static int _run;
    private static int _failed;

    private static readonly ScheduleCandidate[] Model =
    [
        Make(1, "Door Schedule - 1st Floor", "Doors", sheet: "A-201", rows: 42),
        Make(2, "Door Schedule - 2nd Floor", "Doors", sheet: "A-202", rows: 38),
        Make(3, "Window Schedule", "Windows", sheet: "A-203", rows: 17),
        Make(4, "Room Finish Schedule", "Rooms", rows: 96),
        Make(5, "WORKING - scratch counts", "Doors", rows: 0),
        Make(6, "Door Hardware Set", "Doors", kind: ScheduleKind.KeySchedule, rows: 12),
        Make(7, "Paint Takeoff", "Walls", kind: ScheduleKind.MaterialTakeoff, sheet: "A-901", rows: 310),
        Make(8, "Sheet Index", "Sheets", kind: ScheduleKind.SheetList, sheet: "A-001", rows: 24),
        Make(9, "Corrupt Door Count", "Doors", rows: null),
    ];

    private static int Main()
    {
        Console.WriteLine("Export Schedules filter\n");

        NothingIsFilteredByDefault();
        Search();
        Category();
        Kind();
        Placement();
        Content();
        UnknownRowCountsSurvive();
        ClausesCombine();

        Console.WriteLine($"\n{_run - _failed}/{_run} passed.");

        if (_failed > 0) Console.WriteLine($"{_failed} FAILED.");

        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------- the cases

    private static void NothingIsFilteredByDefault()
    {
        Section("a fresh filter hides nothing");

        var filter = new ScheduleFilter();

        True("reports itself unfiltered", filter.IsUnfiltered);
        Names("keeps every schedule", filter, Model.Select(c => c.Name).ToArray());
    }

    private static void Search()
    {
        Section("search");

        Names("matches part of a name", new ScheduleFilter { Search = "window" }, "Window Schedule");

        // The AND is the whole point: each word typed has to narrow the list, or a long
        // search term matches more than a short one and nobody can predict it.
        Names("every word has to match",
            new ScheduleFilter { Search = "door 2nd" },
            "Door Schedule - 2nd Floor");

        Names("is case insensitive",
            new ScheduleFilter { Search = "DOOR SCHEDULE" },
            "Door Schedule - 1st Floor", "Door Schedule - 2nd Floor");

        Names("searches the category too, not just the name",
            new ScheduleFilter { Search = "Windows" },
            "Window Schedule");

        Names("searches the sheet number", new ScheduleFilter { Search = "A-901" }, "Paint Takeoff");

        Names("no match is an empty list, never a fallback to everything",
            new ScheduleFilter { Search = "ceiling" });

        True("whitespace only is not a search",
            new ScheduleFilter { Search = "   " }.Apply(Model).Count == Model.Length);
    }

    private static void Category()
    {
        Section("category");

        Names("exact category, not a substring",
            new ScheduleFilter { Category = "Doors" },
            "Door Schedule - 1st Floor", "Door Schedule - 2nd Floor", "WORKING - scratch counts",
            "Door Hardware Set", "Corrupt Door Count");

        // "Door" is a prefix of "Doors". A substring match here would make the category
        // combo and the search box do the same job, badly.
        Names("a near miss matches nothing", new ScheduleFilter { Category = "Door" });

        Names("case insensitive, because Revit's own casing varies by locale",
            new ScheduleFilter { Category = "rooms" },
            "Room Finish Schedule");
    }

    private static void Kind()
    {
        Section("type");

        Names("key schedules", new ScheduleFilter { Kind = ScheduleKind.KeySchedule }, "Door Hardware Set");
        Names("material takeoffs", new ScheduleFilter { Kind = ScheduleKind.MaterialTakeoff }, "Paint Takeoff");
        Names("sheet lists", new ScheduleFilter { Kind = ScheduleKind.SheetList }, "Sheet Index");

        // A key schedule reports a real category as well. It must not also count as a
        // plain schedule, or "Schedule" means "anything".
        Names("plain schedules exclude the special kinds",
            new ScheduleFilter { Kind = ScheduleKind.Schedule },
            "Door Schedule - 1st Floor", "Door Schedule - 2nd Floor", "Window Schedule",
            "Room Finish Schedule", "WORKING - scratch counts", "Corrupt Door Count");
    }

    private static void Placement()
    {
        Section("placement");

        Names("on a sheet",
            new ScheduleFilter { Placement = SheetPlacement.OnSheet },
            "Door Schedule - 1st Floor", "Door Schedule - 2nd Floor", "Window Schedule",
            "Paint Takeoff", "Sheet Index");

        Names("not on a sheet",
            new ScheduleFilter { Placement = SheetPlacement.NotOnSheet },
            "Room Finish Schedule", "WORKING - scratch counts", "Door Hardware Set", "Corrupt Door Count");

        True("a blank sheet number is not a placement",
            !Make(99, "x", "Doors", sheet: "   ").IsOnSheet);
    }

    private static void Content()
    {
        Section("content");

        Names("empty schedules",
            new ScheduleFilter { Content = ScheduleContent.Empty },
            "WORKING - scratch counts", "Corrupt Door Count");

        True("schedules with rows exclude the empty one",
            !new ScheduleFilter { Content = ScheduleContent.WithData }
                .Apply(Model)
                .Any(c => c.Name == "WORKING - scratch counts"));
    }

    private static void UnknownRowCountsSurvive()
    {
        Section("a row count that could not be read");

        // Null is not zero. A schedule whose table would not open is exactly the one worth
        // looking at, and filing it under "empty" would hide it behind either content
        // filter - the failure would only surface as a missing worksheet, much later.
        var broken = Model.Single(c => c.Name == "Corrupt Door Count");

        True("shows as ? rather than 0", broken.RowsLabel == "?");
        True("survives 'has rows'", new ScheduleFilter { Content = ScheduleContent.WithData }.Matches(broken));
        True("survives 'empty'", new ScheduleFilter { Content = ScheduleContent.Empty }.Matches(broken));
    }

    private static void ClausesCombine()
    {
        Section("clauses are AND-ed");

        Names("category and placement together",
            new ScheduleFilter { Category = "Doors", Placement = SheetPlacement.OnSheet },
            "Door Schedule - 1st Floor", "Door Schedule - 2nd Floor");

        Names("search, category, type and content together",
            new ScheduleFilter
            {
                Search = "schedule",
                Category = "Doors",
                Kind = ScheduleKind.Schedule,
                Content = ScheduleContent.WithData,
            },
            "Door Schedule - 1st Floor", "Door Schedule - 2nd Floor");

        // Adding a clause can only ever remove rows. If one widens the result, two
        // controls are fighting and the panel becomes unpredictable.
        var narrower = new ScheduleFilter { Search = "door", Category = "Doors" }.Apply(Model);
        var wider = new ScheduleFilter { Search = "door" }.Apply(Model);

        True("adding a clause never widens the result", narrower.Count <= wider.Count);
    }

    // ------------------------------------------------------------------- harness

    private static ScheduleCandidate Make(
        long id,
        string name,
        string category,
        ScheduleKind kind = ScheduleKind.Schedule,
        string sheet = "",
        int? rows = 0) =>
        new()
        {
            Id = id,
            Name = name,
            Category = category,
            Kind = kind,
            SheetNumber = sheet,
            DataRows = rows,
        };

    private static void Names(string what, ScheduleFilter filter, params string[] expected)
    {
        _run++;

        var actual = filter.Apply(Model).Select(c => c.Name).ToArray();

        if (actual.SequenceEqual(expected)) Pass(what);
        else Fail($"{what}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
    }

    private static void Section(string name) => Console.WriteLine($"  {name}");

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
}
