using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Finishes;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.UI;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Sits in front of the vendor's "Painted Surface Area" takeoff on the ribbon, in place of a
/// direct reference to PaintedMaterialTakeoff.Command.
///
/// WHY A WRAPPER RATHER THAN A CHANGE TO THAT COMMAND
///   There is no PaintedMaterialTakeoff SOURCE TREE this add-in can build against - see the
///   long comment on RibbonBuilder.PaintTakeoffPath and installer\PaintTakeoff\README.md. The
///   .source.cs beside it is a decompiled record of a rebuilt override DLL, not something this
///   project compiles; editing it does not change what Revit loads. So a pre-execution gate
///   cannot live inside that command - it has to run BEFORE it, on the same click, in code
///   this project actually builds.
///
/// HOW IT REACHES THE VENDOR COMMAND
///   By the same mechanism RibbonBuilder already uses to put that product's buttons on this
///   tab: assembly path + class name, resolved at runtime. Once loaded, the instance is cast
///   straight to Autodesk.Revit.UI.IExternalCommand - safe because Revit itself has already
///   loaded exactly one copy of RevitAPI/RevitAPIUI for the whole session, shared by every
///   add-in regardless of which AssemblyLoadContext its own assembly sits in, so that
///   interface identity holds between this assembly and the vendor's.
///
/// WHY THE RIBBON BUTTON HAD TO MOVE, NOT JUST GAIN A STEP
///   AddButton's own remarks are explicit that an AvailabilityClassName must live in the same
///   assembly as the button's command. This command lives here, so this button now carries
///   ProjectDocumentAvailability instead of the vendor's DocumentAvailability - see
///   RibbonBuilder.AddPaintTakeoffButtons.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class PaintedSurfaceAreaGateCommand : IExternalCommand
{
    private const string VendorAssemblyMissing =
        "Painted Material Takeoff is not installed, or was uninstalled since Revit started. " +
        "Restart Revit if it was just installed.";

    /// <summary>
    /// NOT a <see cref="CommandBase"/>, for the reason the class remarks give: the button it
    /// backs must carry an availability class from this assembly. That trade has a cost —
    /// CommandBase's catch-all is what turns a fault into a readable dialog and a log line, and
    /// nothing inherits it here — so this class has to provide the same guarantee itself, the
    /// same way <see cref="TimeTrackingCommand"/> does.
    ///
    /// THIS USED TO GUARD ONLY <see cref="RunVendorCommand"/>. The gate that runs BEFORE it —
    /// the dialog and the room sweep — was outside any handler, so a fault there escaped into
    /// Revit's own error dialog with nothing written to the log and <c>message</c> never set.
    /// That is the one path in the add-in where that could happen, and it was on the most-used
    /// button on the tab.
    /// </summary>
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            return RunGate(commandData, ref message, elements);
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            // Backing out of a Revit-side prompt is not a failure - same two catches, and the
            // same order, as CommandBase.
            Log.Info("Painted Surface Area: cancelled by user.");
            return Result.Cancelled;
        }
        catch (OperationCanceledException)
        {
            Log.Info("Painted Surface Area: cancelled by user.");
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            Log.Error("Painted Surface Area: the tile-material gate failed", ex);
            message = ex.Message;

            new TaskDialog("Painted Surface Area")
            {
                MainInstruction = "The tile-material check could not complete.",
                MainContent = ex.Message,
                ExpandedContent = ex.ToString(),
                FooterText = $"Details written to {Log.CurrentFile}",
                CommonButtons = TaskDialogCommonButtons.Close,
            }.Show();

            return Result.Failed;
        }
    }

    private static Result RunGate(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiApp = commandData.Application;
        var doc = uiApp.ActiveUIDocument?.Document;

        if (doc is null)
        {
            message = "No active document.";
            return Result.Failed;
        }

        var gate = new TileMaterialGateWindow();
        if (RevitWindow.ShowDialog(gate, uiApp) != true)
        {
            Log.Info("Painted Surface Area: cancelled at the tile-material prompt.");
            return Result.Cancelled;
        }

        var failures = new List<RoomMaterialCodeGate.FailedRoom>();

        if (gate.AlrumKokkenHasTile)
            failures.AddRange(RoomMaterialCodeGate.FindRoomsMissingCode(
                doc, RoomMaterialCodeGate.AlrumKokkenKeywords, "F"));

        if (gate.BadToiletHasTile)
            failures.AddRange(RoomMaterialCodeGate.FindRoomsMissingCode(
                doc, RoomMaterialCodeGate.BadToiletKeywords, "F"));

        if (failures.Count > 0)
        {
            var lines = string.Join(Environment.NewLine, failures.Select(f => $"  • {f.Label}"));

            Log.Warn($"Painted Surface Area: blocked - {failures.Count} room(s) missing a tile " +
                     $"material code ending in F: {string.Join(", ", failures.Select(f => f.Label))}");

            new TaskDialog("Painted Surface Area")
            {
                MainInstruction = "Tile material not detected.",
                MainContent = "You said the room type below has a tile material, but no material " +
                               "code ending in \"F\" (e.g. LIF, VBF, GBF) was found on it:" +
                               Environment.NewLine + Environment.NewLine + lines +
                               Environment.NewLine + Environment.NewLine +
                               "Name the tile material so it ends in \"F\" (the office convention - " +
                               "the template already carries VBF, GBF and VBJ), or assign its " +
                               "Code/Mark/Keynote parameter so it " +
                               "ends in \"F\", or answer No if this room genuinely has none, then run again.",
                CommonButtons = TaskDialogCommonButtons.Close,
            }.Show();

            return Result.Cancelled;
        }

        return RunVendorCommand(commandData, ref message, elements);
    }

    private static Result RunVendorCommand(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var path = RibbonBuilder.PaintTakeoffPath;
        if (path is null)
        {
            message = VendorAssemblyMissing;
            TaskDialog.Show("Painted Surface Area", VendorAssemblyMissing);
            return Result.Failed;
        }

        try
        {
            var assembly = Assembly.LoadFrom(path);
            var type = assembly.GetType("PaintedMaterialTakeoff.Command", throwOnError: true)!;
            var instance = (IExternalCommand)Activator.CreateInstance(type)!;

            Log.Info("Painted Surface Area: tile check passed - running the takeoff.");
            return instance.Execute(commandData, ref message, elements);
        }
        catch (Exception ex)
        {
            Log.Error("Painted Surface Area: could not load or run the vendor command", ex);
            message = ex.Message;

            new TaskDialog("Painted Surface Area")
            {
                MainInstruction = "Could not start the takeoff.",
                MainContent = $"{ex.GetType().Name}: {ex.Message}",
                ExpandedContent = ex.ToString(),
                FooterText = $"Details in {Log.CurrentFile}",
                CommonButtons = TaskDialogCommonButtons.Close,
            }.Show();

            return Result.Failed;
        }
    }
}
