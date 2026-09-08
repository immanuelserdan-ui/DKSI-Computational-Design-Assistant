using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Automation;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Finishes;

/// <summary>
/// Keeps finish areas up to date without anyone pressing a button.
///
/// The design in one line: <b>detect cheaply, recalculate at a moment the user expects
/// a pause.</b>
///
/// Detection is <see cref="FinishStaleUpdater"/>, which runs inside the user's own
/// transaction and does nothing but flip a Yes/No parameter. Recalculation is guaranteed
/// at three points, and none of them is "the instant something changes":
///
///   SAVE, unconditionally - the only point where a delay is both expected and useful,
///   because it guarantees the model on disk (the one that gets synced, issued or
///   scheduled from) has correct areas in it. See <see cref="Recalculate"/>.
///
///   A SCHEDULE BEING OPENED - the only other moment a delay is already expected, and the
///   only moment the numbers are actually being looked at. See <see cref="OnViewActivated"/>.
///
///   THE MODEL GOING QUIET for <see cref="Options.IdleQuietSeconds"/> - the backstop for
///   everything else: someone editing without saving, a schedule left open in a window
///   nobody clicks into. See <see cref="OnIdling"/>.
///
/// RUNNING IT EAGERLY ON EVERY DocumentChanged WAS TRIED AND MEASURED WRONG, not merely
/// rejected on principle. Casework cutting, Udvendig/lining resolution, paint room-override
/// reapply and paint host-type stamping each commit their own transaction, and each one
/// looks like "the model changed" to every other automation - so a single file open could
/// chain into three separate whole-model passes (460ms, 383ms, 224ms measured on
/// FM_Template) within one 16-second burst, which is what "Revit lags after opening a
/// different file" turned out to be. The idle-quiet wait is what collapses a chain like
/// that into the one pass that actually needs to run, once nothing is left to trigger
/// another.
/// </summary>
internal static class FinishAutomation
{
    /// <summary>
    /// Yes/No project parameter, bound to Rooms. Add it as a schedule column and the
    /// rooms waiting on a recalculation are visible at a glance — which is the honest
    /// version of "the schedule is always up to date".
    /// </summary>
    public const string StaleParameter = "Finish Area Stale";

    /// <summary>
    /// Every transaction this add-in opens starts with this. DocumentChanged reports the
    /// transaction names that caused it, so filtering on the prefix means the automation
    /// cannot react to its own writes even if the suppression flag is bypassed by a bug.
    /// Belt as well as braces, because the failure it prevents is an infinite loop.
    /// </summary>
    internal const string TransactionPrefix = "Finish Automation: ";

    /// <summary>
    /// True if a transaction name belongs to this add-in and so must not trigger a
    /// recalculation. Anything that writes to the model from inside DKSI code should name its
    /// transaction with a prefix listed here.
    /// </summary>
    private static bool IsSelfAuthored(string name) =>
        name.StartsWith(TransactionPrefix, StringComparison.Ordinal) ||
        name.StartsWith(Overlay.PaintHighlight.TransactionPrefix, StringComparison.Ordinal);

    private static UIControlledApplication? _uiApp;
    private static FinishStaleUpdater? _updater;
    private static bool _warnedAboutMissingParameter;

    private static ExternalEvent? _syncEvent;

    /// <summary>Counts DocumentChanged invocations, so "the event never fired" is provable.</summary>
    private static int _documentChangedCount;

    // ---------------------------------------------------------------- settings

    private sealed class Options
    {
        public bool Enabled { get; set; } = true;
        public bool RecalculateOnSave { get; set; } = true;

        /// <summary>
        /// Fix room upper limits while the user works, without waiting for a save.
        /// Safe to leave on: the adjustment is a bounding-box query over a handful of
        /// rooms, not the finish measurement.
        /// </summary>
        public bool AdjustLimitsWhileWorking { get; set; } = true;

        /// <summary>
        /// Recalculate finish areas when a schedule view is opened.
        ///
        /// This is the trigger that makes the numbers right at the only moment anyone
        /// can tell — when they are being looked at. The pause lands while the schedule
        /// view is drawing, which is when a pause is already expected.
        /// </summary>
        public bool RecalculateWhenScheduleOpened { get; set; } = true;

        /// <summary>
        /// Recalculate after the model has sat completely still for
        /// <see cref="IdleQuietSeconds"/>, instead of running a full pass on every single
        /// DocumentChanged that leaves work outstanding.
        ///
        /// Defaults ON. It is the backstop for someone editing without saving and for a
        /// schedule left open in a window that never gets clicked into — the case
        /// <see cref="RecalculateWhenScheduleOpened"/> cannot catch — and it is what stops
        /// a chain of unrelated automations (casework, openings, paint carriers) each
        /// triggering their own whole-model pass in response to the others' writes. See
        /// <see cref="OnIdling"/> and the debounce note in <see cref="FlushPendingWork"/>.
        ///
        /// Turned off, a flush runs its pass immediately whenever anything is dirty - the
        /// old, unconditional behaviour - which is the escape hatch if the wait itself
        /// ever needs to be ruled out as a cause. Each pass is still scoped to the rooms
        /// actually marked dirty, not the whole model, either way.
        /// </summary>
        public bool RecalculateWhileIdle { get; set; } = true;

        /// <summary>How long the model must sit untouched before the deferred pass runs.
        /// Long enough that a burst of chained automation writes settles into one pass;
        /// short enough that a genuinely idle user is not left looking at stale numbers
        /// for long. The log line after each pass reports the milliseconds it actually
        /// took, which is the number to watch if this needs tuning.</summary>
        public int IdleQuietSeconds { get; set; } = 8;

        /// <summary>
        /// Writes a DEBUG line for every committed transaction, including the ones that
        /// are filtered out. Defaults ON while the automation is being commissioned —
        /// this is the evidence that distinguishes "never fired" from "fired and skipped".
        /// Turn it off once the behaviour is trusted.
        /// </summary>
        public bool VerboseLogging { get; set; } = true;
    }

    /// <summary>
    /// Per-document flags: finish work owed, opening pass owed, and the idle-quiet clock.
    ///
    /// PER-DOCUMENT, NOT GLOBAL, and that is a correctness fix rather than tidiness. These were
    /// plain statics, and every one of them was read back in <see cref="FlushPendingWork"/>
    /// against whatever document happened to be active when the ExternalEvent fired - which,
    /// after an eight-second idle debounce, is not necessarily the document that set them. See
    /// <see cref="DocumentScoped{T}"/> for the full account of what that cost.
    /// </summary>
    private static readonly DocumentScoped<DocumentFlags> Flags = new();

    /// <summary>
    /// True while the automation is writing to the model itself.
    ///
    /// Without this the feature feeds itself: adjusting a room's upper offset IS a room
    /// geometry change, and rooms are in the updater's own trigger filter. So every pass
    /// re-marks the rooms it just fixed, scheduling another pass. It does converge — the
    /// second pass finds nothing to change — but it doubles the cost of every edit and
    /// leaves the model permanently one pass behind "clean".
    ///
    /// The updater's own re-entrancy guard cannot catch this: that protects a single
    /// Execute call, whereas this loop runs through a separate transaction on a later
    /// idle tick.
    /// </summary>
    public static bool Suppressed { get; private set; }

    /// <summary>
    /// Runs an action with the change updater muted. Restored in a finally, so a throw
    /// mid-write cannot leave the updater permanently deaf.
    /// </summary>
    private static void WithoutSelfTriggering(Action action)
    {
        var previous = Suppressed;
        Suppressed = true;

        try
        {
            action();
        }
        finally
        {
            Suppressed = previous;
        }
    }

    /// <summary>
    /// Rooms touched since the last idle sweep, PER DOCUMENT. Held in memory rather than read
    /// back from the model each tick, so an idle tick with nothing to do costs one comparison.
    ///
    /// The ElementIds in here are only meaningful inside the document that minted them, which
    /// is exactly why the set is keyed by document - see <see cref="DocumentScoped{T}"/>.
    /// </summary>
    private static readonly DocumentScoped<HashSet<ElementId>> Pending = new();

    /// <summary>
    /// Rooms marked dirty since the last full pass, kept independently of <see cref="Pending"/>
    /// because that set is drained by <see cref="AdjustQueuedRoomLimits"/> on every flush - long
    /// before a full pass runs on save. This is what lets <see cref="RunFullPass"/> measure only
    /// the rooms that need it instead of the whole model. Populated everywhere <c>Pending</c> is;
    /// entries are removed only once a full pass has actually measured them - see
    /// <see cref="ScopeForPass"/> and the write-back at the end of <see cref="RunFullPass"/>.
    ///
    /// Per document, for the same reason as <c>Pending</c>.
    /// </summary>
    private static readonly DocumentScoped<HashSet<ElementId>> DirtyFinishRooms = new();

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cda", "RevitAddin", "finish-automation.json");

    private static Options _options = Load();

    public static bool Enabled => _options.Enabled;

    public static bool RecalculateOnSave => _options.RecalculateOnSave;

    private static Options Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<Options>(File.ReadAllText(SettingsPath)) ?? new Options()
                : new Options();
        }
        catch (Exception ex)
        {
            // A corrupt settings file must not stop the add-in loading.
            Log.Warn($"Finish automation settings unreadable, using defaults: {ex.Message}");
            return new Options();
        }
    }

    public static void SetEnabled(bool enabled)
    {
        _options.Enabled = enabled;
        Save();

        // Triggers are what actually cost time in the user's transaction, so turning the
        // feature off removes them rather than leaving them firing into a no-op.
        if (enabled) AddTriggers();
        else if (_updater is not null) UpdaterRegistry.RemoveAllTriggers(_updater.GetUpdaterId());

        Log.Info($"Finish automation {(enabled ? "enabled" : "disabled")}.");
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(_options, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn($"Finish automation settings could not be saved: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>
    /// Called once from OnStartup. Failures here are logged and swallowed: a broken
    /// automation feature must not stop the six working tools from loading.
    /// </summary>
    public static void Register(UIControlledApplication application)
    {
        try
        {
            _uiApp = application;
            Log.Verbose = _options.VerboseLogging;

            // Created once at startup: ExternalEvent.Create must be called from a valid
            // API context, and OnStartup is one. Creating it lazily from inside a
            // read-only event handler throws.
            _syncEvent = ExternalEvent.Create(new FinishSyncEventHandler());

            // The primary trigger. DocumentChanged fires after EVERY committed transaction
            // with the exact ids that changed — no polling, no debounce, no guessing.
            application.ControlledApplication.DocumentChanged += OnDocumentChanged;

            _updater = new FinishStaleUpdater(application.ActiveAddInId);

            // isOptional: true means a model that has never seen this add-in still opens
            // cleanly on a machine without it, instead of warning about a missing updater.
            UpdaterRegistry.RegisterUpdater(_updater, isOptional: true);

            if (_options.Enabled) AddTriggers();

            application.ControlledApplication.DocumentSaving += OnDocumentSaving;
            application.ControlledApplication.DocumentSavingAs += OnDocumentSavingAs;

            // Opening a file is not a change, so DocumentChanged never fires for it and a
            // model saved before the resolvers last ran opens showing stale values.
            application.ControlledApplication.DocumentOpened += OnDocumentOpened;

            // Releases that document's queues and flags. The per-document maps are what keep
            // one project's banked work off another project's elements; this is what keeps
            // them from outliving the documents they describe.
            application.ControlledApplication.DocumentClosing += OnDocumentClosing;

            // The trigger that closes the loop for schedules. Opening a schedule is the
            // moment its numbers start mattering, and the only moment a delay is already
            // expected — the view is drawing anyway.
            application.ViewActivated += OnViewActivated;

            // Settles a full pass once the model actually goes quiet, instead of running one
            // after every write in a chain of unrelated automations - see OnIdling and the
            // debounce note in FlushPendingWork.
            application.Idling += OnIdling;

            Log.Info($"Finish automation registered (enabled={_options.Enabled}, " +
                     $"recalcOnSave={_options.RecalculateOnSave}).");
        }
        catch (Exception ex)
        {
            Log.Error("Finish automation could not be registered.", ex);
        }
    }

    public static void Unregister(UIControlledApplication application)
    {
        try
        {
            application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
            application.ControlledApplication.DocumentSaving -= OnDocumentSaving;
            application.ControlledApplication.DocumentSavingAs -= OnDocumentSavingAs;
            application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
            application.ControlledApplication.DocumentClosing -= OnDocumentClosing;
            application.ViewActivated -= OnViewActivated;
            application.Idling -= OnIdling;

            if (_updater is not null && UpdaterRegistry.IsUpdaterRegistered(_updater.GetUpdaterId()))
                UpdaterRegistry.UnregisterUpdater(_updater.GetUpdaterId());
        }
        catch (Exception ex)
        {
            Log.Warn($"Finish automation shutdown was untidy: {ex.Message}");
        }
    }

    private static void AddTriggers()
    {
        if (_updater is null) return;

        var id = _updater.GetUpdaterId();
        UpdaterRegistry.RemoveAllTriggers(id);

        var filter = FinishStaleUpdater.TriggerFilter();

        // GetChangeTypeAny, NOT GetChangeTypeGeometry.
        //
        // Paint is stored ON the element, so applying or removing it is a generic element
        // change — it is NOT a geometry change. Triggering on geometry alone therefore
        // misses the single most important edit this tool exists to react to: someone
        // painting a surface. It also misses parameter edits that change what counts as a
        // finish. Any is the correct breadth here; the handler is cheap enough to afford it.
        UpdaterRegistry.AddTrigger(id, filter, Element.GetChangeTypeAny());
        UpdaterRegistry.AddTrigger(id, filter, Element.GetChangeTypeElementAddition());
        UpdaterRegistry.AddTrigger(id, filter, Element.GetChangeTypeElementDeletion());
    }

    // ---------------------------------------------------------------- staleness

    /// <summary>
    /// Flags one room as needing recalculation. Returns false when it was already
    /// flagged, so the updater can report how many rooms it actually changed rather
    /// than how many it looked at.
    /// </summary>
    public static bool MarkStale(Element room)
    {
        // QUEUE FIRST, UNCONDITIONALLY.
        //
        // This ordering is load-bearing and was wrong in the first version. The upper-limit
        // adjustment needs no parameter at all — it reads geometry and writes a built-in
        // room property. Queuing it after the 'Finish Area Stale' lookup meant that in any
        // model without that parameter (i.e. every model, until someone adds it) nothing
        // was ever queued, the idle sweep always found an empty list, and room limits never
        // moved. The optional bookkeeping flag silently disabled the feature that matters.
        // room.Document, not the active document. This runs inside an IUpdater, which fires for
        // whichever document the user's transaction belonged to - not necessarily the one in
        // front of them - so the room itself is the only trustworthy source of "which document
        // does this id belong to".
        var doc = room.Document;

        Pending.For(doc).Add(room.Id);
        DirtyFinishRooms.For(doc).Add(room.Id);

        var flags = Flags.For(doc);
        flags.LastChangeUtc = DateTime.UtcNow;
        flags.FinishDirty = true;

        // The updater is an INDEPENDENT trigger, not a helper for DocumentChanged. It runs
        // inside the user's own transaction and sees the change directly, so raising the
        // event here means edits are picked up even when the DocumentChanged path is
        // filtered, broken or never reaches us. Two paths, one queue — Raise() is
        // idempotent, so both firing costs one pass, not two.
        _syncEvent?.Raise();

        var parameter = room.LookupParameter(StaleParameter);

        if (parameter is null or { IsReadOnly: true })
        {
            WarnOnceAboutMissingParameter();
            return false;
        }

        if (parameter.AsInteger() == 1) return false;

        parameter.Set(1);
        return true;
    }

    private static void WarnOnceAboutMissingParameter()
    {
        if (_warnedAboutMissingParameter) return;
        _warnedAboutMissingParameter = true;

        Log.Warn($"'{StaleParameter}' is not bound to Rooms in this model, so staleness " +
                 "cannot be recorded. Bind it as a Yes/No project parameter on Rooms.");
    }

    /// <summary>
    /// Whether a recalculation is owed.
    ///
    /// The IN-MEMORY flag is the authority; the 'Finish Area Stale' parameter is only a
    /// second opinion that survives a restart. This ordering matters and got it wrong
    /// twice: deriving "is work owed?" from an OPTIONAL parameter means that in any model
    /// without it the answer is permanently no, and every trigger becomes a silent no-op.
    /// Optional things must never gate required ones.
    /// </summary>
    /// <remarks>
    /// The banked room set counts as dirty too, and that is what makes a room the last pass
    /// could not reach - checked out by another user, most often - get retried at the next save
    /// rather than being forgotten. <see cref="RunFullPass"/> clears the FLAG after every pass
    /// but leaves those rooms banked; see the note there for why the two are separate.
    /// </remarks>
    public static bool IsDirty(Document doc) =>
        Flags.For(doc).FinishDirty ||
        (DirtyFinishRooms.Has(doc) && DirtyFinishRooms.For(doc).Count > 0) ||
        StaleRooms(doc).Count > 0;

    /// <summary>
    /// Rooms flagged for recalculation.
    ///
    /// The LINQ form of this opened a parameter on EVERY room in the document, in managed
    /// code, every time anything asked "is work owed?" - which the idle path does often. An
    /// ElementParameterFilter pushes the same test into Revit's own evaluation, so the cost
    /// no longer scales with the number of rooms that are NOT stale.
    ///
    /// Falls back to the managed sweep when the parameter is not bound, because
    /// ParameterFilterRuleFactory needs a real parameter id and there is none to give.
    /// </summary>
    private static IReadOnlyList<Element> StaleRooms(Document doc)
    {
        var rooms = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType();

        var filter = StaleFilter(doc);

        return filter is null
            ? [.. rooms.Where(r => r.LookupParameter(StaleParameter)?.AsInteger() == 1)]
            : [.. rooms.WherePasses(filter)];
    }

    /// <summary>
    /// A native filter for "Finish Area Stale = Yes", built from the parameter's own id.
    /// Resolved per call rather than cached: the binding can be added mid-session, and a
    /// cached null would keep the tool on the slow path for the rest of the session.
    /// </summary>
    private static ElementFilter? StaleFilter(Document doc)
    {
        try
        {
            var sample = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .FirstElement();

            var parameter = sample?.LookupParameter(StaleParameter);
            if (parameter is null || parameter.StorageType != StorageType.Integer) return null;

            return new ElementParameterFilter(
                ParameterFilterRuleFactory.CreateEqualsRule(parameter.Id, 1));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Clears the stale flag on exactly the rooms a pass measured - not every room that
    /// currently reads stale in the document.
    ///
    /// That distinction did not matter while every pass was whole-model: "measured" and
    /// "every stale room" were the same set. Once RunFullPass can be scoped, they are not - a
    /// room skipped inside the pass (locked by another user, unplaced, geometry-invalid) must
    /// stay flagged so the next pass retries it, rather than being marked clean by a pass that
    /// never actually reached it.
    /// </summary>
    private static void ClearStale(Document doc, IReadOnlyCollection<long> processedRoomIds)
    {
        foreach (var idValue in processedRoomIds)
        {
            if (doc.GetElement(new ElementId(idValue)) is not { } room) continue;

            var parameter = room.LookupParameter(StaleParameter);
            if (parameter is { IsReadOnly: false }) parameter.Set(0);
        }
    }

    // ------------------------------------------------------- the primary trigger

    /// <summary>
    /// Fires after every committed transaction, with the exact ids that changed.
    ///
    /// Read-only: nothing may be written here. The work is queued and an ExternalEvent is
    /// raised so Revit calls back when writing is legal.
    ///
    /// Note that every exit path below LOGS. In the first version they were silent, so
    /// "the event never fired" and "the event fired and was filtered out" produced the
    /// same evidence — an empty log — and that ambiguity cost several rounds of guessing.
    /// </summary>
    private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        _documentChangedCount++;

        if (!_options.Enabled)
        {
            Log.Debug($"DocumentChanged #{_documentChangedCount}: ignored, automation is off.");
            return;
        }

        if (Suppressed)
        {
            Log.Debug($"DocumentChanged #{_documentChangedCount}: ignored, this is our own write.");
            return;
        }

        try
        {
            var doc = e.GetDocument();

            // DELIBERATELY NOT CHECKING IsReadOnly HERE.
            //
            // A document IS read-only for the duration of DocumentChanged — the event is a
            // notification, and Revit forbids writing from it. Testing IsReadOnly here
            // therefore rejects every edit in every project, which is exactly what the log
            // showed: "#1 ... is not a document we edit" against a perfectly normal file.
            // It was a "can I write?" test placed in the one context where the answer is
            // always no. The real check belongs where writing actually happens, in
            // RunFullPass, and it is still there.
            if (doc.IsFamilyDocument || doc.IsLinked)
            {
                Log.Debug($"DocumentChanged #{_documentChangedCount}: '{doc.Title}' skipped " +
                          $"(family={doc.IsFamilyDocument}, linked={doc.IsLinked}).");
                return;
            }

            // Defence in depth against reacting to our own writes: the suppression flag is
            // a bool that a future bug could leave unset, but a transaction we opened
            // always carries one of our prefixes.
            //
            // The paint highlight is listed alongside the automation's own prefix because it
            // is a LOOKING tool that happens to write: it draws temporary DirectShapes to show
            // where a paint area was measured. Without this, drawing an overlay queued a room
            // recalculation and clearing one queued a full-model sweep - the tool for
            // inspecting the numbers was changing them. That was the exact defect recorded
            // when the earlier QA overlay was removed in 65e0d45, whose transactions were
            // named "QA - ..." and matched nothing here.
            if (e.GetTransactionNames().Any(IsSelfAuthored))
            {
                Log.Debug($"DocumentChanged #{_documentChangedCount}: skipped, self-authored transaction.");
                return;
            }

            var touched = e.GetModifiedElementIds().Concat(e.GetAddedElementIds()).ToList();
            var deleted = e.GetDeletedElementIds();

            if (touched.Count == 0 && deleted.Count == 0)
            {
                Log.Debug($"DocumentChanged #{_documentChangedCount}: nothing added, changed or deleted.");
                return;
            }

            // MOVED HERE, ahead of the room match below, rather than set only when a room is
            // actually found (further down). The per-document clock is the idle-quiet clock every
            // debounced pass in FlushPendingWork reads - RunFullPass, and since the fix
            // described in that method's comments, RunOpeningPass and CaseworkCutAutomation's
            // whole-model sweep too. A door placed in a corridor with no Room object yet takes
            // the "rooms.Count == 0" branch below, which still queues Opening/Casework work
            // and raises the sync event - but with the clock only bumped in the room-found
            // branch, a modelling burst made up entirely of that pattern (a very ordinary way
            // to place several doors before drawing the rooms around them) would read as
            // "already idle" on every single event, because the clock never advanced. That
            // silently defeated the debounce for exactly the edit pattern it exists to cover.
            // A real, uncontested edit just committed either way, room or not - so the clock
            // updates for both.
            //
            // The clock belongs to THIS document, not to the session: two open projects each
            // debounce on their own last edit, and going quiet in one no longer pulls the
            // other's queued work forward.
            var flags = Flags.For(doc);
            flags.LastChangeUtc = DateTime.UtcNow;

            // Asked BEFORE the room matching below, and independently of it. A door in a
            // corridor that has no room placed yet still needs its Udvendig reference
            // resolved, and the room sweep would have discarded that change entirely.
            OpeningAutomation.MarkDirtyIfOpenings(doc, touched, deleted.Count);

            // Same reasoning, and for the same reason it cannot wait for the room sweep: a
            // fitting placed in a room that has not been drawn yet still needs the wall
            // beside it cut, and RoomsFor would have discarded that change entirely.
            CaseworkCutAutomation.MarkDirty(doc, touched, deleted.Count);

            var rooms = RoomsFor(doc, touched);

            // A deleted door still changes the wall's painted area, but a deleted id can no
            // longer be resolved to geometry. Revit normally reports the host wall as
            // modified in the same transaction, which is what catches it — so only fall
            // back to a full sweep when nothing else matched.
            if (rooms.Count == 0 && deleted.Count > 0)
            {
                foreach (var room in AllPlacedRooms(doc)) rooms.Add(room.Id);
                Log.Debug($"DocumentChanged #{_documentChangedCount}: {deleted.Count} deletion(s) matched " +
                          $"no host; queued a full sweep of {rooms.Count} room(s).");
            }

            if (rooms.Count == 0)
            {
                Log.Debug($"DocumentChanged #{_documentChangedCount}: " +
                          $"{touched.Count} change(s) touched no room.");

                // No room work, but there may still be opening or casework work — and those
                // paths have their own flags. Returning without raising here is what would
                // strand them.
                if (OpeningAutomation.IsDirty(doc) || CaseworkCutAutomation.IsDirty(doc))
                    _syncEvent?.Raise();

                return;
            }

            var pending = Pending.For(doc);
            var dirty = DirtyFinishRooms.For(doc);

            foreach (var id in rooms)
            {
                pending.Add(id);
                dirty.Add(id);
            }

            // The clock is already current - set unconditionally above, ahead of the room
            // match, for every real change whether or not it resolves to a room.
            flags.FinishDirty = true;

            Log.Debug($"DocumentChanged #{_documentChangedCount}: {touched.Count} changed, " +
                      $"{deleted.Count} deleted -> {rooms.Count} room(s) queued.");

            // Hand off to a context where writing is allowed.
            _syncEvent?.Raise();
        }
        catch (Exception ex)
        {
            // This runs inside Revit's own transaction handling; throwing can abort the
            // user's edit.
            Log.Error($"DocumentChanged #{_documentChangedCount} handler failed.", ex);
        }
    }

    /// <summary>Rooms bounded by, or near, the changed elements.</summary>
    private static HashSet<ElementId> RoomsFor(Document doc, IReadOnlyCollection<ElementId> changed)
    {
        var rooms = new HashSet<ElementId>();

        foreach (var id in changed)
        {
            var element = doc.GetElement(id);
            if (element is null) continue;

            if (element is Room room)
            {
                rooms.Add(room.Id);
                continue;
            }

            var box = element.get_BoundingBox(null);
            if (box is null) continue;

            const double margin = 2.0;   // feet; a wall's box stops at its faces
            var outline = new Outline(
                new XYZ(box.Min.X - margin, box.Min.Y - margin, box.Min.Z - margin),
                new XYZ(box.Max.X + margin, box.Max.Y + margin, box.Max.Z + margin));

            foreach (var near in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Rooms)
                         .WhereElementIsNotElementType()
                         .WherePasses(new BoundingBoxIntersectsFilter(outline))
                         .ToElementIds())
            {
                rooms.Add(near);
            }
        }

        return rooms;
    }

    private static IEnumerable<Element> AllPlacedRooms(Document doc) =>
        new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .Where(r => r.Area > 0);

    /// <summary>
    /// Does the queued work. Called from the ExternalEvent, where writing is legal.
    /// </summary>
    public static void FlushPendingWork(Document doc)
    {
        // ON-OPEN PASS, ahead of everything else and FORCED.
        //
        // DocumentChanged is the only other trigger, and opening a file is not a change - so
        // a model saved before the resolvers last ran shows stale values until somebody
        // happens to edit something. Doors still reading 'Udvendig' in a freshly opened
        // template is exactly that, and it reads as "the automation is not running" when the
        // automation is working perfectly and has simply never been asked.
        //
        // Forced, because IsDirty tracks changes made in THIS session and a file just opened
        // has none. The dirty flag is the right guard for edits and the wrong one here.
        var flags = Flags.For(doc);

        if (flags.OpenedPending)
        {
            flags.OpenedPending = false;
            RunOpeningPass(doc, "the document was opened", force: true);
        }

        AdjustQueuedRoomLimits(doc);

        // READ AND CONSUMED ONCE PER FLUSH, ahead of every pass below - not just the finish
        // pass, which is where this used to be computed. A schedule opened with work
        // outstanding is now an instant answer everywhere, not only from RunFullPass.
        var dueToSchedule = _scheduleOpenPending;
        _scheduleOpenPending = false;

        var runNow = dueToSchedule || !_options.RecalculateWhileIdle ||
            DateTime.UtcNow - flags.LastChangeUtc >= TimeSpan.FromSeconds(_options.IdleQuietSeconds);

        // FIRST OF THE THREE. Cutting a wall changes its geometry, so anything measured
        // before the cut is measured against a wall that no longer exists in that shape.
        //
        // allowSweep: runNow, NOT force. A fitting JUST PLACED OR MOVED is still cut on THIS
        // flush, unconditionally - CaseworkCutAutomation.Run only skips scoped work when
        // there is none queued, regardless of runNow - because that immediate, single-fitting
        // reaction is the documented behaviour (see that class's own header comment) and nine
        // of the ten reasons this add-in feels slow during modelling are NOT that one. What
        // waits for runNow is the WHOLE-MODEL sweep a wall or floor edit alone schedules
        // (CaseworkCutAutomation.SweepOwed) - previously that sweep could get pulled forward
        // by an unrelated fitting nudge (see Run's allowSweep doc for the bug that caused),
        // running a full CaseworkVoidCutter pass over every casework instance in the model on
        // what looked, from the ribbon, like an ordinary single-fitting edit.
        RunCaseworkPass(doc, "the model changed", allowSweep: runNow);

        // BEFORE the finish pass, not after. Lining resolution changes "Lining YN" and the
        // window material, both of which feed what the finish engine counts as painted. Run
        // it second and every pass would publish areas computed from the previous state.
        //
        // GATED ON runNow, where it previously ran unconditionally on every flush.
        // OpeningAutomation.Run has no scoped mode of its own - its own header comment says
        // plainly "it always passes an empty selection and lets them sweep the whole model" -
        // so unlike the casework pass there is no cheap immediate reaction to protect here.
        // _dirty is also set by conditions far broader than "a door or window changed": ANY
        // deletion anywhere in the model, or any room rename, both set it (see
        // MarkDirtyIfOpenings), which is a deliberate choice - the alternative is a stale
        // schedule - not a bug to narrow. Narrowing it would trade a performance problem for
        // a correctness one. Debouncing it instead, the same way the finish pass already is,
        // fixes the performance problem without touching what counts as dirty at all: a burst
        // of chained edits - deletions, room renames, door nudges - now costs ONE whole-model
        // Udvendig+Lining resolve once the model goes quiet, not one synchronous resolve per
        // edit while the user is still actively modelling.
        if (runNow) RunOpeningPass(doc, "the model changed");

        // DEBOUNCED, NOT IMMEDIATE - and this is the one change in this method that isn't
        // "run everything owed right now".
        //
        // Measured on FM_Template: opening it fires Casework, Opening/Udvendig, Paint
        // room-override reapply, and Paint host-type stamping in a chain, each committing
        // its own transaction - and each transaction is a real DocumentChanged, not a
        // self-authored one this add-in can filter out, because it genuinely is new
        // information the finish engine has to react to. Running RunFullPass eagerly on
        // every one of those meant THREE separate whole-model passes (460ms, 383ms, 224ms)
        // landing back to back within the same 16-second burst - which is what "constant
        // lagging right after opening a file" actually was.
        //
        // RunFullPass's own doc comment already named "idle" as one of its three intended
        // callers, alongside save and schedule-opened; there was simply no Idling handler
        // wiring it up yet, and RecalculateWhileIdle/IdleQuietSeconds sat declared and
        // unused below. This finishes that: unless a schedule is actually being looked at
        // right now (dueToSchedule, unaffected - see OnViewActivated) or the idle path is
        // switched off, the pass is left for OnIdling to run once the model has genuinely
        // gone quiet, so five chained writes collapse into the ONE pass that measures all
        // of them, not three that each measure a partial, then-stale, snapshot.
        if (flags.FinishDirty && runNow)
            RunFullPass(doc, dueToSchedule ? "a schedule was opened" : "the model changed");
    }

    /// <summary>
    /// Set by <see cref="OnViewActivated"/> when it raises the sync event so a dirty room's
    /// numbers are right the moment a schedule is actually being looked at, and cleared the
    /// next time <see cref="FlushPendingWork"/> runs. Lets that one flush skip the idle-quiet
    /// wait <see cref="DocumentFlags.FinishDirty"/> would otherwise be subject to, without
    /// giving every other reason for a flush the same free pass.
    /// </summary>
    private static bool _scheduleOpenPending;

    /// <summary>
    /// The other half of the idle-quiet wait above: without this, a model that goes quiet
    /// with work still owed - the common case, since nobody keeps editing forever - would
    /// never get flushed at all. <see cref="FlushPendingWork"/> only runs when something
    /// raises the sync event, and once the last DocumentChanged has already happened,
    /// nothing will raise it again on its own.
    ///
    /// Cheap on the common tick: two field reads and a return, the same shape
    /// <c>CarrierRevealService.OnIdling</c> already uses for the same reason.
    /// </summary>
    private static void OnIdling(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
    {
        try
        {
            if (!_options.Enabled || !_options.RecalculateWhileIdle) return;

            // THE ACTIVE DOCUMENT DECIDES, because that is the document FlushPendingWork will
            // be handed when the sync event fires. Asking "is any work owed anywhere?" and
            // then flushing whatever happens to be in front of the user is precisely the
            // mismatch this whole file was reworked to remove: the answer has to come from the
            // same document the answer will be acted on.
            //
            // Work owed in a document that is NOT active simply waits. It is not lost - its
            // queue is still banked under its own key - and the next idle tick after the user
            // switches back to it raises the event for that document instead.
            var doc = (sender as UIApplication)?.ActiveUIDocument?.Document;
            if (doc is null || doc.IsFamilyDocument || doc.IsLinked) return;

            // NOT JUST FinishDirty. Casework and Opening work can be pending without it: an
            // edit that matches no room (a door in a corridor with no Room object yet, or a
            // wall/floor nudge - see OnDocumentChanged's "rooms.Count == 0" branch and
            // CaseworkCutAutomation.MarkDirty) queues THEIR work and raises the sync event
            // directly, without ever touching FinishDirty, which belongs to the finish pass
            // alone. Gating this wake-up on that flag only would strand that work indefinitely
            // once the burst that queued it ends - nothing else would ever ask
            // FlushPendingWork to look at it again, and it would sit un-run until the next
            // edit that happens to touch a room, or the next save.
            var flags = Flags.For(doc);

            var owed = flags.FinishDirty || flags.OpenedPending ||
                       OpeningAutomation.IsDirty(doc) || CaseworkCutAutomation.SweepOwed(doc);

            if (!owed) return;

            if (DateTime.UtcNow - flags.LastChangeUtc < TimeSpan.FromSeconds(_options.IdleQuietSeconds))
                return;

            _syncEvent?.Raise();
        }
        catch (Exception ex)
        {
            Log.Warn($"Finish automation idle check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Drives <see cref="CaseworkCutAutomation"/> with the change updater muted, exactly as
    /// the other two passes are driven. Its writes land on walls, which are in the updater's
    /// trigger filter, so without this the pass re-queues itself indefinitely.
    ///
    /// A cut that lands makes the finish areas stale, and says so. Without that line a pass
    /// triggered purely by a casework placement would cut the wall and then publish the areas
    /// it measured before the bite was taken out of it — the schedule would be wrong until
    /// something unrelated happened to dirty a room.
    /// </summary>
    private static void RunCaseworkPass(Document doc, string reason, bool force = false, bool allowSweep = true)
    {
        // The guard now has to ask about BOTH kinds of owed work, not just PendingFittings -
        // otherwise a wall/floor sweep that just became allowed (runNow flipped true on this
        // flush) would be skipped here before CaseworkCutAutomation.Run ever got a chance to
        // notice SweepOwed, and it would sit deferred forever instead of running once idle.
        if (!force && !CaseworkCutAutomation.IsDirty(doc) &&
            !(allowSweep && CaseworkCutAutomation.SweepOwed(doc))) return;

        IReadOnlyList<ElementId> cut = [];
        WithoutSelfTriggering(() =>
            cut = CaseworkCutAutomation.Run(doc, TransactionPrefix, reason, force, allowSweep));

        if (cut.Count == 0) return;

        // THE ROOMS ROUND THE CUT WALLS, NOT JUST THE FLAG.
        //
        // This used to set the dirty flag and nothing else, and that combination was a real
        // defect rather than an omission: RunFullPass decides WHETHER to run from the flag but
        // decides WHAT to measure from DirtyFinishRooms, so a casework cut set the first
        // without ever touching the second. A fitting placed where no Room object has been
        // drawn yet - the exact case OnDocumentChanged's "rooms.Count == 0" branch exists for -
        // therefore produced "work is owed" with an empty scope, and the scoped collector
        // Revit builds from an empty id set throws ArgumentException by contract. The pass
        // failed, the flag stayed set by design, and every later flush AND every save re-entered
        // the same throw - so finish areas silently stopped updating until something unrelated
        // dirtied a room.
        //
        // Naming the rooms round the walls that were actually cut fixes the cause instead of
        // the symptom: the scope is now non-empty exactly when the flag is set, and it holds
        // the rooms whose measurements the cut genuinely invalidated.
        var affected = RoomsFor(doc, cut);

        // NO ROOMS MEANS NO FINISH WORK, so the flag is not raised either.
        //
        // This is the case that used to break the pass: a fitting cutting a wall in a corridor
        // nobody has drawn a Room over yet. There is genuinely nothing for the finish engine to
        // measure - no room's areas changed, because no room is there - and saying so by leaving
        // the flag alone is both correct and what keeps the scope and the flag in step. Raising
        // the flag here instead is exactly what produced "work owed" over an empty scope.
        if (affected.Count == 0)
        {
            Log.Debug($"Casework automation: {cut.Count} cut(s) touched no placed room, so no " +
                      "finish area changed and no pass is owed.");
            return;
        }

        Flags.For(doc).FinishDirty = true;

        var pending = Pending.For(doc);
        var dirty = DirtyFinishRooms.For(doc);

        foreach (var id in affected)
        {
            pending.Add(id);
            dirty.Add(id);
        }
    }

    /// <summary>
    /// Drives <see cref="OpeningAutomation"/> with the change updater muted, exactly as the
    /// finish pass is driven. Its writes land on doors and windows, which are in the
    /// updater's trigger filter, so without this the pass re-queues itself indefinitely.
    /// </summary>
    private static void RunOpeningPass(Document doc, string reason, bool force = false)
    {
        if (!force && !OpeningAutomation.IsDirty(doc)) return;

        WithoutSelfTriggering(() => OpeningAutomation.Run(doc, TransactionPrefix, reason, force));
    }

    private static void AdjustQueuedRoomLimits(Document doc)
    {
        if (!_options.AdjustLimitsWhileWorking || !Pending.Has(doc)) return;

        var queued = Pending.For(doc);
        if (queued.Count == 0) return;

        // Resolve now, not when queued: rooms get deleted between the edit and the flush.
        //
        // The ids are this document's own - the queue is keyed by document - so GetElement
        // resolves them against the file that minted them rather than against whatever was
        // active when the debounce expired.
        var rooms = queued
            .Select(id => doc.GetElement(id))
            .OfType<Room>()
            .Where(r => r.Area > 0)
            .ToList();

        Pending.Forget(doc);
        if (rooms.Count == 0) return;

        var adjusted = 0;

        // swallowWarnings, because nobody asked for this pass and nobody is waiting on it.
        // Raising a room's upper limit posts Revit warnings on a model with unenclosed or
        // overlapping rooms, and the default handler shows each one in a modal dialog - from
        // inside an ExternalEvent callback the user did not trigger. That is the "unattended,
        // a long run blocks forever" case Transactions.Run's own documentation names.
        WithoutSelfTriggering(() =>
            Transactions.Run(doc, TransactionPrefix + "adjust room upper limits", () =>
                adjusted = RoomLimitAdjuster.Adjust(doc, rooms), swallowWarnings: true));

        if (adjusted > 0)
            Log.Info($"Finish automation: raised the upper limit on {adjusted} room(s).");

        ReportCapsNotRoomBounding(doc, rooms);
    }

    // ---------------------------------------------------------------- while working

    /// <summary>
    /// A backstop for the case where a schedule is opened after changes arrived through a
    /// path DocumentChanged did not see — a reload, a link update, someone else's sync.
    ///
    /// It only raises the external event; it does not write. Modifying a document from
    /// inside view activation is asking for trouble, because the view is still being
    /// built. Revit calls the handler back a moment later, where writing is legal.
    /// </summary>
    private static void OnViewActivated(object? sender, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e)
    {
        if (!_options.Enabled || !_options.RecalculateWhenScheduleOpened) return;
        if (e.CurrentActiveView is not ViewSchedule) return;

        var doc = e.Document;
        if (doc is null || doc.IsFamilyDocument || doc.IsReadOnly) return;

        // Asked of the document whose schedule was just opened, and asked AFTER that document
        // is in hand - the dirty flag belongs to a document now, not to the session.
        if (!Flags.For(doc).FinishDirty) return;

        Log.Debug("Schedule opened with work outstanding; requesting a pass.");
        _scheduleOpenPending = true;
        _syncEvent?.Raise();
    }

    /// <summary>Forces a pass now. Exposed for the ribbon button.</summary>
    public static void RunNow(Document doc)
    {
        RunCaseworkPass(doc, "requested from the ribbon", force: true);
        RunOpeningPass(doc, "requested from the ribbon", force: true);
        RunFullPass(doc, "requested from the ribbon", force: true);
    }

    /// <summary>
    /// The upper-limit fix cannot compensate for a ceiling that is not room-bounding —
    /// nothing clips the room, so it fills the whole raised envelope and every derived
    /// area is measured against the wrong surface. Logged rather than shown, because a
    /// dialog interrupting an edit is worse than the problem it reports.
    /// </summary>
    private static void ReportCapsNotRoomBounding(Document doc, IReadOnlyCollection<Room> rooms)
    {
        var offenders = RoomLimitAdjuster.CapsNotRoomBounding(doc, rooms);
        if (offenders.Count == 0) return;

        Log.Warn($"{offenders.Count} ceiling/roof element(s) above recently changed rooms are NOT " +
                 "room bounding, so those rooms are not being clipped by them and their finish " +
                 "areas will be wrong. Ids: " + string.Join(", ", offenders.Take(20)));
    }

    // ---------------------------------------------------------------- on save

    /// <summary>
    /// Queues the opening resolvers for a document that has just been opened.
    ///
    /// QUEUED, NOT RUN. DocumentOpened is a read-only context like DocumentChanged, so the
    /// work goes through the same ExternalEvent that every other trigger uses and lands at
    /// the next moment Revit says it is safe to write.
    ///
    /// WHAT IT REFUSES TO TOUCH, because "run on every file anyone opens" is a big promise:
    ///   * family documents - no rooms, no doors to resolve, nothing to do
    ///   * linked documents - somebody else's model, opened as a reference, and writing to it
    ///     would be a change nobody asked for in a file they may not even own
    ///   * read-only documents - the write would fail anyway, noisily
    /// </summary>
    private static void OnDocumentOpened(object? sender, DocumentOpenedEventArgs e)
    {
        try
        {
            if (!_options.Enabled) return;

            var doc = e.Document;

            if (doc is null || doc.IsFamilyDocument || doc.IsLinked || doc.IsReadOnly)
            {
                Log.Debug("Document opened: not a writable project document; opening pass skipped.");
                return;
            }

            Flags.For(doc).OpenedPending = true;
            _syncEvent?.Raise();

            Log.Debug($"Document opened: '{doc.Title}' queued for the opening resolvers.");
        }
        catch (Exception ex)
        {
            // An exception here reaches Revit's own handler during file open, which is the
            // worst possible moment to show somebody a crash dialog.
            Log.Error("Document opened handler failed; the opening pass was not queued.", ex);
        }
    }

    /// <summary>
    /// Drops every queue and flag belonging to a document that is closing.
    ///
    /// Without this the per-document maps grow for the life of the Revit session - one entry
    /// per file anyone opens - and, worse, a document reopened from the same path would inherit
    /// the previous session's ElementIds, which Revit is free to have reassigned in between.
    /// </summary>
    private static void OnDocumentClosing(object? sender, DocumentClosingEventArgs e)
    {
        try
        {
            var doc = e.Document;
            if (doc is null) return;

            Pending.Forget(doc);
            DirtyFinishRooms.Forget(doc);
            Flags.Forget(doc);

            // The two automations this class drives bank their own per-document queues, and
            // they have no event subscription of their own to hang this off - so the class
            // that owns their lifecycle releases them here too.
            OpeningAutomation.Forget(doc);
            CaseworkCutAutomation.Forget(doc);
        }
        catch (Exception ex)
        {
            Log.Warn($"Finish automation could not release state for a closing document: {ex.Message}");
        }
    }

    private static void OnDocumentSaving(object? sender, DocumentSavingEventArgs e) =>
        Recalculate(e.Document);

    private static void OnDocumentSavingAs(object? sender, DocumentSavingAsEventArgs e) =>
        Recalculate(e.Document);

    /// <summary>
    /// Brings the finish areas up to date, inside the save that is already happening, so
    /// the file written to disk contains correct numbers.
    ///
    /// KNOWN LIMITATION — this recalculates EVERY room, not just the stale ones. The
    /// engine's only entry point is a whole-model pass; there is no per-room mode yet.
    /// The staleness flags are therefore used as a yes/no signal ("did anything change?")
    /// rather than as a work list. Giving RoomFinishCalculator a room-subset overload is
    /// what turns this from "recalculate everything on save" into something fast enough
    /// to run while the user is still working.
    /// </summary>
    private static void Recalculate(Document doc)
    {
        if (!_options.Enabled || !_options.RecalculateOnSave) return;

        // Save is the guarantee point: the file on disk is what gets synced, issued and
        // scheduled from, so all three passes run here even if every earlier trigger was
        // missed.
        //
        // The casework pass is FORCED here and nowhere else. It is the only trigger that
        // catches the case where a WALL moved into or out of a fitting's void rather than
        // the fitting moving — wall edits are too frequent to react to one at a time, so
        // they are banked and settled at the one moment a pause is already expected.
        RunCaseworkPass(doc, "the model was saved", force: true);
        RunOpeningPass(doc, "the model was saved");
        RunFullPass(doc, "the model was saved");
    }

    // ---------------------------------------------------------------- scoping a pass

    /// <summary>How far beyond a room's bounding box to look for a neighbour, in feet. Matches
    /// FinishStaleUpdater.RoomSearchMarginFeet on purpose: that is the margin already used to
    /// mark BOTH rooms bounding a changed wall stale, so a room found dirty by that search is
    /// found as this room's neighbour by the same search here.</summary>
    private const double NeighbourSearchMarginFeet = 2.0;

    /// <summary>
    /// The rooms a non-forced pass should measure: everything marked dirty this session
    /// (<see cref="DirtyFinishRooms"/>), plus anything still flagged stale on disk - which
    /// covers staleness that survived a Revit restart, when the in-memory set is empty but the
    /// model still owes a recalculation - expanded to each dirty room's immediate neighbours.
    ///
    /// THE NEIGHBOUR EXPANSION IS NOT OPTIONAL. RoomFinishCalculator sums each element's finish
    /// area from every room it visits IN ONE PASS and OVERWRITES the element's parameter with
    /// that sum - it has no notion of "add my contribution to what is already there". A wall
    /// between a dirty room and a quiet neighbour that this pass never re-measures would have
    /// its total silently collapse to the dirty side's contribution alone, discarding the quiet
    /// room's share the moment this pass writes. Pulling in neighbours by bounding box - the
    /// same search FinishStaleUpdater already uses to mark both sides of a changed wall stale -
    /// keeps every element's contributing rooms together in the one pass that writes it.
    ///
    /// Deliberately approximate, like that search: a neighbour pulled in unnecessarily costs
    /// one extra room measured; a shared element whose other side is missed corrupts that
    /// element's total silently. The trade is not close.
    /// </summary>
    private static HashSet<ElementId> ScopeForPass(Document doc)
    {
        var banked = DirtyFinishRooms.For(doc);

        // PRUNED AGAINST THE MODEL FIRST, both to keep the scope valid and to stop the banked
        // set growing without limit.
        //
        // A room deleted between the edit that dirtied it and the pass that would measure it
        // leaves an id that resolves to nothing. Handing that to the collector's id-set
        // constructor is asking Revit to filter on an element that is not there, and leaving it
        // banked would keep IsDirty true forever - a save-time pass on every save, for a room
        // that no longer exists.
        banked.RemoveWhere(id => doc.GetElement(id) is not Room);

        var seed = new HashSet<ElementId>(banked);
        foreach (var room in StaleRooms(doc)) seed.Add(room.Id);

        var expanded = new HashSet<ElementId>(seed);

        foreach (var id in seed)
        {
            if (doc.GetElement(id) is not Room room) continue;

            var box = room.get_BoundingBox(null);
            if (box is null) continue;

            var outline = new Outline(
                new XYZ(box.Min.X - NeighbourSearchMarginFeet,
                        box.Min.Y - NeighbourSearchMarginFeet,
                        box.Min.Z - NeighbourSearchMarginFeet),
                new XYZ(box.Max.X + NeighbourSearchMarginFeet,
                        box.Max.Y + NeighbourSearchMarginFeet,
                        box.Max.Z + NeighbourSearchMarginFeet));

            foreach (var near in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Rooms)
                         .WhereElementIsNotElementType()
                         .WherePasses(new BoundingBoxIntersectsFilter(outline))
                         .ToElementIds())
            {
                expanded.Add(near);
            }
        }

        return expanded;
    }

    /// <summary>
    /// The single place the finish engine is driven from. Save, schedule-opened and idle
    /// all funnel through here so they cannot drift into three slightly different
    /// behaviours.
    /// </summary>
    /// <param name="force">
    /// Ignore the dirty check and run regardless. Used by the ribbon button, so it always
    /// does something visible rather than appearing broken when the flags disagree.
    /// </param>
    private static void RunFullPass(Document doc, string reason, bool force = false)
    {
        if (doc.IsFamilyDocument || doc.IsReadOnly) return;

        try
        {
            // Skipping when nothing has changed is what keeps opening a schedule instant
            // in the common case. Note this asks IsDirty, NOT the optional parameter.
            if (!force && !IsDirty(doc))
            {
                Flags.For(doc).FinishDirty = false;
                return;
            }

            var watch = Stopwatch.StartNew();
            var settings = new FinishSettings();

            // Bind anything missing first, in its own transaction. This is what makes a brand
            // new project work without anyone knowing a setup step exists: the first
            // automatic pass creates the parameters it needs and then fills them in.
            //
            // Suppressed like every other write here - binding a parameter to Rooms is a
            // change to rooms, and without this the updater would re-flag every room the
            // moment they were bound and schedule another pass.
            WithoutSelfTriggering(() =>
                FinishParameterSetup.EnsureBound(doc, settings, "the automatic pass ran"));

            var calculator = new RoomFinishCalculator(doc, settings);

            // FORCE MEANS THE WHOLE MODEL, ALWAYS. The ribbon button's job is to do something
            // visible even when the dirty tracking might be wrong, so it is the one caller
            // that gets no scoping - every other trigger (save, schedule-opened) gets exactly
            // the rooms that need it. See ScopeForPass for why that scope is safe to share
            // elements between rooms it does not include.
            var scope = force ? null : ScopeForPass(doc);

            // AN EMPTY SCOPE IS "NOTHING TO MEASURE", NOT "MEASURE NOTHING".
            //
            // FilteredElementCollector's ICollection<ElementId> constructor throws
            // ArgumentException on an empty set - that is its documented contract, not an edge
            // case Revit tolerates - so handing this straight to the engine turned "no rooms
            // need re-measuring" into an exception. The catch below then left the dirty flag
            // set on purpose, so every subsequent flush and every save re-entered the same
            // throw: the finish areas silently stopped updating, with a log line as the only
            // evidence.
            //
            // RunCaseworkPass now names the rooms round the walls it cuts, which is the cause
            // this used to trip over. This stays as the backstop for any future path that sets
            // the flag without naming rooms - and it clears the flag, because a pass that has
            // genuinely nothing to measure IS up to date.
            if (scope is { Count: 0 })
            {
                Flags.For(doc).FinishDirty = false;
                Log.Debug($"Finish automation: work was flagged ({reason}) but no room needs " +
                          "re-measuring; nothing to do.");
                return;
            }

            IReadOnlyCollection<long> processedRoomIds = [];

            // swallowWarnings: the engine raises room upper limits and regenerates, and this
            // runs unattended from a save, an idle tick or a schedule being opened. A modal
            // warning dialog raised from inside DocumentSaving is a save that appears to hang.
            WithoutSelfTriggering(() =>
                Transactions.Run(doc, TransactionPrefix + "update finish areas", () =>
                {
                    // The engine raises room upper limits too, so this write is just as
                    // capable of re-triggering the updater as the idle adjustment is.
                    var result = calculator.Run(scope);
                    processedRoomIds = result.ProcessedRoomIds;
                    ClearStale(doc, processedRoomIds);
                }, swallowWarnings: true));

            // ONLY THE ROOMS THIS PASS ACTUALLY MEASURED stop being tracked as dirty.
            //
            // This used to clear the whole set, which contradicted its own comment. The comment
            // was right about the intent and wrong about the safety net: it leaned on the
            // 'Finish Area Stale' parameter still holding the skipped rooms, and that parameter
            // is OPTIONAL - MarkStale is built around models that do not have it, and the
            // command banner says so in as many words. In such a model, a room the engine
            // skipped (checked out by another user, unplaced, geometry not computable) was
            // dropped from the only record that existed and was never retried. The engine's own
            // report tells the user to "re-run after those users synchronise"; the automation
            // had by then forgotten which rooms.
            var dirty = DirtyFinishRooms.For(doc);
            foreach (var idValue in processedRoomIds) dirty.Remove(new ElementId(idValue));

            // THE FLAG IS CLEARED EVEN WHEN ROOMS REMAIN OWED, and the two are deliberately
            // not the same thing.
            //
            // FinishDirty means "an idle tick should wake up and run a pass". A room this pass
            // could not reach - locked by a colleague for the rest of the day - would keep that
            // true forever and turn every idle tick into a full measurement pass. So the flag
            // is cleared, and the OWED ROOMS STAY BANKED in DirtyFinishRooms instead.
            //
            // IsDirty reads that banked set, so the rooms are still retried at the next SAVE -
            // the point where a pause is already expected and where the guarantee about the
            // file on disk actually matters - and by any later edit that re-dirties them. What
            // they no longer do is spin the idle path.
            Flags.For(doc).FinishDirty = false;

            var scopeNote = scope is null
                ? "whole model"
                : $"{processedRoomIds.Count} of {scope.Count} scoped room(s)";
            Log.Info($"Finish automation: full pass ({scopeNote}) because {reason}. " +
                     $"Took {watch.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            // Whatever triggered this — a save, a view change — must survive the failure.
            // Losing a save because a finish area could not be measured is never the right
            // trade. The flag stays set so the next trigger tries again.
            Log.Error($"Finish automation: full pass failed ({reason}); the model was left alone.", ex);
        }
    }
}
