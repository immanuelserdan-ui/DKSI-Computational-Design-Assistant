using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Finishes;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Turns automatic finish-area updating on or off, reports what it is doing, and forces
/// a pass on demand.
///
/// Manual, not ReadOnly: "Update the areas now" opens a transaction. A ReadOnly command
/// that starts one throws, and the failure appears at the moment the user finally asks
/// the tool to do something — the worst possible time to discover it.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class FinishAutoUpdateCommand : CommandBase
{
    protected override string CommandName => "Auto-Update Finish Areas";

    protected override Result Run(CommandContext ctx)
    {
        var doc = ctx.Document;
        var boundToRooms = IsStaleParameterBound(doc);
        var stale = boundToRooms ? CountStale(doc) : 0;
        var dirty = FinishAutomation.IsDirty(doc);

        // Select a room before running this and it becomes a diagnostic instead of a
        // status page. Guessing why a room "looks wrong" from a screenshot is how two
        // rounds of this got spent on the wrong problem.
        var selectedRoom = FirstSelectedRoom(ctx);

        var dialog = new TaskDialog(CommandName)
        {
            TitleAutoPrefix = false,
            MainInstruction = FinishAutomation.Enabled
                ? "Automatic updating is ON."
                : "Automatic updating is OFF.",
            MainContent = BuildStatus(boundToRooms, stale, selectedRoom is not null),
            ExpandedContent = selectedRoom is null
                ? null
                : RoomLimitAdjuster.Explain(doc, selectedRoom),
            FooterText = $"Log: {Log.CurrentFile}",
            CommonButtons = TaskDialogCommonButtons.Close,
        };

        // Always offer the forcing action first. A status page that cannot DO anything is
        // what made this button feel redundant.
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Update the areas now",
            dirty
                ? "Something has changed since the last pass. This brings the schedule up to date."
                : "Nothing is pending, but this runs a pass anyway.");

        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
            FinishAutomation.Enabled ? "Turn automatic updating off" : "Turn automatic updating on",
            FinishAutomation.Enabled
                ? "Stops the background passes. The areas then only change when you ask."
                : "Areas update by themselves shortly after you stop editing.");

        switch (dialog.Show())
        {
            case TaskDialogResult.CommandLink1:
                FinishAutomation.RunNow(doc);
                TaskDialog.Show(CommandName,
                    "Done. The schedule shows the new numbers immediately — it is a live view.\n\n" +
                    $"Timing is in the log:\n{Log.CurrentFile}");
                break;

            case TaskDialogResult.CommandLink2:
                var wanted = !FinishAutomation.Enabled;
                FinishAutomation.SetEnabled(wanted);
                TaskDialog.Show(CommandName, wanted
                    ? "Automatic updating is on.\n\nAreas refresh a few seconds after you stop " +
                      "editing, when a schedule is opened, and on save."
                    : "Automatic updating is off.\n\nUse this button, or Finish Surface Area, " +
                      "when you want the areas brought up to date.");
                break;
        }

        return Result.Succeeded;
    }

    private static Room? FirstSelectedRoom(CommandContext ctx) =>
        ctx.UiDocument.Selection.GetElementIds()
            .Select(id => ctx.Document.GetElement(id))
            .OfType<Room>()
            .FirstOrDefault();

    private static string BuildStatus(bool boundToRooms, int stale, bool hasSelectedRoom)
    {
        if (hasSelectedRoom)
        {
            return
                "A room is selected — open the details below for a full report on it: its " +
                "limits, the ceilings above it, whether they are Room Bounding, and exactly " +
                "what the automation would change.\n\n" +
                "That report is produced by the same code that does the adjusting, so it " +
                "cannot claim one thing while the tool does another.";
        }

        return BuildStatus(boundToRooms, stale);
    }

    private static string BuildStatus(bool boundToRooms, int stale)
    {
        if (!boundToRooms)
        {
            return
                $"This model has no '{FinishAutomation.StaleParameter}' parameter on Rooms, so " +
                "changed rooms cannot be recorded.\n\n" +
                "Add it as a Yes/No project parameter bound to Rooms. Put it in your finish " +
                "schedule as well — it shows at a glance which rooms are waiting on a " +
                "recalculation.";
        }

        var pending = stale == 0
            ? "Nothing is waiting: every room's areas match the model as it stands."
            : $"{stale} room(s) are waiting to be recalculated.";

        return
            $"{pending}\n\n" +
            "How it works: as you move walls, or add doors, windows and openings, the rooms " +
            "they touch are marked out of date. Saving recalculates them, so the file on disk " +
            "always carries correct areas.\n\n" +
            "Recalculation runs on save rather than while you work because a full finish pass " +
            "takes seconds to minutes — long enough to be disruptive if it interrupted you " +
            "mid-edit. Expect saves to take longer when rooms are waiting.";
    }

    private static bool IsStaleParameterBound(Document doc)
    {
        var room = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .FirstElement();

        // No rooms at all is not a misconfiguration — there is simply nothing to check.
        return room is null || room.LookupParameter(FinishAutomation.StaleParameter) is not null;
    }

    private static int CountStale(Document doc) =>
        new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Count(r => r.LookupParameter(FinishAutomation.StaleParameter)?.AsInteger() == 1);
}
