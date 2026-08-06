using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Standards;

public enum CheckStatus
{
    Pass,
    Fail,

    /// <summary>The rule could not run — nothing to check, or an input was not supplied.</summary>
    Skipped,

    /// <summary>A human judgement. Present in the report, decided by a person.</summary>
    Manual,
}

public sealed record CheckResult(
    string Section, string Item, CheckMode Mode, CheckStatus Status, string Detail);

public sealed class SmbAuditResult
{
    public required IReadOnlyList<CheckResult> Results { get; init; }
    public required string ChecklistSource { get; init; }
    public required string Version { get; init; }

    public int Passed => Results.Count(r => r.Status == CheckStatus.Pass);
    public int Failed => Results.Count(r => r.Status == CheckStatus.Fail && r.Mode == CheckMode.Auto);
    public int Advisory => Results.Count(r => r.Status == CheckStatus.Fail && r.Mode == CheckMode.Advisory);
    public int Skipped => Results.Count(r => r.Status == CheckStatus.Skipped);
    public int ManualOutstanding => Results.Count(r => r.Status == CheckStatus.Manual);
    public int Automated => Results.Count(r => r.Mode != CheckMode.Manual);
}

/// <summary>
/// Runs the machine-checkable part of the SMB checklist against a model.
///
/// WHAT THIS DELIBERATELY DOES NOT DO
///   It does not tick "Wall Direction", "Dimension" or "Wall Split (SL)". Those are
///   judgements about whether the modelling is RIGHT, and Revit cannot tell a wall pointing
///   the wrong way from one pointing the right way — both are valid walls. Auto-passing them
///   would convert a checklist into a rubber stamp and quietly remove the only step that was
///   catching those mistakes.
///
///   Roughly two thirds of the checklist is like that. Those items appear in the report as
///   OUTSTANDING rather than passed, so the number a reviewer sees is honest: this is a
///   tool that removes the mechanical checks from a person's plate, not one that pretends to
///   have done the review.
///
/// Read-only throughout. Nothing here writes to the model.
/// </summary>
public sealed class SmbAudit
{
    private readonly Document _doc;
    private readonly SmbChecklist _checklist;
    private readonly string? _exportFolder;
    private readonly List<CheckResult> _results = [];

    /// <summary>View template names in the model, normalised once.</summary>
    private readonly HashSet<string> _templates;

    private readonly HashSet<string> _schedules;

    public SmbAudit(Document doc, SmbChecklist checklist, string? exportFolder = null)
    {
        _doc = doc;
        _checklist = checklist;
        _exportFolder = exportFolder;

        _templates = [.. new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => v.IsTemplate)
            .Select(v => ParameterHelper.Normalize(v.Name))];

        _schedules = [.. new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .Where(v => !v.IsTemplate)
            .Select(v => ParameterHelper.Normalize(v.Name))];
    }

    public SmbAuditResult Run(string checklistSource)
    {
        foreach (var item in _checklist.ProjectSetup)
            Evaluate("Project setup", item, categories: []);

        foreach (var element in _checklist.Elements)
        {
            var section = $"{element.Roman}. {element.Name}";

            // The view template is itself a checklist line: a template that does not exist
            // means every visual check underneath it was done in the wrong view.
            foreach (var template in element.ViewTemplates) ReportTemplate(section, template);

            foreach (var item in element.Items) Evaluate(section, item, element.Categories);
        }

        foreach (var stage in _checklist.SelfQa)
        {
            var section = $"QA · {stage.ViewTemplate}";

            ReportTemplate(section, stage.ViewTemplate);

            foreach (var schedule in stage.Schedules)
            {
                Add(section, $"Schedule '{schedule}'", CheckMode.Auto,
                    _schedules.Contains(ParameterHelper.Normalize(schedule))
                        ? CheckStatus.Pass
                        : CheckStatus.Fail,
                    _schedules.Contains(ParameterHelper.Normalize(schedule))
                        ? "present"
                        : "not in this model - the QA view has nothing to check against");
            }

            foreach (var item in stage.Items) Evaluate(section, item, categories: []);
        }

        foreach (var rule in _checklist.Export)
        {
            var item = new ChecklistItem
            {
                Text = rule.Text, Mode = rule.Mode, Rule = rule.Rule, Args = rule.Args,
            };

            if (rule.ViewTemplate.Length > 0) ReportTemplate("Export", rule.ViewTemplate);

            Evaluate("Export", item, categories: []);
        }

        return new SmbAuditResult
        {
            Results = _results,
            ChecklistSource = checklistSource,
            Version = _checklist.Version,
        };
    }

    private void ReportTemplate(string section, string name)
    {
        var present = _templates.Contains(ParameterHelper.Normalize(name));

        Add(section, $"View template '{name}'", CheckMode.Auto,
            present ? CheckStatus.Pass : CheckStatus.Fail,
            present ? "present" : "missing from this model");
    }

    // ------------------------------------------------------------------ dispatch

    private void Evaluate(string section, ChecklistItem item, IReadOnlyList<string> categories)
    {
        if (item.Mode == CheckMode.Manual || item.Rule.Length == 0)
        {
            Add(section, item.Text, CheckMode.Manual, CheckStatus.Manual,
                item.Note.Length > 0 ? item.Note : "needs a person");
            return;
        }

        try
        {
            var (status, detail) = item.Rule switch
            {
                "WorkingCopyNotInCloudSync" => CloudSync(),
                "SavedLocally" => SavedLocally(),
                "ProjectInformationComplete" => ProjectInformation(item),
                "ViewTemplatesExist" => TemplatesExist(),
                "HasImportedReferences" => ImportedReferences(),
                "LevelsDefined" => Levels(),
                "NoUnconnectedHeights" => UnconnectedHeights(),
                "RoomsIdentified" => RoomsIdentified(),
                "PartsExist" => PartsExist(),
                "ParameterPopulated" => ParameterPopulated(item, categories),
                "ExportNaming" => ExportNaming(item),
                "FbxNameMatchesType" => FbxNames(item),
                "ExcelExportValidates" => ExcelExports(item),
                _ => (CheckStatus.Skipped, $"no rule named '{item.Rule}' is implemented"),
            };

            Add(section, item.Text, item.Mode, status,
                item.Note.Length > 0 ? $"{detail} — {item.Note}" : detail);
        }
        catch (Exception ex)
        {
            // One rule failing is a gap in the report, not a failed audit.
            Add(section, item.Text, item.Mode, CheckStatus.Skipped, $"check failed: {ex.Message}");
        }
    }

    private void Add(string section, string item, CheckMode mode, CheckStatus status, string detail) =>
        _results.Add(new CheckResult(section, item, mode, status, detail));

    // ------------------------------------------------------------------ rules

    /// <summary>
    /// A live .rvt inside a cloud-sync folder is a genuine corruption source: the sync client
    /// rewrites the file underneath Revit. This looks for the sync roots by name, which is
    /// crude but catches the case that actually happens.
    /// </summary>
    private (CheckStatus, string) CloudSync()
    {
        var path = Safe(() => _doc.PathName) ?? string.Empty;
        if (path.Length == 0) return (CheckStatus.Fail, "the model has never been saved");

        string[] markers = ["OneDrive", "Dropbox", "Google Drive", "iCloud", "Box Sync"];
        var hit = markers.FirstOrDefault(m => path.Contains(m, StringComparison.OrdinalIgnoreCase));

        return hit is null
            ? (CheckStatus.Pass, "not inside a cloud-sync folder")
            : (CheckStatus.Fail,
                $"the model is open from a {hit} folder ({path}). Work from a local copy and " +
                "sync the issued file instead.");
    }

    private (CheckStatus, string) SavedLocally()
    {
        var path = Safe(() => _doc.PathName) ?? string.Empty;

        if (path.Length == 0) return (CheckStatus.Fail, "never saved");

        // A workshared LOCAL file is the correct arrangement even though the central is on a
        // server, so worksharing is reported rather than judged.
        var workshared = Safe(() => _doc.IsWorkshared);
        var unc = path.StartsWith(@"\\", StringComparison.Ordinal);

        if (unc && !workshared)
            return (CheckStatus.Fail, $"opened straight from a network path ({path})");

        return (CheckStatus.Pass, workshared ? $"workshared local file: {path}" : path);
    }

    private (CheckStatus, string) ProjectInformation(ChecklistItem item)
    {
        var names = Strings(item, "parameters");
        if (names.Count == 0) return (CheckStatus.Skipped, "no parameters configured");

        var info = _doc.ProjectInformation;
        if (info is null) return (CheckStatus.Skipped, "this document has no Project Information");

        var empty = names
            .Where(n => string.IsNullOrWhiteSpace(ParameterHelper.Find(info, n)?.AsString()))
            .ToList();

        return empty.Count == 0
            ? (CheckStatus.Pass, $"{names.Count} field(s) filled in")
            : (CheckStatus.Fail, $"empty: {string.Join(", ", empty)}");
    }

    private (CheckStatus, string) TemplatesExist()
    {
        var wanted = _checklist.AllViewTemplates();
        var missing = wanted.Where(t => !_templates.Contains(ParameterHelper.Normalize(t))).ToList();

        return missing.Count == 0
            ? (CheckStatus.Pass, $"all {wanted.Count} SMB view templates are present")
            : (CheckStatus.Fail,
                $"{missing.Count} of {wanted.Count} missing: {string.Join(", ", missing.Take(8))}" +
                (missing.Count > 8 ? " ..." : string.Empty));
    }

    private (CheckStatus, string) ImportedReferences()
    {
        var count = new FilteredElementCollector(_doc)
            .OfClass(typeof(ImportInstance))
            .GetElementCount();

        return count > 0
            ? (CheckStatus.Pass, $"{count} imported/linked reference(s)")
            : (CheckStatus.Fail, "no imported CAD or image references found");
    }

    private (CheckStatus, string) Levels()
    {
        var levels = new FilteredElementCollector(_doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .ToList();

        if (levels.Count == 0) return (CheckStatus.Fail, "no levels in this model");

        var names = string.Join(", ", levels.Take(6).Select(l =>
            $"{l.Name} @ {UnitUtils.ConvertFromInternalUnits(l.Elevation, UnitTypeId.Millimeters)
                .ToString("0", CultureInfo.InvariantCulture)} mm"));

        return (CheckStatus.Pass, $"{levels.Count} level(s): {names}{(levels.Count > 6 ? " ..." : string.Empty)}");
    }

    /// <summary>
    /// Walls whose Top Constraint is "Unconnected". The checklist asks for level or ceiling
    /// height, and an unconnected wall is the one state that silently stops following either.
    /// </summary>
    private (CheckStatus, string) UnconnectedHeights()
    {
        var walls = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Walls)
            .WhereElementIsNotElementType()
            .ToList();

        if (walls.Count == 0) return (CheckStatus.Skipped, "no walls in this model");

        var loose = walls
            .Where(w => w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE) is { } p &&
                        p.AsElementId() == ElementId.InvalidElementId)
            .ToList();

        return loose.Count == 0
            ? (CheckStatus.Pass, $"all {walls.Count} wall(s) are constrained to a level")
            : (CheckStatus.Fail,
                $"{loose.Count} of {walls.Count} wall(s) have an Unconnected top constraint. Ids: " +
                string.Join(", ", loose.Take(15).Select(w => w.Id.Value)));
    }

    private (CheckStatus, string) RoomsIdentified()
    {
        var rooms = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .ToList();

        if (rooms.Count == 0) return (CheckStatus.Fail, "no rooms placed");

        var placed = rooms.Where(r => Safe(() => r.get_Parameter(BuiltInParameter.ROOM_AREA)?.AsDouble() ?? 0) > 0)
            .ToList();

        var incomplete = placed.Where(r =>
            string.IsNullOrWhiteSpace(r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()) ||
            string.IsNullOrWhiteSpace(r.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString()) ||
            string.IsNullOrWhiteSpace(r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString()))
            .ToList();

        var unplaced = rooms.Count - placed.Count;
        var suffix = unplaced > 0 ? $" ({unplaced} unplaced/unenclosed room(s) ignored)" : string.Empty;

        return incomplete.Count == 0
            ? (CheckStatus.Pass, $"all {placed.Count} placed room(s) have Name, Number and Department{suffix}")
            : (CheckStatus.Fail,
                $"{incomplete.Count} of {placed.Count} placed room(s) are missing Name, Number or " +
                $"Department{suffix}. Numbers: " +
                string.Join(", ", incomplete.Take(15)
                    .Select(r => r.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? r.Id.Value.ToString())));
    }

    private (CheckStatus, string) PartsExist()
    {
        var parts = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Parts)
            .WhereElementIsNotElementType()
            .GetElementCount();

        return parts > 0
            ? (CheckStatus.Pass, $"{parts} part(s) in the model")
            : (CheckStatus.Fail, "no Parts have been created");
    }

    /// <summary>
    /// The workhorse: is a named parameter filled in on the instances of these categories?
    ///
    /// Reports the SHARE rather than a bare pass/fail, because "412 of 480 doors" is a
    /// different situation from "0 of 480" and the fix is different too.
    /// </summary>
    private (CheckStatus, string) ParameterPopulated(ChecklistItem item, IReadOnlyList<string> sectionCategories)
    {
        var name = Text(item, "parameter");
        if (name.Length == 0) return (CheckStatus.Skipped, "no parameter name configured");

        var categoryNames = Strings(item, "categories");
        if (categoryNames.Count == 0) categoryNames = [.. sectionCategories];
        if (categoryNames.Count == 0) return (CheckStatus.Skipped, "no categories configured");

        var elements = new List<Element>();

        foreach (var categoryName in categoryNames)
        {
            if (!Enum.TryParse<BuiltInCategory>(categoryName, out var builtIn)) continue;

            try
            {
                elements.AddRange(new FilteredElementCollector(_doc)
                    .OfCategory(builtIn)
                    .WhereElementIsNotElementType());
            }
            catch
            {
                // A category this Revit does not know is not a checklist failure.
            }
        }

        if (elements.Count == 0)
            return (CheckStatus.Skipped, $"no {string.Join("/", categoryNames)} elements in this model");

        var bound = elements.Count(e => ParameterHelper.Find(e, name) is not null);

        if (bound == 0)
            return (CheckStatus.Fail, $"'{name}' is not bound to {string.Join("/", categoryNames)}");

        var filled = elements.Count(e =>
        {
            var p = ParameterHelper.Find(e, name);
            if (p is null) return false;

            return p.StorageType switch
            {
                StorageType.String => !string.IsNullOrWhiteSpace(p.AsString()),
                StorageType.Integer => p.HasValue,
                StorageType.Double => p.HasValue && Math.Abs(p.AsDouble()) > 1e-9,
                StorageType.ElementId => p.AsElementId() != ElementId.InvalidElementId,
                _ => false,
            };
        });

        var share = $"{filled} of {elements.Count}";

        return filled == elements.Count
            ? (CheckStatus.Pass, $"'{name}' filled on all {elements.Count}")
            : (CheckStatus.Fail, $"'{name}' filled on {share} — {elements.Count - filled} blank");
    }

    // ------------------------------------------------------------------ export rules

    private (CheckStatus, string) ExportNaming(ChecklistItem item)
    {
        var files = ExportFiles(item, out var problem);
        if (files is null) return (CheckStatus.Skipped, problem);
        if (files.Count == 0) return (CheckStatus.Skipped, "no matching files in the export folder");

        var pattern = Text(item, "pattern");
        var describe = Text(item, "describe");

        if (pattern.Length == 0) return (CheckStatus.Skipped, "no naming pattern configured");

        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        var bad = files.Where(f => !regex.IsMatch(Path.GetFileNameWithoutExtension(f))).ToList();

        return bad.Count == 0
            ? (CheckStatus.Pass, $"all {files.Count} file(s) match the convention")
            : (CheckStatus.Fail,
                $"{bad.Count} of {files.Count} misnamed ({describe}): " +
                string.Join(", ", bad.Take(8).Select(Path.GetFileName)));
    }

    /// <summary>
    /// FBX filenames must equal a type name in the model. Checked against the actual types
    /// rather than a pattern, because "same as Type Name" is only meaningful against the
    /// model that produced the export.
    /// </summary>
    private (CheckStatus, string) FbxNames(ChecklistItem item)
    {
        var files = ExportFiles(item, out var problem);
        if (files is null) return (CheckStatus.Skipped, problem);
        if (files.Count == 0) return (CheckStatus.Skipped, "no .fbx files in the export folder");

        var typeNames = new HashSet<string>(
            new FilteredElementCollector(_doc)
                .WhereElementIsElementType()
                .Select(t => ParameterHelper.Normalize(Safe(() => t.Name) ?? string.Empty))
                .Where(n => n.Length > 0));

        var orphans = files
            .Where(f => !typeNames.Contains(ParameterHelper.Normalize(Path.GetFileNameWithoutExtension(f))))
            .ToList();

        return orphans.Count == 0
            ? (CheckStatus.Pass, $"all {files.Count} .fbx name(s) match a type in this model")
            : (CheckStatus.Fail,
                $"{orphans.Count} of {files.Count} do not match any type name: " +
                string.Join(", ", orphans.Take(8).Select(Path.GetFileName)));
    }

    /// <summary>
    /// "Validate Excel after export" — the workbook must exist, be non-trivial, and be a real
    /// OOXML package rather than a zero-byte file left by a failed export.
    /// </summary>
    private (CheckStatus, string) ExcelExports(ChecklistItem item)
    {
        var files = ExportFiles(item, out var problem);
        if (files is null) return (CheckStatus.Skipped, problem);
        if (files.Count == 0) return (CheckStatus.Skipped, "no .xlsx files in the export folder");

        var broken = new List<string>();

        foreach (var file in files)
        {
            try
            {
                if (new FileInfo(file).Length < 1024) { broken.Add(Path.GetFileName(file)); continue; }

                // "PK" - the ZIP magic every .xlsx starts with. A renamed CSV fails here.
                using var stream = File.OpenRead(file);
                if (stream.ReadByte() != 'P' || stream.ReadByte() != 'K') broken.Add(Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                broken.Add($"{Path.GetFileName(file)} ({ex.Message})");
            }
        }

        return broken.Count == 0
            ? (CheckStatus.Pass, $"{files.Count} workbook(s) open as valid .xlsx")
            : (CheckStatus.Fail, $"{broken.Count} unreadable or empty: {string.Join(", ", broken.Take(8))}");
    }

    private List<string>? ExportFiles(ChecklistItem item, out string problem)
    {
        if (string.IsNullOrWhiteSpace(_exportFolder))
        {
            problem = "no export folder was chosen, so this was not checked";
            return null;
        }

        if (!Directory.Exists(_exportFolder))
        {
            problem = $"'{_exportFolder}' does not exist";
            return null;
        }

        var extensions = Strings(item, "extensions");
        if (extensions.Count == 0) { problem = "no file extensions configured"; return null; }

        problem = string.Empty;

        return [.. Directory.EnumerateFiles(_exportFolder, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))];
    }

    // ------------------------------------------------------------------ arg helpers

    private static string Text(ChecklistItem item, string key) =>
        item.Args.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static List<string> Strings(ChecklistItem item, string key)
    {
        if (!item.Args.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        return [.. value.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => s.Length > 0)];
    }

    private static T? Safe<T>(Func<T?> read)
    {
        try { return read(); }
        catch { return default; }
    }
}
