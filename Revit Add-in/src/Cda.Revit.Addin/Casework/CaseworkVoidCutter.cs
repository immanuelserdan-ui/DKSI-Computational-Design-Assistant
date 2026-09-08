using Autodesk.Revit.DB;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Casework;

public sealed class CaseworkCutResult
{
    public required IReadOnlyList<string> Summary { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Cuts actually created this pass.</summary>
    public required int CutsAdded { get; init; }

    /// <summary>Pairs Revit rejected because the void does not reach the wall. Expected, not a fault.</summary>
    public required int NoIntersection { get; init; }

    /// <summary>Pairs skipped because the cut already existed. The measure of how idempotent a re-run is.</summary>
    public required int AlreadyCut { get; init; }

    public required int FittingsExamined { get; init; }
}

/// <summary>
/// Applies the Cut Geometry step that Revit will not apply on its own: every wall a casework
/// fitting's SIDE voids reach into, cut by that instance.
///
/// WHAT REVIT DOES AND DOES NOT DO
///   A family marked "Cut with Voids When Loaded" cuts its HOST when the instance is placed.
///   That is the primary void and it needs no help. Every other wall in the room is simply
///   not part of that relationship — an adjacent or intersecting wall is cut only when
///   somebody runs Modify → Cut → Cut Geometry and picks the two elements by hand. This
///   class is that hand.
///
/// THE ATTEMPT IS THE TEST, AND THAT IS THE CENTRAL DESIGN DECISION.
///   The obvious implementation measures the void solids and intersects them with each wall.
///   It cannot be written honestly against a project document. Void geometry is consumed at
///   family regeneration: <c>FamilyInstance.get_Geometry</c> returns solids, and
///   <c>IncludeNonVisibleObjects</c> does not bring the voids back. The only way to read the
///   real void forms is <c>Document.EditFamily</c>, which opens the family in the background,
///   costs the better part of a second per family, cannot be called while a transaction is
///   open — and still has to be transformed back through the instance transform and mirrored
///   flips to be usable.
///
///   Every substitute for that is an approximation: a box around the void, a bounding-box
///   overlap, a distance threshold. An approximation that says yes when the truth is no
///   creates a cut that removes nothing, and a no-op cut is not free — it is a permanent
///   relationship on the wall, it shows in Revit's cut list, and the finish engine's
///   <c>GetElementsBeingCut</c> checks will believe it.
///
///   So this does not approximate. It offers Revit a candidate pair and lets Revit's own
///   geometry engine answer, because <c>AddInstanceVoidCut</c> refuses a pair whose void does
///   not intersect the element. The refusal arrives as an exception, which is why the loop
///   below catches one on the ordinary path rather than only on the failure path. The result
///   is exact: every cut this creates is a cut whose void genuinely reaches that wall.
///
/// WHICH MAKES THE DRY RUN EXACT TOO.
///   There is no <c>apply</c> flag here. The pass always writes and must run inside a
///   transaction; a dry run is the same call inside <see cref="Transactions.Probe"/>, which
///   rolls back. That is not a shortcut — it is the only way to report what WOULD happen
///   without re-deriving Revit's answer from a worse test, and it is why the dry run and the
///   apply can never disagree.
///
/// COST. Per fitting: one bounding-box collector query, then one <c>AddInstanceVoidCut</c>
/// attempt per candidate wall. No document regeneration inside the loop, no wall geometry
/// extracted, no boolean operations. The expensive part is the rejected attempts, which is
/// why the candidate net is capped and sorted nearest-first.
/// </summary>
public sealed class CaseworkVoidCutter
{
    private readonly Document _doc;
    private readonly CaseworkSettings _settings;

    private readonly List<IReadOnlyList<string>> _rows =
        [["Fitting Id", "Family", "Type", "Target Id", "Target", "Outcome", "Detail"]];

    private readonly List<string> _warnings = [];

    /// <summary>Family id → does it carry "Cut with Voids When Loaded". Asked once per family, not per instance.</summary>
    private readonly Dictionary<long, bool> _familyAllowsCut = [];

    /// <summary>
    /// Families Revit has rejected as having no unattached void to cut with. Populated from
    /// the first refusal and then used to skip every other instance of that family, so a
    /// mis-authored family costs one warning rather than one per instance per wall.
    /// </summary>
    private readonly HashSet<long> _familyVoidsRejected = [];

    private int _cuts;
    private int _noIntersection;
    private int _alreadyCut;
    private int _notCuttable;

    public CaseworkVoidCutter(Document doc, CaseworkSettings settings)
    {
        _doc = doc;
        _settings = settings;
    }

    /// <summary>
    /// Cuts every wall the given fittings' voids reach.
    ///
    /// MUST be called inside an open transaction — it writes on the successful path. Wrap it
    /// in <see cref="Transactions.Run"/> to apply, or <see cref="Transactions.Probe"/> to get
    /// the identical answer with nothing committed.
    /// </summary>
    /// <param name="scope">
    /// The fittings to process, or null/empty for every fitting in the model. Scoping is safe
    /// here in a way it is not for the lining resolver: a cut is a property of one instance
    /// and one wall, not of a neighbourhood, so processing one fitting cannot give a
    /// different answer than processing all of them.
    /// </param>
    public CaseworkCutResult Run(IReadOnlyList<Element>? scope = null)
    {
        var fittings = Fittings(scope);

        foreach (var fitting in fittings)
        {
            try
            {
                Process(fitting);
            }
            catch (Exception ex)
            {
                // One malformed instance must not cost the other four hundred their cuts.
                _warnings.Add($"Fitting {fitting.Id.Value} could not be processed: {ex.Message}");
            }
        }

        return new CaseworkCutResult
        {
            Summary =
            [
                $"{fittings.Count} fitting(s) examined" +
                (scope is { Count: > 0 } ? " (scoped)" : " (whole model)") + ".",
                $"{_cuts} wall cut(s) created; {_alreadyCut} already cut and left alone.",
                $"{_noIntersection} candidate wall(s) refused by Revit - the voids do not reach them.",
                $"{_notCuttable} candidate(s) skipped as not cuttable with a void.",
                $"{_warnings.Count} warning(s).",
            ],
            Rows = _rows,
            Warnings = _warnings,
            CutsAdded = _cuts,
            NoIntersection = _noIntersection,
            AlreadyCut = _alreadyCut,
            FittingsExamined = fittings.Count,
        };
    }

    // ---------------------------------------------------------------- what to process

    private IReadOnlyList<FamilyInstance> Fittings(IReadOnlyList<Element>? scope)
    {
        IEnumerable<Element> source = scope is { Count: > 0 }
            ? scope
            : new FilteredElementCollector(_doc)
                .WherePasses(new ElementMulticategoryFilter(_settings.FittingCategories))
                .WhereElementIsNotElementType();

        return [.. source.OfType<FamilyInstance>().Where(IsFitting)];
    }

    private bool IsFitting(FamilyInstance instance)
    {
        // A scoped run is handed elements by id, so the category test cannot be assumed to
        // have happened upstream.
        var category = instance.Category?.Id.Value;
        if (category is null || !_settings.FittingCategories.Any(c => (long)c == category)) return false;

        if (Skipped(instance)) return false;
        if (_settings.AllCasework) return true;

        var family = instance.Symbol?.Family?.Name ?? string.Empty;
        var type = instance.Symbol?.Name ?? string.Empty;
        var blob = (family + " " + type).ToLowerInvariant();

        return _settings.NameTerms.Any(term => blob.Contains(term.ToLowerInvariant()));
    }

    private bool Skipped(Element element)
    {
        if (string.IsNullOrWhiteSpace(_settings.SkipComment)) return false;

        var comments = element
            .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?
            .AsString();

        return comments is not null &&
               comments.Contains(_settings.SkipComment, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- one fitting

    private void Process(FamilyInstance fitting)
    {
        if (!FamilyAllowsVoidCut(fitting)) return;
        if (HasRejectedVoids(fitting)) return;

        // Resolved once and reused for every candidate: the collection is what tells an
        // already-correct model apart from one that needs work, and re-reading it per
        // candidate would be the same answer at N times the cost.
        var alreadyCut = ElementsAlreadyCut(fitting);

        foreach (var target in Candidates(fitting, alreadyCut))
        {
            if (alreadyCut.Contains(target.Id.Value))
            {
                _alreadyCut++;
                Row(fitting, target, "already cut", "left alone; re-running this tool is free");
                continue;
            }

            if (!InstanceVoidCutUtils.CanBeCutWithVoid(target))
            {
                _notCuttable++;
                Row(fitting, target, "not cuttable", "Revit does not allow a void cut on this element");
                continue;
            }

            try
            {
                InstanceVoidCutUtils.AddInstanceVoidCut(_doc, target, fitting);

                _cuts++;
                alreadyCut.Add(target.Id.Value);
                Row(fitting, target, "CUT", "void reaches this wall");
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                // THE ORDINARY OUTCOME, not a fault. Most walls inside the search net are
                // simply near the fitting rather than touched by its voids, and this is the
                // refusal that makes the pass exact instead of approximate. Counted, recorded
                // in the report, and deliberately not warned about - a log line per near
                // miss would bury the real warnings under hundreds of correct decisions.
                _noIntersection++;
                Row(fitting, target, "no intersection", Trim(ex.Message));
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                // A FAMILY-LEVEL rejection, not a pair-level one, and the distinction is what
                // makes this readable. Revit documents two causes for this exception: "the
                // element cannot be cut with a void instance", which the CanBeCutWithVoid test
                // above has already ruled out, and "the element is not a family instance with
                // an UNATTACHED void that can cut" - which is a property of the family, so it
                // will be true for every remaining candidate and every other instance of it.
                //
                // Attached means the void is already consumed cutting a solid INSIDE the
                // family. A void applied to the carcass with Cut Geometry in the family editor
                // is spent; only a free-standing void is available to cut anything in the
                // project. It is the likelier of the two authoring faults, because it looks
                // completely correct in the family editor.
                //
                // So: stop trying this instance, remember the family, and warn once. Carrying
                // on would produce one identical warning per candidate wall per instance -
                // hundreds of lines all saying the same thing about one family.
                RejectFamily(fitting, ex);
                Row(fitting, target, "rejected", Trim(ex.Message));
                return;
            }
        }
    }

    /// <summary>
    /// Walls near enough to be worth offering, nearest first.
    ///
    /// Nearest-first matters only because of the cap: a fitting in a crowded plan can have
    /// more walls in its net than the cap allows, and the ones its voids actually reach are
    /// always the closest. Sorting on bounding-box centres is crude and entirely adequate for
    /// choosing which twelve of twenty walls to try.
    /// </summary>
    private IReadOnlyList<Element> Candidates(FamilyInstance fitting, ICollection<long> alreadyCut)
    {
        var box = fitting.get_BoundingBox(null);
        if (box is null)
        {
            _warnings.Add($"Fitting {fitting.Id.Value} has no bounding box in the model; skipped.");
            return [];
        }

        var reach = _settings.Reach;
        var outline = new Outline(
            new XYZ(box.Min.X - reach, box.Min.Y - reach, box.Min.Z - reach),
            new XYZ(box.Max.X + reach, box.Max.Y + reach, box.Max.Z + reach));

        var centre = (box.Min + box.Max) / 2.0;

        long? hostId = null;
        try { hostId = fitting.Host?.Id.Value; }
        catch { /* unhosted; nothing to exclude */ }

        var candidates = new FilteredElementCollector(_doc)
            .WherePasses(new ElementMulticategoryFilter(_settings.TargetCategories))
            .WhereElementIsNotElementType()
            .WherePasses(new BoundingBoxIntersectsFilter(outline))
            .ToList();

        return
        [
            .. candidates
                .Where(w => _settings.CutHost || w.Id.Value != hostId)
                .OrderBy(w => DistanceFrom(centre, w))
                .Take(_settings.MaxCandidatesPerInstance)
        ];
    }

    private static double DistanceFrom(XYZ point, Element element)
    {
        var box = element.get_BoundingBox(null);
        if (box is null) return double.MaxValue;

        return point.DistanceTo((box.Min + box.Max) / 2.0);
    }

    /// <summary>
    /// The elements this instance already cuts, as raw ids.
    ///
    /// Raw <c>long</c> rather than <c>ElementId</c> because this set is hit once per
    /// candidate and <c>ElementId</c> equality goes through a managed wrapper each time. The
    /// same reason RoomFinishCalculator compares <c>Id.Value</c> in its own already-cut
    /// checks.
    /// </summary>
    private HashSet<long> ElementsAlreadyCut(FamilyInstance fitting)
    {
        try
        {
            return [.. InstanceVoidCutUtils.GetElementsBeingCut(fitting).Select(id => id.Value)];
        }
        catch
        {
            // Not a cutting instance yet - it has never cut anything, which is exactly the
            // case this tool exists for.
            return [];
        }
    }

    /// <summary>True once Revit has refused this instance's family for want of a usable void.</summary>
    private bool HasRejectedVoids(FamilyInstance fitting)
    {
        var family = fitting.Symbol?.Family;
        return family is not null && _familyVoidsRejected.Contains(family.Id.Value);
    }

    private void RejectFamily(FamilyInstance fitting, Exception ex)
    {
        var family = fitting.Symbol?.Family;
        if (family is null)
        {
            _warnings.Add($"Fitting {fitting.Id.Value}: {Trim(ex.Message)}");
            return;
        }

        if (!_familyVoidsRejected.Add(family.Id.Value)) return;

        _warnings.Add(
            $"Family '{family.Name}' has no void Revit will cut with, so every instance of it is " +
            $"being skipped. Revit said: {Trim(ex.Message)} Usual cause: the side voids are " +
            "ATTACHED - already applied to the carcass with Cut Geometry inside the family. A void " +
            "that cuts a solid in the family is spent and cannot cut anything in the project. Open " +
            "the family, uncut the side voids from the carcass, and reload.");
    }

    /// <summary>
    /// Whether the family is flagged "Cut with Voids When Loaded".
    ///
    /// THIS IS THE ONE SETUP STEP THE TOOL CANNOT DO FOR YOU. The flag lives in the family
    /// document (Family Category and Parameters), is read-only once the family is loaded, and
    /// without it <c>AddInstanceVoidCut</c> rejects every pair — so a family that misses it
    /// produces a pass that looks like it ran and cut nothing. Checking it up front turns
    /// that into one warning naming the family, which is the difference between a five-minute
    /// fix and an afternoon.
    ///
    /// A missing parameter is treated as "let Revit decide" rather than as a no. Not every
    /// category exposes it, and refusing to try on that basis would be a guess overruling the
    /// authority.
    /// </summary>
    private bool FamilyAllowsVoidCut(FamilyInstance fitting)
    {
        var family = fitting.Symbol?.Family;
        if (family is null) return true;

        var key = family.Id.Value;
        if (_familyAllowsCut.TryGetValue(key, out var known)) return known;

        var parameter = family.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS);
        var allowed = parameter is null || parameter.AsInteger() == 1;

        _familyAllowsCut[key] = allowed;

        if (!allowed)
        {
            _warnings.Add(
                $"Family '{family.Name}' does not have 'Cut with Voids When Loaded' ticked, so Revit " +
                "refuses every void cut on it. Open the family, Create > Family Category and " +
                "Parameters, tick it, and reload - then re-run this tool.");
        }

        return allowed;
    }

    // ---------------------------------------------------------------- reporting

    private void Row(FamilyInstance fitting, Element target, string outcome, string detail) =>
        _rows.Add(
        [
            fitting.Id.Value.ToString(),
            fitting.Symbol?.Family?.Name ?? string.Empty,
            fitting.Symbol?.Name ?? string.Empty,
            target.Id.Value.ToString(),
            Describe(target),
            outcome,
            detail,
        ]);

    private static string Describe(Element element)
    {
        var type = element.Document.GetElement(element.GetTypeId())?.Name;
        return string.IsNullOrWhiteSpace(type) ? element.Name : type;
    }

    /// <summary>Revit's geometry messages run to several sentences; the first is the one that says why.</summary>
    private static string Trim(string message)
    {
        var line = message.Replace("\r", " ").Replace("\n", " ").Trim();
        return line.Length <= 160 ? line : line[..157] + "...";
    }
}
