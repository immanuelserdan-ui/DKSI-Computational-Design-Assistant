namespace OmniBIM.Desktop.Models;

/// <summary>
/// One row read back from a Revit add-in session ledger (sessionlog-*.csv): one Revit
/// session on one project, from document-open to document-close.
///
/// Mirrors Cda.Revit.Addin.TimeTracking.SessionEntry field-for-field. Note there is no id
/// linking a session to the TimeSegment rows within it - the two ledgers are independent by
/// design (see SessionEntry's own remarks upstream), so "active vs. idle" can only be
/// approximated by comparing sums over a shared time window, never joined row-to-row.
/// </summary>
public sealed class RevitSession
{
    public required DateTime SessionStart { get; init; }
    public required DateTime SessionEnd { get; init; }
    public required string Username { get; init; }

    public string ProjectName { get; init; } = string.Empty;
    public string ProjectNumber { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public DateTime? DateLastModified { get; init; }
    public string Machine { get; init; } = string.Empty;

    public double DurationMinutes => (SessionEnd - SessionStart).TotalMinutes;

    public string ProjectKey => ProjectNumber.Length > 0 ? ProjectNumber : ProjectName;
}
