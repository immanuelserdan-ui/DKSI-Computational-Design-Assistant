using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Cda.Revit.Addin.Doors;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Linings;

namespace Cda.Revit.Addin.Automation;

/// <summary>
/// Runs the two door/window resolvers — Udvendig room substitution and lining clash
/// resolution — off the same DocumentChanged → ExternalEvent pipeline that keeps finish
/// areas current, so neither needs a ribbon button any more.
///
/// Both engines were already headless: <c>Run(apply, selection)</c> takes no UI and does
/// a full recompute every time, which is what makes automating them safe. A partial or
/// incremental run is what produced wrong answers in the past, so this never scopes them
/// — it always passes an empty selection and lets them sweep the whole model.
///
/// Both apply. The lining rules are the ones documented in the office's own
/// Resolve-Lining-Clashes README: symmetric uncheck (both sides of a shared reveal drop
/// that face, each banking its own remnant in "Lining Change"), purely geometric blocking
/// (a neighbour blocks whether or not it carries lining itself), and door-to-window
/// propagation of the master "Lining YN" and material code.
///
/// One asymmetry worth knowing, because it is what makes this safe to leave running: a
/// DOOR's own "Lining YN" is never written. It is the modelling decision the whole rule
/// keys off, so editing it is how you drive the automation rather than something the
/// automation fights you over. Everything else — the three side flags, Lining Change, and
/// touching windows' Lining YN and Window Material — is recomputed from geometry on every
/// pass and will overwrite a manual edit. Put <c>#nolining-auto</c> in an element's
/// Comments to have it skipped entirely.
/// </summary>
internal static class OpeningAutomation
{
    // ---------------------------------------------------------------- settings

    private sealed class Options
    {
        /// <summary>
        /// Replace exterior room references on doors automatically.
        ///
        /// On by default. The resolver is a full recompute with no left/right convention to
        /// get wrong: it reads which room is on the other side of the door and writes that.
        /// Re-running it on an unchanged model writes nothing, so a pass that fires more
        /// often than needed costs time, never correctness.
        /// </summary>
        public bool ResolveUdvendig { get; set; } = true;

        /// <summary>
        /// Resolve lining clashes automatically — uncheck the Top/Left/Right face wherever
        /// two openings share a reveal, bank the uncovered remnant in "Lining Change", and
        /// carry a door's master "Lining YN" and material across to the windows it touches.
        ///
        /// This APPLIES. It was briefly held in report-only mode behind a handedness check,
        /// and that gate was a mistake: <see cref="LeftIsFamilyPlusX"/> only decides which of
        /// Left and Right gets unchecked, and the family defines both as Height —
        ///
        ///     Lining Left  = if(Lining Left YN,  Height, 0 mm)
        ///     Lining Right = if(Lining Right YN, Height, 0 mm)
        ///
        /// — so getting it backwards yields an identical "Lining Length" and an identical
        /// schedule quantity. The only difference is which jamb a drawn board would sit on,
        /// and per the family review the remnant is not drawn at all. There was no number to
        /// protect, and holding the whole feature back to protect it cost real accuracy:
        /// every hour spent gated, "Lining Change" stayed stale, and a stale Lining Change is
        /// added unconditionally by the family formula.
        /// </summary>
        public bool ResolveLiningClashes { get; set; } = true;

        /// <summary>
        /// Mirror of <see cref="LiningSettings.LeftIsFamilyPlusX"/>, surfaced here so it is a
        /// settings edit rather than a rebuild if anyone ever wants the board on the other
        /// jamb. Cosmetic — see above for why it cannot move a quantity.
        ///
        /// Secondary to <see cref="LiningSettings.Handedness"/>, which defaults to WallAxis:
        /// "Left" is one fixed direction along the wall for every opening rather than
        /// following each instance's hand flip. That was calibrated against wall 28377592,
        /// where incidental hand flips made the edge meeting a door come out as "Right" on
        /// one side of it and "Left" on the other.
        /// </summary>
        public int LeftIsFamilyPlusX { get; set; } = 1;
    }

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cda", "RevitAddin", "opening-automation.json");

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
            Log.Warn($"Opening automation settings unreadable, using defaults: {ex.Message}");
            return new Options();
        }
    }

    public static bool UdvendigEnabled => _options.ResolveUdvendig;

    public static bool LiningEnabled => _options.ResolveLiningClashes;

    public static string SettingsFile => SettingsPath;

    // ---------------------------------------------------------------- dirty tracking

    /// <summary>
    /// Set when a door or window is added, changed or deleted. Separate from the finish
    /// engine's flag on purpose: moving a wall makes finish areas stale without changing
    /// any opening, and re-running a whole-model door sweep for that is wasted time.
    /// </summary>
    private static bool _dirty;

    public static bool IsDirty => _dirty;

    /// <summary>
    /// True when any of the given ids is a door or window — including deleted ones, which
    /// is why this takes the categories from the change event rather than resolving each
    /// id against the document. A deleted element cannot be looked up.
    /// </summary>
    public static void MarkDirtyIfOpenings(Document doc, IEnumerable<ElementId> touched, int deletedCount)
    {
        if (_dirty) return;

        // A deletion cannot be classified — the element is gone. Rather than miss the case
        // where the thing deleted WAS a door, treat any deletion as possibly relevant. The
        // cost is one extra full sweep; the cost of the alternative is a stale schedule.
        if (deletedCount > 0)
        {
            _dirty = true;
            return;
        }

        foreach (var id in touched)
        {
            var category = doc.GetElement(id)?.Category?.Id.Value;
            if (category is (long)BuiltInCategory.OST_Doors or (long)BuiltInCategory.OST_Windows)
            {
                _dirty = true;
                return;
            }
        }
    }

    // ---------------------------------------------------------------- the pass

    /// <summary>
    /// Runs both resolvers. MUST be called from inside
    /// <c>FinishAutomation.WithoutSelfTriggering</c> — every write here lands on doors and
    /// windows, which are in the change updater's own trigger filter, so an unsuppressed
    /// pass re-dirties exactly what it just cleaned and schedules itself forever.
    /// </summary>
    /// <param name="force">Ignore the dirty flag. Used when a pass is asked for explicitly.</param>
    public static void Run(Document doc, string transactionPrefix, string reason, bool force = false)
    {
        if (doc.IsFamilyDocument || doc.IsReadOnly) return;
        if (!force && !_dirty) return;

        // Cleared up front, not at the end. A failure part-way must not leave the flag set
        // and have every subsequent trigger retry the same failing sweep; the next real
        // edit sets it again, which is the right moment to try once more.
        _dirty = false;

        if (_options.ResolveUdvendig) RunUdvendig(doc, transactionPrefix, reason);
        if (_options.ResolveLiningClashes) RunLining(doc, transactionPrefix, reason);
    }

    private static void RunUdvendig(Document doc, string transactionPrefix, string reason)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            var resolver = new UdvendigRoomResolver(doc, new UdvendigSettings());
            UdvendigResult? result = null;

            // Whole model, always: selection is [] rather than "whatever happens to be
            // selected". An automatic pass must not depend on where the user's cursor is.
            Transactions.Run(doc, transactionPrefix + "resolve Udvendig rooms",
                () => result = resolver.Run(apply: true, []));

            Log.Info($"Opening automation: Udvendig resolved because {reason}. " +
                     $"Took {watch.ElapsedMilliseconds} ms, {result!.Warnings.Count} warning(s).");

            foreach (var warning in result.Warnings.Take(20))
                Log.Warn("Udvendig: " + warning);
        }
        catch (Exception ex)
        {
            // Same contract as the finish pass: whatever triggered this — a save, a view
            // change — survives. Losing a save because a door could not be resolved is
            // never the right trade.
            Log.Error($"Opening automation: Udvendig pass failed ({reason}); doors were left alone.", ex);
        }
    }

    private static void RunLining(Document doc, string transactionPrefix, string reason)
    {
        try
        {
            var settings = new LiningSettings { LeftIsFamilyPlusX = _options.LeftIsFamilyPlusX };
            var resolver = new LiningClashResolver(doc, settings);
            var watch = Stopwatch.StartNew();

            LiningResult? result = null;

            // The apply pass calls Document.Regenerate() and reads "Lining Length" back off
            // the family, so the whole thing has to sit inside one transaction.
            Transactions.Run(doc, transactionPrefix + "resolve lining clashes",
                () => result = resolver.Run(apply: true, []));

            var lining = result!;
            var reportPath = WriteLiningReport(doc, lining);

            // The summary carries "N need changes; M written", which is the line that tells
            // a stale schedule apart from a pass that correctly found nothing to do.
            Log.Info($"Opening automation: lining clashes resolved because {reason}. " +
                     $"Took {watch.ElapsedMilliseconds} ms. " +
                     string.Join(" ", lining.Summary.Take(3)) +
                     $" Report: {reportPath}");

            foreach (var warning in lining.Warnings.Take(20))
                Log.Warn("Lining: " + warning);
        }
        catch (Exception ex)
        {
            Log.Error($"Opening automation: lining pass failed ({reason}); openings were left alone.", ex);
        }
    }

    /// <summary>
    /// Writes the full lining table and geometry dump to a STABLE filename, overwritten
    /// each pass rather than timestamped.
    ///
    /// Timestamping is right for a button someone presses a few times a day. This runs on
    /// every door and window edit, so timestamping would bury the reports folder in
    /// hundreds of near-identical files within a session and make the current one hard to
    /// find. One file per model, always current, is what you actually want to open when a
    /// lining number looks wrong: it answers "what does the tool think right now, and why".
    ///
    /// The report is the only audit trail left now that the buttons are gone, so it is
    /// written on every applying pass, not only when something changed.
    /// </summary>
    private static string WriteLiningReport(Document doc, LiningResult result)
    {
        var model = "Model";
        try
        {
            if (!string.IsNullOrWhiteSpace(doc.Title))
                model = Path.GetFileNameWithoutExtension(doc.Title);
        }
        catch
        {
            // Keep the fallback.
        }

        var safe = new string(model.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        var csvPath = Path.Combine(ReportWriter.ReportFolder, $"lining-current-{safe}.csv");

        ReportWriter.WriteCsv(csvPath, result.Rows);

        // Same shape as the Dynamo graph's sidecar log, so the two are diffable: summary,
        // then warnings, then the derived lining rectangles in millimetres. The geometry
        // dump is what lets a wrong number be diagnosed from the file instead of by
        // another round of guessing in the model.
        var log = new List<string>(result.Summary) { string.Empty, "WARNINGS:" };
        log.AddRange(result.Warnings.Count > 0 ? result.Warnings.Select(w => "  " + w) : ["  (none)"]);
        log.Add(string.Empty);
        log.Add("GEOMETRY (mm):");
        log.AddRange(result.Geometry);

        ReportWriter.WriteSidecarLog(csvPath, log);

        return csvPath;
    }
}
