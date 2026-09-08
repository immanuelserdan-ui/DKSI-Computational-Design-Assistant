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

    /// <summary>
    /// The CSV and its sidecar log, written as a pair, with any failure reported rather than
    /// thrown. Returns the CSV path, or null if nothing could be written.
    ///
    /// WHY THE THROWING VERSIONS ARE THE WRONG ONES FOR A COMMAND TO CALL
    ///   Every bulk command writes its report AFTER its transaction has committed. An exception
    ///   out of that write reaches CommandBase, which shows "The command could not complete" and
    ///   returns Result.Failed - for a run whose model changes are already committed and sitting
    ///   on the undo stack. The user is told the exact opposite of what happened, and the
    ///   obvious response to that dialog is to undo work that was correct.
    ///
    ///   A report is a by-product. It is worth a line in the summary when it fails; it is never
    ///   worth reporting a successful model change as a failure. The reports folder being
    ///   unwritable - antivirus holding it, redirected or offline AppData, or last run's CSV
    ///   still open in Excel - is ordinary enough that this is a real path, not a theoretical one.
    /// </summary>
    /// <summary>
    /// The sidecar log alone, for a tool whose report is prose rather than rows. Same contract
    /// as <see cref="TryWriteReport"/>: reports its failure instead of throwing it at a command
    /// whose model changes are already committed. Returns the log path, or null.
    /// </summary>
    public static string? TryWriteLog(
        Document doc, string toolSlug, IEnumerable<string> lines, out string problem)
    {
        problem = string.Empty;

        try
        {
            return WriteSidecarLog(DefaultPath(doc, toolSlug), lines);
        }
        catch (Exception ex)
        {
            problem =
                $"The report could not be written ({ex.Message}). Everything above describes " +
                "what the model now contains - the run itself was unaffected.";

            Log.Warn($"{toolSlug}: report not written: {ex.Message}");
            return null;
        }
    }

    /// <param name="problem">
    /// Empty on success; otherwise a sentence the caller can put in front of the user alongside
    /// the result of the run itself.
    /// </param>
    public static string? TryWriteReport(
        Document doc,
        string toolSlug,
        IEnumerable<IReadOnlyList<string>> rows,
        IEnumerable<string> logLines,
        out string problem,
        out string? logPath)
    {
        problem = string.Empty;
        logPath = null;

        try
        {
            var csvPath = DefaultPath(doc, toolSlug);

            WriteCsv(csvPath, rows);
            logPath = WriteSidecarLog(csvPath, logLines);

            return csvPath;
        }
        catch (Exception ex)
        {
            problem =
                $"The report could not be written ({ex.Message}). Everything above describes " +
                "what the model now contains - the run itself was unaffected.";

            Log.Warn($"{toolSlug}: report not written: {ex.Message}");
            return null;
        }
    }

    private static void Write(string path, string content)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        // UTF-8 with BOM so Excel picks up Danish characters without a manual import step.
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
