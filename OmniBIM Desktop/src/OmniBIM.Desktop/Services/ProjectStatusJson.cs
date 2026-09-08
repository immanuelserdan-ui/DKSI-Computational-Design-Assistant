using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniBIM.Desktop.Services;

/// <summary>
/// Reads project-status.json, written by Cda.Revit.Addin.TimeTracking.ProjectStatusStore:
/// one JSON object, keyed by project number-or-name, one record per project, last-write-wins.
/// Read-only here for the same reason as the CSV readers - OmniBIM never edits this file.
/// </summary>
public static class ProjectStatusJson
{
    private sealed class Row
    {
        public string ProjectName { get; set; } = string.Empty;
        public string ProjectNumber { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime DateStarted { get; set; }
        public string LastChangedBy { get; set; } = string.Empty;
        public DateTime LastChangedUtc { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Reads with FileShare.ReadWrite, since the add-in may be writing this file at the same moment.</summary>
    public static IReadOnlyList<Models.ProjectStatus> ReadAll(string path)
    {
        if (!File.Exists(path)) return [];

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var map = JsonSerializer.Deserialize<Dictionary<string, Row>>(stream, Options);
            if (map is null) return [];

            return map.Values.Select(r => new Models.ProjectStatus
            {
                ProjectName = r.ProjectName,
                ProjectNumber = r.ProjectNumber,
                Status = r.Status,
                DateStarted = r.DateStarted,
                LastChangedBy = r.LastChangedBy,
                LastChangedUtc = r.LastChangedUtc,
            }).ToList();
        }
        catch
        {
            // A half-written file (mid-swap on the add-in side) is a transient read miss,
            // not an error worth surfacing - the next watcher tick will pick it up.
            return [];
        }
    }
}
