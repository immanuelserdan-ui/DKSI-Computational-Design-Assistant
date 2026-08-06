using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin;

/// <summary>
/// The add-in entry point. This is the class named by &lt;FullClassName&gt; in the
/// .addin manifest, and it is the only type Revit loads by name - every command is
/// reached through the ribbon buttons this class creates.
///
/// OnStartup runs once, before any document exists. Do not touch documents here.
/// Keep it fast: everything in it is time the user spends staring at the splash screen.
/// </summary>
public sealed class CdaApplication : IExternalApplication
{
    internal const string TabName = "DKSI";

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            Log.Info($"Startup: Revit {application.ControlledApplication.VersionNumber} " +
                     $"build {application.ControlledApplication.VersionBuild}");

            RibbonBuilder.Build(application);

            // Registered after the ribbon: if automation fails to register, the tools are
            // already on screen and the user has lost a feature rather than the add-in.
            Finishes.FinishAutomation.Register(application);

            // Subscribes to SelectionChanged, but does nothing until the ribbon toggle arms
            // it - so an unarmed session pays one early-return per selection and no more.
            Overlay.PaintHighlightService.Register(application);

            // Same reasoning, and last of all: time tracking is the only feature here that
            // subscribes to Idling, so a fault in it would otherwise be felt on every tick.
            // It swallows its own failures for the same reason.
            TimeTracking.TimeTrackingService.Register(application);

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            // Returning Failed makes Revit show its own "add-in failed to load" dialog,
            // which tells the user nothing. Log first, then report something actionable.
            Log.Error("Startup failed", ex);
            TaskDialog.Show("DKSI Revit Tools",
                $"The add-in failed to load.\n\n{ex.Message}\n\nSee {Log.CurrentFile}");
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        // FIRST, before anything else can throw. This is the last moment the open time
        // segment can be written to disk; a failure further down this method would
        // otherwise cost the user the stretch of work they just finished.
        TimeTracking.TimeTrackingService.Unregister(application);

        Overlay.PaintHighlightService.Unregister(application);
        Finishes.FinishAutomation.Unregister(application);
        Log.Info("Shutdown");
        return Result.Succeeded;
    }
}
