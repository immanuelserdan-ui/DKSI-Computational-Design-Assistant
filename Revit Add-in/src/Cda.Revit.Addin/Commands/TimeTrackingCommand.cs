using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.TimeTracking;
using Cda.Revit.Addin.UI;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Opens the time log: current session, today's entries, manual entry and export.
///
/// NOT a <see cref="CommandBase"/>, and that is the only reason this class has its own
/// try/catch. CommandBase resolves <c>ActiveUIDocument.Document</c> up front, which is
/// exactly right for the nine tools that edit a model and exactly wrong here — logging an
/// hour of client meeting is something you do with no model open at all, which is also why
/// the ribbon button has no availability class.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class TimeTrackingCommand : IExternalCommand
{
    private const string CommandName = "Time Tracking";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var uiApp = commandData.Application;

            // Catches the case where the tracker never saw a ViewActivated for the view the
            // user is already in — a model restored from a previous session, or a startup
            // ordering surprise. Cheap, and it makes the status line honest.
            TimeTrackingService.SyncToActiveView(uiApp);

            RevitWindow.ShowDialog(new TimeLogWindow(TimeTrackingService.Settings), uiApp);

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            Log.Error($"{CommandName}: failed", ex);
            message = ex.Message;

            new TaskDialog(CommandName)
            {
                MainInstruction = "The time log could not be opened.",
                MainContent = ex.Message,
                ExpandedContent = ex.ToString(),
                FooterText = $"{BuildInfo.Describe()}  ·  Details written to {Log.CurrentFile}",
                CommonButtons = TaskDialogCommonButtons.Close,
            }.Show();

            return Result.Failed;
        }
    }
}
