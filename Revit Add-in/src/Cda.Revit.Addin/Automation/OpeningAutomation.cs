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

        /// <summary>
        /// Mirror of <see cref="UdvendigSettings.DeriveSidesFromFacing"/>, surfaced here for the
        /// same reason <see cref="LeftIsFamilyPlusX"/> is: a settings edit rather than a rebuild.
        ///
        /// OFF by default, and NOT cosmetic - unlike LeftIsFamilyPlusX, this one moves numbers.
        /// Switching it on makes every flipped door's FROM and TO swap relative to Revit's own
        /// From Room / To Room fields, which is the entire point and also the entire risk: any
        /// other schedule or export reading the built-in fields will then disagree with the
        /// door schedules, silently. Read the note on UdvendigSettings.DeriveSidesFromFacing
        /// before turning it on.
        /// </summary>
        public bool DeriveDoorSidesFromFacing { get; set; }

        /// <summary>
        /// Let the resolver REPLACE the built-in From/To Room columns in every schedule with the
        /// SCRP parameters. OFF - see UdvendigSettings.RepointSchedules for the full account of
        /// why this default changed. Short version: it deleted an office standard from 39
        /// schedules, and re-did it on every model edit, so nobody could put it back by hand.
        /// </summary>
        public bool RepointScheduleColumns { get; set; }

        /// <summary>
        /// Add back the built-in From/To Room columns removed by earlier builds. A one-off
        /// repair: switch on, run, switch off. Additive only - it can never remove a column.
        /// </summary>
        public bool RestoreBuiltInRoomColumns { get; set; }

        /// <summary>
        /// Mirror of <see cref="UdvendigSettings.RestoreFromToScheduleColumns"/>. A one-off
        /// repair, same discipline as <see cref="RestoreBuiltInRoomColumns"/>: switch on, run
        /// 'Resolve Udvendig Rooms' from the ribbon once, switch off. Unlike that one, this
        /// REMOVES the SCRP column it replaces, and only in the interior 'Door * FROM/TO' set -
        /// see the setting's own doc comment for why the two schedule sets need this to differ.
        /// </summary>
        public bool RestoreFromToScheduleColumns { get; set; }

        /// <summary>
        /// Mirror of <see cref="UdvendigSettings.RevealExtDoorRoomColumns"/>. A one-off repair,
        /// same discipline as the two above: switch on, run 'Resolve Udvendig Rooms' from the
        /// ribbon once, switch off. The gentlest of the three - it changes column visibility,
        /// one heading and column order, and never adds or removes a field.
        /// </summary>
        public bool RevealExtDoorRoomColumns { get; set; }

        /// <summary>
        /// Mirror of <see cref="UdvendigSettings.SubstituteExteriorSide"/>. UNLIKE every other
        /// flag here this one defaults to TRUE, because it is the tool's original behaviour and
        /// switching it off changes what the SCRP parameters mean for exterior doors.
        /// </summary>
        public bool SubstituteExteriorSide { get; set; } = true;

        /// <summary>
        /// Mirror of <see cref="UdvendigSettings.RepointFromToScheduleColumns"/>. A one-off
        /// repair. Pair it with <see cref="SubstituteExteriorSide"/> set to false, and never
        /// with <see cref="RestoreFromToScheduleColumns"/>, which is its exact opposite.
        /// </summary>
        public bool RepointFromToScheduleColumns { get; set; }

        /// <summary>
        /// Mirror of <see cref="UdvendigSettings.FilterExtDoorSchedules"/>. A one-off repair,
        /// and the only flag in this file that changes which ROWS a schedule lists.
        /// </summary>
        public bool FilterExtDoorSchedules { get; set; }

        /// <summary>
        /// Mirror of <see cref="UdvendigSettings.RemoveExtDoorFilter"/>. The way back from
        /// <see cref="FilterExtDoorSchedules"/>, which empties that set in this model.
        /// </summary>
        public bool RemoveExtDoorFilter { get; set; }

        /// <summary>
        /// Mirror of <see cref="UdvendigSettings.PointExtDoorToSubstituted"/>. The last step of
        /// the office rule: the 'Ext Door' columns stop showing 'Udvendig'. A one-off repair,
        /// and it needs the substituted shared parameters to exist first.
        /// </summary>
        public bool PointExtDoorToSubstituted { get; set; }
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

    /// <summary>
    /// Exposed so the ribbon command builds its resolver the same way this automation does.
    /// Without it a manual run would use a different rule from the automatic one and quietly
    /// overwrite its answers - the two must never disagree about what a door's sides are.
    /// </summary>
    public static bool DeriveDoorSidesFromFacing => _options.DeriveDoorSidesFromFacing;

    public static bool RepointScheduleColumns => _options.RepointScheduleColumns;

    public static bool RestoreBuiltInRoomColumns => _options.RestoreBuiltInRoomColumns;

    public static bool RestoreFromToScheduleColumns => _options.RestoreFromToScheduleColumns;

    public static bool RevealExtDoorRoomColumns => _options.RevealExtDoorRoomColumns;

    public static bool SubstituteExteriorSide => _options.SubstituteExteriorSide;

    public static bool RepointFromToScheduleColumns => _options.RepointFromToScheduleColumns;

    public static bool FilterExtDoorSchedules => _options.FilterExtDoorSchedules;

    public static bool RemoveExtDoorFilter => _options.RemoveExtDoorFilter;

    public static bool PointExtDoorToSubstituted => _options.PointExtDoorToSubstituted;

    public static string SettingsFile => SettingsPath;

    // ---------------------------------------------------------------- dirty tracking

    /// <summary>
    /// Set when a door, window or ROOM is added, changed or deleted. Separate from the finish
    /// engine's flag on purpose: moving a wall makes finish areas stale without changing
    /// any opening, and re-running a whole-model door sweep for that is wasted time.
    ///
    /// Rooms are in here because the resolver copies room numbers and names onto doors - see
    /// the note in <see cref="MarkDirtyIfOpenings"/>.
    /// </summary>
    /// <remarks>
    /// PER DOCUMENT. As a plain static this meant "SOME document owes a resolve", and
    /// <see cref="Run"/> - handed whatever document happened to be active when the deferred
    /// flush fired - would sweep that one and clear the flag on the way in. The document that
    /// actually owed the pass never got one, and the door schedules it feeds silently kept the
    /// previous run's values. See <see cref="DocumentScoped{T}"/>.
    /// </remarks>
    private static readonly DocumentScoped<DocumentFlags> Flags = new();

    public static bool IsDirty(Document doc) => Flags.For(doc).OpeningDirty;

    /// <summary>Releases a closing document's flag so it cannot outlive the file.</summary>
    public static void Forget(Document? doc) => Flags.Forget(doc);

    /// <summary>
    /// True when any of the given ids is a door or window — including deleted ones, which
    /// is why this takes the categories from the change event rather than resolving each
    /// id against the document. A deleted element cannot be looked up.
    /// </summary>
    public static void MarkDirtyIfOpenings(Document doc, IEnumerable<ElementId> touched, int deletedCount)
    {
        var flags = Flags.For(doc);
        if (flags.OpeningDirty) return;

        // A deletion cannot be classified — the element is gone. Rather than miss the case
        // where the thing deleted WAS a door, treat any deletion as possibly relevant. The
        // cost is one extra full sweep; the cost of the alternative is a stale schedule.
        if (deletedCount > 0)
        {
            flags.OpeningDirty = true;
            return;
        }

        foreach (var id in touched)
        {
            var category = doc.GetElement(id)?.Category?.Id.Value;

            // ROOMS COUNT, and leaving them out was a real gap.
            //
            // The Udvendig resolver writes the room NUMBER and NAME of whatever sits on each
            // side of a door into the SCRP parameters, and the door schedules read those. So
            // renaming or renumbering a room changes what every adjacent door should say -
            // while touching no door at all. Watching only Doors and Windows meant _dirty
            // stayed false, RunOpeningPass early-returned, and the schedules kept showing the
            // OLD room name until somebody happened to edit a door, reopen the model, or press
            // the ribbon button. Save did not rescue it either: Recalculate calls
            // RunOpeningPass unforced.
            //
            // That is indistinguishable from the automation being broken, and it is exactly
            // the "the From/To values do not update" symptom. A room edit is cheap to react to
            // and rare next to geometry edits, so the extra sweep costs little.
            if (category is (long)BuiltInCategory.OST_Doors
                         or (long)BuiltInCategory.OST_Windows
                         or (long)BuiltInCategory.OST_Rooms)
            {
                flags.OpeningDirty = true;
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

        var flags = Flags.For(doc);
        if (!force && !flags.OpeningDirty) return;

        // Cleared up front, not at the end. A failure part-way must not leave the flag set
        // and have every subsequent trigger retry the same failing sweep; the next real
        // edit sets it again, which is the right moment to try once more.
        flags.OpeningDirty = false;

        if (_options.ResolveUdvendig) RunUdvendig(doc, transactionPrefix, reason);
        if (_options.ResolveLiningClashes) RunLining(doc, transactionPrefix, reason);
    }

    private static void RunUdvendig(Document doc, string transactionPrefix, string reason)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            var resolver = new UdvendigRoomResolver(doc, new UdvendigSettings
            {
                DeriveSidesFromFacing = _options.DeriveDoorSidesFromFacing,
                RepointSchedules = _options.RepointScheduleColumns,
                RestoreBuiltInRoomColumns = _options.RestoreBuiltInRoomColumns,
                RestoreFromToScheduleColumns = _options.RestoreFromToScheduleColumns,
                RevealExtDoorRoomColumns = _options.RevealExtDoorRoomColumns,
                SubstituteExteriorSide = _options.SubstituteExteriorSide,
                RepointFromToScheduleColumns = _options.RepointFromToScheduleColumns,
                FilterExtDoorSchedules = _options.FilterExtDoorSchedules,
                RemoveExtDoorFilter = _options.RemoveExtDoorFilter,
                PointExtDoorToSubstituted = _options.PointExtDoorToSubstituted,
            });
            UdvendigResult? result = null;

            // Whole model, always: selection is [] rather than "whatever happens to be
            // selected". An automatic pass must not depend on where the user's cursor is.
            //
            // swallowWarnings, because nobody triggered this and nobody is waiting on it. It
            // runs from an idle tick, from document open, and from inside DocumentSaving - and
            // a modal Revit warning raised from any of those is, to the user, an add-in that
            // has hung the model. Errors are untouched and still roll the transaction back.
            Transactions.Run(doc, transactionPrefix + "resolve Udvendig rooms",
                () => result = resolver.Run(apply: true, []), swallowWarnings: true);

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
            //
            // swallowWarnings for the same reason as the Udvendig pass above: this is an
            // unattended sweep over every opening in the model, run from idle, from document
            // open and from inside DocumentSaving.
            Transactions.Run(doc, transactionPrefix + "resolve lining clashes",
                () => result = resolver.Run(apply: true, []), swallowWarnings: true);

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
