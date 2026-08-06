using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Standards;

/// <summary>How a checklist item is settled.</summary>
public enum CheckMode
{
    /// <summary>A human judgement. Tracked and listed; never decided by code.</summary>
    Manual,

    /// <summary>Revit can prove it. A fail is a defect.</summary>
    Auto,

    /// <summary>Checked where possible, but a fail is a question rather than a defect.</summary>
    Advisory,
}

public sealed class ChecklistItem
{
    public string Text { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CheckMode Mode { get; set; } = CheckMode.Manual;

    /// <summary>Name of the rule in <see cref="SmbAudit"/>. Empty for a manual item.</summary>
    public string Rule { get; set; } = string.Empty;

    /// <summary>Rule arguments — parameter names, patterns, extensions.</summary>
    public Dictionary<string, JsonElement> Args { get; set; } = [];

    /// <summary>Shown beside the result. Why the item exists, not what it does.</summary>
    public string Note { get; set; } = string.Empty;

    public string? Text2 => null;
}

public sealed class ChecklistElement
{
    public string Id { get; set; } = string.Empty;
    public string Roman { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public List<string> ViewTemplates { get; set; } = [];

    /// <summary>BuiltInCategory names, resolved by name so an absent one is skipped, not fatal.</summary>
    public List<string> Categories { get; set; } = [];

    public List<ChecklistItem> Items { get; set; } = [];
}

public sealed class QaStage
{
    public string Id { get; set; } = string.Empty;
    public string ViewTemplate { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public List<string> Schedules { get; set; } = [];
    public List<ChecklistItem> Items { get; set; } = [];
}

public sealed class ExportRule
{
    public string Id { get; set; } = string.Empty;
    public string ViewTemplate { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CheckMode Mode { get; set; } = CheckMode.Auto;

    public string Rule { get; set; } = string.Empty;
    public Dictionary<string, JsonElement> Args { get; set; } = [];
}

/// <summary>
/// The SMB modelling checklist, loaded from JSON.
///
/// WHY THE CHECKLIST IS DATA AND NOT CODE
///   It is a standard, and standards move. A BIM manager renaming SMB-06, adding a casework
///   code or splitting an element row should be editing one file, not raising a change to a
///   compiled add-in that then has to be rebuilt, deployed and verified on every machine.
///   Everything the auditor knows comes from this file; nothing about the checklist is
///   compiled in.
///
///   The RULES are code, because they touch the Revit API. The mapping from a checklist line
///   to a rule is data. That split is what keeps the standard editable without making the
///   verification fake.
///
/// Loaded from, in order: a file beside the deployed DLL (so the office can push one), then
/// the embedded copy. Same shape as the time-tracking defaults, for the same reason.
/// </summary>
public sealed class SmbChecklist
{
    public string Version { get; set; } = string.Empty;
    public string Title { get; set; } = "SMB modelling checklist";

    public List<ChecklistItem> ProjectSetup { get; set; } = [];
    public List<ChecklistElement> Elements { get; set; } = [];
    public List<QaStage> SelfQa { get; set; } = [];
    public List<ExportRule> Export { get; set; } = [];

    private const string FileName = "smb-checklist.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Where an editable copy is looked for, in order.
    ///
    /// BOTH LOCATIONS ARE CHECKED because the build drops the file into a Standards
    /// subfolder while the natural place to edit it is beside the DLL. Looking in only one
    /// produced a silent fallback to the embedded copy: edits to the deployed file appeared
    /// to do nothing, which is the worst possible failure for a file whose whole purpose is
    /// being editable without a rebuild.
    /// </summary>
    public static IReadOnlyList<string> OverridePaths
    {
        get
        {
            try
            {
                var folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(folder)) return [];

                return [Path.Combine(folder, FileName), Path.Combine(folder, "Standards", FileName)];
            }
            catch
            {
                return [];
            }
        }
    }

    /// <summary>The preferred location, for telling the user where to put one.</summary>
    public static string OverridePath => OverridePaths.FirstOrDefault() ?? string.Empty;

    public static SmbChecklist Load(out string source)
    {
        foreach (var path in OverridePaths)
        {
            if (!File.Exists(path)) continue;

            try
            {
                var loaded = JsonSerializer.Deserialize<SmbChecklist>(File.ReadAllText(path), Options);

                if (loaded is not null)
                {
                    source = path;
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                // A broken override must not take the checklist down with it - fall through
                // to the embedded copy and say so, loudly, in the report.
                Log.Warn($"SMB checklist at '{path}' is unreadable, ignoring it: {ex.Message}");
            }
        }

        source = "built in";
        return Embedded();
    }

    private static SmbChecklist Embedded()
    {
        var assembly = typeof(SmbChecklist).Assembly;
        var name = $"{typeof(SmbChecklist).Namespace}.{FileName}";

        using var stream = assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException(
                               $"The embedded checklist '{name}' is missing from the assembly.");

        using var reader = new StreamReader(stream);

        return JsonSerializer.Deserialize<SmbChecklist>(reader.ReadToEnd(), Options)
               ?? throw new InvalidOperationException("The embedded checklist could not be parsed.");
    }

    /// <summary>Every view template the checklist expects to exist, in order of appearance.</summary>
    public IReadOnlyList<string> AllViewTemplates()
    {
        var names = new List<string>();

        foreach (var element in Elements)
            foreach (var template in element.ViewTemplates)
                if (!names.Contains(template)) names.Add(template);

        foreach (var stage in SelfQa)
            if (stage.ViewTemplate.Length > 0 && !names.Contains(stage.ViewTemplate))
                names.Add(stage.ViewTemplate);

        foreach (var rule in Export)
            if (rule.ViewTemplate.Length > 0 && !names.Contains(rule.ViewTemplate))
                names.Add(rule.ViewTemplate);

        return names;
    }

    public IReadOnlyList<string> AllSchedules() =>
        [.. SelfQa.SelectMany(s => s.Schedules).Distinct(StringComparer.CurrentCultureIgnoreCase)];

    public int TotalItems =>
        ProjectSetup.Count +
        Elements.Sum(e => e.Items.Count) +
        SelfQa.Sum(s => s.Items.Count) +
        Export.Count;
}
