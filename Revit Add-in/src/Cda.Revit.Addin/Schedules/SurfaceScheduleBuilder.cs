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
    /// One column: which shared parameter, what the heading over it reads, and whether it is
    /// scheduled but not drawn.
    ///
    /// HIDDEN IS NOT ABSENT, and the distinction is the whole reason this field exists. The
    /// office reference schedules 14 fields and draws 9 - the other five are IsHidden, which
    /// keeps their data available to filters, sorting and grouping while keeping them off a
    /// sheet that is already too wide. See the note on SurfaceScheduleVisibility.SetColumnVisible
    /// for why that is IsHidden rather than RemoveField.
    /// </summary>
    private readonly record struct Column(string Guid, string Parameter, string Heading, bool Hidden = false);

    // GUIDs named once and reused across WallAndFloorColumns, DroppedWallAndFloorColumns and
    // WallAndFloorOrder, so the identity Revit actually keys on is written down in exactly one
    // place regardless of how many lists reference it.
    //
    // CONFIRMED LIVE, NOT COPIED FROM A SHARED-PARAMETER FILE. The values that were here before
    // this comment were already in this file when today's work started and were never
    // independently checked - they came from a different build's PaintedMaterialTakeoff-
    // SharedParameters.txt than the one this model's takeoff-override DLL actually ships, so
    // EVERY one of them silently resolved to nothing. ParameterId(doc, guid) returning
    // InvalidElementId for a wrong GUID looks identical to "this parameter is not bound yet" -
    // there is no exception, no blank-vs-missing distinction, just a column AddMissingColumns
    // quietly could not add. Confirmed by pulling a live carrier's full parameter dump and
    // reading its GUIDs directly, one field at a time, rather than trusting the source again.
    // If this model's takeoff-override DLL is ever rebuilt from a different shared-parameter
    // file, these will need re-confirming the same way - do not assume they are permanent.
    private const string RoomDepartmentGuid = "a27c0a95-65db-4afa-b3c7-0f138a82cbce";
    private const string RoomNameGuid = "7b82a73e-2d81-4dc9-8336-cbbce39d1668";
    private const string RoomNumberGuid = "7e3d7688-6888-4fa9-9d18-d520a9352a77";
    private const string PaintLayerGuid = "6bbc0ea5-7e2c-4cdb-a50f-ac63ced72bae";
    private const string PaintMaterialNameGuid = "12f5a79e-ab9f-4c5a-9139-e6479a18fdbe";
    private const string PaintAsPaintGuid = "8d49a556-5a78-4437-8832-fd4edf74d4d2";
    private const string PaintedSurfaceAreaGuid = "2c614085-7d4b-4854-830e-c04bcd7d698a";
    private const string PaintSurfaceTypeGuid = "1f0c1528-e897-4dba-b15a-77b46f3c9c7b";
    private const string PaintStatusGuid = "600a1b1c-c5c6-4d4f-bd5c-c32faa7f64c2";

    /// <summary>
    /// "Paint Type" - the TYPE NAME of the wall/floor/ceiling a row was measured on
    /// ("YV_Beton - 200mm"), with none of the identity data "Paint Segment" carries.
    /// CONFIRMED LIVE (2026-09-07) and cross-checked against FinishParameterSetup's own
    /// binding table, which uses this exact GUID for FinishSettings.PaintTypeParameter -
    /// unlike the rest of this file's GUIDs, this one is not merely observed on a carrier, it
    /// is this add-in's own declared identity for the parameter.
    /// </summary>
    private const string PaintTypeGuid = "b5c8f716-92a4-4d38-a0e6-14fb27c9d803";

    /// <summary>
    /// "Paint Segment" - host element id and face, e.g. "YV_Beton - 200mm #29307636 · Face 0.0
    /// R2". Real, working data (unlike <see cref="BrokenRoomRelationshipFieldName"/>) - it is
    /// what <see cref="Finishes.PaintRoomOverrides"/> keys on and what
    /// <see cref="Finishes.PaintOverlapAudit"/> uses to tell two distinct faces apart - so this
    /// class only ever removes it from a SCHEDULE, never touches the parameter itself. Confirmed
    /// live the same way as PaintTypeGuid.
    /// </summary>
    private const string PaintSegmentGuid = "759d47e2-727c-4bda-99fc-4fadc78becf1";

    /// <summary>
    /// All three surface schedules' six managed columns, in the office reference layout - one
    /// list for Wall, Floor and Ceiling alike, since <see cref="Build"/> no longer treats
    /// Ceiling as a different shape.
    ///
    /// "Room Department" IS THE SHARED PARAMETER, NOT "Room: Department". A wall schedule was
    /// found carrying the built-in ROOM-RELATIONSHIP field of that near-identical name instead
    /// - the one Revit computes by finding which Room an element's Room Calculation Point sits
    /// in - and that never resolves here: every row is a Generic Model DirectShape, and
    /// DirectShapes get no Room Calculation Point regardless of whether they carry real
    /// geometry, so the column reads blank on every row forever. This is the exact trap the
    /// note on <see cref="PaintTakeoffBuilder.EnsureFields"/> already documents for "Room: Wall
    /// Paint Area" - same mechanism, different column.
    ///
    /// THE HEADING NO LONGER MATCHES THE BROKEN FIELD'S OWN TEXT (2026-09-07). It used to be set
    /// to "Room: Department" - the same text the broken field showed - purely so the reference
    /// layout's wording survived the swap; it is "Lejlighed" now, by request. That is safe only
    /// because <see cref="RemoveBrokenRoomRelationshipField"/> tells the two apart by field
    /// NAME, never by heading - see that method for why.
    /// </summary>
    private static readonly Column[] ManagedColumns =
    [
        new(RoomDepartmentGuid,     "Room Department",      "Lejlighed"),
        new(RoomNumberGuid,         "Room Number",          "Rum nr"),
        new(RoomNameGuid,           "Room Name",            "Rum"),
        new(PaintLayerGuid,         "Paint Layer",          "Lag",       Hidden: true),
        new(PaintAsPaintGuid,       "Paint As Paint",       "Er maling", Hidden: true),
        new(PaintTypeGuid,          "Paint Type",           "Type"),
        new(PaintedSurfaceAreaGuid, "Painted Surface Area", "Mængde"),
        new(PaintMaterialNameGuid,  "Paint Material Name",  "Kode"),
        new(PaintSurfaceTypeGuid,   "Paint Surface Type",   "Fladetype", Hidden: true),
        new(PaintStatusGuid,        "Paint Status",         "Status",    Hidden: true),
        new(SurfaceGroupGuid,       "Paint Surface Group",  "Gruppe",    Hidden: true),
    ];

    /// <summary>
    /// The exact left-to-right order all three surface schedules must end up in - the office
    /// reference's own 14 scheduled fields, of which 9 draw.
    ///
    /// TWO KINDS OF ENTRY, ON PURPOSE. A GUID is one of <see cref="ManagedColumns"/>, which
    /// this class binds, adds, headings, hides and positions. A plain name - "Selskab",
    /// "Afdeling", "enh" - is not a shared parameter this class can bind or look up by GUID.
    /// "Selskab" and "Afdeling" belong to the OFFICE TEMPLATE (Project Information) and this
    /// class only moves them into place where <see cref="AddMissingProjectInfoColumns"/> put
    /// them or they already existed; "enh" is an ordinary project parameter this class DOES
    /// create if missing - see <see cref="AddMissingUnitColumn"/> - it just has no GUID to key
    /// the ManagedColumns table on. A schedule missing a plain-name slot keeps the rest of the
    /// layout and is reported, rather than the whole reorder failing over one column.
    ///
    /// "PAINT SEGMENT" IS DELIBERATELY ABSENT, "PAINT TYPE" TAKES ITS SLOT (2026-09-07). The raw
    /// segment string ("YV_Beton - 200mm #29307636 · Face 0.0 R2") carries host-element-id and
    /// face data that <see cref="Finishes.PaintRoomOverrides"/> and
    /// <see cref="Finishes.PaintOverlapAudit"/> need to stay unique - stripping that text would
    /// corrupt both, so it is never edited. "Paint Type" is a distinct parameter carrying only
    /// the type name half of that same string ("YV_Beton - 200mm"), already written onto every
    /// carrier for exactly this column - see <see cref="Finishes.PaintHostTypeStamp"/>. This
    /// class now manages it under the heading "Type", between Room Name and Painted Surface
    /// Area, and actively removes a raw "Paint Segment" column if one was added by hand -
    /// see <see cref="RemoveRawPaintSegmentField"/>.
    /// </summary>
    private static readonly string[] ColumnOrder =
    [
        "Afdeling", "Selskab",
        RoomDepartmentGuid, RoomNumberGuid, RoomNameGuid,
        PaintLayerGuid, PaintAsPaintGuid,
        PaintTypeGuid,
        PaintedSurfaceAreaGuid,
        "enh",
        PaintMaterialNameGuid,
        PaintSurfaceTypeGuid, PaintStatusGuid, SurfaceGroupGuid,
    ];

    /// <summary>
    /// "Paint Surface Group" - the parameter the takeoff writes Wall / Ceiling / Floor into,
    /// and therefore the one each schedule filters on so three views over one set of rows show
    /// three different things. Its own description says so: "the schedule this surface is
    /// reported in". Confirmed live, same as the GUIDs above - see that note.
    /// </summary>
    private const string SurfaceGroupGuid = "2fee9d3e-582a-44c6-a18a-3a0d3da78d96";

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

        // ALL THREE SURFACES SHARE ONE LAYOUT NOW. Ceiling used to carry a narrower column set
        // of its own - see the git history for what that looked like - but the office
        // reference this class now builds toward applies uniformly across Wall, Floor and
        // Ceiling, so there is only one Column[] and one order left to maintain.
        var columns = ManagedColumns;

        // Bind first. A schedulable field only exists for a parameter the category actually
        // has, so binding has to happen before the fields are asked for - and on a repair run
        // it is a no-op, because Insert is skipped when the binding is already there.
        BindAll(doc, columns, categoryId, problems);

        // THE OFFICE NAME ONLY - NEVER SurfaceScheduleVisibility.FindSchedule's fallback.
        // That fallback exists for opening/showing a view where a name that resolves to
        // SOMETHING is good enough, and it is wrong here for exactly that reason: if the office
        // schedule does not exist yet, the fallback would hand back the LEGACY-named one, this
        // method would repair columns onto it under its OLD name (schedule.Name is only ever
        // set on a newly CREATED schedule, a few lines down), and the caller's next step -
        // LegacySurfaceScheduleCleanup deleting that exact legacy name - would destroy the
        // schedule this method had just finished configuring. Confirmed: that is exactly what
        // happened on a live run - "0 problem(s)" reported, the legacy name logged as removed,
        // and no "@V03" schedule anywhere afterward, because it was never created under that
        // name in the first place.
        var schedule = SurfaceScheduleVisibility.Find(doc, name);

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
        AddMissingProjectInfoColumns(doc, schedule, problems);
        AddMissingUnitColumn(doc, schedule, problems);
        RemoveBrokenRoomRelationshipField(schedule, problems);
        RemoveRawPaintSegmentField(doc, schedule, problems);

        // AFTER AddMissingColumns, NOT INSIDE IT. That one only touches fields it just created,
        // so a schedule already carrying "Paint Layer" as a VISIBLE column - which is what the
        // office views look like today - would keep it visible forever. This runs over every
        // managed column present, new or not, and is what actually makes the five hidden ones
        // hidden.
        ApplyColumnFormatting(doc, schedule, columns, problems);

        EnforceColumnOrder(doc, schedule, problems);
        EnsureSurfaceFilter(doc, schedule, surface, problems);

        return schedule;
    }

    /// <summary>
    /// Revit's built-in room-relationship field, distinct from the "Room Department" SHARED
    /// PARAMETER this class writes above - same words, different mechanism, and the reason
    /// this needs its own check rather than living inside AddMissingColumns.
    /// </summary>
    private const string BrokenRoomRelationshipFieldName = "Room: Department";

    /// <summary>
    /// Deletes the built-in "Room: Department" column if a schedule is carrying one.
    ///
    /// THE ONE COLUMN THIS CLASS EVER REMOVES, AND WHY THAT IS SAFE HERE DESPITE THE POLICY
    /// STATED ON AddMissingColumns. "Nothing is ever removed" protects a column someone added
    /// on purpose - but this one can never have been added FOR A REASON, because it can never
    /// show data: it is a room-relationship field, resolved by finding which Room an element's
    /// Room Calculation Point sits in, and every row here is a Generic Model DirectShape.
    /// DirectShapes get no Room Calculation Point, geometry or not, so the column is not
    /// misconfigured - it is structurally incapable of reading anything but blank, on this
    /// category, in any model. Someone almost certainly dragged it in expecting it to behave
    /// like the real "Room Department" shared parameter, which reads identically at a glance.
    ///
    /// RemoveField, not IsHidden. Unlike the fields PaintTakeoffBuilder's own note warns about,
    /// this one is never wanted for sorting, grouping or filtering - it holds no data to sort,
    /// group or filter BY - so there is no rule elsewhere in the definition that dropping its
    /// id could silently break.
    ///
    /// MATCHED BY FIELD NAME ONLY, NOT BY COLUMN HEADING. WallAndFloorColumns deliberately
    /// overrides the WORKING "Room Department" field's heading to read "Room: Department" too
    /// - the same text this broken one shows - so a heading is no longer safe to tell them
    /// apart with. GetName() is unaffected by that override; it reports the underlying field's
    /// own identity, which differs between the two regardless of what either is heading is set
    /// to display.
    /// </summary>
    private static void RemoveBrokenRoomRelationshipField(ViewSchedule schedule, List<string> problems)
    {
        ScheduleDefinition definition;
        try
        {
            definition = schedule.Definition;
        }
        catch
        {
            return;   // AddMissingColumns already reported this; nothing new to say
        }

        ScheduleFieldId? toRemove = null;

        try
        {
            foreach (var fieldId in definition.GetFieldOrder())
            {
                ScheduleField field;
                try { field = definition.GetField(fieldId); }
                catch { continue; }

                string name;
                try { name = field.GetName(); }
                catch { name = string.Empty; }

                if (string.Equals(name, BrokenRoomRelationshipFieldName, StringComparison.Ordinal))
                {
                    toRemove = fieldId;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            problems.Add($"Could not check '{SafeName(schedule)}' for the broken 'Room: Department' " +
                         $"column: {ex.Message}");
            return;
        }

        if (toRemove is null) return;

        try
        {
            definition.RemoveField(toRemove);
            problems.Add(
                $"'{SafeName(schedule)}' was carrying the built-in 'Room: Department' column, " +
                "which can never show data on these rows - it has been removed. The real " +
                "'Room Department' column (a shared parameter, no colon) reports the same " +
                "thing correctly.");
        }
        catch (Exception ex)
        {
            problems.Add(
                $"'{SafeName(schedule)}' carries the broken 'Room: Department' column and it " +
                $"could not be removed automatically: {ex.Message}. Delete it by hand in the " +
                "schedule's Fields dialog.");
        }
    }

    /// <summary>
    /// Removes a raw "Paint Segment" column if the schedule carries one, now that "Paint Type"
    /// (heading "Type") is the managed column for this position.
    ///
    /// WHY THIS ONE IS ALSO SAFE TO REMOVE, DESPITE AddMissingColumns' "NOTHING IS EVER REMOVED"
    /// POLICY. That policy protects a column someone added ON PURPOSE, as a considered choice.
    /// This one cannot be that: it is the raw identity string ("host type · element id · face")
    /// that <see cref="PaintTypeGuid"/>'s own doc comment explains this class deliberately keeps
    /// off every surface schedule, by request (2026-09-07) - so a schedule carrying it got there
    /// from someone adding the wrong field from the Fields dialog, most likely reaching for the
    /// type name and finding "Paint Segment" before "Paint Type" alphabetically. The PARAMETER
    /// itself is untouched here - only ever the schedule's use of it - so PaintRoomOverrides,
    /// PaintOverlapAudit and every other consumer that keys on the live value keep working.
    /// </summary>
    private static void RemoveRawPaintSegmentField(Document doc, ViewSchedule schedule, List<string> problems)
    {
        var parameterId = ParameterId(doc, PaintSegmentGuid);
        if (parameterId == ElementId.InvalidElementId) return;   // never bound here; nothing to remove

        ScheduleDefinition definition;
        try
        {
            definition = schedule.Definition;
        }
        catch
        {
            return;   // AddMissingColumns already reported this; nothing new to say
        }

        ScheduleFieldId? toRemove = null;
        try
        {
            for (var i = 0; i < definition.GetFieldCount(); i++)
            {
                var field = definition.GetField(i);
                if (field.ParameterId == parameterId)
                {
                    toRemove = field.FieldId;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            problems.Add($"Could not check '{SafeName(schedule)}' for a raw 'Paint Segment' " +
                         $"column: {ex.Message}");
            return;
        }

        if (toRemove is null) return;

        try
        {
            definition.RemoveField(toRemove);
            problems.Add(
                $"'{SafeName(schedule)}' was carrying the raw 'Paint Segment' column (host id and " +
                "face data) - removed in favour of 'Paint Type', which shows just the type name " +
                "under the heading 'Type'.");
        }
        catch (Exception ex)
        {
            problems.Add(
                $"'{SafeName(schedule)}' carries a raw 'Paint Segment' column and it could not be " +
                $"removed automatically: {ex.Message}. Delete it by hand in the schedule's Fields " +
                "dialog.");
        }
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
    /// "Afdeling" and "Selskab" - Project Information shared parameters, requested by name
    /// (2026-08-29) to appear before "Room Department" on all three surface schedules.
    ///
    /// WHY THIS IS SEPARATE FROM AddMissingColumns, NOT ANOTHER ROW IN ManagedColumns. Both
    /// parameters carry no GUID in this class - they are not in
    /// PaintedMaterialTakeoff-SharedParameters.txt, which is what every ManagedColumns entry is
    /// bound and matched by. AddMissingColumns' own doc comment already names the reason: "the
    /// ceiling view's Afdeling and Selskab" - both existed already, added by hand, one schedule
    /// only, which is exactly why nothing here used to create them automatically. This makes
    /// that creation explicit and gives it to all three views, rather than widening
    /// AddMissingColumns' GUID-only contract to cover it silently.
    ///
    /// A NAME MATCH IS NOT ENOUGH - MEASURED WRONG, NOT ASSUMED RIGHT (2026-08-29). The first
    /// version matched by name only, reasoning that exactly one Project Information parameter of
    /// each name can exist in a document. True, and irrelevant: `GetSchedulableFields()` on this
    /// GENERIC MODEL schedule returned TWO entries named "Afdeling" - one `FieldType=ProjectInfo`
    /// (the constant, document-wide value), one `FieldType=Instance` (read per row, off each
    /// carrier's OWN parameters). `FirstOrDefault` by name alone took whichever came first, which
    /// was Instance - and a Paint Takeoff Segment DirectShape carries no "Afdeling" parameter of
    /// its own, so every row rendered blank despite the real value (confirmed live: Selskab=1234,
    /// Afdeling=0001 on the Project Information element) sitting one binding away. The match now
    /// requires `FieldType == ScheduleFieldType.ProjectInfo` explicitly.
    ///
    /// THE WRONG FIELD WAS ALREADY PLACED ON ALL THREE SCHEDULES BEFORE THIS FIX SHIPPED, which
    /// is why "already present" can no longer be a name-only check either: a schedule carrying
    /// the Instance-typed "Afdeling" from the previous run would otherwise report itself done
    /// forever. The presence check now reads the placed field's OWN ParameterId and swaps it out
    /// if it does not match the ProjectInfo-typed one - same remove-then-add shape as
    /// RemoveBrokenRoomRelationshipField uses for its own wrong-field case, and safe for the same
    /// reason: a field that can only ever read blank was never someone's deliberate column.
    ///
    /// POSITION COMES FROM ColumnOrder, NOT FROM HERE. This only fixes which field is bound;
    /// EnforceColumnOrder already lists both ahead of RoomDepartmentGuid and moves any plain-name
    /// entry into place by the same GetName()-matching logic it uses for "enh".
    /// </summary>
    private static readonly string[] ProjectInfoColumns = ["Afdeling", "Selskab"];

    private static void AddMissingProjectInfoColumns(
        Document doc, ViewSchedule schedule, List<string> problems)
    {
        ScheduleDefinition definition;
        try
        {
            definition = schedule.Definition;
        }
        catch (Exception ex)
        {
            problems.Add(
                $"Could not read the definition of '{SafeName(schedule)}' to add Afdeling/Selskab: {ex.Message}");
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

        foreach (var name in ProjectInfoColumns)
        {
            SchedulableField? match = null;
            try
            {
                match = schedulable.FirstOrDefault(
                    f => f.GetName(doc) == name && f.FieldType == ScheduleFieldType.ProjectInfo);
            }
            catch { /* match stays null; reported below */ }

            if (match is null)
            {
                problems.Add($"'{name}' has no Project Information field available on " +
                             $"'{SafeName(schedule)}', so it could not be added. Confirm it is still a " +
                             "Project Information parameter.");
                continue;
            }

            // ALREADY CORRECT if a field with this exact ParameterId is already placed - not
            // just any field with this NAME, which is precisely the check that let the wrong
            // (Instance-typed) field pass as "done" before. A same-named field bound to a
            // DIFFERENT parameter is the bug this method exists to fix, so it is removed first.
            ScheduleFieldId? wrongField = null;
            var alreadyCorrect = false;
            try
            {
                for (var i = 0; i < definition.GetFieldCount(); i++)
                {
                    var existing = definition.GetField(i);
                    if (!string.Equals(existing.GetName(), name, StringComparison.Ordinal)) continue;

                    if (existing.ParameterId == match.ParameterId)
                    {
                        alreadyCorrect = true;
                    }
                    else
                    {
                        wrongField = existing.FieldId;
                    }
                    break;
                }
            }
            catch
            {
                // Fall through and try to add anyway; a genuine duplicate fails harmlessly below.
            }

            if (alreadyCorrect) continue;

            if (wrongField is not null)
            {
                try
                {
                    definition.RemoveField(wrongField);
                    problems.Add($"'{SafeName(schedule)}' was carrying '{name}' bound to the wrong " +
                                 "(per-row Instance) field, which can only ever read blank on these " +
                                 "carriers - removed and re-added bound to Project Information.");
                }
                catch (Exception ex)
                {
                    problems.Add($"'{SafeName(schedule)}' carries '{name}' bound to the wrong field and " +
                                 $"it could not be removed automatically: {ex.Message}. Remove it by hand " +
                                 "in the schedule's Fields dialog, then re-run.");
                    continue;
                }
            }

            try
            {
                definition.AddField(match);
            }
            catch (Exception ex)
            {
                problems.Add($"Could not add '{name}' to '{SafeName(schedule)}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Adds "enh" - the constant unit label ("m²") beside "Mængde" - if the schedule does not
    /// already carry it. <see cref="ColumnOrder"/> already knows where it goes (between
    /// PaintedSurfaceAreaGuid and PaintMaterialNameGuid); this only has to get it ONTO the
    /// schedule in the first place.
    ///
    /// A STAND-IN FOR A CALCULATED VALUE FIELD, NOT A SHARED PARAMETER - so it does not belong
    /// in <see cref="ManagedColumns"/>, which is keyed entirely by GUID. "enh" is an ordinary
    /// project parameter with no GUID to bind or look up by; <see cref="Finishes.PaintUnitStamp"/>
    /// writes the literal text into it. See that class for why this exists at all instead of
    /// the Calculated Value field the office reference schedule actually shows.
    ///
    /// MATCHED BY NAME AND FieldType == Instance, same caution as
    /// <see cref="AddMissingProjectInfoColumns"/> uses for "Afdeling"/"Selskab" - not because
    /// "enh" is known to have a Project Information duplicate, but because that exact class of
    /// bug (two schedulable fields sharing a name, one of them silently unable to read data)
    /// is cheap to guard against and expensive to diagnose after the fact.
    /// </summary>
    private static void AddMissingUnitColumn(Document doc, ViewSchedule schedule, List<string> problems)
    {
        const string name = "enh";

        ScheduleDefinition definition;
        try
        {
            definition = schedule.Definition;
        }
        catch
        {
            return;   // AddMissingColumns already reported this; nothing new to say
        }

        try
        {
            for (var i = 0; i < definition.GetFieldCount(); i++)
            {
                if (string.Equals(definition.GetField(i).GetName(), name, StringComparison.Ordinal))
                    return;   // already there
            }
        }
        catch
        {
            // fall through and try to add anyway; a genuine duplicate fails harmlessly below
        }

        SchedulableField? match;
        try
        {
            match = definition.GetSchedulableFields()
                .FirstOrDefault(f => f.GetName(doc) == name && f.FieldType == ScheduleFieldType.Instance);
        }
        catch (Exception ex)
        {
            problems.Add($"Could not read the available fields for '{SafeName(schedule)}': {ex.Message}");
            return;
        }

        if (match is null)
        {
            problems.Add($"'{name}' has no Instance field available on '{SafeName(schedule)}', so the " +
                         "unit column could not be added. Confirm it is still a project parameter on " +
                         "Generic Models.");
            return;
        }

        try
        {
            var field = definition.AddField(match);
            field.ColumnHeading = name;
        }
        catch (Exception ex)
        {
            problems.Add($"Could not add '{name}' to '{SafeName(schedule)}': {ex.Message}");
        }
    }

    /// <summary>
    /// Sets the heading and the hidden flag on every managed column the schedule carries,
    /// whether this run added it or it was already there.
    ///
    /// THE FIVE HIDDEN ONES ARE THE POINT. Paint Layer, Paint As Paint, Paint Surface Type,
    /// Paint Status and Paint Surface Group stay scheduled - their data still reaches filters,
    /// sorting and grouping - and simply stop being drawn, which is how the office reference
    /// gets 14 fields into 9 columns. An earlier version of this class DELETED those four
    /// instead, which read the same on screen and quietly threw away the data underneath.
    /// </summary>
    private static void ApplyColumnFormatting(
        Document doc, ViewSchedule schedule, Column[] columns, List<string> problems)
    {
        ScheduleDefinition definition;
        try
        {
            definition = schedule.Definition;
        }
        catch
        {
            return;   // AddMissingColumns already reported this; nothing new to say
        }

        foreach (var column in columns)
        {
            var parameterId = ParameterId(doc, column.Guid);
            if (parameterId == ElementId.InvalidElementId) continue;   // never bound here

            ScheduleField? field = null;
            try
            {
                for (var i = 0; i < definition.GetFieldCount(); i++)
                {
                    var candidate = definition.GetField(i);
                    if (candidate.ParameterId == parameterId)
                    {
                        field = candidate;
                        break;
                    }
                }
            }
            catch
            {
                continue;   // a field type this build will not hand out
            }

            if (field is null) continue;   // AddMissingColumns already said why

            try { field.ColumnHeading = column.Heading; }
            catch (Exception ex)
            {
                problems.Add(
                    $"Could not set the heading '{column.Heading}' in '{SafeName(schedule)}': {ex.Message}");
            }

            try { field.IsHidden = column.Hidden; }
            catch (Exception ex)
            {
                problems.Add(
                    $"Could not {(column.Hidden ? "hide" : "show")} '{column.Heading}' in " +
                    $"'{SafeName(schedule)}': {ex.Message}");
            }

            if (column.Guid == PaintedSurfaceAreaGuid)
                ClearUnitSymbol(field, schedule, problems);
        }
    }

    /// <summary>
    /// Clears the "Mængde" column's unit symbol, so its figures read as bare numbers.
    ///
    /// THIS USED TO SET m², AND THE REVERSAL IS DELIBERATE (2026-08-29). The column was
    /// "Areal" with the symbol shown on every row; it is now "Mængde" (quantity), and the unit
    /// belongs in the "enh" column that the office template already carries, so repeating it
    /// per row is noise.
    ///
    /// UseDefault MUST STAY OFF. It is what "Use project settings" unchecked means in the
    /// Format dialog. Leaving it on would let the project's own Area format silently put m²
    /// back - the same failure this method was written to prevent, only in the other
    /// direction, and just as invisible.
    ///
    /// RoundingMethod MUST BE SET TO Nearest FIRST. A freshly-read FormatOptions can carry a
    /// rounding method IsValidSymbol/SetSymbolTypeId reject outright - confirmed live: an
    /// earlier version threw "the rounding method in formatOptions is not set to Nearest" on
    /// all three schedules, every run, and never actually set anything. Revit's own exception
    /// names the fix; this sets it before touching the symbol rather than reacting to the same
    /// failure a second time.
    ///
    /// FOUND, ON THE THIRD PASS, WITH THE DIAGNOSTIC ADDED FOR THE SECOND ONE. The exception's
    /// own stack trace put it inside <c>ScheduleField.SetFormatOptions</c>, not
    /// `SetSymbolTypeId` - and the logged unit for this Area field was
    /// `autodesk.unit.unit:meters-1.0.0`, the LENGTH unit, not `...squareMeters-1.0.1`.
    /// `GetFormatOptions()` on this field hands back a `FormatOptions` whose own `UnitTypeId` is
    /// wrong for what the field is; `SetFormatOptions` then validates the object it's given
    /// against the field's real spec and rejects it - which is exactly the ambiguous "display
    /// unit... or rounding method" exception two earlier versions chased in the wrong half.
    /// RoundingMethod was never the problem either time.
    ///
    /// THE FIX PINS THE UNIT EXPLICITLY RATHER THAN TRUSTING WHAT WAS READ BACK. Confirmed by
    /// hand in the Format dialog on this exact field - "Use project settings" unchecked, Units
    /// "Square metres", Unit symbol "None" - so None is a legitimate choice once the unit is the
    /// right one. `GetValidSymbols()` is re-read AFTER `SetUnitTypeId`, not before: the diagnostic
    /// version queried it against the wrong (meters) unit and got a misleadingly reassuring
    /// `emptyIsValid=True` that had nothing to do with the unit actually in force.
    ///
    /// ACCURACY PINNED TOO, SAME REASON AS THE UNIT (2026-08-29). `FormatOptions.Accuracy` is
    /// the "2 decimal places" / "0.01" rounding increment shown in the dialog - a value entirely
    /// separate from `RoundingMethod` (which only says round-to-nearest vs up vs down). It was
    /// never set here, so whatever `GetFormatOptions()` happened to carry over from the wrong
    /// (meters) unit survived the switch to square metres unexamined - the same class of bug as
    /// the unit itself, just not one that throws, so it went unnoticed until read by eye against
    /// the office reference's own two-decimal figures.
    /// </summary>
    private static void ClearUnitSymbol(ScheduleField field, ViewSchedule schedule, List<string> problems)
    {
        try
        {
            var format = field.GetFormatOptions();

            format.UseDefault = false;
            format.RoundingMethod = RoundingMethod.Nearest;
            format.SetUnitTypeId(UnitTypeId.SquareMeters);
            format.Accuracy = 0.01;

            var noSymbol = new ForgeTypeId();
            IList<ForgeTypeId>? valid = null;
            try { valid = format.GetValidSymbols(); } catch { /* logged as absent below */ }

            var hasEmptyEntry = valid?.Any(v => v.TypeId.Length == 0) ?? false;
            // Info, not Debug - Verbose logging is off by default (Log.Verbose doc comment),
            // and this line is only useful exactly once, on whatever run happens to be live
            // when someone is chasing this down.
            Log.Info(
                $"ClearUnitSymbol '{SafeName(schedule)}': unit={SafeUnitId(format)}, " +
                $"validSymbols=[{string.Join(", ", valid?.Select(v => v.TypeId.Length == 0 ? "<empty>" : v.TypeId) ?? [])}], " +
                $"emptyIsValid={hasEmptyEntry}");

            format.SetSymbolTypeId(noSymbol);
            field.SetFormatOptions(format);
        }
        catch (Exception ex)
        {
            Log.Error($"ClearUnitSymbol '{SafeName(schedule)}' threw", ex);
            problems.Add($"Could not clear the unit symbol in '{SafeName(schedule)}': {ex.Message}");
        }
    }

    private static string SafeUnitId(FormatOptions format)
    {
        try { return format.GetUnitTypeId().TypeId; }
        catch (Exception ex) { return $"<unreadable: {ex.Message}>"; }
    }

    /// <summary>
    /// Reorders a Wall/Floor schedule's columns to match <see cref="WallAndFloorOrder"/>.
    ///
    /// SetFieldOrder DEMANDS THE COMPLETE CURRENT FIELD SET, EXACTLY - Revit throws otherwise,
    /// per its own documented exception. So this builds the full permutation in two passes:
    /// place each named slot, in order, if a matching field currently exists; then append
    /// whatever is left over, in whatever order Revit already had it. An unrecognised column -
    /// something neither this class nor the office template names - is repositioned to the end
    /// rather than silently dropped or, worse, causing the whole reorder to fail outright.
    /// </summary>
    private static void EnforceColumnOrder(
        Document doc, ViewSchedule schedule, List<string> problems)
    {
        ScheduleDefinition definition;
        try
        {
            definition = schedule.Definition;
        }
        catch
        {
            return;   // AddMissingColumns already reported this; nothing new to say
        }

        List<ScheduleFieldId> current;
        try
        {
            current = [.. definition.GetFieldOrder()];
        }
        catch (Exception ex)
        {
            problems.Add($"Could not read the column order of '{SafeName(schedule)}': {ex.Message}");
            return;
        }

        var remaining = new List<ScheduleFieldId>(current);
        var ordered = new List<ScheduleFieldId>();

        foreach (var slot in ColumnOrder)
        {
            // A GUID slot is one of this class's own columns, matched by ParameterId - the
            // stable identity, unaffected by a heading override. Anything else is a name from
            // the office template, which this class has no GUID for and matches by GetName()
            // or ColumnHeading instead - whichever the field actually reports.
            var isGuid = Guid.TryParse(slot, out _);
            var parameterId = isGuid ? ParameterId(doc, slot) : ElementId.InvalidElementId;

            var index = remaining.FindIndex(id =>
            {
                ScheduleField field;
                try { field = definition.GetField(id); }
                catch { return false; }

                if (isGuid) return field.ParameterId == parameterId;

                string name;
                try { name = field.GetName(); } catch { name = string.Empty; }

                string heading;
                try { heading = field.ColumnHeading; } catch { heading = string.Empty; }

                return string.Equals(name, slot, StringComparison.Ordinal) ||
                       string.Equals(heading, slot, StringComparison.Ordinal);
            });

            if (index < 0) continue;   // this schedule does not carry that column; skip its slot

            ordered.Add(remaining[index]);
            remaining.RemoveAt(index);
        }

        // WHATEVER IS LEFT is a column neither this class nor the office template names -
        // appended after everything recognised, in its existing relative order, rather than
        // lost. This is also what keeps SetFieldOrder's "exact same set" requirement satisfied
        // regardless of what else a particular schedule happens to carry.
        ordered.AddRange(remaining);

        // A no-op reorder is still a document change and an undo entry; skip it when nothing
        // actually needs to move.
        if (ordered.SequenceEqual(current)) return;

        try
        {
            definition.SetFieldOrder(ordered);
        }
        catch (Exception ex)
        {
            problems.Add($"Could not reorder the columns of '{SafeName(schedule)}': {ex.Message}");
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

    private static string SafeName(Element element)
    {
        try { return element.Name; }
        catch { return "?"; }
    }
}
