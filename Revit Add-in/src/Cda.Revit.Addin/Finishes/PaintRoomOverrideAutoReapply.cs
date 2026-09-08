using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Finishes;

/// <summary>
/// Re-applies recorded paint room overrides automatically the moment the vendor takeoff
/// recreates its carriers, so a reassignment survives "Painted Surface Area" being run again
/// without anyone having to remember to click "Re-apply every override" afterwards.
///
/// WHY THIS WATCHES DocumentChanged RATHER THAN THE VENDOR'S COMMAND
///   PaintedMaterialTakeoff.dll is a separate product - see ../../installer/PaintTakeoff/README.md
///   on why the two are meant to stay independent installs, not a merged one. Reaching into its
///   Execute would mean this add-in has to load after it, know its class names, and break the
///   moment that build changes under it. DocumentChanged fires after ANY transaction commits, in
///   ANY add-in, which is what lets this react without either of those.
///
/// WHY <see cref="PaintTakeoffTrigger"/> AND NOT A TRANSACTION NAME
///   The first version matched GetTransactionNames() against "Painted Surface Area takeoff",
///   which is what the source in this repository names it. That matched nothing on this
///   machine: the DLL actually loaded is a rebuilt override, and a byte-level check of it found
///   that exact string absent entirely. See PaintTakeoffTrigger's own note for the full account
///   - the short version is that carrier placement is checked instead of a transaction's prose
///   name, because the vendor is free to change the latter on any rebuild.
///
/// WHY ONLY CARRIER PLACEMENT, NOT "Write Painted Area" TOO
///   "Painted Area (project wide)" writes a different, per-element rollup parameter and never
///   creates a carrier DirectShape at all, so it can never satisfy JustPlacedCarriers and never
///   resets an override in the first place - hooking it too would just be a wasted re-apply
///   pass on every run of a command this problem does not affect.
///
/// WHY THIS IS SAFE TO LEAVE RUNNING ALWAYS
///   The common case - no override ever recorded - returns after one Extensible Storage read
///   per commit that actually placed carriers, which is rare (a takeoff run, not every edit in
///   the model). And its own re-apply transaction creates no carriers itself, so it can never
///   re-trigger itself into a loop.
///
/// SEE ALSO <see cref="Infrastructure.DocumentIdentity"/> for why the posted work compares
/// documents the way it does - a reference comparison there was the reason this class matched
/// the trigger correctly and then silently did nothing, for every run, until it was replaced.
/// </summary>
internal static class PaintRoomOverrideAutoReapply
{
    public static void Register(UIControlledApplication application)
    {
        try
        {
            RevitTaskQueue.Initialise();
            application.ControlledApplication.DocumentChanged += OnDocumentChanged;
        }
        catch (Exception ex)
        {
            Log.Error("Paint room override auto-reapply could not be registered.", ex);
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
            Log.Warn($"Paint room override auto-reapply shutdown was untidy: {ex.Message}");
        }
    }

    private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        try
        {
            var doc = e.GetDocument();
            if (doc is null || doc.IsFamilyDocument || doc.IsLinked) return;

            if (!PaintTakeoffTrigger.JustPlacedCarriers(e, doc)) return;

            // NOTHING RECORDED, NOTHING TO DO. Reading Extensible Storage on every takeoff run
            // in every model would be a needless cost for the overwhelmingly common case where
            // no override has ever been written - most schedules never need one at all.
            if (PaintRoomOverrides.Read(doc).Count == 0) return;

            RevitTaskQueue.Post("Auto re-apply paint room overrides", app =>
            {
                var target = app.ActiveUIDocument?.Document;

                // The document that changed may not be the active one by the time this reaches
                // Revit's thread - acting on the WRONG document's overrides would silently
                // corrupt data that has nothing to do with what just ran. See DocumentIdentity
                // for why this is not a reference comparison.
                if (!DocumentIdentity.IsSame(target, doc))
                {
                    Log.Debug("Paint room override auto-reapply: active document " +
                              $"('{target?.Title}') did not match the one that changed " +
                              $"('{doc.Title}'); skipped.");
                    return;
                }

                var applied = 0;
                IReadOnlyList<string> report = [];

                Transactions.Run(target, "DKSI paint room overrides: auto re-apply",
                    () => applied = PaintRoomOverrides.Apply(target, out report), swallowWarnings: true);

                Log.Info($"Paint room overrides: {applied} row(s) auto-restored after the " +
                         "takeoff placed fresh carriers.");

                foreach (var line in report) Log.Info($"Paint room overrides (auto): {line}");
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Paint room override auto-reapply ignored a document change: {ex.Message}");
        }
    }
}
