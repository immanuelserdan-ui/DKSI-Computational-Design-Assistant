using System.IO;
using System.Text;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.TimeTracking;

/// <summary>
/// The ledger. Every completed entry — automated or manual — is appended here the moment
/// it closes, which is what bounds the damage of a Revit crash to the one segment that was
/// still open.
///
/// TWO DESTINATIONS, ONE FORMAT:
///
///   LOCAL   %LOCALAPPDATA%\Cda\RevitAddin\timelog\timelog-YYYY-MM.csv
///           The authority. Always written, never on a network, so tracking survives a
///           dropped VPN or a server going down.
///
///   SHARED  &lt;SharedFolder&gt;\timelog-&lt;username&gt;-YYYY-MM.csv
///           Written as well, when a folder is configured.
///
/// ONE SHARED FILE PER PERSON, NOT ONE FOR THE OFFICE. A single company-wide CSV appended
/// to by fifteen Revit sessions is a torn-write generator: Windows only guarantees an
/// atomic append under 4 KB on a local volume, and guarantees nothing at all over SMB.
/// Per-user files remove the contention rather than trying to lock around it, and the
/// consumer (Power BI, Excel, a Python script) globs the folder — which it has to do
/// anyway to pick up new joiners.
/// </summary>
internal static class TimeLogStore
{
    public static string LocalFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cda", "RevitAddin", "timelog");

    /// <summary>
    /// The durability plumbing (local+shared append, persisted retry queue, header
    /// widening) lives in <see cref="AppendOnlyCsvLog"/> now, shared with
    /// <see cref="SessionLogStore"/>. "pending-writes.json" keeps its original name so an
    /// existing queue left over from before the split is picked up rather than orphaned.
    /// </summary>
    private static readonly AppendOnlyCsvLog Ledger = new(LocalFolder, "pending-writes.json");

    public static string LocalFileFor(DateTime local) =>
        Path.Combine(LocalFolder, $"timelog-{local:yyyy-MM}.csv");

    public static string? SharedFileFor(DateTime local, TimeTrackingSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SharedFolder)) return null;

        var user = Sanitize(Environment.UserName);
        return Path.Combine(settings.SharedFolder, $"timelog-{user}-{local:yyyy-MM}.csv");
    }

    /// <summary>
    /// Appends one entry. Never throws: losing a row is bad, but taking Revit down over a
    /// timesheet is worse, and the caller is usually an event handler.
    /// </summary>
    public static void Append(TimeEntry entry, TimeTrackingSettings settings)
    {
        var separator = settings.SeparatorChar;
        var header = TimeLogCsv.Header(separator);
        var line = TimeLogCsv.Format(entry, separator);

        Ledger.Append(LocalFileFor(entry.Started), SharedFileFor(entry.Started, settings), header, line);
    }

    /// <summary>
    /// Entries between two local dates, inclusive of both days. Reads the LOCAL ledger only
    /// — the shared copy is an output, and reading other people's time back into this UI is
    /// not something a time tracker should quietly do.
    /// </summary>
    public static List<TimeEntry> Read(DateTime fromLocal, DateTime toLocal, TimeTrackingSettings settings)
    {
        var from = fromLocal.Date;
        var to = toLocal.Date.AddDays(1).AddTicks(-1);

        var entries = new List<TimeEntry>();
        var separator = settings.SeparatorChar;

        // Walk month by month rather than globbing, so a folder with three years in it
        // opens one file for a "today" query.
        for (var month = new DateTime(from.Year, from.Month, 1); month <= to; month = month.AddMonths(1))
        {
            var path = LocalFileFor(month);
            if (!File.Exists(path)) continue;

            try
            {
                foreach (var line in AppendOnlyCsvLog.ReadLinesShared(path))
                {
                    var entry = TimeLogCsv.ParseFlexible(line, separator);
                    if (entry is not null && entry.Started >= from && entry.Started <= to)
                        entries.Add(entry);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Time tracking: '{path}' could not be read: {ex.Message}");
            }
        }

        entries.Sort((a, b) => a.Started.CompareTo(b.Started));
        return entries;
    }

    /// <summary>Writes a standalone CSV for a date range. Returns the number of rows.</summary>
    public static int Export(string path, DateTime fromLocal, DateTime toLocal, TimeTrackingSettings settings)
    {
        var entries = Read(fromLocal, toLocal, settings);
        var separator = settings.SeparatorChar;

        var sb = new StringBuilder();
        sb.AppendLine(TimeLogCsv.Header(separator));
        foreach (var entry in entries) sb.AppendLine(TimeLogCsv.Format(entry, separator));

        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        // BOM, so Danish characters in a room or project name survive a double-click.
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        return entries.Count;
    }

    // ------------------------------------------------------------------ file plumbing

    /// <summary>
    /// Writes a probe file into <paramref name="folder"/>, reads it back and deletes it.
    ///
    /// The point is to make a wrong shared path fail AT CONFIGURATION TIME. Without it the
    /// only symptom of a mistyped UNC is a warning in a log nobody reads, discovered at the
    /// end of the month when the timesheets are not there. Existence of the folder is not
    /// enough to test - a read-only share passes that and still loses every entry.
    /// </summary>
    public static (bool Ok, string Message) TestPath(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return (false, "No folder is set, so entries are written locally only.");

        var probe = Path.Combine(folder, $"dksi-write-test-{Guid.NewGuid():N}.tmp");

        try
        {
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                Log.Info($"Time tracking: created the shared folder '{folder}'.");
            }

            var stamp = $"DKSI write test {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            File.WriteAllText(probe, stamp);

            if (File.ReadAllText(probe) != stamp)
                return (false, $"'{folder}' accepted a file but read it back wrong. Do not use it.");

            File.Delete(probe);

            return (true,
                $"'{folder}' is writable.\n\nYour entries will also be appended to:\n" +
                $"{SharedFileFor(DateTime.Now, new TimeTrackingSettings { SharedFolder = folder })}");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, $"You do not have permission to write to '{folder}'. Ask for write access, " +
                           "or pick a different folder - entries would be kept locally and never arrive.");
        }
        catch (DirectoryNotFoundException)
        {
            return (false, $"'{folder}' does not exist and could not be created. Check the spelling, " +
                           "and that you are connected to the network.");
        }
        catch (IOException ex)
        {
            return (false, $"'{folder}' could not be written to: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"'{folder}' could not be tested: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { /* best effort */ }
        }
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "user" : cleaned;
    }
}
