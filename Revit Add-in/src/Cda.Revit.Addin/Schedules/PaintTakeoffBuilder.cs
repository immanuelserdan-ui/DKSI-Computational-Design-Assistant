using System.Globalization;
using Autodesk.Revit.DB;
using Cda.Revit.Addin.Finishes;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Schedules;

/// <summary>
/// Generates the per-room paint takeoff: one element per (room, surface, material), so a
/// schedule can have as many rows as the building has painted faces.
///
/// WHY THIS EXISTS AT ALL
///   A Wall Material Takeoff has one row per (wall, material) and no room dimension. 'Rum' is
///   a parameter on the WALL, so a wall between Alrum and Bad has one slot for two correct
///   answers, and whichever room loses that slot loses its face from the schedule. Observed in
///   the test model: a bathroom with FOUR painted wall faces arrived as TWO rows, because two
///   of its faces sit on walls the neighbouring rooms own. Bad's paint totals 21.26 m2 and the
///   schedule showed 11.91 m2.
///
///   No rule for choosing the owner can fix that. There are only two wall elements filed under
///   Bad, so there can only be two rows. The row count is the problem, and the only way to
///   raise it is to give each row its own element.
///
/// WHAT THESE ELEMENTS ARE
///   Generic Model DirectShapes with no geometry, placed once per takeoff row. They are data
///   carriers, not model content: nothing to see in a view, nothing to clash with, nothing to
///   print. They carry Lejlighed / Rum nr / Rum on the SAME shared parameters the walls use,
///   plus Paint Surface, Paint Material and Paint Area.
///
///   The numbers are not recomputed here. They are the engine's own per-room per-material
///   rows - the same values written to the CSV - so the takeoff cannot drift from the report.
///
/// WHY REGENERATED RATHER THAN UPDATED
///   The set of rows changes shape as the model changes: paint a new face and a row appears,
///   change a material and one row becomes another. Matching old rows to new ones would be
///   guesswork, and a stale row is a quantity nobody can trace. Every run deletes what this
///   tool placed - found by storage stamp, so it also clears rows from earlier sessions - and
///   places the current answer.
/// </summary>
internal static class PaintTakeoffBuilder
{
    /// <summary>Extensible Storage tool name. The identity that makes regeneration exact.</summary>
    public const string Stamp = "DKSI paint takeoff";

    public const string TransactionPrefix = "DKSI paint takeoff: ";

    public sealed class Result
    {
        public int Placed { get; init; }
        public int Removed { get; init; }
        public double TotalSqm { get; init; }
        public IReadOnlyList<string> Notes { get; init; } = [];
    }

    /// <summary>
    /// Replaces the takeoff with one built from <paramref name="rows"/>. Caller owns nothing;
    /// this opens its own transaction and must run in a valid API context.
    /// </summary>
    public static Result Build(Document doc, FinishSettings settings, IReadOnlyList<FinishCsvRow> rows)
    {
        var notes = new List<string>();

        // PAINTED ROWS ONLY. The CSV also carries unpainted substrate - "EM Wall" and the
        // like - because the finish report needs to show what was measured but not painted.
        // A PAINT takeoff that listed them would overstate the cost basis.
        var painted = rows.Where(r => r.Painted && r.AreaSqM > 0.0005).ToList();

        var placed = 0;
        var removed = 0;
        var unwritable = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            Transactions.Run(doc, TransactionPrefix + "rebuild", () =>
            {
                removed = DeleteExisting(doc);

                foreach (var row in painted)
                {
                    if (Place(doc, settings, row, unwritable)) placed++;
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error("Paint takeoff could not be built", ex);
            return new Result { Notes = [$"Paint takeoff failed: {ex.Message}"] };
        }

        if (placed < painted.Count)
        {
            notes.Add(
                $"{painted.Count - placed} row(s) could not be placed. The takeoff is short by " +
                "that much; see the log.");
        }

        // A ROW THAT TOOK NO AREA IS NOT A ROW. Placing succeeds whenever the element is
        // created, which it almost always is - the writes are what can fail, and until now
        // they failed in silence. So a run could report "89 row(s) placed" over a schedule
        // with an empty quantity column, which is the most expensive way this tool can be
        // wrong: it looks finished.
        if (unwritable.Contains(settings.PaintAreaParameter))
        {
            notes.Add(
                $"THE AREA DID NOT WRITE. '{settings.PaintAreaParameter}' is not bound to Generic " +
                "Models as a writable Area parameter, so every row placed above carries no " +
                "quantity and the schedule's area column and grand total will be empty. The rows " +
                "themselves are otherwise correct. Run 'Set Up Finish Schedules' to bind it, then " +
                "run this again.");
        }

        var blankColumns = unwritable.Where(n => n != settings.PaintAreaParameter).Order().ToList();

        if (blankColumns.Count > 0)
        {
            notes.Add(
                $"BLANK COLUMN(S): {string.Join(", ", blankColumns)} could not be written to the " +
                "takeoff rows, so those columns will be empty in the schedule. The areas and their " +
                "room attribution are unaffected - this costs grouping, not quantities. " +
                "'Set Up Finish Schedules' binds them.");
        }

        var total = painted.Sum(r => r.AreaSqM);

        Log.Info($"Paint takeoff: {placed} row(s) placed, {removed} removed, {total:0.00} m² total.");

        return new Result
        {
            Placed = placed,
            Removed = removed,
            TotalSqm = total,
            Notes = notes,
        };
    }

    // ---------------------------------------------------------------------- place

    private static bool Place(
        Document doc, FinishSettings settings, FinishCsvRow row, HashSet<string> unwritable)
    {
        try
        {
            var shape = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));

            // NO GEOMETRY, DELIBERATELY. SetShape is simply never called. These rows are data,
            // and a DirectShape with an empty shape is a valid element that draws nothing - so
            // the takeoff cannot appear in a view, be cut by a section box, or be mistaken for
            // something to build. It still schedules, which is the whole point.
            shape.Name = $"{Stamp} - {row.RoomNumber} {row.Surface} {row.HostType} {row.Material}";

            try
            {
                shape.ApplicationId = "DKSI";
                shape.ApplicationDataId = $"paint-takeoff-{row.RoomNumber}-{row.Surface}-{row.Material}";
            }
            catch
            {
                // Informational only; the storage stamp below is what regeneration relies on.
            }

            // A row that takes none of these is an empty element in the model pretending to
            // be a quantity. Every failure is collected by NAME so the run can say which
            // column will be blank, rather than reporting a placed row and leaving the blank
            // to be found in the schedule.
            Write(shape, settings.ApartmentParameter, row.Apartment, unwritable);
            Write(shape, settings.RoomNumberParameter, row.RoomNumber, unwritable);
            Write(shape, settings.RoomNameParameter, row.RoomName, unwritable);
            Write(shape, settings.PaintSurfaceParameter, row.Surface, unwritable);
            Write(shape, settings.PaintMaterialParameter, row.Material, unwritable);
            Write(shape, settings.PaintTypeParameter, row.HostType, unwritable);

            // Blank for the fallback bucket, which has no single host. Writing "-1" would look
            // like an element id and send someone hunting for an element that does not exist.
            Write(shape, settings.PaintHostParameter,
                row.HostId < 0 ? string.Empty : row.HostId.ToString(CultureInfo.InvariantCulture),
                unwritable);

            WriteArea(shape, settings.PaintAreaParameter, row.AreaSqM, unwritable);

            // THE OWNING ROOM'S OWN FIGURES, under the names they already carry on the Room.
            //
            // Written only into the pair matching THIS row's surface, so the heading always
            // matches what is under it: a Walls row fills 'Wall Paint Area' / 'Wall Finish Area'
            // and leaves the floor and ceiling pairs empty. A ceiling total sitting under a
            // column headed 'Wall Paint Area' would be worse than a blank one.
            //
            // Reference figures, not quantities - identical on every row of the same room, so
            // summing them multiplies the room by its row count. See the note in FinishSettings.
            var (paintName, finishName) = row.Surface switch
            {
                FinishSettings.SurfaceFloor =>
                    (settings.FloorPaintParameter, settings.FloorParameter),
                FinishSettings.SurfaceCeiling =>
                    (settings.CeilingPaintParameter, settings.CeilingParameter),
                _ => (settings.PaintParameter, settings.WallParameter),
            };

            WriteArea(shape, paintName, row.RoomSurfacePaintSqM, unwritable);
            WriteArea(shape, finishName, row.RoomSurfaceFinishSqM, unwritable);

            ElementStamp.Write(shape, Stamp, row.Surface);

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Paint takeoff: row {row.RoomNumber}/{row.Surface}/{row.Material} rejected: {ex.Message}");
            return false;
        }
    }

    private static void Write(
        Element element, string name, string value, HashSet<string> unwritable)
    {
        try
        {
            var parameter = ParameterHelper.Find(element, name);
            if (parameter is { IsReadOnly: false, StorageType: StorageType.String })
            {
                parameter.Set(value ?? string.Empty);
                return;
            }
        }
        catch
        {
            // Falls through to the same record as an unbound parameter: from the schedule's
            // point of view a throw and a missing binding are the same blank column.
        }

        unwritable.Add(name);
    }

    private static void WriteArea(
        Element element, string name, double squareMetres, HashSet<string> unwritable)
    {
        try
        {
            var parameter = ParameterHelper.Find(element, name);
            if (parameter is { IsReadOnly: false, StorageType: StorageType.Double })
            {
                parameter.Set(Measure.FromSquareMetres(squareMetres));
                return;
            }
        }
        catch
        {
            // As above.
        }

        unwritable.Add(name);
    }

    // ---------------------------------------------------------------------- schedule

    public const string ScheduleName = "DKSI Paint Takeoff by Room";

    /// <summary>
    /// Finds or creates the schedule view over the takeoff rows. Caller owns the transaction.
    /// </summary>
    /// <returns>The view, or null with the reason appended to <paramref name="problems"/>.</returns>
    public static ViewSchedule? EnsureSchedule(
        Document doc, FinishSettings settings, List<string> problems)
    {
        var existing = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .FirstOrDefault(v => !v.IsTemplate &&
                                 string.Equals(v.Name, ScheduleName, StringComparison.Ordinal));

        // Reused rather than recreated. The schedule is a VIEW - someone may have placed it on
        // a sheet, changed its column widths or added a filter - and replacing it would throw
        // that away every run. The rows underneath are regenerated; the view is not.
        //
        // BUT REUSED IS NOT THE SAME AS CORRECT, and that gap cost a full session to find. A
        // view carried a column called 'Room: Wall Paint Area' - a ROOM-relationship field, not
        // this tool's 'Paint Area'. The takeoff rows are geometry-less DirectShapes, so they
        // are inside no room, so that field resolves to nothing on every row forever. The
        // elements held their areas the whole time; the view simply never asked for them, and
        // the tool reported success and pointed at a schedule with an empty quantity column.
        //
        // So verify the one field the schedule exists to show, and add it back if it is
        // missing. Nothing is removed - an extra column is someone's business and might be
        // deliberate, while a MISSING area column is never deliberate.
        if (existing is not null)
        {
            EnsureFields(existing, settings, problems);
            return existing;
        }

        ViewSchedule schedule;
        try
        {
            schedule = ViewSchedule.CreateSchedule(
                doc, new ElementId(BuiltInCategory.OST_GenericModel));
        }
        catch (Exception ex)
        {
            problems.Add($"Could not create the takeoff schedule: {ex.Message}");
            return null;
        }

        try { schedule.Name = ScheduleName; }
        catch (Exception ex) { problems.Add($"Could not name the schedule: {ex.Message}"); }

        AddFields(schedule, settings, problems);

        return schedule;
    }

    /// <summary>
    /// Adds any expected column a reused schedule is missing, and says which.
    ///
    /// WHY THIS IS NOT JUST THE AREA COLUMN ANY MORE
    ///   The first version checked only <see cref="FinishSettings.PaintAreaParameter"/>, which
    ///   fixed the failure that prompted it and nothing else. The very next change added
    ///   'Paint Host Id' - and it never appeared, because a schedule that already exists never
    ///   runs <see cref="AddFields"/> and the repair was not looking for it. The user had to
    ///   delete the view by hand to get a complete one, which is the thing reuse exists to
    ///   avoid. Any column added from now on has the same problem unless the check is general.
    ///
    ///   So it now walks the same <see cref="WantedFields"/> list the builder uses. One list,
    ///   both paths, and a new column reaches an existing schedule on the next run.
    ///
    /// NOTHING IS EVER REMOVED. An extra column might be deliberate; a missing one never is.
    /// Appended in order at the end rather than inserted, because a schedule someone has
    /// arranged is theirs, and moving their columns to satisfy a default order would be a
    /// bigger surprise than a column arriving on the right.
    ///
    /// Matched on the field's own name rather than the column heading, because a heading is
    /// editable text: renaming a column must not cause a duplicate, and naming an unrelated
    /// column 'Paint Area' must not make it skip a real repair.
    /// </summary>
    private static void EnsureFields(
        ViewSchedule schedule, FinishSettings settings, List<string> problems)
    {
        var definition = schedule.Definition;

        var present = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var fieldId in definition.GetFieldOrder())
                present.Add(definition.GetField(fieldId).GetName());
        }
        catch (Exception ex)
        {
            // Unreadable field list. Say so rather than adding duplicates of columns that may
            // already be there.
            problems.Add(
                $"Could not check which columns '{ScheduleName}' still shows, so it was left " +
                $"alone: {ex.Message}");
            return;
        }

        var missing = WantedFields(settings).Where(name => !present.Contains(name)).ToList();
        if (missing.Count == 0) return;

        var available = new Dictionary<string, SchedulableField>(StringComparer.Ordinal);

        try
        {
            foreach (var candidate in definition.GetSchedulableFields())
            {
                try { available.TryAdd(candidate.GetName(schedule.Document), candidate); }
                catch { /* a field that will not name itself cannot be matched */ }
            }
        }
        catch (Exception ex)
        {
            problems.Add($"Could not read the available fields for '{ScheduleName}': {ex.Message}");
            return;
        }

        var added = new List<string>();
        var unavailable = new List<string>();

        foreach (var name in missing)
        {
            if (!available.TryGetValue(name, out var field))
            {
                unavailable.Add(name);
                continue;
            }

            try
            {
                definition.AddField(field);
                added.Add(name);
            }
            catch (Exception ex)
            {
                problems.Add($"Could not add '{name}' to '{ScheduleName}': {ex.Message}");
            }
        }

        if (added.Count > 0)
        {
            // Named individually because the area column going missing is a different event
            // from a new column arriving, and the reader needs to know which happened.
            problems.Add(
                $"'{ScheduleName}' was missing {added.Count} column(s) - {string.Join(", ", added)} " +
                "- so they have been added at the end. " +
                (added.Contains(settings.PaintAreaParameter)
                    ? "That includes the AREA column, so until now every row's quantity was " +
                      "invisible in this schedule. "
                    : string.Empty) +
                "Any column beginning 'Room:' is a ROOM-relationship field and will always be " +
                "blank on these rows - they are geometry-less and sit in no room. Delete those.");
        }

        if (unavailable.Count > 0)
        {
            problems.Add(
                $"{string.Join(", ", unavailable)} could not be added to '{ScheduleName}' because " +
                "the field is not available on Generic Models. Run 'Set Up Finish Schedules' to " +
                "bind it, then run this again.");
        }
    }

    /// <summary>
    /// The takeoff's columns, in the order they are added to a new schedule. Shared with
    /// <see cref="EnsureFields"/> so a column added here reaches existing schedules too.
    ///
    /// The order mirrors the Wall Material Takeoff people already read, with the two reference
    /// totals last - they are the least important thing on the row and must not sit where a
    /// reader's eye lands on them as the quantity.
    /// </summary>
    private static string[] WantedFields(FinishSettings settings) =>
    [
        settings.ApartmentParameter,
        settings.RoomNumberParameter,
        settings.RoomNameParameter,
        settings.PaintSurfaceParameter,
        settings.PaintTypeParameter,
        settings.PaintHostParameter,
        settings.PaintMaterialParameter,
        settings.PaintAreaParameter,

        // The room's own wall figures, for comparison against the row. Only the WALL pair is a
        // default column: wall rows are the bulk of any takeoff, and adding all six would put
        // four mostly-empty columns on an already wide schedule. The floor and ceiling
        // equivalents are bound and written - add them by hand if you schedule those surfaces.
        settings.PaintParameter,
        settings.WallParameter,
    ];

    /// <summary>
    /// Fields are resolved by NAME from what the schedule says it can show, never by parameter
    /// id - a shared parameter's id differs per model, so a hardcoded id works in exactly one
    /// file. Same approach as the ceiling takeoff builder, for the same reason.
    /// </summary>
    private static void AddFields(ViewSchedule schedule, FinishSettings settings, List<string> problems)
    {
        var definition = schedule.Definition;

        var available = new Dictionary<string, SchedulableField>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in definition.GetSchedulableFields())
        {
            try { available.TryAdd(field.GetName(schedule.Document), field); }
            catch { /* a field that will not name itself cannot be matched */ }
        }

        // One list, shared with EnsureFields, so a column added to the takeoff reaches both a
        // new schedule and an existing one.
        var wanted = WantedFields(settings);

        ScheduleFieldId? areaField = null;
        var added = new Dictionary<string, ScheduleFieldId>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in wanted)
        {
            if (!available.TryGetValue(name, out var field))
            {
                problems.Add(
                    $"'{name}' is not available as a field, so the schedule is missing that " +
                    "column. Run Set Up Finish Schedules first - it binds these to Generic Models.");
                continue;
            }

            try
            {
                var scheduleField = definition.AddField(field);
                added[name] = scheduleField.FieldId;

                if (name == settings.PaintAreaParameter) areaField = scheduleField.FieldId;
            }
            catch (Exception ex)
            {
                problems.Add($"Could not add the field '{name}': {ex.Message}");
            }
        }

        // Grouped the way a Roombook is read: apartment, then room, then surface. Without the
        // sort the rows arrive in element-creation order, which is meaningless to a reader.
        foreach (var name in new[]
                 {
                     settings.ApartmentParameter,
                     settings.RoomNumberParameter,
                     settings.PaintSurfaceParameter,
                 })
        {
            if (!added.TryGetValue(name, out var fieldId)) continue;

            try { definition.AddSortGroupField(new ScheduleSortGroupField(fieldId)); }
            catch { /* a field Revit will not sort on is not worth failing the schedule for */ }
        }

        if (areaField is null) return;

        try
        {
            definition.IsItemized = true;
            definition.ShowGrandTotal = true;
            definition.ShowGrandTotalTitle = true;
        }
        catch (Exception ex)
        {
            problems.Add($"Could not switch on the totals: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------- clear

    /// <summary>Removes the takeoff. Opens its own transaction.</summary>
    public static int Clear(Document doc)
    {
        var removed = 0;

        try
        {
            Transactions.Run(doc, TransactionPrefix + "clear", () => removed = DeleteExisting(doc));
        }
        catch (Exception ex)
        {
            Log.Warn($"Paint takeoff clear failed: {ex.Message}");
            return 0;
        }

        return removed;
    }

    /// <summary>
    /// Deletes every takeoff row this tool has placed, found by its storage mark. Caller owns
    /// the transaction.
    /// </summary>
    private static int DeleteExisting(Document doc)
    {
        var collector = new FilteredElementCollector(doc)
            .OfClass(typeof(DirectShape))
            .WhereElementIsNotElementType();

        var stamped = ElementStamp.Filter();
        var candidates = stamped is null ? collector : collector.WherePasses(stamped);

        var ours = new List<ElementId>();

        foreach (var element in candidates)
        {
            try
            {
                if (ElementStamp.Read(element, Stamp, Stamp) is not null) ours.Add(element.Id);
            }
            catch
            {
                // Unreadable storage; not ours as far as we can tell, so leave it.
            }
        }

        if (ours.Count == 0) return 0;

        try
        {
            doc.Delete(ours);
            return ours.Count;
        }
        catch (Exception ex)
        {
            Log.Warn($"Paint takeoff: delete refused: {ex.Message}");
            return 0;
        }
    }
}
