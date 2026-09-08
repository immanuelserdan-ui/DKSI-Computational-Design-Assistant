using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Cda.Revit.Addin.Casework;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Automation;

/// <summary>
/// Runs the casework side-void cutter off the same DocumentChanged → ExternalEvent pipeline
/// that keeps finish areas current, so placing a fitting cuts the walls its voids reach
/// without anybody reaching for Modify → Cut → Cut Geometry.
///
/// TWO TRIGGERS, DELIBERATELY DIFFERENT, because the two things that create a missing cut
/// are not equally cheap to react to.
///
///   A FITTING IS PLACED OR MOVED. Reacted to immediately, scoped to that instance. This is
///   the case the tool exists for and the one where an instant answer is worth having: the
///   cut appears while the user is still looking at what they just placed.
///
///   A WALL IS MOVED INTO OR OUT OF A FITTING'S VOID. Deferred to the next save. Walls are
///   edited constantly, a wall edit tells us nothing about WHICH fitting is now affected
///   without a spatial query per changed wall, and running a whole-model sweep on every wall
///   nudge would put a pause in the middle of ordinary drafting. Save is where this add-in
///   already puts work that must be right but need not be instant, for the same reason: the
///   file on disk is what gets synced, issued and scheduled from.
///
/// WHAT IT NEVER DOES IS REMOVE A CUT. Revit drops an instance's cuts when the instance is
/// deleted, and a cut whose void no longer reaches its wall is harmless — the wall simply
/// comes back whole. Removing cuts automatically would mean deciding that a cut somebody made
/// by hand, for a reason not visible in the geometry, was a mistake. Adding is recoverable
/// with one Ctrl+Z; removing somebody else's deliberate work is not.
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
    private static readonly HashSet<ElementId> PendingFittings = [];

    /// <summary>
    /// Something that can be CUT changed, or something was deleted and can no longer be
    /// classified. Neither says which fitting is affected, so both defer to the save sweep.
    /// </summary>
    private static bool _sweepOwed;

    /// <summary>Work the ExternalEvent should pick up now. Wall edits deliberately do not qualify.</summary>
    public static bool IsDirty => PendingFittings.Count > 0;

    public static void MarkDirty(Document doc, IEnumerable<ElementId> touched, int deletedCount)
    {
        if (!_options.Enabled) return;

        // A deletion cannot be classified - the element is gone. It may have been a wall
        // standing in a void, so the sweep is owed; it is never a reason to run right now,
        // because deleting the FITTING already took its cuts with it.
        if (deletedCount > 0) _sweepOwed = true;

        foreach (var id in touched)
        {
            var category = doc.GetElement(id)?.Category?.Id.Value;
            if (category is null) continue;

            if (category == (long)BuiltInCategory.OST_Casework)
            {
                PendingFittings.Add(id);
                continue;
            }

            if (category == (long)BuiltInCategory.OST_Walls) _sweepOwed = true;
        }
    }

    // ---------------------------------------------------------------- the pass

    /// <summary>
    /// Cuts what is owed and returns the number of cuts made, so the caller can decide
    /// whether the finish areas it is about to publish are now stale. They are: taking a
    /// bite out of a wall changes the painted area of the room that wall faces.
    ///
    /// MUST be called from inside <c>FinishAutomation.WithoutSelfTriggering</c>. Every write
    /// here lands on a wall, and walls are in the change updater's own trigger filter, so an
    /// unsuppressed pass re-dirties exactly what it just cleaned.
    /// </summary>
    /// <param name="force">
    /// Sweep the whole model regardless of what is queued. Used on save and by the ribbon
    /// button - the two places where a complete answer is worth the wait.
    /// </param>
    public static int Run(Document doc, string transactionPrefix, string reason, bool force = false)
    {
        if (!_options.Enabled) return 0;
        if (doc.IsFamilyDocument || doc.IsReadOnly) return 0;

        var sweep = force || _sweepOwed;
        if (!sweep && PendingFittings.Count == 0) return 0;

        // Resolved before the work, not after. A failure part-way must not leave the queue
        // set and have every subsequent trigger retry the same failing pass; the next real
        // edit queues it again, which is the right moment to try once more.
        List<Element> scope = sweep
            ? []
            : PendingFittings
                .Select(doc.GetElement)
                .OfType<Element>()
                .ToList();

        PendingFittings.Clear();
        _sweepOwed = false;

        if (!sweep && scope.Count == 0) return 0;

        try
        {
            var watch = Stopwatch.StartNew();
            CaseworkCutResult? result = null;

            Transactions.Run(doc, transactionPrefix + "cut walls with casework side voids",
                () => result = new CaseworkVoidCutter(doc, CurrentSettings).Run(scope));

            var cuts = result!.CutsAdded;

            // Logged at Info only when it did something. A scoped pass runs on every fitting
            // the user nudges, and a line per nudge saying "0 cuts" is how a log stops being
            // read at all.
            if (cuts > 0)
            {
                Log.Info($"Casework automation: {cuts} wall cut(s) created because {reason}. " +
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

            return cuts;
        }
        catch (Exception ex)
        {
            // Same contract as the finish and opening passes: whatever triggered this - a
            // save, a placement - survives. Losing a save because a wall could not be cut is
            // never the right trade.
            Log.Error($"Casework automation: pass failed ({reason}); walls were left alone.", ex);
            return 0;
        }
    }
}
