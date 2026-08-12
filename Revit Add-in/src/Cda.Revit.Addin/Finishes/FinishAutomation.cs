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
/// transaction and does nothing but flip a Yes/No parameter. Recalculation happens on
/// save, because that is the only point where a delay is both expected and useful: it
/// guarantees the model on disk — the one that gets synced, issued or scheduled from —
/// has correct areas in it.
///
/// The alternative, recalculating whenever Revit goes idle, was rejected. A whole-model
/// finish pass takes seconds to minutes, so an idle trigger produces random freezes with
/// no explanation and no cancel. It only becomes viable once the engine can recalculate
/// a single room instead of all of them; see the note on <see cref="Recalculate"/>.
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
        /// <see cref="IdleQuietSeconds"/>. OFF by default and deliberately so: a full
        /// finish pass is seconds to minutes on Revit's UI thread, which reads as the
        /// application hanging for no reason the user can see. Only worth enabling on
        /// small models where a pass is genuinely quick.
        /// </summary>
        /// <summary>
        /// Defaults ON. It is the only trigger that works when the schedule sits in a
        /// window beside the drawing and never gets clicked into — which is how these
        /// schedules are actually used. Turn it off if a pass becomes slow enough to
        /// notice; the log line after each pass reports the milliseconds.
        /// </summary>
        public bool RecalculateWhileIdle { get; set; } = true;

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
    /// Set whenever a room is marked, cleared after a recalculation. A cheap bool so an
    /// idle tick with nothing to do never runs a collector query.
    /// </summary>
    private static bool _dirtySinceRecalculation;

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
    /// Rooms touched since the last idle sweep. Held in memory rather than read back
    /// from the model each tick, so an idle tick with nothing to do costs one
    /// comparison.
    /// </summary>
    private static readonly HashSet<ElementId> Pending = [];

    private static DateTime _lastChangeUtc = DateTime.MinValue;

    /// <summary>
    /// How long the model must sit still before limits are adjusted. Long enough that a
    /// drag, a nudge and a second nudge are one adjustment rather than three; short
    /// enough that the correction lands before the user looks at a schedule.
    /// </summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

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

            // The trigger that closes the loop for schedules. Opening a schedule is the
            // moment its numbers start mattering, and the only moment a delay is already
            // expected — the view is drawing anyway.
            application.ViewActivated += OnViewActivated;

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
            application.ViewActivated -= OnViewActivated;

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
        Pending.Add(room.Id);
        _lastChangeUtc = DateTime.UtcNow;
        _dirtySinceRecalculation = true;

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
    public static bool IsDirty(Document doc) =>
        _dirtySinceRecalculation || StaleRooms(doc).Count > 0;

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

    private static void ClearStale(Document doc)
    {
        foreach (var room in StaleRooms(doc))
        {
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
                if (OpeningAutomation.IsDirty || CaseworkCutAutomation.IsDirty) _syncEvent?.Raise();
                return;
            }

            foreach (var id in rooms) Pending.Add(id);
            _lastChangeUtc = DateTime.UtcNow;
            _dirtySinceRecalculation = true;

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
        AdjustQueuedRoomLimits(doc);

        // FIRST OF THE THREE. Cutting a wall changes its geometry, so anything measured
        // before the cut is measured against a wall that no longer exists in that shape.
        RunCaseworkPass(doc, "the model changed");

        // BEFORE the finish pass, not after. Lining resolution changes "Lining YN" and the
        // window material, both of which feed what the finish engine counts as painted. Run
        // it second and every pass would publish areas computed from the previous state.
        RunOpeningPass(doc, "the model changed");

        if (_dirtySinceRecalculation)
            RunFullPass(doc, "the model changed");
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
    private static void RunCaseworkPass(Document doc, string reason, bool force = false)
    {
        if (!force && !CaseworkCutAutomation.IsDirty) return;

        var cuts = 0;
        WithoutSelfTriggering(() =>
            cuts = CaseworkCutAutomation.Run(doc, TransactionPrefix, reason, force));

        if (cuts > 0) _dirtySinceRecalculation = true;
    }

    /// <summary>
    /// Drives <see cref="OpeningAutomation"/> with the change updater muted, exactly as the
    /// finish pass is driven. Its writes land on doors and windows, which are in the
    /// updater's trigger filter, so without this the pass re-queues itself indefinitely.
    /// </summary>
    private static void RunOpeningPass(Document doc, string reason, bool force = false)
    {
        if (!force && !OpeningAutomation.IsDirty) return;

        WithoutSelfTriggering(() => OpeningAutomation.Run(doc, TransactionPrefix, reason, force));
    }

    private static void AdjustQueuedRoomLimits(Document doc)
    {
        if (!_options.AdjustLimitsWhileWorking || Pending.Count == 0) return;

        // Resolve now, not when queued: rooms get deleted between the edit and the flush.
        var rooms = Pending
            .Select(id => doc.GetElement(id))
            .OfType<Room>()
            .Where(r => r.Area > 0)
            .ToList();

        Pending.Clear();
        if (rooms.Count == 0) return;

        var adjusted = 0;
        WithoutSelfTriggering(() =>
            Transactions.Run(doc, TransactionPrefix + "adjust room upper limits", () =>
                adjusted = RoomLimitAdjuster.Adjust(doc, rooms)));

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
        if (!_dirtySinceRecalculation) return;
        if (e.CurrentActiveView is not ViewSchedule) return;

        var doc = e.Document;
        if (doc is null || doc.IsFamilyDocument || doc.IsReadOnly) return;

        Log.Debug("Schedule opened with work outstanding; requesting a pass.");
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
                _dirtySinceRecalculation = false;
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

            WithoutSelfTriggering(() =>
                Transactions.Run(doc, TransactionPrefix + "update finish areas", () =>
                {
                    // The engine raises room upper limits too, so this write is just as
                    // capable of re-triggering the updater as the idle adjustment is.
                    calculator.Run();
                    ClearStale(doc);
                }));

            _dirtySinceRecalculation = false;

            Log.Info($"Finish automation: full pass because {reason}. " +
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
