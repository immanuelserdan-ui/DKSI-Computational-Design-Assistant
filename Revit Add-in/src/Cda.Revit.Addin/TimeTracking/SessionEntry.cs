namespace Cda.Revit.Addin.TimeTracking;

/// <summary>
/// One Revit session on one project: the span from <see cref="DocumentOpened"/> to the
/// document closing (or Revit shutting down with it still open), independent of how many
/// view-switch segments <see cref="TimeEntry"/> broke that span into.
///
/// A SEPARATE ledger from the segment log on purpose. "How long was this session open" and
/// "which view was the user looking at for which minute" are different questions with
/// different granularity - folding session start/end onto every segment row would either
/// duplicate the same two timestamps across dozens of rows, or force the segment reader to
/// special-case "the first/last row of a session", both worse than a second small file.
/// </summary>
public sealed class SessionEntry
{
    /// <summary>Local wall-clock moment the document was opened.</summary>
    public required DateTime SessionStart { get; init; }

    /// <summary>
    /// Local wall-clock moment the document closed, or the moment Revit shut down with it
    /// still open. Null while the session this row describes is still running - a row is
    /// only ever written once this is known.
    /// </summary>
    public required DateTime SessionEnd { get; init; }

    public required string Username { get; init; }

    public string ProjectName { get; init; } = string.Empty;

    public string ProjectNumber { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    /// <summary>
    /// The document's own last-saved timestamp, read at the moment the session opened -
    /// i.e. "how stale was this file when this session started", not a live-updating value.
    /// A central model reads this off the local file Revit actually opened, same as any
    /// other file property; a local, un-synced copy is exactly what a modeller most needs
    /// flagged.
    /// </summary>
    public DateTime? DateLastModified { get; init; }

    public string Machine { get; init; } = Environment.MachineName;
}
