using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Automation;
using Cda.Revit.Addin.Casework;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// The manual entry point for the casework side-void cutter. The automation runs the same
/// engine off DocumentChanged; this is the only way to get a DRY RUN, the only way to sweep
/// a model that was drawn before the add-in existed, and the only way to see the report.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class CutCaseworkVoidsCommand : CommandBase
{
    protected override string CommandName => "Cut Walls with Casework Voids";

    protected override Result Run(CommandContext ctx)
    {
        var settings = CaseworkCutAutomation.CurrentSettings;

        var selection = ctx.UiDocument.Selection.GetElementIds()
            .Select(ctx.Document.GetElement)
            .OfType<Element>()
            .Where(e => e.Category?.Id.Value == (long)BuiltInCategory.OST_Casework)
            .ToList();

        var choice = new TaskDialog(CommandName)
        {
            MainInstruction = "Cut walls with casework side voids?",
            MainContent =
                "Every wall within " + $"{settings.ReachMm:0} mm of a casework fitting is offered to " +
                "Revit as a cut; Revit accepts the ones the family's voids genuinely reach and refuses " +
                "the rest. Host walls are already cut when the fitting is placed and are left alone, " +
                "and a wall that is already cut by that fitting is skipped - so this is safe to " +
                "re-run as often as you like.\n\n" +
                "Nothing is ever un-cut. Put '" + settings.SkipComment + "' in an instance's Comments " +
                "to have it ignored.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
        };

        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            "Dry run - whole model",
            "Reports exactly which cuts would be made, then rolls back. Nothing is modified.");
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
            "Apply - whole model",
            "One undo step for the whole run.");

        if (selection.Count > 0)
        {
            choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink3,
                $"Dry run - {selection.Count} selected fitting(s)");
            choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink4,
                $"Apply - {selection.Count} selected fitting(s)",
                "Scoping is safe here: a cut depends on one fitting and one wall, so a scoped " +
                "run gives the same answer for those fittings as a whole-model run.");
        }

        var (apply, scoped) = choice.Show() switch
        {
            TaskDialogResult.CommandLink1 => (false, false),
            TaskDialogResult.CommandLink2 => (true, false),
            TaskDialogResult.CommandLink3 => (false, true),
            TaskDialogResult.CommandLink4 => (true, true),
            _ => throw new OperationCanceledException(),
        };

        var scope = scoped ? selection : [];

        // ONE ENGINE CALL EITHER WAY. The dry run is not a second code path that predicts what
        // the apply would do - it is the apply, inside a transaction that is thrown away. That
        // is what makes the two incapable of disagreeing, and it is the only exact answer
        // available: whether a void reaches a wall is a question only Revit's geometry engine
        // can settle, and it settles it by being asked to make the cut.
        CaseworkCutResult? captured = null;

        void Work() => captured = new CaseworkVoidCutter(ctx.Document, settings).Run(scope);

        if (apply) Transactions.Run(ctx.Document, CommandName, Work);
        else Transactions.Probe(ctx.Document, CommandName + " (dry run)", Work);

        var result = captured!;

        var csvPath = ReportWriter.DefaultPath(ctx.Document, apply ? "casework-cuts" : "casework-cuts-dryrun");
        ReportWriter.WriteCsv(csvPath, result.Rows);

        var log = new List<string>(result.Summary) { string.Empty, "WARNINGS:" };
        log.AddRange(result.Warnings.Count > 0 ? result.Warnings.Select(w => "  " + w) : ["  (none)"]);

        var logPath = ReportWriter.WriteSidecarLog(csvPath, log);

        Log.Info($"{CommandName}: apply={apply}, cuts={result.CutsAdded}, " +
                 $"warnings={result.Warnings.Count}, report={csvPath}");

        var summary = new TaskDialog(CommandName)
        {
            MainInstruction = apply
                ? $"Done - {result.CutsAdded} wall cut(s) created."
                : $"Dry run complete - {result.CutsAdded} wall cut(s) would be created. Nothing was modified.",
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
