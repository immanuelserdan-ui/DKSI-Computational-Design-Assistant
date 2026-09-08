using System.IO;
using System.Text.Json;

namespace OmniBIM.Desktop.Services;

/// <summary>
/// Where the Revit add-in's time-tracking files live, from OmniBIM's side of the fence.
///
/// LOCAL FOLDER is fixed and always exists once the add-in has logged anything on this
/// machine: %LOCALAPPDATA%\Cda\RevitAddin\timelog. It holds ONE person's data - whoever is
/// logged into this Windows session - because that is the add-in's own local ledger.
///
/// SHARED FOLDER is what makes the team-wide dashboard widgets (Team Members, Active
/// Projects, per-project status) possible at all: it is the one place every user's CSVs and
/// project-status.json land together. It is optional and configured per machine by the
/// add-in's settings (TimeTrackingSettings.SharedFolder) - if nobody has set it, or OmniBIM
/// is running on a machine that has never opened Revit, there is no team data to read and
/// the dashboard must say so rather than pretend the shared folder is empty of team members.
///
/// RESOLUTION ORDER for the shared folder:
///   1. OmniBIM's own override (Settings > Shared time-tracking folder), if the user set one.
///   2. The add-in's OWN user settings file on this machine, if it exists and has one set.
///   3. None - local-only mode.
/// (1) wins over (2) so a machine that runs OmniBIM but not Revit - e.g. a PM's laptop - can
/// still be pointed at the team folder without needing the add-in installed at all.
/// </summary>
public static class TimeTrackingLocations
{
    public static string LocalTimeLogFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cda", "RevitAddin", "timelog");

    private static string AddinUserSettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cda", "RevitAddin", "time-tracking.json");

    public static string? ResolveSharedFolder(OmniBimSettings ownSettings)
    {
        if (!string.IsNullOrWhiteSpace(ownSettings.SharedTimeTrackingFolder))
            return ownSettings.SharedTimeTrackingFolder;

        return ReadAddinSharedFolder();
    }

    private static string? ReadAddinSharedFolder()
    {
        try
        {
            if (!File.Exists(AddinUserSettingsPath)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(AddinUserSettingsPath));
            if (!doc.RootElement.TryGetProperty("SharedFolder", out var value)) return null;

            var folder = value.GetString();
            return string.IsNullOrWhiteSpace(folder) ? null : folder;
        }
        catch
        {
            // A settings file this app does not own, in a format it does not control - if it
            // cannot be read, fall back to local-only rather than fail the whole app.
            return null;
        }
    }
}
