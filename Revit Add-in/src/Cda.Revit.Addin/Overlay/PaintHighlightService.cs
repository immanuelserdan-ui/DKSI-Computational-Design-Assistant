using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Overlay;

/// <summary>
/// Turns "a row was selected in a Material Takeoff schedule" into "the painted area behind
/// that row is drawn in the model".
///
/// HOW A SCHEDULE ROW REACHES US
///   Selecting a schedule row sets the document selection to the row's element, so
///   UIControlledApplication.SelectionChanged fires with that element - the same event a click
///   in the model raises. Nothing in the API reports the ROW, only the element; see
///   <see cref="PaintHighlight"/> for what that costs and how it is handled.
///
/// WHY THE WORK IS NOT DONE IN THE EVENT
///   SelectionChanged is a notification. Revit forbids modifying the document from inside it,
///   and drawing the highlight means creating DirectShapes in a transaction. The work is
///   therefore posted to <see cref="RevitTaskQueue"/>, which runs it through an ExternalEvent
///   in a context where the API is legal.
///
/// WHY IT IS OFF UNTIL ARMED
///   Every highlight runs a SpatialElementGeometryCalculator over the rooms around the
///   selected element. That is far too expensive to do on every click someone makes while
///   simply modelling. The ribbon toggle is what says "I am reading a takeoff now".
/// </summary>
internal static class PaintHighlightService
{
    private static bool _armed;

    /// <summary>
    /// The element the current highlight was drawn for. Guards against redrawing the same
    /// geometry when Revit re-raises the event for a selection that did not actually change.
    /// </summary>
    private static long _drawnFor = -1;

    public static bool Armed => _armed;

    // ---------------------------------------------------------------- registration

    public static void Register(UIControlledApplication application)
    {
        try
        {
            application.SelectionChanged += OnSelectionChanged;
            application.ControlledApplication.DocumentClosing += OnDocumentClosing;
        }
        catch (Exception ex)
        {
            Log.Error("Paint highlight could not be registered.", ex);
        }
    }

    public static void Unregister(UIControlledApplication application)
    {
        try
        {
            application.SelectionChanged -= OnSelectionChanged;
            application.ControlledApplication.DocumentClosing -= OnDocumentClosing;
        }
        catch (Exception ex)
        {
            Log.Warn($"Paint highlight shutdown was untidy: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- arming

    /// <summary>
    /// Turns the highlight on or off. Must be called from a valid API context - the toggle
    /// command's Execute is one.
    /// </summary>
    /// <returns>A short sentence describing what happened, for the caller to report.</returns>
    public static string SetArmed(UIDocument uiDoc, bool armed)
    {
        _armed = armed;
        _drawnFor = -1;

        if (!armed)
        {
            var removed = PaintHighlight.Clear(uiDoc);

            return removed > 0
                ? $"Paint highlight off. {removed} overlay shape(s) removed."
                : "Paint highlight off.";
        }

        RevitTaskQueue.Initialise();

        // Draw immediately for whatever is already selected, rather than making the user
        // re-click the row they are looking at. Show() purges any existing overlay first, so
        // this also cleans up shapes left behind by a session that closed while armed.
        var selected = uiDoc.Selection.GetElementIds();

        if (selected.Count == 1)
        {
            var id = selected.First();
            var result = PaintHighlight.Show(uiDoc, id);
            _drawnFor = id.Value;

            return Describe(result);
        }

        // Nothing selected to draw for, so purge explicitly - otherwise stale geometry from an
        // earlier session would sit there until the first row is clicked.
        var stale = PaintHighlight.Clear(uiDoc);

        var purged = stale > 0
            ? $" {stale} leftover overlay shape(s) from an earlier session were removed."
            : string.Empty;

        return "Paint highlight on." + purged + " Select a row in a Material Takeoff schedule, " +
               "or an element in the model, and its net painted area is drawn on the surfaces it " +
               "was measured from.\n\nThe overlay is real Generic Model geometry, so turn this off " +
               "before saving if you do not want it in the file. Turning it off, or clearing the " +
               "selection, removes it.";
    }

    private static string Describe(PaintHighlight.Result result)
    {
        if (result.DrewSomething)
        {
            var notes = result.Notes.Count > 0 ? "\n\n" + string.Join(" ", result.Notes) : string.Empty;
            return $"Paint highlight on. {result.AreaSqm:0.00} m² drawn.{notes}";
        }

        return result.Notes.Count > 0
            ? "Paint highlight on.\n\n" + string.Join(" ", result.Notes)
            : "Paint highlight on.";
    }

    // ---------------------------------------------------------------- the event

    private static void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_armed) return;

        try
        {
            var doc = e.GetDocument();
            if (doc is null || doc.IsFamilyDocument || doc.IsLinked) return;

            var selected = e.GetSelectedElements();

            // NOTHING SELECTED - take the overlay down. Deselecting is how someone says they
            // are finished looking at that row.
            if (selected.Count == 0)
            {
                if (_drawnFor == -1) return;

                _drawnFor = -1;
                RevitTaskQueue.Post("Clear paint highlight", app => Clear(app));
                return;
            }

            // MORE THAN ONE - there is no single row to explain, and drawing all of them at
            // once would be a wall of hatch with no way to tell which figure is which.
            if (selected.Count > 1)
            {
                if (_drawnFor == -1) return;

                _drawnFor = -1;
                RevitTaskQueue.Post("Clear paint highlight", app => Clear(app));
                return;
            }

            var id = selected.First();

            // THE USER CLICKED THE OVERLAY ITSELF. Leave everything exactly as it is: clearing
            // here would delete the shape out from under the click, and redrawing would chase
            // its own tail. Selecting a highlight is how you read its name to find the room.
            if (PaintHighlight.IsOurs(doc, id)) return;

            // Revit re-raises this event for selections that did not change. Redrawing means a
            // transaction and a full room-geometry pass, so the same element twice is skipped.
            if (id.Value == _drawnFor) return;

            _drawnFor = id.Value;

            RevitTaskQueue.Post("Paint highlight", app =>
            {
                var uiDoc = app.ActiveUIDocument;
                if (uiDoc is null) return;

                // The selection may have moved on while this sat in the queue. Drawing for a
                // row the user has already left is worse than drawing nothing.
                if (_drawnFor != id.Value) return;

                PaintHighlight.Show(uiDoc, id);
            });
        }
        catch (Exception ex)
        {
            // This runs on Revit's own event path; an exception escaping here is reported to
            // the user as an add-in crash.
            Log.Warn($"Paint highlight: selection handling failed: {ex.Message}");
        }
    }

    private static void Clear(UIApplication app)
    {
        var uiDoc = app.ActiveUIDocument;
        if (uiDoc is not null) PaintHighlight.Clear(uiDoc);
    }

    // ---------------------------------------------------------------- closing

    /// <summary>
    /// Resets in-memory state when a document closes.
    ///
    /// DELIBERATELY DOES NOT CLEAR THE OVERLAY, because it cannot. DocumentClosing is a
    /// notification and Revit forbids modifying the document from inside it, so a Clear here
    /// would throw on every close and log noise while achieving nothing. The same is true of
    /// DocumentSaving, which is the other place it would be natural to try.
    ///
    /// What makes an orphaned overlay recoverable instead is that
    /// <see cref="PaintHighlight.Clear"/> finds shapes by their storage stamp rather than by
    /// remembering what this session drew. So it deletes overlay geometry left by ANY earlier
    /// session, and <see cref="SetArmed"/> runs it on the way in as well as on the way out -
    /// arming the toggle in a model that came back with stale shapes purges them.
    /// </summary>
    // FULLY QUALIFIED on purpose. Both Autodesk.Revit.UI.Events and Autodesk.Revit.DB.Events
    // declare a DocumentClosingEventArgs; the UI one is inaccessible, and `using
    // Autodesk.Revit.UI.Events` above (needed for SelectionChangedEventArgs) makes it the one
    // the compiler picks. DocumentClosing is a ControlledApplication event, so DB is correct.
    private static void OnDocumentClosing(object? sender, Autodesk.Revit.DB.Events.DocumentClosingEventArgs e)
    {
        _drawnFor = -1;

        if (_armed) Log.Info("Paint highlight: document closing while armed; overlay may persist in the file.");
    }
}
