using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Finishes;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.UI;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Reports one painted surface under a room other than the one it faces, and remembers the
/// decision so the next takeoff does not undo it.
///
/// THE CASE THIS IS FOR
///   The Danish rules for stair sections sometimes put the finish under a run in the room
///   ABOVE. Geometry cannot express that - the surface is in the lower room and Revit will
///   always say so - and no room-bounding arrangement changes it. So the attribution is
///   recorded as a decision, with its reason and both rooms, rather than faked by moving
///   walls or rooms until the measurement comes out differently.
///
/// WHY IT NEEDS A REGISTRY RATHER THAN JUST EDITING THE ROW
///   Editing the carrier's Room parameters works and lasts until the next takeoff run, which
///   deletes every carrier and places a fresh set. PaintRoomOverrides keeps the decision at
///   project level and re-applies it, so the override is a property of the project rather
///   than of an element that is about to be thrown away.
///
/// WHAT IT DOES NOT DO, AND THE DIALOG SAYS SO
///   No area moves and nothing is recounted. The Room element's own finish parameters are
///   computed from geometry by the engine and are NOT rewritten, so an overridden row and its
///   original room's total will disagree by the overridden amount - which is the honest
///   result, because the paint really is on a surface in that room.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ReassignPaintRoomCommand : CommandBase
{
    protected override string CommandName => "Reassign Paint to Room";

    protected override Result Run(CommandContext ctx)
    {
        var doc = ctx.Document;

        var selected = ctx.UiDocument.Selection.GetElementIds()
            .Select(doc.GetElement)
            .Where(e => e is not null && !string.IsNullOrWhiteSpace(SegmentOf(e)))
            .ToList();

        var choice = new TaskDialog(CommandName)
        {
            MainInstruction = selected.Count == 1
                ? "Reassign the selected takeoff row?"
                : "Paint room overrides",
            MainContent =
                "Reports a painted surface under a room other than the one it faces - the case " +
                "the Danish stair rules create, where the finish under a run belongs to the room " +
                "above.\n\n" +
                "Click a row in a takeoff schedule to select its carrier, then run this.\n\n" +
                "Nothing is recounted: the area stays where it was measured and both rooms are " +
                "recorded. The original room's own finish parameters keep the geometric figure, " +
                "so that room's total and this row will differ by the amount moved.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
            FooterText = $"{BuildInfo.Describe()}  ·  Log: {Log.CurrentFile}",
        };

        if (selected.Count == 1)
        {
            choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                "Reassign the selected row",
                SegmentOf(selected[0]));
        }

        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
            "Re-apply every override",
            "Run this after a takeoff. Without it the overrides stay recorded but absent from the schedule.");

        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink3,
            "List / remove overrides",
            "Shows everything recorded in this model, and can clear it.");

        var picked = choice.Show();

        return picked switch
        {
            TaskDialogResult.CommandLink1 => Reassign(ctx, selected[0]),
            TaskDialogResult.CommandLink2 => Reapply(ctx),
            TaskDialogResult.CommandLink3 => ListOrClear(ctx),
            _ => Result.Cancelled,
        };
    }

    // ------------------------------------------------------------------ reassign one row

    private Result Reassign(CommandContext ctx, Element carrier)
    {
        var doc = ctx.Document;

        var key = PaintRoomOverrides.KeyFor(carrier);

        if (key is null)
        {
            TaskDialog.Show(CommandName,
                "That element carries no 'Paint Segment', so it is not a takeoff row and there " +
                "is nothing to reassign.");

            return Result.Cancelled;
        }

        var existing = PaintRoomOverrides.Read(doc)
            .FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.Ordinal));

        // THE NATURAL ROOM, NOT WHATEVER IS ON SCREEN. A carrier already carrying an override
        // shows the OVERRIDE's room in "Room Name"/"Room Number", marker text included -
        // reading those here on a second reassignment would record the previous override's
        // target as the "from" room, and the true origin would be lost after one
        // re-reassignment. The first-ever override already knows the real answer and stays the
        // one source of truth from then on; only a carrier with NO override yet has its natural
        // room sitting in those parameters right now.
        var fromNumber = existing?.FromRoomNumber ?? Text(carrier, "Room Number", "Rum nr");
        var fromName = existing?.FromRoomName ?? Text(carrier, "Room Name", "Rum");
        var area = Area(carrier);

        var rooms = Rooms(doc);

        if (rooms.Count == 0)
        {
            TaskDialog.Show(CommandName, "No placed rooms in this model to reassign to.");
            return Result.Cancelled;
        }

        var window = new RoomPickerWindow(
            $"{SegmentOf(carrier)}  ·  {Text(carrier, "Paint Material Name", "Paint Material")}  " +
            $"·  {Measure.ToSquareMetres(area):0.###} m²",
            $"{fromNumber} {fromName}".Trim(),
            rooms);

        if (RevitWindow.ShowDialog(window, ctx.UiApplication) != true || window.Chosen is null)
            return Result.Cancelled;

        var target = window.Chosen;

        // BACK TO THE NATURAL ROOM CLEARS THE OVERRIDE, RATHER THAN RECORDING ONE THAT MAPS A
        // ROOM TO ITSELF. A self-mapping entry would still carry the "(Reassigned)" marker
        // forever and would still need re-applying after every takeoff, for a row the engine
        // already reports correctly on its own. Compared by Number only, because Number is the
        // identity RoomChoice and the picker are built around - a room's name can be edited by
        // hand without that changing which row this is.
        var revertingToNatural =
            string.Equals(target.Number, fromNumber, StringComparison.OrdinalIgnoreCase);

        var applied = 0;
        IReadOnlyList<string> report = [];

        Transactions.Run(doc, CommandName, () =>
        {
            // LAST WRITE WINS, by key, either way: reassigning the same row twice corrects the
            // first decision rather than leaving two contradictory records for a reader to
            // choose between.
            var withoutThisKey = PaintRoomOverrides.Read(doc)
                .Where(o => !string.Equals(o.Key, key, StringComparison.Ordinal))
                .ToList();

            if (revertingToNatural)
            {
                PaintRoomOverrides.Write(doc, withoutThisKey);

                // INSTANT, NOT "AT THE NEXT TAKEOFF". Apply() only touches carriers whose key is
                // still IN the table, so removing the entry alone would leave this carrier
                // showing whatever the override last wrote until something else regenerates it.
                // Writing the natural values back here is what makes the schedule row change
                // back the moment this command finishes.
                PaintRoomOverrides.RestoreNatural(carrier, fromNumber, fromName);

                applied = PaintRoomOverrides.Apply(doc, withoutThisKey, out report);
            }
            else
            {
                var entry = new PaintRoomOverride(
                    key,
                    target.Number, target.Name,
                    fromNumber, fromName,
                    area,
                    window.Reason,
                    Environment.UserName,
                    DateTime.UtcNow);

                var all = withoutThisKey.Append(entry).ToList();

                PaintRoomOverrides.Write(doc, all);

                // The list, not a re-read: Write may have just created the DataStorage, which a
                // collector cannot see until the document regenerates. See the Apply overload.
                applied = PaintRoomOverrides.Apply(doc, all, out report);
            }
        });

        if (revertingToNatural)
        {
            Log.Info($"{CommandName}: '{key}' override cleared - back to {fromNumber} {fromName}.");

            TaskDialog.Show(CommandName,
                $"Override cleared. This row reports under its natural room again: " +
                $"{fromNumber} {fromName}.\n\n" +
                $"{applied} row(s) still carry an override in this model.");

            return Result.Succeeded;
        }

        Log.Info($"{CommandName}: '{key}' -> {target.Number} {target.Name} " +
                 $"({Measure.ToSquareMetres(area):0.###} m²): {window.Reason}");

        TaskDialog.Show(CommandName,
            $"Reported under {target.Number} {target.Name}.\n\n" +
            $"Was: {fromNumber} {fromName}\n" +
            $"Area: {Measure.ToSquareMetres(area):0.###} m² - unchanged, and not recounted\n" +
            $"Reason: {window.Reason}\n\n" +
            $"{applied} row(s) now carry an override in this model. This row is marked " +
            "\"(Reassigned)\" in the schedule - and in anything exported from it - so it stands " +
            "out from rows left in their natural room; reassigning it back to its natural room " +
            "removes the mark.\n\n" +
            "Re-run this command's 'Re-apply every override' after each takeoff, or the " +
            "schedule will show the geometric room again.");

        return Result.Succeeded;
    }

    // ------------------------------------------------------------------ re-apply

    private Result Reapply(CommandContext ctx)
    {
        var doc = ctx.Document;

        var applied = 0;
        IReadOnlyList<string> report = [];

        Transactions.Run(doc, CommandName + " - re-apply",
            () => applied = PaintRoomOverrides.Apply(doc, out report));

        var lines = new List<string> { $"{applied} row(s) re-pointed." };

        if (report.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(report.Take(15));
            if (report.Count > 15) lines.Add($"... and {report.Count - 15} more; see the log.");
        }

        foreach (var line in report) Log.Info($"{CommandName}: {line}");

        TaskDialog.Show(CommandName, string.Join("\n", lines));
        return Result.Succeeded;
    }

    // ------------------------------------------------------------------ list / clear

    private Result ListOrClear(CommandContext ctx)
    {
        var doc = ctx.Document;
        var all = PaintRoomOverrides.Read(doc);

        if (all.Count == 0)
        {
            TaskDialog.Show(CommandName, "No paint room overrides are recorded in this model.");
            return Result.Succeeded;
        }

        var lines = all
            .OrderBy(o => o.ToRoomNumber, StringComparer.OrdinalIgnoreCase)
            .Select(o =>
                $"{o.FromRoomNumber} {o.FromRoomName} -> {o.ToRoomNumber} {o.ToRoomName}  " +
                $"{Measure.ToSquareMetres(o.AreaSqFtAtWrite):0.###} m²\n" +
                $"    {o.Key.Split(PaintRoomOverrides.Separator)[0]}\n" +
                $"    {o.Reason}  ({o.Author}, {o.WrittenUtc.ToLocalTime():yyyy-MM-dd})")
            .ToList();

        var dialog = new TaskDialog(CommandName)
        {
            MainInstruction = $"{all.Count} override(s) recorded",
            MainContent = string.Join("\n\n", lines.Take(10)) +
                          (lines.Count > 10 ? $"\n\n... and {lines.Count - 10} more." : string.Empty),
            CommonButtons = TaskDialogCommonButtons.Close,
            FooterText = $"{BuildInfo.Describe()}  ·  Log: {Log.CurrentFile}",
        };

        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            "Remove all overrides",
            "The rows go back to the room their surface faces at the next re-apply or takeoff.");

        if (dialog.Show() != TaskDialogResult.CommandLink1) return Result.Succeeded;

        Transactions.Run(doc, CommandName + " - clear", () =>
        {
            // SAME REASON AS THE SINGLE-ROW REVERT: removing the entries alone leaves every
            // affected carrier showing its last override's room until a takeoff or re-apply
            // happens to touch it. Restoring each one directly here is what makes "remove all"
            // actually mean "back to natural, now" rather than "back to natural, eventually".
            var byKey = PaintRoomOverrides.Carriers(doc)
                .Select(c => (Carrier: c, Key: PaintRoomOverrides.KeyFor(c)))
                .Where(t => t.Key is not null)
                .ToDictionary(t => t.Key!, t => t.Carrier, StringComparer.Ordinal);

            foreach (var o in all)
            {
                if (byKey.TryGetValue(o.Key, out var carrier))
                    PaintRoomOverrides.RestoreNatural(carrier, o.FromRoomNumber, o.FromRoomName);
            }

            PaintRoomOverrides.Write(doc, []);
        });

        Log.Info($"{CommandName}: {all.Count} override(s) removed.");

        TaskDialog.Show(CommandName,
            $"{all.Count} override(s) removed. Re-run the takeoff, or the rows keep the room " +
            "they were last given until something rewrites them.");

        return Result.Succeeded;
    }

    // ------------------------------------------------------------------ helpers

    private static IReadOnlyList<RoomChoice> Rooms(Document doc)
    {
        var rooms = new List<RoomChoice>();

        foreach (var room in new FilteredElementCollector(doc)
                     .OfClass(typeof(SpatialElement))
                     .WhereElementIsNotElementType()
                     .OfType<Room>())
        {
            try
            {
                if (room.Area <= 0) continue;   // unplaced

                // NOT room.Name. That property returns the name with the NUMBER appended -
                // "Loftrum 13" - so pairing it with the number rendered "13 Loftrum 13" in the
                // picker and, worse, stored that as the room name on the moved row. The
                // ROOM_NAME parameter is the name on its own.
                var name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();

                rooms.Add(new RoomChoice(
                    room.Number ?? string.Empty,
                    string.IsNullOrWhiteSpace(name) ? room.Name ?? string.Empty : name,
                    (doc.GetElement(room.LevelId) as Level)?.Name ?? string.Empty));
            }
            catch
            {
                // Skip a room that will not answer.
            }
        }

        return rooms
            .OrderBy(r => r.Level, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Number, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string SegmentOf(Element element) => Text(element, "Paint Segment");

    private static string Text(Element element, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var value = ParameterHelper.Find(element, name)?.AsString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch
            {
                // Try the next name.
            }
        }

        return string.Empty;
    }

    private static double Area(Element element)
    {
        foreach (var name in new[] { "Painted Surface Area", "Paint Area" })
        {
            try
            {
                var parameter = ParameterHelper.Find(element, name);

                if (parameter is not null && parameter.StorageType == StorageType.Double)
                {
                    var value = parameter.AsDouble();
                    if (value > 0) return value;
                }
            }
            catch
            {
                // Try the next name.
            }
        }

        return 0;
    }
}
