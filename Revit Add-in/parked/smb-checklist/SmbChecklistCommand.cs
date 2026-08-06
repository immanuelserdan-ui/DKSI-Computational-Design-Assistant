using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Standards;
using Microsoft.Win32;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Runs the SMB modelling checklist against the open model.
///
/// READ-ONLY. It changes nothing; it reports where the model stands against the standard and
/// writes the full result beside the other DKSI reports.
///
/// The number it leads with is deliberately the honest one: how many checks a machine can
/// settle, and how many lines still need a person. A tool that reported "94% complete" by
/// counting items it had auto-passed would be measuring itself, not the model.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class SmbChecklistCommand : CommandBase
{
    protected override string CommandName => "SMB Checklist";

    protected override Result Run(CommandContext ctx)
    {
        var checklist = SmbChecklist.Load(out var source);

        var choice = new TaskDialog(CommandName)
        {
            MainInstruction = $"Audit this model against {checklist.Title}?",
            MainContent =
                $"Version {checklist.Version}, {checklist.TotalItems} checklist line(s), loaded from " +
                $"{source}.\n\n" +
                "Every line a machine can settle is checked against the model: view templates, " +
                "QA schedules, Project Information, room identity, parts, and the coded " +
                "parameters on openings, casework and surfaces.\n\n" +
                "Lines that are a human judgement — wall direction, dimensions, splits, swings — " +
                "are listed as OUTSTANDING, never auto-passed. Roughly two thirds of the sheet is " +
                "that kind, and pretending otherwise would remove the only step catching those " +
                "mistakes.\n\n" +
                "Nothing is written to the model.",
            ExpandedContent =
                $"Checklist file: {(source == "built in" ? SmbChecklist.OverridePath + " (not present; using the embedded copy)" : source)}\n\n" +
                "Edit that file to rename a view template, add a code or change a schedule name — " +
                "the add-in has no checklist compiled into it.",
            FooterText = $"{BuildInfo.Describe()}  ·  Log: {Log.CurrentFile}",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
        };
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Audit the model",
            "View templates, schedules, parameters and model data.");
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Audit the model and an export folder",
            "Also checks jpeg naming, FBX names against type names, and that the Excel exports open.");

        var picked = choice.Show();

        if (picked != TaskDialogResult.CommandLink1 && picked != TaskDialogResult.CommandLink2)
            throw new OperationCanceledException();

        string? exportFolder = null;

        if (picked == TaskDialogResult.CommandLink2)
        {
            var folder = new OpenFolderDialog { Title = "Folder holding the exported jpegs, FBX and Excel files" };
            if (folder.ShowDialog() == true) exportFolder = folder.FolderName;
        }

        var result = new SmbAudit(ctx.Document, checklist, exportFolder).Run(source);

        var report = BuildReport(result, exportFolder);
        var reportPath = ReportWriter.WriteSidecarLog(
            ReportWriter.DefaultPath(ctx.Document, "smb-checklist"), report);

        Log.Info($"{CommandName}: {result.Passed} passed, {result.Failed} failed, " +
                 $"{result.Advisory} advisory, {result.Skipped} skipped, " +
                 $"{result.ManualOutstanding} awaiting a person.");

        var failures = result.Results
            .Where(r => r.Status == CheckStatus.Fail && r.Mode == CheckMode.Auto)
            .ToList();

        var summary = new TaskDialog(CommandName)
        {
            MainInstruction = result.Failed == 0
                ? $"Every automated check passed ({result.Passed} of {result.Automated})."
                : $"{result.Failed} automated check(s) failed.",
            MainContent =
                $"Automated: {result.Passed} passed, {result.Failed} failed, " +
                $"{result.Advisory} advisory, {result.Skipped} not applicable.\n" +
                $"Awaiting a person: {result.ManualOutstanding} line(s).\n\n" +
                (failures.Count == 0
                    ? "The model matches the standard everywhere it can be measured. The manual " +
                      "lines in the report are the remaining review."
                    : string.Join("\n", failures.Take(8).Select(f => $"• {f.Section} — {f.Item}: {f.Detail}"))),
            ExpandedContent = string.Join(Environment.NewLine, report.Take(80)),
            FooterText = $"Full report: {reportPath}",
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        summary.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the full report");

        if (summary.Show() == TaskDialogResult.CommandLink1)
            Process.Start(new ProcessStartInfo { FileName = reportPath, UseShellExecute = true });

        return Result.Succeeded;
    }

    private static List<string> BuildReport(SmbAuditResult result, string? exportFolder)
    {
        var lines = new List<string>
        {
            $"SMB MODELLING CHECKLIST — {result.Version}",
            $"Checklist source : {result.ChecklistSource}",
            $"Export folder    : {exportFolder ?? "not checked"}",
            $"Run              : {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            string.Empty,
            $"AUTOMATED  {result.Passed} passed · {result.Failed} failed · " +
            $"{result.Advisory} advisory · {result.Skipped} not applicable",
            $"MANUAL     {result.ManualOutstanding} line(s) still need a person",
            string.Empty,
            "A manual line is not a failure. It is a judgement the model cannot answer —",
            "listed here so the review is complete rather than assumed.",
            new string('-', 78),
        };

        foreach (var section in result.Results.GroupBy(r => r.Section))
        {
            lines.Add(string.Empty);
            lines.Add(section.Key.ToUpperInvariant());

            foreach (var check in section)
            {
                var mark = check.Status switch
                {
                    CheckStatus.Pass => "[x]",
                    CheckStatus.Fail => check.Mode == CheckMode.Advisory ? "[?]" : "[!]",
                    CheckStatus.Skipped => "[-]",
                    _ => "[ ]",
                };

                lines.Add($"  {mark} {check.Item}");
                if (check.Detail.Length > 0) lines.Add($"        {check.Detail}");
            }
        }

        lines.Add(string.Empty);
        lines.Add(new string('-', 78));
        lines.Add("[x] passed   [!] failed   [?] advisory   [-] not applicable   [ ] needs a person");

        return lines;
    }
}
