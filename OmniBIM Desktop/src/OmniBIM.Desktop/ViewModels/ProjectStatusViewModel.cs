using System.Collections.ObjectModel;
using OmniBIM.Desktop.Models;
using OmniBIM.Desktop.Services;

namespace OmniBIM.Desktop.ViewModels;

/// <summary>
/// One row in the Project Status widget. Deliberately carries the STATUS LABEL only, not a
/// completion percentage - project-status.json has no percentage field (see
/// Models.ProjectStatus's remarks), and a widget that made one up would misrepresent every
/// project it lists. If a percentage is wanted later, it needs a real source; this row is
/// ready to carry one the day that exists.
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
    }
}
