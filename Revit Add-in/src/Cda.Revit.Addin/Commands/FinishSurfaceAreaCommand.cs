using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Automation;
using Cda.Revit.Addin.Finishes;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Port of "RoomFinishAreas 7-23-26.dyn". Measures room finish surface areas from real
/// finish-layer geometry and writes them to room and element parameters.
///
/// This WRITES: it enables Areas and Volumes if needed, raises room upper limits over
/// sloped ceilings, and sets five parameters. Hence TransactionMode.Manual.
///
/// It is also the single front door to the finish engine. The automation status, the
/// on/off switch and the per-room diagnostic used to live behind a second ribbon button
/// ("Auto-Update Finish Areas"), which meant the answer to "why is this number wrong?"
/// was under a different button from the number itself. They are now on the first page
/// of this dialog, so the state of the automation is visible at the moment someone
/// reaches for the manual run — which is the moment they doubt it.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class FinishSurfaceAreaCommand : CommandBase
{
    protected override string CommandName => "Finish Surface Area";

    protected override Result Run(CommandContext ctx)
    {
        var settings = new FinishSettings();

        // Select a room before running this and the dialog becomes a diagnostic instead of
        // a menu. Guessing why one room "looks wrong" from a screenshot is how two rounds
        // of this got spent on the wrong problem.
        var selectedRoom = FirstSelectedRoom(ctx);

        var choice = new TaskDialog(CommandName)
        {
            MainInstruction = "Measure room finish surface areas?",
            MainContent =
                AutomationBanner(ctx.Document) + "\n\n" +
                "Walls are measured from the real finish-layer faces, clipped to each room, so " +
                "openings, casework voids and profile edits are already excluded. Painted door " +
                "and window reveals, mezzanine slabs and freestanding partitions are included.\n\n" +
                $"Writes to rooms: {settings.WallParameter}, {settings.PaintParameter}, " +
                $"{settings.FloorParameter}, {settings.FloorPaintParameter}, " +
                $"{settings.CeilingParameter}, {settings.CeilingPaintParameter}, " +
                $"{settings.NetFloorParameter}, {settings.CeilingSourceParameter} - and the same " +
                "area names onto walls/floors/ceilings/roofs/foundations where they are bound.\n\n" +
                $"It also stamps each of those elements with '{settings.ApartmentParameter}', " +
                $"'{settings.RoomNumberParameter}' and '{settings.RoomNameParameter}' from the room " +
                "whose finish it carries - Department, Number and Name. That is what lets a material " +
                "takeoff be grouped per apartment and room, which a takeoff cannot do on its own " +
                "because Revit has no room relationship for a wall or a ceiling.\n\n" +
                "A room with no ceiling over it is measured off the slab above, and failing that " +
                $"the roof; '{settings.CeilingSourceParameter}' records which one, per room.\n\n" +
                "It also enables 'Areas and Volumes' if it is off, and raises room upper limits " +
                "over sloped ceilings.",
            ExpandedContent = selectedRoom is null
                ? null
                : RoomLimitAdjuster.Explain(ctx.Document, selectedRoom),
            FooterText = $"{BuildInfo.Describe()}  ·  Log: {Log.CurrentFile}",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
        };
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            "Measure and write", "One transaction, one undo step. Also exports the material CSV.");
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
            FinishAutomation.Enabled ? "Turn automatic updating off" : "Turn automatic updating on",
            FinishAutomation.Enabled
                ? "Stops the background passes. Areas then only change when you run this."
                : "Areas update by themselves shortly after you stop editing.");

        var picked = choice.Show();

        if (picked == TaskDialogResult.CommandLink2)
        {
            ToggleAutomation();
            return Result.Succeeded;
        }

        if (picked != TaskDialogResult.CommandLink1)
            throw new OperationCanceledException();

        // Create and bind anything this model is missing, BEFORE the measuring transaction -
        // a parameter has to be committed before values can be written into it. A model that
        // already has them pays one binding-map walk and nothing else.
        var bound = FinishParameterSetup.EnsureBound(ctx.Document, settings, "Finish Surface Area ran");

        var calculator = new RoomFinishCalculator(ctx.Document, settings);
        FinishResult? captured = null;

        // One transaction for the whole model: the pass regenerates the document twice
        // (after enabling volumes, after raising limits) and both must be inside it.
        Transactions.Run(ctx.Document, CommandName, () => captured = calculator.Run());
        var result = captured!;

        var csvPath = ReportWriter.DefaultPath(ctx.Document, "finish-areas");
        WriteMaterialCsv(csvPath, result.CsvRows);

        var report = new List<string>(result.Report);

        if (bound is not null)
        {
            report.Insert(0,
                $"PARAMETERS CREATED AUTOMATICALLY: {bound.Created} bound, {bound.Extended} widened " +
                "to reach more categories. This model did not have everything the finish engine " +
                "writes to, so it was set up as part of this run - no separate step needed.");

            report.InsertRange(1, bound.Report);
        }

        var logPath = ReportWriter.WriteSidecarLog(csvPath, report);

        Log.Info($"{CommandName}: {result.Processed} room(s), {result.CsvRows.Count} material row(s), " +
                 $"{result.ElementsWritten} element write(s), report={csvPath}");

        var paintedRows = result.CsvRows.Count(r => r.Painted);
        var paintedM2 = result.CsvRows.Where(r => r.Painted).Sum(r => r.AreaSqM);
        var otherM2 = result.CsvRows.Where(r => !r.Painted).Sum(r => r.AreaSqM);

        var summary = new TaskDialog(CommandName)
        {
            MainInstruction = $"Measured {result.Processed} room(s).",
            MainContent =
                $"{result.SkippedUnplaced} unplaced/unenclosed room(s) ignored.\n" +
                $"{result.CsvRows.Count} material row(s), {result.ElementsWritten} element parameter write(s).\n" +
                $"{result.ElementsTagged} element(s) stamped with {settings.ApartmentParameter} / " +
                $"{settings.RoomNumberParameter} / {settings.RoomNameParameter}" +
                (result.SharedElements == 0
                    ? "."
                    : $", of which {result.SharedElements} carry finish for more than one room and " +
                      "hold the room that contributed most - listed in the log.") + "\n\n" +
                $"Paint basis: {paintedRows} painted row(s) = {paintedM2:0.000} m² " +
                $"(filter Is Painted = Yes), plus {otherM2:0.000} m² on unpainted layer materials, " +
                "which paint costing must exclude.",
            ExpandedContent = string.Join(Environment.NewLine, result.Report.Take(60)),
            FooterText = $"CSV: {csvPath}",
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        summary.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the material CSV");
        summary.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open the per-room log");

        var shown = summary.Show();
        if (shown == TaskDialogResult.CommandLink1)
            Process.Start(new ProcessStartInfo { FileName = csvPath, UseShellExecute = true });
        else if (shown == TaskDialogResult.CommandLink2)
            Process.Start(new ProcessStartInfo { FileName = logPath, UseShellExecute = true });

        return Result.Succeeded;
    }

    /// <summary>
    /// The first paragraph of the dialog: what the automation is doing right now.
    ///
    /// It leads because it decides whether the rest of the dialog is even needed. If
    /// automatic updating is on and nothing is pending, the numbers in the schedule are
    /// already correct and this run only produces the CSV.
    /// </summary>
    private static string AutomationBanner(Document doc)
    {
        if (!IsStaleParameterBound(doc))
        {
            return
                $"AUTOMATIC UPDATING IS UNAVAILABLE: this model has no '{FinishAutomation.StaleParameter}' " +
                "parameter on Rooms, so changed rooms cannot be recorded. Add it as a Yes/No " +
                "project parameter bound to Rooms. Until then the areas are only as current as " +
                "the last time someone ran this.";
        }

        if (!FinishAutomation.Enabled)
        {
            return
                "Automatic updating is OFF. The areas are as current as the last run — " +
                "if the model has moved since, the schedule is behind.";
        }

        var stale = CountStale(doc);
        var finish = stale == 0
            ? "Automatic updating is ON and nothing is pending: every room's areas already match " +
              "the model. Running this now changes no numbers; it exports the material CSV."
            : $"Automatic updating is ON, with {stale} room(s) queued. They are recalculated a few " +
              "seconds after you stop editing, when a schedule is opened, and on save. Running " +
              "this now does it immediately.";

        return finish + "\n\n" + OpeningBanner();
    }

    /// <summary>
    /// The state of the two door/window resolvers, which no longer have buttons of their
    /// own. It belongs here because this is where someone comes when a number looks wrong,
    /// and "lining is not writing yet" is one of the answers.
    /// </summary>
    private static string OpeningBanner()
    {
        var udvendig = OpeningAutomation.UdvendigEnabled
            ? "Udvendig room references: resolved automatically."
            : "Udvendig room references: OFF.";

        var lining = OpeningAutomation.LiningEnabled
            ? "Door/window linings: clashes resolved automatically, including Lining Change, " +
              "and a door's Lining YN and material carried to the windows it touches."
            : "Door/window linings: OFF.";

        return udvendig + "\n" + lining;
    }

    private static void ToggleAutomation()
    {
        var wanted = !FinishAutomation.Enabled;
        FinishAutomation.SetEnabled(wanted);

        TaskDialog.Show("Finish Surface Area", wanted
            ? "Automatic updating is on.\n\nAreas refresh a few seconds after you stop editing, " +
              "when a schedule is opened, and on save. The schedule is a live view, so the new " +
              "numbers appear without reopening it."
            : "Automatic updating is off.\n\nThe areas now only change when you run Finish " +
              "Surface Area yourself. Anything you edit from here on leaves the schedule behind.");
    }

    private static Room? FirstSelectedRoom(CommandContext ctx) =>
        ctx.UiDocument.Selection.GetElementIds()
            .Select(id => ctx.Document.GetElement(id))
            .OfType<Room>()
            .FirstOrDefault();

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

    /// <summary>
    /// Comma-separated with a UTF-8 BOM, exactly as the graph wrote it, so existing
    /// spreadsheets and pivot tables keep working.
    /// </summary>
    private static void WriteMaterialCsv(string path, IReadOnlyList<FinishCsvRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "Lejlighed,Room Number,Room Name,Surface,Type,Host Id,Material,Material Code,Is Painted,Area (m2)");

        foreach (var row in rows)
        {
            // Blank rather than "-1" for the fallback bucket: a spreadsheet reading -1 as an
            // element id would send someone looking for an element that does not exist.
            var hostId = row.HostId < 0
                ? string.Empty
                : row.HostId.ToString(CultureInfo.InvariantCulture);

            var fields = new[]
            {
                row.Apartment, row.RoomNumber, row.RoomName, row.Surface, row.HostType, hostId,
                row.Material, row.MaterialCode, row.Painted ? "Yes" : "No",
            };

            foreach (var field in fields)
            {
                var text = (field ?? string.Empty).Replace('"', '\'');
                sb.Append(text.Contains(',') ? '"' + text + '"' : text).Append(',');
            }

            sb.AppendLine(row.AreaSqM.ToString("0.000", CultureInfo.InvariantCulture));
        }

        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
