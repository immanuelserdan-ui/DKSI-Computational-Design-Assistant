using System.Collections.Concurrent;
using Autodesk.Revit.UI;

namespace Cda.Revit.Addin.Infrastructure;

/// <summary>
/// Runs work inside a valid Revit API context on behalf of a caller that has none.
///
/// WHY THIS EXISTS AT ALL
///   Commands are modal: the command opens a dialog, waits, and does its work afterwards -
///   still inside <c>Execute</c>, where the API is legal. Two things in this add-in are not
///   commands and cannot borrow that context:
///
///     * a modeless window, whose handlers run on the UI thread outside any API context;
///     * an EVENT handler such as <c>UIApplication.SelectionChanged</c>, which Revit raises
///       as a notification and which forbids modifying the document outright.
///
///   From either place Revit rejects almost everything - "Attempting to modify the model
///   outside of a transaction", or an outright access violation on some calls.
///   <see cref="ExternalEvent"/> is Revit's own answer: the caller asks for work, Revit runs
///   it on its own thread when it is safe, and the API is legal again inside
///   <see cref="Execute"/>.
///
/// WHY A QUEUE RATHER THAN ONE HANDLER PER ACTION
///   <see cref="ExternalEvent.Raise"/> is a request, not a call - raising twice before Revit
///   services either one collapses them into a single Execute. A handler holding "the"
///   pending action would silently drop the first. A queue drains everything that was asked
///   for, in order, which is what someone clicking two schedule rows in quick succession
///   expects.
///
/// WHY FAILURES ARE SILENT BY DEFAULT
///   This started life serving a modeless QA panel, where a failed action was a click the
///   user was waiting on and a TaskDialog was the right answer. It now also serves the paint
///   highlight, which runs on EVERY selection change - a dialog there would fire on a
///   mis-click and make the model unusable. Callers that are genuinely user-initiated opt
///   back in with <paramref name="announceFailure"/>.
/// </summary>
internal sealed class RevitTaskQueue : IExternalEventHandler
{
    private static readonly RevitTaskQueue Handler = new();
    private static ExternalEvent? _event;

    private readonly ConcurrentQueue<(string Name, Action<UIApplication> Work, bool Announce)> _queue = new();

    /// <summary>
    /// Creates the ExternalEvent. MUST be called from a valid API context - an external
    /// command's Execute, or application start-up. Calling it from a window or event handler
    /// throws, which is the whole problem this class exists to solve.
    ///
    /// Idempotent: safe to call from every entry point that might be the first one.
    /// </summary>
    public static void Initialise()
    {
        _event ??= ExternalEvent.Create(Handler);
    }

    /// <summary>True once <see cref="Initialise"/> has run.</summary>
    public static bool IsReady => _event is not null;

    /// <summary>
    /// Queues work to run on Revit's thread. Returns immediately - the caller is a UI or event
    /// handler and must not block.
    /// </summary>
    /// <param name="name">Used only for the log, so a failure can be attributed.</param>
    /// <param name="work">The work, which runs inside a valid API context.</param>
    /// <param name="announceFailure">
    /// Show a TaskDialog if the work throws. Correct for a button the user just pressed and
    /// is waiting on; wrong for anything that runs automatically.
    /// </param>
    public static void Post(string name, Action<UIApplication> work, bool announceFailure = false)
    {
        if (_event is null)
        {
            // Nothing can be done from here: creating the event needs the API context we do
            // not have. Say so in the log rather than throwing into a UI handler, where the
            // exception would surface as an unhandled WPF crash and take Revit with it.
            Log.Error($"'{name}' was requested before the external event existed. Ignored.");
            return;
        }

        Handler._queue.Enqueue((name, work, announceFailure));
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
                // One bad item must not stop the rest of the queue, and must not escape into
                // Revit's own handler - an exception thrown out of Execute is reported as an
                // add-in crash.
                Log.Error($"'{item.Name}' failed", ex);

                if (item.Announce)
                {
                    TaskDialog.Show("DKSI",
                        $"{item.Name} could not complete.\n\n{ex.Message}\n\nSee {Log.CurrentFile}");
                }
            }
        }
    }

    public string GetName() => "DKSI task queue";
}
