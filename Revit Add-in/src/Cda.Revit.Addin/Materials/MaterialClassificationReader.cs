using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Materials;

/// <summary>What one material's identity fields resolve to, after validation.</summary>
public sealed class MaterialValues
{
    public required string MaterialName { get; init; }

    /// <summary>Target parameter name -> value, or null when the material was rejected.</summary>
    public required IReadOnlyDictionary<string, string?> Values { get; init; }

    /// <summary>"SWAPPED (corrected)", "rejected: no FK code", or empty.</summary>
    public required string Note { get; init; }

    public bool HasData => Values.Values.Any(v => !string.IsNullOrEmpty(v));
}

/// <summary>
/// Reads and validates the classification data on a material.
///
/// Cached: a whole-model run hits the same few hundred materials many thousands of times.
/// </summary>
public sealed class MaterialClassificationReader
{
    private readonly Document _doc;
    private readonly MaterialSyncSettings _settings;
    private readonly Regex? _codePattern;
    private readonly HashSet<string> _ignore;
    private readonly Dictionary<long, MaterialValues> _cache = [];

    public MaterialClassificationReader(Document doc, MaterialSyncSettings settings)
    {
        _doc = doc;
        _settings = settings;
        _codePattern = string.IsNullOrEmpty(settings.CodePattern) ? null : new Regex(settings.CodePattern);
        _ignore = [.. settings.IgnoreValues.Select(ParameterHelper.Normalize)];
    }

    /// <summary>Every material actually read, for the summary block.</summary>
    public IEnumerable<MaterialValues> Seen => _cache.Values;

    public MaterialValues Read(ElementId materialId)
    {
        var key = materialId.Value;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var material = _doc.GetElement(materialId) as Material;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var mapping in _settings.Mapping)
            values[mapping.Target] = ReadValue(material, mapping);

        var note = Validate(values);

        var result = new MaterialValues
        {
            MaterialName = material?.Name ?? "(missing material)",
            Values = values,
            Note = note,
        };

        _cache[key] = result;
        return result;
    }

    public bool HasData(ElementId materialId)
    {
        try { return Read(materialId).HasData; }
        catch { return false; }
    }

    /// <summary>
    /// BuiltInParameter first: LookupParameter("Comments") on a Material is unreliable
    /// and often returns the wrong parameter or none at all.
    /// </summary>
    private string? ReadValue(Material? material, MaterialMapping mapping)
    {
        if (material is null) return null;

        Parameter? parameter = null;
        try { parameter = material.get_Parameter(mapping.SourceBip); }
        catch { /* not applicable to this material */ }

        if (parameter is null)
        {
            foreach (var name in mapping.SourceNames)
            {
                parameter = ParameterHelper.Find(material, name);
                if (parameter is not null) break;
            }
        }

        if (parameter is null || parameter.StorageType != StorageType.String) return null;

        var value = parameter.AsString();
        if (string.IsNullOrWhiteSpace(value)) return null;

        value = value.Trim();
        return _ignore.Contains(ParameterHelper.Normalize(value)) ? null : value;
    }

    /// <summary>
    /// Decides whether the pair of values is real classification data, and corrects the
    /// case where the two identity fields were filled in the wrong order.
    /// Mutates <paramref name="values"/>; returns the note for the report.
    /// </summary>
    private string Validate(Dictionary<string, string?> values)
    {
        if (_codePattern is null) return string.Empty;

        var codeKey = _settings.Mapping.First(m => m.IsCode).Target;
        var nameKey = _settings.Mapping.First(m => !m.IsCode).Target;

        if (LooksLikeCode(values.GetValueOrDefault(codeKey)))
            return string.Empty;                       // correct orientation

        if (LooksLikeCode(values.GetValueOrDefault(nameKey)))
        {
            if (!_settings.AutoFixSwapped)
            {
                Blank(values);
                return "SWAPPED";
            }

            (values[codeKey], values[nameKey]) = (values[nameKey], values[codeKey]);
            return "SWAPPED (corrected)";
        }

        // No FK code anywhere, so this is not classification data.
        var hadText = values.Values.Any(v => !string.IsNullOrEmpty(v));
        Blank(values);
        return hadText ? "rejected: no FK code" : string.Empty;
    }

    private bool LooksLikeCode(string? value) =>
        !string.IsNullOrEmpty(value) && (_codePattern is null || _codePattern.IsMatch(value));

    private static void Blank(Dictionary<string, string?> values)
    {
        foreach (var key in values.Keys.ToList()) values[key] = null;
    }
}
