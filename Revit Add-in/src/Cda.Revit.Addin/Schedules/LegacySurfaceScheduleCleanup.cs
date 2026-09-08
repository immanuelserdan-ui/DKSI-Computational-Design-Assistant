using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Schedules;

/// <summary>
/// Normalizes the surface schedules the moment the takeoff places a fresh batch of carriers:
/// builds/repairs the office's three "@V03" schedules via <see cref="SurfaceScheduleBuilder"/>,
/// then deletes the vendor's own "&lt;Surface&gt; Surface Area by Room and Face" schedules it
/// just recreated - so running "Painted Surface Area" is the ONLY step anyone has to take.
/// Running "Surface Schedules" by hand afterward is no longer required; it still works, it is
/// just redundant with what this does automatically now.
///
/// WHY THIS HOOKS DocumentChanged, NOT THE VENDOR'S SCHEDULE-BUILDING CODE
///   PaintScheduleBuilder.EnsureAll runs inside PaintedMaterialTakeoff.dll and finds-or-creates
///   its three legacy views on every "Painted Surface Area" run, same as
///   <see cref="Finishes.PaintRoomOverrideAutoReapply"/> already reacts to that product's
///   carrier-recreation without touching its source. Same detection, too - see
///   <see cref="PaintTakeoffTrigger"/> for why carrier placement is checked instead of a
///   transaction name.
///
/// WHY BUILD FIRST, THEN DELETE - ONE TRANSACTION
///   Both happen in the transaction that RemoveLegacySchedules opens. Building first means the
///   office schedules are already in their final shape before the legacy ones are removed, so
///   there is never a moment where neither set is fully correct; one transaction means one undo
///   entry for a click someone experienced as "ran the takeoff", not two.
///
/// WHY THE LEGACY SCHEDULES ARE DELETED RATHER THAN JUST LEFT ALONE
///   SurfaceScheduleVisibility.Apply already prefers the office names over the vendor's and
///   never opens the legacy ones - but "not opened" and "not there" are different promises. A
///   schedule that still exists is still one more thing in the Project Browser and one more
///   candidate for someone to place on a sheet by mistake. Deleting it is the only way to
///   actually deprecate it rather than merely hide it.
///
/// WHY A LEGACY SCHEDULE PLACED ON A SHEET IS NEVER DELETED
///   Removing a view still on a sheet leaves a hole in a real deliverable, and that is a far
///   worse failure than a legacy schedule surviving one more run. GetScheduleInstances() is
///   checked before every delete; a schedule that fails it is reported instead and left alone
///   for a person to deal with by hand.
///
/// SEE ALSO <see cref="Infrastructure.DocumentIdentity"/>. This class's own posted work used a
/// reference comparison at first, and it is the reason a run whose log showed the trigger
/// matching correctly still did nothing - the "same document" check failed every time.
/// </summary>
internal static class LegacySurfaceScheduleCleanup
{
    private static readonly SurfaceScheduleVisibility.Surfaces[] All =
    [
        SurfaceScheduleVisibility.Surfaces.Wall,
        SurfaceScheduleVisibility.Surfaces.Floor,
        SurfaceScheduleVisibility.Surfaces.Ceiling,
    ];

    public static void Register(UIControlledApplication application)
    {
        try
        {
            RevitTaskQueue.Initialise();
            application.ControlledApplication.DocumentChanged += OnDocumentChanged;
        }
        catch (Exception ex)
        {
            Log.Error("Surface schedule auto-sync could not be registered.", ex);
        }
    }

    public static void Unregister(UIControlledApplication application)
    {
        try
        {
            application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
        }
        catch (Exception ex)
        {
            Log.Warn($"Surface schedule auto-sync shutdown was untidy: {ex.Message}");
        }
    }

    private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        try
        {
            var doc = e.GetDocument();
            if (doc is null || doc.IsFamilyDocument || doc.IsLinked) return;

            if (!PaintTakeoffTrigger.JustPlacedCarriers(e, doc)) return;

            RevitTaskQueue.Post("Sync surface schedules", app =>
            {
                var target = app.ActiveUIDocument?.Document;

                // The document that changed may not be the active one by the time this reaches
                // Revit's thread - building or deleting views in the WRONG document would be a
                // real loss, not a cosmetic mismatch. See DocumentIdentity for why this is not a
                // reference comparison.
                if (!DocumentIdentity.IsSame(target, doc))
                {
                    Log.Debug("Surface schedule auto-sync: active document " +
                              $"('{target?.Title}') did not match the one that changed " +
                              $"('{doc.Title}'); skipped.");
                    return;
                }

                // swallowWarnings: nothing here was asked for by the user. It reacts to the
                // vendor takeoff finishing, so a modal warning dialog would appear on top of a
                // command belonging to a different product, with no explanation of where it
                // came from. Errors still surface and still roll this transaction back.
                Transactions.Run(target, "DKSI: sync surface schedules", () =>
                {
                    BuildOfficeSchedules(target);
                    RemoveLegacySchedules(target);
                }, swallowWarnings: true);
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Surface schedule auto-sync ignored a document change: {ex.Message}");
        }
    }

    private static void BuildOfficeSchedules(Document doc)
    {
        var problems = new List<string>();

        foreach (var surface in All)
        {
            try
            {
                SurfaceScheduleBuilder.Build(doc, surface, problems);
            }
            catch (Exception ex)
            {
                problems.Add($"Could not build/repair the {surface} schedule: {ex.Message}");
            }
        }

        foreach (var line in problems) Log.Warn($"Surface schedule auto-sync (build): {line}");

        Log.Info($"Surface schedule auto-sync: office schedules rebuilt, {problems.Count} problem(s).");
    }

    private static void RemoveLegacySchedules(Document doc)
    {
        foreach (var surface in All)
        {
            var name = SurfaceScheduleVisibility.LegacyScheduleNameOf(surface);

            ViewSchedule? schedule;
            try
            {
                schedule = SurfaceScheduleVisibility.Find(doc, name);
            }
            catch (Exception ex)
            {
                Log.Warn($"Surface schedule auto-sync could not search for '{name}': {ex.Message}");
                continue;
            }

            if (schedule is null)
            {
                Log.Debug($"Surface schedule auto-sync: '{name}' not found; nothing to remove.");
                continue;
            }

            ICollection<ElementId> placements;
            try
            {
                placements = schedule.GetScheduleInstances(-1);   // -1: the whole schedule, every segment
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not check whether '{name}' is placed on a sheet: {ex.Message}. " +
                         "Left in place rather than risk deleting a placed schedule.");
                continue;
            }

            if (placements.Count > 0)
            {
                Log.Warn($"'{name}' is placed on {placements.Count} sheet(s), so the legacy " +
                         "schedule was left in place rather than deleted. Remove it from the " +
                         "sheet(s) first if it should go.");
                continue;
            }

            try
            {
                doc.Delete(schedule.Id);
                Log.Info($"Removed legacy schedule '{name}' - the takeoff recreated it; " +
                         $"'{SurfaceScheduleVisibility.ScheduleNameOf(surface)}' is the one in use.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not remove legacy schedule '{name}': {ex.Message}");
            }
        }
    }
}
