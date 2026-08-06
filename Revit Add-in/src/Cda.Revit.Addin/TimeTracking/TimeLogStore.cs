using System.IO;
using System.Text;
using System.Text.Json;
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

    /// <summary>One line that could not be written yet, and where it was going.</summary>
    private sealed class PendingWrite
    {
        public string Path { get; set; } = string.Empty;
        public string Header { get; set; } = string.Empty;
        public string Line { get; set; } = string.Empty;
    }

    /// <summary>
    /// Lines that could not be written — a locked file, a network folder that went away.
    /// Retried on the next append, so a blip costs a delay rather than the afternoon's time.
    ///
    /// PERSISTED TO DISK, not just held in memory. The failure this exists for is a network
    /// share going away, and the times a share goes away are exactly the times something
    /// else is wrong with the machine. An in-memory queue survives the outage and then loses
    /// everything to the crash or the forced restart that follows it — which is the one
    /// scenario where losing a day of timesheet is most likely and least forgivable.
    /// </summary>
    private static readonly List<PendingWrite> Unwritten = [];

    private static bool _pendingLoaded;

    private static readonly object Gate = new();

    private static string PendingPath => Path.Combine(LocalFolder, "pending-writes.json");

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
        try
        {
            var separator = settings.SeparatorChar;
            var header = TimeLogCsv.Header(separator);
            var line = TimeLogCsv.Format(entry, separator);

            lock (Gate)
            {
                LoadPending();
                RetryUnwritten();

                AppendLine(LocalFileFor(entry.Started), header, line);

                var shared = SharedFileFor(entry.Started, settings);
                if (shared is not null) AppendLine(shared, header, line);

                SavePending();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Time tracking: an entry could not be stored.", ex);
        }
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
                foreach (var line in ReadLinesShared(path))
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

    private static void AppendLine(string path, string header, string line)
    {
        if (TryAppend(path, header, line)) return;

        Unwritten.Add(new PendingWrite { Path = path, Header = header, Line = line });
        Log.Warn($"Time tracking: '{path}' was not writable; the entry is queued for the next append.");
    }

    /// <summary>
    /// Reads the queue back after a restart. Once per session - a failure here must not
    /// prevent the entry that triggered it from being written.
    /// </summary>
    private static void LoadPending()
    {
        if (_pendingLoaded) return;
        _pendingLoaded = true;

        try
        {
            if (!File.Exists(PendingPath)) return;

            var saved = JsonSerializer.Deserialize<List<PendingWrite>>(File.ReadAllText(PendingPath));
            if (saved is null || saved.Count == 0) return;

            Unwritten.AddRange(saved);
            Log.Info($"Time tracking: {saved.Count} entry line(s) left over from a previous " +
                     "session were reloaded and will be retried.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Time tracking: the pending-write queue could not be read: {ex.Message}");
        }
    }

    private static void SavePending()
    {
        try
        {
            Directory.CreateDirectory(LocalFolder);

            // Deleted rather than left as an empty array, so the common case leaves no file
            // and "does this exist?" is a meaningful question when diagnosing.
            if (Unwritten.Count == 0)
            {
                if (File.Exists(PendingPath)) File.Delete(PendingPath);
                return;
            }

            File.WriteAllText(PendingPath, JsonSerializer.Serialize(Unwritten));
        }
        catch (Exception ex)
        {
            Log.Warn($"Time tracking: the pending-write queue could not be saved: {ex.Message}");
        }
    }

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

    private static void RetryUnwritten()
    {
        if (Unwritten.Count == 0) return;

        // Oldest first, and stop at the first failure so ordering in the file is preserved.
        var written = 0;
        while (written < Unwritten.Count)
        {
            var pending = Unwritten[written];
            if (!TryAppend(pending.Path, pending.Header, pending.Line)) break;
            written++;
        }

        if (written == 0) return;

        Unwritten.RemoveRange(0, written);
        Log.Info($"Time tracking: {written} queued entry line(s) written on retry.");
    }

    /// <summary>
    /// One append attempt, with a short backoff for the case that actually happens: another
    /// Revit session on the same login writing the same file at the same moment.
    /// </summary>
    private static bool TryAppend(string path, string header, string line)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;

                if (!isNew) UpgradeHeader(path, header);

                using var stream = new FileStream(
                    path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

                // The BOM belongs at the start of the file and nowhere else — emitting it on
                // every append puts three junk bytes in the middle of a row.
                using var writer = new StreamWriter(
                    stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: isNew));

                if (isNew) writer.WriteLine(header);
                writer.WriteLine(line);

                return true;
            }
            catch (IOException)
            {
                // Sharing violation or a network hiccup. Both clear on their own.
                Thread.Sleep(120 * (attempt + 1));
            }
            catch (UnauthorizedAccessException)
            {
                // Permissions will not fix themselves; queueing it is pointless noise, but
                // reporting it once per entry is how the user finds out the path is wrong.
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn($"Time tracking: append to '{path}' failed: {ex.Message}");
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Replaces the header line of an existing file when this version writes more columns
    /// than it was created with.
    ///
    /// WHY BOTHER: new columns are always appended, so an old file plus new rows is a file
    /// whose header names twelve columns while later rows carry sixteen. Everything still
    /// PARSES — the reader tolerates both — but opened in Excel the extra columns arrive
    /// unlabelled, which is exactly the "this output is messy" problem the report exists to
    /// solve. One line changes; not a single data row is touched, so nothing can be lost in
    /// the rewrite.
    ///
    /// Silent on failure, and deliberately so: a header that could not be widened costs
    /// four column captions, and losing the entry over it would cost the time itself.
    /// </summary>
    private static void UpgradeHeader(string path, string header)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0) return;

            var current = lines[0];
            if (current == header) return;

            // Only ever widen a file this add-in wrote, and only forwards. An unrecognised
            // first line, or one already longer than ours, is left completely alone.
            if (!current.StartsWith("Timestamp", StringComparison.Ordinal)) return;
            if (current.Length >= header.Length) return;

            lines[0] = header;

            // Written beside the original and swapped in, so a crash mid-write leaves the
            // original intact rather than a half-written ledger.
            var temp = path + ".upgrading";
            File.WriteAllLines(temp, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.Delete(path);
            File.Move(temp, path);

            Log.Info($"Time tracking: widened the header of '{Path.GetFileName(path)}' to " +
                     $"{TimeLogCsv.ColumnCount} columns. No rows were changed.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Time tracking: could not widen the header of '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Reads with FileShare.ReadWrite, so the log can be read while another session is
    /// appending to it — the alternative is the UI throwing whenever tracking is active.
    /// </summary>
    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (reader.ReadLine() is { } line) yield return line;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "user" : cleaned;
    }
}
