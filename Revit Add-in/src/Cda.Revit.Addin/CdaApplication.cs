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
            var host = application.ControlledApplication.VersionNumber;

            Log.Info($"Startup: Revit {host} " +
                     $"build {application.ControlledApplication.VersionBuild} " +
                     $"(add-in built for Revit {BuildInfo.TargetRevitVersion})");

            // EXCLUSIVELY REVIT 2027 - CHECKED HERE, NOT ONLY AT BUILD TIME.
            //
            // The build already targets one release: the API references resolve out of the
            // Revit 2027 folder, the runtime is net10.0-windows to match that host, and both
            // installers write only to Addins\2027. None of that governs what happens if the
            // manifest and payload are copied into another version's folder by hand, by a
            // migration script, or by an IT deployment that "helpfully" fans out to every
            // installed release.
            //
            // What happens then is version-dependent and none of it is good. An older host
            // is on an older .NET and cannot load a net10.0 assembly at all, so Revit shows
            // its own unexplained "add-in failed to load" box. A FUTURE host on the same
            // runtime is the dangerous one: it loads cleanly and runs against an API that
            // has moved underneath it, and the failure surfaces later as wrong geometry
            // rather than as a refusal.
            //
            // So the add-in verifies the host itself and declines politely. Cancelled rather
            // than Failed: Failed makes Revit add its own dialog on top of this one, and two
            // boxes saying different things about the same event is worse than one saying
            // the right thing.
            if (BuildInfo.TargetRevitVersion.Length > 0 &&
                !string.Equals(host, BuildInfo.TargetRevitVersion, StringComparison.Ordinal))
            {
                var message =
                    $"DKSI Revit Tools is built exclusively for Revit {BuildInfo.TargetRevitVersion}, " +
                    $"and this is Revit {host}.\n\n" +
                    "The add-in has not been loaded. Nothing has been added to the ribbon and no " +
                    "model has been touched.\n\n" +
                    "This usually means the add-in files were copied into the wrong Addins folder. " +
                    $"They belong in Addins\\{BuildInfo.TargetRevitVersion}, and only there.\n\n" +
                    $"Loaded from: {BuildInfo.Location}";

                Log.Warn($"Refusing to load: built for Revit {BuildInfo.TargetRevitVersion}, host is {host}.");
                TaskDialog.Show($"DKSI Revit Tools - wrong Revit version", message);

                return Result.Cancelled;
            }

            // Before the ribbon, because the answer explains everything the user is about to
            // see. A duplicate install means Revit may be running a copy of this add-in that
            // is not the one anyone updated, and every symptom of that reads as "the fix did
            // not work" rather than as an install problem.
            var duplicate = DuplicateInstallCheck.Check(
                application.ControlledApplication.VersionNumber);

            RibbonBuilder.Build(application);

            // Registered after the ribbon: if automation fails to register, the tools are
            // already on screen and the user has lost a feature rather than the add-in.
            Finishes.FinishAutomation.Register(application);

            // Subscribes to SelectionChanged, but does nothing until the ribbon toggle arms
            // it - so an unarmed session pays one early-return per selection and no more.
            Overlay.PaintHighlightService.Register(application);

            // Also SelectionChanged, and deliberately NOT armed by anything: selecting a row in
            // a takeoff schedule selects a carrier the view's filter is painting out, so the
            // click appears to do nothing. This switches that one filter on, zooms to it, and
            // switches it back when the selection moves on. Cheap enough to leave running - a
            // selection that is not a Generic Model costs one category check.
            Overlay.CarrierRevealService.Register(application);

            // Same reasoning, and last of all: time tracking is the only feature here that
            // subscribes to Idling, so a fault in it would otherwise be felt on every tick.
            // It swallows its own failures for the same reason.
            TimeTracking.TimeTrackingService.Register(application);

            // LAST, and only if there is something to say. Shown after registration so a
            // duplicate install never costs the user the tools themselves - the ribbon is
            // already built and working by the time this dialog appears.
            if (duplicate is not null)
                TaskDialog.Show("DKSI Revit Tools - installed twice", duplicate);

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
        Overlay.CarrierRevealService.Unregister(application);
        Finishes.FinishAutomation.Unregister(application);
        Log.Info("Shutdown");
        return Result.Succeeded;
    }
}
