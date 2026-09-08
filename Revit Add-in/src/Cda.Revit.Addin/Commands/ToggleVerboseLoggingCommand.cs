using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Flips Log.Verbose. Nothing else - it does not touch the document, so there is nothing for
/// a transaction to protect.
///
/// SESSION-ONLY, DELIBERATELY NOT PERSISTED. A settings file that remembered "on" would mean
/// verbose logging silently surviving a Revit restart, days after whoever turned it on for one
/// diagnostic run has forgotten it exists. Resetting to off on every Revit launch is what
/// keeps this a tool you reach for, not a mode you can leave on by accident.
///
/// WHY THIS EXISTS: 2026-09-08's performance investigation added Debug-level timing to
/// RoomFinishCalculator (OccludingElements, CeilingFallback.Resolve, MeasureInteriorSlabs,
/// MeasureInteriorWalls, MeasureReveals) - all of it inert until Log.Verbose is on, per
/// Log.Debug's own gate. This is the switch. There was no ribbon-reachable way to turn it on
/// before this command; editing the field required a debugger attached to Revit.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class ToggleVerboseLoggingCommand : CommandBase
{
    protected override string CommandName => "Verbose Logging";

    protected override Result Run(CommandContext ctx)
    {
        Log.Verbose = !Log.Verbose;

        var message = Log.Verbose
            ? "Verbose logging is now ON.\n\n" +
              "Every automation pass now writes its per-phase timing - including the room-finish " +
              "engine's OccludingElements, CeilingFallback.Resolve, MeasureInteriorSlabs, " +
              "MeasureInteriorWalls and MeasureReveals calls - to:\n\n" +
              $"{Log.CurrentFile}\n\n" +
              "Reproduce the slow operation now, then run this command again to turn logging " +
              "back off. Left on, the log grows fast on a large model - one line per phase per " +
              "room, every automatic pass."
            : "Verbose logging is now OFF.\n\n" +
              $"Log file: {Log.CurrentFile}";

        TaskDialog.Show(CommandName, message);

        Log.Info($"Verbose logging turned {(Log.Verbose ? "ON" : "OFF")} from the ribbon.");

        return Result.Succeeded;
    }
}
