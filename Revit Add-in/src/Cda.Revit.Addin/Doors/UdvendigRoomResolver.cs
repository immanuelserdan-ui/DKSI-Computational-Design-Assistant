using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Doors;

public sealed class UdvendigSettings
{
    /// <summary>
    /// Any room whose NAME begins with this counts as outside. 'Udvendig', 'Udvendig 1'
    /// and 'Udvendig 3' all match. Case-insensitive.
    /// </summary>
    public string ExteriorPrefix { get; init; } = "Udvendig";

    public string NumFrom { get; init; } = "02-SCRP Num fr";
    public string NameFrom { get; init; } = "04-SCRP Nam fr";
    public string NumTo { get; init; } = "03-SCRP Num to";
    public string NameTo { get; init; } = "05-SCRP Nam to";

    /// <summary>Doors whose Comments contain this marker are left alone.</summary>
    public string SkipMarker { get; init; } = "#noroom-auto";

    /// <summary>
    /// The 'Rum nr' / 'Rum' columns ship as Revit's built-in From Room / To Room fields,
    /// which are derived from geometry and read-only - writing the SCRP parameters alone
    /// changes nothing on screen. This stage swaps those columns over.
    /// </summary>
    public bool RepointSchedules { get; init; } = true;

    /// <summary>Only schedules whose name contains one of these are touched.</summary>
    public IReadOnlyList<string> ScheduleNameContains { get; init; } = ["Door Casing"];
}

public sealed class DoorPlan
{
    public required Element Door { get; init; }
    public required long Id { get; init; }
    public required string Mark { get; init; }
    public required string TypeName { get; init; }

    /// <summary>What Revit reports: from number, from name, to number, to name.</summary>
    public required string[] Raw { get; init; }

    /// <summary>What will be written to the four SCRP parameters.</summary>
    public required string[] Desired { get; init; }

    public required string[] Current { get; init; }
    public required string Note { get; init; }

    public bool Changed => !Current.SequenceEqual(Desired);
}

public sealed class UdvendigResult
{
    public required IReadOnlyList<string> Summary { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Port of Resolve-Udvendig-Rooms_v1.0.dyn.
///
/// A door leading outside reports an exterior room - 'Udvendig 1', 'Udvendig 3' - on one
/// side. Wherever a side reads 'Udvendig...', it is replaced by the room on the OTHER
/// side of the same door.
///
/// This cannot be done in the schedule: the 'Rum nr' / 'Rum' columns are Revit's built-in
/// From Room / To Room fields with renamed headings, and those are derived from geometry
/// and read-only. So the corrected values go into the door's own SCRP shared parameters,
/// and the schedule columns are re-pointed at those.
///
/// The four parameters are always written in full, not only when a substitution happens,
/// because they become the schedule's source of truth. Re-running is therefore idempotent.
/// </summary>
public sealed class UdvendigRoomResolver
{
    /// <summary>(schedule field type, the room property it shows) -> replacement parameter.</summary>
    private static readonly (ScheduleFieldType FieldType, BuiltInParameter RoomBip, Func<UdvendigSettings, string> Target)[] FieldMap =
    [
        (ScheduleFieldType.FromRoom, BuiltInParameter.ROOM_NUMBER, s => s.NumFrom),
        (ScheduleFieldType.FromRoom, BuiltInParameter.ROOM_NAME, s => s.NameFrom),
        (ScheduleFieldType.ToRoom, BuiltInParameter.ROOM_NUMBER, s => s.NumTo),
        (ScheduleFieldType.ToRoom, BuiltInParameter.ROOM_NAME, s => s.NameTo),
    ];

    private readonly Document _doc;
    private readonly UdvendigSettings _settings;
    private readonly List<string> _warnings = [];
    private readonly List<string> _skipped = [];

    public UdvendigRoomResolver(Document doc, UdvendigSettings settings)
    {
        _doc = doc;
        _settings = settings;
    }

    public UdvendigResult Run(bool apply, IReadOnlyList<Element>? selection = null)
    {
        var doors = selection is { Count: > 0 }
            ? selection.OfType<FamilyInstance>().Cast<Element>().ToList()
            : new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .ToList();

        // Stage 1 - point the schedule columns at the SCRP parameters. Without this the
        // parameter writes below are invisible.
        var scheduleRows = new List<IReadOnlyList<string>>();
        if (_settings.RepointSchedules)
        {
            var sample = doors.FirstOrDefault(d => ParameterHelper.Find(d, _settings.NumFrom) is not null);
            scheduleRows = RepointSchedules(sample, apply);
        }

        // Stage 2 - compute the corrected room values.
        var plans = new List<DoorPlan>();
        foreach (var door in doors)
        {
            try
            {
                var plan = Plan(door);
                if (plan is not null) plans.Add(plan);
            }
            catch (Exception ex)
            {
                _skipped.Add($"{SafeId(door)} -- {ex.Message}");
            }
        }

        // Stage 3 - write.
        var applied = 0;
        var failed = new List<string>();

        if (apply)
        {
            foreach (var plan in plans.Where(p => p.Changed))
            {
                try
                {
                    var names = new[] { _settings.NumFrom, _settings.NameFrom, _settings.NumTo, _settings.NameTo };
                    for (var i = 0; i < names.Length; i++)
                    {
                        var parameter = ParameterHelper.Find(plan.Door, names[i]);
                        if (parameter is not null && !parameter.IsReadOnly) parameter.Set(plan.Desired[i]);
                    }
                    applied++;
                }
                catch (Exception ex)
                {
                    failed.Add($"{plan.Id} -- {ex.Message}");
                }
            }
        }

        return new UdvendigResult
        {
            Rows = BuildRows(plans, scheduleRows),
            Warnings = _warnings,
            Summary = BuildSummary(plans, scheduleRows, apply, applied, failed),
        };
    }

    // ------------------------------------------------------------------ stage 2

    private DoorPlan? Plan(Element door)
    {
        if (ParameterHelper.Find(door, _settings.NumFrom) is null)
        {
            _skipped.Add($"{door.Id.Value} -- no SCRP room parameters");
            return null;
        }

        var comments = BipString(door, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if (comments.Contains(_settings.SkipMarker, StringComparison.OrdinalIgnoreCase))
        {
            _skipped.Add($"{door.Id.Value} -- carries {_settings.SkipMarker}");
            return null;
        }

        var mark = BipString(door, BuiltInParameter.ALL_MODEL_MARK);
        var typeName = "?";
        if (door is FamilyInstance { Symbol: { } symbol })
            typeName = $"{symbol.Family.Name}: {symbol.Name}";

        var (fromNum, fromName) = RoomIdentity(RoomOf(door, from: true));
        var (toNum, toName) = RoomIdentity(RoomOf(door, from: false));

        var extFrom = IsExterior(fromName);
        var extTo = IsExterior(toName);

        var outFrom = (Num: fromNum, Name: fromName);
        var outTo = (Num: toNum, Name: toName);
        var note = string.Empty;

        if (extFrom && extTo)
        {
            note = "both sides exterior -- left unchanged";
            _warnings.Add($"{door.Id.Value} [{(mark.Length > 0 ? mark : "-")}]: both sides are " +
                          $"'{_settings.ExteriorPrefix}' rooms ({fromName} / {toName}); no interior counterpart exists");
        }
        else if (extTo)
        {
            if (toNum.Length > 0 || toName.Length > 0)
            {
                outTo = (fromNum, fromName);
                note = $"TO '{toName}' ({toNum}) -> '{fromName}' ({fromNum})";
            }

            if (fromNum.Length == 0 && fromName.Length == 0)
            {
                _warnings.Add($"{door.Id.Value} [{(mark.Length > 0 ? mark : "-")}]: TO is '{toName}' " +
                              "but there is no room on the other side to copy");
                outTo = (toNum, toName);
                note = "no counterpart -- left unchanged";
            }
        }
        else if (extFrom)
        {
            if (fromNum.Length > 0 || fromName.Length > 0)
            {
                outFrom = (toNum, toName);
                note = $"FROM '{fromName}' ({fromNum}) -> '{toName}' ({toNum})";
            }

            if (toNum.Length == 0 && toName.Length == 0)
            {
                _warnings.Add($"{door.Id.Value} [{(mark.Length > 0 ? mark : "-")}]: FROM is '{fromName}' " +
                              "but there is no room on the other side to copy");
                outFrom = (fromNum, fromName);
                note = "no counterpart -- left unchanged";
            }
        }

        if (fromNum.Length == 0 && fromName.Length == 0)
            _warnings.Add($"{door.Id.Value} [{(mark.Length > 0 ? mark : "-")}]: no FROM room");
        if (toNum.Length == 0 && toName.Length == 0)
            _warnings.Add($"{door.Id.Value} [{(mark.Length > 0 ? mark : "-")}]: no TO room");

        var current = new[]
        {
            ParameterHelper.Find(door, _settings.NumFrom)?.AsString() ?? string.Empty,
            ParameterHelper.Find(door, _settings.NameFrom)?.AsString() ?? string.Empty,
            ParameterHelper.Find(door, _settings.NumTo)?.AsString() ?? string.Empty,
            ParameterHelper.Find(door, _settings.NameTo)?.AsString() ?? string.Empty,
        };

        return new DoorPlan
        {
            Door = door,
            Id = door.Id.Value,
            Mark = mark,
            TypeName = typeName,
            Raw = [fromNum, fromName, toNum, toName],
            Desired = [outFrom.Num, outFrom.Name, outTo.Num, outTo.Name],
            Current = current,
            Note = note,
        };
    }

    /// <summary>
    /// The From or To room of a door, resolved in the door's own phase. Revit exposes
    /// these as phase-dependent accessors; the bare property is the single-phase fallback.
    /// </summary>
    private Room? RoomOf(Element door, bool from)
    {
        if (door is not FamilyInstance instance) return null;

        Phase? phase = null;
        var parameter = instance.get_Parameter(BuiltInParameter.PHASE_CREATED);
        if (parameter is not null) phase = _doc.GetElement(parameter.AsElementId()) as Phase;

        if (phase is not null)
        {
            try { return from ? instance.get_FromRoom(phase) : instance.get_ToRoom(phase); }
            catch { /* fall back to the bare property */ }
        }

        try { return from ? instance.FromRoom : instance.ToRoom; }
        catch { return null; }
    }

    private (string Number, string Name) RoomIdentity(Room? room) =>
        room is null
            ? (string.Empty, string.Empty)
            : (BipString(room, BuiltInParameter.ROOM_NUMBER), BipString(room, BuiltInParameter.ROOM_NAME));

    private bool IsExterior(string name) =>
        name.Trim().StartsWith(_settings.ExteriorPrefix, StringComparison.OrdinalIgnoreCase);

    private static string BipString(Element element, BuiltInParameter bip)
    {
        try
        {
            var parameter = element.get_Parameter(bip);
            if (parameter is null || !parameter.HasValue || parameter.StorageType != StorageType.String)
                return string.Empty;

            return parameter.AsString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeId(Element element)
    {
        try { return element.Id.Value.ToString(); }
        catch { return "?"; }
    }

    // ------------------------------------------------------------------ stage 1

    /// <summary>
    /// Swaps built-in From/To Room columns over to the SCRP shared parameters, preserving
    /// heading, width, alignment and visibility.
    ///
    /// Idempotent: a column already pointing at a SCRP parameter is not a From/To Room
    /// field, so it simply is not matched. Never throws - a schedule that cannot be
    /// converted is reported and skipped.
    /// </summary>
    private List<IReadOnlyList<string>> RepointSchedules(Element? sampleDoor, bool apply)
    {
        var rows = new List<IReadOnlyList<string>>();

        if (sampleDoor is null)
        {
            _warnings.Add("no door available to read the SCRP parameter ids from");
            return rows;
        }

        // ElementId of each SCRP parameter, taken from a real door instance.
        var wanted = new Dictionary<(ScheduleFieldType, BuiltInParameter), (ElementId Id, string Name)>();
        foreach (var (fieldType, roomBip, target) in FieldMap)
        {
            var name = target(_settings);
            var parameter = ParameterHelper.Find(sampleDoor, name);
            if (parameter is null)
            {
                _warnings.Add($"parameter '{name}' not found on door {sampleDoor.Id.Value}");
                continue;
            }

            wanted[(fieldType, roomBip)] = (parameter.Id, name);
        }

        foreach (var schedule in new FilteredElementCollector(_doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
        {
            try
            {
                if (schedule.IsTemplate) continue;

                var scheduleName = schedule.Name;
                if (!_settings.ScheduleNameContains.Any(t =>
                        scheduleName.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var definition = schedule.Definition;

                // Reverse order: inserting at i and removing i+1 keeps the field count
                // stable, so lower indices stay valid either way.
                for (var index = definition.GetFieldCount() - 1; index >= 0; index--)
                {
                    var field = definition.GetField(index);
                    var fieldType = field.FieldType;

                    if (fieldType is not (ScheduleFieldType.FromRoom or ScheduleFieldType.ToRoom)) continue;

                    (ScheduleFieldType, BuiltInParameter)? key = null;
                    foreach (var (mapType, roomBip, _) in FieldMap)
                    {
                        if (mapType != fieldType) continue;
                        if (field.ParameterId == new ElementId(roomBip))
                        {
                            key = (fieldType, roomBip);
                            break;
                        }
                    }

                    if (key is null || !wanted.TryGetValue(key.Value, out var replacement))
                    {
                        rows.Add(new[] { scheduleName, field.GetName(), fieldType.ToString(), "-",
                                         "no mapping for this column" });
                        continue;
                    }

                    // Refuse to touch a column a filter or sort rule depends on - dropping
                    // those silently would change which rows appear.
                    if (IsUsedByFilterOrSort(definition, field))
                    {
                        rows.Add(new[] { scheduleName, field.GetName(), fieldType.ToString(), replacement.Name,
                                         "SKIPPED - a filter or sort rule uses it" });
                        _warnings.Add($"{scheduleName}: column '{field.GetName()}' drives a filter or sort " +
                                      "rule, so it was left alone. Clear that rule, then re-run.");
                        continue;
                    }

                    var schedulable = definition.GetSchedulableFields()
                        .FirstOrDefault(c => c.ParameterId == replacement.Id);

                    if (schedulable is null)
                    {
                        rows.Add(new[] { scheduleName, field.GetName(), fieldType.ToString(), replacement.Name,
                                         "SKIPPED - parameter not schedulable here" });
                        continue;
                    }

                    if (!apply)
                    {
                        rows.Add(new[] { scheduleName, field.GetName(), fieldType.ToString(), replacement.Name,
                                         "would swap" });
                        continue;
                    }

                    var heading = field.ColumnHeading;
                    var width = field.GridColumnWidth;
                    var alignment = field.HorizontalAlignment;
                    var hidden = field.IsHidden;

                    ScheduleField newField;
                    try
                    {
                        newField = definition.InsertField(schedulable, index);
                    }
                    catch (Exception ex)
                    {
                        rows.Add(new[] { scheduleName, heading, fieldType.ToString(), replacement.Name,
                                         $"FAILED to insert: {ex.Message}" });
                        continue;
                    }

                    TrySet(() => newField.ColumnHeading = heading);
                    TrySet(() => newField.GridColumnWidth = width);
                    TrySet(() => newField.HorizontalAlignment = alignment);
                    TrySet(() => newField.IsHidden = hidden);

                    try
                    {
                        definition.RemoveField(index + 1);
                        rows.Add(new[] { scheduleName, heading, fieldType.ToString(), replacement.Name, "swapped" });
                    }
                    catch (Exception ex)
                    {
                        rows.Add(new[] { scheduleName, heading, fieldType.ToString(), replacement.Name,
                                         $"FAILED to remove old column: {ex.Message}" });
                        _warnings.Add($"{scheduleName}: '{heading}' now appears twice -- delete the built-in " +
                                      "one by hand.");
                    }
                }
            }
            catch (Exception ex)
            {
                rows.Add(new[] { SafeName(schedule), "-", "-", "-", $"schedule skipped: {ex.Message}" });
            }
        }

        return rows;
    }

    private static bool IsUsedByFilterOrSort(ScheduleDefinition definition, ScheduleField field)
    {
        try
        {
            if (definition.GetFilters().Any(f => f.FieldId == field.FieldId)) return true;
            if (definition.GetSortGroupFields().Any(s => s.FieldId == field.FieldId)) return true;
        }
        catch
        {
            // If the rules cannot be read, assume the column is safe - the Python did the
            // same, and refusing every column would make the tool useless.
        }

        return false;
    }

    private static void TrySet(Action action)
    {
        try { action(); }
        catch { /* a property the field type does not support */ }
    }

    private static string SafeName(Element element)
    {
        try { return element.Name; }
        catch { return "?"; }
    }

    // ------------------------------------------------------------------- output

    private static List<IReadOnlyList<string>> BuildRows(
        IReadOnlyList<DoorPlan> plans,
        IReadOnlyList<IReadOnlyList<string>> scheduleRows)
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[]
            {
                "ElementId", "Mark", "Type",
                "Revit FROM nr", "Revit FROM name", "Revit TO nr", "Revit TO name",
                "Written FROM nr", "Written FROM name", "Written TO nr", "Written TO name",
                "Changed", "Detail",
            },
        };

        foreach (var plan in plans)
        {
            rows.Add(new[]
            {
                plan.Id.ToString(), plan.Mark, plan.TypeName,
                plan.Raw[0], plan.Raw[1], plan.Raw[2], plan.Raw[3],
                plan.Desired[0], plan.Desired[1], plan.Desired[2], plan.Desired[3],
                plan.Changed ? "YES" : "no", plan.Note,
            });
        }

        if (scheduleRows.Count > 0)
        {
            rows.Add(Array.Empty<string>());
            rows.Add(new[] { "Schedule", "Column", "Was", "Now", "Result" });
            rows.AddRange(scheduleRows);
        }

        return rows;
    }

    private List<string> BuildSummary(
        IReadOnlyList<DoorPlan> plans,
        IReadOnlyList<IReadOnlyList<string>> scheduleRows,
        bool apply,
        int applied,
        IReadOnlyList<string> failed)
    {
        var swapped = scheduleRows.Count(r => r.Count > 4 && r[4] == "swapped");
        var substituted = plans.Count(p => p.Note.Contains("->"));

        var summary = new List<string>
        {
            $"Udvendig room resolver -- {(apply ? "APPLIED" : "DRY RUN -- nothing was modified")}",
            $"Schedules: {swapped} column(s) re-pointed to the SCRP parameters ({scheduleRows.Count} examined).",
            $"Examined {plans.Count} doors.",
            $"{substituted} had an '{_settings.ExteriorPrefix}' side substituted.",
            $"{plans.Count(p => p.Changed)} need parameter writes; {applied} written.",
        };

        if (_skipped.Count > 0)
            summary.Add($"Skipped {_skipped.Count}: {string.Join("; ", _skipped.Take(10))}");

        if (failed.Count > 0)
            summary.Add($"Write failures: {string.Join("; ", failed)}");

        if (_warnings.Count > 0)
            summary.Add($"{_warnings.Count} warning(s) -- see the log.");

        if (!apply && plans.Any(p => p.Changed))
            summary.Add("Run again and choose Apply to write these values.");

        if (_settings.RepointSchedules && apply && swapped == 0)
        {
            summary.Add("No columns were re-pointed. Either it was already done on a previous run, or no " +
                        "From/To Room columns were found -- the table in the report lists what was there.");
        }

        return summary;
    }
}
