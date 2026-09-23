using Autodesk.Revit.DB;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Sweeps;

/// <summary>
/// Takes skirting OFF a wall that has been repainted in a no-skirting finish since the boards
/// were placed - see <see cref="FinishCodeRule"/>. A board wholly on tile is deleted; one partly
/// on tile is shortened, or split into the painted pieces either side.
///
/// ONLY EVER REMOVES. It works on boards Place Skirting already made (found by their stamp), so
/// until that has run there is nothing here to act on. Repainting tile back to paint does not
/// bring a board back: placing one needs the whole room's corner and opening logic, which is
/// what Regenerate is for.
///
/// PLAN, THEN APPLY. Planning reads the model only, so an edit that changes nothing here - most
/// wall edits - opens no transaction and leaves nothing on the undo stack.
/// </summary>
internal sealed class SkirtingFinishTrimmer
{
    public sealed record Change(ElementId Board, Curve Original, IReadOnlyList<Curve> Kept);

    public sealed class Plan
    {
        public List<Change> Changes { get; } = [];
        public SortedSet<string> Materials { get; } = new(StringComparer.Ordinal);
        public int Examined { get; set; }
        public int LockedByOthers { get; set; }
        public int NotCurveBased { get; set; }
    }

    private readonly Document _doc;
    private readonly SkirtingSettings _settings;
    private readonly FinishSkipDetector _detector;

    public SkirtingFinishTrimmer(Document doc, SkirtingSettings settings)
    {
        _doc = doc;
        _settings = settings;
        _detector = new FinishSkipDetector(doc, settings.NoSkirtingFinishSuffix, settings.MinimumRun);
    }

    /// <summary>What would change on the boards placed against <paramref name="hostIds"/>. Read-only.</summary>
    public Plan Survey(IEnumerable<ElementId> hostIds)
    {
        var plan = new Plan();
        if (!_detector.IsOn) return plan;

        var hosts = new Dictionary<string, Element>(StringComparer.Ordinal);
        var insertHosts = new Dictionary<string, Element>(StringComparer.Ordinal);

        foreach (var host in hostIds.Select(_doc.GetElement).OfType<Element>())
        {
            hosts[host.UniqueId] = host;

            // Jamb boards are stamped with their OPENING as source, not the wall, but they sit
            // on the wall's own jamb face - so a repainted wall is their question too.
            if (host is not Wall wall) continue;

            try
            {
                foreach (var insertId in wall.FindInserts(true, false, false, false))
                {
                    if (_doc.GetElement(insertId) is { } insert) insertHosts[insert.UniqueId] = wall;
                }
            }
            catch
            {
                // A wall that will not list its inserts still has its own boards checked.
            }
        }

        if (hosts.Count == 0) return plan;

        var stamped = ElementStamp.Filter();
        if (stamped is null) return plan;

        foreach (var board in new FilteredElementCollector(_doc)
                     .OfClass(typeof(FamilyInstance))
                     .WhereElementIsNotElementType()
                     .WherePasses(stamped))
        {
            var source = ElementStamp.ReadSource(board, SkirtingSettings.Stamp);
            if (source is null) continue;

            if (!hosts.TryGetValue(source, out var host) && !insertHosts.TryGetValue(source, out host))
                continue;

            plan.Examined++;

            if (board.Location is not LocationCurve { Curve: { IsBound: true } curve })
            {
                plan.NotCurveBased++;
                continue;
            }

            var kept = _detector.Remaining(curve, host, SampleZ(board, curve), plan.Materials);

            if (kept.Count == 1 && Math.Abs(kept[0].Length - curve.Length) < Unchanged) continue;

            if (!Worksharing.CanWrite(_doc, board.Id))
            {
                plan.LockedByOthers++;
                continue;
            }

            plan.Changes.Add(new Change(board.Id, curve, kept));
        }

        return plan;
    }

    /// <summary>Applies a survey. Caller owns the transaction. Returns what could not be done.</summary>
    public List<string> Apply(Plan plan, out int deleted, out int shortened, out double removedLength)
    {
        var problems = new List<string>();
        deleted = 0;
        shortened = 0;
        removedLength = 0.0;

        foreach (var change in plan.Changes)
        {
            try
            {
                if (change.Kept.Count == 0)
                {
                    _doc.Delete(change.Board);
                    deleted++;
                    removedLength += change.Original.Length;
                    continue;
                }

                if (_doc.GetElement(change.Board) is not FamilyInstance board ||
                    board.Location is not LocationCurve location) continue;

                // Copies first, from the untouched board, so each keeps its stamp - that is what
                // lets the next Regenerate find and replace them like any other board.
                for (var i = 1; i < change.Kept.Count; i++)
                {
                    var copyId = ElementTransformUtils.CopyElement(_doc, change.Board, XYZ.Zero).First();

                    if (_doc.GetElement(copyId) is FamilyInstance copy && copy.Location is LocationCurve copyLocation)
                    {
                        copyLocation.Curve = change.Kept[i];
                        SquareCutEnds(copy, change.Original, change.Kept[i]);
                    }
                }

                location.Curve = change.Kept[0];
                SquareCutEnds(board, change.Original, change.Kept[0]);

                shortened++;
                removedLength += change.Original.Length - change.Kept.Sum(k => k.Length);
            }
            catch (Exception ex)
            {
                problems.Add($"board {change.Board.Value}: {ex.Message}");
            }
        }

        return problems;
    }

    /// <summary>
    /// An end made by cutting at the edge of the tile is square, whatever corner angle it was
    /// mitred to before. Only matters once the family carries end-angle parameters.
    /// </summary>
    private void SquareCutEnds(FamilyInstance board, Curve original, Curve kept)
    {
        if (kept.GetEndPoint(0).DistanceTo(original.GetEndPoint(0)) > Unchanged)
            SetFirst(board, _settings.MitreStartParameterNames, 0.0);

        if (kept.GetEndPoint(1).DistanceTo(original.GetEndPoint(1)) > Unchanged)
            SetFirst(board, _settings.MitreEndParameterNames, 0.0);
    }

    private static void SetFirst(Element element, IEnumerable<string> names, double value)
    {
        foreach (var name in names)
        {
            var parameter = ParameterHelper.Find(element, name);
            if (parameter is null || parameter.IsReadOnly || parameter.StorageType != StorageType.Double) continue;

            parameter.Set(value);
            return;
        }
    }

    /// <summary>The middle of the board's own height, which is where the tile question is asked.</summary>
    private double SampleZ(Element board, Curve curve)
    {
        try
        {
            var box = board.get_BoundingBox(null);
            if (box is not null) return (box.Min.Z + box.Max.Z) / 2.0;
        }
        catch
        {
            // Fall back to the settings height.
        }

        return curve.GetEndPoint(0).Z + (_settings.BoardHeight / 2.0);
    }

    /// <summary>~1 mm: less than this is the same board, not a trim.</summary>
    private const double Unchanged = 0.0033;
}
