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
///   Because then every carrier in the model draws at once - a wall of translucent slabs over
///   the whole storey - which is exactly why the filter defaults to off. What is wanted is one
///   surface, the one being read, for as long as it is being read.
///
/// WHAT THIS DOES
///   On every selection change: if the selection is a single Generic Model that some filter in
///   the active view is hiding, that filter is switched on and the view zoomed to the element.
///   When the selection moves to anything else, the filter is switched back off. The user sees
///   the surface for their row and the view returns to how they left it.
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
    /// The element the current reveal was performed for. Revit re-raises SelectionChanged for
    /// selections that did not actually change, and re-running the transaction each time would
    /// put an entry in the undo stack per redundant event.
    /// </summary>
    private static long _revealedFor = -1;

    /// <summary>
    /// The view this class put into temporary hide mode, or null.
    ///
    /// Tracked separately from <see cref="_revealed"/> because temporary hide/isolate is a
    /// MODE on the view, not a per-element flag, and leaving someone else's isolate switched
    /// off would be a worse bug than the one this fixes. Only a mode this class turned on is
    /// ever turned off.
    /// </summary>
    private static ElementId? _hidOthersIn;

    /// <summary>
    /// The camera as it was before this class first moved it, and the view it belongs to.
    ///
    /// Captured only when an orientation is actually CHANGED, never when the check decides the
    /// view was already facing the paint - otherwise browsing a schedule would slowly overwrite
    /// the saved viewpoint with ones this class chose.
    /// </summary>
    private static (ElementId View, ViewOrientation3D Orientation)? _cameraWas;

    /// <summary>The element currently carrying this class's emphasis override, and its view.</summary>
    private static (ElementId View, ElementId Element)? _highlighted;

    /// <summary>
    /// True while this class is calling SetElementIds, so the SelectionChanged that raises can
    /// be told apart from a real click.
    ///
    /// LOAD-BEARING. Without it the re-select at the end of a reveal looks like a new selection,
    /// which reveals again, which re-selects... a loop that hangs Revit rather than failing.
    /// </summary>
    private static bool _selfSelecting;

    /// <summary>
    /// Last element handled and when. Revit re-raises SelectionChanged for selections that did
    /// not change, and every one of those would otherwise cost a transaction.
    ///
    /// Time-based rather than "is it the same id", so clicking the SAME row again after looking
    /// around still re-frames it - which is what someone who has rotated the view away and
    /// wants it back would expect.
    /// </summary>
    private static long _lastHandledId = -1;

    private static DateTime _lastHandledAt = DateTime.MinValue;

    /// <summary>How long an identical selection is treated as an echo rather than a new click.</summary>
    private static readonly TimeSpan EchoWindow = TimeSpan.FromMilliseconds(400);

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
            application.ControlledApplication.DocumentClosing -= OnDocumentClosing;
        }
        catch (Exception ex)
        {
            Log.Warn($"Carrier reveal shutdown was untidy: {ex.Message}");
        }
    }

    /// <summary>
    /// Drops every piece of per-document state. NOT a restore: the document is closing and
    /// cannot be modified, and anything left switched on goes with it.
    /// </summary>
    private static void OnDocumentClosing(object? sender, Autodesk.Revit.DB.Events.DocumentClosingEventArgs e)
    {
        _revealed.Clear();
        _hidOthersIn = null;
        _cameraWas = null;
        _highlighted = null;
        _revealedFor = -1;
        _lastHandledId = -1;
        _lastHandledAt = DateTime.MinValue;
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

            // ONE element only. A multi-select is someone doing something else - revealing a
            // filter because a rubber-band happened to catch a carrier would be a surprise.
            if (ids is null || ids.Count != 1)
            {
                Restore();
                return;
            }

            var id = ids.First();

            if (_selfSelecting)
            {
                Log.Debug($"Selection {id.Value} is our own re-select; ignored.");
                return;
            }

            if (id.Value == _lastHandledId && DateTime.UtcNow - _lastHandledAt < EchoWindow)
            {
                Log.Debug($"Selection {id.Value} repeated within the echo window; ignored.");
                return;
            }

            _lastHandledId = id.Value;
            _lastHandledAt = DateTime.UtcNow;

            RevitTaskQueue.Post("Reveal takeoff carrier", app => Apply(app, id));
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
    /// <paramref name="id"/> may be anything, including a wall or InvalidElementId. Whatever
    /// is not a filtered-out Generic Model simply results in the previous reveal being put
    /// back and nothing new shown, which is what "the user clicked away" should do.
    /// </summary>
    private static void Apply(UIApplication app, ElementId id)
    {
        var uidoc = app.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (doc is null || uidoc is null) return;

        var view = TargetView(uidoc, doc);
        var element = doc.GetElement(id);

        // Carriers are Generic Models in both products. Anything else is a normal selection
        // and none of this class's business - but the restore below still has to happen.
        var carrier = element?.Category?.Id.Value == (long)BuiltInCategory.OST_GenericModel;

        var revealable = carrier && view is not null && !view.IsTemplate;

        // Filled inside the transaction, not before it - see the note at the computation.
        var toReveal = new List<ElementId>();

        if (_revealed.Count > 0 || _hidOthersIn is not null || revealable)
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

                    // Put back any temporary hide THIS class applied, before deciding about a
                    // new one. Never touches a mode someone else switched on.
                    if (_hidOthersIn is not null
                        && doc.GetElement(_hidOthersIn) is View previouslyHidden)
                    {
                        try { previouslyHidden.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate); }
                        catch { /* mode already gone */ }
                    }

                    _hidOthersIn = null;

                    // Always drop the previous emphasis - a row that is no longer the one being
                    // read must not stay painted orange.
                    ClearHighlight(doc);

                    // Put the camera back too, whenever this is not another carrier. Clicking
                    // a wall, or clearing the selection, returns the view to where it was
                    // before the first row was ever clicked.
                    if (!revealable
                        && _cameraWas is { } saved
                        && doc.GetElement(saved.View) is View3D cameraView
                        && !cameraView.IsLocked)
                    {
                        try { cameraView.SetOrientation(saved.Orientation); }
                        catch { /* view changed under us; nothing to restore to */ }
                    }

                    if (!revealable) _cameraWas = null;

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
                    if (revealable) toReveal.AddRange(HidingFilters(view!, element!));

                    foreach (var filterId in toReveal)
                    {
                        view!.SetFilterVisibility(filterId, true);
                        _revealed.Add((view.Id, filterId));
                    }

                    // THE FILTER IS ALL-OR-NOTHING, WHICH IS WHY THIS IS HERE.
                    //
                    // SetFilterVisibility switches on EVERY element the filter covers, so
                    // revealing one carrier reveals all of them - the whole storey's worth of
                    // painted regions at once, which is not what "highlight this row" means.
                    // Revit offers no way to except a single element from a filter, so the
                    // siblings are hidden instead and only the row's own region is left drawn.
                    if (toReveal.Count > 0) HideSiblings(doc, view!, id, toReveal);

                    // Emphasise whenever this is a carrier - NOT only when a filter had to be
                    // switched on. A region that was already visible still needs to stand out
                    // from the wall it lies on, and that is the case a "reveal only" rule
                    // silently skips.
                    if (revealable) Highlight(doc, view!, id);

                    // Turn the camera to face the paint. In the same transaction as everything
                    // else so a click stays one undo entry, and before the re-select below,
                    // which is what puts the highlight back after this clears it.
                    if (revealable && view is View3D three) Orient(three, element!, doc);
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"Carrier visibility could not be changed: {ex.Message}");
                _revealed.Clear();
                _revealedFor = -1;
                return;
            }
        }

        // Logged AFTER the transaction, because before it the filter counts describe the
        // previous row's leftovers rather than this one.
        Log.Debug($"Reveal {id.Value}: carrier={carrier} " +
                  $"category={element?.Category?.Name ?? "none"} " +
                  $"view={view?.Name ?? "none"} revealed={toReveal.Count}");

        _revealedFor = carrier ? id.Value : -1;

        if (!carrier || view is null) return;

        if (view is View3D sectioned) WarnIfOutsideSectionBox(sectioned, element!);

        // RE-ASSERT THE SELECTION, because the transaction above almost certainly cleared it.
        //
        // Revit selects the row's element when a schedule row is clicked, and then a
        // transaction that modifies the VIEW - which switching a filter on and hiding siblings
        // both are - drops that selection. The element is revealed and the camera moves to it,
        // and it is not highlighted, because nothing is selected any more.
        //
        // That is exactly the reported symptom, including its inconsistency: a row whose
        // carrier was already visible needs no transaction, keeps its selection and highlights
        // normally, while a row that needed revealing loses it. Which rows misbehave therefore
        // depends on what the view happened to be showing, not on the row.
        //
        // Set AFTER _revealedFor so the SelectionChanged this raises returns immediately
        // instead of re-entering the whole reveal.
        try
        {
            _selfSelecting = true;
            uidoc.Selection.SetElementIds(new List<ElementId> { id });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not re-select {id.Value} after revealing it: {ex.Message}");
        }
        finally
        {
            _selfSelecting = false;
        }

        Zoom(uidoc, view, element!, id);
    }

    /// <summary>
    /// Zooms the view to the carrier - WITHOUT UIDocument.ShowElements.
    ///
    /// WHY NOT ShowElements
    ///   When the element cannot be drawn anywhere, ShowElements puts up a modal Revit dialog:
    ///   "There is no open view that shows any of the highlighted elements. Searching through
    ///   the closed views could take a long time. Continue?" - on every click, for a feature
    ///   whose whole job is to be unobtrusive while someone reads a schedule.
    ///
    ///   And "cannot be drawn anywhere" is not an edge case here. A carrier with an empty
    ///   Shape has no geometry in any view by construction, and this model contains two kinds:
    ///   rows the unfixed takeoff left without carrier geometry (its own summary calls them
    ///   "in the CSV only"), and every row this add-in's PaintTakeoffBuilder places, which
    ///   never calls SetShape at all - deliberately, because those rows are data.
    ///
    /// SO THE BOUNDING BOX IS THE TEST. A null box in this view means there is nothing to look
    /// at, and the correct response is to leave the camera alone and say why in the log, not to
    /// ask the user whether Revit should go hunting through closed views.
    /// </summary>
    private static void Zoom(UIDocument uidoc, View view, Element element, ElementId id)
    {
        try
        {
            var box = element.get_BoundingBox(view);

            if (box is null)
            {
                // NOTHING TO LOOK AT - so show the WALL the row measured instead of nothing.
                //
                // A shapeless carrier still knows its host: the takeoff writes it into "Paint
                // Segment" as the text the schedule shows in 'Vægflade / flade', which carries
                // the element id after a '#'. Zooming there answers "which surface is this
                // row?" approximately, which beats a click that appears to do nothing at all.
                //
                // Deliberately NOT selected - only zoomed. Replacing the user's selection
                // would break the schedule's own row highlight and lose the exact row they
                // clicked, trading a precise selection for an imprecise one.
                var host = HostOf(element, view);

                if (host is not null)
                {
                    var ui2 = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                    ui2?.ZoomAndCenterRectangle(host.Min, host.Max);

                    Log.Info($"Carrier {id.Value} has no geometry, so its host wall was framed " +
                             "instead. Re-run the takeoff to give this row its own region.");
                    return;
                }

                Log.Info($"Carrier {id.Value} has no geometry in '{view.Name}' and no host could " +
                         "be read from 'Paint Segment', so there is nothing to zoom to. The row " +
                         "is selected and its area is correct; the takeoff placed it without a " +
                         "shape.");
                return;
            }

            // PAD THE BOX, OR THE ZOOM UNDOES THE CAMERA WORK.
            //
            // ZoomAndCenterRectangle fits exactly what it is given, so handing it the region's
            // own box crops straight back to that region - the careful stand-off distance is
            // thrown away and the result is the flat close-up again. A jamb strip 0,24 m²
            // becomes the entire screen.
            //
            // The padding is the region's own size or 2 m, whichever is larger, so a small
            // patch gains real surroundings while a whole wall face is not zoomed out to
            // nothing.
            // Internal units are FEET, so the 2 m minimum has to be converted - a bare 2.0
            // here would be 2 feet and the padding would barely register.
            var minimum = UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Meters);
            var pad = Math.Max((box.Max - box.Min).GetLength(), minimum);
            var padding = new XYZ(pad, pad, pad);

            var ui = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
            ui?.ZoomAndCenterRectangle(box.Min - padding, box.Max + padding);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not zoom to {id.Value}: {ex.Message}");
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
    private static void Highlight(Document doc, View view, ElementId id)
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

            view.SetElementOverrides(id, settings);
            _highlighted = (view.Id, id);
        }
        catch (Exception ex)
        {
            // The reveal and the camera still worked; only the emphasis is missing.
            Log.Warn($"Could not emphasise carrier {id.Value}: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the emphasis. An empty OverrideGraphicSettings is Revit's own "no overrides",
    /// so this restores the element to whatever the view would draw anyway - it does not
    /// assume the element had no overrides of its own before.
    /// </summary>
    private static void ClearHighlight(Document doc)
    {
        if (_highlighted is not { } previous) return;

        try
        {
            if (doc.GetElement(previous.View) is View view)
                view.SetElementOverrides(previous.Element, new OverrideGraphicSettings());
        }
        catch
        {
            // View or element gone; nothing to clear.
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
    /// Turns the 3D view to look straight at the painted region, instead of leaving the camera
    /// wherever it was - which is usually behind the wall, with the paint facing away.
    ///
    /// WHY A ZOOM ALONE CANNOT DO THIS
    ///   ZoomAndCenterRectangle pans and scales; it never rotates. Framing a face while the
    ///   camera points at its back gives a confident view of blank plaster.
    ///
    /// THE HARD PART IS WHICH WAY IS "FRONT"
    ///   A carrier is a thin plate, so its two largest faces are equal in area and exactly
    ///   opposite: one looks into the room, one into the wall. Choosing by area alone is a coin
    ///   toss, and losing it puts the camera inside the wall - the very symptom this fixes.
    ///
    ///   The host wall settles it. The region sits on the wall's surface, so the vector from
    ///   the wall's centre to the region points OUT of the wall, and the correct normal is
    ///   whichever of the pair agrees with it.
    ///
    ///   With no host to compare against, the view is left alone rather than turned to a
    ///   guess: an unchanged camera is merely unhelpful, while a wrong one is actively
    ///   confusing and costs the user their viewpoint.
    /// </summary>
    private static void Orient(View3D view, Element carrier, Document doc)
    {
        try
        {
            // A locked 3D view refuses SetOrientation, and that lock is usually deliberate -
            // a view placed on a sheet at a fixed angle.
            if (view.IsLocked) return;

            var box = carrier.get_BoundingBox(null);
            if (box is null) return;

            var normal = FrontNormal(carrier, doc);
            if (normal is null) return;

            // LEAVE A GOOD VIEW ALONE - BUT DEAD-ON IS NOT A GOOD VIEW.
            //
            // Reorienting on every click takes the 3D view away from someone reading down a
            // schedule, so an angle that already shows the face is left untouched. The band
            // matters though: between about 70 and 20 degrees off square reads as a useful
            // three-quarter view, while closer than that is the flat, contextless elevation
            // this method exists to avoid - a wall filling the frame with nothing around it to
            // locate it by. That case is re-framed rather than kept.
            if (view.GetOrientation()?.ForwardDirection is { } facing)
            {
                var squareness = facing.DotProduct(normal);
                if (squareness < -0.35 && squareness > -0.94) return;
            }

            // Remember where the camera was, ONCE, before the first move - so clicking away
            // can put it back exactly. Overwriting it on later rows would save a viewpoint
            // this class chose rather than the one the user had.
            _cameraWas ??= (view.Id, view.GetOrientation());

            var centre = (box.Min + box.Max) / 2.0;

            var eyeDirection = ObliqueFrom(normal);

            // PULLED BACK OFF THE REGION, NOT OFF ITS OWN SIZE.
            //
            // Sizing the distance from the region alone frames a 0,24 m² jamb strip from
            // inches away - technically correct and useless, because nothing around it is in
            // shot. The host wall is what gives the region a place, so its diagonal sets the
            // distance and the region only sets a floor for very large walls.
            var span = (box.Max - box.Min).GetLength();
            var host = HostBox(carrier, doc);
            if (host is not null) span = Math.Max(span, (host.Max - host.Min).GetLength() * 0.6);

            var distance = Math.Max(span, 3.0) * 1.6;

            var forward = -eyeDirection;

            // Up must not be parallel to forward, or the orientation is degenerate and Revit
            // rejects it. Floor and soffit carriers are exactly that case.
            var up = Math.Abs(forward.Z) > 0.9 ? XYZ.BasisY : XYZ.BasisZ;
            up = (up - forward * up.DotProduct(forward)).Normalize();

            view.SetOrientation(new ViewOrientation3D(centre + eyeDirection * distance, up, forward));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not orient '{view.Name}' to the painted face: {ex.Message}");
        }
    }

    /// <summary>
    /// Swings the eye direction off the face normal into a three-quarter view.
    ///
    /// WHY NOT LOOK STRAIGHT AT IT
    ///   Standing exactly on the normal produces an elevation: the wall fills the frame, every
    ///   edge is perpendicular, and there is no depth cue to say which room you are in or what
    ///   the surface adjoins. It is the least informative angle available, and it was what the
    ///   first version did.
    ///
    ///   Swinging 32 degrees around vertical and lifting the eye brings the return walls,
    ///   floor and ceiling into shot, which is what makes a region legible AS part of a room.
    ///
    /// HORIZONTAL FACES NEED THE OPPOSITE TREATMENT. A floor or soffit carrier has a vertical
    /// normal, and swinging that around the vertical axis changes nothing at all. Those are
    /// tilted sideways instead, so the view comes in across the surface rather than straight
    /// down onto it.
    /// </summary>
    private static XYZ ObliqueFrom(XYZ normal)
    {
        try
        {
            if (Math.Abs(normal.Z) > 0.9)
            {
                // Floor or soffit: lean the eye out sideways to get a raking view.
                return (normal + new XYZ(0.55, 0.35, 0.0)).Normalize();
            }

            var swung = Transform
                .CreateRotation(XYZ.BasisZ, 32.0 * Math.PI / 180.0)
                .OfVector(normal);

            // Raise the eye so the camera looks slightly DOWN at the surface, the angle every
            // architectural 3D view is read at.
            return (swung + XYZ.BasisZ * 0.42).Normalize();
        }
        catch
        {
            return normal;
        }
    }

    /// <summary>
    /// The carrier's room-facing normal: the largest planar face's normal, flipped if it
    /// points into the host wall rather than out of it.
    /// </summary>
    private static XYZ? FrontNormal(Element carrier, Document doc)
    {
        try
        {
            var options = new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine,
            };

            var geometry = carrier.get_Geometry(options);
            if (geometry is null) return null;

            PlanarFace? largest = null;

            foreach (var obj in geometry)
            {
                if (obj is not Solid solid) continue;

                foreach (Face face in solid.Faces)
                {
                    if (face is PlanarFace planar && (largest is null || planar.Area > largest.Area))
                        largest = planar;
                }
            }

            if (largest is null) return null;

            var normal = largest.FaceNormal.Normalize();

            var host = HostBox(carrier, doc);
            if (host is null) return null;   // cannot tell front from back - see Orient

            var box = carrier.get_BoundingBox(null);
            if (box is null) return null;

            var outward = (box.Min + box.Max) / 2.0 - (host.Min + host.Max) / 2.0;

            return normal.DotProduct(outward) < 0 ? -normal : normal;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The bounding box of the wall a shapeless carrier measured, read out of its
    /// "Paint Segment" parameter.
    ///
    /// THE ID IS PARSED OUT OF DISPLAY TEXT, which is not something to do lightly - but that
    /// parameter is the only link a carrier keeps to its host, and its shape is fixed by the
    /// takeoff: "IV_Mål - 100mm #29307987 · Face 0.1". The id is the digits after '#'. Anything
    /// that does not match returns null and the caller says so rather than guessing.
    /// </summary>
    private static BoundingBoxXYZ? HostOf(Element carrier, View view)
        => HostElement(carrier, view.Document)?.get_BoundingBox(view);

    /// <summary>The host wall's box in MODEL space - independent of any view's visibility.</summary>
    private static BoundingBoxXYZ? HostBox(Element carrier, Document doc)
        => HostElement(carrier, doc)?.get_BoundingBox(null);

    /// <summary>
    /// The wall a carrier measured, from the id embedded in its "Paint Segment" text.
    /// </summary>
    private static Element? HostElement(Element carrier, Document doc)
    {
        try
        {
            var text = ParameterHelper.Find(carrier, "Paint Segment")?.AsString();
            if (string.IsNullOrWhiteSpace(text)) return null;

            var match = System.Text.RegularExpressions.Regex.Match(text, @"#(\d+)");
            if (!match.Success) return null;

            if (!long.TryParse(match.Groups[1].Value, out var hostId)) return null;

            return doc.GetElement(new ElementId(hostId));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Temporarily hides every OTHER carrier in the view, so the revealed filter shows one
    /// painted region rather than all of them.
    ///
    /// TEMPORARY HIDE, NOT ISOLATE. Isolate would hide the walls too and leave the region
    /// floating in space with nothing to read it against - the point is to see which surface
    /// on which wall the row measured.
    ///
    /// SKIPPED ENTIRELY if the view is already in temporary hide/isolate mode. That is
    /// someone's own working state, and quietly replacing it - then switching it off on the
    /// next click - would lose work that has nothing to do with this feature.
    /// </summary>
    private static void HideSiblings(Document doc, View view, ElementId keep, List<ElementId> filters)
    {
        try
        {
            if (view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate)) return;

            // ONLY CARRIERS, NEVER THE WHOLE CATEGORY.
            //
            // This used to hide every Generic Model in the document. In this test model that
            // is 43 carriers and nothing else, so it looked correct - but Generic Models are
            // where real projects keep furniture, equipment and bespoke joinery, and clicking
            // a schedule row would have made all of it disappear.
            //
            // The filters just switched on are the definition of "is a carrier", so a sibling
            // is anything those same filters match. Nothing outside them is touched.
            var rules = filters
                .Select(doc.GetElement)
                .OfType<ParameterFilterElement>()
                .Select(f => f.GetElementFilter())
                .Where(f => f is not null)
                .ToList();

            if (rules.Count == 0) return;

            var siblings = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_GenericModel)
                .WhereElementIsNotElementType()
                .Where(e => e.Id != keep
                            && e.CanBeHidden(view)
                            && rules.Any(r => Passes(r!, e)))
                .Select(e => e.Id)
                .ToList();

            if (siblings.Count == 0) return;

            view.HideElementsTemporary(siblings);
            _hidOthersIn = view.Id;
        }
        catch (Exception ex)
        {
            // Worst case the siblings stay visible - the row's region is still revealed and
            // still selected, so the feature degrades rather than fails.
            Log.Warn($"Could not hide sibling carriers in '{view.Name}': {ex.Message}");
        }
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
    /// Routed through <see cref="Apply"/> with an invalid id rather than a separate method:
    /// restoring IS the "clicked away" case, and one code path means the two cannot drift.
    /// </summary>
    private static void Restore()
    {
        if (_revealed.Count == 0 && _revealedFor < 0) return;

        RevitTaskQueue.Post("Restore takeoff carrier visibility",
            app => Apply(app, ElementId.InvalidElementId));
    }
}
