namespace Cda.Revit.Addin.TimeTracking;

/// <summary>
/// Where a row came from. The CSV carries the word, not the number, so a timesheet
/// stays readable without a lookup table.
/// </summary>
public enum TimeEntryType
{
    /// <summary>Measured by the add-in from view/document activity.</summary>
    Automated,

    /// <summary>Typed in by the user — meetings, calls, off-model coordination.</summary>
    Manual,
}

/// <summary>
/// One row of the time log. Automated and manual entries share this shape deliberately:
/// a timesheet that stores "real" time differently from "typed" time cannot be summed
/// without special-casing, and the export is the whole point of the feature.
/// </summary>
public sealed class TimeEntry
{
    /// <summary>Local wall-clock start. Local, not UTC — a timesheet means local time.</summary>
    public required DateTime Started { get; init; }

    /// <summary>Local wall-clock end.</summary>
    public required DateTime Ended { get; init; }

    /// <summary>
    /// Billable minutes.
    ///
    /// Stored rather than derived from <see cref="Started"/>/<see cref="Ended"/>, because
    /// the two can legitimately disagree: idle time is cut out of a session, and sub-minimum
    /// fragments are carried into the next segment with the same context (see
    /// <see cref="TimeTracker"/>). Deriving it would either re-bill the idle gap or lose
    /// the carried seconds.
    /// </summary>
    public required double DurationMinutes { get; init; }

    public required string Username { get; init; }

    public required TimeEntryType Type { get; init; }

    /// <summary>Project Information &gt; Project Name, or whatever the user typed for a manual entry.</summary>
    public string ProjectName { get; init; } = string.Empty;

    /// <summary>Project Information &gt; Project Number.</summary>
    public string ProjectNumber { get; init; } = string.Empty;

    /// <summary>Document title — the .rvt file name, central or local.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Active view name at the time.</summary>
    public string ViewName { get; init; } = string.Empty;

    /// <summary>
    /// The view template applied to that view, if any.
    ///
    /// This is the column that actually carries the company naming standard (SMB-01,
    /// SMB-02, …). The standard lives on the TEMPLATE, so grouping a timesheet by view
    /// name gives you one row per sheet and plan; grouping by template gives you time per
    /// discipline/stage, which is the question anyone asks of a timesheet.
    /// </summary>
    public string ViewTemplate { get; init; } = string.Empty;

    /// <summary>Free text. Empty for ordinary automated segments.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Workstation name. Present so two Revit sessions on two machines under one login are
    /// distinguishable in the shared file — otherwise overlapping rows look like a bug.
    /// </summary>
    public string Machine { get; init; } = Environment.MachineName;

    // ---- Project Information, as it was when the time was logged ----------------

    /// <summary>Project Information &gt; Afdeling (department).</summary>
    public string Afdeling { get; init; } = string.Empty;

    /// <summary>Project Information &gt; Client Number.</summary>
    public string ClientNumber { get; init; } = string.Empty;

    /// <summary>Project Information &gt; Operator.</summary>
    public string Operator { get; init; } = string.Empty;

    /// <summary>Project Information &gt; Selskab (company).</summary>
    public string Selskab { get; init; } = string.Empty;

    /// <summary>Project Information &gt; QA (the reviewer of record).</summary>
    public string QA { get; init; } = string.Empty;

    /// <summary>
    /// What kind of work this row represents - one of <see cref="TaskDetection.Presets"/>
    /// picked by hand, a Revit project Phase name read off the view, or one of the two
    /// strong auto-detections (Family Creation, Construction Documentation). See
    /// <see cref="TaskDetection"/> and <see cref="TimeTracker.ApplyAutoTask"/>.
    /// </summary>
    public string TaskPhase { get; init; } = string.Empty;

    /// <summary>Whether <see cref="TaskPhase"/> was typed by a person or inferred automatically.</summary>
    public string TaskCategory { get; init; } = string.Empty;

    /// <summary>
    /// Distinct model-writing transaction names seen during this segment that this add-in
    /// did not author, semicolon-separated. A DELIBERATELY WEAK PROXY, not a measurement of
    /// any specific add-in's usage - it mixes ordinary native Revit commands (a plain "Wall"
    /// or "Move Elements") in with anything a genuine third-party add-in wrote, because
    /// Revit's API exposes no way to tell those apart. Read the raw names to judge for
    /// yourself; do not treat this column as "time spent in an external add-in" on its own.
    /// See <see cref="TimeTrackingService.OnDocumentChanged"/>.
    /// </summary>
    public string ExternalActivity { get; init; } = string.Empty;
}
