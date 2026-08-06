using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Overlay;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Arms and disarms the paint highlight: while it is on, selecting a row in a Material Takeoff
/// schedule draws that element's NET painted area on the surfaces it was measured from,
/// instead of leaving you with the whole host wall highlighted.
///
/// A TOGGLE RATHER THAN A ONE-SHOT. The thing being asked for is a reading MODE - you click
/// down a schedule comparing figures to geometry, and re-pressing a button between every row
/// would defeat the point. It is off by default because each highlight runs a room-geometry
/// pass, which is far too expensive to do on every click made while simply modelling.
///
/// TransactionMode.Manual because turning it off deletes the overlay, and turning it on draws
/// for the current selection - both are writes.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class PaintHighlightCommand : CommandBase
{
    protected override string CommandName => "Paint Highlight";

    protected override Result Run(CommandContext ctx)
    {
        // Created here rather than at start-up because ExternalEvent.Create needs a valid API
        // context, and a command's Execute is one. Idempotent, so pressing the button twice
        // costs nothing.
        RevitTaskQueue.Initialise();

        var turningOn = !PaintHighlightService.Armed;

        var message = PaintHighlightService.SetArmed(ctx.UiDocument, turningOn);

        Log.Info($"Paint highlight {(turningOn ? "armed" : "disarmed")}.");

        TaskDialog.Show(CommandName, message);

        return Result.Succeeded;
    }
}
