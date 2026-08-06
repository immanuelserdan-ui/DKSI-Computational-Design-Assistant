using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Qa;

/// <summary>How much a finding should worry the person reading the grid.</summary>
public enum QaSeverity
{
    /// <summary>Worth knowing, not necessarily wrong.</summary>
    Info = 0,

    /// <summary>Probably wrong; needs a human to look.</summary>
    Warning = 1,

    /// <summary>Wrong. Quantities taken off this are understated.</summary>
    Problem = 2,
}

/// <summary>
/// One row in a QA results grid.
///
/// WHY ElementId AND UniqueId
///   The grid holds these while the user works in the model, and an id can go stale under
///   it: undo a delete, reload a workset, and the element behind a row is gone. Every jump
///   therefore re-resolves through the document and reports a dead row rather than throwing.
///   UniqueId is carried as well because it is the only identity that survives the model
///   being copied, e-transmitted or upgraded - which is what makes an exported CSV of these
///   findings still meaningful next week.
/// </summary>
public sealed class QaFinding
{
    public required QaSeverity Severity { get; init; }

    /// <summary>Short, sortable category - "No skirting", "Gap at corner".</summary>
    public required string Kind { get; init; }

    /// <summary>The element to jump to. The wall for a missing run, the board for a gap.</summary>
    public required ElementId TargetId { get; init; }

    public string? TargetUniqueId { get; init; }

    /// <summary>What the target is, for someone reading the grid without clicking.</summary>
    public required string TargetDescription { get; init; }

    public string Room { get; init; } = string.Empty;

    public string Level { get; init; } = string.Empty;

    /// <summary>Metres. Zero where length is not the point of the finding.</summary>
    public double Length { get; init; }

    /// <summary>The sentence that tells someone what to do about it.</summary>
    public required string Detail { get; init; }

    // ---- display shims, so the ListView can bind without a converter ----

    public string SeverityText => Severity switch
    {
        QaSeverity.Problem => "Problem",
        QaSeverity.Warning => "Check",
        _ => "Info",
    };

    public string IdText => TargetId == ElementId.InvalidElementId
        ? "-"
        : TargetId.Value.ToString();

    public string LengthText => Length > 0 ? $"{Length:0.00} m" : string.Empty;

    /// <summary>Fields for the CSV export, in the order the header declares them.</summary>
    public IReadOnlyList<string> ToCsvRow() =>
    [
        SeverityText,
        Kind,
        IdText,
        TargetUniqueId ?? string.Empty,
        TargetDescription,
        Room,
        Level,
        Length > 0 ? Length.ToString("0.000") : string.Empty,
        Detail,
    ];

    public static IReadOnlyList<string> CsvHeader() =>
    [
        "Severity", "Kind", "Element Id", "Unique Id", "Element", "Room", "Level", "Length (m)", "Detail",
    ];
}

/// <summary>Everything one check run produced: the rows, plus what it did and did not look at.</summary>
public sealed class QaScanResult
{
    public required IReadOnlyList<QaFinding> Findings { get; init; }

    /// <summary>
    /// Free text describing the scope of the run - rooms examined, rooms skipped and why,
    /// how many boards were found in the model at all.
    ///
    /// This is not decoration. An empty findings list means one of two opposite things:
    /// everything is correct, or the check looked at nothing. Without the scope lines those
    /// are indistinguishable, and a QA tool that cannot tell them apart is dangerous.
    /// </summary>
    public required IReadOnlyList<string> Scope { get; init; }

    public int RoomsExamined { get; init; }

    public int RoomsSkipped { get; init; }

    public int SweepsFound { get; init; }
}
