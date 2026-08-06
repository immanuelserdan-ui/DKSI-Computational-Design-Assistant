using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Finishes;

/// <summary>
/// The callback Revit invokes when it is safe to modify the model.
///
/// This is the piece the first implementation was missing, and its absence is why
/// nothing appeared to happen.
///
/// The problem it solves: <c>DocumentChanged</c> tells you exactly what changed, but it
/// is READ-ONLY — writing there is not allowed. So the work has to be queued and picked
/// up somewhere writable. The first version polled <c>Idling</c> on a timer, which meant
/// guessing a debounce, doing nothing useful on the vast majority of ticks, and reacting
/// seconds late.
///
/// ExternalEvent is the mechanism Revit provides for precisely this: raise it from the
/// read-only context and Revit calls <see cref="Execute"/> back at the next safe moment —
/// immediately, not on a timer, and only when there is something to do.
/// </summary>
internal sealed class FinishSyncEventHandler : IExternalEventHandler
{
    public string GetName() => "Finish Automation sync";

    public void Execute(UIApplication app)
    {
        try
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc is null)
            {
                Log.Debug("Sync event: no active document; nothing to do.");
                return;
            }

            FinishAutomation.FlushPendingWork(doc);
        }
        catch (Exception ex)
        {
            // An exception escaping here reaches Revit's own handler and shows the user a
            // crash dialog for work they never asked for.
            Log.Error("Finish Automation sync event failed.", ex);
        }
    }
}
