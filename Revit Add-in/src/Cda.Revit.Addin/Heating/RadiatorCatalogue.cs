using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Linings;

namespace Cda.Revit.Addin.Heating;

/// <summary>One radiator type, with the size it actually is.</summary>
/// <param name="Source">
/// How Length/Height/Depth were arrived at. Carried through into the report because a
/// placement decided from a parsed type name deserves less confidence than one decided from
/// measured geometry, and the reader has no other way to tell them apart.
/// </param>
public sealed record RadiatorSize(
    FamilySymbol Symbol,
    double Length,
    double Height,
    double Depth,
    string Source)
{
    /// <summary>
    /// Underside of the occupied band, measured from the instance's own insertion level.
    ///
    /// NOT the panel's underside. This family stands on feet that reach the slab, so ZLo is
    /// zero while the heating surface starts 120 mm up. That distinction cost a whole run:
    /// the verify step measured the full solid, found it touching the floor, and reported
    /// "only 0 mm off the floor (wanted 120)" on six panels that were positioned perfectly.
    /// </summary>
    public double ZLo { get; init; }

    /// <summary>
    /// Top of the occupied band above the insertion level - the number that has to clear the
    /// sill.
    ///
    /// IT ALREADY INCLUDES THE FLOOR OFFSET. Measured at 720 mm on a 600 mm panel held 120 mm
    /// up. The engine used to add its floor clearance to this again, so every panel was
    /// budgeted 120 mm taller than it is and its sill gap under-reported by the same amount -
    /// window 28306818 has its sill at 1100 mm and was told it had 260 mm of clearance when
    /// it had 380 mm.
    /// </summary>
    public double ZHi { get; init; }

    /// <summary>
    /// Distance along the wall from the insertion point to the middle of the panel.
    ///
    /// A family's origin is wherever its author put it, and in this one it is 53 mm off centre
    /// - which is exactly how far every panel of the last run sat from where it was planned.
    /// Measured once and subtracted at placement, so the PANEL lands centred on the window
    /// rather than the insertion point landing centred and the panel hanging off it.
    /// </summary>
    public double UOffset { get; init; }

    public string Label => $"{Symbol.Family?.Name} : {Symbol.Name}";

    public string Describe() =>
        $"{Symbol.Name} ({Measure.ToMillimetres(Length):0} long x " +
        $"{Measure.ToMillimetres(Depth):0} deep, occupying " +
        $"{Measure.ToMillimetres(ZLo):0}-{Measure.ToMillimetres(ZHi):0} mm above the floor" +
        (Math.Abs(UOffset) > 1e-6
            ? $", origin {Measure.ToMillimetres(UOffset):+0;-0} mm off centre"
            : string.Empty) +
        $", {Source})";
}

/// <summary>
/// The ladder of radiator types available in this model, each measured rather than believed.
///
/// WHY MEASURING IS NOT PARANOIA HERE
///   The obvious way to size a panel is to read the type. This model says why not to. The
///   type named "L 1000 H 600" carries:
///
///       Radiator Length   600 mm
///       Radiator Height   600 mm
///
///   Length and Height are the same number, and Length disagrees with the type's own name by
///   400 mm. One of the two is wrong and nothing in the parameter says which. A tool that
///   trusts 'Radiator Length' plans every panel 400 mm short; one that trusts the name plans
///   every panel 400 mm long and drives it through the door reveal beside it. Both look
///   entirely deliberate in a view.
///
///   Meanwhile the model contains "L 1000  H 600" with two spaces next to "L 1000 H 600"
///   with one, so even the name is not a reliable key.
///
///   So the size is taken from the geometry: one instance of each type is placed in a
///   transaction that is thrown away, measured along the wall it was placed on, and deleted.
///   That is the only source that cannot lie, because it is the thing that will be in the
///   drawing. The declared values are still read - as a CROSS-CHECK, reported when they
///   disagree, so the family gets fixed rather than quietly worked around forever.
///
/// COST
///   One placement and one rollback per type, once per run. This model has two radiator
///   types; a supplier catalogue might have thirty. Either way it is a fixed cost paid
///   before the first real decision, not per window.
/// </summary>
public sealed class RadiatorCatalogue
{
    private static readonly Regex NameSize =
        new(@"L\s*(\d+(?:[.,]\d+)?)\s*[xX]?\s*H\s*(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase);

    private readonly Document _doc;
    private readonly RadiatorSettings _settings;
    private readonly List<RadiatorSize> _sizes = [];
    private readonly List<string> _warnings = [];

    public RadiatorCatalogue(Document doc, RadiatorSettings settings)
    {
        _doc = doc;
        _settings = settings;
    }

    /// <summary>Every usable type, longest first. Empty until <see cref="Calibrate"/> runs.</summary>
    public IReadOnlyList<RadiatorSize> Sizes => _sizes;

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>The family name that actually matched, for the report.</summary>
    public string ResolvedFamily { get; private set; } = string.Empty;

    /// <summary>
    /// How this family may be placed. Reported rather than acted on, because the placement
    /// call either works or throws and that is the more reliable test - but a reader trying
    /// to work out why a panel landed where it did needs this in front of them.
    /// </summary>
    public string Placement { get; private set; } = "unknown placement";

    /// <summary>
    /// True when the family can be hosted on a named FACE, which is the only way this add-in
    /// can choose which side of a wall a panel lands on.
    ///
    /// A OneLevelBasedHosted family cannot. Revit puts its geometry on the side the host
    /// wall's own orientation dictates and offers nothing that changes it afterwards -
    /// measured, not assumed: 96 of 96 failures on this model were walls whose Orientation
    /// points away from the room, and flipping, hand-flipping, moving, mirroring and
    /// re-placing against the far face all left the panel exactly where it was.
    /// </summary>
    public bool FaceBased { get; private set; }

    /// <summary>Other radiator families in the model, and whether any of them is face-based.</summary>
    public IReadOnlyList<string> Alternatives => _alternatives;

    private readonly List<string> _alternatives = [];

    /// <summary>
    /// Measures every type of the radiator family.
    ///
    /// MUST BE CALLED INSIDE A TRANSACTION THAT WILL BE ROLLED BACK - see
    /// <see cref="Transactions.Probe"/>. It creates instances and does not clean them up,
    /// because the rollback is what cleans them up, and a rollback is more thorough than a
    /// delete: it leaves no undo entry, no element ids consumed, and no trace in the model
    /// if the run is abandoned afterwards.
    /// </summary>
    /// <param name="host">A wall to stand the probe instances against - any wall will do.</param>
    /// <param name="level">The level to place them on.</param>
    /// <param name="at">A point on that wall's face.</param>
    public void Calibrate(Wall? host, Level? level, XYZ? at)
    {
        var symbols = Symbols();

        if (symbols.Count == 0)
            throw new InvalidOperationException(
                $"No family matching '{_settings.FamilyName}' is loaded in this model. " +
                Nearby());

        ResolvedFamily = SafeFamilyName(symbols[0]);

        try { Placement = symbols[0].Family.FamilyPlacementType.ToString(); }
        catch { /* keep "unknown placement" */ }

        FaceBased = Placement is "WorkPlaneBased" or "ViewBased";

        SurveyAlternatives();

        var axis = host is null ? null : WallAxis.Of(host);

        foreach (var symbol in symbols)
        {
            var measured = axis is not null && level is not null && at is not null
                ? Probe(symbol, host!, level, at, axis)
                : null;

            var size = measured ?? Declared(symbol) ?? Parsed(symbol);

            if (size is null)
            {
                _warnings.Add($"{SafeName(symbol)}: no measurable size, and neither the type " +
                              "parameters nor the type name give one. Type skipped.");
                continue;
            }

            CrossCheck(symbol, size);
            _sizes.Add(size);
        }

        if (_sizes.Count == 0)
            throw new InvalidOperationException(
                $"'{ResolvedFamily}' is loaded but not one of its {symbols.Count} type(s) has a " +
                "usable size. Nothing can be placed.");

        // Longest first, so "the biggest panel that fits" is a first match rather than a sort
        // at every window.
        _sizes.Sort((a, b) => b.Length.CompareTo(a.Length));
    }

    /// <summary>
    /// The LOWEST panel in the ladder - least tall, not shortest.
    ///
    /// Named for the axis it answers about, because the previous version was not: it returned
    /// <c>_sizes[^1]</c>, the last entry in a list sorted by LENGTH, and the only caller used
    /// it to decide whether the headroom under a sill was too small for anything in the
    /// catalogue. On a ladder where the shortest panel is not also the lowest one, that
    /// reports the wrong figure and can reject a window that had a perfectly good low panel
    /// available - a wrong answer in the direction of doing nothing, which is the hardest kind
    /// to notice.
    /// </summary>
    public RadiatorSize? Lowest() => _sizes.MinBy(s => s.Height);

    /// <summary>
    /// Lists every other radiator family in the model with its placement type.
    ///
    /// WHY THIS IS IN THE REPORT AND NOT IN A GUESS. When the configured family turns out to
    /// be wall-hosted, the only fixes are to re-author it or to use a different one - and
    /// "use a different one" is worth nothing without knowing whether any of the others can
    /// take a face. This model has four radiator families loaded. Reading the answer off a
    /// report beats another round of changing a setting and re-running to find out.
    /// </summary>
    private void SurveyAlternatives()
    {
        var families = new FilteredElementCollector(_doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
            .Cast<FamilySymbol>()
            .Select(s => s.Family)
            .Where(f => f is not null)
            .GroupBy(f => f!.Name)
            .Select(g => g.First()!)
            .Where(f => f.Name.Contains("Radiator", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name);

        foreach (var family in families)
        {
            string placement;
            try { placement = family.FamilyPlacementType.ToString(); }
            catch { placement = "unreadable"; }

            var usable = placement is "WorkPlaneBased" or "ViewBased";

            _alternatives.Add(
                $"{family.Name} ({placement})" +
                (family.Name == ResolvedFamily ? "  <- in use" : string.Empty) +
                (usable ? "  - CAN be hosted on a face" : string.Empty));
        }
    }

    // ------------------------------------------------------------------ measuring

    /// <summary>
    /// Places one instance, measures it along the wall, and leaves it for the rollback.
    /// Returns null if Revit will not place it - a face-based family on a wall it dislikes,
    /// a type that fails to activate - in which case the declared values are used instead.
    /// </summary>
    private RadiatorSize? Probe(FamilySymbol symbol, Wall host, Level level, XYZ at, WallAxis axis)
    {
        try
        {
            if (!symbol.IsActive) symbol.Activate();
            _doc.Regenerate();

            var instance = _doc.Create.NewFamilyInstance(
                at, symbol, host, level, StructuralType.NonStructural);

            if (instance is null) return null;

            _doc.Regenerate();

            var tangent = axis.TangentAt(at);
            var normal = Geometry.NormalOf(tangent);

            // Measured from the axis origin and the insertion level, so the result says where
            // the solids sit RELATIVE TO THE INSERTION POINT - not merely how big they are.
            // Sizes alone cannot answer "will it clear the sill" on a family that holds itself
            // up, nor "where will it actually land" on one whose origin is off centre, and
            // this family does both.
            var extents = Geometry.Extents(instance, tangent, normal, axis.Origin);
            if (extents is null) return null;

            var (u, v, z) = extents.Value;

            var insertionU = axis.UOf(at);

            return new RadiatorSize(symbol, u.Length, z.Hi - z.Lo, v.Length, "measured")
            {
                // Feet and pipe tails reach below the panel and are not the panel; a valve
                // sticking up still has to clear the sill. So the whole occupied band is
                // recorded, and the caller decides which edge each question is about.
                ZLo = z.Lo - at.Z,
                ZHi = z.Hi - at.Z,
                UOffset = (u.Lo + u.Hi) / 2.0 - insertionU,
            };
        }
        catch (Exception ex)
        {
            _warnings.Add($"{SafeName(symbol)}: could not be measured by placement " +
                          $"({ex.Message}); fell back to declared values.");
            return null;
        }
    }

    private RadiatorSize? Declared(FamilySymbol symbol)
    {
        var length = Number(symbol, _settings.LengthNames);
        var height = Number(symbol, _settings.HeightNames);
        var depth = Number(symbol, _settings.DepthNames);

        // ZLo/ZHi are left as a plain 0..height band and UOffset as zero, because declared
        // values say nothing about where the family holds its panel or where its origin sits.
        // That is an approximation, and it is why "type parameters" is recorded as the source:
        // a placement decided from it deserves less confidence, and the report says so.
        return length is > 0 && height is > 0
            ? new RadiatorSize(symbol, length.Value, height.Value, depth ?? 0.0, "type parameters")
                { ZHi = height.Value }
            : null;
    }

    private RadiatorSize? Parsed(FamilySymbol symbol)
    {
        var match = NameSize.Match(SafeName(symbol));
        if (!match.Success) return null;

        if (!double.TryParse(match.Groups[1].Value.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var lengthMm) ||
            !double.TryParse(match.Groups[2].Value.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var heightMm))
        {
            return null;
        }

        var depth = Number(symbol, _settings.DepthNames) ?? 0.0;

        return new RadiatorSize(
            symbol,
            Measure.FromMillimetres(lengthMm),
            Measure.FromMillimetres(heightMm),
            depth,
            "type name")
        {
            ZHi = Measure.FromMillimetres(heightMm),
        };
    }

    /// <summary>
    /// Says so, loudly and once, when the family's own numbers disagree with its geometry.
    ///
    /// This is the deliverable half of measuring. Working around a wrong parameter silently
    /// means the next tool, the next schedule and the next person all hit it again.
    /// </summary>
    private void CrossCheck(FamilySymbol symbol, RadiatorSize size)
    {
        if (size.Source != "measured") return;

        var declaredLength = Number(symbol, _settings.LengthNames);
        var declaredHeight = Number(symbol, _settings.HeightNames);

        if (declaredLength is not null &&
            Math.Abs(declaredLength.Value - size.Length) > Measure.FromMillimetres(5.0))
        {
            _warnings.Add(
                $"{SafeName(symbol)}: declared length {Measure.ToMillimetres(declaredLength.Value):0} mm " +
                $"but measures {Measure.ToMillimetres(size.Length):0} mm. The measured value was used. " +
                "Fix the family - a schedule reading that parameter is reporting the wrong length.");
        }

        if (declaredHeight is not null &&
            Math.Abs(declaredHeight.Value - size.Height) > Measure.FromMillimetres(5.0))
        {
            _warnings.Add(
                $"{SafeName(symbol)}: declared height {Measure.ToMillimetres(declaredHeight.Value):0} mm " +
                $"but measures {Measure.ToMillimetres(size.Height):0} mm. The measured value was used.");
        }

        var parsed = Parsed(symbol);
        if (parsed is not null && Math.Abs(parsed.Length - size.Length) > Measure.FromMillimetres(5.0))
        {
            _warnings.Add(
                $"{SafeName(symbol)}: the type NAME says {Measure.ToMillimetres(parsed.Length):0} mm long " +
                $"but it measures {Measure.ToMillimetres(size.Length):0} mm. The name is what a " +
                "designer picks the type by, so this one misleads at selection time.");
        }
    }

    // ------------------------------------------------------------------ lookup

    private List<FamilySymbol> Symbols()
    {
        var all = new FilteredElementCollector(_doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
            .Cast<FamilySymbol>()
            .ToList();

        var target = ParameterHelper.Normalize(_settings.FamilyName);

        var exact = all
            .Where(s => ParameterHelper.Normalize(SafeFamilyName(s)) == target)
            .ToList();

        if (exact.Count > 0) return exact;

        // Substring, so a family renamed "Radiator_P No Void V7" on the next revision is
        // still found rather than failing the run over a version digit.
        return all
            .Where(s => ParameterHelper.Normalize(SafeFamilyName(s)).Contains(target, StringComparison.Ordinal))
            .ToList();
    }

    private string Nearby()
    {
        var near = new FilteredElementCollector(_doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
            .Cast<FamilySymbol>()
            .Select(SafeFamilyName)
            .Where(n => n.Length > 0)
            .Distinct()
            .Take(12)
            .ToList();

        return near.Count > 0
            ? "Mechanical Equipment families that ARE loaded: " + string.Join(", ", near)
            : "No Mechanical Equipment family is loaded at all - load the radiator family first.";
    }

    private static double? Number(Element element, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var parameter = ParameterHelper.Find(element, name);
            if (parameter is null || !parameter.HasValue) continue;
            if (parameter.StorageType != StorageType.Double) continue;

            var value = parameter.AsDouble();
            if (value > 0) return value;
        }

        return null;
    }

    private static string SafeName(Element element)
    {
        try { return element.Name; }
        catch { return "?"; }
    }

    private static string SafeFamilyName(FamilySymbol symbol)
    {
        try { return symbol.Family?.Name ?? string.Empty; }
        catch { return string.Empty; }
    }
}
