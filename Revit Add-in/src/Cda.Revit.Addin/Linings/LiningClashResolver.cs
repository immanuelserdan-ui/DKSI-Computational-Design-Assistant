using Autodesk.Revit.DB;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Linings;

public sealed class LiningPlan
{
    public required Opening Opening { get; init; }
    public required Dictionary<LiningSide, int> Desired { get; init; }
    public required Dictionary<LiningSide, int?> Current { get; init; }
    public required double ChangeBefore { get; init; }
    public required double ChangeAfter { get; init; }
    public required double PredictedTotal { get; init; }
    public required List<string> Notes { get; init; }

    public bool Changed { get; set; }
    public int? MasterDesired { get; set; }
    public string? MaterialDesired { get; set; }
    public double? ActualTotal { get; set; }
}

public sealed class LiningResult
{
    public required IReadOnlyList<string> Summary { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required IReadOnlyList<string> Geometry { get; init; }
}

/// <summary>
/// Port of Resolve-Lining-Clashes_v1.0.dyn.
///
/// When a door and a window sit in the same wall with (near) zero gap, the lining boards
/// on the facing edges occupy the same physical reveal. Exactly one of the two may carry
/// lining there. Unchecking a side removes the WHOLE side, including the part the
/// neighbour does not cover - which is why the leftover length has to be banked in
/// "Lining Change".
///
/// The family formulas are what make this work:
///     Lining Top   = if(Lining Top YN,   Width,  0 mm)
///     Lining Left  = if(Lining Left YN,  Height, 0 mm)
///     Lining Right = if(Lining Right YN, Height, 0 mm)
///     Lining Length (Door) = Top + Right + Left + Lining Change
///
/// Always a full recompute, never an increment, so it is idempotent and safe to re-run.
/// </summary>
public sealed class LiningClashResolver
{
    private static readonly LiningSide[] Sides = [LiningSide.Top, LiningSide.Left, LiningSide.Right];

    private readonly Document _doc;
    private readonly LiningSettings _settings;

    private readonly List<string> _warnings = [];
    private readonly List<string> _skipped = [];
    private readonly List<Opening> _openings = [];

    /// <summary>
    /// Ids the user asked to act on, or null for the whole model. Openings outside this
    /// set are still loaded and still block their neighbours - they are simply never
    /// written. Scoping the neighbour set instead of the write set is what made an
    /// earlier version switch a door's lining back ON when it was selected alone.
    /// </summary>
    private HashSet<long>? _scopeIds;

    // Tolerances converted once.
    private readonly double _gapTol;
    private readonly double _minBlock;
    private readonly double _minRemnant;
    private readonly double _overlapWarn;
    private readonly double _touchSlack;
    private readonly double _collinearTol;

    public LiningClashResolver(Document doc, LiningSettings settings)
    {
        _doc = doc;
        _settings = settings;

        _gapTol = Units.ToFeet(settings.GapToleranceMm);
        _minBlock = Units.ToFeet(settings.MinBlockMm);
        _minRemnant = Units.ToFeet(settings.MinRemnantMm);
        _overlapWarn = Units.ToFeet(settings.OverlapWarnMm);
        _touchSlack = Units.ToFeet(settings.TouchSlackMm);
        _collinearTol = Units.ToFeet(settings.CollinearToleranceMm);
    }

    public LiningResult Run(bool apply, IReadOnlyList<Element>? selection = null)
    {
        _scopeIds = selection is { Count: > 0 }
            ? [.. selection.Select(e => e.Id.Value)]
            : null;

        // Always the whole model: a clash is a property of the neighbourhood, so the
        // neighbours have to be present even when only one opening is being written.
        var instances = CollectInstances();
        var axes = new Dictionary<long, WallAxis?>();
        var levels = new Dictionary<long, double>();

        foreach (var instance in instances)
        {
            try
            {
                var opening = BuildOpening(instance, axes, levels);
                if (opening is not null) _openings.Add(opening);
            }
            catch (Exception ex)
            {
                // This handler must never throw, or one bad element kills the run.
                _skipped.Add($"{SafeId(instance)} -- {ex.Message}");
            }
        }

        var groups = GroupByWallRun(axes);
        var pulledIn = ExpandScopeToTouchedWindows(groups);
        var plans = Solve(groups);

        if (_settings.PropagateMasterFromDoors || _settings.MaterialFromDoors)
            PropagateFromDoors(groups, plans);

        var applied = 0;
        var failed = new List<string>();
        if (apply) applied = Apply(plans, failed);

        return new LiningResult
        {
            Rows = BuildRows(plans),
            Warnings = BuildWarnings(plans),
            Summary = BuildSummary(plans, groups, apply, applied, failed, pulledIn),
            Geometry = BuildGeometry(groups, plans),
        };
    }

    // ------------------------------------------------------------------ collect

    private List<FamilyInstance> CollectInstances()
    {
        var result = new List<FamilyInstance>();
        foreach (var category in new[] { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows })
        {
            result.AddRange(new FilteredElementCollector(_doc)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>());
        }

        return result;
    }

    private Opening? BuildOpening(
        FamilyInstance instance,
        Dictionary<long, WallAxis?> axes,
        Dictionary<long, double> levels)
    {
        // An opening with no lining parameters is NOT dropped. It still occupies its share
        // of the reveal, so it must block its neighbours, and the Window Material /
        // Lining YN rules are about door-to-window contact, not about lining parameters.
        // Opening.HasLiningParameters records it; only the lining writes are suppressed.
        if (instance.Host is not Wall host)
        {
            _skipped.Add($"{instance.Id.Value} -- host is not a wall");
            return null;
        }

        var hostKey = host.Id.Value;
        if (!axes.TryGetValue(hostKey, out var axis))
            axes[hostKey] = axis = WallAxis.Of(host);

        if (axis is null)
        {
            _skipped.Add($"{instance.Id.Value} -- host wall has no location curve");
            return null;
        }

        var levelKey = instance.LevelId.Value;
        if (!levels.TryGetValue(levelKey, out var elevation))
        {
            var level = _doc.GetElement(instance.LevelId) as Level;
            levels[levelKey] = elevation = level?.Elevation ?? 0.0;
        }

        return new Opening(instance, axis, elevation, _settings);
    }

    // ------------------------------------------------------------------- group

    /// <summary>
    /// Same host wall, then merge collinear hosts, so joined walls and stacked-wall
    /// members are treated as one continuous run.
    /// </summary>
    private List<List<Opening>> GroupByWallRun(Dictionary<long, WallAxis?> axes)
    {
        var parent = new Dictionary<long, long>();

        long Find(long key)
        {
            while (parent[key] != key)
            {
                parent[key] = parent[parent[key]];
                key = parent[key];
            }
            return key;
        }

        void Union(long a, long b)
        {
            var (ra, rb) = (Find(a), Find(b));
            if (ra != rb) parent[rb] = ra;
        }

        foreach (var opening in _openings)
            parent.TryAdd(opening.Axis.WallId, opening.Axis.WallId);

        // Only walls already pointing the same way can be collinear, so bucket on a
        // rounded direction rather than comparing every pair.
        var buckets = new Dictionary<(double X, double Y, double Z), List<long>>();
        foreach (var wallId in parent.Keys)
        {
            var axis = axes[wallId];
            if (axis?.Direction is null) continue;

            var key = (Math.Round(axis.Direction.X, 4),
                       Math.Round(axis.Direction.Y, 4),
                       Math.Round(axis.Direction.Z, 4));

            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
            list.Add(wallId);
        }

        foreach (var bucket in buckets.Values)
        {
            for (var i = 0; i < bucket.Count; i++)
            {
                for (var j = i + 1; j < bucket.Count; j++)
                {
                    if (WallAxis.SameLine(axes[bucket[i]]!, axes[bucket[j]]!, _collinearTol))
                        Union(bucket[i], bucket[j]);
                }
            }
        }

        var groups = new Dictionary<long, List<Opening>>();
        foreach (var opening in _openings)
        {
            var root = Find(opening.Axis.WallId);
            if (!groups.TryGetValue(root, out var list)) groups[root] = list = [];
            list.Add(opening);
        }

        // Openings merged into one run must measure u on one shared axis.
        foreach (var (root, members) in groups)
        {
            var reference = axes[root]!;
            if (reference.Direction is null) continue;

            foreach (var opening in members.Where(o => o.Axis.WallId != root))
                opening.Reproject(reference);
        }

        return [.. groups.Values];
    }

    /// <summary>
    /// A door decides the master "Lining YN" and the Window Material for the windows it
    /// touches. Selecting only the door therefore has to pull those windows into scope,
    /// or the propagation has nothing to write to and the door's material change appears
    /// to do nothing.
    ///
    /// Only windows in direct contact with a SELECTED door are added, never the reverse,
    /// and the count is reported so the extra writes are never silent.
    /// </summary>
    private int ExpandScopeToTouchedWindows(List<List<Opening>> groups)
    {
        if (_scopeIds is null) return 0;
        if (!_settings.PropagateMasterFromDoors && !_settings.MaterialFromDoors) return 0;

        var added = 0;

        foreach (var members in groups)
        {
            var selectedDoors = members
                .Where(o => o.CategoryId == (long)BuiltInCategory.OST_Doors && _scopeIds.Contains(o.Id))
                .ToList();

            if (selectedDoors.Count == 0) continue;

            foreach (var window in members.Where(o => o.CategoryId == (long)BuiltInCategory.OST_Windows))
            {
                if (_scopeIds.Contains(window.Id)) continue;
                if (!selectedDoors.Any(d => Opening.Touches(window, d, _gapTol))) continue;

                _scopeIds.Add(window.Id);
                added++;
            }
        }

        return added;
    }

    // ------------------------------------------------------------------- solve

    private List<LiningPlan> Solve(List<List<Opening>> groups)
    {
        var plans = new List<LiningPlan>();

        foreach (var members in groups)
        {
            ReportGenuineIntersections(members);

            foreach (var opening in members)
            {
                // Out of scope: still a blocker for its neighbours above, never written.
                // Not logged as "skipped" - on a whole-model load that would be thousands
                // of lines of noise.
                if (_scopeIds is not null && !_scopeIds.Contains(opening.Id)) continue;

                if (opening.Skip)
                {
                    _skipped.Add($"{opening.Label()} -- carries {_settings.SkipMarker}");
                    continue;
                }

                plans.Add(SolveOpening(opening, members));
            }
        }

        return plans;
    }

    /// <summary>
    /// Two openings in one wall whose lining rectangles genuinely intersect are a
    /// modelling error, not a lining question. Reported once per pair.
    /// </summary>
    private void ReportGenuineIntersections(List<Opening> members)
    {
        for (var i = 0; i < members.Count; i++)
        {
            for (var j = i + 1; j < members.Count; j++)
            {
                var (a, b) = (members[i], members[j]);
                var du = Span.Overlap(a.USpan, b.USpan);
                var dz = Span.Overlap(a.ZSpan, b.ZSpan);

                if (du is { } u && dz is { } z && u.Length > _overlapWarn && z.Length > _overlapWarn)
                {
                    _warnings.Add($"{a.Label()} and {b.Label()} intersect by " +
                                  $"{Units.ToMm(u.Length):0} x {Units.ToMm(z.Length):0} mm in the same " +
                                  "wall -- lining results for both are unreliable");
                }
            }
        }
    }

    private LiningPlan SolveOpening(Opening opening, List<Opening> members)
    {
        // No lining parameters: nothing to compute or write on the lining side, but the
        // plan still exists so the door -> window Material and Lining YN rules can act.
        if (!opening.HasLiningParameters)
        {
            return new LiningPlan
            {
                Opening = opening,
                Desired = new Dictionary<LiningSide, int>(),
                Current = new Dictionary<LiningSide, int?>(),
                ChangeBefore = 0.0,
                ChangeAfter = 0.0,
                PredictedTotal = 0.0,
                Changed = false,
                Notes = ["no lining parameters - lining not computed; still blocks its neighbours"],
            };
        }

        var desired = new Dictionary<LiningSide, int>();
        var notes = new List<string>();
        var remnantTotal = 0.0;

        foreach (var side in Sides)
        {
            var (position, normal, span) = opening.Edge(side);

            if (span.Length <= _minBlock)
            {
                desired[side] = 1;
                continue;
            }

            var blocks = new List<Span>();

            foreach (var other in members)
            {
                if (other.Id == opening.Id) continue;

                // Under the office rule every neighbour that touches removes the lining on
                // that face, on both sides of the joint. Ownership is only consulted in
                // the fallback.
                if (!_settings.SymmetricUncheck && !Opening.Wins(other, opening)) continue;
                if (!other.HasLining && !_settings.LiningOffCanBlock) continue;

                // Dynamo parity: the graph dropped openings without lining parameters
                // before grouping, so they never blocked. They are still loaded here so a
                // touching door can drive their Window Material and Lining YN.
                if (!other.HasLiningParameters && !_settings.NoLiningParamsCanBlock) continue;

                double gap;
                Span? cross;

                if (side == LiningSide.Top)
                {
                    gap = other.ZBottom - position;
                    cross = Span.Overlap(span, other.USpan);
                }
                else
                {
                    gap = normal > 0 ? other.ULo - position : position - other.UHi;
                    cross = Span.Overlap(span, other.ZSpan);
                }

                // The neighbour has to sit just beyond this edge. A large negative gap
                // does not mean the two intersect - it only means the neighbour is
                // somewhere else along the wall, on the other side of this opening. Real
                // intersections are caught by the dedicated pass above.
                if (cross is not { } covered || gap > _gapTol || gap < -_touchSlack) continue;

                blocks.Add(covered);
                notes.Add($"{side} blocked {Units.ToMm(covered.Length):0} mm by {other.Id} " +
                          $"[{(other.Mark.Length > 0 ? other.Mark : "-")}] (gap {Units.ToMm(gap):0} mm)");
            }

            // Derive the blocked length from the complement so that blockers overlapping
            // each other are never counted twice.
            var remaining = span.Subtract(blocks);
            var blockedTotal = span.Length - remaining.Sum(s => s.Length);

            if (blockedTotal < _minBlock)
            {
                desired[side] = 1;
                continue;
            }

            desired[side] = 0;

            foreach (var piece in remaining)
            {
                if (piece.Length >= _minRemnant)
                {
                    remnantTotal += piece.Length;
                    notes.Add($"{side} remnant kept {Units.ToMm(piece.Length):0} mm");
                }
                else if (piece.Length > _minBlock)
                {
                    notes.Add($"{side} sliver {Units.ToMm(piece.Length):0} mm discarded");
                }
            }
        }

        var current = Sides.ToDictionary(s => s, s => Opening.Integer(opening.Instance, _settings.SideYn[s]));
        var currentChange = Opening.Number(opening.Instance, _settings.Change) ?? 0.0;

        // The family adds Lining Change unconditionally, so a value left from an earlier
        // edit inflates the quantity even with nothing blocked.
        if (remnantTotal == 0.0 && Math.Abs(currentChange) > Units.ToFeet(0.5))
            notes.Add($"stale Lining Change {Units.ToMm(currentChange):0} mm cleared");

        if (!opening.HasLining)
            notes.Add("Lining YN is off -- excluded from the lining schedule");

        var changed = desired.Any(d => current[d.Key] != d.Value) ||
                      Math.Abs(currentChange - remnantTotal) > Units.ToFeet(0.5);

        var keptLength = Sides
            .Where(s => desired.GetValueOrDefault(s, 1) == 1)
            .Sum(s => s == LiningSide.Top ? opening.Width : opening.Height);

        foreach (var message in opening.Warnings)
            _warnings.Add($"{opening.Label()}: {message}");

        return new LiningPlan
        {
            Opening = opening,
            Desired = desired,
            Current = current,
            ChangeBefore = currentChange,
            ChangeAfter = remnantTotal,
            PredictedTotal = keptLength + remnantTotal,
            Changed = changed,
            Notes = notes,
        };
    }

    // --------------------------------------------------- door -> window propagation

    /// <summary>
    /// A door decides the lining and material for the windows it physically touches. Only
    /// windows in direct contact with a door are affected; a window touching no door keeps
    /// whatever the modeller set.
    /// </summary>
    private void PropagateFromDoors(List<List<Opening>> groups, List<LiningPlan> plans)
    {
        var planById = plans.ToDictionary(p => p.Opening.Id);

        foreach (var members in groups)
        {
            var doors = members.Where(o => o.CategoryId == (long)BuiltInCategory.OST_Doors).ToList();
            if (doors.Count == 0) continue;

            foreach (var opening in members.Where(o => o.CategoryId == (long)BuiltInCategory.OST_Windows))
            {
                if (!planById.TryGetValue(opening.Id, out var plan)) continue;

                var touching = doors.Where(d => Opening.Touches(opening, d, _gapTol)).ToList();
                if (touching.Count == 0) continue;

                if (_settings.PropagateMasterFromDoors) PropagateMaster(opening, plan, touching);
                if (_settings.MaterialFromDoors) PropagateMaterial(opening, plan, touching);
            }
        }
    }

    private void PropagateMaster(Opening opening, LiningPlan plan, List<Opening> touching)
    {
        int? want = null;

        // Any touching door without lining wins: the window follows it out of the schedule.
        if (touching.Any(d => !d.HasLining)) want = 0;
        else if (_settings.MirrorMasterBothWays) want = 1;

        if (want is null || (opening.HasLining ? 1 : 0) == want) return;

        var driver = touching.FirstOrDefault(d => !d.HasLining) ?? touching[0];

        plan.MasterDesired = want;
        plan.Changed = true;
        plan.Notes.Add($"Lining YN -> {(want == 1 ? "on" : "off")} (touches door {driver.Id} " +
                       $"[{(driver.Mark.Length > 0 ? driver.Mark : "-")}], whose Lining YN is " +
                       $"{(driver.HasLining ? "on" : "off")})");
    }

    private void PropagateMaterial(Opening opening, LiningPlan plan, List<Opening> touching)
    {
        var codes = touching
            .Select(d => d.DoorMaterial.Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (codes.Count > 1)
        {
            // Two doors disagreeing is a modelling question, not something to resolve by
            // picking one.
            _warnings.Add($"{opening.Label()}: touches doors with different Door Material " +
                          $"({string.Join(", ", codes)}); Window Material left alone");
            return;
        }

        if (codes.Count == 0) return;

        var source = codes[0];
        var target = WindowMaterialFor(source);

        if (target is null)
        {
            _warnings.Add($"{opening.Label()}: door material '{source}' does not start with " +
                          $"'{_settings.DoorPrefix}', so no window code could be derived. Add it to " +
                          "MaterialOverrides if it is a special case.");
            return;
        }

        if (opening.WindowMaterial == target) return;

        var driver = touching.FirstOrDefault(d => d.DoorMaterial.Trim() == source) ?? touching[0];

        plan.MaterialDesired = target;
        plan.Changed = true;
        plan.Notes.Add($"Window Material '{(opening.WindowMaterial.Length > 0 ? opening.WindowMaterial : "(blank)")}' " +
                       $"-> '{target}' (door {driver.Id} [{(driver.Mark.Length > 0 ? driver.Mark : "-")}] is '{source}')");
    }

    /// <summary>'DDL' -> 'WDL'. Null when no code can be derived.</summary>
    private string? WindowMaterialFor(string doorCode)
    {
        var code = doorCode.Trim();
        if (code.Length == 0) return null;

        if (_settings.MaterialOverrides.TryGetValue(code, out var over)) return over;

        return code.StartsWith(_settings.DoorPrefix, StringComparison.OrdinalIgnoreCase)
            ? _settings.WindowPrefix + code[_settings.DoorPrefix.Length..]
            : null;
    }

    // ------------------------------------------------------------------- apply

    private int Apply(List<LiningPlan> plans, List<string> failed)
    {
        var toWrite = plans.Where(p => p.Changed).ToList();
        if (toWrite.Count == 0) return 0;

        var applied = 0;

        foreach (var plan in toWrite)
        {
            try
            {
                if (plan.Opening.HasLiningParameters)
                {
                    foreach (var (side, value) in plan.Desired)
                        Write(plan.Opening, _settings.SideYn[side], p => p.Set(value));

                    Write(plan.Opening, _settings.Change, p => p.Set(plan.ChangeAfter));
                }

                if (plan.MasterDesired is { } master)
                    Write(plan.Opening, _settings.Master, p => p.Set(master));

                if (plan.MaterialDesired is { } material)
                    Write(plan.Opening, _settings.WindowMaterial, p => p.Set(material));

                applied++;
            }
            catch (Exception ex)
            {
                failed.Add($"{plan.Opening.Label()} -- {ex.Message}");
            }
        }

        // Force the reporting parameters to recompute so the read-back is truthful.
        _doc.Regenerate();

        foreach (var plan in toWrite)
        {
            var (actual, _) = Opening.NumberOfAny(plan.Opening.Instance, _settings.Total);
            plan.ActualTotal = actual;
        }

        return applied;
    }

    /// <summary>
    /// A parameter that is absent or read-only used to be skipped in silence, so the run
    /// reported "written" while the model did not move. Now it is a warning.
    /// </summary>
    private void Write(Opening opening, string name, Action<Parameter> set)
    {
        var parameter = ParameterHelper.Find(opening.Instance, name);

        if (parameter is null)
        {
            _warnings.Add($"{opening.Label()}: parameter '{name}' not found - not written");
            return;
        }

        if (parameter.IsReadOnly)
        {
            _warnings.Add($"{opening.Label()}: parameter '{name}' is read-only " +
                          "(driven by a formula or a type parameter?) - not written");
            return;
        }

        set(parameter);
    }

    // ------------------------------------------------------------------ report

    private List<IReadOnlyList<string>> BuildRows(List<LiningPlan> plans)
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[]
            {
                "ElementId", "Mark", "Category", "Family", "Type", "HostWallId",
                "Lining YN before", "Lining YN after",
                "Material before", "Material after",
                "Top YN before", "Top YN after",
                "Left YN before", "Left YN after",
                "Right YN before", "Right YN after",
                "Lining Change before (mm)", "Lining Change after (mm)",
                "Predicted total (mm)", "Predicted schedule qty (Lbm)",
                "Actual total (mm)", "Changed", "Detail",
            },
        };

        foreach (var plan in plans)
        {
            var o = plan.Opening;
            var isWindow = o.CategoryId == (long)BuiltInCategory.OST_Windows;
            var materialBefore = isWindow ? o.WindowMaterial : o.DoorMaterial;

            rows.Add(new[]
            {
                o.Id.ToString(),
                o.Mark,
                o.Category,
                o.Family,
                o.TypeName,
                o.Axis.WallId.ToString(),
                o.HasLining ? "Yes" : "No",
                plan.MasterDesired is { } m ? (m == 1 ? "Yes" : "No") : (o.HasLining ? "Yes" : "No"),
                materialBefore,
                plan.MaterialDesired ?? materialBefore,
                Show(plan.Current.GetValueOrDefault(LiningSide.Top)), Want(plan, LiningSide.Top),
                Show(plan.Current.GetValueOrDefault(LiningSide.Left)), Want(plan, LiningSide.Left),
                Show(plan.Current.GetValueOrDefault(LiningSide.Right)), Want(plan, LiningSide.Right),
                Units.ToMm(plan.ChangeBefore).ToString("0.#"),
                Units.ToMm(plan.ChangeAfter).ToString("0.#"),
                Units.ToMm(plan.PredictedTotal).ToString("0.#"),
                (Units.ToMm(plan.PredictedTotal) / 1000.0).ToString("0.##"),
                plan.ActualTotal is { } a ? Units.ToMm(a).ToString("0.#") : string.Empty,
                plan.Changed ? "YES" : "no",
                string.Join(" | ", plan.Notes),
            });
        }

        return rows;

        static string Show(int? value) => value?.ToString() ?? string.Empty;

        // Blank rather than "0" for an opening that has no lining parameters at all -
        // "0" would read as "we turned this side off".
        static string Want(LiningPlan plan, LiningSide side) =>
            plan.Desired.TryGetValue(side, out var value) ? value.ToString() : string.Empty;
    }

    private List<string> BuildWarnings(List<LiningPlan> plans)
    {
        var warnings = new List<string>(_warnings);

        foreach (var plan in plans)
        {
            if (plan.ActualTotal is not { } actual) continue;

            if (Math.Abs(actual - plan.PredictedTotal) > Units.ToFeet(1.0))
            {
                warnings.Add($"{plan.Opening.Label()}: family reports {Units.ToMm(actual):0} mm but the " +
                             $"sides + Lining Change give {Units.ToMm(plan.PredictedTotal):0} mm -- check " +
                             "the 'Lining Length' formula in the family");
            }
        }

        return warnings;
    }

    /// <summary>
    /// The derived rectangles, so a wrong number can be diagnosed straight from the log
    /// instead of by another round of guessing.
    /// </summary>
    private List<string> BuildGeometry(List<List<Opening>> groups, List<LiningPlan> plans)
    {
        // Only the wall runs that actually produced a plan. On a whole-model run the full
        // dump would be thousands of lines; on a selection this is exactly the
        // neighbourhood you need to see to check a result.
        var planned = plans.Select(p => p.Opening.Id).ToHashSet();

        var relevant = groups
            .Where(g => g.Any(o => planned.Contains(o.Id)))
            .SelectMany(g => g)
            .OrderBy(o => o.Axis.WallId)
            .ThenBy(o => o.ULo);

        return [.. relevant.Select(o =>
            $"  {(planned.Contains(o.Id) ? "*" : " ")} {o.Id} [{(o.Mark.Length > 0 ? o.Mark : "-")}] " +
            $"{o.Category,-9} host {o.Axis.WallId}  " +
            $"u[{Units.ToMm(o.ULo):0}..{Units.ToMm(o.UHi):0}] " +
            $"z[{Units.ToMm(o.ZBottom):0}..{Units.ToMm(o.ZTop):0}]  " +
            $"w={Units.ToMm(o.Width):0} h={Units.ToMm(o.Height):0} " +
            $"leftAt={Units.ToMm(o.Edge(LiningSide.Left).Position):0} " +
            $"liningYN={(o.HasLining ? "on" : "OFF")}")];
    }

    private List<string> BuildSummary(
        List<LiningPlan> plans, List<List<Opening>> groups, bool apply, int applied,
        IReadOnlyList<string> failed, int pulledIn)
    {
        var changed = plans.Count(p => p.Changed);

        var neighbourhood = groups
            .Where(g => g.Any(o => plans.Any(p => p.Opening.Id == o.Id)))
            .Sum(g => g.Count);

        var summary = new List<string>
        {
            $"Lining clash resolver -- {(apply ? "APPLIED" : "DRY RUN -- nothing was modified")}",

            _scopeIds is null
                ? $"Whole model: {plans.Count} openings on {groups.Count} wall run(s)."
                : $"Selection: {plans.Count} opening(s) in scope, resolved against " +
                  $"{neighbourhood} opening(s) sharing their wall run(s). " +
                  $"{_openings.Count} loaded from the model as potential neighbours.",

            pulledIn > 0
                ? $"{pulledIn} window(s) touching a selected door were added to the scope, so the " +
                  "door can drive their Lining YN and Window Material."
                : string.Empty,

            $"{changed} need changes; {applied} written.",

            _openings.Count(o => !o.HasLiningParameters) is var noLining && noLining > 0
                ? $"{noLining} opening(s) carry no lining parameters. They still block their " +
                  "neighbours and can still receive Window Material / Lining YN from a touching door; " +
                  "only lining values are not written to them."
                : string.Empty,

            $"Settings: max clear gap {_settings.GapToleranceMm:0} mm, " +
            $"min remnant kept {_settings.MinRemnantMm:0} mm.",
        };

        if (_skipped.Count > 0)
        {
            summary.Add($"Skipped {_skipped.Count}: {string.Join("; ", _skipped.Take(10))}" +
                        (_skipped.Count > 10 ? " ..." : string.Empty));
        }

        if (failed.Count > 0)
            summary.Add($"Write failures {failed.Count}: {string.Join("; ", failed)}");

        if (_warnings.Count > 0)
            summary.Add($"{_warnings.Count} warning(s) -- see the log.");

        if (!apply && changed > 0)
            summary.Add("Run again and choose Apply to write these values.");

        // Conditional lines above are added as empty strings when they do not apply.
        return [.. summary.Where(line => line.Length > 0)];
    }

    private static string SafeId(Element element)
    {
        try { return element.Id.Value.ToString(); }
        catch { return "?"; }
    }
}
