using System.Globalization;
using Cda.Revit.Addin.Excel;

namespace Cda.Revit.Addin.TimeTracking;

/// <summary>
/// Turns the raw ledger into something a person can read.
///
/// WHY THIS EXISTS SEPARATELY FROM THE CSV
///   The CSV is an EVENT LOG and has to stay one: every view switch closes a segment, so two
///   hours of work is forty-odd rows of fragments, most of them a minute long, with the same
///   view appearing a dozen times. That is the correct shape for a machine and a hopeless
///   shape for a human — opened in Excel it is a wall of near-identical lines with three
///   empty columns down the middle.
///
///   The fix is not to log less. It is to roll up at the point of reading, which is what this
///   does: the ledger keeps every fragment, and the report answers the two questions anyone
///   actually asks — how much time, and on what.
///
/// FORMATS: hours appear TWICE on purpose. "Hours" is a decimal (2.12) because that is what
/// sums, sorts and pastes into a billing system; "Time" is 2:07 because that is what a person
/// reads. Printing only the decimal is how you get someone reading 2.12 as two hours twelve.
/// </summary>
internal static class TimesheetReport
{
    public static void WriteXlsx(
        string path, IReadOnlyList<TimeEntry> entries, DateTime from, DateTime to, string username)
    {
        var sheets = new List<XlsxSheet>
        {
            Summary(entries, from, to, username),
            ByView(entries),
            ByTask(entries),
            Detail(entries),
        };

        XlsxWriter.Write(path, sheets);
    }

    // ------------------------------------------------------------------ sheet 1

    /// <summary>Per day, per project. The answer to "what am I billing this week?".</summary>
    private static XlsxSheet Summary(
        IReadOnlyList<TimeEntry> entries, DateTime from, DateTime to, string username)
    {
        // Added rather than listed in a collection initializer: inside one, [ ... ] is parsed
        // as an INDEXER assignment, not a collection expression, and the error it produces
        // ("'=' expected") points nowhere near the cause.
        // THE PROJECT INFORMATION COLUMNS ARE ALWAYS SHOWN, unlike the incidental ones on
        // the Detail sheet.
        //
        // They were briefly subject to the same "drop anything empty on every row" rule, and
        // that was wrong: these four were asked for by name, so a run where they happen to be
        // empty must show four blank columns and say why, not silently omit them. A missing
        // column reads as a broken feature; a blank one reads as missing data, which is what
        // it is. The generic rule still applies to columns nobody asked for.
        var missing = new List<string>();
        if (!entries.Any(e => e.Selskab.Length > 0)) missing.Add("Selskab");
        if (!entries.Any(e => e.Afdeling.Length > 0)) missing.Add("Afdeling");
        if (!entries.Any(e => e.ClientNumber.Length > 0)) missing.Add("Client no.");
        if (!entries.Any(e => e.Operator.Length > 0)) missing.Add("Operator");
        if (!entries.Any(e => e.QA.Length > 0)) missing.Add("QA");
        if (!entries.Any(e => e.TaskPhase.Length > 0)) missing.Add("Task / Phase");

        // Added rather than listed in a collection initializer: inside one, [ ... ] is parsed
        // as an INDEXER assignment, not a collection expression, and the error it produces
        // ("'=' expected") points nowhere near the cause.
        var rows = new List<IReadOnlyList<string>>();
        rows.Add(["DKSI time log", username]);
        rows.Add([$"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}", $"{entries.Count} entries"]);

        if (missing.Count > 0)
        {
            rows.Add([
                $"Blank in every row: {string.Join(", ", missing)}",
                "— either the parameter is empty in Project Information, or the entry was " +
                "logged before this add-in started recording it.",
            ]);
        }

        rows.Add([]);

        var header = new List<string>
        {
            "Date", "Day", "Selskab", "Afdeling", "Client no.", "Operator", "QA",
            "Project", "Task / Phase", "Hours", "Time", "Entries", "Automated", "Manual",
        };
        rows.Add(header);

        var bold = new HashSet<int> { 0, rows.Count - 1 };

        // Grouped by the Project Information too, not just the project name. If a model's
        // Selskab or Operator changed mid-month that is a real split worth seeing, not a
        // detail to average away. Task/Phase joins the same grouping for the same reason -
        // this is what makes the timesheet answer "how much on Construction Documentation
        // vs Clash Detection today", not just "how much today", which is the whole point of
        // tagging a segment with a task in the first place - see TaskDetection.
        var byDay = entries
            .GroupBy(e => (Date: e.Started.Date, e.ProjectName, e.Selskab, e.Afdeling, e.ClientNumber, e.Operator, e.QA,
                            TaskPhase: e.TaskPhase.Length == 0 ? "(none)" : e.TaskPhase))
            .OrderBy(g => g.Key.Date)
            .ThenBy(g => g.Key.ProjectName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.Key.TaskPhase, StringComparer.CurrentCultureIgnoreCase);

        foreach (var group in byDay)
        {
            var minutes = group.Sum(e => e.DurationMinutes);

            var row = new List<string>
            {
                group.Key.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                group.Key.Date.ToString("ddd", CultureInfo.CurrentCulture),
            };

            row.Add(Blank(group.Key.Selskab));
            row.Add(Blank(group.Key.Afdeling));
            row.Add(Blank(group.Key.ClientNumber));
            row.Add(Blank(group.Key.Operator));
            row.Add(Blank(group.Key.QA));

            row.AddRange([
                Blank(group.Key.ProjectName),
                group.Key.TaskPhase,
                Hours(minutes),
                HoursMinutes(minutes),
                group.Count().ToString(CultureInfo.InvariantCulture),
                HoursMinutes(group.Where(e => e.Type == TimeEntryType.Automated).Sum(e => e.DurationMinutes)),
                HoursMinutes(group.Where(e => e.Type == TimeEntryType.Manual).Sum(e => e.DurationMinutes)),
            ]);

            rows.Add(row);
        }

        var total = entries.Sum(e => e.DurationMinutes);

        // Date, Day, the five Project Information columns, then Project and Task/Phase - so
        // the totals line up under Hours regardless of what any of them contain.
        var totalRow = new List<string> { "TOTAL" };
        while (totalRow.Count < 9) totalRow.Add(string.Empty);

        totalRow.AddRange([
            Hours(total),
            HoursMinutes(total),
            entries.Count.ToString(CultureInfo.InvariantCulture),
            HoursMinutes(entries.Where(e => e.Type == TimeEntryType.Automated).Sum(e => e.DurationMinutes)),
            HoursMinutes(entries.Where(e => e.Type == TimeEntryType.Manual).Sum(e => e.DurationMinutes)),
        ]);

        rows.Add([]);
        bold.Add(rows.Count);
        rows.Add(totalRow);

        return new XlsxSheet
        {
            Name = "Summary",
            Rows = rows,
            BoldRows = bold,
            Widths = [12, 6, 12, 12, 12, 18, 12, 28, 20, 9, 9, 9, 11, 9],

            // Freeze below the header wherever it ended up - the note line above it is
            // conditional, so a hardcoded 4 would cut the table in half when it is absent.
            FreezeAt = rows.FindIndex(r => r.Count > 0 && r[0] == "Date") + 1,
        };
    }

    // ------------------------------------------------------------------ sheet 2

    /// <summary>
    /// Per project, per view, biggest first. The answer to "where did the day go?" — and the
    /// sheet that makes the view-template column earn its place, because grouping by template
    /// is time per discipline rather than time per drawing.
    /// </summary>
    private static XlsxSheet ByView(IReadOnlyList<TimeEntry> entries)
    {
        var rows = new List<IReadOnlyList<string>>();
        rows.Add(["Project", "View", "View template", "Hours", "Time", "Visits"]);

        var groups = entries
            .GroupBy(e => (e.ProjectName, e.ViewName, e.ViewTemplate))
            .Select(g => (Key: g.Key, Minutes: g.Sum(e => e.DurationMinutes), Count: g.Count()))
            .OrderByDescending(g => g.Minutes);

        foreach (var (key, minutes, count) in groups)
        {
            rows.Add([
                Blank(key.ProjectName),
                key.ViewName.Length == 0 ? "(no view — manual entry)" : key.ViewName,
                Blank(key.ViewTemplate),
                Hours(minutes),
                HoursMinutes(minutes),
                count.ToString(CultureInfo.InvariantCulture),
            ]);
        }

        return new XlsxSheet
        {
            Name = "By view",
            Rows = rows,
            BoldRows = [0],
            Widths = [22, 44, 18, 9, 9, 8],
            FreezeAt = 1,
        };
    }

    // ------------------------------------------------------------------ sheet 3

    /// <summary>
    /// Per project, per task/phase, biggest first - the answer "how much of this billed to
    /// Construction Documentation vs Clash Detection", which is the whole point of tagging a
    /// segment with a task in the first place. Rows with no task auto-detected or chosen
    /// (entries logged before this add-in tracked task/phase) group under "(none)" rather
    /// than vanishing, so a month with older rows in it does not silently under-report.
    /// </summary>
    private static XlsxSheet ByTask(IReadOnlyList<TimeEntry> entries)
    {
        var rows = new List<IReadOnlyList<string>>();
        rows.Add(["Project", "Task / Phase", "Category", "Hours", "Time", "Entries"]);

        var groups = entries
            .GroupBy(e => (e.ProjectName, TaskPhase: e.TaskPhase.Length == 0 ? "(none)" : e.TaskPhase, e.TaskCategory))
            .Select(g => (Key: g.Key, Minutes: g.Sum(e => e.DurationMinutes), Count: g.Count()))
            .OrderByDescending(g => g.Minutes);

        foreach (var (key, minutes, count) in groups)
        {
            rows.Add([
                Blank(key.ProjectName),
                key.TaskPhase,
                Blank(key.TaskCategory),
                Hours(minutes),
                HoursMinutes(minutes),
                count.ToString(CultureInfo.InvariantCulture),
            ]);
        }

        return new XlsxSheet
        {
            Name = "By task",
            Rows = rows,
            BoldRows = [0],
            Widths = [22, 26, 16, 9, 9, 8],
            FreezeAt = 1,
        };
    }

    // ------------------------------------------------------------------ sheet 4

    /// <summary>
    /// Every entry, unrolled — the audit trail behind the sheets above.
    ///
    /// Columns that are empty for EVERY row are dropped rather than printed as a grey band
    /// down the middle of the sheet. A project number nobody filled in is not information,
    /// and three such columns is most of why the raw CSV reads as broken.
    /// </summary>
    private static XlsxSheet Detail(IReadOnlyList<TimeEntry> entries)
    {
        var showNumber = entries.Any(e => e.ProjectNumber.Length > 0);
        var showTemplate = entries.Any(e => e.ViewTemplate.Length > 0);
        var showDescription = entries.Any(e => e.Description.Length > 0);
        var showFile = entries.Any(e => e.FileName.Length > 0);
        var showTask = entries.Any(e => e.TaskPhase.Length > 0);

        // A deliberately weak proxy - see TimeEntry.ExternalActivity's remarks. Shown only
        // when there is something to show, same rule as every other optional column here.
        var showExternal = entries.Any(e => e.ExternalActivity.Length > 0);

        // Only when the log actually spans more than one workstation - otherwise it is the
        // same value on every row, which is decoration.
        var showMachine = entries.Select(e => e.Machine).Distinct().Count() > 1;

        var header = new List<string> { "Date", "Start", "End", "Time", "Hours", "Project" };
        if (showNumber) header.Add("Project no.");
        header.Add("View");
        if (showTemplate) header.Add("View template");
        header.Add("Type");
        if (showTask) header.Add("Task / Phase");
        if (showDescription) header.Add("Description");
        if (showExternal) header.Add("Non-DKSI transactions (raw names)");
        if (showFile) header.Add("Model file");
        if (showMachine) header.Add("Workstation");

        var rows = new List<IReadOnlyList<string>> { header };

        foreach (var entry in entries.OrderBy(e => e.Started))
        {
            var row = new List<string>
            {
                entry.Started.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                entry.Started.ToString("HH:mm", CultureInfo.InvariantCulture),
                entry.Ended.ToString("HH:mm", CultureInfo.InvariantCulture),
                HoursMinutes(entry.DurationMinutes),
                Hours(entry.DurationMinutes),
                Blank(entry.ProjectName),
            };

            if (showNumber) row.Add(Blank(entry.ProjectNumber));
            row.Add(entry.ViewName.Length == 0 ? "(no view — manual entry)" : entry.ViewName);
            if (showTemplate) row.Add(Blank(entry.ViewTemplate));
            row.Add(entry.Type.ToString());
            if (showTask) row.Add(Blank(entry.TaskPhase));
            if (showDescription) row.Add(Blank(entry.Description));
            if (showExternal) row.Add(Blank(entry.ExternalActivity));
            if (showFile) row.Add(Blank(entry.FileName));
            if (showMachine) row.Add(Blank(entry.Machine));

            rows.Add(row);
        }

        var widths = new List<double> { 12, 8, 8, 9, 9, 22 };
        if (showNumber) widths.Add(12);
        widths.Add(44);
        if (showTemplate) widths.Add(18);
        widths.Add(11);
        if (showTask) widths.Add(24);
        if (showDescription) widths.Add(30);
        if (showExternal) widths.Add(40);
        if (showFile) widths.Add(34);
        if (showMachine) widths.Add(14);

        return new XlsxSheet
        {
            Name = "Detail",
            Rows = rows,
            BoldRows = [0],
            Widths = widths,
            FreezeAt = 1,
        };
    }

    // ------------------------------------------------------------------ formatting

    /// <summary>Decimal hours, for summing and for pasting into a billing system.</summary>
    private static string Hours(double minutes) =>
        (minutes / 60.0).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>H:MM, for reading. 127.4 minutes is 2:07, which nobody has to work out.</summary>
    private static string HoursMinutes(double minutes)
    {
        var rounded = (int)Math.Round(minutes, MidpointRounding.AwayFromZero);
        return $"{rounded / 60}:{rounded % 60:00}";
    }

    /// <summary>An em dash reads as "nothing here" where an empty cell reads as "broken".</summary>
    private static string Blank(string value) => value.Length == 0 ? "—" : value;
}
