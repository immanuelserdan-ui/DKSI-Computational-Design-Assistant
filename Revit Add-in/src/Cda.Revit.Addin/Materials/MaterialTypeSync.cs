using Autodesk.Revit.DB;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Materials;

public sealed class MaterialSyncResult
{
    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Unresolved { get; init; }
    public required IReadOnlyList<string> Summary { get; init; }
    public required IReadOnlyDictionary<string, int> Counts { get; init; }
}

/// <summary>
/// Port of MaterialToTypeParams.py.
///
///     Material "Manufacturer" -> type "FK Kode"        + instance "FK Kode Instance"
///     Material "Comments"     -> type "FM Bygningsdel" + instance "FM Bygningsdel Instance"
///
/// Runs across every model category. The type parameters are what Edit Type shows; the
/// instance parameters are what the Properties palette shows. Revit cannot flow a type
/// parameter into a differently-named instance parameter, so both are written.
///
/// The caller owns the transaction: run inside <see cref="Transactions.Run"/> when
/// applying, and outside it when previewing.
/// </summary>
public sealed class MaterialTypeSync
{
    private readonly Document _doc;
    private readonly MaterialSyncSettings _settings;
    private readonly MaterialClassificationReader _materials;

    private readonly HashSet<long> _excludedIds = [];
    private readonly HashSet<string> _excludedNames;
    private readonly HashSet<string>? _restrict;

    private readonly List<IReadOnlyList<string>> _rows = [];
    private readonly List<IReadOnlyList<string>> _unresolved = [];

    private readonly Dictionary<string, int> _counts = new()
    {
        ["types scanned"] = 0,
        ["types written"] = 0,
        ["already correct"] = 0,
        ["instances written"] = 0,
        ["missing param"] = 0,
        ["no source value"] = 0,
        ["no material"] = 0,
        ["errors"] = 0,
        ["materials rejected"] = 0,
    };

    private bool _apply;

    public MaterialTypeSync(Document doc, MaterialSyncSettings settings)
    {
        _doc = doc;
        _settings = settings;
        _materials = new MaterialClassificationReader(doc, settings);

        _excludedNames = [.. settings.ExcludedCategoryNames.Select(ParameterHelper.Normalize)];
        _restrict = settings.CategoryNames is { Count: > 0 }
            ? [.. settings.CategoryNames.Select(ParameterHelper.Normalize)]
            : null;

        foreach (var bic in settings.ExcludedCategories)
        {
            try
            {
                var category = doc.Settings.Categories.get_Item(bic);
                if (category is not null) _excludedIds.Add(category.Id.Value);
            }
            catch
            {
                // Category not present in this document's discipline. Nothing to exclude.
            }
        }
    }

    public MaterialSyncResult Run(bool apply)
    {
        _apply = apply;
        var started = DateTime.Now;

        // Index every model instance by its type - one pass over the document, instead of
        // a collector query per type.
        var instancesByType = new Dictionary<long, List<Element>>();
        foreach (var instance in new FilteredElementCollector(_doc).WhereElementIsNotElementType())
        {
            if (!WantedCategory(instance)) continue;

            ElementId typeId;
            try { typeId = instance.GetTypeId(); }
            catch { continue; }

            if (typeId == ElementId.InvalidElementId) continue;

            if (!instancesByType.TryGetValue(typeId.Value, out var list))
                instancesByType[typeId.Value] = list = [];

            list.Add(instance);
        }

        foreach (var elementType in new FilteredElementCollector(_doc).WhereElementIsElementType().OfType<ElementType>())
        {
            if (!WantedCategory(elementType)) continue;

            var instances = instancesByType.GetValueOrDefault(elementType.Id.Value) ?? [];
            ProcessType(elementType, instances);
        }

        if (_unresolved.Count >= _settings.MaxUnresolved)
            _unresolved.Add(new[] { "...", $"truncated at {_settings.MaxUnresolved}", string.Empty });

        return new MaterialSyncResult
        {
            Rows = BuildRows(),
            Unresolved = _unresolved,
            Summary = BuildSummary(started),
            Counts = _counts,
        };
    }

    // ------------------------------------------------------------------- filtering

    private bool WantedCategory(Element element)
    {
        Category? category;
        try { category = element.Category; }
        catch { return false; }

        if (category is null || category.CategoryType != CategoryType.Model) return false;
        if (_excludedIds.Contains(category.Id.Value)) return false;

        var name = ParameterHelper.Normalize(category.Name);
        if (_excludedNames.Contains(name)) return false;
        if (_restrict is not null && !_restrict.Contains(name)) return false;

        return true;
    }

    private bool IsValidMaterialId(ElementId? id) =>
        id is not null && id != ElementId.InvalidElementId && _doc.GetElement(id) is Material;

    // ------------------------------------------------------- material determination

    /// <summary>Walls, floors, roofs, ceilings: a layer material chosen by the rule.</summary>
    private (ElementId? Id, string? How, List<string> LayerNames) FromCompoundStructure(ElementType type)
    {
        CompoundStructure? structure;
        try { structure = (type as HostObjAttributes)?.GetCompoundStructure(); }
        catch { return (null, null, []); }

        if (structure is null) return (null, null, []);

        var layers = structure.GetLayers();
        var usable = layers
            .Select((layer, index) => (Index: index, Layer: layer))
            .Where(x => IsValidMaterialId(x.Layer.MaterialId))
            .ToList();

        if (usable.Count == 0) return (null, null, []);

        var names = usable
            .Select(x => (_doc.GetElement(x.Layer.MaterialId) as Material)?.Name ?? "?")
            .ToList();

        // The classification lives on the material, not on a geometric position.
        if (_settings.LayerRule == LayerRule.Classified)
        {
            var tagged = usable.Where(x => _materials.HasData(x.Layer.MaterialId)).ToList();
            if (tagged.Count > 0)
            {
                var best = tagged.MaxBy(x => x.Layer.Width);
                return (best.Layer.MaterialId,
                        $"classified layer {best.Index + 1} of {layers.Count}",
                        names);
            }
            // Nothing classified: fall through to the geometric rules so the report still
            // names a material rather than going blank.
        }

        switch (_settings.LayerRule)
        {
            case LayerRule.Exterior:
                return (usable[0].Layer.MaterialId, "layer 1 (exterior/top)", names);

            case LayerRule.Interior:
                return (usable[^1].Layer.MaterialId, "last layer (interior/bottom)", names);

            case LayerRule.Structural:
            case LayerRule.Classified:
                var index = structure.StructuralMaterialIndex;
                if (index >= 0 && index < layers.Count && IsValidMaterialId(layers[index].MaterialId))
                    return (layers[index].MaterialId, "structural layer", names);
                break;
        }

        var thickest = usable.MaxBy(x => x.Layer.Width);
        return (thickest.Layer.MaterialId, "thickest layer", names);
    }

    /// <summary>
    /// Any element or type: the first parameter whose value is a Material. Covers
    /// loadable family types and instance-level material overrides alike.
    /// </summary>
    private (ElementId? Id, string? How) FromElementParameters(Element element)
    {
        var candidates = new List<(int Rank, string Name, ElementId Id)>();

        try
        {
            foreach (var parameter in element.Parameters.Cast<Parameter>())
            {
                try
                {
                    if (parameter.StorageType != StorageType.ElementId) continue;

                    var id = parameter.AsElementId();
                    if (!IsValidMaterialId(id)) continue;

                    var name = parameter.Definition.Name;
                    var lower = name.ToLowerInvariant();
                    var rank = lower.Contains("structural") || lower.Contains("konstruktion") ? 0 : 1;

                    candidates.Add((rank, name, id));
                }
                catch
                {
                    // Unreadable parameter; try the next one.
                }
            }
        }
        catch
        {
            return (null, null);
        }

        if (candidates.Count == 0) return (null, null);

        // Same principle as the layer rule: a material carrying classification data
        // outranks one that does not, whatever the parameter happens to be called.
        if (_settings.LayerRule == LayerRule.Classified)
        {
            var tagged = candidates.Where(c => _materials.HasData(c.Id)).ToList();
            if (tagged.Count > 0) candidates = tagged;
        }

        var best = candidates.OrderBy(c => c.Rank).First();
        return (best.Id, $"parameter '{best.Name}'");
    }

    /// <summary>Last resort: the dominant material by volume on a placed instance.</summary>
    private (ElementId? Id, string? How) FromInstances(IReadOnlyList<Element> instances)
    {
        foreach (var instance in instances.Take(5))
        {
            ICollection<ElementId> ids;
            try { ids = instance.GetMaterialIds(false); }
            catch { continue; }

            var scored = new List<(double Quantity, ElementId Id)>();

            foreach (var id in ids)
            {
                if (!IsValidMaterialId(id)) continue;

                double quantity;
                try { quantity = instance.GetMaterialVolume(id); }
                catch { quantity = 0.0; }

                if (quantity == 0.0)
                {
                    try { quantity = instance.GetMaterialArea(id, false); }
                    catch { quantity = 0.0; }
                }

                scored.Add((quantity, id));
            }

            if (scored.Count > 0)
            {
                var best = scored.OrderByDescending(s => s.Quantity).First();
                return (best.Id, "dominant material on instance");
            }
        }

        return (null, null);
    }

    private (ElementId? Id, string? How, List<string> LayerNames) ResolveTypeMaterial(
        ElementType type, IReadOnlyList<Element> instances)
    {
        if (type is HostObjAttributes)
        {
            var (id, how, names) = FromCompoundStructure(type);
            if (id is not null) return (id, how, names);
        }

        var (paramId, paramHow) = FromElementParameters(type);
        if (paramId is not null) return (paramId, paramHow, []);

        var (instId, instHow) = FromInstances(instances);
        return (instId, instHow, []);
    }

    // ---------------------------------------------------------------------- writing

    private string WriteParameter(Element element, string name, string value)
    {
        var parameter = ParameterHelper.Find(element, name);
        if (parameter is null) return "missing";
        if (parameter.IsReadOnly) return "read-only";
        if (parameter.StorageType != StorageType.String) return $"wrong type ({parameter.StorageType})";

        var current = parameter.AsString() ?? string.Empty;
        if (current.Trim() == value) return "same";
        if (current.Trim().Length > 0 && !_settings.Overwrite) return "locked";

        try
        {
            if (parameter.Set(value)) return "written";
        }
        catch (Exception ex)
        {
            try
            {
                if (parameter.SetValueString(value)) return "written";
            }
            catch
            {
                // Fall through to the failure below.
            }

            return $"FAILED: {ex.Message}";
        }

        return "FAILED: Set() returned false";
    }

    // ------------------------------------------------------------------------- main

    private void ProcessType(ElementType type, IReadOnlyList<Element> instances)
    {
        _counts["types scanned"]++;

        string typeName;
        try { typeName = type.Name; }
        catch { typeName = $"<unnamed {type.Id.Value}>"; }

        string categoryName;
        try { categoryName = type.Category?.Name ?? "?"; }
        catch { categoryName = "?"; }

        try
        {
            var (materialId, how, layerNames) = ResolveTypeMaterial(type, instances);

            if (materialId is null)
            {
                _counts["no material"]++;
                if (_unresolved.Count < _settings.MaxUnresolved)
                    _unresolved.Add(new[] { typeName, categoryName, "no material resolved" });
                return;
            }

            var material = _materials.Read(materialId);
            var rowValues = _settings.Mapping
                .Select(m => material.Values.GetValueOrDefault(m.Target) ?? string.Empty)
                .ToList();

            if (!material.HasData)
            {
                _counts["no source value"]++;
                if (material.Note.Length > 0) _counts["materials rejected"]++;

                if (_settings.ReportAll)
                {
                    // Naming every material in the assembly is what tells you whether a
                    // classified material exists on this type at all.
                    var detail = $"resolved '{material.MaterialName}' via {how}";
                    if (material.Note.Length > 0) detail += $" [{material.Note}]";

                    detail += layerNames.Count > 0
                        ? $"; layer materials: {string.Join(", ", layerNames)} -- NONE of them carry Comments/Manufacturer"
                        : " -- material carries no Comments/Manufacturer";

                    _rows.Add(new[] { typeName, categoryName, material.MaterialName }
                        .Concat(rowValues).Append(detail).ToList());
                }

                return;
            }

            var statuses = new List<string>();
            if (!_apply) statuses.Add($"PREVIEW ({how})");
            if (material.Note.Length > 0) statuses.Add(material.Note);
            var noteworthy = false;

            // ---- type parameters (Edit Type) --------------------------------------
            foreach (var mapping in _settings.Mapping)
            {
                var value = material.Values.GetValueOrDefault(mapping.Target);
                if (string.IsNullOrEmpty(value)) continue;

                string status;
                if (_apply)
                {
                    status = WriteParameter(type, mapping.Target, value);
                    switch (status)
                    {
                        case "written": _counts["types written"]++; noteworthy = true; break;
                        case "same": _counts["already correct"]++; break;
                        case "missing": _counts["missing param"]++; noteworthy = true; break;
                        case "read-only": _counts["errors"]++; noteworthy = true; break;
                        default:
                            if (status.StartsWith("FAILED") || status.StartsWith("wrong type"))
                            {
                                _counts["errors"]++;
                                noteworthy = true;
                            }
                            break;
                    }
                }
                else
                {
                    status = "preview";
                }

                statuses.Add($"{mapping.Target}: {status}");
            }

            // ---- instance parameters (Properties palette) -------------------------
            if (_settings.WriteInstances)
                WriteInstanceParameters(type, instances, materialId, statuses, ref noteworthy);

            if (_settings.ReportAll || noteworthy || !_apply)
            {
                _rows.Add(new[] { typeName, categoryName, material.MaterialName }
                    .Concat(rowValues).Append(string.Join(" | ", statuses)).ToList());
            }
        }
        catch (Exception ex)
        {
            _counts["errors"]++;
            _rows.Add(new[] { typeName, categoryName, "?", string.Empty, string.Empty, $"ERROR: {ex.Message}" });
        }
    }

    private void WriteInstanceParameters(
        ElementType type,
        IReadOnlyList<Element> instances,
        ElementId typeMaterialId,
        List<string> statuses,
        ref bool noteworthy)
    {
        if (instances.Count == 0)
        {
            statuses.Add("no instances placed");
            return;
        }

        if (!_apply)
        {
            foreach (var mapping in _settings.Mapping)
            {
                var probe = ParameterHelper.Find(instances[0], mapping.InstanceTarget);
                statuses.Add($"{mapping.InstanceTarget}: " +
                             (probe is not null ? $"bound, {instances.Count} inst" : "NOT BOUND"));
            }
            return;
        }

        var done = new Dictionary<string, int>(StringComparer.Ordinal);
        var absent = new Dictionary<string, int>(StringComparer.Ordinal);
        var failed = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var instance in instances)
        {
            // An instance may carry its own material override.
            var instanceMaterialId = typeMaterialId;
            if (_settings.PerInstanceMaterial)
            {
                var (overrideId, _) = FromElementParameters(instance);
                if (overrideId is not null) instanceMaterialId = overrideId;
            }

            var values = _materials.Read(instanceMaterialId);

            foreach (var mapping in _settings.Mapping)
            {
                var value = values.Values.GetValueOrDefault(mapping.Target);
                if (string.IsNullOrEmpty(value)) continue;

                var status = WriteParameter(instance, mapping.InstanceTarget, value);

                if (status == "written")
                {
                    done[mapping.InstanceTarget] = done.GetValueOrDefault(mapping.InstanceTarget) + 1;
                    _counts["instances written"]++;
                }
                else if (status == "missing")
                {
                    absent[mapping.InstanceTarget] = absent.GetValueOrDefault(mapping.InstanceTarget) + 1;
                }
                else if (status.StartsWith("FAILED") || status == "read-only" || status.StartsWith("wrong type"))
                {
                    _counts["errors"]++;
                    failed.TryAdd(mapping.InstanceTarget, status);
                }
            }
        }

        foreach (var mapping in _settings.Mapping)
        {
            var target = mapping.InstanceTarget;

            if (absent.GetValueOrDefault(target) > 0)
            {
                _counts["missing param"]++;
                statuses.Add($"{target}: NOT BOUND");
                noteworthy = true;
            }
            else if (failed.TryGetValue(target, out var reason))
            {
                statuses.Add($"{target}: {done.GetValueOrDefault(target)}/{instances.Count} inst, {reason}");
                noteworthy = true;
            }
            else if (done.GetValueOrDefault(target) > 0)
            {
                statuses.Add($"{target}: {done[target]}/{instances.Count} inst");
                noteworthy = true;
            }
        }
    }

    // ----------------------------------------------------------------------- output

    private List<IReadOnlyList<string>> BuildRows()
    {
        var header = new List<string> { "Type", "Category", "Material" };
        header.AddRange(_settings.Mapping.Select(m => m.Target));
        header.Add("Status");

        var rows = new List<IReadOnlyList<string>> { header };
        rows.AddRange(_rows);
        return rows;
    }

    private List<string> BuildSummary(DateTime started)
    {
        // Which materials actually drive the run. If this list is not what you expect,
        // nothing else in the report matters.
        var accepted = new List<string>();
        var rejected = new List<string>();

        var codeTarget = _settings.Mapping[0].Target;
        var nameTarget = _settings.Mapping[1].Target;

        foreach (var material in _materials.Seen)
        {
            if (material.HasData)
            {
                accepted.Add($"  {material.MaterialName}  ->  " +
                             $"{codeTarget}='{material.Values.GetValueOrDefault(codeTarget)}'  " +
                             $"{nameTarget}='{material.Values.GetValueOrDefault(nameTarget)}'" +
                             (material.Note.Length > 0 ? $"   [{material.Note}]" : string.Empty));
            }
            else if (material.Note.Length > 0)
            {
                rejected.Add($"  {material.MaterialName}  [{material.Note}]");
            }
        }

        var summary = new List<string>
        {
            $"APPLY={_apply}  rule={_settings.LayerRule}  instances={_settings.WriteInstances}  " +
            $"overwrite={_settings.Overwrite}",

            $"scope: {(_restrict is null ? "ALL model categories" : string.Join(", ", _restrict.Order()))}",
            $"code pattern: {(_settings.CodePattern.Length > 0 ? _settings.CodePattern : "(none - accepts anything)")}",
            $"distinct materials read: {_materials.Seen.Count()}",
            $"elapsed: {(DateTime.Now - started).TotalSeconds:0.0} s",
            string.Empty,
            $"CLASSIFIED MATERIALS ({accepted.Count}) - these drive every write:",
        };

        summary.AddRange(accepted.Count > 0 ? accepted : ["  (none)"]);
        summary.Add(string.Empty);
        summary.Add($"REJECTED MATERIALS ({rejected.Count}) - had text, but no FK code:");
        summary.AddRange(rejected.Count > 0 ? rejected.Take(40) : ["  (none)"]);
        summary.Add(string.Empty);
        summary.Add("COUNTS:");
        summary.AddRange(_counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"  {c.Key}: {c.Value}"));

        return summary;
    }
}
