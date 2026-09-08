using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Sweeps;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Places the skirting board component along the interior perimeter of every room that is
/// not excluded by name or Department, broken at openings and casework.
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
                "boundary of every room, at 0 above the floor. Walls, and also columns and piers - " +
                "anything presenting a face to the room gets a board, not only walls.\n\n" +
                (settings.ExcludedRoomKeywords.Length == 0
                    ? "No room type is skipped by name - wet rooms are skirted like any other. "
                    : "Rooms whose Name or Department contains " +
                      $"{string.Join(" or ", settings.ExcludedRoomKeywords.Select(k => $"'{k}'"))} " +
                      "are skipped. ") +
                $"'{settings.ExteriorRoomPrefix}' exterior placeholder rooms are skipped - those " +
                "are outdoors, and the walls bounding them are the building's exterior faces." +
                (settings.RequireDepartment
                    ? " Rooms with no Department are skipped too: that is this project's marker for " +
                      "'this room has been designed', and it usually excludes more rooms than the " +
                      "name rule does."
                    : string.Empty) + "\n\n" +
                "Doors, windows, wall openings and casework break the run: each wall is placed as " +
                "several shorter pieces rather than one board with holes voided out of it, so the " +
                "lengths in a schedule are the lengths you actually buy.\n\n" +
                "Where an opening leaves bare wall thickness on show at floor level, a board wraps " +
                "across that reveal too - door jambs, cased openings and wall openings alike, so " +
                "the jamb returns are both modelled and measured.\n\n" +
                "Each jamb is measured against the opening family's own geometry first. A door " +
                "that lines its reveal in full gets no board and cannot double up; one with a " +
                "frame at a single face gets a board on the depth left bare; a cased opening gets " +
                "the whole return. Nothing is placed in space the door model already occupies.",
            ExpandedContent =
                "Re-running is safe and repeatable: it deletes what it made last time, so the " +
                "result depends only on the model and the rules - never on what an earlier run " +
                "happened to leave behind.\n\n" +
                "A wall between two qualifying rooms gets a board on each face - there is skirting " +
                "in both rooms.\n\n" +
                "CORNERS are mitred. The board arriving at a corner runs through it to the apex " +
                "and the board leaving starts where its own thickness clears the arriving one, so " +
                "the two meet with no gap and no shared material at any angle - not only at 90 " +
                "degrees.\n\n" +
                "NOTHING OVERLAPS. Every board is checked against what is already standing at the " +
                "same level before it is created, and cut back to the stretch nothing covers - " +
                "whatever it is hosted on, so a column set flush into a wall cannot be skirted " +
                "twice along the same line.\n\n" +
                "WALL SWEEPS STAY WALL-HOSTED. Jamb boards do not: they cross the wall's faces at " +
                "right angles and belong to the opening, so they are placed as independent " +
                "components with no host relationship to the wall and no involvement in the door " +
                "family's lining logic.\n\n" +
                $"Everything placed is stamped '{SkirtingSettings.Stamp}...' in Extensible Storage - " +
                "invisible in the UI and not editable by hand - which is how Regenerate finds and " +
                "removes it. A board placed by hand carries no stamp and is never touched.\n\n" +
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

        // swallowWarnings, for the same reason Place Radiators and Dimension Rooms use it: this
        // places a component along every boundary of every qualifying room in the model, plus a
        // jamb board at every reveal, so a run is hundreds of creations rather than a handful.
        // Revit posts warnings during that kind of bulk placement ("element is slightly off
        // axis", "outside its host" and a dozen more), and the default handler shows each in a
        // modal dialog - which stops the run dead partway and leaves the user clicking through
        // identical boxes they have stopped reading. Warnings only; errors still surface and
        // still roll the whole run back.
        Transactions.Run(ctx.Document, CommandName, generator.Run, swallowWarnings: true);

        var result = generator.Result();

        // The boards are already committed by now, so an unwritable reports folder must not
        // turn a successful run into "the command could not complete" - see
        // ReportWriter.TryWriteLog. This tool's report has always been the sidecar log alone,
        // with no CSV beside it, and that is unchanged.
        var logPath = ReportWriter.TryWriteLog(
            ctx.Document, "skirting",
            [.. result.Report, string.Empty, .. result.Problems],
            out var reportProblem);

        Log.Info($"{CommandName}: {result.Placed} piece(s), {Measure.ToMetres(result.TotalLength):0.00} m, " +
                 $"{result.RoomsExcluded} room(s) excluded, {result.Problems.Count} problem(s).");

        var summary = new TaskDialog(CommandName)
        {
            MainInstruction = result.Placed == 0
                ? "Nothing was placed."
                : $"Placed {result.Placed} piece(s), {Measure.ToMetres(result.TotalLength):0.00} m total.",
            MainContent =
                $"{result.RoomsQualifying} qualifying room(s); {result.RoomsExcluded} excluded by " +
                $"name or missing Department; {result.RoomsExterior} skipped as exterior placeholders.\n" +
                $"{result.OpeningBreaks} break(s) at doors/windows/openings, " +
                $"{result.CaseworkBreaks} at casework.\n" +
                $"{result.RevealPieces} jamb/reveal board(s) wrapping into openings, " +
                $"{Measure.ToMetres(result.RevealLength):0.00} m." +
                // NOT "expected on a second run" any more. That message survived from the
                // removed Top-up mode, which skipped faces it had already visited. Regenerate
                // deletes its own work first, so a second run places exactly what the first
                // did - and zero means the rules excluded everything, which is a real result
                // worth investigating rather than a reassurance.
                (result.Placed == 0
                    ? "\n\nNothing qualified. Regenerate rebuilds from scratch every time, so this " +
                      "is not a 'already done' result - check the exclusions above and the report."
                    : string.Empty) +
                (result.Problems.Count == 0
                    ? string.Empty
                    : $"\n\n{result.Problems.Count} problem(s):\n" + string.Join("\n", result.Problems.Take(6))) +
                (reportProblem.Length == 0 ? string.Empty : $"\n\n{reportProblem}"),
            ExpandedContent = string.Join(Environment.NewLine, result.Report),
            FooterText = logPath is null ? "No report was written." : $"Report: {logPath}",
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        summary.Show();

        return Result.Succeeded;
    }
}
