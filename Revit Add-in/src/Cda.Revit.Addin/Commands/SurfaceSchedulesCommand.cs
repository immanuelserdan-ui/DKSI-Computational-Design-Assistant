using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Schedules;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Rebuilds the three "&lt;surface&gt; Surface Area by Room and Face" schedules and opens them.
///
/// IT DOES NOT MEASURE ANYTHING. The rows these views schedule are placed by Painted Surface
/// Area, which belongs to the standalone Painted Material Takeoff product. This builds the
/// VIEWS over those rows and nothing else, so on a model where the takeoff has never run it
/// produces three correctly-shaped empty schedules and says so.
///
/// WHY IT EXISTS WHEN THE TAKEOFF ALREADY CREATES THEM
///   Because the takeoff creates them once. Nothing puts them back afterwards:
///
///     - someone deletes a view and does not want to re-run a whole takeoff to get it back
///     - someone edits a view's columns by hand and it stops matching the other two
///     - a template or a fresh model needs the three views before any takeoff has run
///
///   All three are repairs, which is why <see cref="SurfaceScheduleBuilder"/> adds missing
///   columns and never removes extra ones. An extra column is somebody's decision; a missing
///   one is never deliberate.
///
/// ONE TRANSACTION for all three, so a single Ctrl+Z reverts the lot. Opening the views is
/// deliberately OUTSIDE it - view visibility is not a document edit and has no business in
/// anyone's undo stack.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class SurfaceSchedulesCommand : CommandBase
{
    protected override string CommandName => "Surface Schedules";

    private static readonly SurfaceScheduleVisibility.Surfaces[] All =
    [
        SurfaceScheduleVisibility.Surfaces.Wall,
        SurfaceScheduleVisibility.Surfaces.Floor,
        SurfaceScheduleVisibility.Surfaces.Ceiling,
    ];

    protected override Result Run(CommandContext ctx)
    {
        var doc = ctx.Document;
        var problems = new List<string>();
        var built = new List<string>();

        Transactions.Run(doc, CommandName, () =>
        {
            foreach (var surface in All)
            {
                var schedule = SurfaceScheduleBuilder.Build(doc, surface, problems);
                if (schedule is not null) built.Add(SurfaceScheduleVisibility.ScheduleNameOf(surface));
            }
        });

        if (built.Count == 0)
        {
            TaskDialog.Show(CommandName,
                "No schedule could be built.\n\n" +
                string.Join("\n", problems) +
                $"\n\nSee {Log.CurrentFile}");
            return Result.Failed;
        }

        // Outside the transaction, and after it: the views must exist before they can be opened.
        var opening = SurfaceScheduleVisibility.Apply(
            ctx.UiDocument,
            SurfaceScheduleVisibility.Surfaces.Wall |
            SurfaceScheduleVisibility.Surfaces.Floor |
            SurfaceScheduleVisibility.Surfaces.Ceiling);

        var lines = new List<string>
        {
            $"{built.Count} schedule(s) ready:",
            "",
            string.Join("\n", built.Select(b => $"  {b}")),
            "",
            "Columns that were missing have been added; existing columns were left alone, " +
            "including any this tool does not know about.",
            "",
            "No areas were measured. These are the views over the takeoff's rows - run " +
            "Painted Surface Area to produce or refresh the rows themselves.",
        };

        if (problems.Count > 0)
            lines.Add("\n" + string.Join("\n", problems));

        if (opening.Count > 0)
            lines.Add("\n" + string.Join("\n", opening));

        Log.Info($"{CommandName}: {built.Count} schedule(s), {problems.Count} problem(s).");

        TaskDialog.Show(CommandName, string.Join("\n", lines));

        return Result.Succeeded;
    }
}
