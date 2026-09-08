using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Overlay;

/// <summary>
/// The reverse of <see cref="CarrierRevealService"/>: selecting a real model element (a wall,
/// not a takeoff row) selects every carrier measured on it, so the schedule scrolls to and
/// highlights the matching row(s) - the same native behaviour Revit already gives any selected
/// element that appears in an open schedule view.
///
/// ONLY WHILE A SCHEDULE IS THE ACTIVE VIEW. This used to fire whenever a schedule was open
/// ANYWHERE, including forgotten in a background tab nobody was reading - which meant every
/// wall click in a plan or 3D view silently replaced the wall selection with its carriers, and
/// Revit's own selection highlight on the wall never appeared, for the rest of the session,
/// the moment any schedule had ever been opened. Reported 2026-09-03: a wall selected in a
/// plan and a 3D view showed no highlight at all, only a stray carrier. Restricting this to the
/// schedule actually being active means it can only meaningfully act on a carrier ROW's own
/// selection - which <see cref="Apply"/> already skips, since a carrier syncing itself is
/// CarrierRevealService's job - so this class is now effectively a no-op. That is the intended
/// trade-off: ordinary model selection must never be hijacked by a schedule the user is not
/// looking at. A real replacement, if wanted later, is a ribbon toggle in the shape of
/// <c>PaintHighlightService</c>'s "Show / Hide Paint Areas" - off by default, on only when
/// someone is deliberately cross-referencing a schedule.
///
/// THERE IS NO "HIGHLIGHT THIS ROW" API, AND NONE IS NEEDED. Revit does not expose a public
/// method to select or scroll to a schedule ROW - <c>ViewSchedule</c> has no such member.
/// What it does do, natively, is scroll an open schedule to whatever the DOCUMENT SELECTION
/// currently contains, the same way it already does when a row is clicked in the other
/// direction. So the entire job here is: given the wall the user just clicked, find the
/// carrier element(s) measured on it, and call <c>UIDocument.Selection.SetElementIds</c> -
/// Revit's own UI does the rest.
///
/// A HOST CAN HAVE MANY CARRIERS. One wall can carry several rows - one per material region,
/// one per room it fronts (see [[paint-belongs-to-the-room-it-faces]]), sometimes one per
/// surface type across all three "@V03" schedules. All of them are selected together; Revit
/// highlights whichever ones are visible in whichever schedule happens to be open, and does
/// nothing for the rest. That is correct, not partial - there is no single "the" row for a
/// wall with painted faces in two rooms.
///
/// MATCHED BY THE 'Paint Host Id' PARAMETER, BY NAME - not by GUID, and not by product. Both
/// <c>PaintedMaterialTakeoff.dll</c>'s carriers and this add-in's own write a parameter of that
/// exact name at placement time, from the same measurement pass that produced the row (see
/// <see cref="PaintHighlight.ResolveTakeoffRow"/>'s doc comment for the forward direction this
/// mirrors). A name match is what lets one index cover both products, the same reasoning
/// <c>Finishes.PaintRoomOverrides.Carriers</c> already uses for the same reason.
///
/// WHY A CACHED INDEX, NOT A SCAN PER CLICK. The forward direction (CarrierRevealService) costs
/// one category check per selection change because it never needs to search for anything - the
/// clicked row IS the element. This direction needs to go looking, and SelectionChanged can
/// fire on every click in a session; scanning every Generic Model in the document each time
/// would make clicking around the model measurably slower on a large takeoff. The index is
/// built once, lazily, on the first selection that needs it, and invalidated only when it can
/// actually be wrong: <see cref="PaintTakeoffTrigger.JustPlacedCarriers"/> says the carriers
/// were just deleted and recreated, or the active document itself has changed.
///
/// SAFETY AGAINST THE INFINITE LOOP, THE SAME SHAPE AS CarrierRevealService's:
///   - <see cref="_selfSelecting"/> is set around the one call that changes the selection, so
///     the SelectionChanged it raises is recognised and ignored rather than re-entering this
///     class.
///   - An echo window ignores the same element id repeated within
///     <see cref="EchoWindow"/> - Revit re-raises SelectionChanged for a selection that did not
///     actually change.
///   - The work runs through <see cref="RevitTaskQueue"/>, not inline in the event handler -
///     the same deferral CarrierRevealService already relies on to stay inside a valid API
///     context and off whatever thread the event happened to arrive on.
/// </summary>
internal static class ModelToScheduleSync
{
    /// <summary>
    /// Host ElementId.Value -&gt; every carrier measured on it, across both products and all
    /// three surfaces. Null means "not built yet, or invalidated" - rebuilt lazily by
    /// <see cref="MatchingCarriers"/>, never eagerly, so a document with no takeoff carriers at
    /// all never pays for one.
    /// </summary>
    private static Dictionary<long, List<ElementId>>? _index;

    /// <summary>The document the current <see cref="_index"/> was built for. A different
    /// Document reference - a document switch, or a fresh open - invalidates it; ElementIds are
    /// only meaningful within the document that minted them.</summary>
    private static Document? _indexedDoc;

    private static bool _selfSelecting;

    private static long _lastHandledId = -1;

    private static DateTime _lastHandledAt = DateTime.MinValue;

    private static readonly TimeSpan EchoWindow = TimeSpan.FromMilliseconds(400);

    public static void Register(UIControlledApplication application)
    {
        try
        {
            // Same queue CarrierRevealService already builds; Initialise is idempotent.
            RevitTaskQueue.Initialise();

            application.SelectionChanged += OnSelectionChanged;
            application.ControlledApplication.DocumentChanged += OnDocumentChanged;
        }
        catch (Exception ex)
        {
            Log.Error("Model-to-schedule sync could not be registered.", ex);
        }
    }

    public static void Unregister(UIControlledApplication application)
    {
        try
        {
            application.SelectionChanged -= OnSelectionChanged;
            application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
        }
        catch (Exception ex)
        {
            Log.Warn($"Model-to-schedule sync shutdown was untidy: {ex.Message}");
        }
    }

    /// <summary>Drops the cached index the moment it can no longer be trusted. Rebuilding
    /// happens lazily on the next selection that actually needs it - not here.</summary>
    private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        try
        {
            var doc = e.GetDocument();
            if (!PaintTakeoffTrigger.JustPlacedCarriers(e, doc)) return;

            _index = null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Model-to-schedule sync could not react to a document change: {ex.Message}");
        }
    }

    private static void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            var ids = e.GetSelectedElements();

            // The multi-element selection chased by the 2026-08-29 diagnostic that used to sit
            // here is explained now: it was this class's OWN SetElementIds call in Apply,
            // raising the SelectionChanged this handler sees next. Not a mystery, just the
            // reverse-sync itself - which is also exactly why it was hijacking wall selection;
            // see the class doc for the active-view restriction that fixes that.
            Log.Debug($"MTS.OnSelectionChanged: {(ids is null ? "null" : ids.Count.ToString())} element(s)" +
                      (ids is { Count: 1 } ? $", id {ids.First().Value}" : string.Empty));

            // ONE element only - the same scope CarrierRevealService keeps to. A multi-select
            // has no single host to look up, and selecting every carrier for every selected
            // wall at once would be a surprising thing to happen from a rubber-band selection.
            if (ids is null || ids.Count != 1) return;

            var id = ids.First();

            if (_selfSelecting) return;

            if (id.Value == _lastHandledId && DateTime.UtcNow - _lastHandledAt < EchoWindow) return;

            _lastHandledId = id.Value;
            _lastHandledAt = DateTime.UtcNow;

            RevitTaskQueue.Post("Sync schedule selection", app => Apply(app, id));
        }
        catch (Exception ex)
        {
            Log.Warn($"Model-to-schedule sync ignored a selection change: {ex.Message}");
        }
    }

    private static void Apply(UIApplication app, ElementId id)
    {
        var uidoc = app.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (doc is null || uidoc is null) return;

        var element = doc.GetElement(id);
        if (element is null)
        {
            Log.Info($"MTS.Apply({id.Value}): element is null (deleted before this ran?)");
            return;
        }

        var isCarrier = element.Category?.Id.Value == (long)BuiltInCategory.OST_GenericModel;

        // A carrier selecting itself is not this class's job - CarrierRevealService already
        // owns that direction, and re-selecting the same element here would do nothing useful
        // while still costing an index lookup.
        if (isCarrier) return;

        // ACTIVE VIEW ONLY, NOT "OPEN ANYWHERE", AND CHECKED BEFORE THE INDEX LOOKUP. A
        // schedule sitting unseen in a background tab has nobody reading it, so there is
        // nothing for a sync to serve there - it would only cost the user their own selection.
        // Checked against the active view rather than "any open schedule", because the whole
        // point of this class is to help someone who is AT a schedule right now; someone
        // clicking around a plan or 3D view, with a schedule merely open behind them, wants
        // ordinary selection behaviour instead - including Revit's own highlight on the
        // element they just clicked, which this class stealing the selection would otherwise
        // erase every time. Checked first, not after MatchingCarriers, because this is now the
        // common case on every single non-schedule selection and the index lookup is the more
        // expensive of the two.
        if (doc.ActiveView is not ViewSchedule) return;

        var carriers = MatchingCarriers(doc, id);
        if (carriers.Count == 0) return;

        // _selfSelecting BEFORE the call, not after - the SelectionChanged this raises can
        // arrive before SetElementIds even returns, and OnSelectionChanged has to see the flag
        // already set when it does. Same ordering CarrierRevealService.Apply uses for its own
        // re-select, and for the same reason.
        try
        {
            _selfSelecting = true;
            uidoc.Selection.SetElementIds(carriers);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not select the takeoff row(s) for {id.Value}: {ex.Message}");
        }
        finally
        {
            _selfSelecting = false;
        }
    }

    private static List<ElementId> MatchingCarriers(Document doc, ElementId hostId)
    {
        if (_index is null || !ReferenceEquals(_indexedDoc, doc))
        {
            _index = BuildIndex(doc);
            _indexedDoc = doc;
        }

        return _index.TryGetValue(hostId.Value, out var carriers) ? carriers : [];
    }

    /// <summary>
    /// One pass over every Generic Model in the document, grouped by the host each one names in
    /// its 'Paint Host Id' parameter. Built once per document per carrier-regeneration, not per
    /// click - see the class doc for why that distinction matters here.
    /// </summary>
    private static Dictionary<long, List<ElementId>> BuildIndex(Document doc)
    {
        var index = new Dictionary<long, List<ElementId>>();

        foreach (var element in new FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_GenericModel)
                     .WhereElementIsNotElementType())
        {
            string? text;
            try { text = ParameterHelper.Find(element, "Paint Host Id")?.AsString(); }
            catch { continue; }

            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!long.TryParse(text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var hostId))
                continue;

            if (!index.TryGetValue(hostId, out var list))
                index[hostId] = list = [];

            list.Add(element.Id);
        }

        return index;
    }
}
