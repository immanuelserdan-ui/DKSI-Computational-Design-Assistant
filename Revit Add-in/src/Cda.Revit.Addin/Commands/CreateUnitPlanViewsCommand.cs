using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Views;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// One cropped floor plan per apartment unit.
///
/// Rooms are grouped by a unit identifier held on a room parameter, each group's rooms are
/// unioned into a bounding box, and a duplicate of the active plan is cropped to it and
/// renamed to the project's document-ID convention.
///
/// The active view is the source deliberately: what the unit plans should inherit is the
/// storey plan the user is currently looking at, with its filters, overrides and detailing
/// already correct. Falling back to a fresh ViewPlan when there is no usable source keeps
/// the command working from a schedule or a 3D view, at the cost of unstyled output.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class CreateUnitPlanViewsCommand : CommandBase
{
    protected override string CommandName => "Unit Plan Views";

    protected override Result Run(CommandContext ctx)
    {
        var settings = new UnitViewSettings();
        var source = ctx.UiDocument.ActiveView as ViewPlan;
        var builder = new UnitPlanViewBuilder(ctx.Document, settings);

        // Group before asking anything, so the dialog can state the real number of units
        // rather than a promise. This reads the model only - no transaction needed, and the
        // result is handed to Run below so the model is not scanned a second time.
        var preview = builder.Collect(out var warnings);
        if (preview.Count == 0)
        {
            new TaskDialog(CommandName)
            {
                MainInstruction = "No units found.",
                MainContent =
                    $"No placed room carries a usable value in '{settings.UnitParameterName}'.\n\n" +
                    (settings.UnitPattern is null
                        ? "That parameter's value is used verbatim as the unit key - check the unit numbers are filled in."
                        : $"Rooms are grouped by the first '{settings.UnitPattern}' match in that parameter - " +
                          "check the unit numbers are filled in, or point UnitParameterName at the " +
                          "parameter that actually holds them.") +
                    (warnings.Count > 0 ? "\n\n" + string.Join("\n", warnings) : string.Empty),
                CommonButtons = TaskDialogCommonButtons.Close,
            }.Show();

            return Result.Cancelled;
        }

        var sourceNote = source is null
            ? "The active view is not a floor plan, so each unit gets a new, unstyled plan view. " +
              "Cancel and open the storey plan you want copied to get filters and detailing carried across."
            : $"Each unit gets a duplicate of '{source.Name}', with its detailing.";

        var choice = new TaskDialog(CommandName)
        {
            MainInstruction = $"Create {preview.Count} unit plan view(s)?",
            MainContent =
                $"{preview.Count} unit(s) found across {preview.Select(g => g.LevelName).Distinct().Count()} level(s), " +
                $"grouped by '{settings.UnitParameterName}'.\n\n" +
                $"{sourceNote}\n\n" +
                $"Each view is cropped to its own rooms plus a {settings.MarginMm:0} mm margin and named " +
                $"to the pattern {settings.NameFormat}.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
        };
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            "Dry run", "List the views and names that would be created. Nothing is modified.");
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
            "Apply", "Create, crop and rename the views. One Ctrl+Z reverts all of them.");

        var apply = choice.Show() switch
        {
            TaskDialogResult.CommandLink1 => false,
            TaskDialogResult.CommandLink2 => true,
            _ => throw new OperationCanceledException(),
        };

        var result = builder.Run(apply, source, preview, warnings);

        Log.Info($"{CommandName}: apply={apply}, units={preview.Count}, warnings={result.Warnings.Count}");

        var detail = result.Rows
            .Select(r => $"{r.Unit}  {r.Level,-16}  {r.Rooms,2} room(s)  {r.ViewName}  [{r.Outcome}]")
            .Concat(result.Warnings.Count > 0 ? ["", "WARNINGS:"] : Array.Empty<string>())
            .Concat(result.Warnings.Select(w => "  " + w));

        new TaskDialog(CommandName)
        {
            MainInstruction = apply ? "Done." : "Dry run complete - nothing was modified.",
            MainContent = string.Join("\n", result.Summary),
            ExpandedContent = string.Join(Environment.NewLine, detail),
            CommonButtons = TaskDialogCommonButtons.Close,
        }.Show();

        return Result.Succeeded;
    }
}
