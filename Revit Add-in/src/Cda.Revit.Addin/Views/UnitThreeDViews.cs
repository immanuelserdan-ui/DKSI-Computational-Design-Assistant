using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Views;

/// <summary>Where the camera for each unit view comes from.</summary>
public enum UnitViewOrientation
{
    /// <summary>
    /// The ViewCube's Top-Front-Right corner: the standard isometric.
    ///
    /// Fixed, so every unit view is identical regardless of how the master happened to be
    /// spun when the command was run - which copying the master cannot promise.
    /// </summary>
    TopFrontRight,

    /// <summary>Whatever the master 3D view is currently looking at.</summary>
    CopyFromMaster,
}

/// <summary>
/// The 3D half of the per-unit deliverable. Grouping, tokens and naming are shared with the
/// plan pass - see <see cref="UnitPlanViewBuilder"/> - so a unit gets the same identity in
/// both, and only the things that differ in 3D live here.
/// </summary>
public sealed record Unit3dViewSettings
{
    /// <summary>
    /// Master 3D view to duplicate, by name. Null uses the active 3D view.
    ///
    /// Duplicating carries the camera with it, which is what requirement 4 actually needs -
    /// set the master to the orientation you want and every unit inherits it exactly, rather
    /// than reproducing an angle from numbers.
    /// </summary>
    public string? MasterViewName { get; init; }

    /// <summary>Refuse to create views rather than create them without the master's setup.</summary>
    public bool RequireMasterView { get; init; } = true;

    /// <summary>
    /// Camera for every created view. Top-Front-Right by default - the ViewCube corner - so
    /// the set is consistent even if the master is left at some other angle.
    /// </summary>
    public UnitViewOrientation Orientation { get; init; } = UnitViewOrientation.TopFrontRight;

    /// <summary>
    /// Force a parallel (isometric) projection. The ViewCube corner reads as a true isometric
    /// only without perspective convergence, and a duplicate inherits the master's projection.
    /// </summary>
    public bool ForceIsometricProjection { get; init; } = true;

    /// <summary>
    /// 3D view template, by name.
    ///
    /// NOTE THE SEPARATORS: the 3D template is "SMB-Export-3d" with hyphens, while the plan
    /// template is "SMB_Export-2d" with an underscore after SMB. Lookup is whitespace-tolerant
    /// but not punctuation-tolerant, so the two are written out exactly as they appear in the
    /// View Templates dialog rather than derived from one another.
    /// </summary>
    public string? ViewTemplateName { get; init; } = "SMB-Export-3d";

    /// <summary>
    /// What to do when the template holds the section box off - the 3D twin of the crop
    /// problem SMB_Export-2d caused on the plans, and it fails the same silent way: assigning
    /// IsSectionBoxActive against a template-controlled parameter simply does nothing.
    /// </summary>
    public TemplateCropConflict OnTemplateBlocksSectionBox { get; init; } = TemplateCropConflict.ReleaseCropOnTemplate;

    /// <summary>Margin around the unit on all six faces of the section box.</summary>
    public double MarginMm { get; init; } = 500;

    /// <summary>
    /// Extra headroom above the rooms, so the roof over the unit is inside the box rather
    /// than sliced off at ceiling level. Rooms stop at their upper limit; the structure above
    /// them does not.
    /// </summary>
    public double RoofHeadroomMm { get; init; } = 1500;

    public bool SkipExisting { get; init; } = true;
    public bool HideForeignElements { get; init; } = true;

    /// <summary>
    /// Hide model elements belonging to any level this unit does not occupy.
    ///
    /// The section box alone is not enough. It clips by Z, which is a different test from
    /// "belongs to another storey": a slab sitting on the box boundary, a wall spanning two
    /// levels or a stair all survive the clip and put a sliver of the flat above or below
    /// into a view that is supposed to show one apartment.
    /// </summary>
    public bool HideOtherLevels { get; init; } = true;
    public bool OpenAndZoomCreatedViews { get; init; } = true;

    /// <summary>
    /// Name layout. MUST differ from the plan format or the 3D view collides with the plan of
    /// the same unit and gets a " (2)" suffix.
    ///
    /// {TYPE} defaults to T3D, WHICH IS A PLACEHOLDER I INVENTED - the reference sheets show
    /// T21/T23/T25 for plans and say nothing about 3D. Set it to the real code before these
    /// names reach a sheet.
    /// </summary>
    public string NameFormat { get; init; } =
        "{PROJECT}-{CASE}-{BUILDING}-{UNIT}-{TYPE}-{PHASE}-{ROLE}-{VERSION}-{REVISION}";

    /// <summary>Token overrides applied on top of the plan pass's resolved tokens.</summary>
    public IReadOnlyDictionary<string, string> TokenOverrides { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["TYPE"] = "T3D" };

    internal double MarginFeet => UnitUtils.ConvertToInternalUnits(MarginMm, UnitTypeId.Millimeters);
    internal double HeadroomFeet => UnitUtils.ConvertToInternalUnits(RoofHeadroomMm, UnitTypeId.Millimeters);
}

/// <summary>
/// Creates one section-boxed 3D view per apartment unit.
///
/// The section box is the 3D equivalent of the plan's crop: same rooms, same grouping, but
/// bounded in Z as well. Everything that is genuinely shared with the plan pass is delegated
/// to <see cref="UnitPlanViewBuilder"/> rather than copied.
/// </summary>
public sealed class Unit3dViewBuilder
{
    private readonly Document _doc;
    private readonly UnitViewSettings _shared;
    private readonly Unit3dViewSettings _settings;
    private readonly UnitPlanViewBuilder _common;
    private readonly HashSet<long> _releasedTemplates = [];

    public Unit3dViewBuilder(Document doc, UnitViewSettings shared, Unit3dViewSettings settings)
    {
        _doc = doc;
        _shared = shared;
        _settings = settings;
        _common = new UnitPlanViewBuilder(doc, shared);
    }

    /// <summary>Grouping is the plan pass's, then merged across levels - see <see cref="MergeAcrossLevels"/>.</summary>
    public IReadOnlyList<UnitGroup> Collect(out List<string> warnings) =>
        MergeAcrossLevels(_common.Collect(out warnings));

    /// <summary>
    /// Collapses a unit's per-level groups into one.
    ///
    /// THE TWO PASSES MUST GROUP DIFFERENTLY, and a maisonette is why. A plan view binds to a
    /// single level, so unit 0380 - three rooms downstairs, five upstairs - correctly needs
    /// two plans. A 3D view has no such constraint, and splitting it the same way would give
    /// that flat two section boxes each showing half of it, when what the deliverable wants is
    /// one box round the whole apartment.
    ///
    /// The merged group carries no level, which is right: it does not belong to one.
    /// </summary>
    public static IReadOnlyList<UnitGroup> MergeAcrossLevels(IReadOnlyList<UnitGroup> groups) =>
        groups
            .GroupBy(g => g.Unit, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var levels = g.Select(x => x.LevelName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var rooms = g.SelectMany(x => x.Rooms).ToList();

                return new UnitGroup(
                    g.Key,
                    levels.Count == 1 ? g.First().LevelId : ElementId.InvalidElementId,
                    levels.Count == 1 ? levels[0] : $"{levels.Count} levels",
                    rooms);
            })
            .OrderBy(g => g.Unit, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ------------------------------------------------------------------ geometry

    /// <summary>
    /// The unit's 3D extents.
    ///
    /// X and Y come from the room boundary curves, exactly as the plan crop does, so the two
    /// deliverables agree on where a unit ends. Z CANNOT come from those curves - they are a
    /// flat loop at floor level - so it is taken from the rooms' element bounding boxes, which
    /// carry the room volume's height, with headroom added for the roof above.
    /// </summary>
    public BoundingBoxXYZ? SectionBoxFor(UnitGroup group)
    {
        var plan = UnitPlanViewBuilder.CombinedBox(group.Rooms, _shared.BoundaryLocation);
        if (plan is null) return null;

        double minZ = double.MaxValue, maxZ = double.MinValue;
        var foundZ = false;

        foreach (var room in group.Rooms)
        {
            var box = room.get_BoundingBox(null);
            if (box is null) continue;

            foreach (var corner in new[] { box.Min, box.Max })
            {
                var p = box.Transform.OfPoint(corner);
                minZ = Math.Min(minZ, p.Z);
                maxZ = Math.Max(maxZ, p.Z);
                foundZ = true;
            }
        }

        if (!foundZ)
        {
            // No room volumes - fall back to the level with a nominal storey height, which is
            // wrong by a little rather than empty.
            var level = _doc.GetElement(group.LevelId) as Level;
            minZ = level?.ProjectElevation ?? 0;
            maxZ = minZ + UnitUtils.ConvertToInternalUnits(3000, UnitTypeId.Millimeters);
        }

        var margin = _settings.MarginFeet;

        return new BoundingBoxXYZ
        {
            Min = new XYZ(plan.Min.X - margin, plan.Min.Y - margin, minZ - margin),
            Max = new XYZ(plan.Max.X + margin, plan.Max.Y + margin, maxZ + _settings.HeadroomFeet),
        };
    }

    // ------------------------------------------------------------------ camera

    /// <summary>
    /// The ViewCube's Top-Front-Right corner, aimed at a section box.
    ///
    /// In Revit's project coordinates the cube's faces are: TOP = +Z, RIGHT = +X, and FRONT =
    /// -Y (the front elevation looks north, so the viewer stands on the negative-Y side).
    /// The corner where those three meet therefore puts the camera along (+1, -1, +1), and
    /// the view direction is the negative of that.
    ///
    /// UP IS DERIVED, NOT ASSUMED. World +Z is not perpendicular to that diagonal, and
    /// SetOrientation requires an up vector orthogonal to forward, so +Z is projected onto the
    /// plane normal to forward:  up = Z - (Z . f) f, normalised. Passing raw +Z here is the
    /// usual cause of a view that ends up subtly rolled or is rejected outright.
    ///
    /// The eye sits back along the diagonal by three box diagonals - far enough that nothing
    /// clips in a parallel projection, where the distance has no effect on framing anyway.
    /// </summary>
    public static ViewOrientation3D TopFrontRightOrientation(BoundingBoxXYZ box)
    {
        var centre = (box.Min + box.Max) / 2.0;
        var toEye = new XYZ(1, -1, 1).Normalize();
        var forward = toEye.Negate();

        var up = XYZ.BasisZ
            .Subtract(forward.Multiply(XYZ.BasisZ.DotProduct(forward)))
            .Normalize();

        var standOff = Math.Max(box.Max.DistanceTo(box.Min) * 3.0, 100.0);
        var eye = centre.Add(toEye.Multiply(standOff));

        return new ViewOrientation3D(eye, up, forward);
    }

    /// <summary>
    /// Switches the section box on, dealing with a template that owns it.
    ///
    /// This is the same failure that produced six uncropped plans: a template-controlled
    /// parameter accepts the assignment and ignores it, so the only honest test is to write
    /// the value and read it back. Releasing the parameter on the template is the default,
    /// because the alternative - detaching - would discard the template the user just asked
    /// for. Detaching stays available for anyone unwilling to touch a shared standard.
    /// </summary>
    private void ActivateSectionBox(View3D view, UnitGroup group, string name, List<string> warnings)
    {
        view.IsSectionBoxActive = true;
        if (view.IsSectionBoxActive) return;

        var templateId = view.ViewTemplateId;
        var templateName = _doc.GetElement(templateId) is View t ? t.Name : "(unknown)";

        switch (_settings.OnTemplateBlocksSectionBox)
        {
            case TemplateCropConflict.DetachTemplateFromNewView:
                view.ViewTemplateId = ElementId.InvalidElementId;
                view.IsSectionBoxActive = true;
                warnings.Add($"Unit {group.Unit}: '{name}' was detached from '{templateName}' so the section box could be switched on. It keeps that template's current appearance but will not follow future edits.");
                break;

            case TemplateCropConflict.ReleaseCropOnTemplate:
                if (_releasedTemplates.Add(templateId.Value) && _doc.GetElement(templateId) is View template)
                {
                    var released = template.GetNonControlledTemplateParameterIds().ToList();
                    var id = new ElementId(BuiltInParameter.VIEWER_MODEL_CLIP_BOX_ACTIVE);

                    if (!released.Any(existing => existing.Value == id.Value)) released.Add(id);
                    template.SetNonControlledTemplateParameterIds(released);

                    warnings.Add($"View template '{templateName}' no longer controls the section box, so each 3D view owns its own. This affects EVERY view on that template; their current state is unchanged. Undo reverts it with the rest of the run.");
                }

                view.IsSectionBoxActive = true;
                if (!view.IsSectionBoxActive)
                    warnings.Add($"Unit {group.Unit}: '{name}' still could not activate its section box, so it shows the whole model.");
                break;

            case TemplateCropConflict.LeaveUncropped:
                warnings.Add($"Unit {group.Unit}: '{name}' has the right section box but it is switched off by template '{templateName}', so the view shows the whole model.");
                break;
        }
    }

    /// <summary>
    /// Hides model elements belonging to levels this unit does not occupy.
    ///
    /// The unit's levels come from its ROOMS, not from the group's single LevelId - a
    /// maisonette group spans several and carries no level of its own, so asking the group
    /// would exclude half the flat.
    ///
    /// Elements whose level cannot be determined are KEPT. Hiding on an unknown is how an
    /// entire category vanishes at once, which has already happened here once with dimensions;
    /// a stray element is easier to spot than a missing one. Categories are restricted to
    /// model geometry so that view-specific annotation is left to the foreign-element pass.
    /// </summary>
    private int HideOtherLevelElements(View3D view, UnitGroup group, List<string> warnings)
    {
        var unitLevels = group.Rooms
            .Select(r => r.LevelId.Value)
            .Where(id => id != ElementId.InvalidElementId.Value)
            .ToHashSet();

        if (unitLevels.Count == 0) return 0;

        var foreign = new List<ElementId>();

        foreach (var element in new FilteredElementCollector(_doc, view.Id).WhereElementIsNotElementType())
        {
            if (element.Category is not { CategoryType: CategoryType.Model }) continue;

            var levelId = element.LevelId;
            if (levelId == ElementId.InvalidElementId) continue;   // unknown -> keep

            if (!unitLevels.Contains(levelId.Value)) foreign.Add(element.Id);
        }

        var hideable = foreign
            .Where(id => _doc.GetElement(id) is { } e && e.CanBeHidden(view))
            .ToList();

        if (hideable.Count == 0) return 0;

        try
        {
            view.HideElements(hideable);
            return hideable.Count;
        }
        catch (Exception ex)
        {
            warnings.Add($"Unit {group.Unit}: could not hide {hideable.Count} element(s) from other levels - {ex.Message}");
            return 0;
        }
    }

    /// <summary>Applies the configured camera. Section box must already be set - it frames the shot.</summary>
    private void ApplyOrientation(View3D view, View3D? master, BoundingBoxXYZ box, UnitGroup group, List<string> warnings)
    {
        try
        {
            // Toggle before orienting: switching projection can reset the camera.
            if (_settings.ForceIsometricProjection && view.IsPerspective) view.ToggleToIsometric();

            var orientation = _settings.Orientation == UnitViewOrientation.CopyFromMaster && master is not null
                ? master.GetOrientation()
                : TopFrontRightOrientation(box);

            view.SetOrientation(orientation);
        }
        catch (Exception ex)
        {
            // A locked or template-controlled camera is not worth losing the view over.
            warnings.Add($"Unit {group.Unit}: the camera could not be set - {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ views

    /// <summary>
    /// The master 3D view, by name or the active one. Ambiguous names are broken by picking
    /// the non-template match with a section box already set up, then the first.
    /// </summary>
    public View3D? ResolveMaster(View3D? active, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(_settings.MasterViewName)) return active;

        var target = ParameterHelper.Normalize(_settings.MasterViewName);

        var candidates = new FilteredElementCollector(_doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .Where(v => !v.IsTemplate && ParameterHelper.Normalize(v.Name) == target)
            .ToList();

        if (candidates.Count == 0)
        {
            warnings.Add($"No 3D view named '{_settings.MasterViewName}' was found, so the active 3D view is used instead.");
            return active;
        }

        if (candidates.Count > 1)
            warnings.Add($"{candidates.Count} 3D views are named '{_settings.MasterViewName}'; the first was used.");

        return candidates[0];
    }

    private ElementId ResolveTemplate(List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(_settings.ViewTemplateName)) return ElementId.InvalidElementId;

        var target = ParameterHelper.Normalize(_settings.ViewTemplateName);

        var match = new FilteredElementCollector(_doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .FirstOrDefault(v => v.IsTemplate && ParameterHelper.Normalize(v.Name) == target);

        if (match is null)
        {
            warnings.Add($"3D view template '{_settings.ViewTemplateName}' was not found, so none was applied.");
            return ElementId.InvalidElementId;
        }

        return match.Id;
    }

    private View3D CreateView(View3D? master, ViewFamilyType? type, out bool duplicated)
    {
        duplicated = true;

        if (master is not null)
        {
            // WithDetailing is not offered by every 3D view; fall back rather than fail, and
            // report which happened.
            foreach (var option in new[] { ViewDuplicateOption.WithDetailing, ViewDuplicateOption.Duplicate })
            {
                if (!master.CanViewBeDuplicated(option)) continue;

                var copyId = master.Duplicate(option);
                if (_doc.GetElement(copyId) is View3D copy) return copy;
            }
        }

        duplicated = false;

        if (type is null)
            throw new InvalidOperationException("No 3D view family type exists in this document.");

        // The camera is set later by ApplyOrientation, for duplicates and fresh isometrics
        // alike - so both end up at the same angle rather than only the fallback path being
        // explicit about it.
        return View3D.CreateIsometric(_doc, type.Id);
    }

    // ------------------------------------------------------------------ run

    /// <summary>
    /// Creates the 3D views. Same transaction shape as the plan pass: a group around the batch
    /// for one undo step, one transaction per unit inside it so a single failure is isolated.
    /// </summary>
    public UnitViewResult Run(bool apply, View3D? master, IReadOnlyList<UnitGroup> groups, List<string> warnings)
    {
        if (master is null && _settings.RequireMasterView)
        {
            throw new InvalidOperationException(
                "No master 3D view to duplicate, so every unit view would use Revit's default camera rather than your orientation.\n\n" +
                "Open the 3D view whose angle you want copied and run this again, or set MasterViewName.\n\n" +
                "Set RequireMasterView = false to accept the default isometric.");
        }

        var rows = new List<UnitViewRow>();
        var createdIds = new List<ElementId>();

        var taken = new HashSet<string>(
            new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name),
            StringComparer.OrdinalIgnoreCase);

        var threeDType = new FilteredElementCollector(_doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);

        // Tokens are the plan pass's, with TYPE overridden so the 3D name cannot collide with
        // the plan name for the same unit.
        var tokens = new Dictionary<string, string>(_common.ResolveTokens(warnings), StringComparer.OrdinalIgnoreCase);
        foreach (var (token, value) in _settings.TokenOverrides) tokens[token] = value;

        var templateId = ResolveTemplate(warnings);
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var created = 0;
        var skipped = 0;
        var failed = 0;

        void ProcessAll()
        {
            foreach (var group in groups)
            {
                var wanted = _common.BuildName(group, tokens, _settings.NameFormat, unresolved);

                if (_settings.SkipExisting && taken.Contains(wanted))
                {
                    rows.Add(new UnitViewRow(group.Unit, group.LevelName, group.Rooms.Count, wanted, "skipped - view exists"));
                    skipped++;
                    continue;
                }

                var box = SectionBoxFor(group);
                if (box is null)
                {
                    warnings.Add($"Unit {group.Unit} ({group.LevelName}): no room geometry, no 3D view created.");
                    rows.Add(new UnitViewRow(group.Unit, group.LevelName, group.Rooms.Count, "-", "failed - no geometry"));
                    failed++;
                    continue;
                }

                var name = UnitPlanViewBuilder.Uniquify(wanted, taken);

                if (!apply)
                {
                    rows.Add(new UnitViewRow(group.Unit, group.LevelName, group.Rooms.Count, name, "would create"));
                    created++;
                    continue;
                }

                try
                {
                    var duplicated = false;
                    var hidden = 0;
                    var createdId = ElementId.InvalidElementId;

                    Transactions.Run(_doc, $"Unit 3D {group.Unit}", () =>
                    {
                        var view = CreateView(master, threeDType, out duplicated);
                        view.Name = name;
                        createdId = view.Id;

                        if (templateId != ElementId.InvalidElementId)
                            view.ViewTemplateId = templateId;

                        // Order matters: the box must be set before it is switched on, or Revit
                        // activates whatever box the master happened to carry.
                        view.SetSectionBox(box);
                        ActivateSectionBox(view, group, name, warnings);

                        // After the section box, because the camera is aimed at its centre.
                        ApplyOrientation(view, master, box, group, warnings);

                        // Levels first: it removes whole storeys cheaply, so the foreign-element
                        // pass afterwards has less to walk and cannot re-judge what is gone.
                        if (_settings.HideOtherLevels)
                            hidden += HideOtherLevelElements(view, group, warnings);

                        if (_settings.HideForeignElements)
                        {
                            // The plan pass's rule, reused: ownership by room where a room
                            // exists, geometry only where it does not.
                            var tight = UnitPlanViewBuilder.CombinedBox(group.Rooms, _shared.BoundaryLocation);
                            if (tight is not null)
                                hidden += _common.HideForeignElements(view, group, tight, warnings);
                        }
                    });

                    var how = duplicated ? "created - duplicated from master" : "created - NEW isometric, master camera copied";
                    rows.Add(new UnitViewRow(group.Unit, group.LevelName, group.Rooms.Count, name,
                        $"{how}; hid {hidden} foreign"));
                    createdIds.Add(createdId);
                    created++;
                }
                catch (Exception ex)
                {
                    taken.Remove(name);
                    warnings.Add($"Unit {group.Unit} ({group.LevelName}): {ex.Message}");
                    rows.Add(new UnitViewRow(group.Unit, group.LevelName, group.Rooms.Count, name, "failed"));
                    failed++;
                    Log.Error($"Unit 3D views: unit {group.Unit} failed", ex);
                }
            }
        }

        if (apply)
            Transactions.RunGrouped(_doc, "Create unit 3D views", ProcessAll);
        else
            ProcessAll();

        if (unresolved.Count > 0)
            warnings.Add($"NameFormat contains token(s) nothing supplies: {string.Join(", ", unresolved.Order())}.");

        var summary = new List<string>
        {
            $"Rooms grouped into {groups.Count} unit(s) by '{_shared.UnitParameterName}'.",
            apply ? $"3D views created: {created}" : $"3D views that would be created: {created}",
            $"Skipped (already present): {skipped}",
            $"Failed: {failed}",
            $"Section box margin: {_settings.MarginMm:0} mm, plus {_settings.RoofHeadroomMm:0} mm headroom for the roof",
            $"View template: {(templateId == ElementId.InvalidElementId ? $"NOT APPLIED - '{_settings.ViewTemplateName}' not found" : (_doc.GetElement(templateId) as View)?.Name ?? "(none)")}",
            $"Other levels: {(_settings.HideOtherLevels ? "hidden - each view shows only the storeys its unit occupies" : "left visible")}",
            $"Camera: {(_settings.Orientation == UnitViewOrientation.TopFrontRight ? "Top-Front-Right isometric (ViewCube corner)" : master is null ? "Revit default" : $"copied from '{master.Name}'")}" +
            (_settings.ForceIsometricProjection ? ", parallel projection" : string.Empty),
            $"Name prefix: {tokens.GetValueOrDefault("PROJECT", "?")}-{tokens.GetValueOrDefault("CASE", "?")}-{tokens.GetValueOrDefault("BUILDING", "?")}, type token '{tokens.GetValueOrDefault("TYPE", "?")}'",
        };

        return new UnitViewResult(rows, summary, warnings, createdIds);
    }
}
