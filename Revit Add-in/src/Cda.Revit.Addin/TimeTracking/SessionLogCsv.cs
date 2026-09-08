using System.Globalization;

namespace Cda.Revit.Addin.TimeTracking;

/// <summary>
/// The on-disk shape of the session log - one line per Revit session, same
/// header-widens-forward, tolerant-parse rules as <see cref="TimeLogCsv"/>, which this
/// mirrors deliberately: anyone who has already written a script against the segment log
/// should find the session log familiar rather than inventing a second convention.
/// </summary>
internal static class SessionLogCsv
{
    private static readonly string[] Columns =
    [
        "Timestamp", "Username", "ProjectName", "ProjectNumber", "FileName",
        "EndTimestamp", "DateLastModified", "Machine",
    ];

    public static int ColumnCount => Columns.Length;

    private const string Stamp = "yyyy-MM-dd HH:mm:ss";

    public static string Header(char separator) => string.Join(separator, Columns);

    public static string Format(SessionEntry entry, char separator)
    {
        var fields = new[]
        {
            entry.SessionStart.ToString(Stamp, CultureInfo.InvariantCulture),
            entry.Username,
            entry.ProjectName,
            entry.ProjectNumber,
            entry.FileName,
            entry.SessionEnd.ToString(Stamp, CultureInfo.InvariantCulture),
            entry.DateLastModified?.ToString(Stamp, CultureInfo.InvariantCulture) ?? string.Empty,
            entry.Machine,
        };

        return string.Join(separator, fields.Select(f => Quote(f, separator)));
    }

    /// <summary>Same rules as <see cref="TimeLogCsv"/>: quote only when needed, flatten newlines.</summary>
    private static string Quote(string? value, char separator)
    {
        var text = (value ?? string.Empty)
            .Replace("\r\n", " · ")
            .Replace('\r', ' ')
            .Replace('\n', ' ');

        var needsQuotes = text.Contains(separator) || text.Contains('"');
        return needsQuotes ? '"' + text.Replace("\"", "\"\"") + '"' : text;
    }

    /// <summary>Returns null for the header, blanks and anything malformed.</summary>
    public static SessionEntry? Parse(string line, char separator)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (line.StartsWith(Columns[0], StringComparison.Ordinal)) return null;

        try
        {
            var f = Split(line, separator);
            if (f.Count < 5) return null;

            var started = DateTime.ParseExact(f[0], Stamp, CultureInfo.InvariantCulture);

            string At(int i) => i < f.Count ? f[i] : string.Empty;

            var ended = DateTime.TryParseExact(At(5), Stamp, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedEnd)
                ? parsedEnd
                : started;

            var lastModified = DateTime.TryParseExact(At(6), Stamp, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedModified)
                ? parsedModified
                : (DateTime?)null;

            return new SessionEntry
            {
                SessionStart = started,
                Username = f[1],
                ProjectName = f[2],
                ProjectNumber = f[3],
                FileName = f[4],
                SessionEnd = ended,
                DateLastModified = lastModified,
                Machine = At(7),
            };
        }
        catch
        {
            return null;
        }
    }

    public static SessionEntry? ParseFlexible(string line, char preferred) =>
        Parse(line, preferred) ?? Parse(line, preferred == ',' ? ';' : ',');

    private static List<string> Split(string line, char separator)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c != '"') { current.Append(c); continue; }
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
