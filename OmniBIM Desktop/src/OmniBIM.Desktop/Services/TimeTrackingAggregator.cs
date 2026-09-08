using OmniBIM.Desktop.Models;

namespace OmniBIM.Desktop.Services;

public sealed record DayHours(DateTime Date, double Hours);

public sealed record ProjectHours(string ProjectKey, string ProjectName, double HoursToday, double HoursThisWeek);

public sealed record TaskPhaseHours(string TaskPhase, double Hours, double Share);

/// <summary>
/// Pure query functions over TimeSegment/RevitSession, for the dashboard widgets. Nothing
/// here reads a file - it's given the rows the repository already read, so it's cheap to
/// re-run on every watcher tick and trivial to unit test without a filesystem.
/// </summary>
public static class TimeTrackingAggregator
{
    /// <summary>
    /// Hours per day for a Monday-to-Sunday week, for the weekly bar chart. Days with no
    /// segments come back as 0, not missing, so the chart always has all seven bars.
    /// </summary>
    public static IReadOnlyList<DayHours> WeeklyHours(IReadOnlyList<TimeSegment> segments, DateTime weekOfLocal)
    {
        var monday = StartOfWeek(weekOfLocal);

        var byDay = segments
            .GroupBy(s => s.Started.Date)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.DurationMinutes) / 60.0);

        return Enumerable.Range(0, 7)
            .Select(offset => monday.AddDays(offset))
            .Select(day => new DayHours(day, byDay.GetValueOrDefault(day)))
            .ToList();
    }

    public static double TotalHours(IReadOnlyList<TimeSegment> segments) =>
        segments.Sum(s => s.DurationMinutes) / 60.0;

    public static double PercentChange(double current, double previous) =>
        previous <= 0 ? 0 : (current - previous) / previous * 100.0;

    /// <summary>
    /// Active (billable) minutes vs. an APPROXIMATION of idle minutes for a day: session
    /// wall-clock time minus segment billable time. This is bounded to zero and is only ever
    /// an approximation, because the two ledgers carry no shared id to join on (see
    /// RevitSession's remarks) - a person who ran two overlapping sessions on two machines
    /// under one login, or who closed Revit uncleanly, will skew this number. Surface it
    /// labelled as an estimate; do not present it as a measured idle time.
    /// </summary>
    public static (double ActiveMinutes, double ApproxIdleMinutes) ActiveVsIdle(
        IReadOnlyList<TimeSegment> segments, IReadOnlyList<RevitSession> sessions)
    {
        var active = segments.Sum(s => s.DurationMinutes);
        var open = sessions.Sum(s => s.DurationMinutes);
        return (active, Math.Max(0, open - active));
    }

    /// <summary>Billable hours grouped by TaskPhase (Construction Documentation, Family Creation, ...), largest first.</summary>
    public static IReadOnlyList<TaskPhaseHours> HoursByTaskPhase(IReadOnlyList<TimeSegment> segments)
    {
        var totalMinutes = segments.Sum(s => s.DurationMinutes);
        if (totalMinutes <= 0) return [];

        return segments
            .GroupBy(s => string.IsNullOrWhiteSpace(s.TaskPhase) ? "(unspecified)" : s.TaskPhase)
            .Select(g =>
            {
                var minutes = g.Sum(s => s.DurationMinutes);
                return new TaskPhaseHours(g.Key, minutes / 60.0, minutes / totalMinutes);
            })
            .OrderByDescending(t => t.Hours)
            .ToList();
    }

    /// <summary>Today's and this week's hours per project, for a per-project breakdown list.</summary>
    public static IReadOnlyList<ProjectHours> HoursByProject(
        IReadOnlyList<TimeSegment> weekSegments, DateTime todayLocal)
    {
        return weekSegments
            .GroupBy(s => s.ProjectKey)
            .Select(g => new ProjectHours(
                ProjectKey: g.Key,
                ProjectName: g.First().ProjectName,
                HoursToday: g.Where(s => s.Started.Date == todayLocal.Date).Sum(s => s.DurationMinutes) / 60.0,
                HoursThisWeek: g.Sum(s => s.DurationMinutes) / 60.0))
            .OrderByDescending(p => p.HoursThisWeek)
            .ToList();
    }

    /// <summary>
    /// Distinct usernames seen in the shared ledger over the window - the closest available
    /// proxy for "team members active recently". Not a headcount: someone on leave the whole
    /// window is invisible to it, same as any activity-based count.
    /// </summary>
    public static int ActiveUserCount(IReadOnlyList<TimeSegment> segments) =>
        segments.Select(s => s.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>Projects whose latest status is not "Done" or "On-hold".</summary>
    public static int ActiveProjectCount(IReadOnlyList<ProjectStatus> statuses) =>
        statuses.Count(p => p.Status is not ("Done" or "On-hold"));

    private static DateTime StartOfWeek(DateTime date)
    {
        var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.Date.AddDays(-diff);
    }
}
