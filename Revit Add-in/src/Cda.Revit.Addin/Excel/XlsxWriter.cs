using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Cda.Revit.Addin.Excel;

/// <summary>One worksheet: a grid of display strings plus its presentation hints.</summary>
public sealed class XlsxSheet
{
    public required string Name { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }
    public HashSet<int> BoldRows { get; init; } = [];
    public IReadOnlyList<double> Widths { get; init; } = [];

    /// <summary>Rows to freeze at the top; 0 for none.</summary>
    public int FreezeAt { get; init; }
}

/// <summary>
/// Enough of SpreadsheetML to produce a workbook Excel, LibreOffice and pandas all open
/// without complaint. Port of the OOXML writer embedded in ExportSchedulesToExcel.py.
///
/// Deliberately has no dependency on Excel, on Interop, or on a NuGet package: the office
/// runs this on machines that may not have Excel installed, and a spreadsheet library
/// would be the add-in's only third-party assembly.
///
/// Strings are written inline, which skips the shared-string table entirely - a larger
/// file, and far less that can go wrong.
/// </summary>
public static partial class XlsxWriter
{
    private const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string NsPkg = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string NsCt = "http://schemas.openxmlformats.org/package/2006/content-types";

    /// <summary>Excel refuses to open a file containing these control characters.</summary>
    [GeneratedRegex("[\x00-\x08\x0b\x0c\x0e-\x1f]")]
    private static partial Regex IllegalChars();

    /// <summary>
    /// Deliberately strict: ASCII digits, one optional '.', optional leading '-'.
    /// Anything with a unit suffix, a thousands separator, or a comma decimal
    /// (da-DK "1.200,5") stays text, because guessing at those corrupts values silently.
    /// </summary>
    [GeneratedRegex(@"^-?(?:\d+|\d*\.\d+)$")]
    private static partial Regex PlainNumber();

    public static void Write(
        string path,
        IReadOnlyList<XlsxSheet> sheets,
        bool numericCells = true,
        bool wrapText = false)
    {
        var parts = new List<(string Name, string Xml)>
        {
            ("[Content_Types].xml", ContentTypes(sheets.Count)),
            ("_rels/.rels", RootRels()),
            ("xl/workbook.xml", Workbook(sheets)),
            ("xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Count)),
            ("xl/styles.xml", Styles(wrapText)),
        };

        for (var i = 0; i < sheets.Count; i++)
            parts.Add(($"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i], numericCells)));

        WriteZip(path, parts);
    }

    private static void WriteZip(string path, IReadOnlyList<(string Name, string Xml)> parts)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        // UTF8Encoding(false): a BOM inside an OOXML part makes Excel reject the workbook.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        foreach (var (name, xml) in parts)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), encoding);
            writer.Write(xml);
        }
    }

    // ------------------------------------------------------------------ package parts

    private static string ContentTypes(int sheetCount)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append($"<Types xmlns=\"{NsCt}\">");
        sb.Append("""<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>""");
        sb.Append("""<Default Extension="xml" ContentType="application/xml"/>""");
        sb.Append("""<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>""");
        sb.Append("""<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");

        for (var i = 1; i <= sheetCount; i++)
        {
            sb.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=" +
                      "\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }

        sb.Append("</Types>");
        return sb.ToString();
    }

    private static string RootRels() =>
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
        $"<Relationships xmlns=\"{NsPkg}\">" +
        $"<Relationship Id=\"rId1\" Type=\"{NsRel}/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private static string Workbook(IReadOnlyList<XlsxSheet> sheets)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append($"<workbook xmlns=\"{NsMain}\" xmlns:r=\"{NsRel}\"><sheets>");

        for (var i = 0; i < sheets.Count; i++)
            sb.Append($"<sheet name=\"{Escape(sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");

        sb.Append("</sheets></workbook>");
        return sb.ToString();
    }

    private static string WorkbookRels(int sheetCount)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append($"<Relationships xmlns=\"{NsPkg}\">");

        for (var i = 1; i <= sheetCount; i++)
            sb.Append($"<Relationship Id=\"rId{i}\" Type=\"{NsRel}/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");

        sb.Append($"<Relationship Id=\"rId{sheetCount + 1}\" Type=\"{NsRel}/styles\" Target=\"styles.xml\"/>");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    private static string Styles(bool wrapText)
    {
        var align = $"<alignment vertical=\"top\"{(wrapText ? " wrapText=\"1\"" : string.Empty)}/>";

        return """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
               $"<styleSheet xmlns=\"{NsMain}\">" +
               "<fonts count=\"2\">" +
               "<font><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
               "<font><b/><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
               "</fonts>" +
               "<fills count=\"2\">" +
               "<fill><patternFill patternType=\"none\"/></fill>" +
               "<fill><patternFill patternType=\"gray125\"/></fill>" +
               "</fills>" +
               "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
               "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
               "<cellXfs count=\"2\">" +
               $"<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\">{align}</xf>" +
               $"<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyAlignment=\"1\">{align}</xf>" +
               "</cellXfs>" +
               "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
               "</styleSheet>";
    }

    private static string SheetXml(XlsxSheet sheet, bool numericCells)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append($"<worksheet xmlns=\"{NsMain}\" xmlns:r=\"{NsRel}\">");

        if (sheet.FreezeAt > 0)
        {
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\">")
              .Append($"<pane ySplit=\"{sheet.FreezeAt}\" topLeftCell=\"A{sheet.FreezeAt + 1}\" ")
              .Append("activePane=\"bottomLeft\" state=\"frozen\"/>")
              .Append("</sheetView></sheetViews>");
        }

        if (sheet.Widths.Count > 0)
        {
            sb.Append("<cols>");
            for (var i = 0; i < sheet.Widths.Count; i++)
            {
                sb.Append(string.Format(CultureInfo.InvariantCulture,
                    "<col min=\"{0}\" max=\"{0}\" width=\"{1:0.00}\" customWidth=\"1\"/>",
                    i + 1, sheet.Widths[i]));
            }
            sb.Append("</cols>");
        }

        sb.Append("<sheetData>");

        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var style = sheet.BoldRows.Contains(r) ? " s=\"1\"" : string.Empty;
            var row = sheet.Rows[r];
            var cells = new StringBuilder();

            for (var c = 0; c < row.Count; c++)
            {
                var value = row[c];
                if (string.IsNullOrEmpty(value)) continue;

                var reference = $"{ColumnLetter(c)}{r + 1}";

                if (numericCells && IsLosslessNumber(value.Trim()))
                    cells.Append($"<c r=\"{reference}\"{style}><v>{value.Trim()}</v></c>");
                else
                    cells.Append($"<c r=\"{reference}\"{style} t=\"inlineStr\"><is>" +
                                 $"<t xml:space=\"preserve\">{Escape(value)}</t></is></c>");
            }

            sb.Append(cells.Length > 0
                ? $"<row r=\"{r + 1}\">{cells}</row>"
                : $"<row r=\"{r + 1}\"/>");
        }

        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    // ----------------------------------------------------------------------- helpers

    /// <summary>0 -> A, 25 -> Z, 26 -> AA.</summary>
    /// <summary>
    /// Is this text safe to write as a NUMBER rather than as text - i.e. would Excel show
    /// exactly the same characters back?
    ///
    /// THE BUG THIS REPLACES. The test was the <see cref="PlainNumber"/> pattern alone, which
    /// asks only "does this look numeric". It has no idea which schedule column a cell came
    /// from, and room numbers are what it damaged: "001" was written as &lt;v&gt;001&lt;/v&gt;
    /// and rendered by Excel as 1, "1.10" as 1.1, "2.00" as 2. The identifier that ties every
    /// exported row back to a room in the model was silently altered by the export, and this
    /// data feeds a digital twin - a room number that does not round-trip is worse than a
    /// missing column, because nothing about it looks wrong.
    ///
    /// The rule is a round-trip rather than a longer regex: parse it, format it back, and
    /// require the characters to match. That keeps genuine quantities ("12.5", "-3", "0")
    /// numeric so they still total and sort as numbers, and leaves anything whose written form
    /// carries meaning - leading zeros, trailing decimal zeros - as text, exactly as it appears
    /// in the schedule.
    /// </summary>
    private static bool IsLosslessNumber(string trimmed)
    {
        if (!PlainNumber().IsMatch(trimmed)) return false;

        // "R" round-trips the value; comparing against the original catches every form whose
        // characters carry information the double does not.
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
               && string.Equals(
                   parsed.ToString("R", CultureInfo.InvariantCulture),
                   trimmed,
                   StringComparison.Ordinal);
    }

    public static string ColumnLetter(int index)
    {
        var name = string.Empty;
        var i = index + 1;
        while (i > 0)
        {
            var rem = (i - 1) % 26;
            i = (i - 1) / 26;
            name = (char)('A' + rem) + name;
        }
        return name;
    }

    private static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        return IllegalChars().Replace(text, string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }
}
