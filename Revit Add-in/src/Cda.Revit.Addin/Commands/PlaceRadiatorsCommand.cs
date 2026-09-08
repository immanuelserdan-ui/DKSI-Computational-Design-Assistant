using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Heating;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Places a radiator under every window in the model, centred, clear of the floor and clear
/// of the sill, and moves the ones that cannot go there to the nearest wall that will take
/// them.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class PlaceRadiatorsCommand : CommandBase
{
    protected override string CommandName => "Place Radiators";

    protected override Result Run(CommandContext ctx)
    {
        var settings = new RadiatorSettings();

        var choice = new TaskDialog(CommandName)
        {
            MainInstruction = "Place a radiator under every window?",
            MainContent =
                $"Places '{settings.FamilyName}' under every window in the model - every level, " +
                "not just this view - centred on the window and hosted in its wall.\n\n" +
                $"CLEARANCES. The panel sits {Measure.ToMillimetres(settings.FloorClearance):0} mm " +
                $"off the finished floor and at least " +
                $"{Measure.ToMillimetres(settings.MinSillClearance):0} mm below the sill, aiming for " +
                $"{Measure.ToMillimetres(settings.SillClearance):0} mm. Those two gaps are the " +
                "convection loop; without them the radiator is a warm plate rather than a heater.\n\n" +
                "SIZE. The tallest panel that fits under the sill, then the longest of that height " +
                "that fits the free wall - height first because output goes as surface area and a " +
                "shorter, taller panel beats a long flat one under the same window.\n\n" +
                "NOTHING OVERLAPS. Doors, columns, casework, sanitary ware and radiators already " +
                "standing are measured in three dimensions and subtracted from the wall before the " +
                "panel is sized. Only what reaches into the panel's own height band counts - the " +
                "window above it does not block it, and neither does a wall cupboard.",
            ExpandedContent =
                "EXCEPTIONS ARE THE POINT. A window that cannot take a panel is not skipped " +
                "quietly. Floor-to-ceiling glazing, a sill too low to clear, and a wall too full " +
                "are three different reasons and are reported as three different reasons.\n\n" +
                $"Where the window will not take one, the panel moves to another wall in the same " +
                "room - preferring the exterior wall and the shortest distance from the glass, so " +
                "the heat stays where the cold surface is. Every relocated panel is listed.\n\n" +
                $"A panel may slide up to {Measure.ToMillimetres(settings.MaxSlide):0} mm off the " +
                "window centreline to clear an obstruction. Past that it counts as no longer under " +
                "the window and goes to relocation instead of quietly drifting.\n\n" +
                "SIZES ARE MEASURED, NOT READ. Each radiator type is placed once in a transaction " +
                "that is thrown away and measured, because this model's type named 'L 1000 H 600' " +
                "reports a Radiator Length of 600 mm. Trusting that parameter plans every panel " +
                "400 mm short; trusting the name plans it through the door reveal. Disagreements " +
                "are listed in the report so the family gets fixed.\n\n" +
                "Rooms named '" + settings.ExteriorRoomPrefix + "...' are outdoors and get nothing.\n\n" +
                "ONLY ROOMS THE MODEL HAS COMMITTED TO. A room with no Name, or with no room " +
                "tag in any view, is skipped and listed. Both are evidence of intent rather " +
                "than geometry: an unnamed room has not been decided about, and an untagged one " +
                "has never been put on a drawing - it is usually a leftover from moved walls or " +
                "a duplicate sitting under a real room. Those are precisely the rooms whose " +
                "radiators nobody would ever notice were wrong.\n\n" +
                "Re-running is safe and repeatable: it deletes what it placed last time and rebuilds, " +
                "so the result depends only on the model and the rules. A radiator placed by hand " +
                "carries no stamp, is never deleted, and still blocks.\n\n" +
                BuildInfo.DescribeFull(),
            FooterText = $"{BuildInfo.Describe()}  ·  Log: {Log.CurrentFile}",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
        };
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            "Regenerate all radiators",
            "Removes every radiator this tool placed before, then rebuilds from scratch. " +
            "One Ctrl+Z reverts the whole run.");
        if (choice.Show() != TaskDialogResult.CommandLink1)
            throw new OperationCanceledException();

        var generator = new RadiatorGenerator(ctx.Document, settings);

        // Two transactions, one undo step. The first is thrown away on purpose: it places a
        // probe instance of every radiator type so the catalogue can be MEASURED rather than
        // read off parameters that disagree with the geometry. A rolled-back transaction
        // leaves no model change and no undo entry, so the user sees one operation.
        Transactions.RunGrouped(ctx.Document, CommandName, () =>
        {
            Transactions.Probe(ctx.Document, CommandName + " (measure catalogue)", generator.Calibrate);
            // Warnings resolved rather than shown: this places up to one element per window
            // across the whole model, and a modal dialog on the eleventh of fifty-one stops
            // the run dead. Errors still surface and still roll back.
            Transactions.Run(ctx.Document, CommandName, generator.Run, swallowWarnings: true);
        });

        var result = generator.Result();

        // Written through TryWriteReport, so an unwritable reports folder costs a footnote
        // rather than turning a committed run of 39 radiators into "the command could not
        // complete" - see that method for the full reasoning.
        var csvPath = ReportWriter.TryWriteReport(
            ctx.Document, "radiators",
            [RadiatorGenerator.Header(), .. result.Rows],
            [.. result.Report, string.Empty, "PROBLEMS", .. result.Problems],
            out var reportProblem, out var logPath);

        Log.Info($"{CommandName}: {result.Placed} under windows, {result.Relocated} relocated, " +
                 $"{result.Skipped} unserved, {result.Failed} failed, {result.Removed} removed.");

        var served = result.Placed + result.Relocated;

        var summary = new TaskDialog(CommandName)
        {
            MainInstruction = served == 0
                ? "No radiator was placed."
                : $"Placed {served} radiator(s) for {result.Windows} window(s).",
            MainContent =
                $"{generator.Centred} centred under their window.\n" +
                $"{generator.Slid} under their window but slid along the wall to clear something.\n" +
                $"{result.Relocated} moved to another wall - the window could not take a panel.\n" +
                $"{result.Skipped} window(s) got nothing.\n" +
                (result.Failed > 0 ? $"{result.Failed} failed outright.\n" : string.Empty) +
                (result.Removed > 0
                    ? $"\n{result.Removed} radiator(s) from a previous run were removed first.\n"
                    : string.Empty) +
                (served == 0
                    ? "\nRegenerate rebuilds from scratch every time, so this is not an " +
                      "'already done' result - check the report for what refused."
                    : string.Empty) +
                (result.Problems.Count == 0
                    ? string.Empty
                    : $"\n{result.Problems.Count} thing(s) worth reading:\n" +
                      string.Join("\n", result.Problems.Take(6))) +
                (reportProblem.Length == 0 ? string.Empty : $"\n\n{reportProblem}"),
            ExpandedContent = string.Join(Environment.NewLine, result.Report),
            FooterText = csvPath is null
                ? "No report was written."
                : $"Report: {csvPath}{Environment.NewLine}Log: {logPath}",
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        summary.Show();

        return Result.Succeeded;
    }
}
