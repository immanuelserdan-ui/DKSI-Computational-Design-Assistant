using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Dimensions;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Draws the interior corner-to-corner dimensions for every placed room, in the plan views you
/// choose, using the office's own dimension style.
///
/// THE SCOPE QUESTION IS ASKED, NOT ASSUMED, and it is the one decision in this tool that
/// cannot be made for the user. Every other automation in this add-in sweeps the whole model
/// because a wall's finish area is a fact about the wall. A dimension is not a fact about a
/// room - it is a fact about a DRAWING, and the same room legitimately wants dimensions on
/// the 1:50 unit plan and none at all on the 1:200 overall. Sweeping every floor plan in a
/// project would put four hundred dimensions on the key plan, and deleting them again by hand
/// is worse than the work this tool saves.
///
///   Within whatever scope is chosen, the sweep IS total: every room on every level those
///   views cover, never the active level and never the visible crop.
///
/// DRY RUN FIRST, ALWAYS. The preview is not an estimate - it creates every dimension for
/// real inside a transaction that is then rolled back, so the counts it reports are the ones
/// Revit actually produced, including the runs Revit refused. Nothing else can tell you in
/// advance that a room's walls will not take a dimension.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class DimensionRoomsCommand : CommandBase
{
    protected override string CommandName => "Dimension Rooms";

    protected override Result Run(CommandContext ctx)
    {
        var doc = ctx.Document;
        var settings = new RoomDimensionSettings();

        if (settings.ResolveType(doc) is null)
        {
            ReportMissingType(doc, settings);
            return Result.Cancelled;
        }

        var views = ChooseViews(ctx);
        if (views.Count == 0) throw new OperationCanceledException();

        // ---- dry run -------------------------------------------------------------

        RoomDimensionGenerator.Outcome? preview = null;

        Transactions.Probe(doc, $"{CommandName} (dry run)",
            () => preview = Execute(doc, settings, views));

        if (preview is null || preview.DimensionsCreated == 0)
        {
            TaskDialog.Show(CommandName,
                "Nothing would be dimensioned.\n\n" +
                Summarise(preview, views.Count) +
                $"\n\nDetails: {Log.CurrentFile}");
            return Result.Cancelled;
        }

        if (!Confirm(preview, views.Count)) throw new OperationCanceledException();

        // ---- apply ---------------------------------------------------------------

        RoomDimensionGenerator.Outcome? result = null;

        // Warnings swallowed: a run across forty views posts one "dimension references are
        // not parallel" per awkward wall, and forty modal dialogs is not a dry run's worth of
        // information - it is a tool that never finishes. Errors still stop and roll back.
        Transactions.Run(doc, CommandName,
            () => result = Execute(doc, settings, views), swallowWarnings: true);

        if (result is null)
        {
            TaskDialog.Show(CommandName, $"The run did not complete. See {Log.CurrentFile}");
            return Result.Failed;
        }

        // The dimensions are already committed by now, so a report that cannot be written is a
        // footnote rather than a failed command - see ReportWriter.TryWriteReport.
        var reportPath = ReportWriter.TryWriteReport(
            doc, "room-dimensions", result.Rows,
            result.Notes.Count > 0 ? result.Notes : ["No warnings."],
            out var reportProblem, out _);

        Log.Info($"{CommandName}: {result.DimensionsCreated} dimension(s) across " +
                 $"{result.ViewsProcessed} view(s), {result.DimensionsReplaced} replaced, " +
                 $"{result.TextsMoved} text(s) moved.");

        TaskDialog.Show(CommandName,
            Summarise(result, views.Count) +
            "\n\nOne Ctrl+Z reverts the whole run." +
            (reportPath is null ? $"\n\n{reportProblem}" : $"\n\nReport: {reportPath}"));

        return Result.Succeeded;
    }

    /// <summary>
    /// The generator and the arranger, in the order they must run.
    ///
    /// THE REGENERATE BETWEEN THEM IS LOAD-BEARING. A dimension Revit has not yet evaluated
    /// has no text position to read and no value to measure text against; asking for either
    /// throws or returns the position it will not keep. This is the whole reason arranging is
    /// a second pass rather than something done as each dimension is created.
    /// </summary>
    private static RoomDimensionGenerator.Outcome Execute(
        Document doc, RoomDimensionSettings settings, IReadOnlyList<ViewPlan> views)
    {
        var outcome = new RoomDimensionGenerator(doc, settings).Run(views);

        doc.Regenerate();

        if (settings.ArrangeText)
            outcome.TextsMoved = DimensionTextArranger.Arrange(doc, outcome.Created, settings);

        return outcome;
    }

    // ---- scope -------------------------------------------------------------------

    private IReadOnlyList<ViewPlan> ChooseViews(CommandContext ctx)
    {
        var doc = ctx.Document;

        var all = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel is not null)
            .OrderBy(v => v.GenLevel!.Elevation)
            .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var active = doc.ActiveView as ViewPlan;
        var activeIsUsable = active is { IsTemplate: false, ViewType: ViewType.FloorPlan };

        var dialog = new TaskDialog(CommandName)
        {
            MainInstruction = "Which views should be dimensioned?",
            MainContent =
                "Dimensions live in a view, not in the model, so this is the one thing the " +
                "tool cannot decide for you. Whichever you pick, every placed room on the " +
                "levels those views cover is dimensioned - not just the ones on screen.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
        };

        // The view name goes in the SUPPORTING text, not the label. A command link's
        // mainContent may not contain a newline - TaskDialog throws ArgumentException rather
        // than wrapping - and the supporting parameter is the one built to carry the detail.
        if (activeIsUsable)
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                "This view only", active!.Name);

        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
            $"Every floor plan ({all.Count})",
            "Includes key plans and 1:200 overalls. Sensible on a model whose floor plans " +
            "are all working drawings, wasteful otherwise.");

        return dialog.Show() switch
        {
            TaskDialogResult.CommandLink1 when activeIsUsable => [active!],
            TaskDialogResult.CommandLink2 => all,
            _ => [],
        };
    }

    // ---- reporting ---------------------------------------------------------------

    private void ReportMissingType(Document doc, RoomDimensionSettings settings)
    {
        var available = RoomDimensionSettings.LinearTypeNames(doc);

        var dialog = new TaskDialog(CommandName)
        {
            MainInstruction = $"No dimension style named '{settings.DimensionTypeName}'.",
            MainContent =
                "Nothing was placed. Load or create that style, or point the tool at one of " +
                "the linear dimension styles this project already has.",
            ExpandedContent = available.Count == 0
                ? "This project has no linear dimension styles at all."
                : string.Join("\n", available),
            CommonButtons = TaskDialogCommonButtons.Close,
        };

        dialog.Show();

        Log.Warn($"{CommandName}: dimension type '{settings.DimensionTypeName}' not found. " +
                 $"Available: {string.Join(", ", available)}");
    }

    private bool Confirm(RoomDimensionGenerator.Outcome preview, int viewCount)
    {
        var dialog = new TaskDialog(CommandName)
        {
            MainInstruction = "Dry run complete.",
            MainContent = Summarise(preview, viewCount) +
                          "\n\nThese counts come from Revit having actually created the " +
                          "dimensions and then rolled them back, so they are what a real run " +
                          "will produce.",
            ExpandedContent = preview.Notes.Count == 0
                ? "No warnings."
                : string.Join("\n", preview.Notes.Take(40)),
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
        };

        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Place the dimensions");

        return dialog.Show() == TaskDialogResult.CommandLink1;
    }

    private static string Summarise(RoomDimensionGenerator.Outcome? outcome, int viewCount)
    {
        if (outcome is null) return "The pass produced no result.";

        var lines = new List<string>
        {
            $"{outcome.DimensionsCreated} dimension string(s) across " +
            $"{outcome.RoomsDimensioned} room(s) in {outcome.ViewsProcessed} of {viewCount} view(s).",
        };

        if (outcome.DimensionsReplaced > 0)
            lines.Add($"{outcome.DimensionsReplaced} dimension(s) from a previous run were " +
                      "replaced. Hand-drawn dimensions were left alone.");

        if (outcome.TextsMoved > 0)
            lines.Add($"{outcome.TextsMoved} text(s) were moved clear with a leader because " +
                      "the segment was narrower than its own number at this view's scale.");

        if (outcome.RoomsSkipped > 0)
            lines.Add($"{outcome.RoomsSkipped} room(s) were skipped - excluded by name, under " +
                      "the minimum area, or without two opposing faces to measure between.");

        if (outcome.Notes.Count > 0)
            lines.Add($"{outcome.Notes.Count} note(s) recorded.");

        return string.Join("\n", lines);
    }

}
