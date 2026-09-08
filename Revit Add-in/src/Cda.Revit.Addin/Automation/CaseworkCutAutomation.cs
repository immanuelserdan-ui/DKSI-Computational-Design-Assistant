using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Cda.Revit.Addin.Casework;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Automation;

/// <summary>
/// Runs the casework side- and bottom-void cutter off the same DocumentChanged →
/// ExternalEvent pipeline that keeps finish areas current, so placing a fitting cuts the
/// walls and floors its voids reach without anybody reaching for Modify → Cut → Cut
/// Geometry.
///
/// TWO TRIGGERS, DELIBERATELY DIFFERENT, because the two things that create a missing cut
/// are not equally cheap to react to.
///
///   A FITTING IS PLACED OR MOVED. Reacted to immediately, scoped to that instance. This is
///   the case the tool exists for and the one where an instant answer is worth having: the
///   cut appears while the user is still looking at what they just placed.
///
///   A WALL OR FLOOR IS MOVED INTO OR OUT OF A FITTING'S VOID. Deferred to the next save, or
///   to the same idle-quiet moment <see cref="Finishes.FinishAutomation"/>'s own full pass
///   already waits for - see the <c>allowSweep</c> parameter on <see cref="Run"/>. Walls and
///   floors are edited constantly, an edit to one tells us nothing about WHICH fitting is now
///   affected without a spatial query per changed element, and running a whole-model sweep on
///   every nudge would put a pause in the middle of ordinary drafting. Save is where this
///   add-in already puts work that must be right but need not be instant, for the same
///   reason: the file on disk is what gets synced, issued and scheduled from.
///
/// WHAT IT NEVER DOES IS REMOVE A CUT. Revit drops an instance's cuts when the instance is
/// deleted, and a cut whose void no longer reaches its target is harmless — the wall or
/// floor simply comes back whole. Removing cuts automatically would mean deciding that a cut
/// somebody made by hand, for a reason not visible in the geometry, was a mistake. Adding is
/// recoverable with one Ctrl+Z; removing somebody else's deliberate work is not.
///
/// Put <c>#nocut-auto</c> in an instance's Comments to have it skipped entirely.
/// </summary>
internal static class CaseworkCutAutomation
{
    // ---------------------------------------------------------------- settings

    private sealed class Options
    {
        /// <summary>
        /// Cut walls automatically when a fitting is placed or moved.
        ///
        /// On by default. The pass only ever ADDS cuts Revit itself has confirmed are real
        /// (see <see cref="CaseworkVoidCutter"/> on why the attempt is the test), so a pass
        /// that fires more often than needed costs time, never correctness.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Sweep the whole model on save, catching fittings whose walls moved rather than
        /// the other way round. Off means side voids are only ever chased when the FITTING
        /// is touched, which misses the wall-moved-into-a-void case entirely.
        /// </summary>
        public bool FullSweepOnSave { get; set; } = true;

        /// <summary>
        /// Treat every casework instance as a candidate rather than only the ones named
        /// "Fitting". Mirrors <see cref="CaseworkSettings.AllCasework"/> so widening the net
        /// is a settings edit rather than a rebuild.
        /// </summary>
        public bool AllCasework { get; set; }

        /// <summary>Mirror of <see cref="CaseworkSettings.ReachMm"/>.</summary>
        public double ReachMm { get; set; } = 300;
    }

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cda", "RevitAddin", "casework-cut-automation.json");

    private static Options _options = Load();

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
            Log.Warn($"Casework cut settings unreadable, using defaults: {ex.Message}");
            return new Options();
        }
    }

    public static bool Enabled => _options.Enabled;

    public static string SettingsFile => SettingsPath;

    public static CaseworkSettings CurrentSettings => new()
    {
        AllCasework = _options.AllCasework,
        ReachMm = _options.ReachMm,
    };

    // ---------------------------------------------------------------- dirty tracking

    /// <summary>
    /// Fittings added or changed since the last pass. A work LIST rather than a flag, because
    /// the immediate pass is scoped to exactly these — a cut is a property of one instance and
    /// one wall, so a scoped pass gives the same answer as a whole-model one at a fraction of
    /// the cost.
    /// </summary>
    private static readonly DocumentScoped<HashSet<ElementId>> PendingFittings = new();

    /// <summary>
    /// Something that can be CUT changed, or something was deleted and can no longer be
    /// classified. Neither says which fitting is affected, so both defer to the save sweep.
    ///
    /// PER DOCUMENT, like the fitting queue beside it. As a plain static this said "a sweep is
    /// owed somewhere", and the flush that acted on it swept whichever document happened to be
    /// active - clearing the flag, so the document that actually owed the sweep never got one.
    /// </summary>
    private static readonly DocumentScoped<DocumentFlags> Flags = new();

    /// <summary>Work the ExternalEvent should pick up now. Wall and floor edits deliberately do not qualify.</summary>
    public static bool IsDirty(Document doc) =>
        PendingFittings.Has(doc) && PendingFittings.For(doc).Count > 0;

    /// <summary>
    /// A wall or floor edit (or a deletion) is owed a whole-model sweep in THIS document and
    /// none has run yet. Exposed so <see cref="Finishes.FinishAutomation"/> can decide whether
    /// THIS flush is allowed to pay for that sweep - see <see cref="Run"/>'s <c>allowSweep</c>
    /// parameter.
    /// </summary>
    public static bool SweepOwed(Document doc) => Flags.For(doc).CaseworkSweepOwed;

    /// <summary>Releases a closing document's queue so its ids cannot outlive it.</summary>
    public static void Forget(Document? doc)
    {
        PendingFittings.Forget(doc);
        Flags.Forget(doc);
    }

    public static void MarkDirty(Document doc, IEnumerable<ElementId> touched, int deletedCount)
    {
        if (!_options.Enabled) return;

        var flags = Flags.For(doc);

        // A deletion cannot be classified - the element is gone. It may have been a wall or
        // floor standing in a void, so the sweep is owed; it is never a reason to run right
        // now, because deleting the FITTING already took its cuts with it.
        if (deletedCount > 0) flags.CaseworkSweepOwed = true;

        var pending = PendingFittings.For(doc);

        foreach (var id in touched)
        {
            var category = doc.GetElement(id)?.Category?.Id.Value;
            if (category is null) continue;

            if (category == (long)BuiltInCategory.OST_Casework)
            {
                pending.Add(id);
                continue;
            }

            if (category == (long)BuiltInCategory.OST_Walls ||
                category == (long)BuiltInCategory.OST_Floors) flags.CaseworkSweepOwed = true;
        }
    }

    // ---------------------------------------------------------------- the pass

    /// <summary>
    /// Cuts what is owed and returns THE ELEMENTS THAT WERE CUT, so the caller can decide
    /// whether the finish areas it is about to publish are now stale, and - crucially - WHICH
    /// rooms that staleness belongs to. They are stale: taking a bite out of a wall or floor
    /// changes the painted or floor-finish area of the room it faces.
    ///
    /// IT USED TO RETURN A COUNT, and the count was not enough. FinishAutomation turned a
    /// non-zero count into a bare "finish work is owed" flag with no rooms attached, while the
    /// pass that acts on that flag takes its work list from the dirty-room set - so a cut made
    /// where no Room object has been drawn yet produced "work owed" over an empty scope, and
    /// the scoped collector Revit builds from an empty id set throws by contract. Handing back
    /// the cut elements lets the caller name the rooms round them, which is the information
    /// that was missing rather than a convenience.
    ///
    /// MUST be called from inside <c>FinishAutomation.WithoutSelfTriggering</c>. Every write
    /// here lands on a wall or floor, and both are in the change updater's own trigger
    /// filter, so an unsuppressed pass re-dirties exactly what it just cleaned.
    /// </summary>
    /// <param name="force">
    /// Sweep the whole model regardless of what is queued. Used on save and by the ribbon
    /// button - the two places where a complete answer is worth the wait.
    /// </param>
    /// <param name="allowSweep">
    /// May this call pay for the whole-model sweep <see cref="_sweepOwed"/> is tracking?
    ///
    /// SPLIT OUT FROM <c>force</c> DELIBERATELY, to fix a real bug this class's own doc
    /// comment did not anticipate: <c>force</c> already means "sweep unconditionally", but
    /// before this parameter existed a plain reactive call - "a fitting was placed, react
    /// NOW" - would ALSO sweep the whole model whenever <see cref="_sweepOwed"/> happened to
    /// already be true from an earlier, unrelated wall or floor edit. That silently broke the
    /// class's own documented contract ("A WALL OR FLOOR IS MOVED INTO OR OUT OF A FITTING'S
    /// VOID. Deferred to the next save.") every time a wall nudge and a fitting nudge landed
    /// in the same working session, which in practice is most sessions - turning what was
    /// supposed to be an instant, cheap, single-fitting cut into a full CaseworkVoidCutter
    /// pass over every casework instance in the model, on a call site that had no reason to
    /// expect one.
    ///
    /// Callers pass <c>false</c> for the ordinary "a fitting just moved, cut it now" reaction
    /// (see <see cref="Finishes.FinishAutomation.FlushPendingWork"/>), which keeps that path
    /// scoped and instant exactly as documented, and <c>_sweepOwed</c> untouched - it simply
    /// waits for the next call that IS allowed to spend the time. <c>true</c> (the default,
    /// so every other existing caller is unaffected) is used once the model has actually gone
    /// idle, and always by <c>force</c> callers - save and the ribbon button - since those
    /// already ignore this flag entirely.
    /// </param>
    public static IReadOnlyList<ElementId> Run(
        Document doc, string transactionPrefix, string reason, bool force = false,
        bool allowSweep = true)
    {
        if (!_options.Enabled) return [];
        if (doc.IsFamilyDocument || doc.IsReadOnly) return [];

        var flags = Flags.For(doc);

        var sweep = force || (allowSweep && flags.CaseworkSweepOwed);

        var queued = PendingFittings.Has(doc) ? PendingFittings.For(doc) : null;
        if (!sweep && (queued is null || queued.Count == 0)) return [];

        // Resolved before the work, not after. A failure part-way must not leave the queue
        // set and have every subsequent trigger retry the same failing pass; the next real
        // edit queues it again, which is the right moment to try once more.
        //
        // The ids come from THIS document's own queue, so GetElement resolves them against the
        // file that minted them rather than against whatever was active when the flush ran.
        List<Element> scope = sweep || queued is null
            ? []
            : queued
                .Select(doc.GetElement)
                .OfType<Element>()
                .ToList();

        PendingFittings.Forget(doc);

        // ONLY CLEARED WHEN THE SWEEP ACTUALLY RAN. A call that was not allowed to sweep
        // (allowSweep: false) must leave the flag exactly as it found it, or the wall/floor
        // edit that set it is silently forgotten - never swept at all, by anyone, ever -
        // instead of merely deferred to the next call that can afford it.
        if (sweep) flags.CaseworkSweepOwed = false;

        if (!sweep && scope.Count == 0) return [];

        try
        {
            var watch = Stopwatch.StartNew();
            CaseworkCutResult? result = null;

            // swallowWarnings: this runs unattended - from an idle tick, and from inside
            // DocumentSaving. InstanceVoidCutUtils.AddInstanceVoidCut posts Revit warnings
            // routinely, and the default handler shows each one in a modal dialog nobody is
            // sitting in front of. Transactions.Run's own documentation calls this out as the
            // difference between a batch pass that finishes and one that blocks forever;
            // errors still surface and still roll the transaction back.
            Transactions.Run(doc, transactionPrefix + "cut walls and floors with casework voids",
                () => result = new CaseworkVoidCutter(doc, CurrentSettings).Run(scope),
                swallowWarnings: true);

            var cuts = result!.CutsAdded;

            // Logged at Info only when it did something. A scoped pass runs on every fitting
            // the user nudges, and a line per nudge saying "0 cuts" is how a log stops being
            // read at all.
            if (cuts > 0)
            {
                Log.Info($"Casework automation: {cuts} cut(s) created (walls/floors) because {reason}. " +
                         $"{result.FittingsExamined} fitting(s) examined in {watch.ElapsedMilliseconds} ms " +
                         $"({result.AlreadyCut} already cut, {result.NoIntersection} out of reach).");
            }
            else
            {
                Log.Debug($"Casework automation: nothing to cut ({reason}). " +
                          $"{result.FittingsExamined} fitting(s) in {watch.ElapsedMilliseconds} ms.");
            }

            foreach (var warning in result.Warnings.Take(20))
                Log.Warn("Casework cut: " + warning);

            return result.CutElementIds;
        }
        catch (Exception ex)
        {
            // Same contract as the finish and opening passes: whatever triggered this - a
            // save, a placement - survives. Losing a save because a wall could not be cut is
            // never the right trade.
            Log.Error($"Casework automation: pass failed ({reason}); walls were left alone.", ex);
            return [];
        }
    }
}
