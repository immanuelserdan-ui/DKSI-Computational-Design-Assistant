namespace Cda.Revit.Addin.Linings;

/// <summary>
/// Two-way door/window synchronisation of one field (master "Lining YN", or the material
/// code), decided from what changed since the last applied run.
///
/// NO REVIT TYPES, DELIBERATELY: compiled straight into Cda.Linings.Tests.
///
/// WHY IT NEEDS A MEMORY
///   Comparing a door with its window only says that they disagree, never which one the user
///   edited. Each opening therefore carries the value the tool last settled on
///   (<see cref="SyncNode.Stored"/>); whichever side no longer matches its own record is the
///   edit, and the other side follows it.
///
/// THE RULES
///   * Window edited, door not   -> the door follows the window (new).
///   * Door edited or neither    -> windows follow their doors, exactly as before.
///   * Both edited, disagreeing  -> conflict: nothing written to either side, and neither is
///     marked settled, so the warning repeats on every run until a person makes them agree.
///     Settling a conflict silently would let the next run resolve it door-wins behind the
///     user's back.
///   * Nothing recorded yet (first run, or a pasted copy) counts as "not edited", so an
///     existing model behaves exactly as it did before the first apply.
///
/// ONE PASS, NOT TWO RUNS
///   A door moved by a window then drives its OTHER windows forward in the same pass. Leaving
///   them for the next run would make the tool's answer depend on how many times it was run.
/// </summary>
public static class LiningSync
{
    public static SyncResult Solve(
        IReadOnlyList<SyncNode> nodes,
        IEnumerable<(long Door, long Window)> contacts,
        SyncRule rule,
        Func<long, string>? label = null)
    {
        label ??= id => id.ToString();

        var byId = nodes.ToDictionary(n => n.Id);
        var doorsOf = new Dictionary<long, List<long>>();
        var windowsOf = new Dictionary<long, List<long>>();

        foreach (var (door, window) in contacts.Distinct())
        {
            if (!byId.TryGetValue(door, out var d) || !d.IsDoor) continue;
            if (!byId.TryGetValue(window, out var w) || w.IsDoor) continue;

            Add(doorsOf, window, door);
            Add(windowsOf, door, window);
        }

        var result = new SyncResult();
        var effective = nodes.Where(n => n.IsDoor).ToDictionary(n => n.Id, n => n.Current);

        // -- window -> door, phase 1: find every conflict ---------------------------------
        // A window caught in a conflict at ANY door pushes to NO door: "write nothing to either
        // side" has to hold for the whole edit, not just the pair that disagreed. Withdrawing a
        // push can only remove disagreements, never create one, so one detection pass is exact.
        var pushes = new Dictionary<long, string>();

        foreach (var (windowId, doorIds) in doorsOf.OrderBy(p => p.Key))
        {
            var window = byId[windowId];
            if (!window.Changed) continue;

            var outcome = rule.Reverse(window.Current);

            if (outcome.Problem is not null)
            {
                result.Conflict($"{label(window.Id)}: {outcome.Problem}", window);
                continue;
            }

            if (outcome.Value is not { } target) continue;

            var blocked = false;

            foreach (var door in doorIds.Order().Select(id => byId[id]))
            {
                if (door.Changed)
                {
                    // Both edited. Agreement is fine; anything else is the user's to settle.
                    if (door.Current == target) continue;

                    result.Conflict(
                        $"{label(window.Id)} was changed to '{window.Current}' and {label(door.Id)} " +
                        $"was changed to '{door.Current}' since the last run; {rule.Field} left " +
                        "alone on both until they agree",
                        window, door);
                    blocked = true;
                }
                else if (!door.Tracked || !door.Writable)
                {
                    if (door.Current == target) continue;

                    result.Conflict(
                        $"{label(door.Id)} cannot follow {label(window.Id)}: the door has no writable " +
                        $"{rule.Field} (missing parameter, marked to be left alone, or outside the " +
                        "selection)",
                        window);
                    blocked = true;
                }
            }

            if (!blocked) pushes[windowId] = target;
        }

        foreach (var (doorId, windowIds) in windowsOf.OrderBy(p => p.Key))
        {
            var door = byId[doorId];
            if (door.Changed) continue;

            var incoming = windowIds.Order().Where(pushes.ContainsKey).ToList();
            if (incoming.Select(id => pushes[id]).Distinct(StringComparer.Ordinal).Count() <= 1) continue;

            result.Conflict(
                $"{label(door.Id)}: touching windows were changed to different {rule.Field} " +
                $"({string.Join(", ", incoming.Select(id => $"{label(id)} '{byId[id].Current}'"))}); " +
                "door left alone",
                [.. incoming.Select(id => byId[id])]);
        }

        foreach (var id in result.Unsettled) pushes.Remove(id);

        // -- window -> door, phase 2: write -----------------------------------------------
        foreach (var (doorId, windowIds) in windowsOf.OrderBy(p => p.Key))
        {
            var door = byId[doorId];
            if (door.Changed || !door.Tracked || !door.Writable) continue;

            if (windowIds.Order().Where(pushes.ContainsKey).Select(id => (long?)id).FirstOrDefault()
                is not { } driver) continue;

            var target = pushes[driver];
            effective[doorId] = target;

            if (target != door.Current)
                result.Writes.Add(new SyncWrite(doorId, target, SyncDirection.WindowToDoor, driver));
        }

        // -- door -> window ---------------------------------------------------------------
        foreach (var (windowId, doorIds) in doorsOf.OrderBy(p => p.Key))
        {
            var window = byId[windowId];

            // An edited window keeps its own value: it is the source this run, not a target.
            if (window.Changed || !window.Writable) continue;

            var doors = doorIds.Order().Select(id => (Id: id, Value: effective[id])).ToList();
            var outcome = rule.Forward(doors);

            if (outcome.Problem is not null)
            {
                result.Issues.Add($"{label(window.Id)}: {outcome.Problem}");
                continue;
            }

            if (outcome.Value is null || outcome.Value == window.Current) continue;

            result.Writes.Add(new SyncWrite(
                windowId, outcome.Value, SyncDirection.DoorToWindow, outcome.DriverId ?? doors[0].Id));
        }

        return result;
    }

    private static void Add(Dictionary<long, List<long>> map, long key, long value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        if (!list.Contains(value)) list.Add(value);
    }
}

/// <summary>One door or window, for one synchronised field.</summary>
public sealed class SyncNode
{
    public required long Id { get; init; }
    public required bool IsDoor { get; init; }

    /// <summary>The value now in the model, normalised the way the rule compares it.</summary>
    public required string Current { get; init; }

    /// <summary>The value the tool last settled on, or null if it never recorded one.</summary>
    public string? Stored { get; init; }

    /// <summary>
    /// False when the parameter is missing or the opening is marked to be left alone. An
    /// untracked opening never counts as edited and never receives a window's value.
    /// </summary>
    public bool Tracked { get; init; } = true;

    /// <summary>False outside the run's scope. Never written.</summary>
    public bool Writable { get; init; } = true;

    public bool Changed => Tracked && Stored is not null && !string.Equals(Current, Stored, StringComparison.Ordinal);
}

public enum SyncDirection
{
    DoorToWindow,
    WindowToDoor,
}

public sealed record SyncWrite(long Id, string Value, SyncDirection Direction, long DriverId);

/// <param name="Value">What the other side should become; null = no opinion, write nothing.</param>
/// <param name="Problem">Set when no value can be derived and a person needs to look.</param>
public readonly record struct SyncOutcome(string? Value, string? Problem = null, long? DriverId = null);

public sealed class SyncResult
{
    public List<SyncWrite> Writes { get; } = [];

    /// <summary>Warnings, conflicts included.</summary>
    public List<string> Issues { get; } = [];

    /// <summary>
    /// Openings whose edit is still pending because it could not be carried through. They are
    /// not re-recorded, so the edit is still seen - and still warned about - next run.
    /// </summary>
    public HashSet<long> Unsettled { get; } = [];

    internal void Conflict(string message, params SyncNode[] involved)
    {
        Issues.Add(message);
        foreach (var node in involved.Where(n => n.Changed)) Unsettled.Add(node.Id);
    }
}

/// <summary>How one field maps between a door and the windows it touches.</summary>
public abstract class SyncRule
{
    public abstract string Field { get; }

    /// <summary>What a window should be, given the doors it touches.</summary>
    public abstract SyncOutcome Forward(IReadOnlyList<(long Id, string Value)> doors);

    /// <summary>What a door should be, given an edited window it touches.</summary>
    public abstract SyncOutcome Reverse(string windowValue);
}

/// <summary>
/// Master "Lining YN", as "1"/"0". A door with the parameter missing counts as "1", the
/// same as <c>Opening.HasLining</c>.
/// </summary>
public sealed class MasterLiningRule(bool mirrorBothWays) : SyncRule
{
    public override string Field => "Lining YN";

    public override SyncOutcome Forward(IReadOnlyList<(long Id, string Value)> doors)
    {
        // Any touching door without lining wins: the window follows it out of the schedule.
        var off = doors.FirstOrDefault(d => d.Value == "0");
        if (off != default) return new SyncOutcome("0", DriverId: off.Id);

        return mirrorBothWays && doors.Count > 0
            ? new SyncOutcome("1", DriverId: doors[0].Id)
            : new SyncOutcome(null);
    }

    public override SyncOutcome Reverse(string windowValue) => windowValue switch
    {
        "0" => new SyncOutcome("0"),
        "1" when mirrorBothWays => new SyncOutcome("1"),
        _ => new SyncOutcome(null),
    };
}

/// <summary>
/// Material code. The first letter names the element (D door, W window) and the rest is the
/// material, so DDL &lt;-&gt; WDL. Overrides are keyed on the door code and are used in both
/// directions.
/// </summary>
public sealed class MaterialRule(
    string doorPrefix,
    string windowPrefix,
    IReadOnlyDictionary<string, string> overrides) : SyncRule
{
    public override string Field => "material";

    public override SyncOutcome Forward(IReadOnlyList<(long Id, string Value)> doors)
    {
        var codes = doors
            .Select(d => d.Value.Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (codes.Count > 1)
        {
            // Two doors disagreeing is a modelling question, not something to resolve by
            // picking one.
            return new SyncOutcome(null,
                $"touches doors with different Door Material ({string.Join(", ", codes)}); " +
                "Window Material left alone");
        }

        if (codes.Count == 0) return new SyncOutcome(null);

        var source = codes[0];
        var driver = doors.First(d => d.Value.Trim() == source).Id;
        var target = ToWindow(source);

        return target is null
            ? new SyncOutcome(null,
                $"door material '{source}' does not start with '{doorPrefix}', so no window code " +
                "could be derived. Add it to MaterialOverrides if it is a special case.")
            : new SyncOutcome(target, DriverId: driver);
    }

    public override SyncOutcome Reverse(string windowValue)
    {
        var code = windowValue.Trim();

        // Blanking a window's material is not an instruction to blank the door. It hands the
        // window back to its door on the next run.
        if (code.Length == 0) return new SyncOutcome(null);

        var target = ToDoor(code);
        return target is null
            ? new SyncOutcome(null,
                $"window material '{code}' does not start with '{windowPrefix}', so no door code " +
                "could be derived; the door was not changed. Add it to MaterialOverrides if it is " +
                "a special case.")
            : new SyncOutcome(target);
    }

    public string? ToWindow(string doorCode)
    {
        var code = doorCode.Trim();
        if (code.Length == 0) return null;

        if (overrides.TryGetValue(code, out var over)) return over;

        return code.StartsWith(doorPrefix, StringComparison.OrdinalIgnoreCase)
            ? windowPrefix + code[doorPrefix.Length..]
            : null;
    }

    public string? ToDoor(string windowCode)
    {
        var code = windowCode.Trim();
        if (code.Length == 0) return null;

        foreach (var (door, window) in overrides)
        {
            if (string.Equals(window, code, StringComparison.OrdinalIgnoreCase)) return door;
        }

        return code.StartsWith(windowPrefix, StringComparison.OrdinalIgnoreCase)
            ? doorPrefix + code[windowPrefix.Length..]
            : null;
    }
}
