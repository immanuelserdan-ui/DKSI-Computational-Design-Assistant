using System.IO;
using System.Text;
using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Infrastructure;

/// <summary>
/// Writes the CSV + .log pair the Dynamo tools produced, so existing office habits and
/// any downstream spreadsheets keep working.
/// </summary>
public static class ReportWriter
{
    /// <summary>
    /// Danish/European Excel expects ';'. Every field is quoted regardless, which is what
    /// the Python did and what keeps embedded separators safe.
    /// </summary>
    private const char Separator = ';';

    public static string ReportFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cda", "RevitAddin", "reports");

    /// <summary>%LOCALAPPDATA%\Cda\RevitAddin\reports\&lt;tool&gt;-&lt;model&gt;-&lt;stamp&gt;.csv</summary>
    public static string DefaultPath(Document doc, string toolSlug, string extension = ".csv")
    {
        Directory.CreateDirectory(ReportFolder);

        var model = "Model";
        try
        {
            if (!string.IsNullOrWhiteSpace(doc.Title))
                model = Path.GetFileNameWithoutExtension(doc.Title);
        }
        catch
        {
            // Keep the fallback.
        }

        var safe = new string(model.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return Path.Combine(ReportFolder, $"{toolSlug}-{safe}-{DateTime.Now:yyyyMMdd-HHmmss}{extension}");
    }

    public static void WriteCsv(string path, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();

        foreach (var row in rows)
            sb.AppendLine(string.Join(Separator, row.Select(c => '"' + (c ?? string.Empty).Replace("\"", "\"\"") + '"')));

        Write(path, sb.ToString());
    }

    public static void WriteText(string path, IEnumerable<string> lines) =>
        Write(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);

    /// <summary>Writes the .log that sits beside a .csv report.</summary>
    public static string WriteSidecarLog(string csvPath, IEnumerable<string> lines)
    {
        var logPath = Path.ChangeExtension(csvPath, ".log");
        WriteText(logPath, lines);
        return logPath;
    }

    private static void Write(string path, string content)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        // UTF-8 with BOM so Excel picks up Danish characters without a manual import step.
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
