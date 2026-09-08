using System.Globalization;
using System.Text;

namespace OmniBIM.Desktop.Services;

/// <summary>
/// Reads timelog-*.csv rows written by Cda.Revit.Addin.TimeTracking.TimeLogCsv.
///
/// THIS MUST STAY BYTE-FOR-BYTE IN SYNC WITH THAT CLASS - column order, the stamp format,
/// and the quoting rules are the wire format, and nothing enforces the two copies agreeing
/// except a human keeping them in sync. If the add-in's column list ever changes, update the
/// column count and the At() offsets below to match. The two are deliberately independent
/// (OmniBIM only READS the ledger; it must never write to it or depend on the add-in's
/// assembly), which is the trade for this duplication - see the architecture note on
/// extracting a shared contracts package once this drifts.
/// </summary>
public static class TimeSegmentCsv
{
    private const string Stamp = "yyyy-MM-dd HH:mm:ss";
    private const string FirstColumn = "Timestamp";

    /// <summary>Returns null for the header, blanks, and anything malformed - one bad line must not stop the read.</summary>
    public static Models.TimeSegment? Parse(string line, char separator)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (line.StartsWith(FirstColumn, StringComparison.Ordinal)) return null;

        try
        {
            var f = CsvLine.Split(line, separator);
            if (f.Count < 7) return null;

            var started = DateTime.ParseExact(f[0], Stamp, CultureInfo.InvariantCulture);
            var minutes = double.Parse(f[4], CultureInfo.InvariantCulture);

            string At(int i) => i < f.Count ? f[i] : string.Empty;

            var ended = DateTime.TryParseExact(At(10), Stamp, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedEnd)
                ? parsedEnd
                : started.AddMinutes(minutes);

            return new Models.TimeSegment
            {
                Started = started,
                Ended = ended,
                Username = f[1],
                ProjectName = f[2],
                ViewName = f[3],
                DurationMinutes = minutes,
                Type = Enum.TryParse<Models.TimeSegmentType>(f[5], out var type) ? type : Models.TimeSegmentType.Automated,
                Description = f[6],
                ProjectNumber = At(7),
                FileName = At(8),
                ViewTemplate = At(9),
                Machine = At(11),
                Selskab = At(12),
                Afdeling = At(13),
                ClientNumber = At(14),
                Operator = At(15),
                QA = At(16),
                TaskPhase = At(17),
                TaskCategory = At(18),
                ExternalActivity = At(19),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A month's file can legitimately mix separators if the office ever changed the setting
    /// mid-year. Try the configured one first, the other as a fallback - same rule the add-in
    /// itself reads with.
    /// </summary>
    public static Models.TimeSegment? ParseFlexible(string line, char preferred) =>
        Parse(line, preferred) ?? Parse(line, preferred == ',' ? ';' : ',');
}

/// <summary>Shared CSV field-splitting, identical to the add-in's own (quoted fields, doubled-quote escaping).</summary>
internal static class CsvLine
{
    public static List<string> Split(string line, char separator)
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
