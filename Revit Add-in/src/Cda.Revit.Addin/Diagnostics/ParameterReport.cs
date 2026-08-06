using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Diagnostics;

/// <summary>
/// Port of DiagnoseParams.py.
///
/// Read-only. Answers the question "why did the sync script report missing / read-only /
/// write nothing?" by dumping what Revit actually exposes, rather than what the UI
/// suggests it exposes.
///
/// No Revit UI types are referenced here on purpose: the report is a string, so this
/// class can be exercised from a test or from a batch job without a running Revit UI.
/// </summary>
public sealed class ParameterReport
{
    /// <summary>The parameters the material-sync tools try to write.</summary>
    private static readonly string[] Targets =
    [
        "FK Kode",
        "FM Bygningsdel",
        "FK Kode Instance",
        "FM Bygningsdel Instance",
    ];

    private readonly Document _doc;

    public ParameterReport(Document doc) => _doc = doc;

    public string Build(string materialName, string typeName)
    {
        var material = FindMaterial(materialName);
        var elementType = FindType(typeName);
        var instance = FindInstance(elementType);

        var sb = new StringBuilder();

        sb.AppendLine("Parameter diagnostics");
        sb.AppendLine($"Document : {_doc.Title}");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Searched : material ~ '{materialName}'   type ~ '{typeName}'");
        sb.AppendLine();

        AppendNotes(sb, material, elementType, instance, materialName, typeName);
        AppendAssembly(sb, elementType);
        AppendDump(sb, material, "MATERIAL");
        AppendDump(sb, elementType, "TYPE");
        AppendDump(sb, instance, "INSTANCE");

        return sb.ToString();
    }

    // ------------------------------------------------------------------ find things

    private Material? FindMaterial(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : new FilteredElementCollector(_doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .FirstOrDefault(m => m.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    private ElementType? FindType(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : new FilteredElementCollector(_doc)
                .WhereElementIsElementType()
                .OfType<ElementType>()
                .FirstOrDefault(t => SafeName(t).Contains(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// First placed instance of the type. This walks non-element-type elements and stops
    /// at the first hit; a diagnostic runs once, so the simple form is preferred over an
    /// ElementParameterFilter on ELEM_TYPE_PARAM.
    /// </summary>
    private Element? FindInstance(ElementType? type) =>
        type is null
            ? null
            : new FilteredElementCollector(_doc)
                .WhereElementIsNotElementType()
                .FirstOrDefault(e => e.GetTypeId() == type.Id);

    // ------------------------------------------------------------------- the test

    private void AppendNotes(
        StringBuilder sb,
        Material? material,
        ElementType? type,
        Element? instance,
        string materialName,
        string typeName)
    {
        sb.AppendLine("=== NOTES ===");
        sb.AppendLine($"material : {(material is not null ? material.Name : $"NOT FOUND ({materialName})")}");
        sb.AppendLine($"type     : {(type is not null ? SafeName(type) : $"NOT FOUND ({typeName})")}");
        sb.AppendLine($"instance : {(instance is not null ? instance.Id.Value.ToString(CultureInfo.InvariantCulture) : "none placed")}");
        sb.AppendLine();

        foreach (var target in Targets)
        {
            foreach (var (holder, label) in new[] { ((Element?)type, "TYPE"), (instance, "INST") })
            {
                if (holder is null) continue;

                var parameter = holder.LookupParameter(target);

                if (parameter is not null)
                {
                    sb.AppendLine(
                        $"{label}  {target,-26} found, storage={parameter.StorageType}, " +
                        $"readonly={parameter.IsReadOnly}, value='{parameter.AsString()}'");
                    continue;
                }

                // LookupParameter matches the display name exactly. A parameter whose name
                // carries a trailing or doubled space is invisible to it but looks correct
                // in the UI - that is the single most common cause of a false "missing".
                // Element.GetParameters(name) looks up by name; Element.Parameters is the
                // full set. ParameterSet is non-generic, hence the Cast.
                var near = holder.Parameters
                    .Cast<Parameter>()
                    .Select(p => p.Definition.Name)
                    .Where(n => Normalize(n) == Normalize(target))
                    .Select(n => $"'{n}'")
                    .ToList();

                sb.AppendLine(
                    $"{label}  {target,-26} LookupParameter -> None" +
                    (near.Count > 0
                        ? $"  BUT normalised match found: {string.Join(", ", near)}  <== WHITESPACE"
                        : string.Empty));
            }
        }

        if (_doc.IsWorkshared)
        {
            sb.AppendLine();
            sb.AppendLine("MODEL IS WORKSHARED - a type owned by another user cannot be written.");
        }

        sb.AppendLine();
    }

    // ------------------------------------------- what is actually IN the type

    private void AppendAssembly(StringBuilder sb, ElementType? type)
    {
        sb.AppendLine($"=== ASSEMBLY OF '{(type is not null ? SafeName(type) : "?")}' ===");
        sb.AppendLine("(read this section first)");

        if (type is HostObjAttributes hostType)
        {
            CompoundStructure? structure = null;
            try { structure = hostType.GetCompoundStructure(); }
            catch { /* some host types legitimately have none */ }

            if (structure is null)
            {
                sb.AppendLine("no compound structure");
                sb.AppendLine();
                return;
            }

            var layers = structure.GetLayers();
            sb.AppendLine($"StructuralMaterialIndex = {structure.StructuralMaterialIndex}  " +
                          "(the layer a 'structural' rule would pick)");

            var classified = 0;
            for (var i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                var layerMaterial = _doc.GetElement(layer.MaterialId) as Material;
                var (info, hasData) = Identity(layerMaterial);
                if (hasData) classified++;

                var mm = UnitUtils.ConvertFromInternalUnits(layer.Width, UnitTypeId.Millimeters);

                sb.AppendLine(
                    $"layer {i + 1}: {mm,7:0.#} mm  function={layer.Function,-12} " +
                    $"material='{layerMaterial?.Name}'  {info}" +
                    (hasData ? "   <== CLASSIFIED" : string.Empty));
            }

            sb.AppendLine();
            sb.AppendLine($"layers whose material carries classification data: {classified} of {layers.Count}");

            if (classified == 0)
            {
                sb.AppendLine("=> NOTHING to copy. The classified material is not in this type's " +
                              "assembly - assign it, or fill in the material that is.");
            }
            else if (classified > 0 && structure.StructuralMaterialIndex >= 0)
            {
                // The classified material is typically a thin finish layer, not the
                // structural core, so a rule that reads StructuralMaterialIndex misses it.
                var structuralLayer = structure.StructuralMaterialIndex;
                var structuralMaterial = structuralLayer < layers.Count
                    ? _doc.GetElement(layers[structuralLayer].MaterialId) as Material
                    : null;
                var (_, structuralHasData) = Identity(structuralMaterial);

                if (!structuralHasData)
                {
                    sb.AppendLine("=> WARNING: the structural layer carries no classification data. " +
                                  "A rule that reads StructuralMaterialIndex will find nothing here.");
                }
            }
        }
        else if (type is not null)
        {
            var found = false;
            foreach (var p in type.Parameters.Cast<Parameter>()
                         .Where(p => p.StorageType == StorageType.ElementId))
            {
                if (_doc.GetElement(p.AsElementId()) is not Material m) continue;
                var (info, hasData) = Identity(m);
                sb.AppendLine($"type parameter '{p.Definition.Name}' -> material '{m.Name}'  {info}" +
                              (hasData ? "   <== CLASSIFIED" : string.Empty));
                found = true;
            }

            if (!found)
                sb.AppendLine("no compound structure and no material-valued type parameter");
        }

        sb.AppendLine();
    }

    /// <summary>Readable Comments/Manufacturer text, plus whether either carries data.</summary>
    private static (string Text, bool HasData) Identity(Material? material)
    {
        if (material is null) return ("(no material)", false);

        var parts = new List<string>();
        var hasData = false;

        foreach (var (bip, label) in new[]
                 {
                     (BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "Comments"),
                     (BuiltInParameter.ALL_MODEL_MANUFACTURER, "Manufacturer"),
                 })
        {
            string? value = null;
            try { value = material.get_Parameter(bip)?.AsString(); }
            catch { /* parameter not applicable to this material */ }

            if (!string.IsNullOrWhiteSpace(value)) hasData = true;
            parts.Add($"{label}='{value}'");
        }

        return (string.Join("  ", parts), hasData);
    }

    // ---------------------------------------------------------------- raw dumps

    private void AppendDump(StringBuilder sb, Element? element, string label)
    {
        sb.AppendLine($"=== {label} ===");

        if (element is null)
        {
            sb.AppendLine("NOT FOUND");
            sb.AppendLine();
            return;
        }

        var rows = new List<string>();
        try
        {
            foreach (var p in element.Parameters.Cast<Parameter>())
                rows.Add(Describe(p));
        }
        catch (Exception ex)
        {
            rows.Add($"could not enumerate: {ex.Message}");
        }

        rows.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows) sb.AppendLine(row);
        sb.AppendLine();
    }

    private string Describe(Parameter p)
    {
        string name;
        try { name = p.Definition.Name; }
        catch { name = "<no definition>"; }

        string storage;
        try { storage = p.StorageType.ToString(); }
        catch { storage = "?"; }

        var value = storage switch
        {
            "String" => $"'{p.AsString()}'",
            "Integer" => p.AsInteger().ToString(CultureInfo.InvariantCulture),
            "Double" => p.AsDouble().ToString("0.######", CultureInfo.InvariantCulture),
            "ElementId" => _doc.GetElement(p.AsElementId()) is { } e
                ? $"'{e.Name}'"
                : p.AsElementId().Value.ToString(CultureInfo.InvariantCulture),
            _ => "(none)",
        };

        var shared = string.Empty;
        try { if (p.IsShared) shared = $"  shared:{p.GUID}"; }
        catch { /* not all parameters expose IsShared */ }

        // Quoting the name is what makes trailing and doubled spaces visible.
        var spacing = name != Normalize(name) ? "   <== NON-STANDARD SPACING" : string.Empty;

        return $"'{name}',{new string(' ', Math.Max(1, 36 - name.Length))}{storage,-9} " +
               $"ro={p.IsReadOnly,-5} value={value}{shared}{spacing}";
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Collapses runs of whitespace and trims, matching the Python normaliser.</summary>
    private static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string SafeName(Element element)
    {
        try { return element.Name; }
        catch { return "<name unavailable>"; }
    }
}
