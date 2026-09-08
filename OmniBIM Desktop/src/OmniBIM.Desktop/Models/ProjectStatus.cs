namespace OmniBIM.Desktop.Models;

/// <summary>
/// One record from project-status.json: the current status of a project, as set by whoever
/// last changed it. Mirrors Cda.Revit.Addin.TimeTracking.ProjectStatusRecord.
///
/// NOTE: this store holds a STATUS LABEL (one of nine fixed strings - see
/// ProjectStatusOptions.Values), not a completion percentage. The OmniBIM mock dashboard
/// shows per-project progress bars (78%, 62%, ...); nothing in the Revit add-in's data
/// produces that number today. Either derive a rough proxy (e.g. map each status to a fixed
/// percentage) and label it clearly as an approximation, or drop the percentage until a real
/// source for it exists - do not invent precision the source data doesn't have.
/// </summary>
public sealed class ProjectStatus
{
    public string ProjectName { get; init; } = string.Empty;
    public string ProjectNumber { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime DateStarted { get; init; }
    public string LastChangedBy { get; init; } = string.Empty;
    public DateTime LastChangedUtc { get; init; }

    public string ProjectKey => ProjectNumber.Length > 0 ? ProjectNumber : ProjectName;
}

public static class ProjectStatusOptions
{
    /// <summary>Exact strings, in the order Cda.Revit.Addin.TimeTracking.ProjectStatusStore.Statuses uses them.</summary>
    public static readonly IReadOnlyList<string> Values =
    [
        "On-going", "EM done", "Ready for DDG", "Pending", "For QA DDG",
        "For QA EM", "On-hold", "Done", "For Classification",
    ];
}
