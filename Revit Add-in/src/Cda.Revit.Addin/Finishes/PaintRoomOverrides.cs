using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Finishes;

/// <summary>One takeoff row deliberately reported under a room other than the one it faces.</summary>
/// <param name="Key">
/// Identity of the ROW, not of the element. Carriers are deleted and re-placed on every
/// takeoff run, so an override keyed to an element id would survive exactly one run. See
/// <see cref="PaintRoomOverrides.KeyFor"/>.
/// </param>
internal sealed record PaintRoomOverride(
    string Key,
    string ToRoomNumber,
    string ToRoomName,
    string FromRoomNumber,
    string FromRoomName,
    double AreaSqFtAtWrite,
    string Reason,
    string Author,
    DateTime WrittenUtc);

/// <summary>
/// A durable table of "this painted surface is reported under THAT room", applied to the
/// takeoff's carriers after every run.
///
/// WHY THIS HAS TO EXIST AT ALL
///   The takeoff attributes a painted face to the room the face fronts, which is right almost
///   always and is not negotiable geometrically - a face cannot front two rooms. But the
///   Danish rules for stair sections sometimes put the finish under a run in the room ABOVE,
///   and no room-bounding arrangement expresses that: the surface is physically in the lower
///   room and Revit will always say so. The attribution is a REPORTING decision that geometry
///   cannot make, so it is recorded as a decision rather than faked as geometry.
///
/// WHY IT IS NOT STORED ON THE CARRIER
///   Because the carrier does not survive. Every takeoff run deletes every element stamped by
///   the product and places a fresh set, so anything written on a carrier lasts until the next
///   run and then vanishes without trace. The override lives in project-level Extensible
///   Storage on a DataStorage element, and is re-applied to whatever carriers currently exist.
///
/// WHAT IT DELIBERATELY DOES NOT DO
///   It does not move area. The square metres stay exactly where they were measured; only the
///   room the row is REPORTED under changes, and both the original room and the new one are
///   recorded so the move can always be read back. It also does not touch the Room element's
///   own finish parameters - those are computed from geometry by the engine, so an overridden
///   row and its old room's total will disagree by the overridden amount. That is a real
///   consequence and the command says so rather than hiding it.
/// </summary>
internal static class PaintRoomOverrides
{
    /// <summary>
    /// Fixed for the life of the add-in. Changing it orphans every override already recorded,
    /// in every model, with no way to find them again.
    /// </summary>
    private static readonly Guid SchemaId = new("e4a91c72-6b58-4d0f-9c37-8a15f2d0b6e3");

    private const string SchemaName = "DksiPaintRoomOverrides";
    private const string EntriesField = "Entries";
    private const string VendorId = "DKSI";

    /// <summary>
    /// Field separator. UNIT SEPARATOR, not a comma or a pipe: room names are user text and
    /// will eventually contain both. U+001F cannot be typed into a Revit parameter, so a
    /// record can never be split by its own content.
    /// </summary>
    public const char Separator = '';

    /// <summary>
    /// Appended to a reassigned row's Room Name so it reads as reassigned at a glance, in
    /// Revit's schedule AND in anything exported from it.
    ///
    /// A REAL TEXT VALUE, DELIBERATELY. Revit's schedule API has no per-row cell style for a
    /// standard schedule's data rows - confirmed against RevitAPI.xml's own documented
    /// exception ("Only allow to override cell style for header section or column header in
    /// body section") rather than assumed - so a colour or bold override on one data row is not
    /// achievable at all. Text in the Room Name parameter is the only mechanism left that shows
    /// up both on screen and in Export Schedules, which reads cell text with
    /// ViewSchedule.GetCellText and therefore sees whatever this writes here.
    ///
    /// PLAIN ASCII PUNCTUATION ON PURPOSE. This office template is Danish; an exotic glyph is
    /// one more thing that can render as a tofu box in an unrelated font somewhere downstream
    /// (a PDF export, a different machine's font substitution). "(Reassigned)" degrades to
    /// nothing worse than itself.
    /// </summary>
    internal const string ReassignedMarker = PaintOverrideOwnership.ReassignedMarker;

    private static Schema? _schema;

    private static Schema? Resolve()
    {
        if (_schema is not null && _schema.IsValidObject) return _schema;

        try
        {
            _schema = Schema.Lookup(SchemaId);
            if (_schema is not null) return _schema;

            var builder = new SchemaBuilder(SchemaId);
            builder.SetSchemaName(SchemaName);
            builder.SetVendorId(VendorId);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddArrayField(EntriesField, typeof(string));

            _schema = builder.Finish();
            return _schema;
        }
        catch (Exception ex)
        {
            Log.Error("Paint room override schema unavailable", ex);
            return null;
        }
    }

    /// <summary>
    /// The row's identity, stable across regeneration.
    ///
    /// 'Paint Segment' names the host element and the face - "IV_Mål - 100mm #29317994 ·
    /// Face 0.2 R1" - and the material completes it, because one face carries one row per
    /// material. Both are rebuilt identically on every run from the same geometry, which is
    /// exactly the property an override key needs.
    ///
    /// It is an in-document identity: it embeds an ElementId, so it does not survive the model
    /// being copied into another project. That is the right trade here - overrides are a
    /// property of one building's paperwork - and the alternative, a UniqueId, is not
    /// something the carrier carries.
    /// </summary>
    public static string? KeyFor(Element carrier)
    {
        try
        {
            var segment = Text(carrier, "Paint Segment");
            var material = Text(carrier, "Paint Material Name", "Paint Material");

            if (string.IsNullOrWhiteSpace(segment)) return null;

            return $"{segment}{Separator}{material}";
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<PaintRoomOverride> Read(Document doc)
    {
        var schema = Resolve();
        if (schema is null) return [];

        var store = FindStore(doc, schema);
        if (store is null) return [];

        try
        {
            using var entity = store.GetEntity(schema);
            if (!entity.IsValid()) return [];

            var raw = entity.Get<IList<string>>(EntriesField);
            if (raw is null) return [];

            var parsed = new List<PaintRoomOverride>();

            foreach (var line in raw)
            {
                var item = Parse(line);
                if (item is not null) parsed.Add(item);
            }

            return parsed;
        }
        catch (Exception ex)
        {
            Log.Warn($"Paint room overrides could not be read: {ex.Message}");
            return [];
        }
    }

    /// <summary>Replaces the whole table. Caller owns the transaction.</summary>
    public static bool Write(Document doc, IReadOnlyList<PaintRoomOverride> all)
    {
        var schema = Resolve();
        if (schema is null) return false;

        try
        {
            var store = FindStore(doc, schema);

            // AN EMPTY TABLE IS DELETED, NOT WRITTEN. Extensible Storage does not reliably
            // accept a zero-length array field - the behaviour differs between releases - and
            // "the user removed the last override" is a state that has to work every time.
            // Removing the DataStorage says the same thing unambiguously and leaves nothing
            // behind for the next Read to trip over.
            if (all.Count == 0)
            {
                if (store is not null) doc.Delete(store.Id);
                return true;
            }

            store ??= DataStorage.Create(doc);

            using var entity = new Entity(schema);

            // THE STATIC TYPE OF THE ARGUMENT IS THE BUG SURFACE. Entity.Set<FieldType> infers
            // FieldType from the compile-time type of what is passed, not from the schema - so
            // handing it a bare .ToList() infers List<string> and Revit rejects it at runtime
            // with "Unsupported type", even though the schema's array field is exactly this
            // data. AddArrayField expects IList<string>, so the value has to be typed as that
            // explicitly; List<string> being assignable to IList<string> does not help; C# only
            // widens the type here if this line is annotated to force it.
            //
            // Silent until now because this method already swallows its own exception and
            // logs rather than throws - see the catch below - so every write since this class
            // was introduced has quietly done nothing while Apply() kept working from the
            // caller's in-memory list and made the override LOOK saved. It only failed once
            // something re-read storage instead of using that list: a fresh command, the
            // takeoff's own auto re-apply, or reopening the file.
            IList<string> entries = all.Select(Format).ToList();
            entity.Set(EntriesField, entries);

            store.SetEntity(entity);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Paint room overrides could not be written", ex);
            return false;
        }
    }

    /// <summary>
    /// Writes Room Name / Room Number on every carrier an override OWNS - same key, and in the
    /// override's from-room (see <see cref="PaintOverrideOwnership"/>) - and reports what it
    /// touched. Carriers that only share the key are left alone. Caller owns the transaction.
    ///
    /// Run this after every takeoff, or the overrides are silently absent from the schedule
    /// while remaining recorded in the model - the most confusing of the possible states.
    /// </summary>
    public static int Apply(Document doc, out IReadOnlyList<string> report) =>
        Apply(doc, Read(doc), out report);

    /// <summary>
    /// Applies a table the caller already holds.
    ///
    /// THIS OVERLOAD IS NOT A CONVENIENCE. Writing an override and then applying it in the
    /// same transaction cannot go through Read: Write may have just created the DataStorage,
    /// and a newly created element is invisible to a FilteredElementCollector until the
    /// document regenerates. Read therefore came back empty and the first override a user
    /// ever recorded reported "0 row(s)" and moved nothing - while sitting correctly in
    /// storage, so it appeared on the NEXT run and looked like a different bug entirely.
    /// </summary>
    public static int Apply(
        Document doc, IReadOnlyList<PaintRoomOverride> overrides, out IReadOnlyList<string> report)
    {
        var lines = new List<string>();

        if (overrides.Count == 0)
        {
            report = lines;
            return 0;
        }

        // A LIST PER KEY, NOT ONE OVERRIDE PER KEY. The key is not unique across rooms - see
        // PaintOverrideOwnership - so two overrides may share one, and which of them (if either)
        // owns a carrier is decided by the carrier's room, per carrier.
        var byKey = GroupByKey(overrides);

        var applied = 0;
        var matched = new HashSet<PaintRoomOverride>();

        // Carriers sharing an override's key that it does NOT own, by override - so an override
        // that matched nothing can say where its surface went instead of just "gone".
        var seenElsewhere = new Dictionary<PaintRoomOverride, SortedSet<string>>();

        foreach (var carrier in Carriers(doc))
        {
            var key = KeyFor(carrier);
            if (key is null || !byKey.TryGetValue(key, out var sameKey)) continue;

            var roomNumber = RoomNumberOf(carrier);
            var roomName = RoomNameOf(carrier);

            var wanted = PaintOverrideOwnership.OwnerOf(
                sameKey, roomNumber, roomName, o => o.FromRoomNumber, o => o.ToRoomNumber, out _);

            if (wanted is null)
            {
                // SHARES THE KEY, NOT THE ROOM: another room's piece of the same surface. Left
                // exactly as the takeoff wrote it. Relabelling it was the defect this replaces.
                foreach (var o in sameKey)
                {
                    if (!seenElsewhere.TryGetValue(o, out var rooms))
                        seenElsewhere[o] = rooms = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    rooms.Add($"{roomNumber} {roomName}".Trim());
                }

                continue;
            }

            matched.Add(wanted);

            // NUMBER STAYS PURE. It is what the schedule groups and sorts on, and what this
            // class matches overrides back onto a room by - marker text there would break both.
            var wroteNumber = SetText(carrier, wanted.ToRoomNumber, "Room Number", "Rum nr");
            var wroteName = SetText(carrier, $"{wanted.ToRoomName}{ReassignedMarker}", "Room Name", "Rum");

            if (!wroteNumber && !wroteName)
            {
                lines.Add($"Carrier {carrier.Id.Value}: room parameters are read-only; not moved.");
                continue;
            }

            applied++;

            lines.Add(
                $"{wanted.FromRoomNumber} {wanted.FromRoomName} -> {wanted.ToRoomNumber} " +
                $"{wanted.ToRoomName}: {Measure.ToSquareMetres(wanted.AreaSqFtAtWrite):0.###} m² " +
                $"on {wanted.Key.Replace(Separator, '/')}" +
                (string.IsNullOrWhiteSpace(wanted.Reason) ? string.Empty : $" - {wanted.Reason}"));

            // REPORTED, NOT ENFORCED. A different area is how a renumbered boundary shows up - the
            // key now naming a different face of the same wall in the same room - but a genuine
            // edit to the face changes the area too, and dropping the override then would be the
            // worse error. So it applies, and says so.
            var area = AreaOf(carrier);
            if (PaintOverrideOwnership.AreaDrifted(area, wanted.AreaSqFtAtWrite))
            {
                lines.Add(
                    $"CHECK AREA: carrier {carrier.Id.Value} on '{wanted.Key.Replace(Separator, '/')}' " +
                    $"now measures {Measure.ToSquareMetres(area):0.###} m², but the override was recorded " +
                    $"at {Measure.ToSquareMetres(wanted.AreaSqFtAtWrite):0.###} m². Confirm it is still the " +
                    "same surface.");
            }
        }

        // A RECORDED OVERRIDE WITH NO ROW IS NOT A NON-EVENT. It means the surface it named has
        // stopped being produced in its from-room - the paint was removed, the split face was
        // deleted, the wall was rebuilt, the room was renumbered - and the override is now
        // describing something that does not exist. Left unreported it would sit in the model
        // forever, waiting to reattach itself to whatever eventually takes that key.
        foreach (var orphan in overrides.Distinct().Where(o => !matched.Contains(o)))
        {
            var elsewhere = seenElsewhere.TryGetValue(orphan, out var rooms) && rooms.Count > 0
                ? $" That surface is still produced, but in {string.Join(", ", rooms)} - not in " +
                  $"{orphan.FromRoomNumber} {orphan.FromRoomName} - so it was NOT moved. If the " +
                  "room was renumbered, reassign the row again."
                : string.Empty;

            lines.Add(
                $"NO ROW MATCHES: '{orphan.Key.Replace(Separator, '/')}' from {orphan.FromRoomNumber} " +
                $"{orphan.FromRoomName} was to be reported under {orphan.ToRoomNumber} " +
                $"{orphan.ToRoomName}, but the takeoff no longer produces that surface in that room. " +
                "The override is still recorded and will apply again if it returns." + elsewhere);
        }

        report = lines;
        return applied;
    }

    /// <summary>
    /// Writes plain, unmarked Room Number / Room Name onto one carrier - what a row looks like
    /// with no override in force.
    ///
    /// FOR THE INSTANT the override for that row is removed, so the row does not keep showing
    /// its old target room until the next takeoff or re-apply happens to touch that key. Callers
    /// remove the entry from the table FIRST, then call this directly on the affected carrier -
    /// it never reads the table itself, because by the time it is worth calling, the table no
    /// longer has anything to say about this key.
    /// </summary>
    public static bool RestoreNatural(Element carrier, string number, string name)
    {
        var wroteNumber = SetText(carrier, number, "Room Number", "Rum nr");
        var wroteName = SetText(carrier, name, "Room Name", "Rum");
        return wroteNumber || wroteName;
    }

    /// <summary>
    /// The override that owns <paramref name="carrier"/>, or null - decided by the carrier's key
    /// AND its room, never the key alone. See <see cref="PaintOverrideOwnership.OwnerOf{T}"/>.
    /// </summary>
    public static PaintRoomOverride? OwnerOf(
        IReadOnlyList<PaintRoomOverride> overrides, Element carrier, out bool ambiguous)
    {
        ambiguous = false;

        var key = KeyFor(carrier);
        if (key is null) return null;

        var sameKey = overrides.Where(o => string.Equals(o.Key, key, StringComparison.Ordinal)).ToList();
        if (sameKey.Count == 0) return null;

        return PaintOverrideOwnership.OwnerOf(
            sameKey, RoomNumberOf(carrier), RoomNameOf(carrier),
            o => o.FromRoomNumber, o => o.ToRoomNumber, out ambiguous);
    }

    /// <summary>The Room Number the carrier currently shows - the same names, in the same order, <see cref="SetText"/> writes.</summary>
    public static string RoomNumberOf(Element carrier) => Text(carrier, "Room Number", "Rum nr");

    /// <summary>The Room Name the carrier currently shows, "(Reassigned)" marker included if present.</summary>
    public static string RoomNameOf(Element carrier) => Text(carrier, "Room Name", "Rum");

    /// <summary>The carrier's measured area in square feet, or 0 when it carries none.</summary>
    public static double AreaOf(Element carrier)
    {
        foreach (var name in new[] { "Painted Surface Area", "Paint Area" })
        {
            try
            {
                var parameter = ParameterHelper.Find(carrier, name);

                if (parameter is not null && parameter.StorageType == StorageType.Double)
                {
                    var value = parameter.AsDouble();
                    if (value > 0) return value;
                }
            }
            catch
            {
                // Try the next name.
            }
        }

        return 0;
    }

    private static Dictionary<string, IReadOnlyList<PaintRoomOverride>> GroupByKey(
        IEnumerable<PaintRoomOverride> overrides) =>
        overrides
            .GroupBy(o => o.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PaintRoomOverride>)g.ToList(), StringComparer.Ordinal);

    /// <summary>Every takeoff carrier, from either product.</summary>
    public static IEnumerable<Element> Carriers(Document doc)
    {
        foreach (var element in new FilteredElementCollector(doc)
                     .OfClass(typeof(DirectShape))
                     .WhereElementIsNotElementType())
        {
            if (!string.IsNullOrWhiteSpace(Text(element, "Paint Segment"))) yield return element;
        }
    }

    // ------------------------------------------------------------------ storage plumbing

    private static DataStorage? FindStore(Document doc, Schema schema)
    {
        try
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(DataStorage))
                .Cast<DataStorage>()
                .FirstOrDefault(d =>
                {
                    try
                    {
                        using var entity = d.GetEntity(schema);
                        return entity.IsValid();
                    }
                    catch
                    {
                        return false;
                    }
                });
        }
        catch
        {
            return null;
        }
    }

    private static string Format(PaintRoomOverride o) => string.Join(Separator,
        o.Key,
        o.ToRoomNumber,
        o.ToRoomName,
        o.FromRoomNumber,
        o.FromRoomName,
        o.AreaSqFtAtWrite.ToString("R"),
        o.Reason,
        o.Author,
        o.WrittenUtc.ToString("O"));

    private static PaintRoomOverride? Parse(string line)
    {
        try
        {
            // The KEY itself contains one separator, so the record has 9 fields of which the
            // first two belong to the key. Split with a cap rather than blindly.
            var parts = line.Split(Separator);
            if (parts.Length < 10) return null;

            var key = $"{parts[0]}{Separator}{parts[1]}";

            return new PaintRoomOverride(
                key,
                parts[2], parts[3], parts[4], parts[5],
                double.TryParse(parts[6], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var area) ? area : 0,
                parts[7], parts[8],
                DateTime.TryParse(parts[9], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var when) ? when : DateTime.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    private static string Text(Element element, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var value = ParameterHelper.Find(element, name)?.AsString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch
            {
                // Try the next name.
            }
        }

        return string.Empty;
    }

    private static bool SetText(Element element, string value, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var parameter = ParameterHelper.Find(element, name);
                if (parameter is null || parameter.IsReadOnly) continue;
                if (parameter.StorageType != StorageType.String) continue;

                parameter.Set(value ?? string.Empty);
                return true;
            }
            catch
            {
                // Try the next name.
            }
        }

        return false;
    }
}
