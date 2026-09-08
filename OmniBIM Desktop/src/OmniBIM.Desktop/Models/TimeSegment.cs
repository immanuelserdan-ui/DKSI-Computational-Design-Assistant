namespace OmniBIM.Desktop.Models;

/// <summary>
/// One row read back from a Revit add-in segment ledger (timelog-*.csv): the span between
/// two view/document activations, with the project context that was active at the time.
///
/// Mirrors Cda.Revit.Addin.TimeTracking.TimeEntry field-for-field. This is a READ-ONLY view
/// of that same on-disk row - OmniBIM never writes to these files, only the add-in does.
/// </summary>
public sealed class TimeSegment
{
    public required DateTime Started { get; init; }
    public required DateTime Ended { get; init; }

    /// <summary>
    /// Billable minutes as the add-in computed them - idle time already cut out, sub-minimum
    /// fragments already carried forward. Do not recompute this from Started/Ended; the two
    /// can legitimately disagree (see TimeEntry.DurationMinutes upstream).
    /// </summary>
    public required double DurationMinutes { get; init; }

    public required string Username { get; init; }
    public required TimeSegmentType Type { get; init; }

    public string ProjectName { get; init; } = string.Empty;
    public string ProjectNumber { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string ViewName { get; init; } = string.Empty;
    public string ViewTemplate { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Machine { get; init; } = string.Empty;

    public string Selskab { get; init; } = string.Empty;
    public string Afdeling { get; init; } = string.Empty;
    public string ClientNumber { get; init; } = string.Empty;
    public string Operator { get; init; } = string.Empty;
    public string QA { get; init; } = string.Empty;

    public string TaskPhase { get; init; } = string.Empty;
    public string TaskCategory { get; init; } = string.Empty;
    public string ExternalActivity { get; init; } = string.Empty;

    /// <summary>The project key used everywhere else in this app: number when there is one, name otherwise.</summary>
    public string ProjectKey => ProjectNumber.Length > 0 ? ProjectNumber : ProjectName;
}

public enum TimeSegmentType
{
    Automated,
    Manual,
}
