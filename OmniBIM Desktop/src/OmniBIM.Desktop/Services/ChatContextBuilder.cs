using System.Text;
using OmniBIM.Desktop.ViewModels;

namespace OmniBIM.Desktop.Services;

/// <summary>
/// Builds the chat panel's system prompt from exactly what's on screen - the same
/// TimeManagementViewModel/ProjectStatusViewModel the dashboard widgets read, so the
/// assistant can never say something the user couldn't already verify by looking at the
/// window behind the chat.
///
/// THE INSTRUCTION NOT TO INVENT NUMBERS IS THE WHOLE POINT OF THIS CLASS. The dashboard this
/// app is modelled on shows a chat panel confidently answering with a "Structure: 95%, MEP:
/// 60%, Façade: 85%" breakdown - numbers with no source anywhere in this application. Nothing
/// stops a language model from producing equally confident, equally fabricated numbers if
/// asked a question this data can't answer - the model has to be told to refuse instead, in
/// the system prompt, every single call.
/// </summary>
public static class ChatContextBuilder
{
    public static string BuildSystemPrompt(MainViewModel dashboard)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            "You are the assistant embedded in OmniBIM by DKSI, a desktop dashboard for an " +
            "architecture/engineering office. Answer questions about the office's projects and " +
            "time tracking using ONLY the data given below - it is everything you have access to.");
        sb.AppendLine();
        sb.AppendLine(
            "CRITICAL: if asked for a number, breakdown, or fact that is not in this data " +
            "(for example a completion percentage broken down by discipline, a budget, a " +
            "deadline, or anything about a specific room, drawing or model element), say " +
            "plainly that it is not tracked in this data rather than estimating or inventing a " +
            "number. A wrong-but-confident answer is worse than 'I don't have that.'");
        sb.AppendLine();
        sb.AppendLine("=== THIS WEEK'S TIME TRACKING ===");
        sb.AppendLine($"Total billable hours this week: {dashboard.TimeManagement.TotalThisWeek:0.0}h ({dashboard.TimeManagement.WeekTrendLabel}).");
        sb.AppendLine($"Distinct people with logged time this week: {dashboard.TimeManagement.TeamMemberCount}.");

        if (dashboard.TimeManagement.TaskPhaseBreakdown.Count > 0)
        {
            sb.AppendLine("Hours by task phase this week:");
            foreach (var phase in dashboard.TimeManagement.TaskPhaseBreakdown)
                sb.AppendLine($"  - {phase.TaskPhase}: {phase.Hours:0.0}h ({phase.Share:P0})");
        }

        if (dashboard.TimeManagement.ProjectBreakdown.Count > 0)
        {
            sb.AppendLine("Hours by project this week (today / this week):");
            foreach (var project in dashboard.TimeManagement.ProjectBreakdown)
                sb.AppendLine($"  - {project.ProjectName}: {project.HoursToday:0.0}h today, {project.HoursThisWeek:0.0}h this week");
        }

        sb.AppendLine();
        sb.AppendLine("=== PROJECT STATUS ===");
        sb.AppendLine(dashboard.ProjectStatus.IsTeamWide
            ? "This list is team-wide (a shared folder is configured)."
            : "IMPORTANT: no shared folder is configured, so this list only covers projects THIS machine has set a status for - say so if asked about the whole office's projects.");

        if (dashboard.ProjectStatus.Projects.Count == 0)
        {
            sb.AppendLine("No project has a status set yet.");
        }
        else
        {
            foreach (var project in dashboard.ProjectStatus.Projects)
            {
                sb.AppendLine(
                    $"  - {project.ProjectName}: status \"{project.Status}\", estimated {project.EstimatedPercent}% " +
                    "(THIS PERCENTAGE IS NOT MEASURED - it's a fixed lookup from the status label only, " +
                    $"call it an estimate if you mention it), last changed by {project.LastChangedBy}.");
            }
        }

        return sb.ToString();
    }
}
