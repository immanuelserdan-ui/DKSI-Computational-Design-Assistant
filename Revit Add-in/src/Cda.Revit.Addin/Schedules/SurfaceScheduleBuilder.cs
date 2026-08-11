using Autodesk.Revit.DB;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Schedules;

/// <summary>
/// Builds the three "&lt;surface&gt; Surface Area by Room and Face" schedules, with the exact
/// columns, headings and order they carry today, and binds the shared parameters they need.
///
/// READ THIS BEFORE USING IT: PAINTEDMATERIALTAKEOFF ALREADY CREATES THESE VIEWS.
///   The names below are compiled into PaintedMaterialTakeoff.dll and appear when Painted
///   Surface Area is run. So this class is NOT needed to get the schedules in the first
///   place. It exists for the cases that product does not cover:
///
///     - rebuilding a view someone deleted, without re-running the whole takeoff
///     - repairing a view whose columns were changed by hand
///     - getting the same schedules into a model or template where the takeoff has not run
///
///   It finds-or-repairs rather than replacing, for the reason PaintTakeoffBuilder gives at
///   length: a schedule is a VIEW, it may be on a sheet with column widths and filters
///   someone set, and recreating it throws all of that away every run.
///
/// THE HEADINGS ARE DANISH AND THE PARAMETERS ARE ENGLISH, which is the single thing most
/// likely to trip up anyone reading the schedule and then searching the parameter list. The
/// field is "Room Name"; the column heading is "Rum". ColumnHeading is an override stored on
/// the view, so it has to be set explicitly here - adding the field alone gives an English
/// column and a schedule that no longer matches the others.
///
/// WHERE THE DEFINITIONS COME FROM
///   PaintedMaterialTakeoff-SharedParameters.txt, shipped inside the takeoff product's own
///   install folder. Parameters are looked up BY GUID, not by name: a GUID is the identity
///   Revit uses, names can be edited in the shared parameter file, and binding the wrong
///   "Room Name" to these rows would produce a schedule that looks right and reads empty.
/// </summary>
internal static class SurfaceScheduleBuilder
{
    /// <summary>
    /// The category the takeoff rows are placed in.
    ///
    /// GENERIC MODELS IS AN ASSUMPTION, and a checked one rather than a guess: it is what this
    /// project's own PaintTakeoffBuilder uses for the identical job, and the takeoff's rows are
    /// geometry-less carriers of exactly that kind. It is a parameter on every method here so a
    /// model that proves otherwise needs no code change - pass the real category instead.
    /// </summary>
    public static readonly BuiltInCategory DefaultCategory = BuiltInCategory.OST_GenericModel;

    /// <summary>
    /// One column: which shared parameter, and what the heading over it reads.
    /// </summary>
    private readonly record struct Column(string Guid, string Parameter, string Heading);

    /// <summary>
    /// The wall and floor schedules, which carry identical columns in identical order. Taken
    /// from the views themselves, left to right, not from what would look tidy.
    /// </summary>
    private static readonly Column[] WallAndFloorColumns =
    [
        new("1155a2b8-5513-4a17-94bb-10cfbcedcf10", "Room Name",          "Rum"),
        new("8b7fe984-a90d-4a21-a304-073e56f80e64", "Room Number",        "Rum nr"),
        new("16f6f4cb-c548-4e6b-87b3-ef1d0e1613ec", "Paint Segment",      "Vægflade / flade"),
        new("635718f8-263e-47a5-9913-7733277cea28", "Paint Layer",        "Lag"),
        new("799d3204-c7a3-4329-8368-db08388982ea", "Paint Material Name","Materiale"),
        new("eefb7625-ed32-4eae-9591-75d77da0f6e3", "Paint As Paint",     "Er maling"),
        new("790fbc84-7c4e-437c-b1b2-2b87919a8554", "Painted Surface Area", "Areal"),
        new("3b6950aa-2c11-46cd-bae0-dc5e7bc9a884", "Paint Surface Type", "Fladetype"),
        new("4362cc33-0f18-4f4e-819f-d243b31e04a2", "Paint Status",       "Status"),
    ];

    /// <summary>
    /// The ceiling schedule, which is NOT the same shape and must not be built from the list
    /// above. Its first two columns - Afdeling and Selskab - and its "enh" column are NOT in
    /// PaintedMaterialTakeoff-SharedParameters.txt at all; they come from somewhere else, most
    /// likely an office template. They are therefore omitted here rather than invented, and
    /// <see cref="Build"/> says so instead of silently producing a narrower schedule.
    /// </summary>
    private static readonly Column[] CeilingColumns =
    [
        new("9484206f-81ab-439e-8f8b-e2527a6a019c", "Room Department",    "Room Department"),
        new("8b7fe984-a90d-4a21-a304-073e56f80e64", "Room Number",        "Rum nr"),
        new("1155a2b8-5513-4a17-94bb-10cfbcedcf10", "Room Name",          "Rum"),
        new("16f6f4cb-c548-4e6b-87b3-ef1d0e1613ec", "Paint Segment",      "Vægflade / flade"),
        new("790fbc84-7c4e-437c-b1b2-2b87919a8554", "Painted Surface Area", "Areal"),
        new("799d3204-c7a3-4329-8368-db08388982ea", "Paint Material Name","Materiale"),
    ];

    /// <summary>
    /// "Paint Surface Group" - the parameter the takeoff writes Wall / Ceiling / Floor into,
    /// and therefore the one each schedule filters on so three views over one set of rows show
    /// three different things. Its own description says so: "the schedule this surface is
    /// reported in".
    /// </summary>
    private const string SurfaceGroupGuid = "0c7a4e91-53d8-4b62-9f10-a6d3e8571c24";

    /// <summary>
    /// Creates or repairs one surface schedule.
    ///
    /// CALLER OWNS THE TRANSACTION. Binding parameters and creating a view are both document
    /// edits; doing all three surfaces in one transaction is why this does not open its own.
    /// </summary>
    /// <param name="surface">Wall, Floor or Ceiling - matched against Paint Surface Group.</param>
    /// <returns>The view, or null with the reason appended to <paramref name="problems"/>.</returns>
    public static ViewSchedule? Build(
        Document doc,
        SurfaceScheduleVisibility.Surfaces surface,
        List<string> problems,
        BuiltInCategory? category = null)
    {
        var categoryId = new ElementId(category ?? DefaultCategory);
        var name = SurfaceScheduleVisibility.ScheduleNameOf(surface);

        var columns = surface == SurfaceScheduleVisibility.Surfaces.Ceiling
            ? CeilingColumns
            : WallAndFloorColumns;

        if (surface == SurfaceScheduleVisibility.Surfaces.Ceiling)
        {
            problems.Add(
                "Ceiling schedule: 'Afdeling', 'Selskab' and 'enh' are not defined in " +
                "PaintedMaterialTakeoff-SharedParameters.txt, so they were left out. Add them " +
                "by hand, or point this at the shared parameter file that does define them.");
        }

        // Bind first. A schedulable field only exists for a parameter the category actually
        // has, so binding has to happen before the fields are asked for - and on a repair run
        // it is a no-op, because Insert is skipped when the binding is already there.
        BindAll(doc, columns, categoryId, problems);
        Bind(doc, SurfaceGroupGuid, "Paint Surface Group", categoryId, problems);

        var schedule = Find(doc, name);

        if (schedule is null)
        {
            try
            {
                schedule = ViewSchedule.CreateSchedule(doc, categoryId);
            }
            catch (Exception ex)
            {
                problems.Add($"Could not create '{name}': {ex.Message}");
                return null;
            }

            try { schedule.Name = name; }
            catch (Exception ex) { problems.Add($"Could not name the schedule '{name}': {ex.Message}"); }
        }

        AddMissingColumns(doc, schedule, columns, problems);
        EnsureSurfaceFilter(doc, schedule, surface, problems);

        return schedule;
    }

    /// <summary>
    /// Adds any expected column the view does not already have, and sets its heading.
    ///
    /// NOTHING IS EVER REMOVED. An extra column is someone's business and may well be
    /// deliberate - the ceiling view's Afdeling and Selskab are exactly that. A MISSING column
    /// is never deliberate, so that is the only direction this repairs in. Same posture as
    /// PaintTakeoffBuilder.EnsureFields, and for the same reason.
    /// </summary>
    private static void AddMissingColumns(
        Document doc, ViewSchedule schedule, Column[] columns, List<string> problems)
    {
        ScheduleDefinition definition;
        try
        {
            definition = schedule.Definition;
        }
        catch (Exception ex)
        {
            problems.Add($"Could not read the definition of '{SafeName(schedule)}': {ex.Message}");
            return;
        }

        IList<SchedulableField> schedulable;
        try
        {
            schedulable = definition.GetSchedulableFields();
        }
        catch (Exception ex)
        {
            problems.Add($"Could not read the available fields for '{SafeName(schedule)}': {ex.Message}");
            return;
        }

        foreach (var column in columns)
        {
            var parameterId = ParameterId(doc, column.Guid);
            if (parameterId == ElementId.InvalidElementId)
            {
                problems.Add($"'{column.Parameter}' is not in this model, so '{column.Heading}' " +
                             "could not be added.");
                continue;
            }

            if (AlreadyPresent(definition, parameterId)) continue;

            var match = schedulable.FirstOrDefault(f => f.ParameterId == parameterId);
            if (match is null)
            {
                problems.Add($"'{column.Parameter}' is bound but not schedulable here, so " +
                             $"'{column.Heading}' could not be added.");
                continue;
            }

            try
            {
                var field = definition.AddField(match);

                // The override that makes the column read Danish. Without it the schedule shows
                // the English parameter name and stops matching the other two views.
                field.ColumnHeading = column.Heading;
            }
            catch (Exception ex)
            {
                problems.Add($"Could not add '{column.Heading}' to '{SafeName(schedule)}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Filters the view to one surface group, so three schedules over one set of rows show
    /// three different things. Idempotent: an existing filter on the same field is left alone
    /// rather than duplicated, because a second filter on one field ANDs with the first and
    /// would empty the schedule.
    /// </summary>
    private static void EnsureSurfaceFilter(
        Document doc,
        ViewSchedule schedule,
        SurfaceScheduleVisibility.Surfaces surface,
        List<string> problems)
    {
        var parameterId = ParameterId(doc, SurfaceGroupGuid);
        if (parameterId == ElementId.InvalidElementId)
        {
            problems.Add("'Paint Surface Group' is not in this model, so the schedule was left " +
                         "unfiltered and will show every surface.");
            return;
        }

        try
        {
            var definition = schedule.Definition;

            var field = Enumerable.Range(0, definition.GetFieldCount())
                .Select(definition.GetField)
                .FirstOrDefault(f => f.ParameterId == parameterId);

            if (field is null)
            {
                var match = definition.GetSchedulableFields()
                    .FirstOrDefault(f => f.ParameterId == parameterId);

                if (match is null)
                {
                    problems.Add("'Paint Surface Group' is not schedulable here, so the schedule " +
                                 "was left unfiltered.");
                    return;
                }

                field = definition.AddField(match);

                // Hidden, not absent. The rows are filtered by it and nobody wants to read it,
                // but a hidden field keeps its id and keeps filtering - which is the whole
                // reason this is IsHidden rather than a field that was never added.
                field.IsHidden = true;
            }

            if (definition.GetFilters().Any(f => f.FieldId == field.FieldId)) return;

            definition.AddFilter(
                new ScheduleFilter(field.FieldId, ScheduleFilterType.Equal, surface.ToString()));
        }
        catch (Exception ex)
        {
            problems.Add($"Could not filter '{SafeName(schedule)}' to {surface}: {ex.Message}");
        }
    }

    private static bool AlreadyPresent(ScheduleDefinition definition, ElementId parameterId)
    {
        for (var i = 0; i < definition.GetFieldCount(); i++)
        {
            try
            {
                if (definition.GetField(i).ParameterId == parameterId) return true;
            }
            catch
            {
                // a field type this build will not hand out - it is not the one we are adding
            }
        }

        return false;
    }

    private static void BindAll(
        Document doc, Column[] columns, ElementId categoryId, List<string> problems)
    {
        foreach (var column in columns)
            Bind(doc, column.Guid, column.Parameter, categoryId, problems);
    }

    /// <summary>
    /// Binds one shared parameter to the category, if it is not bound already.
    ///
    /// LOOKED UP BY GUID, NOT BY NAME. The GUID is the identity Revit stores; a name in a
    /// shared parameter file can be edited by anyone. Binding a DIFFERENT parameter that
    /// happens to be called "Room Name" produces a schedule that looks correct and reads empty
    /// on every row, which is the hardest kind of wrong to notice.
    /// </summary>
    private static void Bind(
        Document doc, string guid, string parameterName, ElementId categoryId, List<string> problems)
    {
        if (ParameterId(doc, guid) != ElementId.InvalidElementId) return;

        var definition = FindExternalDefinition(doc, guid);
        if (definition is null)
        {
            problems.Add($"'{parameterName}' is not in the shared parameter file, so it could " +
                         "not be bound. Point Revit at PaintedMaterialTakeoff-SharedParameters.txt.");
            return;
        }

        try
        {
            var categories = doc.Application.Create.NewCategorySet();
            categories.Insert(Category.GetCategory(doc, categoryId));

            var binding = doc.Application.Create.NewInstanceBinding(categories);

            if (!doc.ParameterBindings.Insert(definition, binding, GroupTypeId.Data))
                problems.Add($"Revit refused to bind '{parameterName}'.");
        }
        catch (Exception ex)
        {
            problems.Add($"Could not bind '{parameterName}': {ex.Message}");
        }
    }

    /// <summary>
    /// The parameter's id in THIS model, or InvalidElementId when it has never been bound.
    /// </summary>
    private static ElementId ParameterId(Document doc, string guid)
    {
        try
        {
            return SharedParameterElement.Lookup(doc, new Guid(guid))?.Id ?? ElementId.InvalidElementId;
        }
        catch
        {
            return ElementId.InvalidElementId;
        }
    }

    private static ExternalDefinition? FindExternalDefinition(Document doc, string guid)
    {
        try
        {
            var file = doc.Application.OpenSharedParameterFile();
            if (file is null) return null;

            var wanted = new Guid(guid);

            return file.Groups
                .SelectMany(g => g.Definitions.Cast<Definition>())
                .OfType<ExternalDefinition>()
                .FirstOrDefault(d => d.GUID == wanted);
        }
        catch (Exception ex)
        {
            Log.Warn($"Shared parameter file could not be read: {ex.Message}");
            return null;
        }
    }

    private static ViewSchedule? Find(Document doc, string name)
        => new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .FirstOrDefault(v => !v.IsTemplate &&
                                 string.Equals(v.Name, name, StringComparison.Ordinal));

    private static string SafeName(Element element)
    {
        try { return element.Name; }
        catch { return "?"; }
    }
}
