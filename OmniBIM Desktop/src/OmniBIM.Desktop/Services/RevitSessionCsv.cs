using System.Globalization;

namespace OmniBIM.Desktop.Services;

/// <summary>
/// Reads sessionlog-*.csv rows written by Cda.Revit.Addin.TimeTracking.SessionLogCsv.
/// Same sync obligation as <see cref="TimeSegmentCsv"/> - see its remarks.
/// </summary>
public static class RevitSessionCsv
{
    private const string Stamp = "yyyy-MM-dd HH:mm:ss";
    private const string FirstColumn = "Timestamp";

    public static Models.RevitSession? Parse(string line, char separator)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (line.StartsWith(FirstColumn, StringComparison.Ordinal)) return null;

        try
        {
            var f = CsvLine.Split(line, separator);
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

            return new Models.RevitSession
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

    public static Models.RevitSession? ParseFlexible(string line, char preferred) =>
        Parse(line, preferred) ?? Parse(line, preferred == ',' ? ';' : ',');
}
