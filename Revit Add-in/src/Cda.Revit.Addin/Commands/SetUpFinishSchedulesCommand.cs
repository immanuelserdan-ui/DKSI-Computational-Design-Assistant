using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Finishes;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Schedules;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// One-click setup for a model that has never run the finish engine: binds every parameter
/// it writes, then builds the material takeoff that can display a ceiling finish measured
/// off something that is not a ceiling.
///
/// It exists because the engine's failure mode without it is quiet and misleading. The
/// numbers are computed correctly, cannot be written anywhere, and the schedule shows
/// blanks - which reads as "the tool does not work" rather than "the parameters are not
/// bound". A dozen trips through the Shared Parameters dialog, in the right order, with the
/// right categories ticked, is not a reasonable thing to ask of anyone.
///
/// Safe to run twice. Existing parameters are widened, never replaced, so data already
/// written under them survives; an existing schedule of the target name is left alone.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class SetUpFinishSchedulesCommand : CommandBase
{
    protected override string CommandName => "Set Up Finish Schedules";

    protected override Result Run(CommandContext ctx)
    {
        var settings = new FinishSettings();
        var setup = new FinishParameterSetup(ctx.Document, settings);
        var builder = new CeilingTakeoffBuilder(ctx.Document, settings);

        var plan = string.Join("\n", setup.Specs()
            .Select(s => $"  • {s.Name}  →  {string.Join(", ", s.Categories.Select(Pretty))}"));

        var choice = new TaskDialog(CommandName)
        {
            MainInstruction = "Prepare this model for the finish engine?",
            MainContent =
                "Binds the parameters the finish tools write to, as SHARED parameters on fixed " +
                "GUIDs so every model uses one definition:\n\n" + plan + "\n\n" +
                $"Then creates '{builder.ScheduleName}' - a multi-category material takeoff over " +
                "Ceilings, Floors and Roofs, filtered to rows that actually carry a ceiling area.\n\n" +
                "Parameters that already exist keep their definition and their data; only the " +
                "missing categories are added to them. Nothing is deleted.",
            ExpandedContent =
                $"Definition file: {FinishParameterSetup.DefinitionFilePath}\n\n" +
                "Why a multi-category takeoff: with no ceilings modelled, a room's ceiling " +
                "finish is measured off the slab above or the roof, and that area is written " +
                "onto the floor or roof element it came from. A schedule limited to the " +
                "Ceilings category has no rows to put it in.",
            FooterText = $"{BuildInfo.Describe()}  ·  Log: {Log.CurrentFile}",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
        };
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            "Bind the parameters and build the schedule",
            "One undo step. Run the finish engine afterwards to fill it in.");
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
            "Bind the parameters only",
            "Leaves the schedules to you.");

        var picked = choice.Show();

        if (picked != TaskDialogResult.CommandLink1 && picked != TaskDialogResult.CommandLink2)
            throw new OperationCanceledException();

        var buildSchedule = picked == TaskDialogResult.CommandLink1;

        ParameterSetupResult? parameters = null;
        TakeoffResult? takeoff = null;

        // Grouped, not one transaction: the schedule can only see a parameter as a
        // schedulable field once the binding is COMMITTED. Both still collapse into a
        // single undo step.
        Transactions.RunGrouped(ctx.Document, CommandName, () =>
        {
            Transactions.Run(ctx.Document, CommandName + " - parameters",
                () => parameters = setup.Run());

            if (buildSchedule)
            {
                Transactions.Run(ctx.Document, CommandName + " - schedule",
                    () => takeoff = builder.Run());
            }
        });

        var report = new List<string>(parameters!.Report);
        var problems = new List<string>(parameters.Problems);

        if (takeoff is not null)
        {
            report.Add(string.Empty);
            report.AddRange(takeoff.Report);
            problems.AddRange(takeoff.Problems);
        }

        var logPath = ReportWriter.WriteSidecarLog(
            ReportWriter.DefaultPath(ctx.Document, "finish-setup"), report);

        Log.Info($"{CommandName}: {parameters.Created} bound, {parameters.Extended} widened, " +
                 $"{parameters.AlreadyCorrect} already correct, " +
                 $"{takeoff?.Created.Count ?? 0} schedule(s) created, {problems.Count} problem(s).");

        var summary = new TaskDialog(CommandName)
        {
            MainInstruction = problems.Count == 0
                ? "This model is ready."
                : $"Done, with {problems.Count} thing(s) to look at.",
            MainContent =
                $"{parameters.Created} parameter(s) bound, {parameters.Extended} widened to reach " +
                $"more categories, {parameters.AlreadyCorrect} already correct." +
                (takeoff is null
                    ? string.Empty
                    : $"\n{takeoff.Created.Count} schedule(s) created.") +
                (problems.Count == 0
                    ? "\n\nRun Finish Surface Area next to fill the numbers in."
                    : "\n\n" + string.Join("\n", problems.Take(6))),
            ExpandedContent = string.Join(Environment.NewLine, report.Take(60)),
            FooterText = $"Report: {logPath}",
            CommonButtons = TaskDialogCommonButtons.Close,
        };

        var first = takeoff?.Created.FirstOrDefault();
        if (first is not null)
            summary.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the new schedule");

        if (summary.Show() == TaskDialogResult.CommandLink1 && first is not null)
            ctx.UiDocument.ActiveView = first;

        return Result.Succeeded;
    }

    /// <summary>Category names for the dialog, without the OST_ the API uses.</summary>
    private static string Pretty(BuiltInCategory category) =>
        category.ToString().Replace("OST_", string.Empty);
}
