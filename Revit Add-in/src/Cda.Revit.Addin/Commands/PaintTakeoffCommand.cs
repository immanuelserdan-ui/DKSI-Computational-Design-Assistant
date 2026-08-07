using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Finishes;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Schedules;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Builds the per-room paint takeoff - the schedule a Wall Material Takeoff cannot be.
///
/// THE PROBLEM IT SOLVES, IN ONE LINE
///   A room shows only the walls it OWNS, not every face that touches it. 'Rum' lives on the
///   wall, one wall carries one value, and a wall between two rooms gives that value to one of
///   them - so a bathroom with four painted faces arrives as two rows, and the paint on the
///   two faces whose walls belong to the neighbours is simply absent.
///
///   Measured in the test model: Bad's paint totals 21.26 m2; the wall takeoff showed 11.91.
///
/// WHAT IT DOES
///   Runs the finish engine, then places one Generic Model row per (room, surface, material)
///   from the engine's own per-room results, and finds or creates a schedule over them. Four
///   faces in, four rows out, and the room on each row is the room that touches that paint.
///
/// WHY IT RUNS THE ENGINE RATHER THAN READING PARAMETERS
///   The element parameters are the lossy form - that is the whole problem. The per-room
///   figures only exist inside a calculation pass, so the takeoff is built from a fresh one.
///   It costs a few seconds and removes any chance of the takeoff disagreeing with the report.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class PaintTakeoffCommand : CommandBase
{
    protected override string CommandName => "Paint Takeoff";

    protected override Result Run(CommandContext ctx)
    {
        var doc = ctx.Document;
        var settings = new FinishSettings();

        // Parameters first, and this is not optional politeness: the rows carry six shared
        // parameters, and a row placed before they are bound is a row with no data on it.
        //
        // INSIDE A TRANSACTION, which is the whole reason the first run of this command
        // failed. Creating a shared parameter and widening a binding both modify the model.
        // Outside a transaction the creation THROWS ("Attempt to modify the model outside of
        // transaction") while ParameterBindings.ReInsert quietly returns a bare false - so the
        // same missing transaction produced two completely different-looking errors, one of
        // which read as Revit refusing the widening on its own merits.
        var setup = new FinishParameterSetup(doc, settings);

        var setupWarnings = new List<string>();

        if (setup.AnythingMissing())
        {
            ParameterSetupResult? parameters = null;

            Transactions.Run(doc, CommandName + " - parameters", () => parameters = setup.Run());

            if (parameters is { Problems.Count: > 0 })
            {
                // NOT EVERY BINDING FAILURE IS FATAL, and treating them alike cost a user a
                // whole cycle: 'Lejlighed' - an apartment GROUPING column - refused to widen,
                // and the command abandoned the run, so a model with perfectly good paint
                // areas produced no takeoff at all over a field nobody was going to price.
                //
                // These four are the takeoff. Without the area there is no quantity; without
                // the room there is nothing to attribute it to, which is the entire reason
                // this schedule exists instead of a Wall Material Takeoff. Anything else
                // costs a column.
                string[] essential =
                [
                    settings.PaintAreaParameter,
                    settings.RoomNameParameter,
                    settings.RoomNumberParameter,
                    settings.PaintMaterialParameter,
                ];

                var fatal = parameters.FailedParameters
                    .Where(name => essential.Contains(name, StringComparer.Ordinal))
                    .ToList();

                if (fatal.Count > 0)
                {
                    TaskDialog.Show(CommandName,
                        $"{string.Join(", ", fatal)} could not be bound. That is the takeoff " +
                        "itself - the quantity and the room it belongs to - so the rows would " +
                        "carry nothing worth scheduling.\n\n" +
                        string.Join("\n", parameters.Problems) +
                        $"\n\nSee {Log.CurrentFile}");
                    return Result.Failed;
                }

                // Reported at the END of a successful run rather than as a blocking dialog: the
                // user asked for paint quantities, and they are about to get correct ones.
                setupWarnings.Add(
                    "SOME COLUMNS WILL BE BLANK. These parameters could not be bound, so the " +
                    "takeoff carries no value for them. The areas and their room attribution " +
                    "are unaffected:\n" + string.Join("\n", parameters.Problems));
            }
        }

        var calculator = new RoomFinishCalculator(doc, settings);
        FinishResult? finish = null;

        // Same requirement: the engine writes room and element parameters as it measures.
        Transactions.Run(doc, CommandName + " - measure", () => finish = calculator.Run());

        if (finish is null)
        {
            TaskDialog.Show(CommandName,
                $"The measurement pass did not complete. See {Log.CurrentFile}");
            return Result.Failed;
        }

        if (finish.CsvRows.Count == 0)
        {
            TaskDialog.Show(CommandName,
                "The engine measured nothing, so there is no takeoff to build.\n\n" +
                "That usually means no rooms are placed, or nothing in them is painted.");
            return Result.Cancelled;
        }

        var takeoff = PaintTakeoffBuilder.Build(doc, settings, finish.CsvRows);

        var problems = new List<string>();
        ViewSchedule? schedule = null;

        try
        {
            Transactions.Run(doc, PaintTakeoffBuilder.TransactionPrefix + "schedule",
                () => schedule = PaintTakeoffBuilder.EnsureSchedule(doc, settings, problems));
        }
        catch (Exception ex)
        {
            problems.Add($"The schedule could not be created: {ex.Message}");
        }

        var lines = new List<string>
        {
            $"{takeoff.Placed} takeoff row(s) placed, {takeoff.TotalSqm:0.00} m² of paint.",
            $"{finish.Processed} room(s) measured.",
        };

        if (takeoff.Removed > 0) lines.Add($"{takeoff.Removed} row(s) from a previous run removed.");

        lines.Add(schedule is not null
            ? $"Schedule: '{PaintTakeoffBuilder.ScheduleName}' in the project browser under Schedules."
            : "The schedule could not be created; the rows exist and can be scheduled by hand.");

        // "on one wall", not "on one surface": since the row key gained the host, a room's four
        // painted faces give four rows even when two share a material. The old wording described
        // the version before that and undersold what the schedule now does.
        lines.Add(
            "\nEvery row is one room's own measured area on one wall in one material, so a " +
            "wall painted on both faces appears once for each room it faces. This is the " +
            "takeoff to issue - a Wall Material Takeoff cannot carry a room column and drops " +
            "the face whose wall belongs to the neighbour.");

        if (takeoff.Notes.Count > 0) lines.Add("\n" + string.Join("\n", takeoff.Notes));
        if (problems.Count > 0) lines.Add("\n" + string.Join("\n", problems));
        if (setupWarnings.Count > 0) lines.Add("\n" + string.Join("\n", setupWarnings));

        Log.Info($"{CommandName}: {takeoff.Placed} row(s), {takeoff.TotalSqm:0.00} m², " +
                 $"{finish.Processed} room(s).");

        TaskDialog.Show(CommandName, string.Join("\n", lines));

        return Result.Succeeded;
    }
}
