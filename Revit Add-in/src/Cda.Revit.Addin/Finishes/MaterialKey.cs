using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Finishes;

/// <summary>
/// Identifies the material a measured area belongs to, plus whether that face was painted.
///
/// Document.IsPainted positively identifies painted faces (Split Face regions the user
/// painted), whose MaterialElementId then reports the PAINT material. Unpainted faces
/// report their wall-type LAYER material instead ("EM Wall", "Default Wall") - the same
/// material Revit shows in a Material Takeoff, and the same one Revit's paint takeoff
/// EXCLUDES when filtered on 'Material: As Paint = Yes'.
///
/// Both resolve to a MaterialElementId, so the id alone cannot tell paint from substrate.
/// The flag is what makes the two reconcilable: painted rows are the paint cost basis,
/// unpainted rows are substrate that must not be paint-costed (or are finishes missing
/// from the model - a modelling flag).
/// </summary>
public readonly record struct MaterialKey(long MaterialId, string Label, bool Painted)
{
    /// <summary>Sentinel for a key that carries a descriptive label instead of a material.</summary>
    private const long NoMaterial = long.MinValue;

    public static MaterialKey Of(ElementId id, bool painted) => new(id.Value, string.Empty, painted);

    public static MaterialKey Named(string label, bool painted) => new(NoMaterial, label, painted);

    /// <summary>
    /// Area that belongs to no identifiable face material. Never painted by definition:
    /// arithmetic fallback area is not measured off a face at all.
    /// </summary>
    public static readonly MaterialKey Fallback = Named("(fallback)", false);

    public bool HasMaterial => MaterialId != NoMaterial;

    /// <summary>(name, code, isPainted). Code is the first non-empty of the Material's
    /// 'Code', 'Mark' or 'Keynote' parameter - office codes like VBJ / VBF.</summary>
    public (string Name, string Code, bool Painted) Describe(Document doc)
    {
        if (!HasMaterial) return (Label, string.Empty, Painted);

        try
        {
            if (doc.GetElement(new ElementId(MaterialId)) is not Material material)
                return ("(no material)", string.Empty, Painted);

            var code = string.Empty;
            foreach (var name in new[] { "Code", "Mark", "Keynote" })
            {
                var parameter = material.LookupParameter(name);
                if (parameter is null || !parameter.HasValue) continue;

                var value = parameter.AsString();
                if (!string.IsNullOrEmpty(value))
                {
                    code = value;
                    break;
                }
            }

            return (material.Name, code, Painted);
        }
        catch
        {
            return ("(no material)", string.Empty, Painted);
        }
    }
}

/// <summary>Accumulates area per material key.</summary>
public sealed class MaterialLedger
{
    private readonly Dictionary<MaterialKey, double> _areas = [];

    public IReadOnlyDictionary<MaterialKey, double> Areas => _areas;

    public void Add(MaterialKey key, double area)
    {
        if (area <= 0) return;
        _areas[key] = _areas.GetValueOrDefault(key) + area;
    }

    public void AddRange(MaterialLedger other)
    {
        foreach (var (key, area) in other._areas) Add(key, area);
        DroppedRegions += other.DroppedRegions;
    }

    /// <summary>
    /// Split-face regions that could neither be clipped nor shown to lie wholly inside the
    /// room, and so contributed no area.
    ///
    /// Counted rather than thrown because the alternative was worse: the exact clip used to
    /// abandon the whole element on the first failure, and a split-face wall runs one boolean
    /// PER REGION - so the more paint colours a wall carried, the likelier it was to lose all
    /// of them and be reported as unpainted. A named shortfall beats a silent zero.
    /// </summary>
    public int DroppedRegions { get; private set; }

    /// <summary>Records one region that could not be measured.</summary>
    public void Drop() => DroppedRegions++;

    public double Total => _areas.Values.Sum();

    /// <summary>
    /// Sum of the PAINTED areas only. Unpainted layer/substrate faces (EM Wall,
    /// Default Wall, fallback) are excluded, so this is the paint cost basis.
    /// </summary>
    public double PaintedTotal => _areas.Where(a => a.Key.Painted).Sum(a => a.Value);

    /// <summary>
    /// How many DISTINCT painted materials this face set carries. More than one means a
    /// wall with two paint colours on one face (split-face); a single per-element paint
    /// value would repeat on both paint rows of a takeoff and double-count.
    /// </summary>
    public int DistinctPaintedMaterials => _areas
        .Where(a => a.Key.Painted && a.Value > 0.005)
        .Select(a => a.Key.MaterialId)
        .Distinct()
        .Count();
}
