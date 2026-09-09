namespace Cda.Revit.Addin.Schedules;

/// <summary>
/// What kind of schedule view this is. Revit exposes these as separate flags and
/// categories rather than one enum, so they are read once when the list is built and
/// carried on the candidate - the dialog filters thousands of times, the model is read once.
/// </summary>
public enum ScheduleKind
{
    Schedule,
    KeySchedule,
    MaterialTakeoff,
    SheetList,
    ViewList,
}

/// <summary>How each <see cref="ScheduleKind"/> is written on screen.</summary>
public static class ScheduleKinds
{
    public static string Label(ScheduleKind kind) => kind switch
    {
        ScheduleKind.KeySchedule => "Key schedule",
        ScheduleKind.MaterialTakeoff => "Material takeoff",
        ScheduleKind.SheetList => "Sheet list",
        ScheduleKind.ViewList => "View list",
        _ => "Schedule",
    };
}

/// <summary>Whether the schedule view is placed on a sheet.</summary>
public enum SheetPlacement
{
    Any,
    OnSheet,
    NotOnSheet,
}

/// <summary>Whether the schedule has any data rows under its headers.</summary>
public enum ScheduleContent
{
    Any,
    WithData,
    Empty,
}

/// <summary>
/// One schedule as the filter dialog sees it: enough to list it, sort it and filter it,
/// and nothing that requires reading the table cell by cell.
///
/// Deliberately holds a plain <see cref="long"/> rather than an ElementId. That keeps this
/// file free of Revit types, which is what lets Cda.ScheduleFilter.Tests compile the real
/// filtering code instead of a copy of it.
/// </summary>
public sealed class ScheduleCandidate
{
    /// <summary>ElementId.Value of the ViewSchedule.</summary>
    public required long Id { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// The schedule's category as Revit names it - "Doors", "Rooms", "Mechanical
    /// Equipment". This is the closest thing a Revit model has to a discipline or
    /// department column, and it is how people actually think about which schedules
    /// belong to whom.
    /// </summary>
    public string Category { get; init; } = string.Empty;

    public ScheduleKind Kind { get; init; } = ScheduleKind.Schedule;

    /// <summary>Sheet number the view is placed on; empty when it is not on a sheet.</summary>
    public string SheetNumber { get; init; } = string.Empty;

    /// <summary>
    /// Body rows below the header, or null when the count could not be read.
    ///
    /// Null is not zero and must never be treated as zero: a schedule whose table data
    /// would not open is exactly the one worth exporting and looking at, and quietly
    /// filing it under "empty" would hide it behind the default filter.
    /// </summary>
    public int? DataRows { get; init; }

    public bool IsOnSheet => !string.IsNullOrWhiteSpace(SheetNumber);

    public string KindLabel => ScheduleKinds.Label(Kind);

    /// <summary>Columns in a ListView cannot show a null, and "0" would be a lie.</summary>
    public string RowsLabel => DataRows?.ToString() ?? "?";

    public string SheetLabel => IsOnSheet ? SheetNumber : "-";
}

/// <summary>
/// The filter behind the Export Schedules dialog.
///
/// Every clause is AND-ed: narrowing one control never widens the result, which is the
/// only behaviour a filter panel can have that people can predict.
///
/// NOTE ON DATE RANGE: Revit's API exposes no created-on or modified-on date for a view,
/// and workshared models only add who touched it, never when. A date filter here would
/// have to be invented from something else - and a filter that quietly means something
/// other than what its label says is worse than no filter at all. Placement and content
/// are the honest "status" axes a schedule actually has.
/// </summary>
public sealed class ScheduleFilter
{
    /// <summary>
    /// Free text. Split on whitespace; every term must appear somewhere in the name,
    /// the category or the sheet number. AND rather than OR, so typing more narrows the
    /// list - "door 2 fl" finds "Door Schedule - 2nd Floor".
    /// </summary>
    public string Search { get; set; } = string.Empty;

    /// <summary>Exact category name, or null for any.</summary>
    public string? Category { get; set; }

    /// <summary>Null for any kind.</summary>
    public ScheduleKind? Kind { get; set; }

    public SheetPlacement Placement { get; set; } = SheetPlacement.Any;

    public ScheduleContent Content { get; set; } = ScheduleContent.Any;

    /// <summary>True when nothing is being filtered out - used to word the dialog's count line.</summary>
    public bool IsUnfiltered =>
        string.IsNullOrWhiteSpace(Search) &&
        Category is null &&
        Kind is null &&
        Placement == SheetPlacement.Any &&
        Content == ScheduleContent.Any;

    public bool Matches(ScheduleCandidate candidate)
    {
        if (Category is not null &&
            !string.Equals(candidate.Category, Category, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Kind is { } kind && candidate.Kind != kind) return false;

        switch (Placement)
        {
            case SheetPlacement.OnSheet when !candidate.IsOnSheet: return false;
            case SheetPlacement.NotOnSheet when candidate.IsOnSheet: return false;
        }

        // Unknown row counts pass both content filters on purpose - see ScheduleCandidate.DataRows.
        switch (Content)
        {
            case ScheduleContent.WithData when candidate.DataRows == 0: return false;
            case ScheduleContent.Empty when candidate.DataRows > 0: return false;
        }

        return MatchesSearch(candidate);
    }

    private bool MatchesSearch(ScheduleCandidate candidate)
    {
        var terms = Search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return true;

        foreach (var term in terms)
        {
            var hit =
                candidate.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                candidate.Category.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                candidate.SheetNumber.Contains(term, StringComparison.OrdinalIgnoreCase);

            if (!hit) return false;
        }

        return true;
    }

    public IReadOnlyList<ScheduleCandidate> Apply(IEnumerable<ScheduleCandidate> candidates) =>
        candidates.Where(Matches).ToList();
}
