using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.TimeTracking;

/// <summary>
/// One record: where a project stands, set by a person, read by everyone who opens it.
///
/// A record on this project, not on this row of the time log - unlike every other
/// TimeTracking field, this is not a fact about a segment or a session, it is the current
/// state of the PROJECT, and it must be the same answer no matter who asks or which
/// machine they ask from.
/// </summary>
public sealed class ProjectStatusRecord
{
    public string ProjectName { get; set; } = string.Empty;
    public string ProjectNumber { get; set; } = string.Empty;

    /// <summary>One of <see cref="ProjectStatusStore.Statuses"/>. Stored as plain text, not
    /// an enum ordinal, so a status added in a later version does not corrupt older rows and
    /// a hand-edited value is at worst unrecognised rather than unreadable.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Stamped once, the first time this project key is ever written, and never touched
    /// again - see <see cref="ProjectStatusStore.SetStatus"/>.
    /// </summary>
    public DateTime DateStarted { get; set; }

    public string LastChangedBy { get; set; } = string.Empty;
    public DateTime LastChangedUtc { get; set; }
}

/// <summary>
/// The shared status board: one JSON file, one record per project, last-write-wins.
///
/// WHY JSON AND NOT ANOTHER ROW IN THE CSV LEDGER: the ledger is an append-only EVENT log -
/// every write is a new fact that happened at a point in time, and nothing already written
/// is ever revised. Status is the opposite: one current value per project that gets
/// OVERWRITTEN in place every time someone changes it. Forcing that into an append-only file
/// means either scanning the whole ledger for the latest row per project every time the
/// window opens, or maintaining a second index anyway - a keyed JSON document says exactly
/// what it means and costs one read.
///
/// LOCATION FOLLOWS THE SAME RULE AS THE LEDGER: the shared folder when one is configured
/// (this is the whole point - status set on one machine must be visible from another), a
/// local-only file otherwise. Unlike the ledger, there is no local+shared double-write: a
/// per-project status has exactly one authority, and writing a local "shadow" copy that
/// could disagree with the shared one is worse than just not having an offline copy.
/// </summary>
internal static class ProjectStatusStore
{
    /// <summary>
    /// The nine values the brief specifies, in the order they were given. Exact strings -
    /// whatever consumes the shared file (a dashboard, a Power BI report) matches against
    /// these literally.
    /// </summary>
    public static readonly IReadOnlyList<string> Statuses =
    [
        "On-going", "EM done", "Ready for DDG", "Pending", "For QA DDG",
        "For QA EM", "On-hold", "Done", "For Classification",
    ];

    private const string FileName = "project-status.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly object Gate = new();

    /// <summary>The key a project is filed under: its number, or its name when there is none.</summary>
    public static string KeyFor(WorkContext context) =>
        context.ProjectNumber.Length > 0 ? context.ProjectNumber : context.ProjectName;

    private static string PathFor(TimeTrackingSettings settings) => Path.Combine(
        string.IsNullOrWhiteSpace(settings.SharedFolder) ? TimeLogStore.LocalFolder : settings.SharedFolder,
        FileName);

    /// <summary>This project's record, or null when nobody has ever set a status for it.</summary>
    public static ProjectStatusRecord? Get(WorkContext context, TimeTrackingSettings settings)
    {
        var key = KeyFor(context);
        if (key.Length == 0) return null;

        lock (Gate)
        {
            var all = ReadAll(settings);
            return all.TryGetValue(key, out var record) ? record : null;
        }
    }

    /// <summary>
    /// Sets this project's status. DateStarted is stamped the first time this key is ever
    /// written and preserved on every write after that - it answers "when did anyone first
    /// start tracking this project", not "when was the status last changed".
    /// </summary>
    public static void SetStatus(WorkContext context, string status, string changedBy, TimeTrackingSettings settings)
    {
        var key = KeyFor(context);
        if (key.Length == 0)
        {
            Log.Warn("Project status: no project number or name to file this under; not saved.");
            return;
        }

        lock (Gate)
        {
            var all = ReadAll(settings);

            var existing = all.TryGetValue(key, out var found) ? found : null;

            all[key] = new ProjectStatusRecord
            {
                ProjectName = context.ProjectName,
                ProjectNumber = context.ProjectNumber,
                Status = status,
                DateStarted = existing?.DateStarted ?? DateTime.Now,
                LastChangedBy = changedBy,
                LastChangedUtc = DateTime.UtcNow,
            };

            WriteAll(all, settings);
        }
    }

    // ------------------------------------------------------------------ file plumbing

    private static Dictionary<string, ProjectStatusRecord> ReadAll(TimeTrackingSettings settings)
    {
        var path = PathFor(settings);

        try
        {
            if (!File.Exists(path)) return [];

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var map = JsonSerializer.Deserialize<Dictionary<string, ProjectStatusRecord>>(stream, Json);
            return map ?? [];
        }
        catch (Exception ex)
        {
            Log.Warn($"Project status: '{path}' could not be read, treating it as empty: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Writes the whole file back. A short retry on a sharing violation - two people saving
    /// a status in the same second is rare but not impossible - and a temp-file-and-swap so
    /// a crash mid-write cannot leave the board half-written.
    /// </summary>
    private static void WriteAll(Dictionary<string, ProjectStatusRecord> all, TimeTrackingSettings settings)
    {
        var path = PathFor(settings);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                var temp = path + $".{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(all, Json));

                // File.Move with overwrite is the atomic swap; File.Replace needs the
                // destination to already exist, which is exactly the case this cannot
                // assume for the very first status anyone ever sets.
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(150 * (attempt + 1));
            }
            catch (Exception ex)
            {
                Log.Warn($"Project status: '{path}' could not be saved: {ex.Message}");
                return;
            }
        }

        Log.Warn($"Project status: '{path}' stayed locked; the status was not saved.");
    }
}
