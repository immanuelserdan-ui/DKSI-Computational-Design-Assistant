using System.Diagnostics;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Excel;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Schedules;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Port of ExportSchedulesToExcel.dyn. Exports every schedule in the model to one .xlsx,
/// one worksheet per schedule. Read-only - opens no transaction.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class ExportSchedulesCommand : CommandBase
{
    protected override string CommandName => "Export Schedules";

    protected override Result Run(CommandContext ctx)
    {
        var doc = ctx.Document;

        var settings = new ScheduleExportSettings();
        var result = new ScheduleExporter(doc, settings).Collect();

        if (result.Sheets.Count == 0)
        {
            // Two very different failures used to share one misleading message. Report
            // which one actually happened, and never discard the reasons.
            var nothingToExport = result.Collected == 0;

            foreach (var line in result.Skipped) Log.Warn($"Export Schedules: {line}");

            var problem = new TaskDialog(CommandName)
            {
                MainInstruction = nothingToExport
                    ? "No schedules found in this model."
                    : $"Found {result.Collected} schedule(s), but none could be read.",

                MainContent = nothingToExport
                    ? "Nothing matched after filtering out view templates, revision schedules and " +
                      "Revit's own internal <angle-bracketed> schedules."
                    : "Every schedule failed while reading its table data. The reasons are below.",

                ExpandedContent = result.Skipped.Count > 0
                    ? string.Join(Environment.NewLine, result.Skipped.Take(60))
                    : "No reasons were recorded.",

                FooterText = $"Details in {Log.CurrentFile}",
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            problem.Show();

            return Result.Cancelled;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export schedules",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            InitialDirectory = DefaultFolder(doc),
            FileName = DefaultFileName(doc),
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true)
            throw new OperationCanceledException();

        XlsxWriter.Write(dialog.FileName, result.Sheets, settings.NumericCells, settings.WrapText);

        Log.Info($"Exported {result.Sheets.Count} schedules to {dialog.FileName}");
        foreach (var line in result.Skipped) Log.Warn($"Export Schedules: {line}");

        var summary = new TaskDialog(CommandName)
        {
            MainInstruction = $"Exported {result.Sheets.Count} schedules.",
            MainContent = Path.GetFileName(dialog.FileName),
            ExpandedContent = string.Join(Environment.NewLine, result.Report) +
                              (result.Skipped.Count > 0
                                  ? Environment.NewLine + Environment.NewLine + "Skipped:" + Environment.NewLine +
                                    string.Join(Environment.NewLine, result.Skipped)
                                  : string.Empty),
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        summary.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the workbook");

        if (summary.Show() == TaskDialogResult.CommandLink1)
            Process.Start(new ProcessStartInfo { FileName = dialog.FileName, UseShellExecute = true });

        return Result.Succeeded;
    }

    /// <summary>Next to the .rvt; Desktop when the model has never been saved.</summary>
    private static string DefaultFolder(Document doc)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(doc.PathName))
            {
                var folder = Path.GetDirectoryName(doc.PathName);
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)) return folder;
            }
        }
        catch
        {
            // Cloud models have no local path. Fall through.
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    private static string DefaultFileName(Document doc)
    {
        var model = "Model";
        try
        {
            if (!string.IsNullOrWhiteSpace(doc.PathName))
                model = Path.GetFileNameWithoutExtension(doc.PathName);
            else if (!string.IsNullOrWhiteSpace(doc.Title))
                model = Path.GetFileNameWithoutExtension(doc.Title);
        }
        catch
        {
            // Keep the fallback.
        }

        var safe = new string(model.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return $"{safe}_Schedules_{DateTime.Now:yyyyMMdd-HHmm}.xlsx";
    }
}
