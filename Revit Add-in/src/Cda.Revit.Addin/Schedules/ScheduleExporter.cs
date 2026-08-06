using Autodesk.Revit.DB;
using Cda.Revit.Addin.Excel;

namespace Cda.Revit.Addin.Schedules;

/// <summary>
/// Everything that was a constant in the Python config block. Kept as a settable object
/// rather than consts so a future dialog can expose any of it without touching the reader.
/// </summary>
public sealed class ScheduleExportSettings
{
    /// <summary>Write plainly numeric cells as numbers rather than text.</summary>
    public bool NumericCells { get; init; } = true;

    /// <summary>Prepend an "Index" worksheet listing every schedule in the workbook.</summary>
    public bool IncludeIndexSheet { get; init; }

    /// <summary>
    /// Keep the schedule's column-header row. Off: the title stays in row 1 and data
    /// starts at row 2.
    /// </summary>
    public bool IncludeColumnHeaders { get; init; }

    /// <summary>Force a header-row count instead of detecting it. Null = detect.</summary>
    public int? HeaderRowCount { get; init; }

    /// <summary>Drop rows where every cell is empty - Revit's group separators.</summary>
    public bool DropBlankRows { get; init; } = true;

    /// <summary>Wrap long cell text, which makes those rows taller.</summary>
    public bool WrapText { get; init; }

    /// <summary>Include schedules that contain no data rows.</summary>
    public bool IncludeEmpty { get; init; } = true;

    /// <summary>Only export schedule views that are placed on a sheet.</summary>
    public bool OnlyOnSheets { get; init; }

    // Excel's own ceilings.
    public const int MaxRows = 1_048_576;
    public const int MaxCols = 16_384;
}

public sealed class ScheduleExportResult
{
    public required IReadOnlyList<XlsxSheet> Sheets { get; init; }
    public required IReadOnlyList<string> Report { get; init; }
    public required IReadOnlyList<string> Skipped { get; init; }

    /// <summary>
    /// How many schedules survived filtering, before any were read. Without this an
    /// empty export cannot tell "this model has no schedules" apart from "every schedule
    /// failed to read" - two very different problems with the same symptom.
    /// </summary>
    public required int Collected { get; init; }
}

/// <summary>
/// Port of the schedule-reading half of ExportSchedulesToExcel.py.
///
/// Cell text is taken from Revit's own table data, so values, units and totals land in
/// Excel exactly as the schedule displays them - no re-formatting, no unit conversion,
/// nothing to drift out of step with the drawing.
/// </summary>
public sealed class ScheduleExporter
{
    /// <summary>Section, and whether its rows render bold.</summary>
    private static readonly (SectionType Section, bool Bold)[] Sections =
    [
        (SectionType.Header, true),
        (SectionType.Body, false),
        (SectionType.Summary, false),
        (SectionType.Footer, false),
    ];

    private readonly Document _doc;
    private readonly ScheduleExportSettings _settings;

    public ScheduleExporter(Document doc, ScheduleExportSettings settings)
    {
        _doc = doc;
        _settings = settings;
    }

    public ScheduleExportResult Collect()
    {
        var skipped = new List<string>();
        var report = new List<string>();
        var sheets = new List<XlsxSheet>();
        var indexRows = new List<IReadOnlyList<string>>
        {
            new[] { "Schedule", "Worksheet", "Rows", "Columns" },
        };

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_settings.IncludeIndexSheet) taken.Add("index");

        var collected = CollectSchedules(skipped);

        foreach (var schedule in collected)
        {
            List<List<string>> rows;
            HashSet<int> bold;
            int dataRows;

            var sectionErrors = new List<string>();

            try
            {
                (rows, bold, dataRows) = ReadSchedule(schedule, sectionErrors);
            }
            catch (Exception ex)
            {
                skipped.Add($"{schedule.Name} - could not read table data: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            // A section that failed is worth reporting even when the rest exported fine -
            // it is the difference between a short worksheet and a wrong one.
            foreach (var error in sectionErrors)
                skipped.Add($"{schedule.Name} - {error}");

            if (dataRows == 0 && !_settings.IncludeEmpty)
            {
                skipped.Add($"{schedule.Name} - no data rows");
                continue;
            }

            // Nothing in Revit realistically hits these, but a truncated sheet with a
            // warning beats a workbook Excel refuses to open.
            if (rows.Count > ScheduleExportSettings.MaxRows)
            {
                skipped.Add($"{schedule.Name} - truncated to {ScheduleExportSettings.MaxRows} rows (Excel limit)");
                rows = rows.Take(ScheduleExportSettings.MaxRows).ToList();
                bold = [.. bold.Where(i => i < ScheduleExportSettings.MaxRows)];
            }

            if (rows.Any(r => r.Count > ScheduleExportSettings.MaxCols))
            {
                skipped.Add($"{schedule.Name} - truncated to {ScheduleExportSettings.MaxCols} columns (Excel limit)");
                rows = rows.Select(r => r.Take(ScheduleExportSettings.MaxCols).ToList()).ToList();
            }

            var name = SheetName(schedule.Name, taken);
            var columns = rows.Count > 0 ? rows.Max(r => r.Count) : 0;

            sheets.Add(new XlsxSheet
            {
                Name = name,
                Rows = rows,
                BoldRows = bold,
                Widths = Measure(rows),
                FreezeAt = bold.Count > 0 ? bold.Max() + 1 : 0,
            });

            indexRows.Add(new[] { schedule.Name, name, dataRows.ToString(), columns.ToString() });
            report.Add($"{schedule.Name,-45} -> {name,-31}  {dataRows} rows x {columns} cols");
        }

        if (_settings.IncludeIndexSheet && sheets.Count > 0)
        {
            var index = new List<IReadOnlyList<string>>
            {
                new[] { $"{_doc.Title} - {sheets.Count} schedules" },
                Array.Empty<string>(),   // spacer row under the title
            };
            index.AddRange(indexRows);

            sheets.Insert(0, new XlsxSheet
            {
                Name = "Index",
                Rows = index,
                BoldRows = [0, 2],
                Widths = Measure(index),
                FreezeAt = 3,
            });
        }

        return new ScheduleExportResult
        {
            Sheets = sheets,
            Report = report,
            Skipped = skipped,
            Collected = collected.Count,
        };
    }

    // --------------------------------------------------------- collect the schedules

    private List<ViewSchedule> CollectSchedules(List<string> skipped)
    {
        var revisionCategory = new ElementId(BuiltInCategory.OST_Revisions);
        var result = new List<ViewSchedule>();

        foreach (var view in new FilteredElementCollector(_doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
        {
            string name;
            try { name = view.Name; }
            catch { continue; }

            if (view.IsTemplate) continue;

            // Revit's own internal schedules - revision blocks on title blocks, the
            // keynote legend placeholder. Angle-bracketed, and never user-authored.
            try { if (view.IsTitleblockRevisionSchedule) continue; }
            catch { /* property not applicable */ }

            try { if (view.Definition.CategoryId == revisionCategory) continue; }
            catch { /* definition unreadable */ }

            if (name.StartsWith('<') && name.EndsWith('>')) continue;

            if (_settings.OnlyOnSheets)
            {
                var sheetNumber = view.get_Parameter(BuiltInParameter.VIEWER_SHEET_NUMBER)?.AsString();
                if (string.IsNullOrWhiteSpace(sheetNumber))
                {
                    skipped.Add($"{name} - not placed on a sheet");
                    continue;
                }
            }

            result.Add(view);
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    // ------------------------------------------------------------ read one schedule

    private (List<List<string>> Rows, HashSet<int> Bold, int DataRows) ReadSchedule(
        ViewSchedule view, List<string> sectionErrors)
    {
        var table = view.GetTableData();
        var collected = new List<(SectionType Section, bool Bold, List<string> Cells)>();

        foreach (var (section, isBold) in Sections)
        {
            // The whole section read is guarded, not just GetSectionData. A section that
            // is absent on a given schedule can return an object whose NumberOfRows or
            // FirstRowNumber then throws, and a narrower try meant one unusable Summary
            // or Footer section discarded the entire schedule.
            try
            {
                var data = table.GetSectionData(section);
                if (data is null) continue;
                if (data.NumberOfRows <= 0 || data.NumberOfColumns <= 0) continue;

                var firstRow = data.FirstRowNumber;
                var firstCol = data.FirstColumnNumber;

                // The Python guarded each row and column with IsRowHidden /
                // IsColumnHidden inside a try/except. Those methods do not exist on
                // TableSectionData, so the except swallowed an AttributeError every time
                // and nothing was ever filtered. Behaviour is kept identical here rather
                // than silently changing what the office's existing exports contain. If
                // hidden columns ever need excluding, the real API for it is
                // ScheduleDefinition.GetField(i).IsHidden.
                for (var r = firstRow; r < firstRow + data.NumberOfRows; r++)
                {
                    var cells = new List<string>();
                    for (var c = firstCol; c < firstCol + data.NumberOfColumns; c++)
                    {
                        try { cells.Add(view.GetCellText(section, r, c)); }
                        catch { cells.Add(string.Empty); }
                    }

                    collected.Add((section, isBold, cells));
                }
            }
            catch (Exception ex)
            {
                sectionErrors.Add($"{section} section unreadable ({ex.GetType().Name}: {ex.Message})");
            }
        }

        var body = collected.Where(x => x.Section == SectionType.Body).Select(x => x.Cells).ToList();
        var headerRows = HeaderRowCount(view, body);

        var rows = new List<List<string>>();
        var bold = new HashSet<int>();
        var seenBody = 0;
        var dataRows = 0;

        foreach (var (section, isBoldRow, cells) in collected)
        {
            var isBold = isBoldRow;

            if (section == SectionType.Body)
            {
                seenBody++;
                if (seenBody <= headerRows)
                {
                    if (!_settings.IncludeColumnHeaders) continue;
                    isBold = true;
                }
                else if (!IsBlank(cells))
                {
                    dataRows++;
                }
            }

            if (_settings.DropBlankRows && IsBlank(cells)) continue;

            if (isBold) bold.Add(rows.Count);
            rows.Add(cells);
        }

        return (rows, bold, dataRows);
    }

    /// <summary>
    /// How many leading Body rows are column headings rather than data.
    ///
    /// Matched against the schedule's own field headings rather than assumed to be one
    /// row, so a schedule with stacked or grouped headings loses all of them, and one
    /// with headers switched off loses none.
    /// </summary>
    private int HeaderRowCount(ViewSchedule view, List<List<string>> body)
    {
        if (_settings.HeaderRowCount is { } forced) return forced;

        var headings = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var definition = view.Definition;
            for (var i = 0; i < definition.GetFieldCount(); i++)
            {
                var heading = definition.GetField(i).ColumnHeading;
                if (!string.IsNullOrWhiteSpace(heading)) headings.Add(heading.Trim());
            }
        }
        catch
        {
            headings.Clear();
        }

        var count = 0;
        if (headings.Count > 0)
        {
            foreach (var row in body)
            {
                var cells = row.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
                if (cells.Count > 0 && cells.All(headings.Contains)) count++;
                else break;
            }
        }

        if (count == 0 && headings.Count == 0)
        {
            // Field headings were unreadable, so fall back to Revit's own flag. Only in
            // that case: if the headings read fine and nothing matched, the first body row
            // is data, and keeping a stray header beats deleting a row.
            try { count = view.Definition.ShowHeaders ? 1 : 0; }
            catch { count = 0; }
        }

        return count;
    }

    // ----------------------------------------------------------------------- helpers

    private static bool IsBlank(IEnumerable<string> row) =>
        row.All(string.IsNullOrWhiteSpace);

    private static List<double> Measure(IEnumerable<IReadOnlyList<string>> rows)
    {
        var widths = new List<double>();

        foreach (var row in rows)
        {
            for (var c = 0; c < row.Count; c++)
            {
                while (widths.Count <= c) widths.Add(8.0);
                widths[c] = Math.Max(widths[c], Math.Min((row[c] ?? string.Empty).Length + 3.0, 60.0));
            }
        }

        return widths;
    }

    /// <summary>
    /// Excel worksheet names: 31 characters or fewer, none of []:*?/\, no leading or
    /// trailing apostrophe, "History" is reserved, and every name must be unique.
    /// </summary>
    private static string SheetName(string raw, HashSet<string> taken)
    {
        var name = new string((raw ?? "Schedule")
            .Select(c => "[]:*?/\\".Contains(c) ? '-' : c)
            .ToArray())
            .Trim()
            .Trim('\'');

        name = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (name.Length == 0) name = "Schedule";
        if (name.Equals("history", StringComparison.OrdinalIgnoreCase)) name = "History_";
        if (name.Length > 31) name = name[..31];

        if (taken.Add(name)) return name;

        for (var i = 2; i < 1000; i++)
        {
            var suffix = $" ({i})";
            var candidate = name[..Math.Min(name.Length, 31 - suffix.Length)] + suffix;
            if (taken.Add(candidate)) return candidate;
        }

        throw new InvalidOperationException($"Could not make a unique worksheet name for '{raw}'.");
    }
}
