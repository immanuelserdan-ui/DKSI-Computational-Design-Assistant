using System.IO;
using Autodesk.Revit.DB;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Finishes;

/// <summary>One parameter the finish engine writes, and the categories it must reach.</summary>
public sealed record FinishParameterSpec(
    string Name,
    string Guid,
    ForgeTypeId Spec,
    BuiltInCategory[] Categories,
    string Purpose);

public sealed class ParameterSetupResult
{
    public required IReadOnlyList<string> Report { get; init; }
    public required int Created { get; init; }
    public required int Extended { get; init; }
    public required int AlreadyCorrect { get; init; }
    public required IReadOnlyList<string> Problems { get; init; }

    /// <summary>
    /// The NAMES of the parameters that could not be bound, parallel to <see cref="Problems"/>.
    ///
    /// Problems carries prose for a human; this carries identity for a caller. A command that
    /// wants to know "can I still do useful work?" has to distinguish a failure on the area
    /// parameter, which makes every row worthless, from one on a grouping column, which makes
    /// a column blank - and it cannot do that by parsing sentences.
    /// </summary>
    public required IReadOnlyList<string> FailedParameters { get; init; }
}

/// <summary>
/// Creates and binds the parameters the finish engine writes to, so a model goes from
/// "the tool reports MISSING PARAMS on every room" to working without anyone opening the
/// Shared Parameters dialog seven times.
///
/// WHY THE GUIDS ARE HARDCODED
///   A shared parameter is identified by its GUID, not its name. Letting Revit mint a new
///   one per model gives every project its own incompatible "Ceiling Finish Area": they
///   look identical in the UI, schedule identically, and cannot be combined, transferred
///   with Transfer Project Standards, or read by anything that expects one definition.
///   Fixing the GUIDs in source means every model this is ever run on shares one
///   definition, which is the whole point of a shared parameter.
///
///   The consequence to accept: these GUIDs are now permanent. Changing one orphans the
///   data already written under the old one in every model.
///
/// WHY IT EXTENDS RATHER THAN REPLACES
///   A model that already has "Ceiling Finish Area" bound to Rooms and Ceilings has real
///   data under that definition. Deleting and recreating the binding would discard it.
///   So an existing definition is kept and its category set widened; only a genuinely
///   absent parameter is created.
/// </summary>
public sealed class FinishParameterSetup
{
    private readonly Document _doc;
    private readonly FinishSettings _settings;

    private readonly List<string> _report = [];
    private readonly List<string> _problems = [];

    /// <summary>Names behind <see cref="_problems"/>. See ParameterSetupResult.FailedParameters.</summary>
    private readonly List<string> _failed = [];

    private int _created;
    private int _extended;
    private int _alreadyCorrect;

    public FinishParameterSetup(Document doc, FinishSettings settings)
    {
        _doc = doc;
        _settings = settings;
    }

    /// <summary>
    /// The group these land in, both in the shared parameter file and in the Properties
    /// palette. One group so they are found together rather than scattered through Other.
    /// </summary>
    private const string GroupName = "DKSI Finish Areas";

    public IReadOnlyList<FinishParameterSpec> Specs()
    {
        // Rooms carry the per-room total; the element categories carry the same name so a
        // takeoff can be scheduled against the element that owns the surface.
        BuiltInCategory[] roomsAndWalls = [BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Walls];

        return
        [
            new(_settings.WallParameter, "3f1c6a84-2b7d-4e19-9a05-6c3d81f4b217",
                SpecTypeId.Area, roomsAndWalls,
                "room-clipped wall finish face, paint plus substrate"),

            new(_settings.PaintParameter, "5d92e0b7-8c14-42a6-b3f8-1e7a95c60d43",
                SpecTypeId.Area, roomsAndWalls,
                "painted-only wall area - the paint cost basis"),

            // Structural foundations are in the list because a slab-on-grade IS the ground
            // floor. RoomFinishCalculator measures and claims them - OST_StructuralFoundation
            // is in its _slabCategories - so without the binding the value it computed for
            // every ground-floor room was written nowhere, silently, with no report line.
            new(_settings.FloorParameter, "a47b3f16-9e58-4c02-8d71-2b6f0a83e594",
                SpecTypeId.Area,
                [
                    BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Floors,
                    BuiltInCategory.OST_StructuralFoundation,
                ],
                "floor finish area"),

            // The finish-material subset of the above, on the same categories. Added because
            // the run reports show the floor bucket really does split: 45.000 m2 of bare
            // "EM Floor" substrate against 27.876 m2 of identified finish in the test model,
            // which one total cannot express.
            new(_settings.FloorPaintParameter, "4d261758-4957-4584-a354-d51c5d614abe",
                SpecTypeId.Area,
                [
                    BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Floors,
                    BuiltInCategory.OST_StructuralFoundation,
                ],
                "identified floor finish area - excludes bare substrate"),

            // THE ONE THIS EXERCISE IS ABOUT. Floors and Roofs are in the list because with
            // no ceiling modelled the fallback chain measures the soffit of the slab or roof
            // instead, and that area has to be written onto the element it was measured from
            // - there is no ceiling element to carry it.
            new(_settings.CeilingParameter, "c8e5d203-7a91-4b6f-95c4-0d38b7e1a462",
                SpecTypeId.Area,
                [
                    BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Ceilings,
                    BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs,
                ],
                "ceiling finish area, from the ceiling / slab above / roof priority chain"),

            // The PT subset of the above, on the same categories so a paint takeoff can be
            // scheduled against whichever element the chain actually measured.
            new(_settings.CeilingPaintParameter, "4f2b8d61-a095-4e37-b6c8-71d4903ae526",
                SpecTypeId.Area,
                [
                    BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Ceilings,
                    BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs,
                ],
                "painted-only ceiling area - the PT cost basis"),

            new(_settings.NetFloorParameter, "e61a94c5-3d70-4f28-a1b9-8c25e0d76f31",
                SpecTypeId.Area, [BuiltInCategory.OST_Rooms],
                "walkable area - footprint backed by a real slab"),

            new(_settings.CeilingSourceParameter, "b73f0e28-6c45-4a91-8f27-3e5d1b09c874",
                SpecTypeId.String.Text, [BuiltInCategory.OST_Rooms],
                "which element the ceiling area came from"),

            // ---- room identity on the finish elements --------------------------------
            //
            // NOT bound to Rooms, deliberately. A room already knows its own Department,
            // Number and Name; binding a second copy onto Rooms creates two fields that can
            // disagree, and the one people would then schedule is the copy. These exist only
            // to answer "which room does this WALL belong to?", which is a question the
            // element genuinely cannot answer for itself.
            //
            // Roofs are in the list for the same reason they are in the ceiling specs: with
            // no ceiling modelled, the room's ceiling finish is measured off the slab above
            // or the roof, and the row in the takeoff is that element's row.
            new(_settings.ApartmentParameter, "183f9946-30c0-463c-ba40-162898959cf5",
                SpecTypeId.String.Text, IdentityCategories,
                "apartment - the finished room's Department"),

            new(_settings.RoomNumberParameter, "68442e05-a311-4702-8a5d-170448fe56cb",
                SpecTypeId.String.Text, IdentityCategories,
                "the finished room's Number"),

            new(_settings.RoomNameParameter, "9ff26881-416a-43ce-8c91-91b907c8af2f",
                SpecTypeId.String.Text, IdentityCategories,
                "the finished room's Name"),

            // ---- the per-room paint takeoff ------------------------------------------
            //
            // On Generic Models, because that is what the takeoff rows are: one generated
            // element per (room, surface, material), carrying the area the engine measured
            // for that room on that surface in that material.
            //
            // They exist because a WALL cannot carry this. A wall material takeoff has one
            // row per (wall, material) and no room dimension, so a wall between Alrum and
            // Bad has a single 'Rum' and the losing room's face disappears from the
            // schedule entirely - four faces of a bathroom arriving as two rows. Giving the
            // row its own element is the only way to have as many rows as there are faces.
            new(_settings.PaintAreaParameter, "a4d7e219-5b83-4c06-9e71-2f8a63d05b17",
                SpecTypeId.Area, TakeoffCategories,
                "painted area of one material on one surface of one room"),

            new(_settings.PaintSurfaceParameter, "c92f5a03-7e14-48d2-b6a9-08c47e13d5f2",
                SpecTypeId.String.Text, TakeoffCategories,
                "which surface the takeoff row measures - Walls, Floor or Ceiling"),

            new(_settings.PaintMaterialParameter, "6e01b8d4-3c57-4a19-85f2-9d7b04ea61c3",
                SpecTypeId.String.Text, TakeoffCategories,
                "the painted material on that surface"),

            new(_settings.PaintTypeParameter, "b5c8f716-92a4-4d38-a0e6-14fb27c9d803",
                SpecTypeId.String.Text, TakeoffCategories,
                "the type of the element the row was measured on - 'IV-Gips-100mm'"),

            // Text rather than Integer: element ids are 64-bit and a Revit Integer parameter
            // is 32-bit, so a large model would overflow one silently.
            new(_settings.PaintHostParameter, "d3f4a681-27b9-4e5c-91a0-6c8b53e7f24d",
                SpecTypeId.String.Text, TakeoffCategories,
                "the element id of the wall the row was measured on - what makes a row traceable"),

            // Reference columns, not quantities. Both repeat the owning room's total on every
            // row of that room, so both are wrong if summed - see RoomPaintTotalParameter.
            new(_settings.RoomPaintTotalParameter, "7c2ea940-8b16-4f73-a5d8-30e91c6b4f27",
                SpecTypeId.Area, TakeoffCategories,
                "the owning room's paint total for this row's surface - reference only, never sum"),

            new(_settings.RoomFinishTotalParameter, "e58b1073-4da2-49c6-b70f-92a4d81e35bc",
                SpecTypeId.Area, TakeoffCategories,
                "the owning room's finish total for this row's surface - reference only, never sum"),

            // Not a finish area, but the reason the automation reports itself unavailable in
            // every model that has never been set up. Binding it here is what turns the
            // banner from a warning into a status.
            new(FinishAutomation.StaleParameter, "1a8c5b94-0f63-4e27-b8d5-9426a7e30cf1",
                SpecTypeId.Boolean.YesNo, [BuiltInCategory.OST_Rooms],
                "rooms awaiting recalculation"),
        ];
    }

    /// <summary>
    /// Every category the finish engine can write a measured surface onto. The room
    /// identity parameters reach all five, because a takeoff row can be any of them.
    ///
    /// Structural foundations belong here for the same reason they belong on the floor
    /// specs: a slab-on-grade is the ground floor, and the engine claims it for the room
    /// above. This list is also what <c>IsFinishCategory</c> tests, so leaving foundations
    /// out did double damage - the identity write failed AND the failure was classified as
    /// expected, so it never reached the MISSING IDENTITY PARAMS report.
    /// </summary>
    public static readonly BuiltInCategory[] FinishElementCategories =
    [
        BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Roofs,
        BuiltInCategory.OST_StructuralFoundation,
    ];

    /// <summary>The category the generated per-room takeoff rows live in.</summary>
    public static readonly BuiltInCategory[] TakeoffCategories = [BuiltInCategory.OST_GenericModel];

    /// <summary>
    /// Everything that can carry room identity: the measured finish elements, plus the
    /// generated takeoff rows.
    ///
    /// The takeoff rows need Lejlighed / Rum nr / Rum bound to the SAME shared parameters, on
    /// the same GUIDs, as the walls do - otherwise the takeoff would group on a second field
    /// with the same name and a different identity, which is exactly the "two fields that can
    /// disagree" trap the note above is about.
    /// </summary>
    public static readonly BuiltInCategory[] IdentityCategories =
        [.. FinishElementCategories, .. TakeoffCategories];

    /// <summary>
    /// Read-only check: is any parameter absent, or bound to fewer categories than it needs?
    ///
    /// Cheap by design - it walks the document's binding map, which holds a handful of
    /// entries, and touches no elements. That is what makes it safe to call on every run of
    /// the finish engine rather than only when someone remembers to press a button.
    /// </summary>
    public bool AnythingMissing()
    {
        foreach (var spec in Specs())
        {
            var wanted = WantedCategories(spec);
            if (wanted.Count == 0) continue;

            var existing = FindBinding(spec.Name);
            if (existing is null) return true;

            // A type binding is a problem, but not one this can fix - Run() reports it
            // rather than silently rebinding and dropping the data underneath.
            if (existing.Value.Binding is TypeBinding) continue;

            var had = new HashSet<long>();
            foreach (Category category in existing.Value.Binding.Categories) had.Add(category.Id.Value);

            if (wanted.Any(c => !had.Contains(c.Id.Value))) return true;
        }

        return false;
    }

    /// <summary>
    /// Binds whatever is missing, in its own transaction, and returns null when there was
    /// nothing to do.
    ///
    /// WHY ON DEMAND RATHER THAN ON DOCUMENT OPEN
    ///   Binding on DocumentOpened would modify every model anyone opens - including ones
    ///   they only meant to look at, and other people's models they have open for reference.
    ///   Silently dirtying a file someone did not intend to edit is worse than the
    ///   inconvenience it saves.
    ///
    ///   Calling it when a finish tool actually runs is the honest trigger: the user has
    ///   asked for something that needs these parameters, so creating them is part of
    ///   answering the request rather than a side effect of opening a file.
    ///
    /// MUST NOT be called from inside an open transaction - it opens its own.
    /// </summary>
    public static ParameterSetupResult? EnsureBound(
        Document doc, FinishSettings settings, string reason)
    {
        try
        {
            // Never touch a family, a link, or anything we cannot write to anyway.
            if (doc.IsFamilyDocument || doc.IsLinked || doc.IsReadOnly) return null;

            var setup = new FinishParameterSetup(doc, settings);
            if (!setup.AnythingMissing()) return null;

            ParameterSetupResult? result = null;

            Transactions.Run(doc, $"Bind finish parameters ({reason})", () => result = setup.Run());

            if (result is not null)
            {
                Log.Info($"Auto-bound finish parameters because {reason}: " +
                         $"{result.Created} created, {result.Extended} widened, " +
                         $"{result.Problems.Count} problem(s).");
            }

            return result;
        }
        catch (Exception ex)
        {
            // A model that will not take the binding must still be measurable - the engine
            // reports MISSING PARAMS per room and carries on.
            Log.Warn($"Finish parameters could not be bound automatically: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Binds everything. The caller owns the transaction - ParameterBindings.Insert is a
    /// model write like any other.
    /// </summary>
    public ParameterSetupResult Run()
    {
        var application = _doc.Application;
        var originalFile = SafeSharedParameterFile(application);

        try
        {
            var definitions = OpenDefinitionFile(application);

            foreach (var spec in Specs())
            {
                try
                {
                    Bind(spec, definitions);
                }
                catch (Exception ex)
                {
                    Fail(spec.Name, ex.Message);
                    _report.Add($"FAILED {spec.Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            // Leaving Revit pointed at our file would silently redirect the next person who
            // opens the Shared Parameters dialog expecting the office file.
            try { application.SharedParametersFilename = originalFile; }
            catch (Exception ex) { Log.Warn($"Could not restore the shared parameter file: {ex.Message}"); }
        }

        return new ParameterSetupResult
        {
            Report = _report,
            Created = _created,
            Extended = _extended,
            AlreadyCorrect = _alreadyCorrect,
            Problems = _problems,
            FailedParameters = _failed,
        };
    }

    /// <summary>
    /// One binding failure, recorded as both prose and identity. Every problem goes through
    /// here so the two lists cannot drift - a problem with no matching name would make a
    /// caller think the failure was harmless.
    /// </summary>
    private void Fail(string name, string detail)
    {
        _problems.Add($"{name}: {detail}");
        _failed.Add(name);
    }

    // ------------------------------------------------------- the definition file

    /// <summary>
    /// Where our definitions live. Deliberately NOT the office shared parameter file: this
    /// writes definitions, and a tool that edits a shared file every user's models depend
    /// on is a tool that eventually corrupts it. The GUIDs are fixed in source, so a
    /// per-machine file still produces identical parameters everywhere.
    /// </summary>
    public static string DefinitionFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cda", "RevitAddin", "DKSI-FinishAreas.txt");

    private static string SafeSharedParameterFile(Autodesk.Revit.ApplicationServices.Application application)
    {
        try { return application.SharedParametersFilename ?? string.Empty; }
        catch { return string.Empty; }
    }

    private DefinitionFile OpenDefinitionFile(Autodesk.Revit.ApplicationServices.Application application)
    {
        var path = DefinitionFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (!File.Exists(path))
        {
            // Revit fills in the header itself once a group is added; it only needs the file
            // to exist and be writable.
            File.WriteAllText(path, string.Empty);
            _report.Add($"Created the definition file {path}.");
        }

        application.SharedParametersFilename = path;

        return application.OpenSharedParameterFile()
               ?? throw new InvalidOperationException(
                   $"Revit could not open the shared parameter file at {path}. If it is " +
                   "read-only or on a disconnected network drive, fix that and run again.");
    }

    // -------------------------------------------------------------- one parameter

    private void Bind(FinishParameterSpec spec, DefinitionFile file)
    {
        var wanted = WantedCategories(spec);

        if (wanted.Count == 0)
        {
            Fail(spec.Name, "none of its categories exist in this model.");
            return;
        }

        var existing = FindBinding(spec.Name);

        if (existing is not null)
        {
            ExtendBinding(spec, existing.Value.Definition, existing.Value.Binding, wanted);
            return;
        }

        var definition = FindOrCreateDefinition(spec, file);

        var set = _doc.Application.Create.NewCategorySet();
        foreach (var category in wanted) set.Insert(category);

        if (!_doc.ParameterBindings.Insert(definition, _doc.Application.Create.NewInstanceBinding(set),
                GroupTypeId.Data))
        {
            throw new InvalidOperationException("Revit refused the binding.");
        }

        BindingsChanged();
        _created++;
        _report.Add($"BOUND  {spec.Name} -> {Describe(wanted)}  ({spec.Purpose})");
    }

    private List<Category> WantedCategories(FinishParameterSpec spec)
    {
        var categories = new List<Category>();

        foreach (var builtIn in spec.Categories)
        {
            Category? category;
            try { category = Category.GetCategory(_doc, builtIn); }
            catch { continue; }

            // A category that does not allow bound parameters cannot be added, and trying
            // throws out of the whole Insert rather than skipping the one entry.
            if (category is { AllowsBoundParameters: true }) categories.Add(category);
        }

        return categories;
    }

    /// <summary>
    /// Every binding in the document, read once and cached.
    ///
    /// THE ITERATOR MUST BE RUN TO COMPLETION, and that is the entire point of this method.
    /// Revit's BindingMap cannot be modified while an iterator over it is still live, and
    /// the previous version of the lookup returned from INSIDE the while loop the moment it
    /// found a match — leaving that iterator open for the rest of the call. Insert happened
    /// to survive it; ReInsert did not, and returned false with no explanation. That is why
    /// widening an existing parameter had never once worked, while creating a new one always
    /// had: only the widen path modifies a map somebody is still reading.
    ///
    /// Reading the map once instead of once per parameter is the incidental benefit.
    /// </summary>
    private List<(Definition Definition, ElementBinding Binding)> Bindings()
    {
        if (_bindings is not null) return _bindings;

        var found = new List<(Definition, ElementBinding)>();
        var iterator = _doc.ParameterBindings.ForwardIterator();

        // No early exit anywhere in this loop. MoveNext must be allowed to return false.
        while (iterator.MoveNext())
        {
            try
            {
                if (iterator.Key is { } definition && iterator.Current is ElementBinding binding)
                    found.Add((definition, binding));
            }
            catch
            {
                // A binding whose definition cannot be read is one we cannot match anyway.
            }
        }

        _bindings = found;
        return _bindings;
    }

    private List<(Definition Definition, ElementBinding Binding)>? _bindings;

    /// <summary>Drops the cache after the map has been changed underneath it.</summary>
    private void BindingsChanged() => _bindings = null;

    /// <summary>The document's existing binding for this name, if any.</summary>
    private (Definition Definition, ElementBinding Binding)? FindBinding(string name)
    {
        var target = ParameterHelper.Normalize(name);

        foreach (var entry in Bindings())
        {
            try
            {
                if (ParameterHelper.Normalize(entry.Definition.Name) == target) return entry;
            }
            catch
            {
                // Unreadable name; cannot be the one we want.
            }
        }

        return null;
    }

    private void ExtendBinding(
        FinishParameterSpec spec, Definition definition, ElementBinding binding,
        IReadOnlyList<Category> wanted)
    {
        // A type binding on a finish area is a modelling decision this tool must not
        // silently reverse - every instance of the type would share one area, which is
        // wrong, but reversing it here would drop the data already under it.
        if (binding is TypeBinding)
        {
            Fail(spec.Name,
                "bound as a TYPE parameter. The finish engine writes per " +
                "instance, so it cannot write to it. Rebind it as an instance parameter.");
            return;
        }

        var set = _doc.Application.Create.NewCategorySet();
        var had = new HashSet<long>();

        // Category NAMES are collected alongside the ids because the report has to be able
        // to state what the parameter is ACTUALLY bound to.
        //
        // This line used to print the categories the tool wanted, which made a status line
        // a restatement of the request rather than a fact about the model. "Net Floor Area
        // is already bound to Rooms" was printed for a parameter bound to Rooms AND Floors,
        // and "Lejlighed is already bound to Walls, Floors, Ceilings, Roofs" said nothing
        // about whether Rooms was still in there. Both readings were reasonable and both
        // were wrong, and each one cost an investigation.
        var existing = new List<string>();

        foreach (Category category in binding.Categories)
        {
            set.Insert(category);
            had.Add(category.Id.Value);
            existing.Add(category.Name);
        }

        var added = new List<Category>();
        foreach (var category in wanted)
        {
            if (had.Contains(category.Id.Value)) continue;

            set.Insert(category);
            added.Add(category);
        }

        if (added.Count == 0)
        {
            _alreadyCorrect++;

            // "is bound to", not "is already bound to": the old wording invited reading the
            // list as the set this tool needs, when it is the set the model has. Anything
            // beyond what was wanted is listed too, because a category nobody expected is
            // exactly the kind of thing worth noticing.
            _report.Add($"OK     {spec.Name} is bound to {Sorted(existing)}.");
            return;
        }

        var newBinding = _doc.Application.Create.NewInstanceBinding(set);

        // KEEP THE PARAMETER IN THE GROUP IT IS ALREADY IN.
        //
        // Insert chooses a group because the parameter is new and has none. ReInsert is a
        // different situation: forcing Data onto a parameter the office deliberately filed
        // under, say, Text or Identity Data both moves it in everyone's Properties palette
        // and is a second thing for Revit to refuse. The existing group is the correct
        // answer and asking for no change at all is the most likely to be accepted.
        var group = ExistingGroup(definition);

        var accepted = ReInsert(definition, newBinding, group);

        // The overload without a group, as a second attempt. These two differ in what Revit
        // validates, and a refusal from one is not a refusal from the other.
        if (!accepted) accepted = ReInsert(definition, newBinding, null);

        if (!accepted)
        {
            Fail(spec.Name, ExplainRefusal(spec, definition, binding, added));
            _report.Add($"FAILED {spec.Name}: Revit refused the widened binding. {Describe(added)} " +
                        "could not be added to it.");
            return;
        }

        BindingsChanged();
        _extended++;

        // Both halves matter: what changed, and what the parameter now reaches. Reporting
        // only the addition leaves the reader to reconstruct the total from two lines that
        // are not both on screen.
        _report.Add($"WIDENED {spec.Name} - added {Describe(added)}; now bound to " +
                    $"{Sorted(existing.Concat(added.Select(c => c.Name)))}.");
    }

    /// <summary>
    /// Category names for a report line, ordered so the same binding always reads the same
    /// way. Revit hands them back in whatever order the map holds them, which would
    /// otherwise make two identical models produce two different-looking reports.
    /// </summary>
    private static string Sorted(IEnumerable<string> names)
    {
        var list = names.ToList();
        if (list.Count == 0) return "no readable category";

        list.Sort(StringComparer.CurrentCultureIgnoreCase);
        return string.Join(", ", list);
    }

    private bool ReInsert(Definition definition, InstanceBinding binding, ForgeTypeId? group)
    {
        try
        {
            return group is null
                ? _doc.ParameterBindings.ReInsert(definition, binding)
                : _doc.ParameterBindings.ReInsert(definition, binding, group);
        }
        catch (Exception ex)
        {
            Log.Warn($"ReInsert threw for '{definition.Name}': {ex.Message}");
            return false;
        }
    }

    private static ForgeTypeId? ExistingGroup(Definition definition)
    {
        try { return definition.GetGroupTypeId(); }
        catch { return null; }
    }

    /// <summary>
    /// Why the widening was refused, in enough detail to act on.
    ///
    /// "Revit refused the widened binding" is a true sentence that helps nobody: the API
    /// returns a bare false with no reason, so the reason has to be reconstructed from the
    /// things that are known to cause it. Each check below is a cause someone can actually
    /// fix, and the fallback tells them where to do it by hand.
    /// </summary>
    private string ExplainRefusal(
        FinishParameterSpec spec, Definition definition, ElementBinding binding,
        IReadOnlyList<Category> added)
    {
        var had = new List<string>();
        try
        {
            foreach (Category category in binding.Categories) had.Add(category.Name);
        }
        catch
        {
            // Leave the list short rather than lose the whole message.
        }

        var detail =
            $"Revit refused to add {Describe(added)} to it. It is currently bound to {Sorted(had)}.";

        if (KeyScheduleUsing(spec.Name) is { } schedule)
        {
            return detail +
                   $" CAUSE: it is the key parameter of the key schedule '{schedule}'. Revit owns " +
                   "that binding and will not let anything else change it. Either schedule the " +
                   "finish takeoff against a different parameter, or make a separate shared " +
                   "parameter for the element-side apartment value.";
        }

        if (_doc.IsWorkshared)
        {
            detail +=
                " POSSIBLE CAUSE: this model is workshared, and parameter bindings live in " +
                "Project Standards. If another user has those checked out, no binding can be " +
                "changed. Synchronise, make sure nobody is holding the standards, and re-run.";
        }

        return detail +
               $" TO FIX BY HAND: Manage > Project Parameters > {spec.Name} > Edit, and tick " +
               $"{Describe(added)}. That is the only change needed - it keeps the existing " +
               "definition, its GUID and its data. Do NOT create a second parameter of the same " +
               "name; two same-named shared parameters with different GUIDs is the one mistake " +
               "here that is genuinely painful to undo.";
    }

    /// <summary>
    /// The key schedule, if any, that owns a parameter of this name. A key schedule's own key
    /// parameter is managed by Revit, which is one of the few things that makes a rebinding
    /// fail outright rather than just being disallowed by permissions.
    /// </summary>
    private string? KeyScheduleUsing(string name)
    {
        var target = ParameterHelper.Normalize(name);

        try
        {
            foreach (var schedule in new FilteredElementCollector(_doc)
                         .OfClass(typeof(ViewSchedule))
                         .Cast<ViewSchedule>())
            {
                try
                {
                    // IsKeySchedule is on the definition; the parameter name it manages is on
                    // the view. Both are needed - reading the name off a schedule that is not
                    // a key schedule returns something meaningless rather than throwing.
                    if (!schedule.Definition.IsKeySchedule) continue;

                    if (ParameterHelper.Normalize(schedule.KeyScheduleParameterName) == target)
                        return schedule.Name;
                }
                catch
                {
                    // A schedule that will not answer cannot be the culprit we can name.
                }
            }
        }
        catch
        {
            // No schedules readable; fall through to the generic advice.
        }

        return null;
    }

    /// <summary>
    /// The definition from our file, created on the fixed GUID if it is not there yet.
    /// Matching by GUID rather than name means a definition someone renamed in the file is
    /// still recognised as the same parameter.
    /// </summary>
    private ExternalDefinition FindOrCreateDefinition(FinishParameterSpec spec, DefinitionFile file)
    {
        var guid = Guid.Parse(spec.Guid);

        foreach (DefinitionGroup existingGroup in file.Groups)
        {
            foreach (Definition definition in existingGroup.Definitions)
            {
                if (definition is ExternalDefinition external && external.GUID == guid) return external;
            }
        }

        var group = file.Groups.get_Item(GroupName) ?? file.Groups.Create(GroupName);

        var options = new ExternalDefinitionCreationOptions(spec.Name, spec.Spec)
        {
            GUID = guid,
            Description = spec.Purpose,
            Visible = true,
        };

        return group.Definitions.Create(options) as ExternalDefinition
               ?? throw new InvalidOperationException(
                   $"The definition for '{spec.Name}' could not be created in the parameter file.");
    }

    private static string Describe(IEnumerable<Category> categories) =>
        string.Join(", ", categories.Select(c => c.Name));
}
