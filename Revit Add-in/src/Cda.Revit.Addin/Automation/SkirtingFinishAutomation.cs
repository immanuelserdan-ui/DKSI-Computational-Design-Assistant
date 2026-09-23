using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Sweeps;

namespace Cda.Revit.Addin.Automation;

/// <summary>
/// Keeps generated skirting off tile as the model is repainted: paint a wall stretch in a
/// no-skirting finish (a code ending in F - VBF, GBF) and the boards on that stretch are
/// removed, shortened or split right away, without running Place Skirting again.
///
/// Rides the same DocumentChanged → ExternalEvent pipeline as the casework cutter, and for the
/// same reason reacts immediately and scoped: the Paint tool reports the wall it painted as
/// modified, so the work list is exactly the walls touched, and each is a small job.
///
/// ACTIVE ONLY WHERE SKIRTING ALREADY EXISTS. It acts on boards Place Skirting stamped, so on a
/// model - or a wall - with none there is nothing to trim and nothing happens. It never places a
/// board: see <see cref="SkirtingFinishTrimmer"/>.
///
/// Put <c>"Enabled": false</c> in skirting-finish-automation.json to switch it off.
/// </summary>
internal static class SkirtingFinishAutomation
{
    private sealed class Options
    {
        public bool Enabled { get; set; } = true;
    }

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cda", "RevitAddin", "skirting-finish-automation.json");

    private static readonly Options _options = Load();

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
            Log.Warn($"Skirting finish automation settings unreadable, using defaults: {ex.Message}");
            return new Options();
        }
    }

    /// <summary>Walls and columns edited since the last pass - anything a board can be placed against.</summary>
    private static readonly DocumentScoped<HashSet<ElementId>> PendingHosts = new();

    public static bool IsDirty(Document doc) => PendingHosts.Has(doc) && PendingHosts.For(doc).Count > 0;

    public static void Forget(Document? doc) => PendingHosts.Forget(doc);

    /// <summary>Read-only context: classify and queue, nothing more.</summary>
    public static void MarkDirty(Document doc, IEnumerable<ElementId> touched)
    {
        if (!_options.Enabled) return;

        HashSet<ElementId>? pending = null;

        foreach (var id in touched)
        {
            var element = doc.GetElement(id);
            if (element is null) continue;

            // SPLIT FACE is reported as a new splitter, not always as a change to the wall it
            // divides - and splitting is the usual first step of tiling part of a wall.
            var host = element is FaceSplitter splitter ? splitter.SplitElementId : id;

            var category = element is FaceSplitter
                ? doc.GetElement(host)?.Category?.Id.Value
                : element.Category?.Id.Value;

            if (category is (long)BuiltInCategory.OST_Walls
                         or (long)BuiltInCategory.OST_Columns
                         or (long)BuiltInCategory.OST_StructuralColumns)
            {
                (pending ??= PendingHosts.For(doc)).Add(host);
            }
        }
    }

    /// <summary>
    /// Trims what is owed. MUST be called from inside <c>FinishAutomation.WithoutSelfTriggering</c>:
    /// deleting and reshaping boards is itself a model change.
    /// </summary>
    public static void Run(Document doc, string transactionPrefix, string reason)
    {
        if (!_options.Enabled || doc.IsFamilyDocument || doc.IsReadOnly) return;
        if (!PendingHosts.Has(doc)) return;

        // Taken up front: a failing pass must not be retried on every flush. The next edit to
        // the wall queues it again.
        var hosts = PendingHosts.For(doc).ToList();
        PendingHosts.Forget(doc);
        if (hosts.Count == 0) return;

        try
        {
            var watch = Stopwatch.StartNew();
            var trimmer = new SkirtingFinishTrimmer(doc, new SkirtingSettings());
            var plan = trimmer.Survey(hosts);

            if (plan.LockedByOthers > 0)
                Log.Warn($"Skirting finish automation: {plan.LockedByOthers} board(s) on tile are checked " +
                         "out by another user and were left in place.");

            if (plan.Changes.Count == 0)
            {
                Log.Debug($"Skirting finish automation: nothing on tile ({reason}); " +
                          $"{plan.Examined} board(s) on {hosts.Count} host(s) checked in {watch.ElapsedMilliseconds} ms.");
                return;
            }

            var problems = new List<string>();
            int deleted = 0, shortened = 0;
            var removed = 0.0;

            Transactions.Run(doc, transactionPrefix + "remove skirting from tiled wall",
                () => problems = trimmer.Apply(plan, out deleted, out shortened, out removed),
                swallowWarnings: true);

            Log.Info($"Skirting finish automation: {deleted} board(s) removed and {shortened} shortened, " +
                     $"{Measure.ToMetres(removed):0.00} m taken off " +
                     $"{string.Join(", ", plan.Materials.Select(m => $"'{m}'"))} because {reason}. " +
                     $"{watch.ElapsedMilliseconds} ms.");

            foreach (var problem in problems.Take(20))
                Log.Warn("Skirting finish automation: " + problem);
        }
        catch (Exception ex)
        {
            // Same contract as every other automatic pass: the edit that triggered it survives.
            Log.Error($"Skirting finish automation: pass failed ({reason}); boards were left alone.", ex);
        }
    }
}
