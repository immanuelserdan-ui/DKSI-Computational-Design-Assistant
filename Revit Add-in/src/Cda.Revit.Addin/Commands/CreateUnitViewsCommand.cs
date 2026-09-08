using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Views;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// The per-unit deliverable, plans and 3D together.
///
/// This was two commands. Merging them is worth it because the expensive and error-prone part
/// - deciding which rooms form a unit - is identical for both, and running it twice invited
/// the two halves to disagree about what a unit was. Now the model is scanned once and both
/// passes are handed the same groups.
///
/// The masters are resolved BY NAME rather than from the active view, so one command can feed
/// two passes that need different sources. That only became possible once the 3D camera was
/// fixed to Top-Front-Right: while orientation was inherited, the 3D pass needed you to be
/// standing in the right 3D view, which a merged command cannot arrange.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class CreateUnitViewsCommand : CommandBase
{
    protected override string CommandName => "Unit Views";

    private enum Scope { DryRun, Both, PlansOnly, ThreeDOnly }

    protected override Result Run(CommandContext ctx)
    {
        var shared = new UnitViewSettings();

        // The 3D master is optional here. It contributes graphics and template, not the camera
        // any more, so its absence costs appearance rather than correctness - and refusing to
        // run would block the plans too.
        var settings3d = new Unit3dViewSettings { RequireMasterView = false };

        var planBuilder = new UnitPlanViewBuilder(ctx.Document, shared);
        var builder3d = new Unit3dViewBuilder(ctx.Document, shared, settings3d);

        var warnings = new List<string>();
        var planMaster = planBuilder.ResolveMaster(ctx.UiDocument.ActiveView as ViewPlan, warnings);
        var master3d = builder3d.ResolveMaster(ctx.UiDocument.ActiveView as View3D, warnings);

        // Scanned ONCE, then shared. This is the whole point of the merge.
        var groups = planBuilder.Collect(out var groupingWarnings);
        warnings.AddRange(groupingWarnings);

        if (groups.Count == 0)
        {
            new TaskDialog(CommandName)
            {
                MainInstruction = "No units found.",
                MainContent =
                    $"No placed room carries a usable value in '{shared.UnitParameterName}'.\n\n" +
                    (warnings.Count > 0 ? string.Join("\n", warnings) : string.Empty),
                CommonButtons = TaskDialogCommonButtons.Close,
            }.Show();

            return Result.Cancelled;
        }

        var planNote = planMaster is null
            ? "NO MASTER PLAN - plans would be created empty. Open the annotated storey plan first."
            : $"Plans duplicate '{planMaster.Name}' ({planMaster.ViewType}), carrying its {planBuilder.AnnotationSummary(planMaster)}.";

        var note3d = master3d is null
            ? "3D views are created fresh (no master found), so they carry Revit's default graphics."
            : $"3D views duplicate '{master3d.Name}' for its graphics.";

        var choice = new TaskDialog(CommandName)
        {
            MainInstruction = $"Create views for {groups.Count} unit(s)?",
            MainContent =
                $"{groups.Count} unit(s), grouped by '{shared.UnitParameterName}'.\n\n" +
                $"{planNote}\n\n{note3d}\n\n" +
                $"Plans are cropped and 3D views section-boxed to each unit's own rooms. " +
                $"3D uses the Top-Front-Right isometric.\n\n" +
                $"A unit spanning two storeys gets one plan per storey - a plan can only bind to " +
                $"one level - but a single 3D view covering the whole apartment.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel,
        };
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            "Dry run", "List everything that would be created. Nothing is modified.");
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
            "Apply - plans and 3D", "Both deliverables for every unit. One Ctrl+Z reverts the lot.");
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink3,
            "Apply - plans only", "Skip the 3D pass.");
        choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink4,
            "Apply - 3D only", "Skip the plans.");

        var scope = choice.Show() switch
        {
            TaskDialogResult.CommandLink1 => Scope.DryRun,
            TaskDialogResult.CommandLink2 => Scope.Both,
            TaskDialogResult.CommandLink3 => Scope.PlansOnly,
            TaskDialogResult.CommandLink4 => Scope.ThreeDOnly,
            _ => throw new OperationCanceledException(),
        };

        var apply = scope != Scope.DryRun;
        var doPlans = scope is Scope.DryRun or Scope.Both or Scope.PlansOnly;
        var do3d = scope is Scope.DryRun or Scope.Both or Scope.ThreeDOnly;

        UnitViewResult? planResult = null;
        UnitViewResult? result3d = null;

        var planWarnings = new List<string>();
        var warnings3d = new List<string>();

        void RunPasses()
        {
            // Plans first so their names are already taken when the 3D pass seeds its own
            // uniqueness set - the two formats differ by the TYPE token, but ordering makes
            // that a belt-and-braces guarantee rather than a dependency on configuration.
            if (doPlans) planResult = planBuilder.Run(apply, planMaster, groups, planWarnings);

            // The 3D pass takes the SAME scan but merged across levels, so a maisonette gets
            // two plans and one 3D rather than being halved in both.
            if (do3d) result3d = builder3d.Run(apply, master3d, Unit3dViewBuilder.MergeAcrossLevels(groups), warnings3d);
        }

        // One outer group so a merged run is a single undo step, rather than one per pass.
        // Nested groups are legal; each builder still opens its own inside this.
        if (apply)
            Transactions.RunGrouped(ctx.Document, CommandName, RunPasses);
        else
            RunPasses();

        Log.Info($"{CommandName}: scope={scope}, units={groups.Count}, " +
                 $"planWarnings={planWarnings.Count}, warnings3d={warnings3d.Count}");

        // Prefer showing a plan; it is the primary deliverable. The 3D view is the fallback
        // so that "Apply - 3D only" still lands somewhere visible.
        var toShow = planResult?.CreatedViewIds.FirstOrDefault()
                     ?? result3d?.CreatedViewIds.FirstOrDefault();

        if (apply && toShow is not null && toShow != ElementId.InvalidElementId)
            OpenAndZoom(ctx, toShow);

        ShowSummary(scope, apply, warnings, planResult, planWarnings, result3d, warnings3d);

        return Result.Succeeded;
    }

    private void ShowSummary(
        Scope scope,
        bool apply,
        List<string> shared,
        UnitViewResult? plans,
        List<string> planWarnings,
        UnitViewResult? threeD,
        List<string> warnings3d)
    {
        var summary = new List<string>();

        if (plans is not null)
        {
            summary.Add("PLANS");
            summary.AddRange(plans.Summary.Select(s => "  " + s));
        }

        if (threeD is not null)
        {
            if (summary.Count > 0) summary.Add(string.Empty);
            summary.Add("3D");
            summary.AddRange(threeD.Summary.Select(s => "  " + s));
        }

        static IEnumerable<string> Rows(string label, UnitViewResult? result) =>
            result is null
                ? []
                : result.Rows.Select(r => $"{label}  {r.Unit}  {r.Level,-16}  {r.Rooms,2} room(s)  {r.ViewName}  [{r.Outcome}]");

        // Warnings are labelled by pass. An unlabelled merged list makes a 3D problem look
        // like a plan problem, which is exactly the confusion the merge could have introduced.
        var allWarnings = shared
            .Concat(planWarnings.Select(w => "[plans] " + w))
            .Concat(warnings3d.Select(w => "[3D] " + w))
            .Distinct()
            .ToList();

        var detail = Rows("2D", plans)
            .Concat(Rows("3D", threeD))
            .Concat(allWarnings.Count > 0 ? ["", "WARNINGS:"] : Array.Empty<string>())
            .Concat(allWarnings.Select(w => "  " + w));

        new TaskDialog(CommandName)
        {
            MainInstruction = apply ? "Done." : "Dry run complete - nothing was modified.",
            MainContent = summary.Count > 0 ? string.Join("\n", summary) : "Nothing to report.",
            ExpandedContent = string.Join(Environment.NewLine, detail),
            CommonButtons = TaskDialogCommonButtons.Close,
        }.Show();
    }

    /// <summary>Opens one created view and zooms it to fit. Cosmetic, so failure is logged only.</summary>
    private static void OpenAndZoom(CommandContext ctx, ElementId viewId)
    {
        try
        {
            if (ctx.Document.GetElement(viewId) is not View view) return;

            ctx.UiDocument.ActiveView = view;
            ctx.UiDocument.RefreshActiveView();

            foreach (var uiView in ctx.UiDocument.GetOpenUIViews())
            {
                if (uiView.ViewId == viewId) uiView.ZoomToFit();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Unit Views: could not open and zoom the created view - {ex.Message}");
        }
    }
}
