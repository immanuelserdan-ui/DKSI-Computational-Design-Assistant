using Autodesk.Revit.DB;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Sweeps;

namespace Cda.Revit.Addin.Qa;

/// <summary>One piece of skirting in the model, however it was modelled.</summary>
public sealed class SweepBoard
{
    public required ElementId Id { get; init; }
    public required BoundingBoxXYZ Box { get; init; }
    public required XYZ Centre { get; init; }

    /// <summary>How it was found - shown in the grid so a mixed model is legible.</summary>
    public required string Origin { get; init; }

    /// <summary>Walls this board is hosted on, where the API can say. Empty is normal.</summary>
    public IReadOnlyList<ElementId> Hosts { get; init; } = [];

    public double MinZ => Box.Min.Z;
    public double MaxZ => Box.Max.Z;
}

/// <summary>
/// Finds every element in the model that is skirting, by any of the three ways it can get
/// there.
///
/// THE CENTRAL FACT THIS CLASS EXISTS FOR
///   The brief for this check said "scan <c>OST_WallSweeps</c>". On THIS project that would
///   find nothing and report every wall in the building as missing its board.
///   <c>Skirtingboard_21-80mm</c> is a type under the <c>Wall_Sweep V6</c> COMPONENT family -
///   a native <see cref="WallSweep"/> was built first and deliberately discarded, because a
///   voided native sweep still reports its full length in a schedule and skirting is priced
///   by the metre. See <see cref="SkirtingPlacer"/>.
///
///   So the inventory covers all three:
///     * native WallSweep elements       - hand-modelled, inherited, or from a consultant
///     * components carrying our stamp   - anything Place Skirting produced
///     * components matching the type    - placed by hand from the same family
///
///   Counting only the first would be a false alarm on every wall. Counting only the second
///   would report hand-placed boards as missing, which teaches people to ignore the tool.
/// </summary>
public static class SweepInventory
{
    public static IReadOnlyList<SweepBoard> Collect(Document doc, QaSettings settings)
    {
        var boards = new List<SweepBoard>();
        var seen = new HashSet<long>();

        if (settings.CountNativeWallSweeps) CollectNative(doc, boards, seen);
        CollectStamped(doc, boards, seen, settings);
        if (settings.CountUnstampedComponents) CollectByName(doc, boards, seen, settings);

        return boards;
    }

    // ---------------------------------------------------------------- native sweeps

    private static void CollectNative(Document doc, List<SweepBoard> boards, HashSet<long> seen)
    {
        IEnumerable<Element> found;

        try
        {
            found = new FilteredElementCollector(doc)
                .OfClass(typeof(WallSweep))
                .WhereElementIsNotElementType()
                .ToElements();
        }
        catch (Exception ex)
        {
            Log.Warn($"QA: native wall sweeps could not be collected: {ex.Message}");
            return;
        }

        foreach (var element in found)
        {
            if (element is not WallSweep sweep) continue;

            // Reveals are cut INTO a wall, not applied to it - a recess, not a board.
            // Counting one as skirting would mark a bare wall as covered.
            try
            {
                if (sweep.GetWallSweepInfo()?.WallSweepType != WallSweepType.Sweep) continue;
            }
            catch
            {
                // Unreadable info: keep it. A false "covered" is worse than a false alarm,
                // but so is dropping a real sweep, and this path is rare enough that the
                // Origin column will make it obvious in the grid.
            }

            var hosts = new List<ElementId>();
            try { hosts.AddRange(sweep.GetHostIds()); }
            catch { /* not all sweeps report hosts */ }

            Add(boards, seen, element, "Native wall sweep", hosts);
        }
    }

    // --------------------------------------------------------------- stamped boards

    /// <summary>
    /// Everything Place Skirting has put in this model, found through Extensible Storage.
    ///
    /// The quick filter is the point: without it this is a sweep of every FamilyInstance in
    /// the document with a parameter read on each - tens of thousands of reads to find
    /// perhaps eighty boards. Same reasoning as <c>SkirtingGenerator.Generated</c>, and the
    /// same fallback when no schema exists yet.
    /// </summary>
    private static void CollectStamped(
        Document doc, List<SweepBoard> boards, HashSet<long> seen, QaSettings settings)
    {
        try
        {
            var collector = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .WhereElementIsNotElementType();

            var stamped = ElementStamp.Filter();
            var candidates = stamped is null ? collector : collector.WherePasses(stamped);

            foreach (var element in candidates)
            {
                if (ElementStamp.Read(element, SkirtingSettings.Stamp, SkirtingSettings.Stamp) is null)
                    continue;

                Add(boards, seen, element, "Placed by DKSI tool", Hosts(element));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"QA: stamped skirting could not be collected: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ name-matched boards

    private static void CollectByName(
        Document doc, List<SweepBoard> boards, HashSet<long> seen, QaSettings settings)
    {
        var needle = settings.Skirting.TypeName;
        if (string.IsNullOrWhiteSpace(needle)) return;

        try
        {
            var filter = new ElementMulticategoryFilter(settings.ComponentCategories);

            foreach (var element in new FilteredElementCollector(doc)
                         .OfClass(typeof(FamilyInstance))
                         .WhereElementIsNotElementType()
                         .WherePasses(filter))
            {
                if (seen.Contains(element.Id.Value)) continue;
                if (!NameMatches(doc, element, needle)) continue;

                Add(boards, seen, element, "Placed by hand", Hosts(element));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"QA: name-matched skirting could not be collected: {ex.Message}");
        }
    }

    /// <summary>
    /// Matched against the family name AND the type name, exactly as
    /// <see cref="SkirtingSettings.TypeName"/> documents - either can carry the name
    /// depending on how the family was authored.
    /// </summary>
    private static bool NameMatches(Document doc, Element element, string needle)
    {
        try
        {
            if (element.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;

            if (doc.GetElement(element.GetTypeId()) is not ElementType type) return false;

            return type.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                   type.FamilyName.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // -------------------------------------------------------------------- helpers

    private static IReadOnlyList<ElementId> Hosts(Element element)
    {
        try
        {
            return element is FamilyInstance { Host: not null } instance
                ? [instance.Host.Id]
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static void Add(
        List<SweepBoard> boards,
        HashSet<long> seen,
        Element element,
        string origin,
        IReadOnlyList<ElementId> hosts)
    {
        if (!seen.Add(element.Id.Value)) return;

        BoundingBoxXYZ? box;
        try { box = element.get_BoundingBox(null); }
        catch { return; }

        // No box means no geometry in any view - a board that cannot be measured cannot
        // cover anything, and silently treating it as coverage would hide a real gap.
        if (box is null) return;

        boards.Add(new SweepBoard
        {
            Id = element.Id,
            Box = box,
            Centre = (box.Min + box.Max) / 2.0,
            Origin = origin,
            Hosts = hosts,
        });
    }
}
