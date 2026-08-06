using System.Globalization;
using System.Text;

namespace Cda.Revit.Addin.TimeTracking;

/// <summary>
/// The on-disk shape of the time log: one line per entry, one file format for the local
/// ledger, the shared copy and the export. Having a single format is what lets the log be
/// both an append-only store and the deliverable — no separate "export schema" to keep in
/// step.
/// </summary>
internal static class TimeLogCsv
{
    /// <summary>
    /// The seven columns the brief requires, in the order it lists them, FOLLOWED by four
    /// extras. Order matters: anything downstream reading by column index keeps working,
    /// and anything reading by header name gets the extra detail for free.
    /// </summary>
    private static readonly string[] Columns =
    [
        "Timestamp", "Username", "ProjectName", "ViewName", "DurationMinutes", "EntryType", "Description",
        "ProjectNumber", "FileName", "ViewTemplate", "EndTimestamp", "Machine",
        // Appended, never inserted. Anything reading by index keeps working, and a row
        // written before these existed simply runs out of fields - which Parse tolerates.
        "Selskab", "Afdeling", "ClientNumber", "Operator",
    ];

    /// <summary>The header a file must carry to hold every column this version writes.</summary>
    public static int ColumnCount => Columns.Length;

    /// <summary>Sortable, unambiguous, and what Excel parses as a date without being asked.</summary>
    private const string Stamp = "yyyy-MM-dd HH:mm:ss";

    public static string Header(char separator) => string.Join(separator, Columns);

    public static string Format(TimeEntry entry, char separator)
    {
        var fields = new[]
        {
            entry.Started.ToString(Stamp, CultureInfo.InvariantCulture),
            entry.Username,
            entry.ProjectName,
            entry.ViewName,
            entry.DurationMinutes.ToString("0.00", CultureInfo.InvariantCulture),
            entry.Type.ToString(),
            entry.Description,
            entry.ProjectNumber,
            entry.FileName,
            entry.ViewTemplate,
            entry.Ended.ToString(Stamp, CultureInfo.InvariantCulture),
            entry.Machine,
            entry.Selskab,
            entry.Afdeling,
            entry.ClientNumber,
            entry.Operator,
        };

        return string.Join(separator, fields.Select(f => Quote(f, separator)));
    }

    /// <summary>
    /// Standard CSV quoting: quote only when needed, double any embedded quote.
    ///
    /// Newlines are REPLACED rather than quoted. A quoted newline is legal CSV, but it makes
    /// the file impossible to read a line at a time, and this file is appended to from
    /// several Revit sessions at once — line-at-a-time is what keeps a torn read
    /// recoverable instead of corrupting everything after it.
    /// </summary>
    private static string Quote(string? value, char separator)
    {
        var text = (value ?? string.Empty)
            .Replace("\r\n", " · ")
            .Replace('\r', ' ')
            .Replace('\n', ' ');

        var needsQuotes = text.Contains(separator) || text.Contains('"');
        return needsQuotes ? '"' + text.Replace("\"", "\"\"") + '"' : text;
    }

    /// <summary>
    /// Reads one line back. Returns null for the header, blanks and anything malformed —
    /// a single bad line must not stop the rest of a month being read.
    /// </summary>
    public static TimeEntry? Parse(string line, char separator)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (line.StartsWith(Columns[0], StringComparison.Ordinal)) return null;

        try
        {
            var f = Split(line, separator);
            if (f.Count < 7) return null;

            var started = DateTime.ParseExact(f[0], Stamp, CultureInfo.InvariantCulture);
            var minutes = double.Parse(f[4], CultureInfo.InvariantCulture);

            // Files written before a column existed are still readable: anything past the
            // required seven is taken only if it is actually there.
            string At(int i) => i < f.Count ? f[i] : string.Empty;

            var ended = DateTime.TryParseExact(At(10), Stamp, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedEnd)
                ? parsedEnd
                : started.AddMinutes(minutes);

            return new TimeEntry
            {
                Started = started,
                Ended = ended,
                Username = f[1],
                ProjectName = f[2],
                ViewName = f[3],
                DurationMinutes = minutes,
                Type = Enum.TryParse<TimeEntryType>(f[5], out var type) ? type : TimeEntryType.Automated,
                Description = f[6],
                ProjectNumber = At(7),
                FileName = At(8),
                ViewTemplate = At(9),
                Machine = At(11),
                Selskab = At(12),
                Afdeling = At(13),
                ClientNumber = At(14),
                Operator = At(15),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a line without assuming which separator wrote it.
    ///
    /// The separator is a setting, so a month's file can legitimately contain both: comma
    /// rows written before someone switched to semicolon for Danish Excel, semicolon rows
    /// after. Trying the configured one first and the other as a fallback means changing
    /// that setting never makes earlier work vanish from the window or the export.
    /// </summary>
    public static TimeEntry? ParseFlexible(string line, char preferred) =>
        Parse(line, preferred) ?? Parse(line, preferred == ',' ? ';' : ',');

    private static List<string> Split(string line, char separator)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c != '"') { current.Append(c); continue; }

                // "" inside a quoted field is one literal quote.
                if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; continue; }

                inQuotes = false;
            }
            else if (c == '"') inQuotes = true;
            else if (c == separator) { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }

        fields.Add(current.ToString());
        return fields;
    }
}
