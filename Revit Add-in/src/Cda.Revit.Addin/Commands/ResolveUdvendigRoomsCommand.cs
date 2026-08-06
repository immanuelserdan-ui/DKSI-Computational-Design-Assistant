using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Doors;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Port of Resolve-Udvendig-Rooms_v1.0.dyn. Replaces exterior room references on doors
/// with the room on the other side, writing the 02/03/04/05-SCRP parameters, and points
/// the Door Casing schedule columns at those parameters.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ResolveUdvendigRoomsCommand : CommandBase
{
    protected override string CommandName => "Resolve Udvendig Rooms";

    protected override Result Run(CommandContext ctx)
    {
        // Selected doors, if any - otherwise every door in the model.
        var selection = ctx.UiDocument.Selection.GetElementIds()
            .Select(ctx.Document.GetElement)
            .Where(e => e?.Category?.Id.Value == (long)BuiltInCategory.OST_Doors)
            .ToList();

        var scope = selection.Count > 0
            ? $"{selection.Count} selected door(s)"
            : "every door in the model";

        var choice = new TaskDialog(CommandName)
        {
            MainInstruction = $"Resolve exterior room references on {scope}?",
            MainContent =
                "Where a door's FROM or TO side reads 'Udvendig...', it is replaced by the room on " +
                "the other side, and written to the SCRP parameters.\n\n" +
                "The Door Casing schedule columns are also re-pointed at those parameters - the " +
                "built-in From/To Room fields are read-only, so without that step nothing changes on screen.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
        };
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            "Dry run", "Report what would change. Nothing is modified.");
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
            "Apply", "Write the parameters and swap the schedule columns.");

        var apply = choice.Show() switch
        {
            TaskDialogResult.CommandLink1 => false,
            TaskDialogResult.CommandLink2 => true,
            _ => throw new OperationCanceledException(),
        };

        var resolver = new UdvendigRoomResolver(ctx.Document, new UdvendigSettings());
        UdvendigResult result;

        if (apply)
        {
            // The Dynamo graph used two separate transactions - one for the schedule
            // columns, one for the doors. A single transaction here means the whole
            // operation is one undo step, and a failure part-way leaves neither half
            // applied rather than schedules pointing at parameters that were never filled.
            UdvendigResult? captured = null;
            Transactions.Run(ctx.Document, CommandName,
                () => captured = resolver.Run(apply: true, selection));
            result = captured!;
        }
        else
        {
            result = resolver.Run(apply: false, selection);
        }

        var csvPath = ReportWriter.DefaultPath(ctx.Document, apply ? "udvendig" : "udvendig-dryrun");
        ReportWriter.WriteCsv(csvPath, result.Rows);

        var log = new List<string>(result.Summary) { string.Empty, "WARNINGS:" };
        log.AddRange(result.Warnings.Count > 0
            ? result.Warnings.Select(w => "  " + w)
            : ["  (none)"]);

        var logPath = ReportWriter.WriteSidecarLog(csvPath, log);

        Log.Info($"{CommandName}: apply={apply}, warnings={result.Warnings.Count}, report={csvPath}");

        var summary = new TaskDialog(CommandName)
        {
            MainInstruction = apply ? "Done." : "Dry run complete - nothing was modified.",
            MainContent = string.Join("\n", result.Summary),
            ExpandedContent = result.Warnings.Count > 0
                ? string.Join(Environment.NewLine, result.Warnings.Take(60))
                : null,
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
