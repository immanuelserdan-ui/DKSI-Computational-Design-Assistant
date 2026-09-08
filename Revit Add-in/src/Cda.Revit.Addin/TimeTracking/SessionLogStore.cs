using System.IO;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.TimeTracking;

/// <summary>
/// The session ledger, alongside <see cref="TimeLogStore"/>'s segment ledger. Same two
/// destinations (local always, shared folder when configured), same one-file-per-person
/// rule and the same reasoning for it - see TimeLogStore's remarks, which apply unchanged.
///
///   LOCAL   %LOCALAPPDATA%\Cda\RevitAddin\timelog\sessionlog-YYYY-MM.csv
///   SHARED  &lt;SharedFolder&gt;\sessionlog-&lt;username&gt;-YYYY-MM.csv
///
/// Deliberately in the SAME local folder as the segment log rather than a new one: it is
/// the same feature, configured by the same settings, and a second top-level folder would
/// only make "where did my time go" a two-place search for no benefit.
/// </summary>
internal static class SessionLogStore
{
    public static string LocalFolder => TimeLogStore.LocalFolder;

    private static readonly AppendOnlyCsvLog Ledger = new(LocalFolder, "pending-session-writes.json");

    public static string LocalFileFor(DateTime local) =>
        Path.Combine(LocalFolder, $"sessionlog-{local:yyyy-MM}.csv");

    public static string? SharedFileFor(DateTime local, TimeTrackingSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SharedFolder)) return null;

        var user = Sanitize(Environment.UserName);
        return Path.Combine(settings.SharedFolder, $"sessionlog-{user}-{local:yyyy-MM}.csv");
    }

    public static void Append(SessionEntry entry, TimeTrackingSettings settings)
    {
        var separator = settings.SeparatorChar;
        var header = SessionLogCsv.Header(separator);
        var line = SessionLogCsv.Format(entry, separator);

        Ledger.Append(LocalFileFor(entry.SessionStart), SharedFileFor(entry.SessionStart, settings), header, line);
    }

    /// <summary>Sessions between two local dates, inclusive of both days. Local ledger only.</summary>
    public static List<SessionEntry> Read(DateTime fromLocal, DateTime toLocal, TimeTrackingSettings settings)
    {
        var from = fromLocal.Date;
        var to = toLocal.Date.AddDays(1).AddTicks(-1);

        var entries = new List<SessionEntry>();
        var separator = settings.SeparatorChar;

        for (var month = new DateTime(from.Year, from.Month, 1); month <= to; month = month.AddMonths(1))
        {
            var path = LocalFileFor(month);
            if (!File.Exists(path)) continue;

            try
            {
                foreach (var line in AppendOnlyCsvLog.ReadLinesShared(path))
                {
                    var entry = SessionLogCsv.ParseFlexible(line, separator);
                    if (entry is not null && entry.SessionStart >= from && entry.SessionStart <= to)
                        entries.Add(entry);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Session tracking: '{path}' could not be read: {ex.Message}");
            }
        }

        entries.Sort((a, b) => a.SessionStart.CompareTo(b.SessionStart));
        return entries;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "user" : cleaned;
    }
}
