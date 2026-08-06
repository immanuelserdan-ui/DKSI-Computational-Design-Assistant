using System.Collections.Concurrent;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Qa;

/// <summary>
/// Runs work inside a valid Revit API context on behalf of a MODELESS window.
///
/// WHY THIS EXISTS AT ALL
///   Every other dialog in this add-in is modal: the command opens it, waits, and does its
///   work afterwards - still inside <c>Execute</c>, where the API is legal. The QA windows
///   cannot be modal. Their whole purpose is to sit open while you look at the model, and a
///   modal dialog blocks Revit entirely: click "zoom to this wall" and you see nothing,
///   because the view behind cannot repaint until the dialog closes.
///
///   The moment a window is modeless, its event handlers run OUTSIDE the API context, and
///   Revit rejects almost everything from there - "Attempting to modify the model outside of
///   a transaction", or an outright access violation on some calls. <see cref="ExternalEvent"/>
///   is Revit's own answer: the window asks for work, Revit runs it on its own thread when it
///   is safe, and the API is legal again inside <see cref="Execute"/>.
///
/// WHY A QUEUE RATHER THAN ONE HANDLER PER ACTION
///   <see cref="ExternalEvent.Raise"/> is a request, not a call - raising twice before Revit
///   services either one collapses them into a single Execute. A handler holding "the"
///   pending action would silently drop the first. A queue drains everything that was asked
///   for, in order, which is what someone clicking two rows in quick succession expects.
/// </summary>
internal sealed class RevitTaskQueue : IExternalEventHandler
{
    private static readonly RevitTaskQueue Handler = new();
    private static ExternalEvent? _event;

    private readonly ConcurrentQueue<(string Name, Action<UIApplication> Work)> _queue = new();

    /// <summary>
    /// Creates the ExternalEvent. MUST be called from a valid API context - an external
    /// command's Execute, or application start-up. Calling it from a window event handler
    /// throws, which is the whole problem this class exists to solve.
    ///
    /// Idempotent: the QA button can be pressed any number of times.
    /// </summary>
    public static void Initialise()
    {
        _event ??= ExternalEvent.Create(Handler);
    }

    /// <summary>True once <see cref="Initialise"/> has run.</summary>
    public static bool IsReady => _event is not null;

    /// <summary>
    /// Queues work to run on Revit's thread. Returns immediately - the caller is a UI event
    /// handler and must not block.
    /// </summary>
    /// <param name="name">Used only for the log, so a failure can be attributed.</param>
    public static void Post(string name, Action<UIApplication> work)
    {
        if (_event is null)
        {
            // Nothing can be done from here: creating the event needs the API context we do
            // not have. Say so in the log rather than throwing into a UI handler, where the
            // exception would surface as an unhandled WPF crash and take Revit with it.
            Log.Error($"QA: '{name}' was requested before the external event existed. Ignored.");
            return;
        }

        Handler._queue.Enqueue((name, work));
        _event.Raise();
    }

    public void Execute(UIApplication app)
    {
        while (_queue.TryDequeue(out var item))
        {
            try
            {
                item.Work(app);
            }
            catch (Exception ex)
            {
                // One bad row must not stop the rest of the queue, and must not escape into
                // Revit's own handler - an exception thrown out of Execute is reported as an
                // add-in crash.
                Log.Error($"QA: '{item.Name}' failed", ex);

                TaskDialog.Show("QA Tools",
                    $"{item.Name} could not complete.\n\n{ex.Message}\n\nSee {Log.CurrentFile}");
            }
        }
    }

    public string GetName() => "DKSI QA Tools";
}
