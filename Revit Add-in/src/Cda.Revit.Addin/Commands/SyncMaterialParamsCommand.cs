using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Materials;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Port of MaterialToTypeParams.dyn. Copies material identity data onto the FK Kode /
/// FM Bygningsdel type and instance parameters across every model category.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class SyncMaterialParamsCommand : CommandBase
{
    protected override string CommandName => "Sync Material Parameters";

    protected override Result Run(CommandContext ctx)
    {
        // The Dynamo graph had a "Preview only" toggle that was easy to leave in the
        // wrong position. Asking every time makes the destructive choice explicit, and
        // makes preview the path of least resistance.
        var choice = new TaskDialog(CommandName)
        {
            MainInstruction = "Sync material classification to type and instance parameters?",
            MainContent =
                "Manufacturer -> FK Kode / FK Kode Instance\n" +
                "Comments -> FM Bygningsdel / FM Bygningsdel Instance\n\n" +
                "This runs across every model category in the project.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
        };
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            "Preview", "Report what would change. Nothing is written.");
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
            "Apply", "Write the parameters. One undo step in Revit.");

        var answer = choice.Show();
        var apply = answer switch
        {
            TaskDialogResult.CommandLink1 => false,
            TaskDialogResult.CommandLink2 => true,
            _ => throw new OperationCanceledException(),
        };

        var settings = new MaterialSyncSettings();
        var sync = new MaterialTypeSync(ctx.Document, settings);
        MaterialSyncResult result;

        if (apply)
        {
            // One transaction for the whole model: thousands of small transactions is the
            // single biggest cause of a tool that "takes twenty minutes", and it also
            // turns Ctrl+Z into thousands of undo steps.
            MaterialSyncResult? captured = null;
            Transactions.Run(ctx.Document, CommandName, () => captured = sync.Run(apply: true));
            result = captured!;
        }
        else
        {
            result = sync.Run(apply: false);
        }

        var csvPath = ReportWriter.DefaultPath(ctx.Document, apply ? "material-sync" : "material-sync-preview");
        ReportWriter.WriteCsv(csvPath, result.Rows);

        var log = new List<string>(result.Summary);
        if (result.Unresolved.Count > 0)
        {
            log.Add(string.Empty);
            log.Add($"UNRESOLVED ({result.Unresolved.Count}) - no material could be determined:");
            log.AddRange(result.Unresolved.Select(r => "  " + string.Join(" | ", r)));
        }

        var logPath = ReportWriter.WriteSidecarLog(csvPath, log);

        Log.Info($"{CommandName}: apply={apply}, " +
                 $"types written={result.Counts.GetValueOrDefault("types written")}, " +
                 $"instances written={result.Counts.GetValueOrDefault("instances written")}, " +
                 $"report={csvPath}");

        var summary = new TaskDialog(CommandName)
        {
            MainInstruction = apply ? "Parameters written." : "Preview complete - nothing was written.",
            MainContent = string.Join("\n", result.Counts.Select(c => $"{c.Key}: {c.Value}")),
            ExpandedContent = string.Join(Environment.NewLine, result.Summary),
            FooterText = $"Report: {csvPath}",
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        summary.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the report");
        summary.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open the log");

        var shown = summary.Show();
        if (shown == TaskDialogResult.CommandLink1)
            Process.Start(new ProcessStartInfo { FileName = csvPath, UseShellExecute = true });
        else if (shown == TaskDialogResult.CommandLink2)
            Process.Start(new ProcessStartInfo { FileName = logPath, UseShellExecute = true });

        return Result.Succeeded;
    }
}
