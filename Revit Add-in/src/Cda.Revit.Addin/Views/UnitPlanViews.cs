using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Views;

/// <summary>
/// What to do when the new view's template holds 'Crop View' off, which is the one thing
/// that can silently produce correctly-named views showing the whole building.
/// </summary>
public enum TemplateCropConflict
{
    /// <summary>
    /// Drop the template association on the NEW view only, then crop it.
    ///
    /// Revit bakes the template's current graphic settings into the view at the moment the
    /// association is removed, so the plan keeps the look it had - it just stops tracking
    /// future template edits. Default because it is the only option whose blast radius is
    /// the views this tool just made.
    /// </summary>
    DetachTemplateFromNewView,

    /// <summary>
    /// Add the crop parameters to the template's non-controlled list, so every view using
    /// that template owns its own crop.
    ///
    /// Arguably the correct standards-level fix - a crop region is per-view by nature and
    /// has no business being template-controlled - but it EDITS A SHARED TEMPLATE and
    /// therefore touches every other view using it. Opt in deliberately.
    /// </summary>
    ReleaseCropOnTemplate,

    /// <summary>Leave the view uncropped and warn. The behaviour that produced the six
    /// uncropped plans; kept so the old outcome is still reachable on purpose.</summary>
    LeaveUncropped,
}

/// <summary>
/// Everything the unit-plan pass reads, in one place so the command stays a thin shell
/// and the defaults are visible rather than scattered through the loop.
/// </summary>
public sealed record UnitViewSettings
{
    /// <summary>
    /// How to handle a template that holds cropping off. See the enum.
    ///
    /// DEFAULTS TO ReleaseCropOnTemplate because <see cref="ViewTemplateName"/> is set: a
    /// template that must be applied AND a crop that must be active cannot both hold, unless
    /// the template stops owning the crop parameters. Detaching would satisfy the crop and
    /// silently discard the template, which is not what "apply the template" means.
    /// </summary>
    public TemplateCropConflict OnTemplateBlocksCrop { get; init; } = TemplateCropConflict.ReleaseCropOnTemplate;

    /// <summary>
    /// Hide tags, dimensions and casework belonging to the units either side of this one.
    ///
    /// The crop cuts geometry at a rectangle; it does not know which unit an annotation
    /// belongs to. A neighbour's room tag sitting inside that rectangle is drawn, which is
    /// how the 0377 plan ended up labelled with 0376 and 0378 rooms.
    /// </summary>
    public bool HideForeignElements { get; init; } = true;

    /// <summary>
    /// Slack around a unit's true extents before an element counts as foreign. Casework sits
    /// against walls and a dimension witness line reaches past the room it measures, so a
    /// zero-tolerance test would hide things that plainly belong.
    /// </summary>
    public double ForeignToleranceMm { get; init; } = 150;

    /// <summary>Activate the first created view and zoom it to fit when the run finishes.</summary>
    public bool OpenAndZoomCreatedViews { get; init; } = true;

    /// <summary>
    /// Categories tested for foreign ownership. Walls and doors are deliberately ABSENT: a
    /// party wall is shared, and hiding the neighbour's half of it would leave the unit drawn
    /// without its own boundary.
    /// </summary>
    public IReadOnlyList<BuiltInCategory> ForeignCategories { get; init; } =
    [
        BuiltInCategory.OST_Casework,
        BuiltInCategory.OST_Furniture,
        BuiltInCategory.OST_FurnitureSystems,
        BuiltInCategory.OST_PlumbingFixtures,
        BuiltInCategory.OST_ElectricalEquipment,
        BuiltInCategory.OST_ElectricalFixtures,
        BuiltInCategory.OST_SpecialityEquipment,
    ];

    internal double ForeignToleranceFeet =>
        UnitUtils.ConvertToInternalUnits(ForeignToleranceMm, UnitTypeId.Millimeters);

    /// <summary>
    /// Master view to duplicate, by name. Null uses whatever plan is active.
    ///
    /// Naming it beats trusting the active view, and this model shows why twice over: TWO
    /// views are called "(02) Stueplan, terraen" - a Ceiling Plan with no annotation and a
    /// Floor Plan carrying 48 room tags and 70 dimensions - and running the command from a
    /// schedule or a 3D view leaves no source at all, which is how six empty plans happened.
    /// </summary>
    public string? MasterViewName { get; init; } = "(02) Stueplan, terræn";

    /// <summary>
    /// Refuse to create views at all rather than create them without detailing.
    ///
    /// A blank plan named exactly like a real deliverable is worse than no plan: it looks
    /// finished in the browser and only fails review once someone opens it.
    /// </summary>
    public bool RequireMasterView { get; init; } = true;

    /// <summary>
    /// View template applied to every unit view, by name. Resolved whitespace-tolerantly, so
    /// a template saved as "SMB_Export-2d " still matches. Set null to leave whatever the
    /// duplicated master already carries.
    /// </summary>
    public string? ViewTemplateName { get; init; } = "SMB_Export-2d";

    /// <summary>
    /// Where a room's boundary is measured. Center reaches the wall centrelines, so half of
    /// each enclosing wall is inside the extents before the margin is added - which is why
    /// the exterior wall lands inside the crop rather than relying on the margin alone.
    /// Finish measures the room-side face instead.
    /// </summary>
    public SpatialElementBoundaryLocation BoundaryLocation { get; init; } = SpatialElementBoundaryLocation.Center;

    /// <summary>
    /// Parameter carrying the unit identifier. "Department" is the built-in room parameter
    /// (ROOM_DEPARTMENT); any project or shared text parameter name works instead - the
    /// lookup goes through <see cref="ParameterHelper"/>, so a definition created with a
    /// trailing space still resolves.
    /// </summary>
    public string UnitParameterName { get; init; } = "Department";

    /// <summary>
    /// How the unit number is pulled out of that parameter's text. The default takes the
    /// first run of four digits, so "Bolig 1452", "1452", and "A-1452-ST" all group as 1452.
    /// Set to <c>null</c> to use the parameter value verbatim as the key.
    /// </summary>
    public string? UnitPattern { get; init; } = @"\d{4}";

    /// <summary>Margin added around the combined room extents, in millimetres.</summary>
    public double MarginMm { get; init; } = 500.0;

    /// <summary>
    /// One view per level per unit. A maisonette occupies two levels and needs two plans,
    /// and a plan view can only be tied to one level, so this is on by default. Turning it
    /// off gives one view per unit, cropped to that unit's rooms on the source view's level.
    /// </summary>
    public bool GroupPerLevel { get; init; } = true;

    /// <summary>
    /// How a source view is copied. WithDetailing carries annotation, dimensions and
    /// detail lines across - the reference sheets are dimensioned, so that is the default.
    /// Use <see cref="ViewDuplicateOption.Duplicate"/> for model geometry only.
    /// </summary>
    public ViewDuplicateOption DuplicateOption { get; init; } = ViewDuplicateOption.WithDetailing;

    /// <summary>Leave a unit alone when a view of the target name already exists.</summary>
    public bool SkipExisting { get; init; } = true;

    /// <summary>Optional template applied to each new view. See the note in ApplyCrop.</summary>
    public ElementId? ViewTemplateId { get; init; }

    /// <summary>
    /// Draw the crop boundary in the view. On, as requested - it makes the crop obvious
    /// while checking the results. Note it PRINTS: the reference sheets show the unit with
    /// no rectangle around it, so set this false before issuing.
    /// </summary>
    public bool ShowCropBoundary { get; init; } = true;

    /// <summary>
    /// Crop annotation as well as model geometry, so a neighbouring unit's room tags do not
    /// hang into the plan. Revit's own annotation offsets are kept - they are already small,
    /// and the API rejects zero.
    /// </summary>
    public bool CropAnnotation { get; init; } = true;

    /// <summary>
    /// View scale, or null to inherit. NULL IS THE DEFAULT ON PURPOSE: when the source is the
    /// storey plan being duplicated, its scale is already the right one, and putting a number
    /// here silently rescales every unit plan away from it.
    /// </summary>
    public int? Scale { get; init; }

    /// <summary>
    /// Name layout. Tokens are {UNIT}, {LEVEL} and anything in <see cref="Tokens"/>.
    ///
    /// The default reproduces the document ID on the reference sheets -
    /// 722-0553-0006-1001-T21-A00-R-V00-R00 - where 1001 is the unit. {LEVEL} is not in it
    /// because those unit numbers are unique across the building. If yours repeat per
    /// storey, put {LEVEL} in the format rather than relying on the uniquifier, which only
    /// appends " (2)" and tells a reader nothing.
    /// </summary>
    public string NameFormat { get; init; } =
        "{PROJECT}-{CASE}-{BUILDING}-{UNIT}-{TYPE}-{PHASE}-{ROLE}-{VERSION}-{REVISION}";

    /// <summary>
    /// Read {PROJECT}, {CASE} and {BUILDING} out of the model instead of using the fallback
    /// tokens below, so the same build produces correct names in every project.
    ///
    /// Mapping, established against 634-0001-036-DDG-Heliosvaenget:
    ///   {CASE}     <- Project Information 'Selskab'   (0001)
    ///   {BUILDING} <- Project Information 'Afdeling'  (0036)
    ///   {PROJECT}  <- Project Number, or the leading segment of the file name when that is
    ///                 blank, which it was in that model (634 lives only in the file name).
    ///
    /// Anything that cannot be derived falls back to <see cref="Tokens"/> AND raises a
    /// warning - a silently inherited project number is the one error that would survive
    /// onto a sheet.
    /// </summary>
    public bool DeriveProjectTokensFromModel { get; init; } = true;

    /// <summary>
    /// Fallback values substituted into <see cref="NameFormat"/>. PROJECT, CASE and BUILDING
    /// are overwritten from the model unless <see cref="DeriveProjectTokensFromModel"/> is off.
    /// </summary>
    public IReadOnlyDictionary<string, string> Tokens { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PROJECT"] = "722",
            ["CASE"] = "0553",
            ["BUILDING"] = "0006",
            ["TYPE"] = "T21",
            ["PHASE"] = "A00",
            ["ROLE"] = "R",
            ["VERSION"] = "V00",
            ["REVISION"] = "R00",
        };

    internal double MarginFeet => UnitUtils.ConvertToInternalUnits(MarginMm, UnitTypeId.Millimeters);
}

/// <summary>One apartment on one level, with the rooms that proved it exists.</summary>
public sealed record UnitGroup(string Unit, ElementId LevelId, string LevelName, IReadOnlyList<Room> Rooms);

/// <summary>What happened to a single unit. One row per group, in report order.</summary>
public sealed record UnitViewRow(string Unit, string Level, int Rooms, string ViewName, string Outcome);

public sealed record UnitViewResult(
    IReadOnlyList<UnitViewRow> Rows,
    IReadOnlyList<string> Summary,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ElementId> CreatedViewIds);

/// <summary>
/// Creates one cropped floor plan per apartment unit, driven by a room parameter.
///
/// The shape of the job: group placed rooms by unit, union their bounding boxes, and give
/// each group a view whose crop region is that union plus a margin. Nothing else in the
/// model is touched - no rooms are edited, no geometry moves.
/// </summary>
public sealed class UnitPlanViewBuilder
{
    private readonly Document _doc;
    private readonly UnitViewSettings _settings;
    private readonly Regex? _unitRegex;
    private readonly HashSet<long> _releasedTemplates = [];
    private readonly Dictionary<long, ViewPlan?> _mastersByLevel = [];

    public UnitPlanViewBuilder(Document doc, UnitViewSettings settings)
    {
        _doc = doc;
        _settings = settings;
        _unitRegex = settings.UnitPattern is null
            ? null
            : new Regex(settings.UnitPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    // ------------------------------------------------------------------ grouping

    /// <summary>
    /// Every placed room carrying a readable unit identifier, grouped.
    ///
    /// Unplaced and unenclosed rooms are dropped: both report Area 0 and neither has a
    /// bounding box, so they would contribute nothing to a crop and would inflate the room
    /// counts in the report into something nobody could reconcile against the model.
    /// </summary>
    public IReadOnlyList<UnitGroup> Collect(out List<string> warnings)
    {
        warnings = [];

        var rooms = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .OfType<Room>()
            .ToList();

        var skippedUnplaced = 0;
        var skippedNoUnit = 0;
        var groups = new Dictionary<(string Unit, long Level), List<Room>>();

        foreach (var room in rooms)
        {
            if (room.Area <= 1e-9 || room.Location is null)
            {
                skippedUnplaced++;
                continue;
            }

            var unit = ReadUnit(room);
            if (string.IsNullOrEmpty(unit))
            {
                skippedNoUnit++;
                continue;
            }

            // GroupPerLevel off collapses the level half of the key so a maisonette's rooms
            // land in one group; ElementId.InvalidElementId.Value is the sentinel for "any".
            var levelKey = _settings.GroupPerLevel ? room.LevelId.Value : ElementId.InvalidElementId.Value;

            if (!groups.TryGetValue((unit, levelKey), out var list))
                groups[(unit, levelKey)] = list = [];
            list.Add(room);
        }

        if (skippedUnplaced > 0)
            warnings.Add($"{skippedUnplaced} room(s) skipped: unplaced, unenclosed or zero area.");
        if (skippedNoUnit > 0)
            warnings.Add($"{skippedNoUnit} placed room(s) skipped: '{_settings.UnitParameterName}' empty or no match for the unit pattern.");

        return groups
            .Select(g =>
            {
                // Guarded rather than leaning on GetElement(InvalidElementId): the sentinel
                // key from GroupPerLevel=false is not a real id and must not be looked up.
                var levelId = g.Key.Level == ElementId.InvalidElementId.Value
                    ? ElementId.InvalidElementId
                    : new ElementId(g.Key.Level);

                var levelName = levelId != ElementId.InvalidElementId && _doc.GetElement(levelId) is Level level
                    ? level.Name
                    : "(all levels)";

                return new UnitGroup(g.Key.Unit, levelId, levelName, g.Value);
            })
            .OrderBy(g => g.LevelName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Unit, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string ReadUnit(Room room)
    {
        // Built-in first when the caller named the built-in parameter: LookupParameter can
        // miss it in a localised model, where the display name is not "Department".
        var raw = _settings.UnitParameterName.Equals("Department", StringComparison.OrdinalIgnoreCase)
            ? room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString()
              ?? ParameterHelper.Find(room, _settings.UnitParameterName)?.AsString()
            : ParameterHelper.Find(room, _settings.UnitParameterName)?.AsString();

        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        if (_unitRegex is null) return raw.Trim();

        var match = _unitRegex.Match(raw);
        return match.Success ? match.Value : string.Empty;
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>
    /// Union of the rooms' bounding boxes, in model coordinates.
    ///
    /// Each box is expanded through its own eight corners rather than by comparing Min and
    /// Max directly, because a box carrying a rotated Transform - which happens on a model
    /// with a rotated project north - has a Min that is not the minimum of anything in
    /// model space.
    /// </summary>
    public static BoundingBoxXYZ? CombinedBox(
        IEnumerable<Room> rooms,
        SpatialElementBoundaryLocation location = SpatialElementBoundaryLocation.Center)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        var found = false;

        void Expand(XYZ p)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            found = true;
        }

        var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = location };

        foreach (var room in rooms)
        {
            var loops = SafeBoundary(room, options);

            if (loops is { Count: > 0 })
            {
                foreach (var loop in loops)
                {
                    foreach (var segment in loop)
                    {
                        var curve = segment.GetCurve();
                        if (curve is null) continue;

                        // Tessellate, not just the endpoints: an arc-walled room bulges past
                        // the chord between its ends, and endpoints alone would crop through it.
                        foreach (var point in curve.Tessellate())
                            Expand(point);
                    }
                }

                continue;
            }

            // No boundary - typically a room whose bounding walls were deleted. The element
            // bounding box is coarser but better than dropping the room from the extents.
            var box = room.get_BoundingBox(null);
            if (box is null) continue;

            foreach (var corner in Corners(box))
                Expand(box.Transform.OfPoint(corner));
        }

        if (!found) return null;

        return new BoundingBoxXYZ
        {
            Min = new XYZ(minX, minY, minZ),
            Max = new XYZ(maxX, maxY, maxZ),
        };
    }

    /// <summary>
    /// GetBoundarySegments throws on some malformed rooms rather than returning an empty
    /// list, and one bad room must not take the whole unit down.
    /// </summary>
    private static IList<IList<BoundarySegment>>? SafeBoundary(Room room, SpatialElementBoundaryOptions options)
    {
        try
        {
            return room.GetBoundarySegments(options);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
    {
        var (a, b) = (box.Min, box.Max);
        yield return new XYZ(a.X, a.Y, a.Z);
        yield return new XYZ(b.X, a.Y, a.Z);
        yield return new XYZ(a.X, b.Y, a.Z);
        yield return new XYZ(b.X, b.Y, a.Z);
        yield return new XYZ(a.X, a.Y, b.Z);
        yield return new XYZ(b.X, a.Y, b.Z);
        yield return new XYZ(a.X, b.Y, b.Z);
        yield return new XYZ(b.X, b.Y, b.Z);
    }

    /// <summary>
    /// Points the view's crop region at a model-space box.
    ///
    /// The crop box is NOT in model coordinates. Its Min and Max are expressed in the
    /// coordinate system of its own Transform, which for a plan view is anchored at the
    /// view origin and rotated by the view's orientation. Assigning model coordinates
    /// straight into Min/Max is the standard way this goes wrong: it looks correct on an
    /// unrotated view at the origin and lands somewhere else on every other model.
    ///
    /// So: take the view's existing crop box for its Transform, push the model corners
    /// through the inverse, and write the extents back in that space.
    ///
    /// Z IS DELIBERATELY LEFT ALONE. On a plan view the crop box's Z range is the view
    /// depth, and the view range - cut plane, top and bottom - is what should own it.
    /// Overwriting it with the rooms' floor-to-ceiling extents would silently retire the
    /// template's view range and change what the plan draws.
    /// </summary>
    public static void SetCropExtents(View view, BoundingBoxXYZ modelBox, double marginFeet)
    {
        var crop = view.CropBox;
        var toCrop = crop.Transform.Inverse;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var corner in Corners(modelBox))
        {
            var p = toCrop.OfPoint(corner);
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }

        crop.Min = new XYZ(minX - marginFeet, minY - marginFeet, crop.Min.Z);
        crop.Max = new XYZ(maxX + marginFeet, maxY + marginFeet, crop.Max.Z);
        view.CropBox = crop;
    }

    // ------------------------------------------------------------------ naming

    // Revit rejects these outright in an element name; a name built from a parameter value
    // can easily contain one.
    private static readonly char[] IllegalNameChars = ['\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~'];

    private static readonly Regex TokenPattern =
        new(@"\{[A-Za-z0-9_]+\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Project, case and building segments read from the model, falling back to the settings
    /// tokens for anything missing. See <see cref="UnitViewSettings.DeriveProjectTokensFromModel"/>
    /// for the mapping and why a fallback also warns.
    /// </summary>
    public IReadOnlyDictionary<string, string> ResolveTokens(List<string> warnings)
    {
        var tokens = new Dictionary<string, string>(_settings.Tokens, StringComparer.OrdinalIgnoreCase);
        if (!_settings.DeriveProjectTokensFromModel) return tokens;

        var info = _doc.ProjectInformation;

        void Take(string token, string? value, string source)
        {
            if (!string.IsNullOrWhiteSpace(value))
                tokens[token] = value.Trim();
            else
                warnings.Add($"{{{token}}} could not be read from the model ({source} is empty), so it fell back to '{tokens.GetValueOrDefault(token, "?")}'. Check it before these names reach a sheet.");
        }

        // Project Number is the natural home, but it is routinely left blank - in which case
        // the leading segment of the file name is the only place the project code exists.
        var project = info?.Number;
        if (string.IsNullOrWhiteSpace(project)) project = LeadingSegment(_doc.Title);
        Take("PROJECT", project, "Project Number and the file name");

        Take("CASE", info is null ? null : ParameterHelper.Find(info, "Selskab")?.AsString(), "'Selskab'");
        Take("BUILDING", info is null ? null : ParameterHelper.Find(info, "Afdeling")?.AsString(), "'Afdeling'");

        return tokens;
    }

    /// <summary>
    /// "634-0001-036-DDG-Heliosvaenget_52_132-R00" -> "634". Returns null unless the segment
    /// is all digits, so a document named "Detached House" contributes nothing rather than
    /// putting a word where a project code belongs.
    /// </summary>
    private static string? LeadingSegment(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var head = title.Split('-', 2)[0].Trim();
        return head.Length > 0 && head.All(char.IsDigit) ? head : null;
    }

    private string BuildName(UnitGroup group, IReadOnlyDictionary<string, string> tokens, ISet<string> unresolved) =>
        BuildName(group, tokens, _settings.NameFormat, unresolved);

    /// <summary>
    /// Name composition with an explicit format, so the 3D pass produces names in the same
    /// convention from the same tokens rather than reimplementing it.
    /// </summary>
    /// <summary>
    /// Short code for a storey, taken from the digits in its name: "(03) 1. sal" -> "03".
    /// Used to tell one storey's copy of a unit from another's when the name format itself
    /// carries no level.
    /// </summary>
    public static string FloorCode(string levelName)
    {
        var digits = Regex.Match(levelName ?? string.Empty, @"\d+");
        return digits.Success ? digits.Value : (levelName ?? string.Empty).Trim();
    }

    public string BuildName(UnitGroup group, IReadOnlyDictionary<string, string> tokens, string format, ISet<string> unresolved)
    {
        var name = format
            .Replace("{UNIT}", group.Unit, StringComparison.OrdinalIgnoreCase)
            .Replace("{FLOOR}", FloorCode(group.LevelName), StringComparison.OrdinalIgnoreCase)
            .Replace("{LEVEL}", group.LevelName, StringComparison.OrdinalIgnoreCase);

        foreach (var (token, value) in tokens)
            name = name.Replace("{" + token + "}", value, StringComparison.OrdinalIgnoreCase);

        // A token nobody supplied would otherwise be silently mangled into "-FOO-" by the
        // illegal-character sweep below, producing a document ID that looks plausible and is
        // wrong. Naming is the deliverable here, so an unresolved token gets reported.
        foreach (Match leftover in TokenPattern.Matches(name))
            unresolved.Add(leftover.Value);

        foreach (var c in IllegalNameChars)
            name = name.Replace(c, '-');

        return name.Trim();
    }

    public static string Uniquify(string name, ISet<string> taken)
    {
        if (taken.Add(name)) return name;

        for (var i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (taken.Add(candidate)) return candidate;
        }
    }

    // ------------------------------------------------------------------ run

    /// <summary>
    /// Creates the views.
    ///
    /// TRANSACTIONS: one group around the whole batch so the user gets a single undo step,
    /// and one transaction per unit inside it. Per-unit is the right granularity - a unit
    /// whose crop fails rolls back on its own and is reported, instead of discarding the
    /// forty views that already succeeded. If the batch as a whole throws, the group rolls
    /// back and the model is exactly as it was.
    /// </summary>
    /// <param name="apply">false reports what would be created and writes nothing.</param>
    /// <param name="source">
    /// View to duplicate. Its level must match the group's, or the group gets a fresh
    /// ViewPlan on its own level instead - duplicating carries the source's level with it,
    /// so a level-1 source cannot produce a level-2 unit plan.
    /// </param>
    /// <param name="groups">
    /// Groups from a previous <see cref="Collect"/>, so a caller that already scanned to
    /// size up the job does not scan again - and does not lose that scan's warnings.
    /// </param>
    /// <param name="warnings">Warnings from that scan; added to, not replaced.</param>
    public UnitViewResult Run(bool apply, ViewPlan? source) =>
        Run(apply, source, Collect(out var warnings), warnings);

    /// <inheritdoc cref="Run(bool, ViewPlan?)"/>
    public UnitViewResult Run(bool apply, ViewPlan? source, IReadOnlyList<UnitGroup> groups, List<string> warnings)
    {
        var rows = new List<UnitViewRow>();

        // Nothing to duplicate means every view would be a fresh, empty plan - the outcome
        // that produced six correctly named views with no tags and no dimensions. Stop here
        // rather than manufacture them; the caller turns this into a readable dialog.
        if (source is null && _settings.RequireMasterView)
        {
            throw new InvalidOperationException(
                $"No master view to duplicate, so the unit views would be created empty - no room tags, no dimensions, no detailing.\n\n" +
                $"Open the annotated storey plan (a Floor Plan, not the Ceiling Plan of the same name) and run this again, " +
                $"or set MasterViewName to the view you want copied.\n\n" +
                $"Set RequireMasterView = false only if blank plans are genuinely what you want.");
        }

        // Templates are deliberately INCLUDED. They are View elements sharing one name
        // namespace, so a template called like our target name would make the rename throw;
        // uniquifying against it costs nothing and avoids the failure.
        var taken = new HashSet<string>(
            new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name),
            StringComparer.OrdinalIgnoreCase);

        var floorPlanType = new FilteredElementCollector(_doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan);

        var created = 0;
        var skipped = 0;
        var failed = 0;
        var createdIds = new List<ElementId>();
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = ResolveTokens(warnings);
        var templateId = ResolveViewTemplate(source, warnings);

        // The master's own browser folder is the target every unit view should land in.
        var sourceFolder = source is null ? null : BrowserFolder(source.Id);

        // Extents are computed for EVERY unit before any view is made, so each unit's
        // footprint can be judged against its neighbours. A unit whose rooms happen to span
        // the whole building produces a crop that is technically correct and useless, and the
        // only way to recognise that is by comparison - see FlagFootprintOutliers.
        var measured = groups
            .Select(g => (Group: g, Box: CombinedBox(g.Rooms, _settings.BoundaryLocation)))
            .ToList();

        FlagFootprintOutliers(measured, warnings);

        void ProcessAll()
        {
            foreach (var (group, box) in measured)
            {
                var wanted = BuildName(group, tokens, unresolved);

                // A NAME CLAIMED BY AN EARLIER GROUP IN THIS RUN IS NOT AN EXISTING VIEW.
                // The name format carries no level, so a unit occupying two storeys produces
                // the same name twice - and treating the second as "already exists" is how the
                // upper floor of a maisonette silently never got a plan. Disambiguate by
                // storey instead of skipping, and say so.
                if (claimedThisRun.Contains(wanted))
                {
                    var byFloor = $"{wanted}-{FloorCode(group.LevelName)}";

                    warnings.Add(
                        $"Unit {group.Unit} occupies more than one storey, and NameFormat has no level token, so both storeys wanted the name '{wanted}'. " +
                        $"The {group.LevelName} plan was named '{byFloor}' instead. Put {{FLOOR}} or {{LEVEL}} in NameFormat to control this properly.");

                    wanted = byFloor;
                }

                if (_settings.SkipExisting && taken.Contains(wanted))
                {
                    rows.Add(new UnitViewRow(group.Unit, group.LevelName, group.Rooms.Count, wanted, "skipped - view exists"));
                    skipped++;
                    continue;
                }

                claimedThisRun.Add(wanted);

                if (box is null)
                {
                    warnings.Add($"Unit {group.Unit} ({group.LevelName}): no room geometry, no view created.");
                    rows.Add(new UnitViewRow(group.Unit, group.LevelName, group.Rooms.Count, "-", "failed - no geometry"));
                    failed++;
                    continue;
                }

                var name = Uniquify(wanted, taken);

                if (!apply)
                {
                    rows.Add(new UnitViewRow(group.Unit, group.LevelName, group.Rooms.Count, name, $"would create; {FootprintM2(box):0} m2"));
                    created++;
                    continue;
                }

                try
                {
                    var duplicated = false;
                    var hidden = 0;
                    var createdId = ElementId.InvalidElementId;

                    // Per level, not per run: a maisonette's upper storey needs that storey's
                    // own annotated plan, not the ground floor's.
                    var master = MasterForLevel(group.LevelId, source, warnings);

                    Transactions.Run(_doc, $"Unit plan {group.Unit}", () =>
                    {
                        var view = CreateView(group, master, floorPlanType, out duplicated);
                        view.Name = name;
                        createdId = view.Id;

                        // Template before crop. A template can control CropBoxActive and the
                        // view range, and applying it afterwards would overwrite the crop we
                        // just set.
                        if (templateId != ElementId.InvalidElementId)
                            view.ViewTemplateId = templateId;

                        if (_settings.Scale is { } scale && !IsTemplateControlled(view, BuiltInParameter.VIEW_SCALE))
                            view.Scale = scale;

                        // A template that owns 'Crop View' and holds it OFF is what produced
                        // six correctly-named, uncropped plans. Resolve it before touching the
                        // toggles, rather than writing them and finding out they did nothing.
                        if (IsTemplateControlled(view, BuiltInParameter.VIEWER_CROP_REGION) && !view.CropBoxActive)
                            ResolveTemplateCropConflict(view, group, name, warnings);

                        if (!IsTemplateControlled(view, BuiltInParameter.VIEWER_CROP_REGION))
                            view.CropBoxActive = true;

                        if (!IsTemplateControlled(view, BuiltInParameter.VIEWER_CROP_REGION_VISIBLE))
                            view.CropBoxVisible = _settings.ShowCropBoundary;

                        SetCropExtents(view, box, _settings.MarginFeet);

                        if (!view.CropBoxActive)
                            warnings.Add($"Unit {group.Unit} ({group.LevelName}): '{name}' still could not be cropped - the template holds 'Crop View' off and OnTemplateBlocksCrop is set to LeaveUncropped.");

                        // Silently skipping this when the template owned it is what let the
                        // neighbouring unit's tags stay on the plan. It now says so.
                        if (_settings.CropAnnotation && IsTemplateControlled(view, BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE))
                            warnings.Add($"Unit {group.Unit}: the view template controls 'Annotation Crop', so '{name}' may still show room tags belonging to the units either side of it.");

                        if (_settings.CropAnnotation && !IsTemplateControlled(view, BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE))
                        {
                            // Annotation crop is a view PARAMETER, not part of the crop-region
                            // shape manager - the offsets there do nothing while this is off.
                            var annotation = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                            if (annotation is { IsReadOnly: false }) annotation.Set(1);
                        }

                        // Last, so the collector sees the view exactly as it will be drawn -
                        // cropped, templated, and with the annotation crop already applied.
                        if (_settings.HideForeignElements)
                            hidden = HideForeignElements(view, group, box, warnings);
                    });

                    // Browser placement is read AFTER the commit: the organization resolves a
                    // view's folder from parameters this transaction has just written.
                    var folder = createdId == ElementId.InvalidElementId ? "(unknown)" : BrowserFolder(createdId);

                    if (sourceFolder is not null && !string.Equals(folder, sourceFolder, StringComparison.OrdinalIgnoreCase))
                        warnings.Add($"Unit {group.Unit}: '{name}' sits in browser folder '{folder}' but the master is in '{sourceFolder}'. Whatever parameter the Browser Organization groups by did not carry across.");

                    var how = duplicated ? "created - duplicated with detailing" : "created - NEW plan, not a duplicate";
                    rows.Add(new UnitViewRow(group.Unit, group.LevelName, group.Rooms.Count, name,
                        $"{how}; {FootprintM2(box):0} m2; hid {hidden} foreign; browser: {folder}"));
                    createdIds.Add(createdId);
                    created++;
                }
                catch (Exception ex)
                {
                    // The per-unit transaction has already rolled back by this point.
                    taken.Remove(name);
                    warnings.Add($"Unit {group.Unit} ({group.LevelName}): {ex.Message}");
                    rows.Add(new UnitViewRow(group.Unit, group.LevelName, group.Rooms.Count, name, "failed"));
                    failed++;
                    Log.Error($"Unit plan views: unit {group.Unit} failed", ex);
                }
            }
        }

        if (apply)
            Transactions.RunGrouped(_doc, "Create unit plan views", ProcessAll);
        else
            ProcessAll();

        if (unresolved.Count > 0)
            warnings.Add($"NameFormat contains token(s) nothing supplies: {string.Join(", ", unresolved.Order())}. " +
                         "Add them to UnitViewSettings.Tokens - every view name above has them replaced with '-'.");

        var summary = new List<string>
        {
            $"Rooms grouped into {groups.Count} unit view(s) by '{_settings.UnitParameterName}'.",
            apply ? $"Views created: {created}" : $"Views that would be created: {created}",
            $"Skipped (already present): {skipped}",
            $"Failed: {failed}",
            $"Crop margin: {_settings.MarginMm:0} mm",
            // Stated outright: the prefix is the part a reader cannot verify by eye, because
            // a plausible-looking wrong project number reads exactly like a right one.
            $"Name prefix: {tokens.GetValueOrDefault("PROJECT", "?")}-{tokens.GetValueOrDefault("CASE", "?")}-{tokens.GetValueOrDefault("BUILDING", "?")}" +
            (_settings.DeriveProjectTokensFromModel ? " (read from this model)" : " (fixed in settings)"),
            $"View template: {(templateId == ElementId.InvalidElementId ? "(none applied)" : (_doc.GetElement(templateId) as View)?.Name ?? "(none applied)")}",
            $"Source: {(source is null ? "no plan active - new views, NO detailing" : $"'{source.Name}' duplicated with detailing")}",
        };

        return new UnitViewResult(rows, summary, warnings, createdIds);
    }

    private ViewPlan CreateView(UnitGroup group, ViewPlan? source, ViewFamilyType? floorPlanType, out bool duplicated)
    {
        duplicated = true;

        // Duplicating keeps the source's view settings, filters, overrides and detailing -
        // which is the whole point when the source is the drawn, annotated storey plan. It
        // is only usable when that source sits on the group's level.
        if (source is not null
            && (!_settings.GroupPerLevel || source.GenLevel?.Id == group.LevelId)
            && source.CanViewBeDuplicated(_settings.DuplicateOption))
        {
            var copyId = source.Duplicate(_settings.DuplicateOption);
            return (ViewPlan)_doc.GetElement(copyId);
        }

        // Past this point the master could not be duplicated, so detailing will NOT come
        // across - the caller reports that per view rather than letting it pass as equivalent.
        duplicated = false;

        if (floorPlanType is null)
            throw new InvalidOperationException("No floor plan view family type exists in this document.");

        var levelId = _settings.GroupPerLevel ? group.LevelId : source?.GenLevel?.Id;
        if (levelId is null || levelId == ElementId.InvalidElementId)
            throw new InvalidOperationException($"Unit {group.Unit} has no level to place a plan view on.");

        return ViewPlan.Create(_doc, floorPlanType.Id, levelId);
    }

    /// <summary>
    /// The view to duplicate: the one named by <see cref="UnitViewSettings.MasterViewName"/>,
    /// falling back to the active plan.
    ///
    /// AMBIGUITY IS RESOLVED BY ANNOTATION COUNT, not by picking the first match. Two views
    /// sharing a name is not a hypothetical here - a Ceiling Plan and a Floor Plan both called
    /// "(02) Stueplan, terraen" exist in this model, and only one of them carries the room tags
    /// and dimensions the unit plans are supposed to inherit. Type alone does not settle it
    /// either, since both are ViewPlan to the API.
    /// </summary>
    public ViewPlan? ResolveMaster(ViewPlan? active, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(_settings.MasterViewName)) return active;

        var target = ParameterHelper.Normalize(_settings.MasterViewName);

        var candidates = new FilteredElementCollector(_doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(v => !v.IsTemplate && ParameterHelper.Normalize(v.Name) == target)
            .ToList();

        if (candidates.Count == 0)
        {
            warnings.Add($"No view named '{_settings.MasterViewName}' was found, so the active view is used instead.");
            return active;
        }

        if (candidates.Count == 1) return candidates[0];

        var ranked = candidates
            .Select(v => (View: v, Annotations: AnnotationCount(v)))
            .OrderByDescending(x => x.Annotations)
            .ToList();

        var chosen = ranked[0];
        warnings.Add(
            $"{candidates.Count} views are named '{_settings.MasterViewName}'. Duplicating the {chosen.View.ViewType} " +
            $"one, which carries {chosen.Annotations} tag(s) and dimension(s); the others carry " +
            $"{string.Join(", ", ranked.Skip(1).Select(r => $"{r.Annotations} ({r.View.ViewType})"))}.");

        return chosen.View;
    }

    /// <summary>
    /// "48 room tag(s) and 70 dimension(s)" for the confirmation dialog, so the user can see
    /// before committing whether the master actually carries anything worth duplicating.
    /// </summary>
    public string AnnotationSummary(View view)
    {
        try
        {
            var tags = new FilteredElementCollector(_doc, view.Id)
                .OfCategory(BuiltInCategory.OST_RoomTags).GetElementCount();
            var dimensions = new FilteredElementCollector(_doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Dimensions).GetElementCount();

            return tags + dimensions == 0
                ? "NO room tags or dimensions - check this is the right view"
                : $"{tags} room tag(s) and {dimensions} dimension(s)";
        }
        catch
        {
            return "detailing";
        }
    }

    /// <summary>
    /// The annotated floor plan for one level.
    ///
    /// A maisonette forced this. Unit 0380 has three rooms on the ground floor and five on
    /// the storey above, so it produces two plan groups - and a single master view can only
    /// serve one of them, because duplicating carries the source's level with it. The upper
    /// group previously fell through to a fresh, empty ViewPlan.
    ///
    /// Candidates are floor plans on that level, ranked by how much annotation they carry,
    /// for the same reason ResolveMaster ranks: this model has two views per level sharing a
    /// name, and only one of each pair is drawn on.
    /// </summary>
    public ViewPlan? MasterForLevel(ElementId levelId, ViewPlan? fallback, List<string> warnings)
    {
        if (levelId == ElementId.InvalidElementId) return fallback;
        if (_mastersByLevel.TryGetValue(levelId.Value, out var cached)) return cached;

        // UNCROPPED FIRST, then annotation count. On a re-run this level already holds the
        // unit views from last time - they are floor plans on this level too, so they are
        // candidates to become their own master. A storey plan is not cropped; a unit view
        // always is. Ranking on annotation alone would usually pick correctly and would be
        // relying on luck to do it.
        var best = new FilteredElementCollector(_doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(v => !v.IsTemplate
                        && v.ViewType == ViewType.FloorPlan
                        && v.GenLevel?.Id == levelId)
            .Select(v => (View: v, Cropped: v.CropBoxActive, Annotations: AnnotationCount(v)))
            .OrderBy(x => x.Cropped)
            .ThenByDescending(x => x.Annotations)
            .FirstOrDefault();

        var chosen = best.View;

        if (chosen is null)
        {
            var levelName = (_doc.GetElement(levelId) as Level)?.Name ?? levelId.Value.ToString();
            warnings.Add($"No floor plan exists on level '{levelName}', so that level's unit plans fall back to '{fallback?.Name ?? "a new empty plan"}' and will not carry its detailing.");
            chosen = fallback;
        }
        else if (best.Annotations == 0)
        {
            warnings.Add($"The floor plan '{chosen.Name}' carries no tags or dimensions, so unit plans duplicated from it will be bare.");
        }

        _mastersByLevel[levelId.Value] = chosen;
        return chosen;
    }

    /// <summary>Room tags plus dimensions visible in a view - the detailing worth inheriting.</summary>
    private int AnnotationCount(View view)
    {
        try
        {
            var tags = new FilteredElementCollector(_doc, view.Id)
                .OfCategory(BuiltInCategory.OST_RoomTags).GetElementCount();
            var dimensions = new FilteredElementCollector(_doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Dimensions).GetElementCount();

            return tags + dimensions;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Hides annotation and equipment belonging to other units.
    ///
    /// OWNERSHIP IS ESTABLISHED BY ROOM WHERE IT CAN BE, and only falls back to geometry where
    /// no room association exists:
    ///
    ///   Room tags       - the tagged room either is or is not one of this unit's. Exact, and
    ///                     immune to a tag dragged outside the room it labels.
    ///   Casework etc.   - FamilyInstance.Room when Revit knows it, otherwise the location
    ///                     point against the unit's extents.
    ///   Dimensions      - geometry only. A dimension has no room, so its midpoint is tested.
    ///
    /// The extents used are the unit's TRUE extents, not the crop box: the crop carries a
    /// 500 mm margin that reaches well into the neighbour, and testing against it would keep
    /// exactly the elements this is meant to remove.
    /// </summary>
    public int HideForeignElements(View view, UnitGroup group, BoundingBoxXYZ tight, List<string> warnings)
    {
        // REGENERATE FIRST. The crop was set moments ago in this same transaction, and a
        // view-based collector reports what Revit has computed, not what has been assigned.
        // Without this the collector walks the whole storey - every element the uncropped view
        // showed - which is both far slower and a different set from what the finished view
        // will draw.
        _doc.Regenerate();

        var ownRoomIds = group.Rooms.Select(r => r.Id.Value).ToHashSet();
        var tolerance = _settings.ForeignToleranceFeet;
        var foreign = new List<ElementId>();

        bool Inside(XYZ point, double slack) =>
            point.X >= tight.Min.X - slack && point.X <= tight.Max.X + slack
            && point.Y >= tight.Min.Y - slack && point.Y <= tight.Max.Y + slack;

        // THE VIEW ARGUMENT IS NOT OPTIONAL. A view-specific element - a dimension, a detail
        // line - has no model-space bounding box, so get_BoundingBox(null) returns null for
        // it. Passing null here is what hid every dimension in the first version: the centre
        // came back null, the containment test failed, and the whole category was classed as
        // foreign.
        XYZ? CentreOf(Element element)
        {
            var box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            if (box is null) return null;

            var centre = (box.Min + box.Max) / 2.0;
            return box.Transform.OfPoint(centre);
        }

        // Collecting against the view means the crop has already narrowed this to elements
        // near the unit, rather than sweeping the whole storey.
        foreach (var tag in new FilteredElementCollector(_doc, view.Id)
                     .OfCategory(BuiltInCategory.OST_RoomTags)
                     .OfType<RoomTag>())
        {
            ElementId? taggedRoom = null;
            try { taggedRoom = tag.Room?.Id; } catch { /* tag pointing at a deleted room */ }

            // A tag whose room cannot be read is left visible: hiding on a failed lookup would
            // quietly strip labels that are perfectly valid.
            if (taggedRoom is null) continue;

            if (!ownRoomIds.Contains(taggedRoom.Value)) foreign.Add(tag.Id);
        }

        foreach (var category in _settings.ForeignCategories)
        {
            foreach (var instance in new FilteredElementCollector(_doc, view.Id)
                         .OfCategory(category)
                         .OfType<FamilyInstance>())
            {
                ElementId? host = null;
                try { host = instance.Room?.Id; } catch { /* no room in this phase */ }

                if (host is not null)
                {
                    if (!ownRoomIds.Contains(host.Value)) foreign.Add(instance.Id);
                    continue;
                }

                // No room association: fall back to position, and KEEP anything whose position
                // cannot be established. Hiding on an unknown is how a whole category
                // disappears at once.
                var point = (instance.Location as LocationPoint)?.Point ?? CentreOf(instance);
                if (point is not null && !Inside(point, tolerance)) foreign.Add(instance.Id);
            }
        }

        // Dimensions get the CROP MARGIN as slack, not the tight 150 mm the others use. A
        // dimension line is drawn offset outside the wall it measures - at 1:50 a few
        // millimetres on paper is several hundred in the model - so a witness line for this
        // unit's own wall sits beyond the room boundary by more than the casework tolerance
        // allows. Judging it that tightly would delete the unit's own dimensions.
        var dimensionSlack = Math.Max(tolerance, _settings.MarginFeet);

        foreach (var dimension in new FilteredElementCollector(_doc, view.Id)
                     .OfCategory(BuiltInCategory.OST_Dimensions)
                     .WhereElementIsNotElementType())
        {
            var centre = CentreOf(dimension);
            if (centre is not null && !Inside(centre, dimensionSlack)) foreign.Add(dimension.Id);
        }

        // CanBeHidden is not optional - HideElements throws on the whole collection if one
        // member refuses, which would lose every legitimate hide alongside it.
        var hideable = foreign
            .Where(id => _doc.GetElement(id) is { } e && e.CanBeHidden(view))
            .ToList();

        if (hideable.Count > 0)
        {
            try
            {
                view.HideElements(hideable);
            }
            catch (Exception ex)
            {
                warnings.Add($"Unit {group.Unit}: could not hide {hideable.Count} element(s) from neighbouring units - {ex.Message}");
                return 0;
            }
        }

        return hideable.Count;
    }

    /// <summary>Footprint of a crop box in square metres, ignoring height.</summary>
    public static double FootprintM2(BoundingBoxXYZ box)
    {
        var area = (box.Max.X - box.Min.X) * (box.Max.Y - box.Min.Y);
        return UnitUtils.ConvertFromInternalUnits(area, UnitTypeId.SquareMeters);
    }

    /// <summary>
    /// Warns about units whose footprint is nothing like their neighbours'.
    ///
    /// WHY THIS EXISTS: unit 1567 grouped nine rooms that were all named "Bad" and scattered
    /// across the whole building, so its bounding box was the building. The crop was correct
    /// for the data and the drawing was worthless, and nothing in the run said so - it looked
    /// exactly like the five good units in the report.
    ///
    /// The median is the reference rather than the mean, so a couple of bad units cannot drag
    /// the baseline out to meet themselves. This flags; it never skips. A genuinely large
    /// penthouse beside small flats is a legitimate outlier, and only the user knows which
    /// kind they are looking at.
    /// </summary>
    private void FlagFootprintOutliers(
        IReadOnlyList<(UnitGroup Group, BoundingBoxXYZ? Box)> measured,
        List<string> warnings)
    {
        var areas = measured
            .Where(m => m.Box is not null)
            .Select(m => (m.Group, Area: FootprintM2(m.Box!)))
            .ToList();

        if (areas.Count < 3) return;   // too few to have a meaningful median

        var sorted = areas.Select(a => a.Area).Order().ToList();
        var median = sorted[sorted.Count / 2];
        if (median <= 0) return;

        foreach (var (group, area) in areas)
        {
            var ratio = area / median;

            if (ratio >= 2.0)
                warnings.Add($"Unit {group.Unit}: footprint {area:0} m2 is {ratio:0.0}x the median unit ({median:0} m2). Its rooms are spread far wider than an apartment - check they all really belong to this unit before using the view.");
            else if (ratio <= 0.5)
                warnings.Add($"Unit {group.Unit}: footprint {area:0} m2 is only {ratio:0.00}x the median unit ({median:0} m2) ON THIS LEVEL. Either rooms are missing a '{_settings.UnitParameterName}' value, or the unit continues on another storey - check the other levels before treating this as an error.");
        }
    }

    /// <summary>
    /// The view template to apply, resolved from <see cref="UnitViewSettings.ViewTemplateName"/>
    /// once per run. An explicit ViewTemplateId wins if one was supplied.
    /// </summary>
    private ElementId ResolveViewTemplate(ViewPlan? source, List<string> warnings)
    {
        if (_settings.ViewTemplateId is { } explicitId && explicitId != ElementId.InvalidElementId)
            return explicitId;

        if (string.IsNullOrWhiteSpace(_settings.ViewTemplateName))
            return ElementId.InvalidElementId;

        var target = ParameterHelper.Normalize(_settings.ViewTemplateName);

        var match = new FilteredElementCollector(_doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .FirstOrDefault(v => v.IsTemplate && ParameterHelper.Normalize(v.Name) == target);

        if (match is null)
        {
            warnings.Add($"View template '{_settings.ViewTemplateName}' was not found, so the unit views keep whatever template the master carries. Check the name against the View Templates dialog.");
            return ElementId.InvalidElementId;
        }

        // Applying a template built for another view type throws. Catching that per view would
        // report six identical failures; naming it once here is the useful version.
        if (source is not null && match.ViewType != source.ViewType)
        {
            warnings.Add($"View template '{match.Name}' is for {match.ViewType} views, not {source.ViewType}, so it was not applied.");
            return ElementId.InvalidElementId;
        }

        return match.Id;
    }

    /// <summary>
    /// Where the project browser puts a view, as a "Folder / Subfolder" path. Used to prove
    /// the unit views land beside their master rather than in the unassigned "???" bucket.
    /// </summary>
    private string BrowserFolder(ElementId viewId)
    {
        try
        {
            var organization = BrowserOrganization.GetCurrentBrowserOrganizationForViews(_doc);
            var items = organization.GetFolderItems(viewId);

            return items.Count == 0 ? "(root)" : string.Join(" / ", items.Select(i => i.Name));
        }
        catch
        {
            // No browser organization defined, or it cannot place this view. Not worth failing over.
            return "(unknown)";
        }
    }

    /// <summary>
    /// Frees the view's crop toggles from its template, by whichever route the settings ask
    /// for. Every route is reported: which of the two ways a view stopped following its
    /// template is not something to discover later from a drawing that changed on its own.
    /// </summary>
    private void ResolveTemplateCropConflict(View view, UnitGroup group, string name, List<string> warnings)
    {
        var templateId = view.ViewTemplateId;
        var templateName = _doc.GetElement(templateId) is View t ? t.Name : "(unknown)";

        switch (_settings.OnTemplateBlocksCrop)
        {
            case TemplateCropConflict.DetachTemplateFromNewView:
                // Scoped to this new view. The graphic settings the template had already
                // applied stay on the view; only the live link goes.
                view.ViewTemplateId = ElementId.InvalidElementId;
                warnings.Add($"Unit {group.Unit}: '{name}' was detached from view template '{templateName}' so it could be cropped. It keeps that template's current appearance but will not follow future edits to it.");
                break;

            case TemplateCropConflict.ReleaseCropOnTemplate:
                // Once per template per run. Every unit view hits this, and re-adding the same
                // ids on each pass would grow the template's list without changing anything.
                if (!_releasedTemplates.Add(templateId.Value)) break;

                if (_doc.GetElement(templateId) is View template)
                {
                    var released = template.GetNonControlledTemplateParameterIds().ToList();

                    // ANNOTATION CROP IS IN THIS LIST DELIBERATELY. Without it the model crop
                    // hides the neighbouring unit's walls but its room tags still draw outside
                    // the boundary - which is exactly the stray '4456 Vaer. 1' tags seen on the
                    // 1567 plan.
                    foreach (var parameter in new[]
                             {
                                 BuiltInParameter.VIEWER_CROP_REGION,
                                 BuiltInParameter.VIEWER_CROP_REGION_VISIBLE,
                                 BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE,
                             })
                    {
                        var id = new ElementId(parameter);
                        if (!released.Any(existing => existing.Value == id.Value)) released.Add(id);
                    }

                    template.SetNonControlledTemplateParameterIds(released);

                    warnings.Add($"View template '{templateName}' no longer controls the crop region, which is what lets the unit views be cropped while still using it. This affects EVERY view on that template - their current crop state is unchanged, but each now owns it. Undo reverts it with the rest of the run.");
                }
                break;

            case TemplateCropConflict.LeaveUncropped:
                break;
        }
    }

    /// <summary>
    /// True when a view template owns the parameter, in which case writing it does nothing.
    /// Checking beats catching: Revit does not throw here, it just ignores the assignment.
    /// </summary>
    private bool IsTemplateControlled(View view, BuiltInParameter parameter)
    {
        if (view.ViewTemplateId == ElementId.InvalidElementId) return false;
        if (_doc.GetElement(view.ViewTemplateId) is not View template) return false;

        // A template lists the parameters it does NOT control, so anything absent from that
        // list is controlled by it.
        return !template.GetNonControlledTemplateParameterIds()
            .Any(id => id.Value == (long)parameter);
    }
}
