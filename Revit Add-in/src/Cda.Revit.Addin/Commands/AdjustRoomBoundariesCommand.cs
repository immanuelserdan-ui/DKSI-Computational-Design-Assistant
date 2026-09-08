using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Finishes;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Corrects room boundaries against the ceilings, slabs and roofs above them. Nothing else.
///
/// WHY IT IS SEPARATE FROM THE FINISH ENGINE
///   These two corrections used to be private steps inside Finish Surface Area, so the only
///   way to get a model's room volumes right was to run a full measurement pass as a side
///   effect - binding seven shared parameters, writing areas onto every room and element, and
///   exporting a CSV. That is a lot of model change to accept when the thing you wanted was
///   for the rooms to stop at the ceiling.
///
/// WHAT IT WRITES, IN FULL
///   1. 'Areas and Volumes' on the document, only if it was off. Without it Revit does not
///      clip rooms against bounding ceilings at all.
///   2. Room Upper Offset, RAISE-ONLY, on rooms whose limit sits below the ceiling, slab or
///      roof overlapping them. A sloped ceiling above the limit otherwise leaves the room
///      sliced flat, missing the wedge beneath the slope.
///
///   No parameters are created or bound. No areas are measured. No CSV is written. If neither
///   correction applies, this command changes nothing at all and says so.
///
/// WHAT IT DELIBERATELY DOES NOT DO: TOUCH ROOM BOUNDING
///   A mezzanine slab or hanging wall clips the room it stands inside, and no upper limit can
///   undo that - Revit stops a room's volume at the NEAREST bounding element, so the mezzanine
///   wins over the ceiling however high the limit goes. The only lever is the element's own
///   Room Bounding flag, and this command does not pull it.
///
///   That was tried, on 2026-09-08, both as a hand-typed marker and as a geometric detector.
///   The detector unchecked NINE elements on the first real model, of which four were the
///   walls of the structure one storey up: their base is naturally clear of the room below,
///   and the room's own limit envelope reached high enough to contain them, so they read as
///   hanging walls. A containment guard written against measured bounding boxes did not catch
///   it.
///
///   It should not be tried a third time without a different idea, because the office
///   convention makes the whole feature unnecessary: mezzanines and hanging walls are modelled
///   Room Bounding = YES, which is what PaintedMaterialTakeoff's InteriorElementCalculator
///   requires to find them at all (see RoomFinishCalculator.FindInteriorElements). A room
///   clipped at a mezzanine is the accepted cost of the takeoff seeing that mezzanine.
///
/// ONE TRANSACTION, so a single Ctrl+Z reverts the whole thing including the volumes setting.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class AdjustRoomBoundariesCommand : CommandBase
{
    protected override string CommandName => "Adjust Room Boundaries";

    protected override Result Run(CommandContext ctx)
    {
        var doc = ctx.Document;
        var adjuster = new RoomBoundaryAdjuster(doc, new FinishSettings());

        RoomBoundaryAdjuster.Result? result = null;

        Transactions.Run(doc, CommandName, () => result = adjuster.Run());

        if (result is null)
        {
            TaskDialog.Show(CommandName,
                $"The pass did not complete. See {Log.CurrentFile}");
            return Result.Failed;
        }

        var lines = new List<string>();

        if (!result.ChangedAnything)
        {
            // A "nothing happened" that is a PASS, not a failure - and worth saying in full,
            // because a silent dialog on a tool that writes to the model reads as broken.
            lines.Add("Nothing needed changing.");
            lines.Add(
                "\n'Areas and Volumes' is already on, and every room's upper limit already " +
                "clears the ceilings, slabs and roofs above it. Room volumes are bounded " +
                "correctly as the model stands.");
        }
        else
        {
            if (result.VolumesEnabled)
                lines.Add("'Areas and Volumes' was OFF and has been enabled.");

            if (result.RoomsAdjusted > 0)
                lines.Add($"{result.RoomsAdjusted} room(s) had their upper limit raised.");

            lines.Add(
                "\nRaise-only: no room's limit was lowered, so a room that was already bounded " +
                "correctly is untouched. The ceiling still clips the volume, so the extra " +
                "headroom above it changes nothing you can measure.");
        }

        lines.Add("\nNo parameters were bound and no areas were measured - this command only " +
                  "moves room boundaries. One Ctrl+Z reverts it.");

        if (result.Notes.Count > 0) lines.Add("\n" + string.Join("\n", result.Notes));

        Log.Info($"{CommandName}: volumes enabled={result.VolumesEnabled}, " +
                 $"{result.RoomsAdjusted} room(s) raised.");

        TaskDialog.Show(CommandName, string.Join("\n", lines));

        return Result.Succeeded;
    }
}
