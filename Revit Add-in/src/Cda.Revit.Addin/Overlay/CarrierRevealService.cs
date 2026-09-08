using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Overlay;

/// <summary>
/// Makes a takeoff schedule row select something the user can actually SEE.
///
/// THE PROBLEM, EXACTLY
///   Clicking a row in "Wall Surface Area by Room and Face" already works: Revit sets the
///   document selection to that row's carrier element. Nothing appears to happen because the
///   carrier is a Generic Model that the takeoff's own "Paint Takeoff Carriers" VIEW FILTER
///   paints out of the view. The element is selected, on screen, and invisible.
///
///   That is a different condition from a hidden element and needs a different fix. Hidden
///   elements are unhidden with View.UnhideElements; an element filtered out is still fully
///   "in" the view, and only the filter's visibility flag decides whether it draws. Calling
///   UnhideElements on it does nothing at all, which is the trap this class exists to avoid.
///
/// WHY NOT JUST LEAVE "SHOW / HIDE PAINT AREAS" ON
///   Because then every carrier in the model draws at once, all in the same solid debug
///   colour and full opacity - a wall of slabs over the whole storey with no way to tell which
///   one the schedule row is about. What is wanted is one surface that reads as THE surface
///   being read, with the rest still present for context but visibly out of focus.
///
/// WHAT THIS DOES
///   On every selection change: whichever selected elements are Generic Models the active
///   view's filters are hiding have those filters switched on - one row selected or several,
///   a schedule's multi-select works exactly like its single-select. Every OTHER carrier the
///   same filters match is then pushed to halftone and high transparency - see
///   <see cref="DimSiblings"/> - and every selected one is emphasised, together. The camera is
///   left alone; this only changes what is visible, never where the view is looking. When the
///   selection moves to something with no carriers in it, the filters and every dimmed sibling
///   are put back exactly as found.
///
/// WHY IT IS SAFE TO RUN ALWAYS, UNLIKE <see cref="PaintHighlightService"/>
///   That one rebuilds room geometry per click and must be armed from the ribbon first. This
///   one reads a handful of filter flags and, in the common case - selection is not a Generic
///   Model - does nothing but one category check. There is no toggle because there is nothing
///   worth toggling off.
///
/// WORKS FOR BOTH TAKEOFFS. It matches on category and on the view filters actually hiding the
/// element, not on a product's stamp or parameters, so it covers PaintedMaterialTakeoff's
/// carriers and this add-in's alike. <see cref="PaintHighlight.ResolveTakeoffRow"/> cannot -
/// it reads DKSI's own "Paint Host Id" and returns the id unchanged for anything else.
/// </summary>
internal static class CarrierRevealService
{
    /// <summary>
    /// Filters this class switched on, and the view they belong to, so the change can be put
    /// back. Keyed by view because the same filter can be applied to several views with
    /// different visibility in each - restoring the wrong one would leave a view altered.
    /// </summary>
    private static readonly List<(ElementId View, ElementId Filter)> _revealed = [];

    /// <summary>
    /// The document every id in <see cref="_revealed"/>, <see cref="_dimmed"/> and
    /// <see cref="_highlighted"/> belongs to. Empty when nothing is revealed.
    ///
    /// WHY THIS HAD TO EXIST. All three of those are ElementIds, and an ElementId means nothing
    /// outside the document that minted it. <see cref="Apply"/> takes its document from
    /// UIApplication.ActiveUIDocument, and <see cref="OnViewActivated"/> fires a restore on ANY
    /// view activation - INCLUDING activating a view in another open project. So a reveal made
    /// in project A was restored against project B: an id that happened to resolve to a View in
    /// B passed the type test, and SetElementOverrides then reset the graphic overrides of
    /// whatever unrelated elements carried those ids, inside a committed transaction. The empty
    /// catch blocks around those calls are there for deleted elements and cannot tell "gone"
    /// from "belongs to another file", so it failed silently in both directions - B quietly
    /// altered, A left with its filter on and its carriers still painted orange.
    /// </summary>
    private static string _stateDocumentKey = string.Empty;

    /// <summary>
    /// True while a reveal is in effect for the current selection - one carrier or several.
    /// Distinct from <see cref="_revealed"/> being non-empty: a carrier that needed no filter
    /// switched on (already visible) is still "revealed" for <see cref="Restore"/>'s purposes.
    /// </summary>
    private static bool _hasActiveReveal;

    /// <summary>
    /// Carriers this class pushed to halftone and high transparency, and the view they belong
    /// to, so the dimming can be put back.
    ///
    /// Tracked as a list of elements rather than a view mode, unlike the temporary hide/isolate
    /// this replaced: SetElementOverrides is per-element, so there is no single flag to switch
    /// off and each dimmed sibling has to be cleared individually.
    /// </summary>
    private static (ElementId View, List<ElementId> Elements)? _dimmed;

    /// <summary>How transparent a dimmed sibling is, 0-100. High enough to read as "not the
    /// row being inspected" at a glance without erasing it entirely - halftone alone already
    /// desaturates it, this pushes it back as well.</summary>
    private const int DimTransparencyPercent = 75;

    /// <summary>The elements currently carrying this class's emphasis override, and their view.</summary>
    private static (ElementId View, List<ElementId> Elements)? _highlighted;

    /// <summary>
    /// True while this class is calling SetElementIds, so the SelectionChanged that raises can
    /// be told apart from a real click.
    ///
    /// LOAD-BEARING. Without it the re-select at the end of a reveal looks like a new selection,
    /// which reveals again, which re-selects... a loop that hangs Revit rather than failing.
    /// </summary>
    private static bool _selfSelecting;

    /// <summary>
    /// Last selection handled and when, sorted so two selections with the same elements in a
    /// different order still compare equal. Revit re-raises SelectionChanged for selections
    /// that did not change, and every one of those would otherwise cost a transaction.
    ///
    /// Time-based rather than "is it the same set", so clicking the SAME row (or rows) again
    /// after looking around still re-frames it - which is what someone who has rotated the
    /// view away and wants it back would expect.
    /// </summary>
    private static List<long> _lastHandledIds = [];

    private static DateTime _lastHandledAt = DateTime.MinValue;

    /// <summary>How long an identical selection is treated as an echo rather than a new click.</summary>
    private static readonly TimeSpan EchoWindow = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// True when the current reveal was made with a schedule open somewhere in the document -
    /// the only case <see cref="OnIdling"/> should ever act on. A carrier selected directly in
    /// the model (no schedule involved at all, e.g. after toggling "Show / Hide Paint Areas" on
    /// and clicking one by hand) must not have its highlight yanked away the instant Idling
    /// next fires just because no schedule happens to be open - there was never one to close.
    /// </summary>
    private static bool _revealedWithScheduleOpen;

    /// <summary>When the idle check for "is a schedule still open" last ran.</summary>
    private static DateTime _lastIdleCheck = DateTime.MinValue;

    /// <summary>
    /// How often the idle check runs. Idling can fire many times a second while the mouse
    /// moves, and the check - real as it is cheap - still has no reason to run on every tick.
    /// </summary>
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromMilliseconds(500);

    public static void Register(UIControlledApplication application)
    {
        try
        {
            // BUILD THE QUEUE HERE, or nothing this class posts will ever run.
            //
            // RevitTaskQueue.Initialise creates the ExternalEvent, and until it has run every
            // Post is dropped with "requested before the external event existed". The only
            // other callers are PaintHighlightCommand and PaintHighlightService.Arm - both
            // reached exclusively through a ribbon button that is not on the ribbon, so in a
            // normal session the queue was never built at all. This class is the first thing
            // that needs it without a command having run first.
            //
            // Start-up is one of the two contexts Initialise documents as valid, and it is
            // idempotent, so arming the highlight later still works.
            RevitTaskQueue.Initialise();

            application.SelectionChanged += OnSelectionChanged;

            // LEAVING THE VIEW RESTORES IT, and this is what stops a reveal outliving the
            // session that made it.
            //
            // DocumentClosing cannot restore anything - a closing document may not be modified
            // - so a reveal still active when Revit closes leaves its filter switched ON, and
            // filter visibility is SAVED WITH THE VIEW. Measured: after one such close the
            // "Paint Takeoff Carriers" filter stayed on for an entire working day, and 2.721
            // consecutive clicks reported revealed=0 because there was never anything left to
            // reveal. The feature looked broken when it was simply already switched on.
            //
            // Switching views is the last moment the document is still modifiable, so the
            // restore happens there instead.
            application.ViewActivated += OnViewActivated;

            // CLOSING THE SCHEDULE TAB DOES NOT ALWAYS RAISE ViewActivated. It only does when
            // the closed tab was the active one and Revit falls back to another open view - if
            // the graphical view (the 3D view carrying the reveal) was already the active tab
            // and the schedule sat in the background, closing it changes nothing about which
            // view is active, so no event fires at all and the reveal would otherwise sit
            // there until something else clears it.
            //
            // Revit's API has no view-closed event to catch that directly, so Idling is what is
            // left: it runs in a valid API context on every idle tick, and OnIdling below asks
            // the one question that matters - is any ViewSchedule still open - cheaply enough
            // to afford asking it repeatedly.
            application.Idling += OnIdling;

            // State is per-document: a filter id, a view id and a camera from the model being
            // closed mean nothing in the next one, and acting on them would either fail or -
            // worse - match something unrelated by id. Cleared rather than restored, because
            // a closing document cannot be modified.
            application.ControlledApplication.DocumentClosing += OnDocumentClosing;
        }
        catch (Exception ex)
        {
            Log.Error("Carrier reveal could not be registered.", ex);
        }
    }

    public static void Unregister(UIControlledApplication application)
    {
        try
        {
            application.SelectionChanged -= OnSelectionChanged;
            application.ViewActivated -= OnViewActivated;
            application.Idling -= OnIdling;
            application.ControlledApplication.DocumentClosing -= OnDocumentClosing;
        }
        catch (Exception ex)
        {
            Log.Warn($"Carrier reveal shutdown was untidy: {ex.Message}");
        }
    }

    /// <summary>
    /// Puts a reveal back when the user navigates to another view.
    ///
    /// The reveal only makes sense in the view it was made in, and this is the last point at
    /// which the document can still be modified before a close. Without it a filter switched
    /// on here is saved into the view and stays on indefinitely.
    /// </summary>
    private static void OnViewActivated(object? sender, ViewActivatedEventArgs e)
    {
        try
        {
            if (_revealed.Count == 0 && _dimmed is null && _highlighted is null) return;

            // The reveal belongs to the view being LEFT. Restoring while arriving somewhere
            // else is exactly right - Apply targets the remembered view ids, not the new one.
            Restore();
        }
        catch (Exception ex)
        {
            Log.Warn($"Carrier reveal could not be restored on view change: {ex.Message}");
        }
    }

    /// <summary>
    /// Catches the case <see cref="OnViewActivated"/> cannot: the schedule tab closing while
    /// the graphical view holding the reveal was already the active one, so no view-activation
    /// event fires at all.
    ///
    /// Throttled to <see cref="IdleCheckInterval"/> and short-circuited immediately when there
    /// is nothing to restore, which is the overwhelming majority of idle ticks in a normal
    /// session - most of the cost of this handler is the two field reads that let it return.
    /// </summary>
    private static void OnIdling(object? sender, IdlingEventArgs e)
    {
        try
        {
            if (!_revealedWithScheduleOpen) return;

            if (_revealed.Count == 0 && _dimmed is null && _highlighted is null) return;

            if (DateTime.UtcNow - _lastIdleCheck < IdleCheckInterval) return;
            _lastIdleCheck = DateTime.UtcNow;

            var uiDoc = (sender as UIApplication)?.ActiveUIDocument;
            if (uiDoc is null) return;

            var doc = uiDoc.Document;

            var scheduleStillOpen = uiDoc.GetOpenUIViews()
                .Any(v => doc.GetElement(v.ViewId) is ViewSchedule);

            if (scheduleStillOpen) return;

            // No schedule is open any more, so whatever is revealed, dimmed or highlighted was
            // being read for one and nothing is reading it now.
            Restore();
        }
        catch (Exception ex)
        {
            Log.Warn($"Carrier reveal idle check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Drops the banked state when the document it belongs to is the one closing. NOT a
    /// restore: the document is closing and cannot be modified, and anything left switched on
    /// goes with it.
    ///
    /// ONLY WHEN IT IS THAT DOCUMENT. Clearing unconditionally meant closing a second, unrelated
    /// project threw away the record of a reveal still standing in the first - leaving its
    /// filter switched on and its siblings dimmed with nothing left that knew how to put them
    /// back. The echo-window fields are session-wide rather than document-scoped and are reset
    /// either way, since a stale "already handled this selection" entry is only ever a missed
    /// redraw.
    /// </summary>
    private static void OnDocumentClosing(object? sender, Autodesk.Revit.DB.Events.DocumentClosingEventArgs e)
    {
        _lastHandledIds = [];
        _lastHandledAt = DateTime.MinValue;

        var closingKey = DocumentIdentity.KeyOf(e.Document);

        if (_stateDocumentKey.Length > 0 &&
            !string.Equals(_stateDocumentKey, closingKey, StringComparison.OrdinalIgnoreCase))
            return;

        _revealed.Clear();
        _dimmed = null;
        _highlighted = null;
        _hasActiveReveal = false;
        _revealedWithScheduleOpen = false;
        _stateDocumentKey = string.Empty;
    }

    // ------------------------------------------------------------------ the event

    private static void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            var ids = e.GetSelectedElements();

            // EVERY event is recorded, because "some rows do not highlight" cannot be told
            // apart from "some rows raise no event" without it. A row that logs nothing here
            // never reached this add-in at all - which points at Revit and the schedule cell,
            // not at anything below.
            Log.Debug($"Selection changed: {(ids is null ? "null" : ids.Count.ToString())} element(s)" +
                      (ids is { Count: 1 } ? $", id {ids.First().Value}" : string.Empty));

            // EMPTY ONLY bails out early. A multi-select used to be treated the same as
            // "clicked away" - revealing a filter because a rubber-band happened to catch a
            // carrier would be a surprise - but selecting several schedule rows at once is a
            // deliberate multi-select, not a rubber-band, and Apply already only acts on
            // whichever of the selected elements are actually carriers. A selection with none
            // degrades to exactly the old single-element behaviour: nothing to reveal, restore
            // the last one.
            if (ids is null || ids.Count == 0)
            {
                Restore();
                return;
            }

            var idList = ids.ToList();

            if (_selfSelecting)
            {
                Log.Debug($"Selection ({idList.Count}) is our own re-select; ignored.");
                return;
            }

            var sorted = idList.Select(i => i.Value).OrderBy(v => v).ToList();

            if (sorted.SequenceEqual(_lastHandledIds) && DateTime.UtcNow - _lastHandledAt < EchoWindow)
            {
                Log.Debug($"Selection ({idList.Count}) repeated within the echo window; ignored.");
                return;
            }

            _lastHandledIds = sorted;
            _lastHandledAt = DateTime.UtcNow;

            RevitTaskQueue.Post("Reveal takeoff carrier", app => Apply(app, idList));
        }
        catch (Exception ex)
        {
            Log.Warn($"Carrier reveal ignored a selection change: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ the work

    /// <summary>
    /// Puts back the last reveal and performs this one, in ONE transaction.
    ///
    /// WHY THEY MUST SHARE A TRANSACTION
    ///   Every view-graphics change in Revit is a document change, so each one is an undo
    ///   entry and each one marks the model dirty. Restoring and revealing separately means
    ///   TWO of those per click - twenty rows read while checking a takeoff would leave forty
    ///   entries in the undo stack and a file that claims unsaved changes, for what the user
    ///   experienced as reading a schedule. One entry per click is the least this can cost
    ///   while still using the filter mechanism at all.
    ///
    /// <paramref name="ids"/> may be empty, or contain anything including a wall - a
    /// multi-select is not required to be all carriers. Whichever ones are filtered-out
    /// Generic Models are revealed together; the rest just ride along in the reselect at the
    /// end. A selection with no carriers at all simply results in the previous reveal being
    /// put back and nothing new shown, which is what "the user clicked away" should do.
    /// </summary>
    private static void Apply(UIApplication app, List<ElementId> ids)
    {
        var uidoc = app.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (doc is null || uidoc is null) return;

        // THE STATE BELONGS TO A DOCUMENT, AND THIS IS WHERE THAT IS ENFORCED.
        //
        // Everything banked in _revealed / _dimmed / _highlighted is an ElementId from the
        // document that was active when the reveal was made. If the active document has since
        // changed - which OnViewActivated reacts to by queueing a restore - those ids address
        // nothing meaningful here, and acting on them silently rewrites view overrides on
        // unrelated elements in the file the user has just switched to.
        //
        // The banked state is DROPPED rather than restored, because it cannot be restored: the
        // document that owns it is no longer the one this callback was handed, and Revit offers
        // no way to open a transaction on a document that is not active. What is left behind in
        // that file is a switched-on filter and some emphasis overrides - visible, undoable, and
        // cleared the next time a carrier is clicked there, since every Apply begins by putting
        // the previous reveal back. That is a cosmetic remnant in the right file, against
        // silently corrupting graphics in the wrong one.
        var documentKey = DocumentIdentity.KeyOf(doc);

        if (_stateDocumentKey.Length > 0 &&
            !string.Equals(_stateDocumentKey, documentKey, StringComparison.OrdinalIgnoreCase))
        {
            Log.Debug($"Carrier reveal: state belongs to another document ('{_stateDocumentKey}'), " +
                      $"the active one is '{documentKey}'. Dropped rather than restored against " +
                      "the wrong file; the overlay there is cleared by the next reveal in it.");

            _revealed.Clear();
            _dimmed = null;
            _highlighted = null;
            _hasActiveReveal = false;
            _revealedWithScheduleOpen = false;
            _stateDocumentKey = string.Empty;
        }

        var view = TargetView(uidoc, doc);

        // Carriers are Generic Models in both products. Anything else is a normal selection
        // and none of this class's business - but the restore below still has to happen.
        // A multi-select is filtered down to just its carriers: a rubber-band that happens to
        // catch one carrier among a hundred walls still means "spotlight that one carrier",
        // the same as it always has for a single click.
        var carrierIds = ids
            .Where(candidate => doc.GetElement(candidate)?.Category?.Id.Value
                                 == (long)BuiltInCategory.OST_GenericModel)
            .ToList();

        var revealable = carrierIds.Count > 0 && view is not null && !view.IsTemplate;

        // Filled inside the transaction, not before it - see the note at the computation.
        var toReveal = new List<ElementId>();

        // _highlighted BELONGS IN THIS CONDITION, and leaving it out was a bug.
        //
        // ClearHighlight only runs inside the transaction below. Clicking a carrier and then
        // a wall left revealable false, _revealed empty and _dimmed null - so no
        // transaction ran, the orange override was never removed, and it stayed on the element
        // indefinitely. A stuck override with solid fill and zero transparency makes Revit's
        // own selection highlight look broken on that element, which is exactly how it was
        // reported: highlights that "stop working after a few instances".
        if (_revealed.Count > 0 || _dimmed is not null || _highlighted is not null || revealable)
        {
            try
            {
                Transactions.Run(doc, "Takeoff carrier visibility", () =>
                {
                    foreach (var (viewId, filterId) in _revealed)
                    {
                        try
                        {
                            if (doc.GetElement(viewId) is View previous)
                                previous.SetFilterVisibility(filterId, false);
                        }
                        catch
                        {
                            // View or filter deleted since; nothing to put back.
                        }
                    }

                    _revealed.Clear();

                    // Put back any dimming THIS class applied, before deciding about a new
                    // one. Each sibling is cleared individually - there is no single view flag
                    // to switch off the way temporary hide/isolate had.
                    if (_dimmed is { } previouslyDimmed
                        && doc.GetElement(previouslyDimmed.View) is View dimmedView)
                    {
                        foreach (var dimmedId in previouslyDimmed.Elements)
                        {
                            try { dimmedView.SetElementOverrides(dimmedId, new OverrideGraphicSettings()); }
                            catch { /* element or view gone */ }
                        }
                    }

                    _dimmed = null;

                    // Always drop the previous emphasis - a row that is no longer the one being
                    // read must not stay painted orange.
                    ClearHighlight(doc);

                    // WORK OUT WHAT HIDES THIS ELEMENT ONLY NOW, AFTER THE RESTORE ABOVE.
                    //
                    // Computing it earlier is a bug that hides every OTHER row. HidingFilters
                    // only reports filters whose visibility is OFF, and at that point the
                    // filter is still switched ON from the previous row - so it reports
                    // nothing, the restore then switches it off, and this carrier is left
                    // invisible. The next click sees it off again and works. The symptom is
                    // exactly alternating rows, which is what the log showed:
                    //
                    //   29314959 hidingFilters=1   29314960 hidingFilters=0
                    //   29314961 hidingFilters=1   29314963 hidingFilters=0
                    //
                    // Reading the view AFTER it has been put back means the question asked is
                    // "what hides this element in a clean view", which is the only version of
                    // the question with a stable answer.
                    //
                    // UNIONED ACROSS EVERY SELECTED CARRIER, then de-duplicated - two rows
                    // hidden by the same filter must only switch it on and record it once, or
                    // the second SetFilterVisibility(true) is a harmless no-op but the second
                    // _revealed entry would try to switch the same filter off twice on restore.
                    if (revealable)
                    {
                        foreach (var carrierId in carrierIds)
                            toReveal.AddRange(HidingFilters(view!, doc.GetElement(carrierId)!));
                    }

                    foreach (var filterId in toReveal.Distinct())
                    {
                        view!.SetFilterVisibility(filterId, true);
                        _revealed.Add((view.Id, filterId));
                    }

                    // THE FILTER IS ALL-OR-NOTHING, WHICH IS WHY THIS IS HERE.
                    //
                    // SetFilterVisibility switches on EVERY element the filter covers, so
                    // revealing one carrier reveals all of them - the whole storey's worth of
                    // painted regions at once, which is not what "highlight this row" means.
                    // Revit offers no way to except a single element from a filter, so every
                    // sibling is dimmed instead and only the selected rows' own regions are
                    // left at full strength.
                    //
                    // MATCHED REGARDLESS OF WHETHER A FILTER NEEDED SWITCHING ON. A carrier
                    // that was already visible before this click - "Show / Hide Paint Areas"
                    // left on, say - still has to yield the spotlight to whichever row was just
                    // selected, so this is not restricted to the filters found above. Also
                    // unioned across every selected carrier, same reason as toReveal.
                    if (revealable)
                    {
                        var carrierFilters = carrierIds
                            .SelectMany(carrierId => AllMatchingFilters(view!, doc.GetElement(carrierId)!))
                            .Distinct()
                            .ToList();

                        if (carrierFilters.Count > 0) DimSiblings(doc, view!, carrierIds, carrierFilters);
                    }

                    // Emphasise whenever this is a carrier - NOT only when a filter had to be
                    // switched on. A region that was already visible still needs to stand out
                    // from the wall it lies on, and that is the case a "reveal only" rule
                    // silently skips. Every selected carrier is emphasised, not just one - that
                    // is the whole point of allowing a multi-select through at all.
                    if (revealable) Highlight(doc, view!, carrierIds);
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"Carrier visibility could not be changed: {ex.Message}");
                _revealed.Clear();
                _hasActiveReveal = false;
                _stateDocumentKey = string.Empty;
                return;
            }
        }

        // Logged AFTER the transaction, because before it the filter counts describe the
        // previous row's leftovers rather than this one.
        Log.Debug($"Reveal {(carrierIds.Count == 1 ? carrierIds[0].Value.ToString() : $"{carrierIds.Count} carrier(s)")}: " +
                  $"selected={ids.Count} view={view?.Name ?? "none"} revealed={toReveal.Distinct().Count()}");

        _hasActiveReveal = carrierIds.Count > 0;

        // Stamped alongside _hasActiveReveal, from the document this pass actually wrote to.
        // Cleared when nothing is left banked, so the guard at the top of Apply only fires
        // while there is genuinely state belonging to some other file.
        _stateDocumentKey =
            _revealed.Count > 0 || _dimmed is not null || _highlighted is not null || _hasActiveReveal
                ? documentKey
                : string.Empty;

        // Recorded here, not guessed at in OnIdling: a schedule open NOW, at the moment this
        // reveal was made, is the only thing that justifies auto-restoring later when one
        // closes. Checked regardless of whether toReveal/DimSiblings actually ran, because a
        // carrier that needed no filter change can still have been selected from a schedule row.
        _revealedWithScheduleOpen = revealable
            && uidoc.GetOpenUIViews().Any(v => doc.GetElement(v.ViewId) is ViewSchedule);

        if (carrierIds.Count == 0 || view is null) return;

        if (view is View3D sectioned)
        {
            foreach (var carrierId in carrierIds)
            {
                if (doc.GetElement(carrierId) is { } carrierElement)
                    WarnIfOutsideSectionBox(sectioned, carrierElement);
            }
        }

        // RE-ASSERT THE SELECTION, because the transaction above almost certainly cleared it.
        //
        // Revit selects the row's element(s) when a schedule row is clicked, and then a
        // transaction that modifies the VIEW - which switching a filter on and hiding siblings
        // both are - drops that selection. The element is revealed and the camera moves to it,
        // and it is not highlighted, because nothing is selected any more.
        //
        // That is exactly the reported symptom, including its inconsistency: a row whose
        // carrier was already visible needs no transaction, keeps its selection and highlights
        // normally, while a row that needed revealing loses it. Which rows misbehave therefore
        // depends on what the view happened to be showing, not on the row.
        //
        // THE FULL ORIGINAL SELECTION, not just the carriers - any wall or other element that
        // rode along in a multi-select is put back too, so this never narrows what the user
        // had selected.
        //
        // Set AFTER _hasActiveReveal so the SelectionChanged this raises returns immediately
        // instead of re-entering the whole reveal.
        try
        {
            _selfSelecting = true;
            uidoc.Selection.SetElementIds(ids);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not re-select after revealing: {ex.Message}");
        }
        finally
        {
            _selfSelecting = false;
        }
    }

    /// <summary>
    /// Paints the revealed region solid orange with a heavy outline, so it reads as the thing
    /// being inspected rather than as one more surface in a grey model.
    ///
    /// WHY REVIT'S OWN SELECTION IS NOT ENOUGH
    ///   Selection draws a thin blue edge. On a carrier that is a flat plate lying against a
    ///   wall, seen at an angle, that edge is a few pixels of blue against whatever the wall
    ///   already is - and the takeoff's own carriers are semi-transparent debug shading, so the
    ///   region reads as a faint tint. The point of clicking the row is to SEE the surface.
    ///
    /// ORANGE, NOT BLUE. Revit's selection is blue and the model's own paint materials here run
    /// to reds, purples and greens. Orange collides with none of them, and keeping it distinct
    /// from selection blue means "this is the row's region" and "this is selected" stay
    /// legible as two separate facts.
    ///
    /// THE SOLID PATTERN IS FOUND BY PROPERTY, NOT BY NAME. "&lt;Solid fill&gt;" is localised -
    /// this template is Danish - so it is located by IsSolidFill instead, which is the same in
    /// every language.
    /// </summary>
    /// <summary>
    /// Emphasises every carrier in <paramref name="ids"/> - one for a single click, several
    /// for a multi-select. Each is overridden individually because SetElementOverrides is
    /// per-element; there is no "highlight this set" call.
    /// </summary>
    private static void Highlight(Document doc, View view, IReadOnlyList<ElementId> ids)
    {
        try
        {
            var colour = new Color(255, 120, 0);

            var settings = new OverrideGraphicSettings()
                .SetProjectionLineColor(colour)
                .SetProjectionLineWeight(6)
                .SetSurfaceTransparency(0)
                .SetHalftone(false);

            var solid = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);

            if (solid is not null)
            {
                settings.SetSurfaceForegroundPatternVisible(true);
                settings.SetSurfaceForegroundPatternId(solid.Id);
                settings.SetSurfaceForegroundPatternColor(colour);
            }

            var applied = new List<ElementId>();

            foreach (var id in ids)
            {
                try
                {
                    view.SetElementOverrides(id, settings);
                    applied.Add(id);
                }
                catch (Exception ex)
                {
                    // The reveal and the camera still worked for this one; only its own
                    // emphasis is missing. The rest of the selection still gets its highlight.
                    Log.Warn($"Could not emphasise carrier {id.Value}: {ex.Message}");
                }
            }

            if (applied.Count > 0) _highlighted = (view.Id, applied);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not emphasise carriers: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the emphasis from every element it was applied to. An empty
    /// OverrideGraphicSettings is Revit's own "no overrides", so this restores each element to
    /// whatever the view would draw anyway - it does not assume the element had no overrides
    /// of its own before.
    /// </summary>
    private static void ClearHighlight(Document doc)
    {
        if (_highlighted is not { } previous) return;

        if (doc.GetElement(previous.View) is View view)
        {
            foreach (var id in previous.Elements)
            {
                try { view.SetElementOverrides(id, new OverrideGraphicSettings()); }
                catch
                {
                    // View or element gone; nothing to clear.
                }
            }
        }

        _highlighted = null;
    }

    /// <summary>
    /// Warns when the carrier sits outside the view's section box.
    ///
    /// WHY THIS DESERVES ITS OWN CHECK
    ///   A section box clips by volume and nothing else reports it. Every step of the reveal
    ///   succeeds - the filter goes on, the siblings hide, the camera turns - and the region
    ///   still does not draw, which is indistinguishable from the feature being broken. This
    ///   is the one failure that looks exactly like a bug in everything above it.
    ///
    /// Approximate on purpose: the carrier's centre is tested against the box rather than its
    /// full extent. A region half in and half out still draws, and warning about it would be
    /// noise.
    /// </summary>
    private static void WarnIfOutsideSectionBox(View3D view, Element carrier)
    {
        try
        {
            if (!view.IsSectionBoxActive) return;

            var box = view.GetSectionBox();
            var carrierBox = carrier.get_BoundingBox(null);
            if (box is null || carrierBox is null) return;

            // The section box is stored in its own coordinates, so the point has to come back
            // through its transform before Min/Max mean anything.
            var centre = (carrierBox.Min + carrierBox.Max) / 2.0;
            var local = box.Transform.Inverse.OfPoint(centre);

            var inside = local.X >= box.Min.X && local.X <= box.Max.X
                      && local.Y >= box.Min.Y && local.Y <= box.Max.Y
                      && local.Z >= box.Min.Z && local.Z <= box.Max.Z;

            if (!inside)
            {
                Log.Info($"Carrier {carrier.Id.Value} lies OUTSIDE the section box of " +
                         $"'{view.Name}', so it cannot draw however it is revealed. Widen the " +
                         "section box or use a view without one.");
            }
        }
        catch
        {
            // A section box that cannot be read is not worth reporting on.
        }
    }

    /// <summary>
    /// Does this element match the filter? A filter that cannot evaluate a given element
    /// throws rather than returning false, and one unevaluable element must not abandon the
    /// whole sweep.
    /// </summary>
    private static bool Passes(ElementFilter rule, Element element)
    {
        try { return rule.PassesFilter(element); }
        catch { return false; }
    }

    /// <summary>
    /// Pushes every OTHER carrier in the view to halftone and high transparency, so the
    /// selected row's region reads as the one being inspected against a dimmed but still
    /// visible model, rather than the only thing left on screen.
    ///
    /// GRAPHIC OVERRIDE, NOT TEMPORARY HIDE. This used to call View.HideElementsTemporary,
    /// which removed every sibling from the view entirely - useful for isolating one shape,
    /// useless for a QA pass that needs to see where it sits among the room's other painted
    /// faces. SetElementOverrides keeps siblings drawn, just visually pushed back, which is
    /// what "identify, track and audit" a row against its neighbours actually needs.
    ///
    /// ONLY CARRIERS, NEVER THE WHOLE CATEGORY. <paramref name="filters"/> is the same set of
    /// parameter filters that classify this element as a carrier, so a sibling is anything
    /// those filters match. Generic Models is also where a real project keeps furniture,
    /// equipment and bespoke joinery, and none of that dims because a schedule row was
    /// clicked.
    ///
    /// <paramref name="keep"/> IS EVERY SELECTED CARRIER, not just one - a multi-select dims
    /// every carrier that is neither of the selected rows, so two selected rows read against
    /// a dimmed model together instead of each one dimming the other.
    /// </summary>
    private static void DimSiblings(Document doc, View view, IReadOnlyCollection<ElementId> keep, List<ElementId> filters)
    {
        try
        {
            var rules = filters
                .Select(doc.GetElement)
                .OfType<ParameterFilterElement>()
                .Select(f => f.GetElementFilter())
                .Where(f => f is not null)
                .ToList();

            if (rules.Count == 0) return;

            var keepSet = keep as HashSet<ElementId> ?? new HashSet<ElementId>(keep);

            var siblings = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_GenericModel)
                .WhereElementIsNotElementType()
                .Where(e => !keepSet.Contains(e.Id) && rules.Any(r => Passes(r!, e)))
                .Select(e => e.Id)
                .ToList();

            if (siblings.Count == 0) return;

            var overrides = new OverrideGraphicSettings()
                .SetHalftone(true)
                .SetSurfaceTransparency(DimTransparencyPercent);

            var dimmed = new List<ElementId>();

            foreach (var siblingId in siblings)
            {
                try
                {
                    view.SetElementOverrides(siblingId, overrides);
                    dimmed.Add(siblingId);
                }
                catch
                {
                    // An element that will not take overrides in this view is left as it was;
                    // the selected row's own highlight still reads against everything else.
                }
            }

            if (dimmed.Count > 0) _dimmed = (view.Id, dimmed);
        }
        catch (Exception ex)
        {
            // Worst case the siblings stay at full strength - the row's region is still
            // revealed and still selected, so the feature degrades rather than fails.
            Log.Warn($"Could not dim sibling carriers in '{view.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Every parameter filter on the view - visible or not - that <paramref name="element"/>
    /// passes. Used to define "sibling carrier" for dimming, which must include a carrier that
    /// was already visible before this click; <see cref="HidingFilters"/> only reports filters
    /// currently switched off and is the wrong list for that.
    /// </summary>
    private static List<ElementId> AllMatchingFilters(View view, Element element)
    {
        var matching = new List<ElementId>();

        try
        {
            foreach (var filterId in view.GetFilters())
            {
                if (view.Document.GetElement(filterId) is not ParameterFilterElement filter) continue;

                try
                {
                    if (filter.GetElementFilter()?.PassesFilter(element) == true) matching.Add(filterId);
                }
                catch
                {
                    // A filter whose rules cannot be evaluated against this element is not one
                    // that classifies it.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the filters on '{view.Name}': {ex.Message}");
        }

        return matching;
    }

    /// <summary>
    /// The view whose filters decide whether the carrier draws - which is NOT the active view
    /// when a schedule row is what was clicked.
    ///
    /// THIS IS THE TRAP. Clicking a row makes the SCHEDULE the active view, and a ViewSchedule
    /// carries no graphic filters at all: asking it for the filter hiding a Generic Model
    /// returns nothing, every time, and the reveal silently does nothing while looking correct
    /// in the code. The view that matters is the graphical one the user is reading alongside
    /// the schedule.
    ///
    /// 3D IS PREFERRED over a plan. A wall-face carrier is a thin plate on the wall's surface,
    /// so in plan it is edge-on and a "successful" reveal shows the user a line. Falling back
    /// to whatever is open is still better than failing.
    /// </summary>
    private static View? TargetView(UIDocument uidoc, Document doc)
    {
        if (doc.ActiveView is { } active && active is not ViewSchedule && !active.IsTemplate)
            return active;

        try
        {
            var open = uidoc.GetOpenUIViews()
                .Select(v => doc.GetElement(v.ViewId))
                .OfType<View>()
                .Where(v => v is not ViewSchedule && !v.IsTemplate)
                .ToList();

            return open.OfType<View3D>().FirstOrDefault() ?? open.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not find a graphical view to reveal in: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The view's filters that are switched OFF and that this element passes - i.e. the ones
    /// actually responsible for it not drawing.
    ///
    /// Tested with the filter's own ElementFilter rather than by re-implementing its rules.
    /// A filter can be a parameter filter, a category filter or a selection set, and only
    /// Revit knows what each one matches.
    /// </summary>
    private static List<ElementId> HidingFilters(View view, Element element)
    {
        var hiding = new List<ElementId>();

        try
        {
            foreach (var filterId in view.GetFilters())
            {
                bool visible;
                try { visible = view.GetFilterVisibility(filterId); }
                catch { continue; }

                if (visible) continue;   // this one is not hiding anything

                if (view.Document.GetElement(filterId) is not ParameterFilterElement filter) continue;

                try
                {
                    // GetElementFilter(), not an ElementFilter property - the property does not
                    // exist on this class in the 2027 API.
                    if (filter.GetElementFilter()?.PassesFilter(element) == true) hiding.Add(filterId);
                }
                catch
                {
                    // A filter whose rules cannot be evaluated against this element is not
                    // the one hiding it.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the filters on '{view.Name}': {ex.Message}");
        }

        return hiding;
    }

    // ------------------------------------------------------------------ restoring

    /// <summary>
    /// Queues a restore-only pass. For the event handler, which has no API context.
    ///
    /// Routed through <see cref="Apply"/> with an empty list rather than a separate method:
    /// restoring IS the "clicked away" case (or "selected only non-carriers"), and one code
    /// path means the two cannot drift.
    /// </summary>
    private static void Restore()
    {
        if (_revealed.Count == 0 && !_hasActiveReveal) return;

        RevitTaskQueue.Post("Restore takeoff carrier visibility",
            app => Apply(app, []));
    }
}
