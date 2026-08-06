using Autodesk.Revit.DB;
using Cda.Revit.Addin.Finishes;

namespace Cda.Revit.Addin.Schedules;

public sealed class TakeoffResult
{
    public required IReadOnlyList<string> Report { get; init; }

    /// <summary>Schedules created by this run. Empty when they all already existed.</summary>
    public required IReadOnlyList<ViewSchedule> Created { get; init; }

    public required IReadOnlyList<string> Problems { get; init; }
}

/// <summary>
/// Builds the material takeoff that can actually SHOW a ceiling finish measured off
/// something that is not a ceiling.
///
/// THE PROBLEM THIS SOLVES
///   A Ceiling Material Takeoff schedules the Ceilings category. In a model with no
///   ceiling elements it has no rows - not because the numbers are missing, but because
///   the category is empty. Once the fallback chain measures a room's ceiling off the
///   slab above or the roof, that area is written onto the FLOOR or ROOF element it came
///   from, because no ceiling element exists to carry it. A schedule limited to Ceilings
///   can never show it.
///
///   The multi-category takeoff spans Ceilings, Floors and Roofs, so whichever tier of
///   the chain answered, the row appears. Filtering on "Ceiling Finish Area &gt; 0" keeps
///   floors that are only ever floors out of it.
///
/// Multi-category is attempted first and per-category schedules are the fallback, because
/// whether OST_MultiCategory is accepted for a material takeoff is a property of the
/// installed Revit rather than something worth asserting from source.
/// </summary>
public sealed class CeilingTakeoffBuilder
{
    private readonly Document _doc;
    private readonly FinishSettings _settings;

    private readonly List<string> _report = [];
    private readonly List<string> _problems = [];
    private readonly List<ViewSchedule> _created = [];

    public CeilingTakeoffBuilder(Document doc, FinishSettings settings)
    {
        _doc = doc;
        _settings = settings;
    }

    /// <summary>
    /// Named so it sorts beside the schedules it replaces and says what it is. Existence of
    /// this exact name is what makes the command idempotent - run it twice and the second
    /// run reports rather than making "... 2".
    /// </summary>
    public string ScheduleName => $"DKSI {_settings.CeilingParameter} (all sources)";

    /// <summary>The caller owns the transaction.</summary>
    public TakeoffResult Run()
    {
        if (FindSchedule(ScheduleName) is { } existing)
        {
            _report.Add($"'{existing.Name}' already exists; left alone. Delete it and run " +
                        "again to rebuild it from scratch.");

            return new TakeoffResult { Report = _report, Created = _created, Problems = _problems };
        }

        // InvalidElementId, not an OST_MultiCategory enum member - there isn't one. A
        // multi-category schedule is expressed as a schedule with no category, which is
        // also why it can only show parameters bound to more than one category.
        var multi = TryBuild(ElementId.InvalidElementId, ScheduleName);

        if (multi is not null)
        {
            _created.Add(multi);
            _report.Add($"Created '{multi.Name}' - a multi-category material takeoff over " +
                        "Ceilings, Floors and Roofs.");
        }
        else
        {
            _report.Add("This Revit would not create a multi-category material takeoff, so one " +
                        "schedule per category was created instead. They add up to the same " +
                        "numbers; they just cannot be totalled in a single view.");

            foreach (var (source, category) in FinishSettings.CeilingFallbackTiers)
            {
                var name = $"{ScheduleName} - {source}";
                if (FindSchedule(name) is not null) continue;

                var single = TryBuild(new ElementId(category), name);

                if (single is not null)
                {
                    _created.Add(single);
                    _report.Add($"Created '{single.Name}'.");
                }
                else
                {
                    _problems.Add($"Could not create a material takeoff for {source}.");
                }
            }
        }

        return new TakeoffResult { Report = _report, Created = _created, Problems = _problems };
    }

    // ------------------------------------------------------------------ building

    private ViewSchedule? TryBuild(ElementId categoryId, string name)
    {
        ViewSchedule schedule;
        try
        {
            schedule = ViewSchedule.CreateMaterialTakeoff(_doc, categoryId);
        }
        catch (Exception ex)
        {
            _problems.Add($"CreateMaterialTakeoff failed for '{name}': {ex.Message}");
            return null;
        }

        try
        {
            schedule.Name = UniqueName(name);
        }
        catch (Exception ex)
        {
            // A name clash must not cost the schedule; Revit's default name is still usable.
            _problems.Add($"Could not name the schedule '{name}': {ex.Message}");
        }

        AddFields(schedule);
        return schedule;
    }

    /// <summary>
    /// Fields are resolved by NAME from what the schedule says it can show, never by
    /// parameter id. A material takeoff exposes "Material: Area" as a schedulable field
    /// with no BuiltInParameter behind it, and the shared parameter's id differs per model,
    /// so name matching is the only form that works for both in one loop.
    /// </summary>
    private void AddFields(ViewSchedule schedule)
    {
        var definition = schedule.Definition;

        var available = new Dictionary<string, SchedulableField>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in definition.GetSchedulableFields())
        {
            try
            {
                var name = field.GetName(_doc);
                if (!string.IsNullOrEmpty(name)) available.TryAdd(name, field);
            }
            catch
            {
                // A field whose name cannot be read cannot be matched either.
            }
        }

        // Order is the column order in the finished schedule. Category first so a mixed
        // multi-category view says what each row is, then WHERE it is, then the type, then
        // the quantity this exists for, then the material breakdown that makes it a takeoff.
        //
        // The three room fields lead the quantity deliberately: a finish takeoff is read and
        // issued per apartment, so they are what the schedule wants to be sorted and grouped
        // by. They are blank until Finish Surface Area has run - it is the pass that works
        // out which room each element's finish belongs to and writes the answer onto it.
        string[] wanted =
        [
            "Category",
            _settings.ApartmentParameter,
            _settings.RoomNumberParameter,
            _settings.RoomNameParameter,
            "Family and Type",
            _settings.CeilingParameter,
            _settings.CeilingPaintParameter,
            "Material: Name",
            "Material: Area",
            "Material: As Paint",
        ];

        // The fields worth complaining about when absent: an unbound parameter is a setup
        // problem the user can fix. "Category" and "Family and Type" are simply not offered
        // by a single-category schedule, which is expected rather than wrong.
        string[] mustHave =
        [
            _settings.CeilingParameter,
            _settings.ApartmentParameter,
            _settings.RoomNumberParameter,
            _settings.RoomNameParameter,
        ];

        ScheduleFieldId? quantityField = null;

        foreach (var name in wanted)
        {
            if (!available.TryGetValue(name, out var field))
            {
                if (mustHave.Contains(name))
                {
                    _problems.Add(
                        $"'{name}' is not available as a field in this schedule, so it could " +
                        "not be added. Bind it to Ceilings, Floors and Roofs first - the " +
                        "parameter setup in this same command does that.");
                }

                continue;
            }

            try
            {
                var added = definition.AddField(field);
                if (name == _settings.CeilingParameter) quantityField = added.FieldId;
            }
            catch (Exception ex)
            {
                _problems.Add($"Could not add the field '{name}': {ex.Message}");
            }
        }

        if (quantityField is null) return;

        // Without this the schedule lists every floor and roof in the model, most of them
        // with no ceiling area at all, and the one useful number is lost in the noise.
        try
        {
            if (definition.CanFilterByValue(quantityField))
                definition.AddFilter(new ScheduleFilter(quantityField, ScheduleFilterType.GreaterThan, 0.0));
            else
                _problems.Add($"'{_settings.CeilingParameter}' cannot be filtered on in this schedule.");
        }
        catch (Exception ex)
        {
            _problems.Add($"Could not filter on '{_settings.CeilingParameter}': {ex.Message}");
        }
    }

    // ------------------------------------------------------------------- helpers

    private ViewSchedule? FindSchedule(string name) =>
        new FilteredElementCollector(_doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .FirstOrDefault(v => !v.IsTemplate &&
                                 string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    private string UniqueName(string wanted)
    {
        if (FindSchedule(wanted) is null) return wanted;

        for (var i = 2; i < 100; i++)
        {
            var candidate = $"{wanted} {i}";
            if (FindSchedule(candidate) is null) return candidate;
        }

        return $"{wanted} {Guid.NewGuid():N}";
    }
}
