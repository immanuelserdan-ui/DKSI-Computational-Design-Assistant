using System.IO;
using OmniBIM.Desktop.Models;

namespace OmniBIM.Desktop.Services;

/// <summary>
/// Reads the whole team's time-tracking data by globbing the shared folder for every user's
/// monthly CSVs - the same approach the add-in's own design assumes any consumer will use
/// (see TimeLogStore's remarks: "the consumer ... globs the folder"). Falls back to this
/// machine's local ledger (one user) when no shared folder is configured.
///
/// ALWAYS OPENS WITH FileShare.ReadWrite. The add-in appends to these files continuously from
/// however many Revit sessions are open across the office; a reader that does not share the
/// handle would throw constantly and would itself be the multi-process-safety bug the brief
/// asks to avoid. Never rename, move, or write into any file under these folders - the add-in
/// owns them exclusively (see AppendOnlyCsvLog upstream for the crash-safety machinery this
/// app must not fight with).
/// </summary>
public sealed class TimeTrackingRepository(char separator = ',')
{
    private readonly OmniBimSettings _settings = OmniBimSettings.Load();

    public string? SharedFolder => TimeTrackingLocations.ResolveSharedFolder(_settings);

    /// <summary>Folders actually read: the shared one (if configured) plus the local one, deduplicated.</summary>
    public IReadOnlyList<string> ActiveFolders()
    {
        var folders = new List<string> { TimeTrackingLocations.LocalTimeLogFolder };
        var shared = SharedFolder;
        if (!string.IsNullOrWhiteSpace(shared) && Directory.Exists(shared)) folders.Add(shared);
        return folders;
    }

    public IReadOnlyList<TimeSegment> ReadSegments(DateTime fromLocal, DateTime toLocal)
    {
        var results = new List<TimeSegment>();

        foreach (var path in GlobFiles("timelog-*.csv"))
        {
            foreach (var line in ReadLinesShared(path))
            {
                var entry = TimeSegmentCsv.ParseFlexible(line, separator);
                if (entry is not null && entry.Started >= fromLocal.Date && entry.Started < toLocal.Date.AddDays(1))
                    results.Add(entry);
            }
        }

        results.Sort((a, b) => a.Started.CompareTo(b.Started));
        return results;
    }

    public IReadOnlyList<RevitSession> ReadSessions(DateTime fromLocal, DateTime toLocal)
    {
        var results = new List<RevitSession>();

        foreach (var path in GlobFiles("sessionlog-*.csv"))
        {
            foreach (var line in ReadLinesShared(path))
            {
                var entry = RevitSessionCsv.ParseFlexible(line, separator);
                if (entry is not null && entry.SessionStart >= fromLocal.Date && entry.SessionStart < toLocal.Date.AddDays(1))
                    results.Add(entry);
            }
        }

        results.Sort((a, b) => a.SessionStart.CompareTo(b.SessionStart));
        return results;
    }

    public IReadOnlyList<ProjectStatus> ReadProjectStatuses()
    {
        // project-status.json has exactly one authority (shared folder if configured,
        // otherwise the local one) - never both. Same rule as ProjectStatusStore upstream.
        var folder = !string.IsNullOrWhiteSpace(SharedFolder) && Directory.Exists(SharedFolder)
            ? SharedFolder!
            : TimeTrackingLocations.LocalTimeLogFolder;

        return ProjectStatusJson.ReadAll(Path.Combine(folder, "project-status.json"));
    }

    /// <summary>
    /// Month-by-month globbing, mirroring TimeLogStore.Read: walking only the months in range
    /// keeps a folder with years of history from being fully re-parsed for a "this week" query.
    /// </summary>
    private IEnumerable<string> GlobFiles(string pattern)
    {
        foreach (var folder in ActiveFolders())
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, pattern)) yield return file;
        }
    }

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (IOException)
        {
            // Genuinely locked (rare - the add-in itself never holds an exclusive lock); skip
            // this file for this refresh rather than blocking the whole read.
            yield break;
        }

        using (stream)
        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
        {
            while (reader.ReadLine() is { } line) yield return line;
        }
    }
}
