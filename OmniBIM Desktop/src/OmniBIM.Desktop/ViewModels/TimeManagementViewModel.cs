using System.Collections.ObjectModel;
using OmniBIM.Desktop.Services;

namespace OmniBIM.Desktop.ViewModels;

public sealed class DayBarViewModel(string label, double hours, bool isToday)
{
    public string Label { get; } = label;
    public double Hours { get; } = hours;
    public bool IsToday { get; } = isToday;

    /// <summary>0-1, for a bar's width/height binding. Capped at a 10h workday so one long day doesn't flatten the rest.</summary>
    public double FractionOfCap => Math.Min(1.0, Hours / 10.0);
}

/// <summary>
/// Backs the "Time Management" dashboard widget: the Mon-Sun bar row, the total-this-week
/// figure and its vs-last-week delta. Re-populated by MainViewModel whenever the watcher
/// fires or the user changes the week.
/// </summary>
public sealed class TimeManagementViewModel : ObservableObject
{
    public ObservableCollection<DayBarViewModel> Days { get; } = [];

    /// <summary>This week's billable hours grouped by TaskPhase, largest first. Real aggregation, not shown on the compact Home widget - see TimeBreakdownView.</summary>
    public ObservableCollection<TaskPhaseHours> TaskPhaseBreakdown { get; } = [];

    /// <summary>This week's (and today's) hours per project, largest week-total first.</summary>
    public ObservableCollection<ProjectHours> ProjectBreakdown { get; } = [];

    private double _totalThisWeek;
    public double TotalThisWeek { get => _totalThisWeek; private set => SetField(ref _totalThisWeek, value); }

    private double _percentVsLastWeek;
    public double PercentVsLastWeek { get => _percentVsLastWeek; private set => SetField(ref _percentVsLastWeek, value); }

    /// <summary>
    /// "vs last week" text with the arrow and magnitude already resolved, so the view has no
    /// sign logic of its own to get wrong. (A hardcoded green up-arrow in the view previously
    /// showed a 72% DROP in hours as if it were an improvement - this is why that decision
    /// lives here instead.)
    /// </summary>
    private string _weekTrendLabel = string.Empty;
    public string WeekTrendLabel { get => _weekTrendLabel; private set => SetField(ref _weekTrendLabel, value); }

    private bool _weekTrendIsImprovement;
    public bool WeekTrendIsImprovement { get => _weekTrendIsImprovement; private set => SetField(ref _weekTrendIsImprovement, value); }

    private double _approxIdleHoursToday;
    public double ApproxIdleHoursToday { get => _approxIdleHoursToday; private set => SetField(ref _approxIdleHoursToday, value); }

    /// <summary>
    /// Distinct usernames with at least one segment this week - the Home stat tile's "Team
    /// Members" figure. An activity count, not a headcount: only as team-wide as the shared
    /// folder is configured (see TimeTrackingRepository.SharedFolder), and invisible to anyone
    /// on leave the whole week.
    /// </summary>
    private int _teamMemberCount;
    public int TeamMemberCount { get => _teamMemberCount; private set => SetField(ref _teamMemberCount, value); }

    public void Load(TimeTrackingRepository repository, DateTime todayLocal)
    {
        var weekStart = todayLocal.Date.AddDays(-(int)((7 + (todayLocal.DayOfWeek - DayOfWeek.Monday)) % 7));
        var weekEnd = weekStart.AddDays(6);
        var priorWeekStart = weekStart.AddDays(-7);
        var priorWeekEnd = weekStart.AddDays(-1);

        var thisWeekSegments = repository.ReadSegments(weekStart, weekEnd);
        var lastWeekSegments = repository.ReadSegments(priorWeekStart, priorWeekEnd);

        var daily = TimeTrackingAggregator.WeeklyHours(thisWeekSegments, todayLocal);

        Days.Clear();
        foreach (var day in daily)
            Days.Add(new DayBarViewModel(day.Date.ToString("ddd"), day.Hours, day.Date == todayLocal.Date));

        TotalThisWeek = TimeTrackingAggregator.TotalHours(thisWeekSegments);
        PercentVsLastWeek = TimeTrackingAggregator.PercentChange(
            TotalThisWeek, TimeTrackingAggregator.TotalHours(lastWeekSegments));

        WeekTrendIsImprovement = PercentVsLastWeek >= 0;
        var arrow = PercentVsLastWeek switch { > 0 => "▲", < 0 => "▼", _ => "▬" };
        WeekTrendLabel = $"{arrow} {Math.Abs(PercentVsLastWeek):0}% vs last week";

        var todaySegments = repository.ReadSegments(todayLocal, todayLocal);
        var todaySessions = repository.ReadSessions(todayLocal, todayLocal);
        var (_, idleMinutes) = TimeTrackingAggregator.ActiveVsIdle(todaySegments, todaySessions);
        ApproxIdleHoursToday = idleMinutes / 60.0;

        TaskPhaseBreakdown.Clear();
        foreach (var phase in TimeTrackingAggregator.HoursByTaskPhase(thisWeekSegments))
            TaskPhaseBreakdown.Add(phase);

        ProjectBreakdown.Clear();
        foreach (var project in TimeTrackingAggregator.HoursByProject(thisWeekSegments, todayLocal))
            ProjectBreakdown.Add(project);

        TeamMemberCount = TimeTrackingAggregator.ActiveUserCount(thisWeekSegments);
    }
}
