using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Doors;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Port of Resolve-Udvendig-Rooms_v1.0.dyn. Replaces exterior room references on doors
/// with the room on the other side, writing the 02/03/04/05-SCRP parameters.
///
/// It does NOT modify schedules. It once re-pointed every built-in From/To Room column in the
/// model at those parameters; that is off by default now - see UdvendigSettings.RepointSchedules.
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

        // PREREQUISITE FIRST. Without the SCRP parameters on Doors every door is skipped and
        // the run reports success having written nothing - which is what it did, silently, on
        // every model change for days. Offered before the main dialog so the answer to "why
        // did nothing happen" arrives before the question.
        OfferScrpBinding(ctx);

        // NO DRY RUN ANY MORE. It existed while the tool was unproven and every run had to be
        // inspected before it was trusted. Now the same resolver applies unattended on every
        // model change and on document open, so a report-only path from the button would be
        // reporting on work the automation has already done - a question nobody is asking.
        //
        // The confirmation stays: this sweeps every door in the model by default, and an
        // explicit click deserves an explicit scope. Resolver.Run(apply: false, ...) is still
        // there if a dry run is ever wanted back.
        var choice = new TaskDialog(CommandName)
        {
            MainInstruction = $"Resolve exterior room references on {scope}?",
            MainContent =
                "Where a door's FROM or TO side reads 'Udvendig...', it is replaced by the room on " +
                "the other side, and written to the SCRP parameters.\n\n" +
                "Schedules are NOT modified. Revit's own From/To Room columns are left exactly as " +
                "they are; add the SCRP parameters as schedule fields wherever you want them.\n\n" +
                "One undo step reverts the whole run.",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Yes,
        };

        if (choice.Show() != TaskDialogResult.Yes) throw new OperationCanceledException();

        // Built from the SAME setting the automatic pass uses - see
        // OpeningAutomation.DeriveDoorSidesFromFacing. A manual run that resolved sides by a
        // different rule would silently overwrite whatever the automation had just written, and
        // the two would fight on every edit.
        var resolver = new UdvendigRoomResolver(ctx.Document, new UdvendigSettings
        {
            DeriveSidesFromFacing = Automation.OpeningAutomation.DeriveDoorSidesFromFacing,
            RepointSchedules = Automation.OpeningAutomation.RepointScheduleColumns,
            RestoreBuiltInRoomColumns = Automation.OpeningAutomation.RestoreBuiltInRoomColumns,
            RestoreFromToScheduleColumns = Automation.OpeningAutomation.RestoreFromToScheduleColumns,
            RevealExtDoorRoomColumns = Automation.OpeningAutomation.RevealExtDoorRoomColumns,
            SubstituteExteriorSide = Automation.OpeningAutomation.SubstituteExteriorSide,
            RepointFromToScheduleColumns = Automation.OpeningAutomation.RepointFromToScheduleColumns,
            FilterExtDoorSchedules = Automation.OpeningAutomation.FilterExtDoorSchedules,
            RemoveExtDoorFilter = Automation.OpeningAutomation.RemoveExtDoorFilter,
            PointExtDoorToSubstituted = Automation.OpeningAutomation.PointExtDoorToSubstituted,
        });

        // The Dynamo graph used two separate transactions - one for the schedule columns, one
        // for the doors. A single transaction here means the whole operation is one undo step,
        // and a failure part-way leaves neither half applied rather than schedules pointing at
        // parameters that were never filled.
        UdvendigResult? captured = null;
        Transactions.Run(ctx.Document, CommandName,
            () => captured = resolver.Run(apply: true, selection));

        var result = captured!;

        var csvPath = ReportWriter.DefaultPath(ctx.Document, "udvendig");
        ReportWriter.WriteCsv(csvPath, result.Rows);

        var log = new List<string>(result.Summary) { string.Empty, "WARNINGS:" };
        log.AddRange(result.Warnings.Count > 0
            ? result.Warnings.Select(w => "  " + w)
            : ["  (none)"]);

        var logPath = ReportWriter.WriteSidecarLog(csvPath, log);

        Log.Info($"{CommandName}: warnings={result.Warnings.Count}, report={csvPath}");

        var summary = new TaskDialog(CommandName)
        {
            MainInstruction = "Done.",
            MainContent = string.Join("\n", result.Summary),
            ExpandedContent = result.Warnings.Count > 0
                ? string.Join(Environment.NewLine, result.Warnings.Take(60))
                : null,
            // BUILD STAMP IN THE FOOTER, for the same reason the radiator and finish dialogs
            // carry one: Revit locks the DLL, so a deploy made while it was running never
            // landed and the session is still on the previous build. The symptom - "the fix
            // did not change anything" - is indistinguishable from a bad fix until you can
            // see which build is actually loaded, and by then the guessing has already cost
            // a round trip.
            FooterText = $"{BuildInfo.Describe()}  ·  Report: {csvPath}",
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

    /// <summary>
    /// Offers to bind SCRP parameters that are in the document but not on Doors.
    ///
    /// ONLY EVER OFFERS WHAT IT CAN DO HONESTLY. A definition already in the document carries
    /// the office GUID, so binding it invents no identity and nothing downstream shifts. A
    /// definition that is absent is reported and NOT created - that one needs the office
    /// shared parameter file, and a GUID minted here would look right and match nothing.
    ///
    /// Silent when every parameter is already bound, which is the normal case.
    /// </summary>
    private void OfferScrpBinding(CommandContext ctx)
    {
        var settings = new UdvendigSettings();

        // THE FOUR ROOM PARAMETERS ONLY - the classification flag is deliberately NOT here.
        //
        // ScrpBinder answers "is this a SHARED parameter definition sitting in the document,
        // unbound?" by enumerating SharedParameterElement. 'CRP Exterior Door' is a PROJECT
        // parameter, so that lookup cannot see it and would report it as "not in this document
        // at all" - on a model where it exists and is already bound to every door. Offering to
        // fix something that is not broken, using a mechanism that could not fix it anyway, is
        // worse than staying quiet. The resolver reports the flag's real state itself.
        // THE SUBSTITUTED PAIR IS INCLUDED, unlike the flag, because it is a shared parameter
        // like the other four and ScrpBinder can genuinely see and bind it. If the office has
        // added the definitions to the document but not bound them to Doors, this is what
        // offers to finish the job - and if they are absent, the dialog says so by name, which
        // is exactly the message someone setting this up for the first time needs.
        var names = new[]
        {
            settings.NumFrom, settings.NameFrom, settings.NumTo, settings.NameTo,
            settings.NumSubstituted, settings.NameSubstituted,
        };

        var binder = new ScrpBinder(ctx.Document);
        var survey = binder.Survey(names);

        var bindable = survey.Where(s => s.Bindable).Select(s => s.Name).ToList();
        var absent = survey.Where(s => !s.InDocument).Select(s => s.Name).ToList();

        // BOUND TO DOORS, BUT AS TYPE PARAMETERS. Reported, because the resolver writes through
        // instance parameters only and therefore writes nothing to these - while the old survey
        // called them correctly bound, so this prompt stayed silent and the run reported success
        // over a schedule that never changed. Not repaired automatically: rebinding type to
        // instance discards whatever the type binding holds, which is not this tool's call to
        // make on office shared parameters.
        var typeBound = survey.Where(s => s.NeedsRebinding).Select(s => s.Name).ToList();

        var typeBoundNote = typeBound.Count == 0
            ? string.Empty
            : "\n\nBOUND TO DOORS AS TYPE PARAMETERS, WHICH WILL NOT WORK: " +
              string.Join(", ", typeBound) +
              ".\nTwo doors of the same type face different rooms, so these values have to live " +
              "per instance - a type binding means every door of a type would share one answer, " +
              "and this tool cannot write to them at all. Rebind them to Doors as INSTANCE " +
              "parameters in Manage > Project Parameters. That is not done here because it " +
              "discards whatever the type binding currently holds.";

        if (bindable.Count == 0 && absent.Count == 0 && typeBound.Count == 0)
            return;   // all bound, the right way - nothing to say

        if (bindable.Count == 0)
        {
            var lines = new List<string>();

            if (absent.Count > 0)
            {
                lines.Add(
                    "These SCRP parameters are not in this document at all:\n\n  " +
                    string.Join("\n  ", absent) +
                    "\n\nThey have to be added from the office shared parameter file and bound to " +
                    "the Doors category. This tool will not create them: their GUIDs must match the " +
                    "ones the door schedules already use, and a new GUID would look correct here " +
                    "and line up with nothing else.");
            }

            if (typeBoundNote.Length > 0) lines.Add(typeBoundNote.TrimStart());

            TaskDialog.Show(CommandName, string.Join("\n\n", lines));
            return;
        }

        var dialog = new TaskDialog(CommandName)
        {
            MainInstruction = $"Bind {bindable.Count} SCRP parameter(s) to Doors first?",
            MainContent =
                "These exist in this document but are not bound to the Doors category, so every " +
                "door is skipped and nothing can be written:\n\n  " +
                string.Join("\n  ", bindable) +
                "\n\nThey already carry the correct GUIDs - binding reuses them, so no other " +
                "model or schedule shifts. Bound as INSTANCE parameters, because two doors of " +
                "the same type face different rooms." +
                (absent.Count > 0
                    ? "\n\nNot in this document, and NOT created here: " + string.Join(", ", absent)
                    : string.Empty) +
                typeBoundNote,
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.Yes,
        };

        if (dialog.Show() != TaskDialogResult.Yes) return;

        IReadOnlyList<ScrpBinder.Outcome> outcomes = [];

        Transactions.Run(ctx.Document, "Bind SCRP parameters to Doors",
            () => outcomes = binder.Bind(bindable));

        foreach (var outcome in outcomes)
            Log.Info($"SCRP binding - {outcome.Name}: {(outcome.Success ? "OK" : "FAILED")} - {outcome.Detail}");

        TaskDialog.Show(CommandName,
            string.Join("\n", outcomes.Select(o => $"{(o.Success ? "OK   " : "FAIL ")} {o.Name} - {o.Detail}")));
    }
}
