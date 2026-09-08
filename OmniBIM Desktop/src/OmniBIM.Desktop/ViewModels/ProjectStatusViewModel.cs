using System.Collections.ObjectModel;
using OmniBIM.Desktop.Models;
using OmniBIM.Desktop.Services;

namespace OmniBIM.Desktop.ViewModels;

/// <summary>
/// One row in the Project Status widget. Carries the STATUS LABEL (the real, on-disk value)
/// and an EstimatedPercent (see ProjectStatusPercentEstimates) - a fixed lookup, not a
/// measurement, kept clearly separate and always shown as "Est." so it is never mistaken for
/// real completion data. project-status.json itself still has no percentage field.
/// </summary>
public sealed class ProjectStatusRowViewModel(ProjectStatus status)
{
    public string ProjectName { get; } = status.ProjectName.Length > 0 ? status.ProjectName : status.ProjectNumber;
    public string ProjectNumber { get; } = status.ProjectNumber;

    /// <summary>
    /// Exactly what's on disk. Not restricted to ProjectStatusOptions.Values - a status typed
    /// by hand into the JSON, or written by an older/newer add-in version, must still show up
    /// rather than vanish from the board.
    /// </summary>
    public string Status { get; } = status.Status.Length > 0 ? status.Status : "(no status set)";

    /// <summary>A rough, non-measured estimate derived only from Status - see ProjectStatusPercentEstimates.</summary>
    public int EstimatedPercent { get; } = ProjectStatusPercentEstimates.For(status.Status);

    public string LastChangedBy { get; } = status.LastChangedBy;
    public DateTime LastChangedLocal { get; } = status.LastChangedUtc == default
        ? default
        : status.LastChangedUtc.ToLocalTime();

    public bool IsKnownStatus { get; } = ProjectStatusOptions.Values.Contains(status.Status);
}

/// <summary>
/// Backs the "Project Status" dashboard widget. Reads project-status.json through the
/// repository - the same last-write-wins board the add-in's own status dropdown writes to.
/// </summary>
public sealed class ProjectStatusViewModel : ObservableObject
{
    public ObservableCollection<ProjectStatusRowViewModel> Projects { get; } = [];

    private int _activeCount;
    public int ActiveCount { get => _activeCount; private set => SetField(ref _activeCount, value); }

    /// <summary>Projects whose status is exactly "On-going" - the Home stat tile's "In Progress" figure.</summary>
    private int _onGoingCount;
    public int OnGoingCount { get => _onGoingCount; private set => SetField(ref _onGoingCount, value); }

    private bool _isTeamWide;

    /// <summary>
    /// False when no shared folder is configured: the board then reflects only whichever
    /// projects THIS machine has set a status for, not the whole team's. The widget should
    /// say so rather than presenting a partial board as if it were complete.
    /// </summary>
    public bool IsTeamWide { get => _isTeamWide; private set => SetField(ref _isTeamWide, value); }

    public void Load(TimeTrackingRepository repository)
    {
        IsTeamWide = repository.SharedFolder is { Length: > 0 };

        var statuses = repository.ReadProjectStatuses()
            .OrderByDescending(s => s.LastChangedUtc)
            .ToList();

        Projects.Clear();
        foreach (var status in statuses) Projects.Add(new ProjectStatusRowViewModel(status));

        ActiveCount = TimeTrackingAggregator.ActiveProjectCount(statuses);
        OnGoingCount = statuses.Count(s => s.Status == "On-going");
    }
}
