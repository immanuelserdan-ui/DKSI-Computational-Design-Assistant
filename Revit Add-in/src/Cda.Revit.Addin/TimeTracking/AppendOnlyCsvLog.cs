using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.TimeTracking;

/// <summary>
/// Local-plus-shared append-only CSV log: a local file that is always written, an optional
/// shared-folder copy, a persisted retry queue for writes that could not land immediately
/// (a locked file, a network folder that went away), and in-place header widening when a
/// newer version of the add-in writes more columns than the file was created with.
///
/// Factored out of <see cref="TimeLogStore"/> so the new session ledger
/// (<see cref="SessionLogStore"/>) gets the same crash-safety guarantees without a second,
/// independently-maintained copy of this logic. The two ledgers differ only in what a row
/// means and how many of them there are a day (dozens of segments vs. one session) - never
/// in how a row gets to disk safely, which is exactly the part worth sharing.
///
/// Each instance owns its OWN pending-write queue file, named by the caller, so the segment
/// ledger's backlog and the session ledger's backlog never collide on disk.
/// </summary>
internal sealed class AppendOnlyCsvLog(string localFolder, string pendingFileName)
{
    /// <summary>One line that could not be written yet, and where it was going.</summary>
    private sealed class PendingWrite
    {
        public string Path { get; set; } = string.Empty;
        public string Header { get; set; } = string.Empty;
        public string Line { get; set; } = string.Empty;
    }

    private readonly List<PendingWrite> _unwritten = [];
    private bool _pendingLoaded;
    private readonly object _gate = new();

    private string PendingPath => Path.Combine(localFolder, pendingFileName);

    /// <summary>
    /// Appends one row to the local file and, when given, the shared copy. Never throws:
    /// losing a row is bad, but taking Revit down over a log is worse, and the caller is
    /// usually an event handler.
    /// </summary>
    public void Append(string localPath, string? sharedPath, string header, string line)
    {
        try
        {
            lock (_gate)
            {
                LoadPending();
                RetryUnwritten();

                AppendLine(localPath, header, line);
                if (sharedPath is not null) AppendLine(sharedPath, header, line);

                SavePending();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"'{Path.GetFileName(localPath)}': an entry could not be stored.", ex);
        }
    }

    /// <summary>
    /// Reads with FileShare.ReadWrite, so the file can be read while another session is
    /// appending to it - the alternative is the UI throwing whenever tracking is active.
    /// </summary>
    public static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (reader.ReadLine() is { } line) yield return line;
    }

    // ------------------------------------------------------------------ file plumbing

    private void AppendLine(string path, string header, string line)
    {
        if (TryAppend(path, header, line)) return;

        _unwritten.Add(new PendingWrite { Path = path, Header = header, Line = line });
        Log.Warn($"'{path}' was not writable; the entry is queued for the next append.");
    }

    /// <summary>
    /// Reads the queue back after a restart. Once per instance - a failure here must not
    /// prevent the entry that triggered it from being written.
    /// </summary>
    private void LoadPending()
    {
        if (_pendingLoaded) return;
        _pendingLoaded = true;

        try
        {
            if (!File.Exists(PendingPath)) return;

            var saved = JsonSerializer.Deserialize<List<PendingWrite>>(File.ReadAllText(PendingPath));
            if (saved is null || saved.Count == 0) return;

            _unwritten.AddRange(saved);
            Log.Info($"{saved.Count} entry line(s) left over from a previous session were " +
                     "reloaded and will be retried.");
        }
        catch (Exception ex)
        {
            Log.Warn($"The pending-write queue at '{PendingPath}' could not be read: {ex.Message}");
        }
    }

    private void SavePending()
    {
        try
        {
            Directory.CreateDirectory(localFolder);

            // Deleted rather than left as an empty array, so the common case leaves no file
            // and "does this exist?" is a meaningful question when diagnosing.
            if (_unwritten.Count == 0)
            {
                if (File.Exists(PendingPath)) File.Delete(PendingPath);
                return;
            }

            File.WriteAllText(PendingPath, JsonSerializer.Serialize(_unwritten));
        }
        catch (Exception ex)
        {
            Log.Warn($"The pending-write queue at '{PendingPath}' could not be saved: {ex.Message}");
        }
    }

    private void RetryUnwritten()
    {
        if (_unwritten.Count == 0) return;

        // Oldest first, and stop at the first failure so ordering in the file is preserved.
        var written = 0;
        while (written < _unwritten.Count)
        {
            var pending = _unwritten[written];
            if (!TryAppend(pending.Path, pending.Header, pending.Line)) break;
            written++;
        }

        if (written == 0) return;

        _unwritten.RemoveRange(0, written);
        Log.Info($"{written} queued entry line(s) written on retry.");
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

                // The BOM belongs at the start of the file and nowhere else - emitting it on
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
                Log.Warn($"Append to '{path}' failed: {ex.Message}");
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Replaces the header line of an existing file when this version writes more columns
    /// than it was created with. See <see cref="TimeLogStore"/>'s original for the full
    /// rationale - it applies unchanged to the session ledger.
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

            Log.Info($"Widened the header of '{Path.GetFileName(path)}'. No rows were changed.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not widen the header of '{path}': {ex.Message}");
        }
    }
}
