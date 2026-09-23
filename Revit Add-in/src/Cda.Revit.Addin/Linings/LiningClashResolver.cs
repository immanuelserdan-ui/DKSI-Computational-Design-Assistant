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
///
/// The one thing it remembers between runs is what each touching door and window held for
/// "Lining YN" and material at the last apply - the only way to tell which of the two a user
/// edited. See <see cref="LiningSync"/>. The lining faces and "Lining Change" never use it.
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
        var contacts = DoorWindowContacts(groups);
        var pulledIn = ExpandScopeAlongContacts(contacts);
        var plans = Solve(groups);

        var syncs = Synchronise(contacts, plans);

        var applied = 0;
        var failed = new List<string>();
        if (apply)
        {
            applied = Apply(plans, failed, out var failedWrites);
            Record(syncs, plans, failedWrites);
        }

        return new LiningResult
        {
            Rows = BuildRows(plans),
            Warnings = BuildWarnings(plans),
            Summary = BuildSummary(plans, groups, apply, applied, failed, pulledIn, syncs),
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

    /// <summary>Every door/window pair in one wall run whose lining rectangles touch.</summary>
    private List<(Opening Door, Opening Window)> DoorWindowContacts(List<List<Opening>> groups)
    {
        var contacts = new List<(Opening, Opening)>();

        foreach (var members in groups)
        {
            var doors = members.Where(IsDoor).ToList();
            if (doors.Count == 0) continue;

            foreach (var window in members.Where(IsWindow))
            {
                foreach (var door in doors.Where(d => Opening.Touches(window, d, _gapTol)))
                    contacts.Add((door, window));
            }
        }

        return contacts;
    }

    /// <summary>
    /// Door and window drive each other's "Lining YN" and material, so a selection has to
    /// bring in what it is linked to, or the sync has nothing to write to and the edit
    /// appears to do nothing.
    ///
    /// Two-way: the whole chain of door/window contact reachable from the selection, since a
    /// door that follows a selected window then drives its OTHER windows. One-way (reverse
    /// off): only windows touching a selected door, as before. The count is reported either
    /// way so the extra writes are never silent.
    /// </summary>
    private int ExpandScopeAlongContacts(List<(Opening Door, Opening Window)> contacts)
    {
        if (_scopeIds is null) return 0;
        if (!_settings.PropagateMasterFromDoors && !_settings.MaterialFromDoors) return 0;

        var before = _scopeIds.Count;

        if (!_settings.ReverseFromWindows)
        {
            foreach (var (door, window) in contacts.Where(c => _scopeIds.Contains(c.Door.Id)).ToList())
                _scopeIds.Add(window.Id);

            return _scopeIds.Count - before;
        }

        bool grew;
        do
        {
            grew = false;
            foreach (var (door, window) in contacts)
            {
                if (_scopeIds.Contains(door.Id) == _scopeIds.Contains(window.Id)) continue;

                _scopeIds.Add(door.Id);
                _scopeIds.Add(window.Id);
                grew = true;
            }
        } while (grew);

        return _scopeIds.Count - before;
    }

    private static bool IsDoor(Opening o) => o.CategoryId == (long)BuiltInCategory.OST_Doors;

    private static bool IsWindow(Opening o) => o.CategoryId == (long)BuiltInCategory.OST_Windows;

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

    // ------------------------------------------------ door <-> window synchronisation

    private sealed record FieldSync(bool IsMaster, IReadOnlyList<SyncNode> Nodes, SyncResult Result);

    private bool TwoWay => _settings.ReverseFromWindows && LiningSyncStore.Available;

    private int _recorded;
    private int _recordFailures;
    private readonly List<string> _lockedByOthers = [];

    /// <summary>
    /// Door and window keep "Lining YN" and the material code in step, whichever of the two
    /// was edited. The decision is <see cref="LiningSync"/>'s; this gathers its inputs from
    /// the model and turns its answer into plan writes and notes.
    ///
    /// Only pairs in physical contact take part. A window touching no door keeps whatever the
    /// modeller set, and a door touching no window is never written by this.
    /// </summary>
    private List<FieldSync> Synchronise(List<(Opening Door, Opening Window)> contacts, List<LiningPlan> plans)
    {
        var syncs = new List<FieldSync>();
        if (!_settings.PropagateMasterFromDoors && !_settings.MaterialFromDoors) return syncs;

        var planById = plans.ToDictionary(p => p.Opening.Id);

        // Pairs with neither side in scope are someone else's run; their warnings are noise.
        var relevant = contacts
            .Where(c => planById.ContainsKey(c.Door.Id) || planById.ContainsKey(c.Window.Id))
            .ToList();
        if (relevant.Count == 0) return syncs;

        var openings = relevant
            .SelectMany(c => new[] { c.Door, c.Window })
            .DistinctBy(o => o.Id)
            .ToDictionary(o => o.Id);

        var pairs = relevant.Select(c => (c.Door.Id, c.Window.Id)).ToList();

        var stored = TwoWay
            ? openings.Values.ToDictionary(o => o.Id, o => LiningSyncStore.Read(o.Instance))
            : null;

        string Label(long id) => openings.TryGetValue(id, out var o) ? Short(o) : id.ToString();

        if (_settings.PropagateMasterFromDoors)
        {
            var nodes = openings.Values.Select(o => new SyncNode
            {
                Id = o.Id,
                IsDoor = IsDoor(o),
                Current = MasterValue(o.HasLining),
                Stored = stored?[o.Id].Lining,
                Tracked = !o.Skip && o.MasterOn is not null,
                Writable = planById.ContainsKey(o.Id),
            }).ToList();

            var result = LiningSync.Solve(nodes, pairs, new MasterLiningRule(_settings.MirrorMasterBothWays), Label);
            syncs.Add(new FieldSync(true, nodes, result));
        }

        if (_settings.MaterialFromDoors)
        {
            var nodes = openings.Values.Select(o => new SyncNode
            {
                Id = o.Id,
                IsDoor = IsDoor(o),
                Current = MaterialOf(o).Trim(),
                Stored = stored?[o.Id].Material,
                Tracked = !o.Skip && ParameterHelper.Find(o.Instance, MaterialParameter(o)) is not null,
                Writable = planById.ContainsKey(o.Id),
            }).ToList();

            var rule = new MaterialRule(_settings.DoorPrefix, _settings.WindowPrefix, _settings.MaterialOverrides);
            var result = LiningSync.Solve(nodes, pairs, rule, Label);
            syncs.Add(new FieldSync(false, nodes, result));
        }

        foreach (var sync in syncs)
        {
            _warnings.AddRange(sync.Result.Issues);

            // A door's value as the windows saw it this run: what it is being set to, if anything.
            var effective = sync.Nodes.ToDictionary(n => n.Id, n => n.Current);
            foreach (var write in sync.Result.Writes) effective[write.Id] = write.Value;

            foreach (var write in sync.Result.Writes)
            {
                if (!planById.TryGetValue(write.Id, out var plan)) continue;

                var target = openings[write.Id];
                var driver = openings[write.DriverId];

                plan.Changed = true;

                if (sync.IsMaster)
                {
                    plan.MasterDesired = write.Value == "1" ? 1 : 0;
                    plan.Notes.Add(write.Direction == SyncDirection.DoorToWindow
                        ? $"Lining YN -> {OnOff(write.Value)} (touches {Short(driver)}, whose Lining YN is " +
                          $"{OnOff(effective[driver.Id])})"
                        : $"Lining YN -> {OnOff(write.Value)} ({Short(driver)} touching it was changed to " +
                          $"{OnOff(driver.HasLining)} since the last run)");
                }
                else
                {
                    var before = MaterialOf(target);
                    plan.MaterialDesired = write.Value;
                    plan.Notes.Add(write.Direction == SyncDirection.DoorToWindow
                        ? $"{MaterialParameter(target)} '{Blank(before)}' -> '{write.Value}' " +
                          $"({Short(driver)} is '{effective[driver.Id]}')"
                        : $"{MaterialParameter(target)} '{Blank(before)}' -> '{write.Value}' " +
                          $"({Short(driver)} touching it was changed to '{MaterialOf(driver).Trim()}' since the last run)");
                }
            }
        }

        return syncs;

        static string OnOff(object value) => value is "1" or true ? "on" : "off";
        static string Blank(string value) => value.Length > 0 ? value : "(blank)";
    }

    /// <summary>
    /// After an apply, records what each synchronised opening now holds, so the next run can
    /// tell which side a user edits. Read back from the model rather than taken from the plan,
    /// so a write Revit refused is never recorded as done.
    ///
    /// NOT RECORDED: an edit still held back by a conflict, and both ends of a write that
    /// failed. Recording either would make the next run see "nothing changed" and quietly
    /// hand the pair to the door.
    /// </summary>
    private void Record(
        List<FieldSync> syncs,
        List<LiningPlan> plans,
        (HashSet<long> Master, HashSet<long> Material) failedWrites)
    {
        if (!TwoWay) return;

        var byId = plans.ToDictionary(p => p.Opening.Id, p => p.Opening);

        foreach (var sync in syncs)
        {
            var failed = sync.IsMaster ? failedWrites.Master : failedWrites.Material;

            var hold = new HashSet<long>(sync.Result.Unsettled);
            foreach (var write in sync.Result.Writes.Where(w => failed.Contains(w.Id)))
            {
                hold.Add(write.Id);
                hold.Add(write.DriverId);
            }

            foreach (var node in sync.Nodes)
            {
                if (!node.Tracked || !node.Writable || hold.Contains(node.Id)) continue;
                if (!byId.TryGetValue(node.Id, out var opening)) continue;

                // The record is a write like any other, and the first pass on a model makes it
                // on every touching door and window - so it needs the same ownership guard.
                if (!Worksharing.CanWrite(_doc, opening.Instance.Id)) continue;

                try
                {
                    var instance = opening.Instance;

                    if (sync.IsMaster)
                    {
                        if (Opening.Integer(instance, _settings.Master) is not { } now) continue;
                        if (node.Stored != MasterValue(now != 0)) _recorded++;
                        LiningSyncStore.Write(instance, MasterValue(now != 0), null);
                    }
                    else
                    {
                        var now = Opening.Text(instance, MaterialParameter(opening)).Trim();
                        if (node.Stored != now) _recorded++;
                        LiningSyncStore.Write(instance, null, now);
                    }
                }
                catch (Exception ex)
                {
                    _recordFailures++;
                    Log.Warn($"Lining sync record not written on {node.Id}: {ex.Message}");
                }
            }
        }
    }

    private static string MasterValue(bool on) => on ? "1" : "0";

    private string MaterialParameter(Opening o) => IsDoor(o) ? _settings.DoorMaterial : _settings.WindowMaterial;

    private static string MaterialOf(Opening o) => IsDoor(o) ? o.DoorMaterial : o.WindowMaterial;

    private static string Short(Opening o) =>
        $"{(IsDoor(o) ? "door" : "window")} {o.Id} [{(o.Mark.Length > 0 ? o.Mark : "-")}]";

    // ------------------------------------------------------------------- apply

    private int Apply(
        List<LiningPlan> plans,
        List<string> failed,
        out (HashSet<long> Master, HashSet<long> Material) failedWrites)
    {
        failedWrites = ([], []);

        var toWrite = plans.Where(p => p.Changed).ToList();
        if (toWrite.Count == 0) return 0;

        var applied = 0;

        foreach (var plan in toWrite)
        {
            // On a central model, one opening checked out by a colleague throws at commit and
            // rolls back the WHOLE pass - every other opening's correct values with it. Skip
            // it instead, and count it as a failed write so no sync record claims it settled.
            if (!Worksharing.CanWrite(_doc, plan.Opening.Instance.Id))
            {
                _lockedByOthers.Add(plan.Opening.Label());
                failedWrites.Master.Add(plan.Opening.Id);
                failedWrites.Material.Add(plan.Opening.Id);
                continue;
            }

            try
            {
                if (plan.Opening.HasLiningParameters)
                {
                    foreach (var (side, value) in plan.Desired)
                        Write(plan.Opening, _settings.SideYn[side], p => p.Set(value));

                    Write(plan.Opening, _settings.Change, p => p.Set(plan.ChangeAfter));
                }

                if (plan.MasterDesired is { } master &&
                    !Write(plan.Opening, _settings.Master, p => p.Set(master)))
                    failedWrites.Master.Add(plan.Opening.Id);

                if (plan.MaterialDesired is { } material &&
                    !Write(plan.Opening, MaterialParameter(plan.Opening), p => p.Set(material)))
                    failedWrites.Material.Add(plan.Opening.Id);

                applied++;
            }
            catch (Exception ex)
            {
                failed.Add($"{plan.Opening.Label()} -- {ex.Message}");
                failedWrites.Master.Add(plan.Opening.Id);
                failedWrites.Material.Add(plan.Opening.Id);
            }
        }

        // Force the reporting parameters to recompute so the read-back is truthful.
        _doc.Regenerate();

        // Skipped openings still hold their old values, so reading them back would raise a
        // false "family formula disagrees" warning for each one.
        HashSet<long> skipped = _lockedByOthers.Count == 0
            ? []
            : [.. toWrite.Where(p => !Worksharing.CanWrite(_doc, p.Opening.Instance.Id)).Select(p => p.Opening.Id)];

        foreach (var plan in toWrite.Where(p => !skipped.Contains(p.Opening.Id)))
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
    private bool Write(Opening opening, string name, Action<Parameter> set)
    {
        var parameter = ParameterHelper.Find(opening.Instance, name);

        if (parameter is null)
        {
            _warnings.Add($"{opening.Label()}: parameter '{name}' not found - not written");
            return false;
        }

        if (parameter.IsReadOnly)
        {
            _warnings.Add($"{opening.Label()}: parameter '{name}' is read-only " +
                          "(driven by a formula or a type parameter?) - not written");
            return false;
        }

        set(parameter);
        return true;
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
        IReadOnlyList<string> failed, int pulledIn, List<FieldSync> syncs)
    {
        var fromWindows = syncs.Sum(s => s.Result.Writes.Count(w => w.Direction == SyncDirection.WindowToDoor));
        var heldBack = syncs.SelectMany(s => s.Result.Unsettled).Distinct().Count();

        var syncLine = (_settings.PropagateMasterFromDoors || _settings.MaterialFromDoors) switch
        {
            false => string.Empty,
            true when !_settings.ReverseFromWindows =>
                "Lining YN / material sync: one-way, doors drive the windows they touch.",
            true when !LiningSyncStore.Available =>
                "Lining YN / material sync: the sync record is unavailable, so window edits cannot be " +
                "detected - doors drive the windows they touch, one-way, this run.",
            _ =>
                $"Lining YN / material sync (two-way): {fromWindows} door value(s) follow an edited " +
                $"window; {heldBack} edit(s) held back by a conflict - see warnings." +
                (apply
                    ? $" {_recorded} sync record(s) updated" +
                      (_recordFailures > 0 ? $", {_recordFailures} could not be written." : ".")
                    : " A dry run records nothing; edits are measured against the last APPLY."),
        };

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
                ? $"{pulledIn} opening(s) in door/window contact with the selection were added to " +
                  "the scope, so Lining YN and material stay in step across the pair."
                : string.Empty,

            $"{changed} need changes; {applied} written.",

            syncLine,

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

        if (_lockedByOthers.Count > 0)
        {
            summary.Add(
                $"{_lockedByOthers.Count} opening(s) are checked out by another user and were left " +
                $"alone; they are picked up once released: {string.Join("; ", _lockedByOthers.Take(10))}" +
                (_lockedByOthers.Count > 10 ? " ..." : string.Empty));
        }

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
