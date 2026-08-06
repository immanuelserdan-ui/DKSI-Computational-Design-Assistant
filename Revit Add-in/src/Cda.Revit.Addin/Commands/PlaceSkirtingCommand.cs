using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Sweeps;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Places the skirting board component along the interior perimeter of every room that is
/// not a wet room, broken at openings and casework.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class PlaceSkirtingCommand : CommandBase
{
    protected override string CommandName => "Place Skirting";

    protected override Result Run(CommandContext ctx)
    {
        var settings = new SkirtingSettings();

        var choice = new TaskDialog(CommandName)
        {
            MainInstruction = "Place skirting boards in every qualifying room?",
            MainContent =
                $"Places '{settings.TypeName}' as a component along the room-side face of every " +
                "wall bounding a room, at 0 above the floor.\n\n" +
                "Rooms whose Name or Department contains " +
                $"{string.Join(" or ", settings.ExcludedRoomKeywords.Select(k => $"'{k}'"))} are skipped, " +
                $"as are '{settings.ExteriorRoomPrefix}' exterior placeholder rooms - those are " +
                "outdoors, and the walls bounding them are the building's exterior faces.\n\n" +
                "Doors, windows, wall openings and casework break the run: each wall is placed as " +
                "several shorter pieces rather than one board with holes voided out of it, so the " +
                "lengths in a schedule are the lengths you actually buy.\n\n" +
                "Where an opening leaves bare wall thickness on show at floor level, a board wraps " +
                "across that reveal too. Doors are excluded from this by default - their frame " +
                "already covers the reveal - but cased openings are included.",
            ExpandedContent =
                "Re-running is safe and repeatable: it deletes what it made last time, so the " +
                "result depends only on the model and the rules - never on what an earlier run " +
                "happened to leave behind.\n\n" +
                "A wall between two qualifying rooms gets a board on each face - there is skirting " +
                "in both rooms.\n\n" +
                $"Everything placed is stamped in Comments as '{SkirtingSettings.Stamp}...', which is " +
                "how Regenerate finds and removes it. A board placed by hand carries no stamp and " +
                "is never touched.\n\n" +
                BuildInfo.DescribeFull(),
            FooterText = $"{BuildInfo.Describe()}  ·  Log: {Log.CurrentFile}",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
        };
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            "Regenerate all skirting",
            "Removes every board this tool placed before, then rebuilds from scratch. " +
            "Use this after any change to the model or the rules.");
        if (choice.Show() != TaskDialogResult.CommandLink1)
            throw new OperationCanceledException();

        // ONE MODE ONLY.
        //
        // Fill Gaps and Top-up were removed. Both existed to avoid rebuilding, and both
        // earned their removal: top-up skipped any face it had already visited, so it could
        // never find a gap INSIDE a finished face; Fill Gaps re-planned every face but
        // depended on matching existing boards geometrically, and a flaw in that match
        // stacked 23 duplicate boards on top of correct ones.
        //
        // Regenerate is the only mode whose result depends solely on the model and the
        // rules, with no residue from an earlier run or an earlier version of the rules.
        // A tool that is right every time beats one that is faster and sometimes wrong.
        var generator = new SkirtingGenerator(ctx.Document, settings);

        Transactions.Run(ctx.Document, CommandName, generator.Run);

        var result = generator.Result();

        var logPath = ReportWriter.WriteSidecarLog(
            ReportWriter.DefaultPath(ctx.Document, "skirting"),
            [.. result.Report, string.Empty, .. result.Problems]);

        Log.Info($"{CommandName}: {result.Placed} piece(s), {Measure.ToMetres(result.TotalLength):0.00} m, " +
                 $"{result.RoomsExcluded} room(s) excluded, {result.Problems.Count} problem(s).");

        var summary = new TaskDialog(CommandName)
        {
            MainInstruction = result.Placed == 0
                ? "Nothing was placed."
                : $"Placed {result.Placed} piece(s), {Measure.ToMetres(result.TotalLength):0.00} m total.",
            MainContent =
                $"{result.RoomsQualifying} qualifying room(s); {result.RoomsExcluded} excluded as wet " +
                $"rooms; {result.RoomsExterior} skipped as exterior placeholders.\n" +
                $"{result.OpeningBreaks} break(s) at doors/windows/openings, " +
                $"{result.CaseworkBreaks} at casework.\n" +
                $"{result.RevealPieces} reveal board(s) wrapping into openings, " +
                $"{Measure.ToMetres(result.RevealLength):0.00} m." +
                (result.Placed == 0
                    ? "\n\nIf every segment was already done, this is expected on a second run."
                    : string.Empty) +
                (result.Problems.Count == 0
                    ? string.Empty
                    : $"\n\n{result.Problems.Count} problem(s):\n" + string.Join("\n", result.Problems.Take(6))),
            ExpandedContent = string.Join(Environment.NewLine, result.Report),
            FooterText = $"Report: {logPath}",
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        summary.Show();

        return Result.Succeeded;
    }
}
