using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Linings;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Port of Resolve-Lining-Clashes_v1.0.dyn. Resolves door/window lining clashes in shared
/// wall reveals and banks the uncovered remnant in "Lining Change".
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ResolveLiningClashesCommand : CommandBase
{
    protected override string CommandName => "Resolve Lining Clashes";

    protected override Result Run(CommandContext ctx)
    {
        var doorsAndWindows = new[] { (long)BuiltInCategory.OST_Doors, (long)BuiltInCategory.OST_Windows };

        var selection = ctx.UiDocument.Selection.GetElementIds()
            .Select(ctx.Document.GetElement)
            .Where(e => e?.Category is { } c && doorsAndWindows.Contains(c.Id.Value))
            .ToList();

        var settings = new LiningSettings();

        var choice = new TaskDialog(CommandName)
        {
            MainInstruction = "Resolve lining clashes?",
            MainContent =
                "Where two openings share a wall reveal, both sides uncheck that lining face and each " +
                "banks its own uncovered remnant in 'Lining Change'. A touching door also drives each " +
                "window's Lining YN and Window Material.\n\n" +
                $"Max clear gap {settings.GapToleranceMm:0} mm, minimum remnant kept {settings.MinRemnantMm:0} mm. " +
                "A full recompute every run, so it is safe to re-run after the model changes.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
        };

        // The Dynamo graph hard-wires its selection input to null, so Dynamo Player always
        // runs the whole model. Whole model is therefore the default and the first option
        // here; scoping to a selection has to be chosen deliberately, because a partial
        // run is exactly what produced the wrong answers earlier.
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            "Dry run - whole model", "Every door and window. Matches the Dynamo graph. Nothing is modified.");
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
            "Apply - whole model", "Every door and window. Matches the Dynamo graph.");

        if (selection.Count > 0)
        {
            choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink3,
                $"Dry run - {selection.Count} selected only",
                "Neighbours are still read from the whole model, but only the selection is written.");
            choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink4,
                $"Apply - {selection.Count} selected only",
                "Use when you are deliberately correcting a few openings.");
        }

        var (apply, scoped) = choice.Show() switch
        {
            TaskDialogResult.CommandLink1 => (false, false),
            TaskDialogResult.CommandLink2 => (true, false),
            TaskDialogResult.CommandLink3 => (false, true),
            TaskDialogResult.CommandLink4 => (true, true),
            _ => throw new OperationCanceledException(),
        };

        var effectiveSelection = scoped ? selection : [];

        var resolver = new LiningClashResolver(ctx.Document, settings);
        LiningResult result;

        if (apply)
        {
            // The apply pass calls Document.Regenerate() and reads the family's own
            // "Lining Length" back, so the whole thing has to sit inside one transaction.
            LiningResult? captured = null;
            Transactions.Run(ctx.Document, CommandName,
                () => captured = resolver.Run(apply: true, effectiveSelection));
            result = captured!;
        }
        else
        {
            result = resolver.Run(apply: false, effectiveSelection);
        }

        var csvPath = ReportWriter.DefaultPath(ctx.Document, apply ? "lining" : "lining-dryrun");
        ReportWriter.WriteCsv(csvPath, result.Rows);

        var log = new List<string>(result.Summary) { string.Empty, "WARNINGS:" };
        log.AddRange(result.Warnings.Count > 0 ? result.Warnings.Select(w => "  " + w) : ["  (none)"]);
        log.Add(string.Empty);
        log.Add("GEOMETRY (mm):");
        log.AddRange(result.Geometry);

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
        summary.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open the log (includes geometry dump)");

        var shown = summary.Show();
        if (shown == TaskDialogResult.CommandLink1)
            Process.Start(new ProcessStartInfo { FileName = csvPath, UseShellExecute = true });
        else if (shown == TaskDialogResult.CommandLink2)
            Process.Start(new ProcessStartInfo { FileName = logPath, UseShellExecute = true });

        return Result.Succeeded;
    }
}
